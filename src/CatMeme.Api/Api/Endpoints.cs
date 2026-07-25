using CatMeme.Api.Data;
using CatMeme.Api.Domain;
using CatMeme.Api.Jobs;
using CatMeme.Api.Services;
using FluentValidation;
using Hangfire;
using Microsoft.EntityFrameworkCore;

namespace CatMeme.Api.Api;

/// <param name="Topic">
/// Optional. When supplied, the pipeline skips topic invention and builds one video around
/// this exact line instead; <paramref name="Count"/> is ignored.
/// </param>
/// <param name="Dialogue">
/// Optional hand-written conversation, one turn per line as "a: ..." or "b: ...".
/// Only meaningful alongside <paramref name="Topic"/>; blank lets the model decide.
/// </param>
public record BatchRequest(int Count, string? Topic = null, string? Dialogue = null);
public record BatchResponse(string BatchId, string[] JobIds);

public class BatchRequestValidator : AbstractValidator<BatchRequest>
{
    public BatchRequestValidator()
    {
        // Count only has to be sane when it is actually used.
        RuleFor(x => x.Count).InclusiveBetween(1, 50)
            .When(x => string.IsNullOrWhiteSpace(x.Topic))
            .WithMessage("Count must be between 1 and 50.");

        RuleFor(x => x.Topic!)
            .MinimumLength(8).WithMessage("Give the topic a few more words.")
            .MaximumLength(200).WithMessage("Keep the topic under 200 characters.")
            .When(x => !string.IsNullOrWhiteSpace(x.Topic));

        // Parsed here rather than in the job so a typo comes back as a form error instead of
        // a video that fails minutes later.
        RuleFor(x => x.Dialogue!)
            .Must(d => DialogueLine.ParseLines(d).Count >= 2)
            .WithMessage("Write at least two turns, each starting with \"a:\" or \"b:\".")
            .When(x => !string.IsNullOrWhiteSpace(x.Dialogue));

        RuleFor(x => x.Dialogue!)
            .Must((req, _) => !string.IsNullOrWhiteSpace(req.Topic))
            .WithMessage("Dialogue needs a topic to go with it.")
            .When(x => !string.IsNullOrWhiteSpace(x.Dialogue));
    }
}

public record VideoDto(
    int Id, int TopicId, string Topic, string Mood, string Layout, string Status,
    string? Error, int DurationMs, string? VideoUrl, string? ThumbUrl,
    DateTime CreatedAt, DateTime? CompletedAt, string? BatchId,
    string? Caption, string? Hashtags, DateTime? ScheduledAt, int? PostId, string? PostStatus);

public record SettingsDto(
    string ScriptProvider, string BackgroundSource, string? PexelsApiKey, string? FbPageId, string? FbAccessToken,
    string? IgUserId, string? IgAccessToken, string DefaultPostCaptionTemplate,
    bool AutoUpload, string TargetTimezone, int BatchSize,
    string MorningSlotUtc, string AfternoonSlotUtc, string EveningSlotUtc, int TargetDurationMs);

public record ClipDto(int Id, string Filename, int DurationMs, string MoodTags, string KeyColor,
    double KeySimilarity, string? CropFilter, int Width, int Height, bool IsActive,
    double BarResidue, string? DisabledReason, string SourceFile, double SubjectFill);

public record ImportRequest(string? SourcePath);

/// <param name="MoodTags">Comma separated; must contain at least one castable action if active.</param>
public record ClipUpdate(string? MoodTags, bool? IsActive, string? DisabledReason);
public record ScheduleRequest(DateTime ScheduledAtUtc, string? Platform);
public record StatsDto(int Total, int Rendering, int AwaitingApproval, int Ready, int Posted, int Failed, int Cancelled, int Clips);

/// <summary>
/// One page of results plus the full count, so the UI can render real pager controls.
/// <paramref name="Total"/> is the size of the whole filtered set, not of <paramref name="Items"/>.
/// </summary>
public record PagedDto<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);

