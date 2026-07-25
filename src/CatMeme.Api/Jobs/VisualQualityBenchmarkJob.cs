using System.Text.Json;
using CatMeme.Api.Data;
using CatMeme.Api.Domain;
using CatMeme.Api.Services;
using Hangfire;
using Microsoft.EntityFrameworkCore;

namespace CatMeme.Api.Jobs;

public sealed record BenchmarkRunReport(
    int Run, int VideoId, bool Passed, string Status, int DurationMs, string? Detail);

public sealed record BenchmarkTopicReport(
    string Name, string Topic, bool Passed, int PassedRuns, int RequiredRuns,
    IReadOnlyList<BenchmarkRunReport> Runs);

public sealed record BenchmarkSuiteReport(
    DateTime StartedUtc, DateTime FinishedUtc, bool Passed,
    int PassedTopics, int TotalTopics, IReadOnlyList<BenchmarkTopicReport> Topics);

/// <summary>
/// Renders every reference benchmark <see cref="VisualQualityBenchmarkRunner.RequiredIndependentRuns"/>
/// times through the REAL pipeline and records whether each run survived visual QA.
///
/// The existing <see cref="VisualQualityBenchmarkRunner"/> proves the reviewer is
/// deterministic across fixtures that already exist; it never renders. This job answers the
/// different, harder question the quality standard actually depends on: can the pipeline
/// produce an acceptable reel for a known-good storyboard, repeatably, from scratch. A single
/// lucky render cannot pass - every run is a separate topic, video id and output file.
/// </summary>
public class VisualQualityBenchmarkJob(
    CatMemeDbContext db,
    BackgroundAssetService backgrounds,
    VideoEditJob editor,
    PipelinePaths paths,
    ILogger<VisualQualityBenchmarkJob> log)
{
    public static string ReportPath(PipelinePaths paths) =>
        Path.Combine(paths.Work, "benchmarks", "visual-quality-latest.json");

    [AutomaticRetry(Attempts = 0)]   // a benchmark that fails must report, not silently retry
    public async Task RunAsync(CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        var settings = await SettingsAccessor.GetAsync(db, ct);
        var definitions = VisualQualityReferenceBenchmarks.Create();
        var topics = new List<BenchmarkTopicReport>(definitions.Count);

        log.LogInformation("Visual quality benchmark: {N} topics x {R} runs",
            definitions.Count, VisualQualityBenchmarkRunner.RequiredIndependentRuns);

        foreach (var def in definitions)
        {
            var runs = new List<BenchmarkRunReport>();
            for (var run = 1; run <= VisualQualityBenchmarkRunner.RequiredIndependentRuns; run++)
            {
                ct.ThrowIfCancellationRequested();
                runs.Add(await RenderOnceAsync(def, run, settings, ct));
            }

            var passedRuns = runs.Count(r => r.Passed);
            topics.Add(new BenchmarkTopicReport(
                def.Name,
                def.Topic.TopicText,
                passedRuns == runs.Count,
                passedRuns,
                runs.Count,
                runs));

            log.LogInformation("Benchmark '{Name}': {Passed}/{Total} runs passed",
                def.Name, passedRuns, runs.Count);
        }

        var report = new BenchmarkSuiteReport(
            started,
            DateTime.UtcNow,
            topics.Count > 0 && topics.All(t => t.Passed),
            topics.Count(t => t.Passed),
            topics.Count,
            topics);

        var path = ReportPath(paths);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), ct);

        log.LogInformation("Visual quality benchmark finished: passed={Passed} ({P}/{T} topics)",
            report.Passed, report.PassedTopics, report.TotalTopics);
    }

    /// <summary>
    /// One independent render. Each run gets its own topic row, so nothing is shared between
    /// runs except the authored storyboard itself.
    /// </summary>
    private async Task<BenchmarkRunReport> RenderOnceAsync(
        VisualQualityBenchmarkDefinition def, int run, AppSettings settings, CancellationToken ct)
    {
        var source = def.Topic;
        var topic = new Topic
        {
            TopicText = source.TopicText,
            CatALabel = source.CatALabel,
            CatBLabel = source.CatBLabel,
            CatAPosition = source.CatAPosition,
            CatBPosition = source.CatBPosition,
            CatAAction = source.CatAAction,
            CatBAction = source.CatBAction,
            CaptionTop = source.CaptionTop,
            CaptionBottom = source.CaptionBottom,
            Mood = source.Mood,
            Layout = source.Layout,
            DialogueJson = source.DialogueJson,
            BatchId = $"bench-{def.Name}",
            Status = TopicStatus.Pending,
        };
        db.Topics.Add(topic);
        await db.SaveChangesAsync(ct);

        var video = new Video
        {
            TopicId = topic.Id,
            BackgroundPath = await backgrounds.GetBackgroundAsync(settings, topic, ct),
            Status = VideoStatus.Rendering,
            BatchId = topic.BatchId,
        };
        db.Videos.Add(video);
        await db.SaveChangesAsync(ct);

        try
        {
            await editor.RunAsync(video.Id, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            log.LogWarning("Benchmark '{Name}' run {Run} failed: {Message}", def.Name, run, ex.Message);
        }

        // Re-read: the editor owns the final status and message.
        var final = await db.Videos.AsNoTracking().FirstOrDefaultAsync(v => v.Id == video.Id, ct);
        var ok = final?.Status is VideoStatus.AwaitingApproval or VideoStatus.Ready;

        return new BenchmarkRunReport(
            run,
            video.Id,
            ok,
            final?.Status ?? "missing",
            final?.DurationMs ?? 0,
            string.IsNullOrWhiteSpace(final?.ErrorMessage) ? null : final!.ErrorMessage);
    }
}
