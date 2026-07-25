using System.Text;
using CatMeme.Api.Domain;

namespace CatMeme.Api.Services;

public sealed record VisualQualityBenchmarkDefinition(
    string Name,
    Topic Topic,
    int Runs = 3,
    double MinimumScore = 0.70);

public sealed record VisualQualityBenchmarkCase(
    VisualQualityBenchmarkDefinition Definition,
    IReadOnlyList<VisualQualityReviewRequest> Requests);

public sealed record VisualQualityBenchmarkRun(
    int Run,
    bool Passed,
    double Score,
    string Signature,
    int VideoId,
    string VideoPath);

public sealed record VisualQualityBenchmarkResult(
    string Name,
    bool Passed,
    bool Repeatable,
    double AverageScore,
    IReadOnlyList<VisualQualityBenchmarkRun> Runs);

public sealed record VisualQualityBenchmarkSuiteResult(
    bool Passed,
    IReadOnlyList<VisualQualityBenchmarkResult> Benchmarks);

/// <summary>
/// Reviews three independently rendered, local fixtures for each definition and compares
/// their evidence signatures. A single output can never masquerade as a 3x repeatability
/// run: every request must have a distinct positive video id and canonical video path.
/// The runner never generates a topic, downloads media, or enables a secondary AI reviewer,
/// so the benchmark itself remains network-free and isolates render-to-render QA stability.
/// </summary>
public sealed class VisualQualityBenchmarkRunner(VisualQualityReviewService reviewer)
{
    public const int RequiredIndependentRuns = 3;

    public async Task<VisualQualityBenchmarkSuiteResult> RunAsync(
        IReadOnlyList<VisualQualityBenchmarkCase> cases,
        CancellationToken ct = default)
    {
        var results = new List<VisualQualityBenchmarkResult>(cases.Count);
        foreach (var benchmark in cases)
        {
            ValidateIndependentRequests(benchmark);

            var runs = new List<VisualQualityBenchmarkRun>(RequiredIndependentRuns);
            for (var index = 0; index < benchmark.Requests.Count; index++)
            {
                ct.ThrowIfCancellationRequested();
                var request = benchmark.Requests[index];
                var result = await reviewer.ReviewAsync(request, ct);
                runs.Add(new VisualQualityBenchmarkRun(
                    index + 1,
                    result.Passed,
                    result.Score,
                    Signature(result),
                    request.VideoId,
                    request.VideoPath));
            }

            var repeatable = runs.Select(r => r.Signature).Distinct(StringComparer.Ordinal).Count() == 1;
            var average = Math.Round(runs.Average(r => r.Score), 4, MidpointRounding.AwayFromZero);
            var passed = repeatable && runs.All(r => r.Passed) &&
                         average >= benchmark.Definition.MinimumScore;
            results.Add(new VisualQualityBenchmarkResult(
                benchmark.Definition.Name, passed, repeatable, average, runs));
        }

        return new VisualQualityBenchmarkSuiteResult(
            results.Count > 0 && results.All(r => r.Passed), results);
    }

    internal static void ValidateIndependentRequests(VisualQualityBenchmarkCase benchmark)
    {
        if (benchmark.Definition.Runs != RequiredIndependentRuns)
            throw new ArgumentOutOfRangeException(
                nameof(benchmark),
                $"Repeatability definitions must require exactly {RequiredIndependentRuns} independent renders.");

        if (benchmark.Requests.Count != RequiredIndependentRuns)
            throw new InvalidOperationException(
                $"Benchmark '{benchmark.Definition.Name}' requires exactly " +
                $"{RequiredIndependentRuns} independently rendered requests; received {benchmark.Requests.Count}.");

        if (benchmark.Requests.Any(request => request.VideoId <= 0))
            throw new InvalidOperationException(
                $"Benchmark '{benchmark.Definition.Name}' requires positive video ids.");

        if (benchmark.Requests.Select(request => request.VideoId).Distinct().Count() !=
            RequiredIndependentRuns)
            throw new InvalidOperationException(
                $"Benchmark '{benchmark.Definition.Name}' reused a video id; every run must be a separate render.");

        var paths = benchmark.Requests.Select(request => CanonicalVideoPath(
            request.VideoPath, benchmark.Definition.Name)).ToList();
        if (paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != RequiredIndependentRuns)
            throw new InvalidOperationException(
                $"Benchmark '{benchmark.Definition.Name}' reused a video path; every run must be a separate render.");

        if (benchmark.Requests.Any(request => request.EnableSecondaryReviewer))
            throw new InvalidOperationException(
                "Repeatability benchmarks cannot enable the optional AI secondary reviewer.");
    }

    private static string CanonicalVideoPath(string path, string benchmarkName)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException(
                $"Benchmark '{benchmarkName}' contains an empty video path.");

