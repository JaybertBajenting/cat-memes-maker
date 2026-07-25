using CatMeme.Api.Domain;
using CatMeme.Api.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace CatMeme.Api.Tests;

public sealed class VisualQualityReviewTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(), $"catmeme-visual-qa-{Guid.NewGuid():N}");

    [Fact]
    public async Task Reference_style_suite_is_repeatable_across_three_offline_runs()
    {
        var definitions = VisualQualityReferenceBenchmarks.Create();
        var scenarios = new Dictionary<string, SyntheticScenario>(StringComparer.OrdinalIgnoreCase);
        var cases = new List<VisualQualityBenchmarkCase>();
        var nextVideoId = 1;

        foreach (var definition in definitions)
        {
            var script = ScriptValidator.Validate(definition.Topic);
            Assert.True(script.Ok, $"{definition.Name}: {string.Join("; ", script.Errors)}");
            var durationMs = Math.Max(13_000, RenderService.RecommendedDurationMs(definition.Topic));
            var requests = new List<VisualQualityReviewRequest>();
            for (var run = 1; run <= VisualQualityBenchmarkRunner.RequiredIndependentRuns; run++)
            {
                var fixture = Fixture(
                    $"{definition.Name}-render-{run}", definition.Topic, durationMs, nextVideoId++);
                scenarios[fixture.Request.VideoPath] = new SyntheticScenario(
                    definition.Topic, durationMs, BrokenTransition: false);
                requests.Add(fixture.Request);
            }
            cases.Add(new VisualQualityBenchmarkCase(definition, requests));
        }

        var service = Reviewer(new SyntheticFrameSampler(scenarios));
        var result = await new VisualQualityBenchmarkRunner(service).RunAsync(cases);

        Assert.True(result.Passed);
        Assert.Equal(3, result.Benchmarks.Count);
        Assert.All(result.Benchmarks, benchmark =>
        {
            Assert.True(benchmark.Passed);
            Assert.True(benchmark.Repeatable);
            Assert.Equal(3, benchmark.Runs.Count);
            Assert.Equal(3, benchmark.Runs.Select(r => r.VideoId).Distinct().Count());
            Assert.Equal(3, benchmark.Runs.Select(r => r.VideoPath).Distinct().Count());
            Assert.Single(benchmark.Runs.Select(r => r.Signature).Distinct());
        });
    }

    [Fact]
    public async Task Repeatability_runner_rejects_reused_video_ids_before_reviewing()
    {
        var definition = VisualQualityReferenceBenchmarks.Create()[0];
        var durationMs = RenderService.RecommendedDurationMs(definition.Topic);
        var first = Fixture("same-id-1", definition.Topic, durationMs, videoId: 77).Request;
        var requests = new[]
        {
            first,
            first with { VideoPath = "same-id-2.mp4" },
            first with { VideoPath = "same-id-3.mp4" },
        };
        var runner = new VisualQualityBenchmarkRunner(Reviewer(
            new SyntheticFrameSampler(new Dictionary<string, SyntheticScenario>())));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.RunAsync([new VisualQualityBenchmarkCase(definition, requests)]));

        Assert.Contains("reused a video id", error.Message);
    }

    [Fact]
    public async Task Repeatability_runner_rejects_reused_canonical_video_paths_before_reviewing()
    {
        var definition = VisualQualityReferenceBenchmarks.Create()[0];
        var durationMs = RenderService.RecommendedDurationMs(definition.Topic);
        var first = Fixture("same-path", definition.Topic, durationMs, videoId: 81).Request;
        var requests = new[]
        {
            first,
            first with { VideoId = 82, VideoPath = $".\\{first.VideoPath}" },
            first with { VideoId = 83 },
        };
        var runner = new VisualQualityBenchmarkRunner(Reviewer(
            new SyntheticFrameSampler(new Dictionary<string, SyntheticScenario>())));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.RunAsync([new VisualQualityBenchmarkCase(definition, requests)]));

        Assert.Contains("reused a video path", error.Message);
    }

    [Fact]
    public async Task Wrong_action_metadata_fails_and_returns_the_recastable_clip()
    {
        var definition = VisualQualityReferenceBenchmarks.Create()[0];
        var durationMs = RenderService.RecommendedDurationMs(definition.Topic);
        var fixture = Fixture("wrong-action", definition.Topic, durationMs);
        var wrongId = fixture.Request.RenderedBeats.First(b => b.CatA is not null).CatA!.ClipId;
        var clips = fixture.Request.Clips.ToDictionary(x => x.Key, x => x.Value);
        clips[wrongId] = clips[wrongId] with { MoodTags = "gagging" };
        var request = fixture.Request with { Clips = clips };
        var scenarios = new Dictionary<string, SyntheticScenario>
        {
            [request.VideoPath] = new(definition.Topic, durationMs, BrokenTransition: false),
        };

        var result = await Reviewer(new SyntheticFrameSampler(scenarios)).ReviewAsync(request);

        Assert.False(result.Passed);
        Assert.Contains(wrongId, result.RecastClipIds);
        Assert.Contains(result.Issues, i => i.Code == "action_mismatch" && i.ClipId == wrongId);
    }

    [Fact]
    public async Task Partial_transition_fails_as_storyboard_error_without_recasting_cats()
    {
        var definition = VisualQualityReferenceBenchmarks.Create()
            .Single(x => x.Name == "midnight-snack-transition");
        var durationMs = RenderService.RecommendedDurationMs(definition.Topic);
        var fixture = Fixture("bad-transition", definition.Topic, durationMs);
        var scenarios = new Dictionary<string, SyntheticScenario>
        {
            [fixture.Request.VideoPath] = new(definition.Topic, durationMs, BrokenTransition: true),
        };

        var result = await Reviewer(new SyntheticFrameSampler(scenarios))
            .ReviewAsync(fixture.Request);

        Assert.False(result.Passed);
        Assert.Empty(result.RecastClipIds);
        Assert.Contains(result.Issues, i => i.Code == "transition_not_full_frame");
    }

    [Fact]
    public async Task Secondary_reviewer_is_inert_until_explicitly_enabled()
    {
        var definition = VisualQualityReferenceBenchmarks.Create()[0];
        var durationMs = RenderService.RecommendedDurationMs(definition.Topic);
        var fixture = Fixture("secondary-opt-in", definition.Topic, durationMs);
        var suspect = fixture.Request.Clips.Keys.First();
        var scenarios = new Dictionary<string, SyntheticScenario>
        {
            [fixture.Request.VideoPath] = new(definition.Topic, durationMs, BrokenTransition: false),
        };
        var secondary = new RejectingSecondaryReviewer(suspect);
        var reviewer = Reviewer(new SyntheticFrameSampler(scenarios), [secondary]);

        var primaryOnly = await reviewer.ReviewAsync(fixture.Request);
        var optedIn = await reviewer.ReviewAsync(fixture.Request with
        {
            EnableSecondaryReviewer = true,
        });

        Assert.True(primaryOnly.Passed);
        Assert.Equal(1, secondary.Calls);
        Assert.False(optedIn.Passed);
        Assert.Contains(suspect, optedIn.RecastClipIds);
    }

    private (VisualQualityReviewRequest Request, IReadOnlyDictionary<int, string> Actions) Fixture(
        string name,
        Topic topic,
        int durationMs,
        int videoId = 1)
    {
        var drafts = VisualBeatPlanner.Build(topic, durationMs / 1000.0);
        var nextId = 10;
        var identities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var actions = new Dictionary<int, string>();
        var evidence = new Dictionary<int, VisualQualityClipEvidence>();

        RenderCatInput Cat(string role, string action)
        {
            var key = $"{role}:{action}";
            if (!identities.TryGetValue(key, out var id))
            {
                id = nextId++;
                identities[key] = id;
                actions[id] = action;
                evidence[id] = new VisualQualityClipEvidence(
                    id, action, "neutral", true, false, 0.01, 0.52);
            }
            return new RenderCatInput(
                id,
                $"clip-{id}.mp4",
                new ClipAnalysis("0x00FF00", 0.12, null, 420, 620, 5_000, true, 0.52));
        }

        var rendered = drafts.Select(d => new RenderVisualBeat(
            d.StartSec,
            d.EndSec,
            d.InteractionAction == StoryboardInteractionAction.Slap || !d.ShowA
                ? null
                : Cat("a", d.ActionA),
            d.InteractionAction == StoryboardInteractionAction.Slap || !d.ShowB || d.ActionB is null
                ? null
                : Cat("b", d.ActionB),
            d.Stage,
            d.StoryRole,
            d.TitleVisible,
            d.TitleStyle,
            d.LabelsVisible,
            d.Lighting,
            d.CatScale,
            d.Transition,
            d.SoundCue,
            d.StoryboardProfile,
            d.InteractionAction,
            d.InteractionAction == StoryboardInteractionAction.Slap
                ? new RenderInteractionInput(
                    1,
                    "paired-slap.mp4",
                    new ClipAnalysis("0x00FF00", 0.12, null, 620, 644, 5_100))
                : null)).ToList();
        var artifactDirectory = Path.Combine(root, name);
        var request = new VisualQualityReviewRequest(
            videoId,
            $"{name}.mp4",
            $"{name}-background.png",
            topic,
            durationMs,
            rendered,
            evidence,
            artifactDirectory,
            EnableSecondaryReviewer: false);
        return (request, actions);
    }

    private VisualQualityReviewService Reviewer(
        IVisualFrameSampler sampler,
        IEnumerable<IVisualQualitySecondaryReviewer>? secondary = null)
    {
        var pipeline = Path.Combine(root, "pipeline");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["PIPELINE_BASE_PATH"] = pipeline,
        }).Build();
        var paths = new PipelinePaths(config);
        return new VisualQualityReviewService(
            sampler,
            paths,
            secondary ?? [],
            NullLogger<VisualQualityReviewService>.Instance);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        catch { /* test cleanup is best effort */ }
    }

    private sealed record SyntheticScenario(Topic Topic, int DurationMs, bool BrokenTransition);

    private sealed class RejectingSecondaryReviewer(int suspectClipId)
        : IVisualQualitySecondaryReviewer
    {
        public int Calls { get; private set; }

        public Task<VisualQualitySecondaryResult> ReviewAsync(
            VisualQualitySecondaryContext context,
            CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(new VisualQualitySecondaryResult(
                "fake-local-cli", false, 0.95, "Obvious pose mismatch.", [suspectClipId]));
        }
    }

    private sealed class SyntheticFrameSampler(
        IReadOnlyDictionary<string, SyntheticScenario> scenarios) : IVisualFrameSampler
    {
        public async Task<VisualSampleSet> SampleAsync(
            string videoPath,
            string backgroundPath,
            IReadOnlyList<VisualFrameSampleRequest> samples,
            string artifactDirectory,
            CancellationToken ct = default)
        {
            var scenario = scenarios[videoPath];
            var beats = VisualBeatPlanner.Build(scenario.Topic, scenario.DurationMs / 1000.0);
            Directory.CreateDirectory(artifactDirectory);

            var backgroundFrame = Path.Combine(artifactDirectory, "background.png");
            using (var background = new Image<Rgb24>(540, 960, new Rgb24(220, 210, 190)))
                await background.SaveAsPngAsync(backgroundFrame, ct);

            var artifacts = new List<VisualFrameArtifact>();
            foreach (var sample in samples)
            {
                var beat = beats[sample.BeatIndex];
                using var frame = new Image<Rgb24>(540, 960, new Rgb24(220, 210, 190));
                if (beat.Stage == DialogueStage.Transition ||
                    beat.Transition == StoryboardTransition.TimeCard ||
                    beat.TitleStyle == StoryboardTitleStyle.TimeCard)
                {
                    if (scenario.BrokenTransition)
                        Fill(frame, 80, 170, 380, 620, new Rgb24(145, 28, 24));
                    else
                        Fill(frame, 0, 0, frame.Width, frame.Height, new Rgb24(145, 28, 24));
                }
                else if (beat.InteractionAction == StoryboardInteractionAction.Slap)
                {
                    Fill(frame, 55, 445, 430, 420, new Rgb24(92, 58, 42));
                }
                else
                {
                    if (beat.ShowA) Fill(frame, 60, 430, 170, 260, new Rgb24(72, 55, 40));
                    if (beat.ShowB) Fill(frame, 310, 430, 170, 260, new Rgb24(55, 62, 72));
                }

                var path = Path.Combine(artifactDirectory,
                    $"beat-{sample.BeatIndex:D2}-{sample.SampleIndex:D2}.png");
                await frame.SaveAsPngAsync(path, ct);
                artifacts.Add(new VisualFrameArtifact(
                    sample.BeatIndex, sample.SampleIndex, sample.AtSec, path));
            }

            var contact = Path.Combine(artifactDirectory, "contact-sheet.jpg");
            using (var first = await Image.LoadAsync<Rgb24>(artifacts[0].Path, ct))
                await first.SaveAsJpegAsync(contact, ct);
            return new VisualSampleSet(
                new MediaInfo(scenario.DurationMs, 1080, 1920, HasAudio: true),
                backgroundFrame,
                contact,
                artifacts);
        }

        private static void Fill(Image<Rgb24> image, int x, int y, int width, int height, Rgb24 color)
        {
            var x1 = Math.Min(image.Width, x + width);
            var y1 = Math.Min(image.Height, y + height);
            for (var py = Math.Max(0, y); py < y1; py++)
            for (var px = Math.Max(0, x); px < x1; px++)
                image[px, py] = color;
        }
    }
}
