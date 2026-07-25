using CatMeme.Api.Domain;

namespace CatMeme.Api.Services;

public record ScriptCheck(bool Ok, List<string> Errors);

/// <summary>
/// Strict validation of a script BEFORE any rendering happens.
///
/// The render pipeline used to trust the model's output and degrade soft when casting
/// failed, which produced videos that were technically valid but wrong - a visibly yelling
/// cat cast as the character who "goes quiet". Every rule here exists because its absence
/// produced a real bad video.
/// </summary>
public static class ScriptValidator
{
    /// <summary>Phrases in a title that mean one character is silent or unresponsive.</summary>
    private static readonly string[] QuietPhrases =
    [
        "goes quiet", "went quiet", "silent", "says nothing", "said nothing",
        "won't say", "wont say", "doesn't say", "doesn't answer", "no reply",
        "stops talking", "stopped talking", "ignores", "ignoring", "seen-zone",
        "left on read", "doesn't respond",
    ];

    /// <summary>
    /// Actions whose clips are visibly non-vocal. Must match the clip library's labels:
    /// "talking", "gagging" and "angry" clips visibly yell and can never play a quiet
    /// character.
    /// </summary>
    public static readonly HashSet<string> QuietActions = new(
        ["staring", "innocent", "vibing", "eating", "shocked", "asking", "sleeping"],
        StringComparer.OrdinalIgnoreCase);

    private static readonly string[] SlapSemanticHints =
        ["slap", "smack", "hit", "wake", "alarm", "caught", "steal", "stole", "grab", "fight"];

    public static ScriptCheck Validate(Topic t) => ValidateCore(t, requiredProfile: null);

    /// <summary>
    /// Forces a profile even before its marker has reached storage. AI parsing uses this
    /// overload; normal render validation auto-detects the embedded profile instead.
    /// </summary>
    public static ScriptCheck Validate(Topic t, StoryboardQualityProfile requiredProfile) =>
        ValidateCore(t, requiredProfile);

    private static ScriptCheck ValidateCore(Topic t, StoryboardQualityProfile? requiredProfile)
    {
        var errors = new List<string>();

        // --- title ---
        var title = (t.TopicText ?? "").Trim();
        var words = title.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length is < 4 or > 16)
            errors.Add($"title has {words.Length} words; expected 4-16");
        if (title.Any(c => c >= 0xD800 || c == 0xFFFD))
            errors.Add("title contains emoji or non-renderable characters");

        // --- labels ---
        if (string.IsNullOrWhiteSpace(t.CatALabel))
            errors.Add("cat A label is missing");
        var needsTwo = t.Layout != Layout.SinglePov;
        if (needsTwo && string.IsNullOrWhiteSpace(t.CatBLabel))
            errors.Add("cat B label is missing for a two-cat layout");

        // --- actions ---
        if (string.IsNullOrWhiteSpace(t.CatAAction) ||
            !ClaudeCliService.ClipActions.Contains(t.CatAAction!, StringComparer.OrdinalIgnoreCase))
            errors.Add($"cat A action '{t.CatAAction}' is missing or not in the clip vocabulary");
        if (needsTwo &&
            (string.IsNullOrWhiteSpace(t.CatBAction) ||
             !ClaudeCliService.ClipActions.Contains(t.CatBAction!, StringComparer.OrdinalIgnoreCase)))
            errors.Add($"cat B action '{t.CatBAction}' is missing or not in the clip vocabulary");

        // Two cats doing the SAME thing is not the format - the joke in every reference
        // reel is the contrast between two reactions. It is also uncastable against the
        // curated library, where an action tag is normally backed by a single clip, so the
        // two cats would have to be the same clip twice.
        if (needsTwo &&
            !string.IsNullOrWhiteSpace(t.CatAAction) &&
            string.Equals(t.CatAAction, t.CatBAction, StringComparison.OrdinalIgnoreCase) &&
            !t.CatAAction.Equals("sleeping", StringComparison.OrdinalIgnoreCase))
            errors.Add($"both cats perform '{t.CatAAction}'; the two actions must differ");

