using System.Text.Json;
using CatMeme.Api.Domain;
using CatMeme.Api.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CatMeme.Api.Tests;

public sealed class StoryboardQualityTests
{
    [Fact]
    public void Strong_payoff_requires_a_visible_payoff_title_but_accepts_the_paired_slap()
    {
        var hiddenTitle = new StoryboardDirectives(
            TitleVisible: false,
            TitleStyle: StoryboardTitleStyle.Payoff,
            LabelsVisible: true,
            Lighting: StoryboardLighting.Normal,
            CatScale: StoryboardCatScale.Normal,
            Transition: StoryboardTransition.Cut,
            SoundCue: StoryboardSoundCue.None,
            InteractionAction: StoryboardInteractionAction.None);

        Assert.False(hiddenTitle.IsStrongPayoff);
        Assert.True((hiddenTitle with { TitleVisible = true }).IsStrongPayoff);
        Assert.True((hiddenTitle with
        {
            TitleStyle = StoryboardTitleStyle.Minimal,
            InteractionAction = StoryboardInteractionAction.Slap,
        }).IsStrongPayoff);
    }

    [Fact]
    public void Standard_and_minimal_titles_have_distinct_render_treatments()
    {
        var standard = RenderService.QualityTitlePresentationFor(StoryboardTitleStyle.Standard);
        var minimal = RenderService.QualityTitlePresentationFor(StoryboardTitleStyle.Minimal);
        var standardDecoration = RenderService.TextDecorationFor(false, StoryboardTitleStyle.Standard);
        var minimalDecoration = RenderService.TextDecorationFor(false, StoryboardTitleStyle.Minimal);

        Assert.NotEqual(standard, minimal);
        Assert.True(minimal.FontSize < standard.FontSize);
        Assert.NotEqual(standardDecoration, minimalDecoration);
        Assert.Contains("borderw=5", standardDecoration);
        Assert.Contains("borderw=2", minimalDecoration);
    }

    [Fact]
    public void Legacy_unmarked_dialogue_keeps_the_legacy_contract()
    {
        var topic = new Topic
        {
            TopicText = "pov: when she steals my fries",
            CatAAction = "talking",
            CatBAction = "staring",
            Layout = Layout.SideBySide,
            DialogueJson = DialogueLine.Serialize([
                new DialogueLine("a", "Those were mine"),
                new DialogueLine("b", "Not anymore"),
            ]),
        };

        Assert.True(ScriptValidator.Validate(topic).Ok);
    }

    [Fact]
    public void Default_storyboard_requires_six_to_nine_beats_and_a_complete_arc()
    {
        var valid = WakeStoryboard();
        Assert.True(ScriptValidator.Validate(valid).Ok);

        valid.DialogueJson = DialogueLine.Serialize(valid.Dialogue().Take(5));
        var shortCheck = ScriptValidator.Validate(valid);

        Assert.False(shortCheck.Ok);
        Assert.Contains(shortCheck.Errors, error => error.Contains("expected 6-9"));
        Assert.Contains(shortCheck.Errors, error => error.Contains("end with a payoff"));
    }

    [Fact]
    public void Default_storyboard_rejects_missing_or_unsafe_per_beat_directives()
    {
        var topic = WakeStoryboard();
        var beats = topic.Dialogue().ToList();
        beats[1] = beats[1] with { Directives = null };
        beats[2] = beats[2] with
        {
            Directives = beats[2].Directives! with { Lighting = "neon_filter=evil" },
        };
        topic.DialogueJson = DialogueLine.Serialize(beats);

        var check = ScriptValidator.Validate(topic);

        Assert.False(check.Ok);
        Assert.Contains(check.Errors, error => error.Contains("missing directives"));
        Assert.Contains(check.Errors, error => error.Contains("unsafe"));
    }

    [Fact]
    public void Slap_selector_is_only_valid_for_an_earned_two_cat_final_payoff()
    {
        var early = WakeStoryboard();
        var earlyBeats = early.Dialogue().ToList();
        earlyBeats[3] = earlyBeats[3] with
        {
            Directives = earlyBeats[3].Directives! with
            {
                InteractionAction = StoryboardInteractionAction.Slap,
            },
        };
        early.DialogueJson = DialogueLine.Serialize(earlyBeats);
        Assert.Contains(
            ScriptValidator.Validate(early).Errors,
            error => error.Contains("only allowed on a two-cat final payoff"));

        var unearned = WakeStoryboard();
        unearned.TopicText = "pov: when dinner plans go completely wrong";
        var unearnedBeats = unearned.Dialogue().ToList();
        unearnedBeats[0] = unearnedBeats[0] with { Text = "Can you save my seat?" };
        unearnedBeats[3] = unearnedBeats[3] with { Text = "This got much worse" };
        unearnedBeats[^1] = unearnedBeats[^1] with { Text = "That was your whole plan?" };
        unearned.DialogueJson = DialogueLine.Serialize(unearnedBeats);
        Assert.Contains(
            ScriptValidator.Validate(unearned).Errors,
            error => error.Contains("not earned"));
    }

    [Fact]
    public void Planner_preserves_each_authored_beat_and_safe_directives()
    {
        var topic = WakeStoryboard();

        var plan = VisualBeatPlanner.Build(topic, 21.0);

        Assert.Equal(6, plan.Count);
        Assert.Equal(StoryboardRole.Setup, plan[0].StoryRole);
        Assert.Equal(StoryboardTitleStyle.TimeCard, plan[2].TitleStyle);
        Assert.False(plan[2].LabelsVisible);
        Assert.Equal(StoryboardLighting.Dark, plan[2].Lighting);
        Assert.Equal(StoryboardTransition.TimeCard, plan[2].Transition);
        Assert.Equal(StoryboardSoundCue.Whoosh, plan[2].SoundCue);
        Assert.Equal(StoryboardInteractionAction.Slap, plan[^1].InteractionAction);
        Assert.Equal(StoryboardQualityProfile.Default.Id, plan[^1].StoryboardProfile);
    }

