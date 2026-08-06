using System.Text.Json;
using System.Text.RegularExpressions;
using CatMeme.Api.Data;
using CatMeme.Api.Domain;
using CatMeme.Api.Jobs;

namespace CatMeme.Api.Services;

/// <summary>
/// Generates the pipeline's text through the locally authenticated CLI selected in Settings.
/// The historical class name is retained to avoid a broad service-registration migration.
/// </summary>
public partial class ClaudeCliService(
    ProcessRunner runner,
    CatMemeDbContext db,
    IConfiguration cfg,
    ILogger<ClaudeCliService> log)
{
    private readonly string _claudeCli = cfg["CLAUDE_CLI_PATH"] ?? "claude";
    private readonly string _codexCli = cfg["CODEX_CLI_PATH"] ?? "codex";

    /// <summary>
    /// How long one CLI invocation may take before it is killed.
    ///
    /// This was 5 minutes, and generation quietly stopped working when the CLI's default
    /// model began spending a minute or more on extended thinking before emitting any
    /// output: a five-script request measured 3-6 minutes and straddled the limit, so
    /// every batch timed out and produced nothing. A generous ceiling costs nothing when
    /// the call succeeds - it is a kill switch for a hung process, not a pacing tool.
    /// </summary>
    private static readonly TimeSpan CliTimeout = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Scripts requested per CLI invocation. Each one is a 6-9 beat storyboard with full
    /// directives, so a large batch is a lot of generated JSON in a single reply. Chunking
    /// keeps every call far inside <see cref="CliTimeout"/> and means a late failure costs
    /// one chunk instead of the whole batch.
    ///
    /// Three, because <see cref="ThinkingBudget"/> is on. Five in one call measured 526s
    /// against the 15 minute timeout - safe, but 59% of the budget spent on a default
    /// batch, and an empty batch on a slow day is the exact failure this pipeline already
    /// had once. Three measured 208s. The extra call costs one more copy of the prompt,
    /// about a cent, which is a cheap price for that much headroom.
    /// </summary>
    private const int TopicsPerCall = 3;

    /// <summary>
    /// Replaces the interactive agent's system prompt. This job is one-shot text
    /// generation: there is no conversation to manage, no tools to choose between, and no
    /// repository to reason about.
    /// </summary>
    private const string ScriptWriterSystemPrompt =
        "You are a comedy writer for short-form cat meme reels. You reply with exactly " +
        "the JSON the user asks for: no markdown, no prose, no code fences.";

    /// <summary>
    /// Extended thinking budget for a script call, billed as output tokens.
    ///
    /// Kept ON for the writing itself: the structural checks (beat count, roles, castable
    /// actions) pass either way, but they cannot measure whether a joke lands, and that is
    /// the part worth paying for. It is CAPPED rather than left open, because uncapped
    /// thinking against a stripped-down system prompt ran away - the model reasoned for
    /// minutes and cost more per call than the original full-agent setup did.
    ///
    /// Set to "0" to switch thinking off. That measured ~3.8x cheaper on output tokens and
    /// ~5.5x faster, at the cost of whatever the reasoning was contributing to the comedy.
    /// </summary>
    private static readonly Dictionary<string, string> ThinkingBudget =
        new() { ["MAX_THINKING_TOKENS"] = "4000" };

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private sealed record DialogueDto(
        string? speaker, string? text,
        string? cat_a_action, string? cat_b_action, string? stage,
        string? story_role, StoryboardDirectives? directives);

    private sealed record TopicDto(
        string? topic, string? cat_a_label, string? cat_b_label,
        string? cat_a_position, string? cat_b_position,
        string? cat_a_action, string? cat_b_action,
        string? caption_top, string? caption_bottom, string? mood, string? layout,
        string? storyboard_profile, List<DialogueDto>? dialogue);

    private sealed class CodexCliInvocationException(string message) : InvalidOperationException(message)
    {
    }

    /// <summary>
    /// Scripts for one batch, plus whether they came from the built-in seed set rather
    /// than the AI CLI. The caller needs to know: the seeds are a small fixed list, so
    /// holding them to the same "never repeat a recent topic" rule as fresh AI output
    /// rejects all of them and leaves the batch empty.
    /// </summary>
    public sealed record TopicBatch(List<Topic> Topics, bool FromFallback);

    /// <summary>
    /// The actions the curated clip library can actually show. Kept in sync with the
    /// clips' action tags; the model must pick from this list so every requested
    /// reaction is renderable.
    /// </summary>
    public static readonly string[] ClipActions =
        ["talking", "gagging", "asking", "innocent", "dancing", "begging",
         "silly", "shocked", "eating", "staring", "angry", "vibing", "sleeping"];

    internal static string DefaultStoryboardPromptContract =>
        $"Quality gate {StoryboardQualityProfile.Default.Id}: " +
        $"{StoryboardQualityProfile.Default.MinBeats}-{StoryboardQualityProfile.Default.MaxBeats} purposeful beats; " +
        "setup -> escalation/time_change -> payoff; complete safe directives on every beat; " +
        "interaction_action is none|slap and slap is only an earned final two-cat payoff.";

    /// <summary>
    /// Asks the configured AI CLI for a batch of POV scripts, in chunks of
    /// <see cref="TopicsPerCall"/>. Each chunk avoids what earlier chunks just produced,
    /// so splitting the request does not reintroduce the repetition the avoid-list exists
    /// to prevent. Whatever the CLI managed to produce is returned; only a completely
    /// empty result falls back.
    /// </summary>
    public async Task<TopicBatch> GenerateTopicsAsync(
        int count, IReadOnlyCollection<string>? avoidTopics = null, CancellationToken ct = default)
    {
        var avoid = new List<string>(avoidTopics ?? []);
        var collected = new List<Topic>();

        for (var remaining = count; remaining > 0; remaining -= TopicsPerCall)
        {
            var ask = Math.Min(remaining, TopicsPerCall);
            var raw = await InvokeAsync(BuildTopicsPrompt(ask, avoid), CliTimeout, ct);
            var chunk = ParseTopics(raw, ask, StoryboardQualityProfile.Default);

            if (chunk.Count == 0)
            {
                // One dead chunk means the CLI is unhealthy right now - timed out, signed
                // out, rate limited. Grinding through the remaining chunks would burn a
                // full timeout each for the same answer, so stop and use what we have.
                log.LogWarning(
                    "AI CLI produced no usable scripts for this chunk; stopping after {N} of {Count}.",
                    collected.Count, count);
                break;
            }

            collected.AddRange(chunk);
            avoid.AddRange(chunk.Select(t => t.TopicText));
        }

        if (collected.Count > 0) return new TopicBatch(collected, FromFallback: false);

        log.LogWarning("AI CLI returned no usable topics; using built-in fallback set.");
        return new TopicBatch(FallbackTopics(count), FromFallback: true);
    }

    private static string BuildTopicsPrompt(int count, IReadOnlyCollection<string>? avoidTopics)
    {
        var avoidBlock = avoidTopics is { Count: > 0 }
            ? "\n\nALREADY USED - do not repeat or closely paraphrase ANY of these:\n"
              + string.Join("\n", avoidTopics.Select(t => $"- {t}"))
            : "";

        var prompt = $$$"""
            Generate {{{count}}} POV cat meme reel scripts as a JSON array.

            Voice, from a survey of top-performing cat meme reels:
            - "topic" is the on-screen title. Most should START with "POV:". Lowercase or
              sentence case, 5-12 words, first-person, deadpan.
            - A title is a JOKE, not a topic. Naming a situation is NOT a joke: "she took
              the blanket" or "he won't wake up" is a complaint, and nobody shares a
              complaint. The line must TURN - its last few words have to reverse it,
              escalate it past what is reasonable, or quietly admit something. Test every
              title by asking what the second half does to the first. If it only continues
              it, the title has no joke in it yet: rewrite it.
            - The strongest turn is SELF-INCRIMINATION - the narrator is the one at fault
              and gives themselves away without noticing. Write roughly half the batch this
              way. NEVER write a whole batch where the partner is the only one in the wrong;
              that reads as complaining about a person rather than laughing at a couple.
            - Be SPECIFIC. A concrete number, object or span of time is what makes a reader
              feel personally caught: "4 hours", "six alarms", "two months ago", "her side
              of the bed". Vague nouns kill it - never "something", "stuff", "a while".
            - PLAIN and LITERAL means REAL, not bland. Exaggerating a real behaviour is the
              whole point: "he acts like he's dying when he has a cold" is right. What is
              banned is the surreal and the writerly - nothing physically impossible, no
              metaphors, no wordplay, no puns. "He has already written his will" is wrong.
            - Two worked examples of the turn, to show the mechanism only. Do NOT reuse
              either line:
              flat -> "POV: she took the whole blanket and she's blaming me"
              turned -> "POV: I took the whole blanket and somehow I'm the one upset"
              flat -> "POV: he set six alarms and is still not awake"
              turned -> "POV: I set six alarms and blamed her for turning one off"
            - EVERY script is about a ROMANTIC RELATIONSHIP. The two cats are a couple -
              me/gf, me/bf, her/him. This is the whole channel, so there are no exceptions:
              never write friend-group, roommate, sibling, parent, pet or coworker jokes.
              No "that one friend who...", no "the boys", no "my mom", no "my roommate".
              If a scenario only works between friends, it is the wrong scenario.
            - Every script must be a DIFFERENT scenario. Spread across the situations
              couples actually argue and joke about: the silent treatment, "I'm not mad",
              jealousy over who they were texting, apologising badly, hogging the bed or
              the blanket, eating off my plate, leaving me on read, spending money without
              saying, choosing the gym or the game over me, refusing to get up / setting a
              5am alarm, pretending to be fine, overreacting to something tiny, borrowing
              my hoodie and keeping it, who pays, taking forever to get ready, saying they
              don't care where we eat and then rejecting everywhere. Invent fresh ones too.
              Do not write two scripts on the same theme in one batch. These are STARTING
              POINTS, not titles - a theme with no turn on it is exactly the flat line the
              rules above reject.
            - "cat_a_label" / "cat_b_label" are short role tags shown next to each cat, and
              must read as a couple: "Me", "Gf", "Bf", "her:", "him:", "bae". Two cats are
              on screen almost always.
            - "cat_a_action" / "cat_b_action": the reaction each cat visibly performs,
              chosen ONLY from this list: {{{string.Join(", ", ClipActions)}}}.
              The two cats MUST have DIFFERENT actions - the joke is the contrast between
              two reactions. The sole exception is sleeping + sleeping: the renderer
              intentionally mirrors one clean sleeping cat as two characters.
              The action must fit the joke: the accused looks "innocent" or "shocked",
              the complainer is "talking" or "angry", the oblivious one is "vibing" or
              "eating", the wanting one is "begging" or "asking", the disgusted one is
              "gagging", the winner is "dancing" or "silly", the judge is "staring".
              CRITICAL: if the joke says a character is quiet, silent, ignoring, or says
              nothing, that cat's action MUST be one of: staring, innocent, vibing,
              eating, shocked, asking, sleeping. Never give a quiet character "talking", "angry"
              or "gagging" - those cats visibly yell.
            - "layout" should be "side_by_side" for most items. Use "single_pov" only when
              the joke genuinely involves one character.
            - "storyboard_profile" MUST be "{{{StoryboardQualityProfile.Default.Id}}}".
            - "dialogue" is the REQUIRED storyboard: 6 to 9 purposeful beats in spoken
              order, where "a" is cat A and "b" is cat B. Every beat has this shape:
              {"speaker":"a"|"b", "text":"...", "cat_a_action":null|ACTION,
               "cat_b_action":null|ACTION, "stage":null|STAGE,
               "story_role":ROLE,
               "directives":{"title_visible":true|false,"title_style":TITLE_STYLE,
                 "labels_visible":true|false,"lighting":LIGHTING,"cat_scale":CAT_SCALE,
                 "transition":TRANSITION,"sound_cue":SOUND_CUE,
                 "interaction_action":INTERACTION_ACTION}}. Rules:
                * exactly 6 to 9 beats, mostly alternating; no filler or repeated beat
                * ROLE is only: setup, escalation, time_change, reaction, payoff
                * EXACTLY ONE beat has ROLE setup, and it MUST be the first beat. No later
                  beat may be setup, however much it feels like a second scene opening.
                * EXACTLY ONE beat has ROLE payoff, and it MUST be the last beat. No
                  earlier beat may be payoff. It lands the strongest joke.
                * at least one beat between the first and the last has ROLE escalation or
                  time_change. Every other middle beat is reaction.
                * a time_change beat MUST have non-empty "text" - a short card label such
                  as "5:00 AM" or "a few moments later". A blank time card is rejected.
                * each turn UNDER 60 characters - these are text-message length, not speeches
                * text may be empty ONLY for a purely visual beat with an action or stage cue
                * write it as real talk, no quotation marks
                * action fields are optional visual state CHANGES and persist into later turns;
                  use them whenever a character's visible reaction changes during the story
                * ACTION comes only from the action list above. Visible cats must have
                  different actions except that both may be sleeping
                * STAGE is only: hide_both, hide_a, hide_b, show_both, show_a, show_b,
                  transition, sleep_pause. It applies for one beat only. Use transition on a
                  centred time-jump card. Use sleep_pause for rest/darkness without visible
                  cats; prefer sleeping actions when the cats should be visible
                * EVERY directives object is REQUIRED and complete. TITLE_STYLE is only:
                  standard, minimal, time_card, payoff. LIGHTING is only: normal, dim, dark,
                  flash. CAT_SCALE is only: normal, close_a, close_b, wide. TRANSITION is
                  only: cut, fade, flash, time_card, smash_cut. SOUND_CUE is only: none, pop,
                  whoosh, alarm, impact, record_scratch. INTERACTION_ACTION is only none or
                  slap; use slap only on the final payoff of a two-cat story where the setup
                  genuinely earns that physical gag, never as a random reaction
                * a time_change beat should use time_card styling/transition; a payoff must
                  be visibly or audibly strong using payoff title style, smash_cut, impact,
                  or record_scratch. Do not invent fonts, effects, file paths, or cue names.
                * reference house style: show the outlined POV title on ordinary setup,
                  escalation and reaction beats; hide it on time cards and the final payoff.
                  Keep labels_visible true so each cat carries its role tag, as the reference reel does.
                  Every time_change needs short visible card text such as "5:00 AM" or
                  "a few moments later"; never emit a blank time card.
              Arc shape: setup, reactions that escalate or mark elapsed time, strong payoff.

            Each object must have exactly these keys:
            "topic", "cat_a_label", "cat_b_label", "cat_a_position", "cat_b_position",
            "cat_a_action", "cat_b_action", "caption_top", "caption_bottom", "mood", "layout",
            "storyboard_profile", "dialogue"

            - cat_a_position / cat_b_position: "left" | "right" | "top" | "bottom" | "overlay_small"
            - caption_top / caption_bottom: usually null; when used, plain text only, no emoji
            - mood: "funny" | "wholesome" | "dramatic"
            {{{avoidBlock}}}

            {{{DefaultStoryboardPromptContract}}}

            Return ONLY the JSON array. No markdown, no prose, no code fences.
            """;

        return prompt;
    }

    /// <summary>
    /// Builds ONE script around a topic the user typed, instead of inventing topics.
    ///
    /// The supplied line is preserved verbatim - it is the user's joke, and a model asked to
    /// "improve" it reliably rewrites away the specific detail that made it worth filming.
    /// Only the surrounding craft (labels, actions, layout, mood, dialogue) is generated.
    /// </summary>
    public async Task<Topic> BuildScriptForTopicAsync(string topicText, CancellationToken ct = default)
    {
        var prompt = $$$"""
            Build ONE POV cat meme reel script for this exact topic:

            "{{{topicText}}}"

            Return a JSON array containing exactly one object with these keys:
            "topic", "cat_a_label", "cat_b_label", "cat_a_position", "cat_b_position",
            "cat_a_action", "cat_b_action", "caption_top", "caption_bottom", "mood", "layout",
            "storyboard_profile", "dialogue"

            - "topic" must be the given line, unchanged.
            - "cat_a_label" / "cat_b_label": short role tags shown next to each cat, chosen to
              fit this joke - "Me", "Gf", "Bro", "her:", "Mom".
            - "cat_a_action" / "cat_b_action": what each cat visibly performs, chosen ONLY
              from this list: {{{string.Join(", ", ClipActions)}}}. They MUST differ except for
              sleeping + sleeping. If the joke says a character is quiet, silent or
              ignoring, that cat's action MUST be one of: staring, innocent, vibing, eating,
              shocked, asking, sleeping.
            - "layout": "side_by_side" unless the joke genuinely involves one character, in
              which case "single_pov".
            - "storyboard_profile": exactly "{{{StoryboardQualityProfile.Default.Id}}}".
            - "dialogue": a REQUIRED 6 to 9 beat storyboard. Beat 1 is setup; middle beats
              escalate the situation or explicitly mark a time change; the LAST beat is a
              strong payoff. Every beat has {"speaker":"a"|"b", "text":"...",
              "cat_a_action":null|ACTION, "cat_b_action":null|ACTION,
              "stage":null|STAGE, "story_role":ROLE,
              "directives":{"title_visible":true|false,"title_style":TITLE_STYLE,
              "labels_visible":true|false,"lighting":LIGHTING,"cat_scale":CAT_SCALE,
              "transition":TRANSITION,"sound_cue":SOUND_CUE,
              "interaction_action":INTERACTION_ACTION}}. Each beat must advance the
              joke; do not add filler. Optional action fields persist into later turns. Use
              them for real scene changes such as asking -> sleeping -> angry. ACTION comes
              only from the action list above; visible cats must differ except sleeping +
              sleeping. STAGE is only hide_both, hide_a, hide_b, show_both, show_a, show_b,
              transition, sleep_pause. ROLE is only setup, escalation, time_change, reaction,
              payoff. TITLE_STYLE is only standard, minimal, time_card, payoff; LIGHTING is
              normal, dim, dark, flash; CAT_SCALE is normal, close_a, close_b, wide;
              TRANSITION is cut, fade, flash, time_card, smash_cut; SOUND_CUE is none, pop,
              whoosh, alarm, impact, record_scratch; INTERACTION_ACTION is none or slap.
              Use slap only on the final payoff of a two-cat story where the topic and setup
              genuinely earn a physical gag. All other beats use none. All other directive
              fields are required. A
              time_change should use a time_card; the final payoff must use payoff style,
              smash_cut, impact, or record_scratch. Never invent directive values.
              Use the outlined title on normal setup/escalation/reaction beats, hide it on
              time cards and the final payoff, and keep labels_visible true so each cat
              keeps its role tag on screen. A time_change must contain short visible card
              text; never make a blank time card.
            - caption_top / caption_bottom: null unless genuinely needed.
            - mood: "funny" | "wholesome" | "dramatic"

            {{{DefaultStoryboardPromptContract}}}

            Return ONLY the JSON array. No markdown, no prose, no code fences.
            """;

        try
        {
            var raw = await InvokeAsync(prompt, CliTimeout, ct);
            var parsed = ParseTopics(raw, 1, StoryboardQualityProfile.Default);
            if (parsed.Count > 0)
            {
                // Trust nothing about the title: keep exactly what was typed.
                parsed[0].TopicText = topicText;
                return parsed[0];
            }
            log.LogWarning("AI CLI returned no usable script for '{Topic}'; using defaults.", topicText);
        }
        catch (CodexCliInvocationException)
        {
            // A selected Codex provider must fail visibly. Otherwise a missing login,
            // stale CLI, or incompatible user config looks like successful AI output.
            throw;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Script generation failed for '{Topic}'; using defaults.", topicText);
        }

        // Keyless / CLI-less installs still get a renderable script. Common narrative
        // categories receive a deterministic visual arc instead of the old universal
        // talking/staring pair, which was especially wrong for sleep and wake-up jokes.
        return FallbackScriptForTopic(topicText);
    }

    internal static Topic FallbackScriptForTopic(string topicText)
    {
        var lower = topicText.ToLowerInvariant();
        var isWakeRequest =
            (lower.Contains("ask") || lower.Contains("tell")) && lower.Contains("wake") ||
            lower.Contains("wake her") || lower.Contains("wake him") || lower.Contains("wake me");

        if (isWakeRequest)
        {
            var partner = lower.Contains("gf") || lower.Contains("girlfriend") ? "Gf"
                : lower.Contains("bf") || lower.Contains("boyfriend") ? "Bf"
                : "Partner";

            return new Topic
            {
                TopicText = topicText,
                CatALabel = "Me",
                CatBLabel = partner,
                // The root cast is the opening shot. Later state changes (including the
                // whole-scene slap payoff) are cast from their own authored beats.
                CatAAction = "staring",
                CatBAction = "asking",
                Layout = Domain.Layout.SideBySide,
                Mood = "funny",
                DialogueJson = DialogueLine.Serialize([
                    DefaultBeat("b", "Can you wake me up?", StoryboardRole.Setup,
                        "staring", "asking", titleVisible: true, soundCue: StoryboardSoundCue.Pop),
                    DefaultBeat("a", "Okay", StoryboardRole.Reaction,
                        catScale: StoryboardCatScale.CloseA),
                    DefaultBeat("a", "5:00 AM", StoryboardRole.TimeChange,
                        "sleeping", "sleeping", titleVisible: false,
                        titleStyle: StoryboardTitleStyle.TimeCard, labelsVisible: false,
                        lighting: StoryboardLighting.Dark, catScale: StoryboardCatScale.Wide,
                        transition: StoryboardTransition.TimeCard, soundCue: StoryboardSoundCue.Whoosh),
                    DefaultBeat("a", "Time to wake up", StoryboardRole.Escalation,
                        "talking", "shocked", lighting: StoryboardLighting.Flash,
                        catScale: StoryboardCatScale.CloseA,
                        transition: StoryboardTransition.Flash, soundCue: StoryboardSoundCue.Alarm),
                    DefaultBeat("b", "Five more minutes", StoryboardRole.Reaction,
                        "staring", "sleeping", lighting: StoryboardLighting.Dim,
                        catScale: StoryboardCatScale.CloseB, soundCue: StoryboardSoundCue.Pop),
                    DefaultBeat("a", "32 minutes later", StoryboardRole.TimeChange,
                        "sleeping", "sleeping", titleVisible: false,
                        titleStyle: StoryboardTitleStyle.TimeCard, labelsVisible: false,
                        lighting: StoryboardLighting.Dark, catScale: StoryboardCatScale.Wide,
                        transition: StoryboardTransition.TimeCard, soundCue: StoryboardSoundCue.Whoosh),
                    DefaultBeat("b", "Why didn't you wake me?", StoryboardRole.Payoff,
                        "innocent", "angry", titleVisible: false,
                        titleStyle: StoryboardTitleStyle.Payoff,
                        catScale: StoryboardCatScale.CloseB,
                        transition: StoryboardTransition.SmashCut, soundCue: StoryboardSoundCue.Impact,
                        interactionAction: StoryboardInteractionAction.Slap),
                ]),
            };
        }

        var sleepScene = lower.Contains("sleep") || lower.Contains("asleep") ||
                         lower.Contains("bed") || lower.Contains("alarm");
        return new Topic
        {
            TopicText = topicText,
            CatALabel = "me",
            CatBLabel = "her",
            CatAAction = sleepScene ? "sleeping" : "talking",
            CatBAction = "staring",
            Layout = Domain.Layout.SideBySide,
            Mood = "funny",
            DialogueJson = DefaultFallbackDialogue(
                sleepScene ? "sleeping" : "talking", "staring"),
        };
    }

    private static DialogueLine DefaultBeat(
        string speaker,
        string text,
        string storyRole,
        string? catAAction = null,
        string? catBAction = null,
        string? stage = null,
        bool titleVisible = true,
        string titleStyle = StoryboardTitleStyle.Minimal,
        // Role tags stay ON by default, as in the reference reel, where "her" and "me" are
        // held under the cats for the whole runtime. This defaulted to false, and because
        // every fallback-authored beat inherits it, profiled reels shipped with no role tags
        // at all - the renderer was doing exactly what the storyboard asked. Time-card beats
        // still pass false explicitly, since the card owns the whole frame.
        bool labelsVisible = true,
        string lighting = StoryboardLighting.Normal,
        string catScale = StoryboardCatScale.Normal,
        string transition = StoryboardTransition.Cut,
        string soundCue = StoryboardSoundCue.None,
        string? interactionAction = StoryboardInteractionAction.None) =>
        new(
            speaker,
            text,
            catAAction,
            catBAction,
            stage,
            storyRole,
            new StoryboardDirectives(
                titleVisible,
                titleStyle,
                labelsVisible,
                lighting,
                catScale,
                transition,
                soundCue,
                interactionAction),
            StoryboardQualityProfile.Default.Id);

    private static string DefaultFallbackDialogue(string catAAction, string catBAction) =>
        DialogueLine.Serialize([
            DefaultBeat("a", "This was supposed to be simple", StoryboardRole.Setup,
                catAAction, catBAction, titleVisible: true, soundCue: StoryboardSoundCue.Pop),
            DefaultBeat("b", "Then why did you do that?", StoryboardRole.Escalation,
                catScale: StoryboardCatScale.CloseB),
            DefaultBeat("a", "It made sense at the time", StoryboardRole.Reaction,
                catScale: StoryboardCatScale.CloseA),
            DefaultBeat("b", "That is your explanation?", StoryboardRole.Escalation,
                catScale: StoryboardCatScale.CloseB, soundCue: StoryboardSoundCue.Pop),
            DefaultBeat("a", "So we're good?", StoryboardRole.Reaction,
                catScale: StoryboardCatScale.CloseA),
            DefaultBeat("b", "We are absolutely not good", StoryboardRole.Payoff,
                titleVisible: true, titleStyle: StoryboardTitleStyle.Payoff,
                catScale: StoryboardCatScale.CloseB,
                transition: StoryboardTransition.SmashCut, soundCue: StoryboardSoundCue.Impact),
        ]);

    /// <summary>
    /// Reference reels use 2-5 hashtags (mean 3.3), not the 10-15 the original spec assumed.
    /// The top page reuses one fixed set on every post rather than tailoring per video.
    /// </summary>
    public async Task<string> GenerateHashtagsAsync(string topic, CancellationToken ct = default)
    {
        var prompt = $"""
            Give exactly 3 lowercase hashtags for this cat meme reel about couple humor: "{topic}"
            Use broad, high-traffic tags like #funnycat #relatable #relationships #catmemes.
            Return them space separated on one line, each starting with #. No other text.
            """;
        try
        {
            var raw = (await InvokeAsync(prompt, TimeSpan.FromMinutes(2), ct)).Trim();
            var tags = HashtagRx().Matches(raw).Select(m => m.Value.ToLowerInvariant()).Distinct().Take(4).ToList();
            if (tags.Count > 0) return string.Join(' ', tags);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Hashtag generation failed for '{Topic}'", topic);
        }
        return "#catsofinstagram #couplecomedy #relatable";
    }

    private async Task<string> InvokeAsync(string prompt, TimeSpan timeout, CancellationToken ct)
    {
        var settings = await SettingsAccessor.GetAsync(db, ct);
        var useCodex = settings.ScriptProvider == "codex";
        ProcessResult result;
        string output;

        if (useCodex)
        {
            // Codex's -o contract isolates the final assistant message from progress output.
            // The prompt goes over stdin to avoid Windows' command-line length limit.
            var outputFile = Path.Combine(Path.GetTempPath(), $"catmeme-codex-{Guid.NewGuid():N}.txt");
            try
            {
                result = await runner.RunAsync(_codexCli,
                    ["exec", "--ignore-user-config", "--ephemeral", "--sandbox", "read-only", "--skip-git-repo-check",
                     "--color", "never", "--output-last-message", outputFile, "-"],
                    timeout, stdin: prompt, ct: ct);
                output = File.Exists(outputFile)
                    ? await File.ReadAllTextAsync(outputFile, ct)
                    : result.StdOut;
            }
            finally
            {
                FFmpegService.TryDelete(outputFile);
            }
        }
        else
        {
            // -p runs Claude in non-interactive print mode.
            //
            // The rest strips the interactive agent down to the one thing this pipeline
            // wants: JSON on stdout. Its default system prompt, its tool definitions and
            // the CLAUDE.md files for whatever directory the app was started from are all
            // billed on every call and none of them can influence a pure text generation
            // - the CLAUDE.md picked up here was about editing YouTube videos in CapCut.
            // Measured on a three-script request: 26,535 input tokens -> 3,536, with the
            // same scripts accepted at the same beat count.
            result = await runner.RunAsync(_claudeCli,
                ["-p", prompt,
                 "--system-prompt", ScriptWriterSystemPrompt,
                 "--tools", "",
                 "--setting-sources", "",
                 "--strict-mcp-config"],
                timeout, environment: ThinkingBudget, ct: ct);
            output = result.StdOut;
        }

        if (!result.Ok)
        {
            var error = FFmpegService.Tail(result.StdErr);
            log.LogWarning("{Provider} CLI exit {Code}: {Err}",
                useCodex ? "Codex" : "Claude", result.ExitCode, error);

            if (useCodex)
                throw new CodexCliInvocationException(
                    $"Codex CLI failed with exit code {result.ExitCode}. " +
                    $"Make sure Codex is installed and signed in. {error}");
        }

        if (useCodex && string.IsNullOrWhiteSpace(output))
            throw new CodexCliInvocationException(
                "Codex CLI completed successfully but produced no final response. " +
                "Make sure Codex is installed, signed in, and up to date.");

        return output;
    }

    /// <summary>
    /// Legacy overload retained for parser-focused callers. Production generation passes
    /// the default profile explicitly, which stamps and strictly validates every new script.
    /// </summary>
    internal List<Topic> ParseTopics(string raw, int max) => ParseTopics(raw, max, requiredProfile: null);

    internal List<Topic> ParseTopics(
        string raw,
        int max,
        StoryboardQualityProfile? requiredProfile)
    {
        var json = ExtractJsonArray(raw);
        if (json is null) return [];

        try
        {
            var dtos = JsonSerializer.Deserialize<List<TopicDto>>(json, JsonOpts) ?? [];
            var topics = new List<Topic>();
            foreach (var d in dtos.Where(d => !string.IsNullOrWhiteSpace(d.topic)))
            {
                if (topics.Count >= max) break;

                if (requiredProfile is not null &&
                    !string.Equals(Clean(d.storyboard_profile), requiredProfile.Id,
                        StringComparison.OrdinalIgnoreCase))
                {
                    log.LogWarning(
                        "Discarding AI topic because storyboard_profile was '{Profile}', expected '{Expected}'",
                        Clean(d.storyboard_profile) ?? "missing", requiredProfile.Id);
                    continue;
                }

                var dialogueJson = BuildDialogue(d.dialogue, requiredProfile);
                if (requiredProfile is not null && dialogueJson is null)
                {
                    log.LogWarning("Discarding AI topic because its default storyboard was incomplete or unsafe");
                    continue;
                }

                var topic = new Topic
                {
                    TopicText = d.topic!.Trim(),
                    CatALabel = Clean(d.cat_a_label) ?? "me:",
                    CatBLabel = Clean(d.cat_b_label) ?? "her:",
                    CatAPosition = Clean(d.cat_a_position) ?? "left",
                    CatBPosition = Clean(d.cat_b_position) ?? "right",
                    CatAAction = ValidAction(d.cat_a_action),
                    CatBAction = ValidAction(d.cat_b_action),
                    CaptionTop = Clean(d.caption_top),
                    CaptionBottom = Clean(d.caption_bottom),
                    Mood = Clean(d.mood) is { } m && m is "funny" or "wholesome" or "dramatic" ? m : "funny",
                    Layout = Clean(d.layout) is { } l && l is Domain.Layout.SideBySide or Domain.Layout.TopBottom
                        or Domain.Layout.OverlaySmall or Domain.Layout.StackedCenter
                        ? l : Domain.Layout.SinglePov,
                    DialogueJson = dialogueJson,
                };

                if (requiredProfile is not null)
                {
                    var check = ScriptValidator.Validate(topic, requiredProfile);
                    if (!check.Ok)
                    {
                        log.LogWarning(
                            "Discarding AI topic '{Topic}' because it missed the {Profile} standard: {Errors}",
                            topic.TopicText, requiredProfile.Id, string.Join("; ", check.Errors));
                        continue;
                    }
                }

                topics.Add(topic);
            }

            return topics;
        }
        catch (JsonException ex)
        {
            log.LogWarning(ex, "Could not parse topic JSON from AI CLI output.");
            return [];
        }
    }

    private static string? Clean(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var t = s.Trim();
        return t.Equals("null", StringComparison.OrdinalIgnoreCase) ? null : t;
    }

    private static string? ValidAction(string? s)
    {
        var t = Clean(s)?.ToLowerInvariant();
        return t is not null && ClipActions.Contains(t) ? t : null;
    }

    private static string? ValidStage(string? s)
    {
        var t = Clean(s)?.ToLowerInvariant();
        return t is not null && DialogueStage.All.Contains(t) ? t : null;
    }

    private static string? ValidStoryRole(string? s)
    {
        var t = Clean(s)?.ToLowerInvariant();
        return t is not null && StoryboardRole.All.Contains(t) ? t : null;
    }

    private static StoryboardDirectives? ValidDirectives(StoryboardDirectives? raw)
    {
        if (raw is null) return null;

        var normalized = new StoryboardDirectives(
            raw.TitleVisible,
            Clean(raw.TitleStyle)?.ToLowerInvariant(),
            raw.LabelsVisible,
            Clean(raw.Lighting)?.ToLowerInvariant(),
            Clean(raw.CatScale)?.ToLowerInvariant(),
            Clean(raw.Transition)?.ToLowerInvariant(),
            Clean(raw.SoundCue)?.ToLowerInvariant(),
            Clean(raw.InteractionAction)?.ToLowerInvariant());

        return normalized.IsSafe ? normalized : null;
    }

    /// <summary>
    /// Normalises the optional conversation. Anything unusable becomes null rather than a
    /// half-built exchange, so the reel falls back to the static style instead of rendering
    /// a conversation with a missing turn.
    /// </summary>
    private static string? BuildDialogue(
        List<DialogueDto>? raw,
        StoryboardQualityProfile? requiredProfile = null)
    {
        if (raw is null || raw.Count == 0) return null;
        if (requiredProfile is not null &&
            (raw.Count < requiredProfile.MinBeats || raw.Count > requiredProfile.MaxBeats))
            return null;

        var lines = new List<DialogueLine>();
        foreach (var d in raw)
        {
            // Empty text is meaningful on a purely visual beat (sleeping, lights out, or
            // a hidden pause). Null still means the model omitted the required field.
            var text = d.text?.Trim();
            if (text is null) return null;

            var speaker = Clean(d.speaker)?.ToLowerInvariant();
            speaker = speaker switch
            {
                "a" or "cat_a" or "left" => DialogueLine.SpeakerA,
                "b" or "cat_b" or "right" => DialogueLine.SpeakerB,
                _ => null,
            };
            if (speaker is null) return null;

            var actionA = ValidAction(d.cat_a_action);
            var actionB = ValidAction(d.cat_b_action);
            var stage = ValidStage(d.stage);
            var storyRole = ValidStoryRole(d.story_role);
            var directives = ValidDirectives(d.directives);

            // A supplied-but-invalid visual directive must not be silently erased. Correct
            // words over the wrong, unchanged cat state are worse than rejecting the script.
            if (Clean(d.cat_a_action) is not null && actionA is null) return null;
            if (Clean(d.cat_b_action) is not null && actionB is null) return null;
            if (Clean(d.stage) is not null && stage is null) return null;
            if (Clean(d.story_role) is not null && storyRole is null) return null;
            if (d.directives is not null && directives is null) return null;
            if (requiredProfile is not null && (storyRole is null || directives is null)) return null;
            var purposefulTimeCard =
                string.Equals(storyRole, StoryboardRole.TimeChange, StringComparison.OrdinalIgnoreCase) &&
                directives is not null;
            if (text.Length == 0 && actionA is null && actionB is null && stage is null &&
                !purposefulTimeCard)
                return null;

            lines.Add(new DialogueLine(
                speaker,
                text,
                actionA,
                actionB,
                stage,
                storyRole,
                directives,
                requiredProfile?.Id));
        }

        return lines.Count >= 2 ? DialogueLine.Serialize(lines) : null;
    }

    /// <summary>Pulls the JSON array out of whatever wrapper text the CLI emitted.</summary>
    internal static string? ExtractJsonArray(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var fence = FenceRx().Match(raw);
        if (fence.Success) raw = fence.Groups[1].Value;

        var start = raw.IndexOf('[');
        var end = raw.LastIndexOf(']');
        return start >= 0 && end > start ? raw[start..(end + 1)] : null;
    }

    /// <summary>Keeps the pipeline usable when the CLI is unavailable or rate limited.</summary>
    internal static List<Topic> FallbackTopics(int count)
    {
        var seeds = new (string Topic, string A, string B, string Mood, string ActA, string ActB)[]
        {
            ("pov: me when I need attention but my bf is busy", "me:", "him:", "funny", "asking", "vibing"),
            ("when she says she's not mad but her whole face says otherwise", "me:", "her:", "dramatic", "innocent", "angry"),
            ("me pretending to be asleep so she makes breakfast", "me:", "her:", "funny", "vibing", "staring"),
            ("when I steal her side of the bed and refuse to move", "me:", "her:", "funny", "vibing", "talking"),
            ("her waiting for me to notice the new haircut", "her:", "me:", "wholesome", "staring", "eating"),
            ("when he finally texts back after 4 hours", "me:", "him:", "dramatic", "angry", "innocent"),
            ("me explaining why I need another snack at 2am", "me:", "her:", "funny", "begging", "staring"),
            ("when she lets me have the last slice of pizza", "me:", "her:", "wholesome", "dancing", "vibing"),
            ("pov: I said one thing wrong and now it's a whole thing", "me:", "her:", "dramatic", "shocked", "talking"),
            ("when we both reach for the remote at the same time", "me:", "her:", "funny", "asking", "angry"),
        };

        // Start somewhere different each time. Reading from index 0 on every fallback meant
        // a run of CLI failures produced the same handful of seeds over and over, which is
        // what the caller sees on the Queue when the AI is down for a while.
        var offset = Random.Shared.Next(seeds.Length);

        return Enumerable.Range(0, count)
            .Select(i => seeds[(offset + i) % seeds.Length])
            .Select((s, i) => new Topic
            {
                TopicText = i >= seeds.Length ? $"{s.Topic} (take {i / seeds.Length + 1})" : s.Topic,
                CatALabel = s.A,
                CatBLabel = s.B,
                Mood = s.Mood,
                CatAAction = s.ActA,
                CatBAction = s.ActB,
                Layout = Domain.Layout.SideBySide,
                DialogueJson = DefaultFallbackDialogue(s.ActA, s.ActB),
            })
            .ToList();
    }

    [GeneratedRegex(@"```(?:json)?\s*(.*?)```", RegexOptions.Singleline)]
    private static partial Regex FenceRx();

    [GeneratedRegex(@"#[a-zA-Z0-9_]+")]
    private static partial Regex HashtagRx();
}