        return Path.GetFullPath(path.Trim())
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    internal static string Signature(VisualQualityReviewResult result)
    {
        var value = new StringBuilder()
            .Append(result.Passed).Append('|')
            .Append(result.Score.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture))
            .Append("|recast=").AppendJoin(',', result.RecastClipIds);
        foreach (var beat in result.Beats)
        {
            value.Append("|b").Append(beat.BeatIndex).Append('=')
                .Append(beat.Passed).Append(':')
                .Append(beat.Score.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture))
                .Append(':').AppendJoin(',', beat.Issues.Select(i => i.Code).OrderBy(x => x));
        }
        return value.ToString();
    }
}

/// <summary>Reference-style story shapes kept stable as an offline regression corpus.</summary>
public static class VisualQualityReferenceBenchmarks
{
    public static IReadOnlyList<VisualQualityBenchmarkDefinition> Create() =>
    [
        new("wake-request-seven-beat", WakeRequest()),
        new("late-reply-reaction-arc", LateReply()),
        new("midnight-snack-transition", MidnightSnack()),
    ];

    private static Topic WakeRequest() => ClaudeCliService.FallbackScriptForTopic(
        "POV: when my gf asks me to wake her up at 5AM tomorrow");

    private static Topic LateReply() => new()
    {
        TopicText = "POV: when she replies four hours later like nothing happened",
        CatALabel = "Me",
        CatBLabel = "Her",
        CatAAction = "staring",
        CatBAction = "innocent",
        Layout = Layout.SideBySide,
        Mood = "dramatic",
        DialogueJson = DialogueLine.Serialize([
            Beat("a", "You disappeared for four hours", StoryboardRole.Setup,
                "staring", "innocent", sound: StoryboardSoundCue.Pop),
            Beat("b", "I was busy", StoryboardRole.Reaction,
                scale: StoryboardCatScale.CloseB),
            Beat("a", "", StoryboardRole.Escalation,
                "angry", "innocent", sound: StoryboardSoundCue.RecordScratch),
            Beat("b", "Why are you looking at me like that?", StoryboardRole.Reaction,
                "angry", "asking", scale: StoryboardCatScale.CloseB),
            Beat("a", "No reason", StoryboardRole.Escalation,
                scale: StoryboardCatScale.CloseA),
            Beat("b", "Right. Totally normal.", StoryboardRole.Payoff,
                "staring", "shocked", titleVisible: false,
                titleStyle: StoryboardTitleStyle.Payoff,
                transition: StoryboardTransition.SmashCut,
                sound: StoryboardSoundCue.Impact),
        ]),
    };

    private static Topic MidnightSnack() => new()
    {
        TopicText = "POV: when I hear the snack bag open at midnight",
        CatALabel = "Me",
        CatBLabel = "Her",
        CatAAction = "sleeping",
        CatBAction = "eating",
        Layout = Layout.SideBySide,
        Mood = "funny",
        DialogueJson = DialogueLine.Serialize([
            Beat("a", "", StoryboardRole.Setup,
                "sleeping", "eating", lighting: StoryboardLighting.Dark),
            Beat("b", "I thought you were asleep", StoryboardRole.Reaction,
                lighting: StoryboardLighting.Dim, scale: StoryboardCatScale.CloseB),
            Beat("a", "I heard snacks", StoryboardRole.Escalation,
                "shocked", "eating", transition: StoryboardTransition.Flash,
                sound: StoryboardSoundCue.Pop),
            Beat("a", "Two seconds later", StoryboardRole.TimeChange,
                stage: DialogueStage.Transition, titleVisible: false,
                titleStyle: StoryboardTitleStyle.TimeCard,
                transition: StoryboardTransition.TimeCard,
                sound: StoryboardSoundCue.Whoosh),
            Beat("a", "Save some for me", StoryboardRole.Reaction,
                "asking", "eating", scale: StoryboardCatScale.CloseA),
            Beat("b", "You ate all of them", StoryboardRole.Payoff,
                "innocent", "angry", titleVisible: false,
                titleStyle: StoryboardTitleStyle.Payoff,
                transition: StoryboardTransition.SmashCut,
                sound: StoryboardSoundCue.Impact),
        ]),
    };

    private static DialogueLine Beat(
        string speaker,
        string text,
        string role,
        string? actionA = null,
        string? actionB = null,
        string? stage = null,
        bool titleVisible = true,
        string titleStyle = StoryboardTitleStyle.Minimal,
        string lighting = StoryboardLighting.Normal,
        string scale = StoryboardCatScale.Normal,
        string transition = StoryboardTransition.Cut,
        string sound = StoryboardSoundCue.None) => new(
            speaker,
            text,
            actionA,
            actionB,
            stage,
            role,
            new StoryboardDirectives(
                titleVisible,
                titleStyle,
                // The reference reel keeps "her" / "me" on screen for its whole runtime.
                LabelsVisible: true,
                lighting,
                scale,
                transition,
                sound,
                StoryboardInteractionAction.None),
            StoryboardQualityProfile.Default.Id);
}