    [Fact]
    public void Strict_AI_parser_accepts_valid_profile_and_rejects_unsafe_selector()
    {
        var service = Parser();
        var valid = WakeStoryboard();

        var parsed = service.ParseTopics(
            Payload(valid), 1, StoryboardQualityProfile.Default);
        Assert.Single(parsed);
        Assert.True(ScriptValidator.Validate(parsed[0]).Ok);

        var unsafeTopic = WakeStoryboard();
        var beats = unsafeTopic.Dialogue().ToList();
        beats[^1] = beats[^1] with
        {
            Directives = beats[^1].Directives! with { InteractionAction = "asset.mp4" },
        };
        unsafeTopic.DialogueJson = DialogueLine.Serialize(beats);

        Assert.Empty(service.ParseTopics(
            Payload(unsafeTopic), 1, StoryboardQualityProfile.Default));
    }

    [Fact]
    public void Prompt_contract_and_wake_fallback_use_the_default_standard()
    {
        Assert.Contains("6-9 purposeful beats", ClaudeCliService.DefaultStoryboardPromptContract);
        Assert.Contains("interaction_action is none|slap", ClaudeCliService.DefaultStoryboardPromptContract);

        var fallback = ClaudeCliService.FallbackScriptForTopic(
            "when my gf asks me to wake her up at 5AM tomorrow");
        var check = ScriptValidator.Validate(fallback);

        Assert.True(check.Ok, string.Join("; ", check.Errors));
        Assert.InRange(fallback.Dialogue().Count, 6, 9);
        Assert.Equal(
            StoryboardInteractionAction.Slap,
            fallback.Dialogue()[^1].Directives?.InteractionAction);
    }

    private static ClaudeCliService Parser() => new(
        null!,
        null!,
        new ConfigurationManager(),
        NullLogger<ClaudeCliService>.Instance);

    private static string Payload(Topic topic) => JsonSerializer.Serialize(new[]
    {
        new
        {
            topic = topic.TopicText,
            cat_a_label = topic.CatALabel,
            cat_b_label = topic.CatBLabel,
            cat_a_position = topic.CatAPosition,
            cat_b_position = topic.CatBPosition,
            cat_a_action = topic.CatAAction,
            cat_b_action = topic.CatBAction,
            caption_top = topic.CaptionTop,
            caption_bottom = topic.CaptionBottom,
            mood = topic.Mood,
            layout = topic.Layout,
            storyboard_profile = StoryboardQualityProfile.Default.Id,
            dialogue = topic.Dialogue(),
        },
    });

    private static Topic WakeStoryboard()
    {
        var profile = StoryboardQualityProfile.Default.Id;
        return new Topic
        {
            TopicText = "pov: when she asks me to wake her early",
            CatALabel = "Me",
            CatBLabel = "Her",
            CatAAction = "innocent",
            CatBAction = "angry",
            Layout = Layout.SideBySide,
            DialogueJson = DialogueLine.Serialize([
                Beat("b", "Wake me at five", StoryboardRole.Setup,
                    "staring", "asking", titleVisible: true, soundCue: StoryboardSoundCue.Pop),
                Beat("a", "Okay", StoryboardRole.Reaction,
                    catScale: StoryboardCatScale.CloseA),
                Beat("a", "5:00 AM", StoryboardRole.TimeChange,
                    "sleeping", "sleeping", titleVisible: false,
                    titleStyle: StoryboardTitleStyle.TimeCard, labelsVisible: false,
                    lighting: StoryboardLighting.Dark, catScale: StoryboardCatScale.Wide,
                    transition: StoryboardTransition.TimeCard, soundCue: StoryboardSoundCue.Whoosh),
                Beat("a", "Time to get up", StoryboardRole.Escalation,
                    "talking", "shocked", lighting: StoryboardLighting.Flash,
                    catScale: StoryboardCatScale.CloseA,
                    transition: StoryboardTransition.Flash, soundCue: StoryboardSoundCue.Alarm),
                Beat("b", "Five more minutes", StoryboardRole.Reaction,
                    "staring", "sleeping", lighting: StoryboardLighting.Dim,
                    catScale: StoryboardCatScale.CloseB),
                Beat("b", "Why did you let me sleep?", StoryboardRole.Payoff,
                    "innocent", "angry", titleVisible: false,
                    titleStyle: StoryboardTitleStyle.Payoff,
                    catScale: StoryboardCatScale.CloseB,
                    transition: StoryboardTransition.SmashCut,
                    soundCue: StoryboardSoundCue.Impact,
                    interactionAction: StoryboardInteractionAction.Slap),
            ]),
        };

        DialogueLine Beat(
            string speaker,
            string text,
            string role,
            string? actionA = null,
            string? actionB = null,
            string? stage = null,
            bool titleVisible = false,
            string titleStyle = StoryboardTitleStyle.Minimal,
            bool labelsVisible = true,
            string lighting = StoryboardLighting.Normal,
            string catScale = StoryboardCatScale.Normal,
            string transition = StoryboardTransition.Cut,
            string soundCue = StoryboardSoundCue.None,
            string interactionAction = StoryboardInteractionAction.None) =>
            new(
                speaker,
                text,
                actionA,
                actionB,
                stage,
                role,
                new StoryboardDirectives(
                    titleVisible,
                    titleStyle,
                    labelsVisible,
                    lighting,
                    catScale,
                    transition,
                    soundCue,
                    interactionAction),
                profile);
    }
}
