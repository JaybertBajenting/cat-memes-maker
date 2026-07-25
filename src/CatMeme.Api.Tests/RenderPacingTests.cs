using CatMeme.Api.Domain;
using CatMeme.Api.Services;
using Xunit;

namespace CatMeme.Api.Tests;

public sealed class RenderPacingTests
{
    [Fact]
    public void Seven_beat_wake_story_gets_readable_duration_and_visual_holds()
    {
        var topic = WakeStory();

        var recommendedMs = RenderService.RecommendedDurationMs(topic);
        var beats = VisualBeatPlanner.Build(topic, recommendedMs / 1000.0);
        var sleepHolds = beats
            .Where(b => b.ActionA == "sleeping" && b.ActionB == "sleeping")
            .Select(b => b.EndSec - b.StartSec)
            .ToList();

        Assert.InRange(recommendedMs, 18_000, 22_000);
        Assert.Equal(2, sleepHolds.Count);
        Assert.All(sleepHolds, hold => Assert.InRange(hold, 3.5, 4.1));
    }

    [Fact]
    public void Static_script_does_not_override_configured_duration()
    {
        Assert.Equal(0, RenderService.RecommendedDurationMs(new Topic()));
    }

    private static Topic WakeStory() => new()
    {
        Layout = Layout.SideBySide,
        CatAAction = "innocent",
        CatBAction = "angry",
        DialogueJson = DialogueLine.Serialize([
            new DialogueLine("b", "Can you wake me up?", "staring", "asking"),
            new DialogueLine("a", "Okay"),
            new DialogueLine("a", "", "sleeping", "sleeping"),
            new DialogueLine("a", "Time to wake up", "talking", "shocked"),
            new DialogueLine("b", "Five more minutes", "staring", "sleeping"),
            new DialogueLine("a", "", "sleeping", "sleeping"),
            new DialogueLine("b", "Why didn't you wake me?", "innocent", "angry")
        ])
    };
}
