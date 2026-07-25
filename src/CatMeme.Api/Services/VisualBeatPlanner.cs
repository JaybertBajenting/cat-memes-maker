using CatMeme.Api.Domain;

namespace CatMeme.Api.Services;

/// <summary>
/// One derived visual window. Dialogue timing remains automatic; the script only declares
/// state changes, so changing the target reel length cannot invalidate authored timestamps.
/// </summary>
public record VisualBeatDraft(
    double StartSec,
    double EndSec,
    string ActionA,
    string? ActionB,
    bool ShowA,
    bool ShowB,
    string? Stage,
    string? StoryRole = null,
    bool? TitleVisible = null,
    string? TitleStyle = null,
    bool? LabelsVisible = null,
    string? Lighting = null,
    string? CatScale = null,
    string? Transition = null,
    string? SoundCue = null,
    string? StoryboardProfile = null,
    string? InteractionAction = null);

/// <summary>
/// Turns optional visual directives on dialogue lines into a continuous stage plan.
/// Scripts without directives return an empty plan and retain the original one-pair render.
/// </summary>
public static class VisualBeatPlanner
{
    public static List<VisualBeatDraft> Build(Topic topic, double durationSec)
    {
        var dialogue = topic.Dialogue();
        if (dialogue.Count == 0 || !dialogue.Any(d => d.HasVisualDirective)) return [];

        var scheduled = RenderService.ScheduleDialogue(dialogue, durationSec);
        if (scheduled.Count == 0) return [];

        var actionA = Normalize(topic.CatAAction) ?? "";
        var actionB = topic.Layout == Layout.SinglePov ? null : Normalize(topic.CatBAction);
        var hasB = topic.Layout != Layout.SinglePov;
        var result = new List<VisualBeatDraft>();

        for (var i = 0; i < scheduled.Count; i++)
        {
            var line = scheduled[i].Line;
            actionA = Normalize(line.CatAAction) ?? actionA;
            if (hasB) actionB = Normalize(line.CatBAction) ?? actionB;

            var stage = Normalize(line.Stage);
            var (showA, showB) = Visibility(stage, hasB);
            var directives = line.Directives;

            // Visuals span the small dialogue gaps too. Otherwise both cats flash out for
            // 450ms between every text turn, which reads as a broken key rather than pacing.
            var start = i == 0 ? 0 : scheduled[i].Start;
            var end = i + 1 < scheduled.Count ? scheduled[i + 1].Start : durationSec;
            if (end <= start) continue;

            var beat = new VisualBeatDraft(
                start,
                end,
                actionA,
                actionB,
                showA,
                showB,
                stage,
                Normalize(line.StoryRole),
                directives?.TitleVisible,
                Normalize(directives?.TitleStyle),
                directives?.LabelsVisible,
                Normalize(directives?.Lighting),
                Normalize(directives?.CatScale),
                Normalize(directives?.Transition),
                Normalize(directives?.SoundCue),
                Normalize(line.StoryboardProfile),
                Normalize(directives?.InteractionAction));

            // Adjacent turns with no visual change share one input window. This keeps the
            // ffmpeg graph small for dialogue-heavy scripts while text still changes per turn.
            var authoredStoryboardBeat = directives is not null ||
                                         !string.IsNullOrWhiteSpace(line.StoryRole) ||
                                         !string.IsNullOrWhiteSpace(line.StoryboardProfile);
            if (!authoredStoryboardBeat && result.Count > 0 && SameVisual(result[^1], beat))
                result[^1] = result[^1] with { EndSec = beat.EndSec };
            else
                result.Add(beat);
        }

        return result;
    }

    private static (bool ShowA, bool ShowB) Visibility(string? stage, bool hasB) => stage switch
    {
        DialogueStage.HideBoth or DialogueStage.Transition or DialogueStage.SleepPause => (false, false),
        DialogueStage.HideA or DialogueStage.ShowB => (false, hasB),
        DialogueStage.HideB or DialogueStage.ShowA => (true, false),
        _ => (true, hasB),
    };

    private static bool SameVisual(VisualBeatDraft a, VisualBeatDraft b) =>
        a.ShowA == b.ShowA &&
        a.ShowB == b.ShowB &&
        string.Equals(a.ActionA, b.ActionA, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(a.ActionB, b.ActionB, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(a.Stage, b.Stage, StringComparison.OrdinalIgnoreCase);

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();
}