public static class Endpoints
{
    public static void MapCatMemeApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        MapJobs(api);
        MapQueue(api);
        MapSettings(api);
        MapPosts(api);
        MapClips(api);
    }

    private static void MapJobs(RouteGroupBuilder api)
    {
        api.MapPost("/jobs/batch", async (
            BatchRequest req, IValidator<BatchRequest> validator,
            IBackgroundJobClient jobs, CancellationToken ct) =>
        {
            var result = await validator.ValidateAsync(req, ct);
            if (!result.IsValid)
                return Results.ValidationProblem(result.ToDictionary());

            var batchId = $"b{DateTime.UtcNow:yyyyMMddHHmmss}";
            var custom = req.Topic?.Trim();

            var jobId = string.IsNullOrWhiteSpace(custom)
                ? jobs.Enqueue<TopicGenerationJob>(j => j.RunAsync(batchId, req.Count, CancellationToken.None))
                : jobs.Enqueue<TopicGenerationJob>(j => j.RunOneAsync(batchId, custom, req.Dialogue, CancellationToken.None));

            return Results.Ok(new BatchResponse(batchId, [jobId]));
        });

        // Cancel a render that is queued or actively running.
        api.MapDelete("/jobs/{videoId:int}/cancel", async (
            int videoId, CatMemeDbContext db, PipelinePaths paths, CancellationToken ct) =>
        {
            var video = await db.Videos.FirstOrDefaultAsync(v => v.Id == videoId, ct);
            if (video is null) return Results.NotFound();

            if (video.Status is VideoStatus.Posted or VideoStatus.Ready or VideoStatus.AwaitingApproval)
                return Results.Conflict(new { message = "Video already completed, cannot cancel." });

            if (video.Status is VideoStatus.Cancelled)
                return Results.Ok(new { message = "Already cancelled." });

            if (!string.IsNullOrWhiteSpace(video.HangfireJobId))
                try { BackgroundJob.Delete(video.HangfireJobId); } catch { /* may have already started */ }

            if (video.FfmpegPid is int pid) ProcessRunner.KillByPid(pid);

            FFmpegService.TryDelete(paths.OutputVideo(videoId));
            FFmpegService.TryDelete(paths.Thumbnail(videoId));

            video.Status = VideoStatus.Cancelled;
            video.FfmpegPid = null;
            video.OutputPath = null;
            await db.SaveChangesAsync(ct);

            return Results.Ok(new { message = "Cancelled." });
        });

        api.MapPost("/jobs/{videoId:int}/retry", async (
            int videoId, CatMemeDbContext db, IBackgroundJobClient jobs,
            ILogger<VideoEditJob> log, CancellationToken ct) =>
        {
            var video = await db.Videos.AsNoTracking().FirstOrDefaultAsync(v => v.Id == videoId, ct);
            if (video is null) return Results.NotFound();

            var policy = VideoRetryPolicy.Check(video.Status);
            if (!policy.Allowed)
                return Results.Conflict(new { message = policy.Message });

            // Claim the failed/cancelled row atomically. Two simultaneous clicks can both
            // read the old status, but only one conditional UPDATE is allowed to transition
            // it to rendering; the loser never creates a Hangfire job.
            var previousStatus = video.Status;
            var previousError = video.ErrorMessage;
            var claimed = await db.Videos
                .Where(v => v.Id == videoId && v.Status == previousStatus)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(v => v.Status, VideoStatus.Rendering)
                    .SetProperty(v => v.ErrorMessage, (string?)null)
                    .SetProperty(v => v.FfmpegPid, (int?)null)
                    .SetProperty(v => v.HangfireJobId, (string?)null), ct);

            if (claimed == 0)
            {
                var current = await db.Videos.AsNoTracking()
                    .Where(v => v.Id == videoId)
                    .Select(v => v.Status)
                    .SingleAsync(ct);
                return Results.Conflict(new
                {
                    message = current == VideoStatus.Rendering
                        ? "Video is already queued or rendering."
                        : $"Video changed to '{current}' before retry could be queued.",
                });
            }

            string jobId;
            try
            {
                jobId = jobs.Enqueue<VideoEditJob>(j => j.RunAsync(videoId, CancellationToken.None));
            }
            catch (Exception ex)
            {
                // Do not strand the video in rendering if Hangfire storage is unavailable.
                await db.Videos
                    .Where(v => v.Id == videoId && v.Status == VideoStatus.Rendering &&
                                v.HangfireJobId == null)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(v => v.Status, previousStatus)
                        .SetProperty(v => v.ErrorMessage, previousError), CancellationToken.None);
                log.LogError(ex, "Could not enqueue retry for video {VideoId}; claim rolled back", videoId);
                return Results.Problem("Could not queue the retry. The video remains retryable.");
            }

            await db.Videos
                .Where(v => v.Id == videoId && v.HangfireJobId == null)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(v => v.HangfireJobId, jobId), ct);

            return Results.Ok(new { jobId });
        });
    }

    private static void MapQueue(RouteGroupBuilder api)
    {
        api.MapGet("/queue", async (
            string? status, int? page, int? pageSize, CatMemeDbContext db, CancellationToken ct) =>
        {
            var q = db.Videos.Include(v => v.Topic).Include(v => v.Posts).AsNoTracking();
            if (!string.IsNullOrWhiteSpace(status))
            {
                var wanted = status.Split(',', StringSplitOptions.RemoveEmptyEntries);
                q = q.Where(v => wanted.Contains(v.Status));
            }

            // Paged, not capped. This used to Take(200) with no way to reach anything
            // older, so a growing library silently lost its own history.
            var size = Math.Clamp(pageSize ?? 24, 1, 100);
            var total = await q.CountAsync(ct);
            var last = Math.Max(1, (int)Math.Ceiling(total / (double)size));
            var current = Math.Clamp(page ?? 1, 1, last);   // a stale page number lands on the last one

            var videos = await q.OrderByDescending(v => v.Id)
                .Skip((current - 1) * size)
                .Take(size)
                .ToListAsync(ct);

            return Results.Ok(new PagedDto<VideoDto>(videos.Select(ToDto).ToList(), total, current, size));
        });

        api.MapGet("/queue/stats", async (CatMemeDbContext db, CancellationToken ct) =>
        {
            var g = await db.Videos.GroupBy(v => v.Status)
                .Select(x => new { Status = x.Key, Count = x.Count() })
                .ToListAsync(ct);
            int C(string s) => g.FirstOrDefault(x => x.Status == s)?.Count ?? 0;

            return Results.Ok(new StatsDto(
                g.Sum(x => x.Count), C(VideoStatus.Rendering), C(VideoStatus.AwaitingApproval),
                C(VideoStatus.Ready), C(VideoStatus.Posted), C(VideoStatus.Failed), C(VideoStatus.Cancelled),
                await db.CatClips.CountAsync(c => c.IsActive, ct)));
        });

        api.MapPost("/videos/{id:int}/approve", async (int id, CatMemeDbContext db, CancellationToken ct) =>
        {
            var v = await db.Videos.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (v is null) return Results.NotFound();
            if (v.Status != VideoStatus.AwaitingApproval)
                return Results.Conflict(new { message = $"Video is '{v.Status}', not awaiting approval." });

            v.Status = VideoStatus.Ready;
            v.ApprovedAt = DateTime.UtcNow;
            v.ApprovedBy = "local";
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { message = "Approved." });
        });

        api.MapPost("/videos/{id:int}/reject", async (
            int id, CatMemeDbContext db, PipelinePaths paths, CancellationToken ct) =>
        {
            var v = await db.Videos.Include(x => x.Posts).FirstOrDefaultAsync(x => x.Id == id, ct);
            if (v is null) return Results.NotFound();

            v.Status = VideoStatus.Rejected;
            FFmpegService.TryDelete(paths.OutputVideo(id));
            v.OutputPath = null;

            foreach (var p in v.Posts.Where(p => p.Status == PostStatus.Scheduled))
                p.Status = PostStatus.Cancelled;

            await db.SaveChangesAsync(ct);
            return Results.Ok(new { message = "Rejected." });
        });
    }

    private static void MapSettings(RouteGroupBuilder api)
    {
        api.MapGet("/settings", async (CatMemeDbContext db, CancellationToken ct) =>
        {
            var s = await SettingsAccessor.GetAsync(db, ct);
            return Results.Ok(new SettingsDto(
                s.ScriptProvider, s.BackgroundSource, s.PexelsApiKey, s.FbPageId, s.FbAccessToken,
                s.IgUserId, s.IgAccessToken, s.DefaultPostCaptionTemplate, s.AutoUpload,
                s.TargetTimezone, s.BatchSize, s.MorningSlotUtc, s.AfternoonSlotUtc,
                s.EveningSlotUtc, s.TargetDurationMs));
        });

        api.MapPut("/settings", async (SettingsDto dto, CatMemeDbContext db, CancellationToken ct) =>
        {
            var s = await SettingsAccessor.GetAsync(db, ct);
            s.ScriptProvider = dto.ScriptProvider is "codex" ? "codex" : "claude";
            s.BackgroundSource = dto.BackgroundSource is "codex" ? "codex" : "pexels";
            s.PexelsApiKey = dto.PexelsApiKey;
            s.FbPageId = dto.FbPageId;
            s.FbAccessToken = dto.FbAccessToken;
            s.IgUserId = dto.IgUserId;
            s.IgAccessToken = dto.IgAccessToken;
            s.DefaultPostCaptionTemplate = string.IsNullOrWhiteSpace(dto.DefaultPostCaptionTemplate)
                ? "{topic} {hashtags}" : dto.DefaultPostCaptionTemplate;
            s.AutoUpload = dto.AutoUpload;
            s.TargetTimezone = dto.TargetTimezone;
            s.BatchSize = Math.Clamp(dto.BatchSize, 1, 50);
            s.MorningSlotUtc = dto.MorningSlotUtc;
            s.AfternoonSlotUtc = dto.AfternoonSlotUtc;
            s.EveningSlotUtc = dto.EveningSlotUtc;
            s.TargetDurationMs = Math.Clamp(dto.TargetDurationMs, 2000, 60000);
            s.UpdatedAt = DateTime.UtcNow;

            await db.SaveChangesAsync(ct);
            return Results.Ok(new { message = "Saved." });
        });
    }

    private static void MapPosts(RouteGroupBuilder api)
    {
        // Calendar feed.
        api.MapGet("/posts", async (DateTime? from, DateTime? to, CatMemeDbContext db, CancellationToken ct) =>
        {
            var q = db.Posts.Include(p => p.Video).ThenInclude(v => v!.Topic).AsNoTracking();
            if (from is not null) q = q.Where(p => p.ScheduledAt >= from);
            if (to is not null) q = q.Where(p => p.ScheduledAt <= to);

            var posts = await q.OrderBy(p => p.ScheduledAt).Take(500).ToListAsync(ct);
            return Results.Ok(posts.Select(p => new
            {
                p.Id,
                p.VideoId,
                Topic = p.Video?.Topic?.TopicText ?? "",
                p.Platform,
                p.Caption,
                p.Hashtags,
                p.ScheduledAt,
                p.ScheduledAtTimezone,
                p.PostedAt,
                p.Status,
                p.ErrorMessage,
                p.FbPostId,
                p.IgPostId,
                VideoStatus = p.Video?.Status,
                VideoUrl = p.Video?.OutputPath is null ? null : $"/media/video_{p.VideoId}.mp4",
                ThumbUrl = p.Video?.ThumbnailPath is null ? null : $"/media/thumbs/video_{p.VideoId}.jpg",
            }));
        });

        api.MapPut("/posts/{id:int}/schedule", async (
            int id, ScheduleRequest req, CatMemeDbContext db, CancellationToken ct) =>
        {
            var p = await db.Posts.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (p is null) return Results.NotFound();
            if (p.Status == PostStatus.Posted)
                return Results.Conflict(new { message = "Already posted." });

            p.ScheduledAt = DateTime.SpecifyKind(req.ScheduledAtUtc, DateTimeKind.Utc);
            if (!string.IsNullOrWhiteSpace(req.Platform)) p.Platform = req.Platform!;
            p.Status = PostStatus.Scheduled;
            p.ErrorMessage = null;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { message = "Rescheduled." });
        });

        api.MapDelete("/posts/{id:int}", async (int id, CatMemeDbContext db, CancellationToken ct) =>
        {
            var p = await db.Posts.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (p is null) return Results.NotFound();
            if (p.Status == PostStatus.Posted)
                return Results.Conflict(new { message = "Already posted, cannot cancel." });

            p.Status = PostStatus.Cancelled;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { message = "Cancelled." });
        });
    }

    private static void MapClips(RouteGroupBuilder api)
    {
        api.MapGet("/clips", async (CatMemeDbContext db, CancellationToken ct) =>
        {
            var clips = await db.CatClips.AsNoTracking().OrderBy(c => c.Id).ToListAsync(ct);
            return Results.Ok(clips.Select(c => new ClipDto(
                c.Id, c.Filename, c.DurationMs, c.MoodTags, c.KeyColor, c.KeySimilarity,
                c.CropFilter, c.SourceWidth, c.SourceHeight, c.IsActive,
                c.BarResidue, c.DisabledReason, Path.GetFileName(c.SourceFile), c.SubjectFill)));
        });

        // Long running: splits a compilation into the clip library.
        api.MapPost("/clips/import", async (
            ImportRequest req, bool? reset, ClipLibraryService lib, IConfiguration cfg, CancellationToken ct) =>
        {
            var source = req.SourcePath;
            if (string.IsNullOrWhiteSpace(source)) source = cfg["DEFAULT_COMPILATION_PATH"];
            if (string.IsNullOrWhiteSpace(source))
                return Results.BadRequest(new { message = "No source path supplied." });
            if (!File.Exists(source))
                return Results.BadRequest(new { message = $"File not found: {source}" });

            var result = await lib.ImportCompilationAsync(source, reset ?? false, ct);
            return result.Error is null
                ? Results.Ok(new { result.Imported, result.Skipped })
                : Results.BadRequest(new { message = result.Error });
        });

        // Re-measures every clip for trapped letterbox bars and disables the bad ones.
        api.MapPost("/clips/reanalyze", async (bool? apply, ClipLibraryService lib, CancellationToken ct) =>
            Results.Ok(await lib.ReanalyzeAsync(apply ?? true, ct)));

        // Renders every reference benchmark three times and reports whether the quality
        // standard holds. Long running (nine renders), so it is queued rather than awaited.
        api.MapPost("/benchmarks/visual-quality", (IBackgroundJobClient jobs) =>
        {
            var jobId = jobs.Enqueue<VisualQualityBenchmarkJob>(j => j.RunAsync(CancellationToken.None));
            return Results.Ok(new { jobId, message = "Benchmark queued; poll GET for the report." });
        });

        api.MapGet("/benchmarks/visual-quality", async (PipelinePaths paths, CancellationToken ct) =>
        {
            var path = VisualQualityBenchmarkJob.ReportPath(paths);
            if (!File.Exists(path))
                return Results.NotFound(new { message = "No benchmark has been run yet." });

            return Results.Content(await File.ReadAllTextAsync(path, ct), "application/json");
        });

        // Edit what a clip is TAGGED as. Import can only guess tags from duration, which
        // produces clips that are active but uncastable, so this is the only way a newly
        // imported clip ever becomes usable.
        api.MapPut("/clips/{id:int}", async (
            int id, ClipUpdate req, CatMemeDbContext db, CancellationToken ct) =>
        {
            var c = await db.CatClips.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (c is null) return Results.NotFound();

            var tags = (req.MoodTags ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(t => t.ToLowerInvariant())
                .Distinct()
                .ToList();

            var active = req.IsActive ?? c.IsActive;

            // An active clip with no recognised action can never be cast - it just sits in
            // the library looking usable. Refuse rather than create another inert row.
            if (active && !tags.Any(t => ClaudeCliService.ClipActions.Contains(t)))
                return Results.BadRequest(new
                {
                    message = "An active clip needs at least one action tag from: "
                              + string.Join(", ", ClaudeCliService.ClipActions),
                });

            c.MoodTags = string.Join(",", tags);
            c.IsActive = active;
            c.DisabledReason = active ? null : req.DisabledReason ?? c.DisabledReason;
            await db.SaveChangesAsync(ct);

            return Results.Ok(new { c.Id, c.MoodTags, c.IsActive });
        });

        api.MapDelete("/clips/{id:int}", async (int id, CatMemeDbContext db, CancellationToken ct) =>
        {
            var c = await db.CatClips.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (c is null) return Results.NotFound();
            c.IsActive = false;                 // soft delete keeps rendered videos referentially intact
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { message = "Deactivated." });
        });
    }

    private static VideoDto ToDto(Video v)
    {
        var post = v.Posts.OrderByDescending(p => p.Id).FirstOrDefault();
        return new VideoDto(
            v.Id, v.TopicId, v.Topic?.TopicText ?? "", v.Topic?.Mood ?? "", v.Topic?.Layout ?? "",
            v.Status, v.ErrorMessage, v.DurationMs,
            v.OutputPath is null ? null : $"/media/video_{v.Id}.mp4",
            v.ThumbnailPath is null ? null : $"/media/thumbs/video_{v.Id}.jpg",
            v.CreatedAt, v.CompletedAt, v.BatchId,
            post?.Caption, post?.Hashtags, post?.ScheduledAt, post?.Id, post?.Status);
    }
}