        // --- dialogue (optional) ---
        var dialogue = t.Dialogue();
        if (!string.IsNullOrWhiteSpace(t.DialogueJson) && dialogue.Count == 0)
            errors.Add("dialogue is present but could not be parsed");

        if (dialogue.Count > 0)
        {
            if (dialogue.Count is < 2 or > 12)
                errors.Add($"dialogue has {dialogue.Count} lines; expected 2-12");

            foreach (var line in dialogue)
            {
                var text = (line.Text ?? "").Trim();
                if (text.Length == 0 && !line.HasVisualDirective)
                    errors.Add("a dialogue line is empty and has no visual directive");
                // Each turn is confined to half the canvas; past this it wraps into a wall.
                else if (text.Length > 60)
                    errors.Add($"dialogue line is {text.Length} chars; keep turns under 60");

                if (text.Any(c => c >= 0xD800 || c == 0xFFFD))
                    errors.Add("a dialogue line contains emoji or non-renderable characters");

                if (!string.Equals(line.Speaker, DialogueLine.SpeakerA, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(line.Speaker, DialogueLine.SpeakerB, StringComparison.OrdinalIgnoreCase))
                    errors.Add($"dialogue speaker '{line.Speaker}' must be 'a' or 'b'");

                foreach (var (role, action) in new[]
                         { ("A", line.CatAAction), ("B", line.CatBAction) })
                {
                    if (!string.IsNullOrWhiteSpace(action) &&
                        !ClaudeCliService.ClipActions.Contains(action, StringComparer.OrdinalIgnoreCase))
                        errors.Add($"dialogue cat {role} action '{action}' is not in the clip vocabulary");
                }

                if (!string.IsNullOrWhiteSpace(line.Stage) && !DialogueStage.All.Contains(line.Stage))
                    errors.Add($"dialogue stage '{line.Stage}' is not supported");

                if (!string.IsNullOrWhiteSpace(line.StoryRole) &&
                    !StoryboardRole.All.Contains(line.StoryRole))
                    errors.Add($"story role '{line.StoryRole}' is not supported");

                if (line.Directives is not null && !line.Directives.IsSafe)
                    errors.Add("a storyboard beat contains incomplete or unsafe directives");
            }

            // Side is the ONLY cue for who is speaking, so a one-cat reel cannot carry
            // lines for a cat that is not on screen.
            if (!needsTwo && dialogue.Any(l => !l.IsA))
                errors.Add("a single_pov reel has no second cat, so every dialogue line must be speaker 'a'");
            else if (needsTwo && (dialogue.All(l => l.IsA) || dialogue.All(l => !l.IsA)))
                errors.Add("a two-cat conversation needs lines from both cats");

            // Visual action overrides persist until changed. Keep the contrast rule at
            // every visible beat, with one intentional exception: two sleeping cats may
            // duplicate/mirror the same clean source, as the reference format does.
            var beatActionA = t.CatAAction;
            var beatActionB = t.CatBAction;
            foreach (var line in dialogue)
            {
                if (!string.IsNullOrWhiteSpace(line.CatAAction)) beatActionA = line.CatAAction;
                if (!string.IsNullOrWhiteSpace(line.CatBAction)) beatActionB = line.CatBAction;

                var stage = line.Stage?.Trim().ToLowerInvariant();
                var bothVisible = needsTwo && stage is not DialogueStage.HideBoth
                    and not DialogueStage.HideA and not DialogueStage.HideB
                    and not DialogueStage.ShowA and not DialogueStage.ShowB
                    and not DialogueStage.Transition and not DialogueStage.SleepPause;

                if (bothVisible &&
                    !string.IsNullOrWhiteSpace(beatActionA) &&
                    string.Equals(beatActionA, beatActionB, StringComparison.OrdinalIgnoreCase) &&
                    !beatActionA.Equals("sleeping", StringComparison.OrdinalIgnoreCase))
                    errors.Add($"a visual beat gives both cats '{beatActionA}'; only sleeping may be duplicated");
            }
        }

        ValidateInteractions(t, dialogue, needsTwo, errors);
        ValidateStoryboardProfile(t, dialogue, needsTwo, requiredProfile, errors);

        // --- quiet-character consistency ---
        // Weak but reliable heuristic: mapping "she"/"he" to cat A or B is guesswork, but a
        // title about someone going quiet where NEITHER cat performs a quiet action is
        // always wrong (this is the exact bug that shipped a yelling cat as the quiet gf).
        var lower = title.ToLowerInvariant();
        if (QuietPhrases.Any(lower.Contains))
        {
            var aQuiet = t.CatAAction is not null && QuietActions.Contains(t.CatAAction);
            var bQuiet = !needsTwo || (t.CatBAction is not null && QuietActions.Contains(t.CatBAction));
            if (!aQuiet && !bQuiet)
                errors.Add("title implies a quiet character but neither cat performs a quiet action");
        }

        return new ScriptCheck(errors.Count == 0, errors);
    }

