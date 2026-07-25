using CatMeme.Api.Data;
using CatMeme.Api.Domain;
using CatMeme.Api.Jobs;
using Hangfire;
using Hangfire.Common;
using Hangfire.Server;
using Hangfire.States;
using Hangfire.Storage;
using Microsoft.EntityFrameworkCore;

namespace CatMeme.Api.Services;

/// <summary>Central retry-state policy shared by the API and focused tests.</summary>
public static class VideoRetryPolicy
{
    public static (bool Allowed, string Message) Check(string status) => status switch
    {
        VideoStatus.Failed or VideoStatus.Cancelled => (true, ""),
        VideoStatus.Rendering => (false, "Video is already queued or rendering."),
        VideoStatus.AwaitingApproval or VideoStatus.Ready or VideoStatus.Posted =>
            (false, $"Video already completed with status '{status}'."),
        VideoStatus.Rejected => (false, "Video was intentionally rejected and cannot be retried."),
        _ => (false, $"Video in status '{status}' cannot be retried."),
    };
}

/// <summary>
/// Routes every transition of an edit job into the render queue. State election is used
/// rather than only changing the initial enqueue so Hangfire's automatic retries stay on
/// the one-worker render queue as well.
/// </summary>
public sealed class VideoRenderQueueFilter : JobFilterAttribute, IElectStateFilter
{
    public const string RenderQueue = "video-render";

    public static bool Routes(Type jobType) => jobType == typeof(VideoEditJob);

    public void OnStateElection(ElectStateContext context)
    {
        if (Routes(context.BackgroundJob.Job.Type) &&
            context.CandidateState is EnqueuedState { Queue: not RenderQueue })
        {
            context.CandidateState = new EnqueuedState(RenderQueue);
        }
    }
}

/// <summary>
/// Last line of defence against duplicate workers writing the same video_N.mp4.
///
/// The render queue serialises ordinary FFmpeg load in one process. This distributed lock
/// additionally protects the same video across duplicate Hangfire deliveries or accidental
/// second app instances. Once the lease is held, stale job ids and terminal video states are
/// cancelled before <see cref="VideoEditJob.RunAsync"/> is invoked.
/// </summary>
public sealed class VideoRenderGuard(
    IServiceScopeFactory scopes,
    ILogger<VideoRenderGuard> log) : IServerFilter
{
    private const string LeaseItem = "catmeme.video-render-lease";

    public void OnPerforming(PerformingContext context)
    {
        if (!TryGetVideoId(context, out var videoId)) return;

        IDisposable? lease = null;
        try
        {
            try
            {
                lease = context.Connection.AcquireDistributedLock(
                    $"catmeme:video-render:{videoId}", TimeSpan.FromSeconds(1));
            }
            catch (DistributedLockTimeoutException ex)
            {
                // Do not mark a contending delivery successful: the active worker might
                // still fail. Throwing leaves Hangfire's retry policy in charge, and a later
                // attempt will either render safely or observe the terminal video state.
                log.LogWarning(
                    "Deferring duplicate render job {JobId} for video {VideoId}: lease is already held",
                    context.BackgroundJob.Id, videoId);
                throw new InvalidOperationException(
                    $"Video {videoId} is already being rendered by another worker.", ex);
            }

            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CatMemeDbContext>();
            var video = db.Videos.AsNoTracking()
                .Where(v => v.Id == videoId)
                .Select(v => new { v.Status, v.HangfireJobId })
                .SingleOrDefault();

            var currentJobId = context.BackgroundJob.Id;
            if (!CanRun(video?.Status, video?.HangfireJobId, currentJobId))
            {
                context.Canceled = true;
                log.LogWarning(
                    "Cancelling stale render job {JobId} for video {VideoId}: status={Status}, currentJob={CurrentJob}",
                    currentJobId, videoId, video?.Status ?? "missing", video?.HangfireJobId ?? "none");
                return;
            }

            context.Items[LeaseItem] = lease;
            lease = null; // ownership transferred to OnPerformed
        }
        finally
        {
            lease?.Dispose();
        }
    }

    public void OnPerformed(PerformedContext context)
    {
        if (context.Items.TryGetValue(LeaseItem, out var value) && value is IDisposable lease)
            lease.Dispose();
    }

    public static bool CanRun(string? status, string? recordedJobId, string currentJobId)
    {
        var matchingJob = string.IsNullOrWhiteSpace(recordedJobId) ||
                          string.Equals(recordedJobId, currentJobId, StringComparison.Ordinal);
        return matchingJob && status is VideoStatus.Rendering or VideoStatus.Failed;
    }

    private static bool TryGetVideoId(PerformingContext context, out int videoId)
    {
        videoId = 0;
        var job = context.BackgroundJob.Job;
        return job.Type == typeof(VideoEditJob) &&
               job.Method.Name == nameof(VideoEditJob.RunAsync) &&
               job.Args.FirstOrDefault() is int id &&
               (videoId = id) > 0;
    }
}