    private static void ValidateStoryboardProfile(
        Topic topic,
        IReadOnlyList<DialogueLine> dialogue,
        bool needsTwo,
        StoryboardQualityProfile? requiredProfile,
        List<string> errors)
    {
        var markers = dialogue
            .Select(line => line.StoryboardProfile?.Trim())
            .Where(marker => !string.IsNullOrWhiteSpace(marker))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var profile = requiredProfile;
        if (profile is null)
        {
            if (markers.Count == 0) return; // historical unmarked dialogue
            if (markers.Count != 1)
            {
                errors.Add("storyboard beats contain conflicting quality profiles");
                return;
            }

            profile = StoryboardQualityProfile.Resolve(markers[0]);
            if (profile is null)
            {
                errors.Add($"storyboard profile '{markers[0]}' is not supported");
                return;
            }
        }

        if (dialogue.Count < profile.MinBeats || dialogue.Count > profile.MaxBeats)
            errors.Add(
                $"{profile.Id} storyboard has {dialogue.Count} beats; " +
                $"expected {profile.MinBeats}-{profile.MaxBeats}");

        for (var i = 0; i < dialogue.Count; i++)
        {
            var beat = dialogue[i];
            var number = i + 1;

            if (!string.Equals(beat.StoryboardProfile, profile.Id, StringComparison.OrdinalIgnoreCase))
                errors.Add($"storyboard beat {number} is missing profile '{profile.Id}'");

            var role = beat.StoryRole?.Trim().ToLowerInvariant();
            if (role is null || !StoryboardRole.All.Contains(role))
                errors.Add($"storyboard beat {number} is missing a safe story_role");

            if (profile.RequireCompleteDirectives && beat.Directives is null)
                errors.Add($"storyboard beat {number} is missing directives");
            else if (profile.RequireCompleteDirectives && !beat.Directives!.IsComplete)
                errors.Add($"storyboard beat {number} does not specify every required directive");
            else if (beat.Directives is not null && !beat.Directives.IsSafe)
                errors.Add($"storyboard beat {number} contains an unsafe directive value");

            var purposeful = !string.IsNullOrWhiteSpace(beat.Text) ||
                             !string.IsNullOrWhiteSpace(beat.CatAAction) ||
                             !string.IsNullOrWhiteSpace(beat.CatBAction) ||
                             !string.IsNullOrWhiteSpace(beat.Stage) ||
                             string.Equals(role, StoryboardRole.TimeChange, StringComparison.OrdinalIgnoreCase);
            if (!purposeful)
                errors.Add($"storyboard beat {number} is filler; it needs dialogue, action, stage, or a time change");

            if (i > 0 && SamePurpose(dialogue[i - 1], beat))
                errors.Add($"storyboard beat {number} repeats the previous beat instead of advancing the joke");

            if (string.Equals(role, StoryboardRole.TimeChange, StringComparison.OrdinalIgnoreCase) &&
                beat.Directives is { } timeDirectives &&
                !string.Equals(timeDirectives.TitleStyle, StoryboardTitleStyle.TimeCard,
                    StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(timeDirectives.Transition, StoryboardTransition.TimeCard,
                    StringComparison.OrdinalIgnoreCase))
                errors.Add($"storyboard beat {number} is a time_change but has no time_card directive");

            if (string.Equals(role, StoryboardRole.TimeChange, StringComparison.OrdinalIgnoreCase) &&
                string.IsNullOrWhiteSpace(beat.Text))
                errors.Add($"storyboard beat {number} is a blank time card; add a short time label");

        }

        if (dialogue.Count == 0) return;

        if (!string.Equals(dialogue[0].StoryRole, StoryboardRole.Setup,
                StringComparison.OrdinalIgnoreCase))
            errors.Add("default storyboard must begin with a setup beat");

        if (!string.Equals(dialogue[^1].StoryRole, StoryboardRole.Payoff,
                StringComparison.OrdinalIgnoreCase))
            errors.Add("default storyboard must end with a payoff beat");

        if (dialogue.Skip(1).Any(beat =>
                string.Equals(beat.StoryRole, StoryboardRole.Setup, StringComparison.OrdinalIgnoreCase)))
            errors.Add("setup may only appear on the first storyboard beat");

        if (dialogue.Take(Math.Max(0, dialogue.Count - 1)).Any(beat =>
                string.Equals(beat.StoryRole, StoryboardRole.Payoff, StringComparison.OrdinalIgnoreCase)))
            errors.Add("payoff may only appear on the final storyboard beat");

        var middle = dialogue.Skip(1).Take(Math.Max(0, dialogue.Count - 2));
        if (!middle.Any(beat =>
                string.Equals(beat.StoryRole, StoryboardRole.Escalation, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(beat.StoryRole, StoryboardRole.TimeChange, StringComparison.OrdinalIgnoreCase)))
            errors.Add("default storyboard needs an escalation or time_change before the payoff");

        if (profile.RequireStrongPayoff && dialogue[^1].Directives?.IsStrongPayoff != true)
            errors.Add("final payoff needs payoff styling, a smash cut, impact, or record scratch");
    }

    private static bool SamePurpose(DialogueLine a, DialogueLine b) =>
        string.Equals(a.Text?.Trim(), b.Text?.Trim(), StringComparison.OrdinalIgnoreCase) &&
        string.Equals(a.CatAAction?.Trim(), b.CatAAction?.Trim(), StringComparison.OrdinalIgnoreCase) &&
        string.Equals(a.CatBAction?.Trim(), b.CatBAction?.Trim(), StringComparison.OrdinalIgnoreCase) &&
        string.Equals(a.Stage?.Trim(), b.Stage?.Trim(), StringComparison.OrdinalIgnoreCase) &&
        string.Equals(a.StoryRole?.Trim(), b.StoryRole?.Trim(), StringComparison.OrdinalIgnoreCase);

    private static void ValidateInteractions(
        Topic topic,
        IReadOnlyList<DialogueLine> dialogue,
        bool needsTwo,
        List<string> errors)
    {
        for (var i = 0; i < dialogue.Count; i++)
        {
            var beat = dialogue[i];
            var interaction = beat.Directives?.InteractionAction?.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(interaction)) continue;

            if (!StoryboardInteractionAction.All.Contains(interaction))
            {
                errors.Add($"storyboard beat {i + 1} interaction_action '{interaction}' is not supported");
                continue;
            }

            if (!string.Equals(interaction, StoryboardInteractionAction.Slap,
                    StringComparison.OrdinalIgnoreCase))
                continue;

            if (!needsTwo || i != dialogue.Count - 1 ||
                !string.Equals(beat.StoryRole, StoryboardRole.Payoff, StringComparison.OrdinalIgnoreCase))
                errors.Add("interaction_action 'slap' is only allowed on a two-cat final payoff");

            var earnedArc = dialogue.Take(i).Any(previous =>
                string.Equals(previous.StoryRole, StoryboardRole.Escalation,
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(previous.StoryRole, StoryboardRole.TimeChange,
                    StringComparison.OrdinalIgnoreCase));
            if (!earnedArc || !InteractionIsEarned(topic, dialogue))
                errors.Add("interaction_action 'slap' is not earned by this story's setup and escalation");
        }
    }

    private static bool InteractionIsEarned(Topic topic, IReadOnlyList<DialogueLine> dialogue)
    {
        var story = string.Join(' ',
            new[] { topic.TopicText }.Concat(dialogue.Select(beat => beat.Text)))
            .ToLowerInvariant();
        return SlapSemanticHints.Any(story.Contains);
    }
}
