using CatMeme.Api.Data;
using CatMeme.Api.Domain;
using CatMeme.Api.Services;
using Hangfire;
using Microsoft.EntityFrameworkCore;

namespace CatMeme.Api.Jobs;

/// <summary>Step 1: ask the configured AI CLI for POV scripts, then fan out one asset job per topic.</summary>
public class TopicGenerationJob(
    CatMemeDbContext db,
    ClaudeCliService claude,
    StoryboardCastabilityService castability,
    IBackgroundJobClient jobs,
    PipelinePaths paths,
    ILogger<TopicGenerationJob> log)
{
    [AutomaticRetry(Attempts = 3, DelaysInSeconds = [30, 120, 300])]
    public async Task RunAsync(string batchId, int count, CancellationToken ct)
    {
        log.LogInformation("Batch {Batch}: generating {Count} topics", batchId, count);

        // Every batch used to generate blind, so the same lines recurred across batches.
        // Failed topics are excluded: nothing was ever published from them, and a rejected
        // line occupying a slot only shrinks the useful part of the avoid list.
        var recent = await db.Topics.Where(t => t.Status != TopicStatus.Failed)
            .OrderByDescending(t => t.Id)
            .Select(t => t.TopicText).Take(80).ToListAsync(ct);
        var settings = await SettingsAccessor.GetAsync(db, ct);

        var batch = await claude.GenerateTopicsAsync(count, recent, ct);
        var topics = batch.Topics;

        // The avoid-list and format rules are advisory to the model; enforce them here.
        var kept = new List<Topic>();
        foreach (var t in topics)
        {
            // Fallback seeds are exempt from the recent check. They are a small fixed list,
            // so once the pipeline has run for a while every seed is already "recent" and
            // this rule rejected the entire fallback batch - the pipeline produced nothing
            // at all whenever the AI CLI was unavailable. A repeated topic beats an empty
            // batch. Duplicates *within* the batch are still dropped either way.
            if ((!batch.FromFallback && recent.Any(r => TopicsLookAlike(r, t.TopicText))) ||
                kept.Any(k => TopicsLookAlike(k.TopicText, t.TopicText)))
            {
                log.LogInformation("Dropping duplicate topic: {Topic}", t.TopicText);
                continue;
            }

            var check = ScriptValidator.Validate(t);
            if (!check.Ok)
            {
                log.LogWarning("Dropping invalid script '{Topic}': {Errors}",
                    t.TopicText, string.Join("; ", check.Errors));
                continue;
            }

            // Prove every beat, including the payoff interaction, against the assets that
            // are available now. Valid action words alone do not make a storyboard filmable.
            var target = Math.Max(settings.TargetDurationMs, RenderService.RecommendedDurationMs(t));
            var cast = await castability.CheckAsync(t, target, ct);
            if (!cast.Ok)
            {
                log.LogWarning("Dropping uncastable storyboard '{Topic}': {Error}",
                    t.TopicText, cast.Summary);
                continue;
            }

            kept.Add(t);
        }
        topics = kept;

        foreach (var t in topics)
        {
            t.BatchId = batchId;
            t.Status = TopicStatus.Pending;
        }

        db.Topics.AddRange(topics);
        await db.SaveChangesAsync(ct);

        // Keep a copy on disk, as the spec's /pipeline/topics contract requires.
        var file = Path.Combine(paths.Topics, $"topics_{batchId}.json");
        await File.WriteAllTextAsync(file,
            System.Text.Json.JsonSerializer.Serialize(topics.Select(t => new
            {
                t.TopicText, t.CatALabel, t.CatBLabel, t.Mood, t.Layout,
            }), new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), ct);

        if (topics.Count == 0)
        {
            // A batch that queues nothing looks exactly like a button that did nothing, and
            // "queued 0 topics" at INF hid a total generation outage for days. Record it on
            // the Queue the way a rejected custom topic is recorded, so it is visible where
            // the user is already looking.
            var reason = batch.FromFallback
                ? "the AI CLI produced nothing usable and no fallback script could be cast"
                : "no generated script passed validation and casting";
            log.LogWarning("Batch {Batch}: nothing queued - {Reason}", batchId, reason);

            // Status Failed keeps this out of the avoid list that seeds the next prompt.
            var placeholder = new Topic
            {
                TopicText = $"batch {batchId} produced no usable scripts",
                BatchId = batchId,
                Status = TopicStatus.Failed,
            };
            db.Topics.Add(placeholder);
            db.Videos.Add(new Video
            {
                Topic = placeholder,
                Status = VideoStatus.Failed,
                BatchId = batchId,
                ErrorMessage = $"nothing to render - {reason}",
            });
            await db.SaveChangesAsync(ct);
            return;
        }

        foreach (var t in topics)
            jobs.Enqueue<AssetGenerationJob>(j => j.RunAsync(t.Id, CancellationToken.None));

        log.LogInformation("Batch {Batch}: queued {N} topics for asset generation", batchId, topics.Count);
    }

    /// <summary>
    /// Renders one video from a topic the user typed, with optionally hand-written dialogue.
    ///
    /// Unlike a generated batch - where a bad script is simply dropped and the batch moves on
    /// - a rejected custom topic is recorded as a FAILED video carrying the reason. Silently
    /// discarding something someone typed by hand looks like the button did nothing.
    /// </summary>
    [AutomaticRetry(Attempts = 0)]
    public async Task RunOneAsync(string batchId, string topicText, string? dialogueText, CancellationToken ct)
    {
        log.LogInformation("Batch {Batch}: building a script for '{Topic}'", batchId, topicText);

        var topic = await claude.BuildScriptForTopicAsync(topicText, ct);
        topic.BatchId = batchId;
        topic.Status = TopicStatus.Pending;

        // A hand-written conversation wins over whatever the model proposed.
        var typed = DialogueLine.ParseLines(dialogueText);
        if (typed.Count > 0) topic.DialogueJson = DialogueLine.Serialize(typed);

        var scriptCheck = ScriptValidator.Validate(topic);
        string? problem;
        if (!scriptCheck.Ok)
        {
            problem = string.Join("; ", scriptCheck.Errors);
        }
        else
        {
            var settings = await SettingsAccessor.GetAsync(db, ct);
            var target = Math.Max(
                settings.TargetDurationMs,
                RenderService.RecommendedDurationMs(topic));
            var castCheck = await castability.CheckAsync(topic, target, ct);
            problem = castCheck.Ok ? null : castCheck.Summary;
        }

        db.Topics.Add(topic);

        if (problem is not null)
        {
            // Surfaced on the Queue as a failed card rather than vanishing into the log.
            topic.Status = TopicStatus.Failed;
            db.Videos.Add(new Video
            {
                Topic = topic,
                Status = VideoStatus.Failed,
                BatchId = batchId,
                ErrorMessage = $"blocked before render - {problem}",
            });
            await db.SaveChangesAsync(ct);
            log.LogWarning("Custom topic '{Topic}' rejected: {Problem}", topicText, problem);
            return;
        }

        await db.SaveChangesAsync(ct);
        jobs.Enqueue<AssetGenerationJob>(j => j.RunAsync(topic.Id, CancellationToken.None));
        log.LogInformation("Custom topic {Id} queued for asset generation", topic.Id);
    }

    /// <summary>
    /// Word-overlap near-duplicate test. "POV: when she makes a mistake vs when I make a
    /// mistake" and its case/punctuation variants must count as the same topic.
    /// </summary>
    internal static bool TopicsLookAlike(string a, string b)
    {
        static HashSet<string> Words(string s) => new(
            s.ToLowerInvariant()
                .Where(c => char.IsLetterOrDigit(c) || c == ' ')
                .Aggregate("", (acc, c) => acc + c)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w.Length > 2));   // drop filler: pov, the, my, she...

        var wa = Words(a);
        var wb = Words(b);
        if (wa.Count == 0 || wb.Count == 0) return false;

        var overlap = wa.Intersect(wb).Count();
        return (double)overlap / Math.Min(wa.Count, wb.Count) > 0.7;
    }
}

/// <summary>Step 2: source a background plate and create the video row.</summary>
public class AssetGenerationJob(
    CatMemeDbContext db,
    BackgroundAssetService backgrounds,
    IBackgroundJobClient jobs,
    ILogger<AssetGenerationJob> log)
{
    [AutomaticRetry(Attempts = 3, DelaysInSeconds = [30, 120, 300])]
    public async Task RunAsync(int topicId, CancellationToken ct)
    {
        var topic = await db.Topics.FirstOrDefaultAsync(t => t.Id == topicId, ct);
        if (topic is null) { log.LogWarning("Topic {Id} vanished", topicId); return; }

        var settings = await SettingsAccessor.GetAsync(db, ct);
        var bg = await backgrounds.GetBackgroundAsync(settings, topic, ct);

        topic.Status = TopicStatus.Editing;

        var video = new Video
        {
            TopicId = topic.Id,
            BackgroundPath = bg,
            Status = VideoStatus.Rendering,
            BatchId = topic.BatchId,
        };
        db.Videos.Add(video);
        await db.SaveChangesAsync(ct);

        var jobId = jobs.Enqueue<VideoEditJob>(j => j.RunAsync(video.Id, CancellationToken.None));
        video.HangfireJobId = jobId;
        await db.SaveChangesAsync(ct);

        log.LogInformation("Topic {Id}: background ready, video {VideoId} queued (job {JobId})", topicId, video.Id, jobId);
    }
}

/// <summary>Step 3: pick clips, run ffmpeg, and route to approval or ready.</summary>
public class VideoEditJob(
    CatMemeDbContext db,
    ClipLibraryService clips,
    IPairedInteractionAssetSelector interactions,
    RenderService render,
    RenderPreflight preflight,
    VideoVerifier verifier,
    VisualQualityReviewService visualQuality,
    BackgroundAssetService backgrounds,
    ClaudeCliService claude,
    PipelinePaths paths,
    ILogger<VideoEditJob> log)
{
    [AutomaticRetry(Attempts = 2, DelaysInSeconds = [60, 240])]
    public async Task RunAsync(int videoId, CancellationToken ct)
    {
        var video = await db.Videos.Include(v => v.Topic).FirstOrDefaultAsync(v => v.Id == videoId, ct);
        if (video is null) { log.LogWarning("Video {Id} vanished", videoId); return; }

        // A user cancel may have landed while this job sat in the queue.
        if (video.Status is VideoStatus.Cancelled or VideoStatus.Rejected)
        {
            log.LogInformation("Video {Id} was {Status} before render started; skipping.", videoId, video.Status);
            return;
        }

        var settings = await SettingsAccessor.GetAsync(db, ct);
        var topic = video.Topic!;

        try
        {
            // Strict pre-render gate: refuse to render a script that could come out wrong.
            // Validated again here (not only at generation) because clips can be disabled
            // and topics can predate the action vocabulary; a retry must not slip through.
            var scriptCheck = ScriptValidator.Validate(topic);
            if (!scriptCheck.Ok)
                throw new InvalidOperationException(
                    $"blocked before render - script invalid: {string.Join("; ", scriptCheck.Errors)}");

            var needsTwo = topic.Layout != Layout.SinglePov;
            var recommendedTarget = RenderService.RecommendedDurationMs(topic);
            var target = Math.Clamp(
                Math.Max(settings.TargetDurationMs, recommendedTarget),
                RenderService.MinDurationMs,
                RenderService.MaxDurationMs);
            if (target > settings.TargetDurationMs)
            {
                log.LogInformation(
                    "Video {Id}: extending configured {Configured}ms target to {Target}ms for {Turns} dialogue beats",
                    video.Id, settings.TargetDurationMs, target, topic.Dialogue().Count);
            }

            // Casting and preflight run as one loop. Every input the renderer touches is
            // checked against the files actually on disk first, and anything that fails is
            // REPLACED rather than failing the video outright - a single stale clip row or
            // a half-written background must not take a whole batch down with it.
            // Bounded because each pass costs several ffmpeg probes.
            const int maxAttempts = 4;
            const int maxQualityAttempts = 3;
            var ruledOut = new List<int>();
            var ruledOutInteractions = new List<int>();
            RenderResult? acceptedResult = null;
            string? verificationWarning = null;
            VisualQualityReviewResult? lastQualityReview = null;
            var storyboardRegenerated = false;

            for (var qualityAttempt = 1; qualityAttempt <= maxQualityAttempts; qualityAttempt++)
            {
                RenderRequest? req = null;
                PreflightReport? pf = null;
                Dictionary<int, CatClip>? qualityClips = null;
                Dictionary<int, PairedInteractionAsset>? qualityInteractions = null;

                for (var attempt = 1; attempt <= maxAttempts; attempt++)
                {
                // Source clips are short loops, so asking for one as long as a paced story
                // would match nothing. A third of the target still prefers longer clips and
                // keeps loop seams infrequent as recommended story durations expand.
                var preferredClipMs = target / 3;

                // A video that was already cast keeps its cast. The current semantic ranker
                // is deterministic, but pinning also protects historical renders if tags or
                // the active library change later. Only preflight rejection forces a re-cast.
                var pinned = await clips.PinnedCastAsync(
                    video.CatAClipId, needsTwo ? video.CatBClipId : null, ruledOut, ct);

                CatClip clipA;
                CatClip? clipB;

                if (pinned is not null)
                {
                    (clipA, clipB) = pinned.Value;
                    log.LogInformation("Video {Id}: reusing cast A={A} B={B}", video.Id, clipA.Id, clipB?.Id);
                }
                else
                {
                    // Both cats are cast together: casting A first and B from the remainder
                    // strands B whenever the clip it needs was also a candidate for A.
                    var cast = await clips.CastAsync(topic, preferredClipMs, ruledOut, ct);
                    if (!cast.Ok)
                        throw new InvalidOperationException($"blocked before render - {cast.Error}");

                    clipA = cast.A!;
                    clipB = cast.B;
                    log.LogInformation(
                        "Video {Id}: cast A={AId} ({AAction}; {ATags}) B={BId} ({BAction}; {BTags})",
                        video.Id,
                        clipA.Id, topic.CatAAction, clipA.MoodTags,
                        clipB?.Id, topic.CatBAction, clipB?.MoodTags);
                }

                var selectedClips = new Dictionary<int, CatClip> { [clipA.Id] = clipA };
                if (clipB is not null) selectedClips[clipB.Id] = clipB;
                qualityClips = selectedClips;
                var selectedInteractions = new Dictionary<int, PairedInteractionAsset>();
                qualityInteractions = selectedInteractions;

                var visualBeats = await BuildVisualBeatsAsync(
                    topic, target, preferredClipMs, clipA, clipB,
                    ruledOut, ruledOutInteractions, selectedClips, selectedInteractions, ct);

                req = new RenderRequest(
                    video.Id,
                    video.BackgroundPath!,
                    paths.CatClip(clipA.Filename),
                    clipB is null ? null : paths.CatClip(clipB.Filename),
                    ToAnalysis(clipA),
                    clipB is null ? null : ToAnalysis(clipB),
                    topic,
                    target,
                    visualBeats);

                pf = await preflight.CheckAsync(req, ct);
                if (pf.Ok)
                {
                    video.CatAClipId = clipA.Id;
                    video.CatBClipId = clipB?.Id;
                    break;
                }

                if (attempt == maxAttempts) break;

                log.LogWarning("Video {Id} preflight attempt {N}/{Max} failed: {Issues}",
                    video.Id, attempt, maxAttempts, pf.Summary);

                // The renderer would silently clamp this; adopting it here keeps the value
                // the verifier is later given equal to the one ffmpeg was actually asked for.
                if (pf.Faulted(PreflightFault.Duration))
                    target = pf.EffectiveDurationMs;

                if (pf.Faulted(PreflightFault.Background))
                    video.BackgroundPath = await backgrounds.GetBackgroundAsync(settings, topic, ct);

                foreach (var issue in pf.Issues
                             .Where(i => i.Fault is PreflightFault.ClipA or PreflightFault.ClipB)
                             .GroupBy(i => i.ClipId ??
                                 (i.Fault == PreflightFault.ClipA ? clipA.Id : clipB?.Id ?? -1))
                             .Select(g => g.First()))
                {
                    var clipId = issue.ClipId ??
                        (issue.Fault == PreflightFault.ClipA ? clipA.Id : clipB?.Id);
                    if (clipId is not int id || !selectedClips.TryGetValue(id, out var clip)) continue;

                    // Unusable against the file on disk - a fact, not a judgement call - so
                    // take it out of circulation instead of letting the next video trip on it.
                    clip.IsActive = false;
                    clip.DisabledReason = issue.Message;
                    if (!ruledOut.Contains(clip.Id)) ruledOut.Add(clip.Id);
                    log.LogWarning("Disabling clip {Id} ({File}): {Reason}",
                        clip.Id, clip.Filename, clip.DisabledReason);
                }

                foreach (var assetId in pf.RecastInteractionAssetIds)
                {
                    if (!ruledOutInteractions.Contains(assetId))
                        ruledOutInteractions.Add(assetId);
                    log.LogWarning(
                        "Video {Id}: excluding paired interaction asset {AssetId} after preflight",
                        video.Id, assetId);
                }

                await db.SaveChangesAsync(ct);
            }

            if (req is null || pf is null || !pf.Ok)
                throw new InvalidOperationException(
                    $"blocked before render - inputs still failed preflight after {maxAttempts} " +
                    $"attempts: {pf?.Summary}");

            video.Status = VideoStatus.Rendering;
            await db.SaveChangesAsync(ct);

            var rendered = await render.RenderAsync(req, pid =>
            {
                // Recorded synchronously so the cancel endpoint can find the live process.
                video.FfmpegPid = pid;
                db.SaveChanges();
            }, ct);

            if (!rendered.Success)
                throw new InvalidOperationException(rendered.Error ?? "render failed");

            // A zero exit code is not proof the reel is usable. Verified against the request
            // that was rendered, never the raw setting - those two differ once clamped.
            var check = await verifier.VerifyAsync(rendered.OutputPath!, req.TargetDurationMs, ct);
            if (!check.Ok)
                throw new InvalidOperationException($"output failed verification: {check.Summary}");

            var evidence = (qualityClips ?? [])
                .ToDictionary(
                    pair => pair.Key,
                    pair => VisualQualityClipEvidence.From(pair.Value));
            var interactionEvidence = (qualityInteractions ?? [])
                .ToDictionary(
                    pair => pair.Key,
                    pair => VisualQualityInteractionEvidence.From(pair.Value));
            var qaArtifacts = Path.Combine(
                paths.Work,
                "visual-qa",
                $"video-{video.Id}",
                $"attempt-{qualityAttempt}");
            lastQualityReview = await visualQuality.ReviewAsync(
                new VisualQualityReviewRequest(
                    video.Id,
                    rendered.OutputPath!,
                    video.BackgroundPath!,
                    topic,
                    req.TargetDurationMs,
                    req.VisualBeats ?? [],
                    evidence,
                    qaArtifacts,
                    Interactions: interactionEvidence),
                ct);

            if (lastQualityReview.Passed)
            {
                acceptedResult = rendered;
                verificationWarning = check.Warnings.Count > 0 ? check.Summary : null;
                log.LogInformation(
                    "Video {Id}: visual quality passed on attempt {Attempt}/{Max} " +
                    "with score {Score:0.000}",
                    video.Id, qualityAttempt, maxQualityAttempts, lastQualityReview.Score);
                break;
            }

            if ((lastQualityReview.RecastClipIds.Count > 0 ||
                 lastQualityReview.RecastInteractionAssetIds.Count > 0) &&
                qualityAttempt < maxQualityAttempts)
            {
                foreach (var id in lastQualityReview.RecastClipIds)
                    if (!ruledOut.Contains(id)) ruledOut.Add(id);
                foreach (var id in lastQualityReview.RecastInteractionAssetIds)
                    if (!ruledOutInteractions.Contains(id)) ruledOutInteractions.Add(id);

                if (video.CatAClipId is int a && ruledOut.Contains(a)) video.CatAClipId = null;
                if (video.CatBClipId is int b && ruledOut.Contains(b)) video.CatBClipId = null;
                video.FfmpegPid = null;
                await db.SaveChangesAsync(ct);
                log.LogWarning(
                    "Video {Id}: visual QA rejected clips [{Clips}] and paired assets " +
                    "[{Interactions}]; recasting weak beats " +
                    "for attempt {Next}/{Max}",
                    video.Id,
                    string.Join(',', lastQualityReview.RecastClipIds),
                    string.Join(',', lastQualityReview.RecastInteractionAssetIds),
                    qualityAttempt + 1,
                    maxQualityAttempts);
                continue;
            }

            if (!storyboardRegenerated && qualityAttempt < maxQualityAttempts &&
                topic.Dialogue().Any(line => StoryboardQualityProfile.Resolve(
                    line.StoryboardProfile) is not null))
            {
                var replacement = await claude.BuildScriptForTopicAsync(topic.TopicText, ct);
                var replacementCheck = ScriptValidator.Validate(
                    replacement, StoryboardQualityProfile.Default);
                if (!replacementCheck.Ok)
                    throw new InvalidOperationException(
                        "visual QA requested storyboard regeneration, but the replacement " +
                        $"missed the quality profile: {string.Join("; ", replacementCheck.Errors)}");

                ApplyStoryboard(topic, replacement);
                storyboardRegenerated = true;
                needsTwo = topic.Layout != Layout.SinglePov;
                target = Math.Clamp(
                    Math.Max(settings.TargetDurationMs, RenderService.RecommendedDurationMs(topic)),
                    RenderService.MinDurationMs,
                    RenderService.MaxDurationMs);
                video.CatAClipId = null;
                video.CatBClipId = null;
                video.BackgroundPath = await backgrounds.GetBackgroundAsync(settings, topic, ct);
                video.FfmpegPid = null;
                await db.SaveChangesAsync(ct);
                log.LogWarning(
                    "Video {Id}: visual QA found a structural storyboard problem; " +
                    "regenerated once for attempt {Next}/{Max}",
                    video.Id, qualityAttempt + 1, maxQualityAttempts);
                continue;
            }

            throw new InvalidOperationException(
                "output failed visual quality review: " +
                QualitySummary(lastQualityReview));
            }

            if (acceptedResult is null)
                throw new InvalidOperationException(
                    "output failed visual quality review after " + maxQualityAttempts +
                    " attempts: " + QualitySummary(lastQualityReview));

            var result = acceptedResult;
            video.ErrorMessage = verificationWarning;

            video.OutputPath = result.OutputPath;
            video.DurationMs = result.DurationMs;
            video.ThumbnailPath = File.Exists(paths.Thumbnail(video.Id)) ? paths.Thumbnail(video.Id) : null;
            video.FfmpegPid = null;
            video.CompletedAt = DateTime.UtcNow;
            video.Status = settings.AutoUpload ? VideoStatus.Ready : VideoStatus.AwaitingApproval;

            topic.Status = TopicStatus.Done;

            var hashtags = await claude.GenerateHashtagsAsync(topic.TopicText, ct);
            var caption = settings.DefaultPostCaptionTemplate
                .Replace("{topic}", topic.TopicText)
                .Replace("{hashtags}", hashtags)
                .Trim();

            db.Posts.Add(new Post
            {
                VideoId = video.Id,
                Platform = "both",
                Caption = caption,
                Hashtags = hashtags,
                ScheduledAt = SchedulingService.NextSlotUtc(settings, DateTime.UtcNow),
                ScheduledAtTimezone = settings.TargetTimezone,
                Status = PostStatus.Scheduled,
            });

            await db.SaveChangesAsync(ct);
            log.LogInformation("Video {Id} rendered -> {Status} ({Ms}ms)", video.Id, video.Status, video.DurationMs);
        }
        catch (OperationCanceledException)
        {
            video.Status = VideoStatus.Cancelled;
            video.FfmpegPid = null;
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Video {Id} render failed", videoId);
            video.Status = VideoStatus.Failed;
            video.ErrorMessage = ex.Message;
            video.FfmpegPid = null;
            topic.Status = TopicStatus.Failed;
            await db.SaveChangesAsync(CancellationToken.None);
            throw;   // let Hangfire apply its retry policy
        }
    }

    /// <summary>
    /// Resolves the planner's action windows into concrete clips. Repeated visual states
    /// reuse the same cast, while a genuinely new state first tries to avoid every clip
    /// already used in the reel. Thin libraries fall back to reuse instead of failing, but
    /// the hard action constraint is never relaxed.
    /// </summary>
    private async Task<IReadOnlyList<RenderVisualBeat>> BuildVisualBeatsAsync(
        Topic topic,
        int targetDurationMs,
        int preferredClipMs,
        CatClip baseA,
        CatClip? baseB,
        IReadOnlyCollection<int> ruledOut,
        IReadOnlyCollection<int> ruledOutInteractions,
        IDictionary<int, CatClip> selectedClips,
        IDictionary<int, PairedInteractionAsset> selectedInteractions,
        CancellationToken ct)
    {
        var drafts = VisualBeatPlanner.Build(topic, targetDurationMs / 1000.0);
        if (drafts.Count == 0) return [];

        var cache = new Dictionary<string, (CatClip? A, CatClip? B)>(StringComparer.OrdinalIgnoreCase);
        var usedStates = new HashSet<int> { baseA.Id };
        if (baseB is not null) usedStates.Add(baseB.Id);

        var baseKey = StateKey(
            topic.CatAAction ?? "", topic.Layout == Layout.SinglePov ? null : topic.CatBAction,
            showA: true, showB: baseB is not null);
        cache[baseKey] = (baseA, baseB);

        var result = new List<RenderVisualBeat>();
        foreach (var draft in drafts)
        {
            var interactionAction = draft.InteractionAction?.Trim().ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(interactionAction) &&
                interactionAction != StoryboardInteractionAction.None)
            {
                var minimumMs = Math.Min(
                    4_500,
                    Math.Max(1_000, (int)Math.Round((draft.EndSec - draft.StartSec) * 1000)));
                var paired = await interactions.SelectAsync(new PairedInteractionRequest(
                    interactionAction,
                    MinimumDurationMs: minimumMs,
                    RequireAudio: interactionAction == StoryboardInteractionAction.Slap,
                    ExcludeAssetIds: ruledOutInteractions), ct);
                if (!paired.Ok)
                    throw new InvalidOperationException(
                        $"blocked before render - paired interaction '{interactionAction}': {paired.Error}");

                var asset = paired.Asset!;
                selectedInteractions[asset.Id] = asset;
                var outPointMs = asset.OutPointMs > 0 ? asset.OutPointMs : asset.DurationMs;
                var interaction = new RenderInteractionInput(
                    asset.Id,
                    paired.FilePath!,
                    ToAnalysis(asset),
                    asset.PreserveAudio,
                    asset.AudioGainDb,
                    asset.InPointMs / 1000.0,
                    outPointMs / 1000.0);
                result.Add(ToRenderBeat(draft, null, null, interaction));
                log.LogInformation(
                    "Video beat {Start:0.##}-{End:0.##}s: paired {Action}/{AssetId}",
                    draft.StartSec, draft.EndSec, interactionAction, asset.Id);
                continue;
            }

            if (!draft.ShowA && !draft.ShowB)
            {
                result.Add(ToRenderBeat(draft, null, null));
                continue;
            }

            var requestedB = draft.ShowB ? draft.ActionB : null;
            var key = StateKey(draft.ActionA, requestedB, draft.ShowA, draft.ShowB);
            if (!cache.TryGetValue(key, out var state))
            {
                // Prefer a visibly new cat for a visibly new action. If the curated pool is
                // too thin, retry with only genuinely faulty clips excluded.
                var preferDistinct = ruledOut.Concat(usedStates).Distinct().ToArray();
                var cast = await clips.CastBeatAsync(
                    topic, draft.ActionA, requestedB, draft.ShowA, draft.ShowB,
                    preferredClipMs, preferDistinct, ct);
                if (!cast.Ok && preferDistinct.Length > ruledOut.Count)
                    cast = await clips.CastBeatAsync(
                        topic, draft.ActionA, requestedB, draft.ShowA, draft.ShowB,
                        preferredClipMs, ruledOut, ct);

                if (!cast.Ok)
                    throw new InvalidOperationException(
                        $"blocked before render - visual beat {draft.StartSec:0.##}-{draft.EndSec:0.##}s: {cast.Error}");

                if ((draft.ShowA && cast.A is null) || (draft.ShowB && cast.B is null))
                    throw new InvalidOperationException(
                        $"blocked before render - visual beat {draft.StartSec:0.##}-{draft.EndSec:0.##}s: " +
                        "caster did not return every visible role");

                state = (cast.A, cast.B);
                cache[key] = state;
            }

            RenderCatInput? renderA = null;
            RenderCatInput? renderB = null;

            if (draft.ShowA)
            {
                var catA = state.A ?? throw new InvalidOperationException(
                    $"visual beat {draft.StartSec:0.##}-{draft.EndSec:0.##}s has no cat A cast");
                selectedClips[catA.Id] = catA;
                usedStates.Add(catA.Id);
                renderA = new RenderCatInput(
                    catA.Id, paths.CatClip(catA.Filename), ToAnalysis(catA));
            }

            if (draft.ShowB)
            {
                var catB = state.B ?? throw new InvalidOperationException(
                    $"visual beat {draft.StartSec:0.##}-{draft.EndSec:0.##}s has no cat B cast");
                selectedClips[catB.Id] = catB;
                usedStates.Add(catB.Id);
                var duplicateSleep = state.A is not null && state.A.Id == catB.Id &&
                    draft.ActionA.Equals("sleeping", StringComparison.OrdinalIgnoreCase) &&
                    draft.ActionB?.Equals("sleeping", StringComparison.OrdinalIgnoreCase) == true;
                renderB = new RenderCatInput(
                    catB.Id, paths.CatClip(catB.Filename), ToAnalysis(catB),
                    Mirror: duplicateSleep, PhaseOffsetSec: duplicateSleep ? 0.35 : 0);
            }

            result.Add(ToRenderBeat(draft, renderA, renderB));

            log.LogInformation(
                "Video beat {Start:0.##}-{End:0.##}s: A={AAction}/{AId} B={BAction}/{BId} stage={Stage}",
                draft.StartSec, draft.EndSec,
                draft.ShowA ? draft.ActionA : "hidden", renderA?.ClipId,
                draft.ShowB ? draft.ActionB : "hidden", renderB?.ClipId,
                draft.Stage);
        }

        return result;
    }

    private static string StateKey(
        string actionA, string? actionB, bool showA, bool showB)
    {
        var visibleA = showA ? actionA.Trim().ToLowerInvariant() : "-";
        var visibleB = showB ? actionB?.Trim().ToLowerInvariant() : "-";
        return $"{showA}:{visibleA}|{showB}:{visibleB}";
    }

    private static ClipAnalysis ToAnalysis(CatClip c) =>
        new(c.KeyColor, c.KeySimilarity, c.CropFilter, c.SourceWidth, c.SourceHeight, c.DurationMs);

    private static ClipAnalysis ToAnalysis(PairedInteractionAsset asset) =>
        new(
            asset.KeyColor,
            asset.KeySimilarity,
            asset.CropFilter,
            asset.SourceWidth,
            asset.SourceHeight,
            PairedInteractionMatching.UsableDurationMs(asset));

    private static void ApplyStoryboard(Topic target, Topic replacement)
    {
        // The user-authored topic string and persistence identity stay put. Everything the
        // storyboard model is responsible for is replaced atomically on the tracked entity.
        target.CatALabel = replacement.CatALabel;
        target.CatBLabel = replacement.CatBLabel;
        target.CatAPosition = replacement.CatAPosition;
        target.CatBPosition = replacement.CatBPosition;
        target.CatAAction = replacement.CatAAction;
        target.CatBAction = replacement.CatBAction;
        target.CaptionTop = replacement.CaptionTop;
        target.CaptionBottom = replacement.CaptionBottom;
        target.Mood = replacement.Mood;
        target.Layout = replacement.Layout;
        target.DialogueJson = replacement.DialogueJson;
    }

    private static string QualitySummary(VisualQualityReviewResult? review)
    {
        if (review is null) return "no visual-quality report was produced";
        var errors = review.Issues
            .Where(issue => issue.Severity == VisualQualitySeverity.Error)
            .Select(issue => issue.Message)
            .Distinct()
            .Take(5)
            .ToList();
        return errors.Count == 0
            ? $"score {review.Score:0.000} did not meet the quality threshold"
            : string.Join("; ", errors);
    }

    private static RenderVisualBeat ToRenderBeat(
        VisualBeatDraft draft,
        RenderCatInput? catA,
        RenderCatInput? catB,
        RenderInteractionInput? interaction = null) => new(
            draft.StartSec,
            draft.EndSec,
            catA,
            catB,
            draft.Stage,
            draft.StoryRole,
            draft.TitleVisible,
            draft.TitleStyle,
            draft.LabelsVisible,
            draft.Lighting,
            draft.CatScale,
            draft.Transition,
            draft.SoundCue,
            draft.StoryboardProfile,
            draft.InteractionAction,
            interaction);
}

/// <summary>Step 4: publish anything ready whose scheduled time has arrived.</summary>
public class PostSchedulerJob(
    CatMemeDbContext db,
    GraphApiService graph,
    ILogger<PostSchedulerJob> log)
{
    [AutomaticRetry(Attempts = 0)]   // recurring; a failed run is retried on the next tick
    public async Task RunAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        var due = await db.Posts
            .Include(p => p.Video)
            .Where(p => p.Status == PostStatus.Scheduled
                        && p.ScheduledAt != null && p.ScheduledAt <= now
                        && p.Video!.Status == VideoStatus.Ready)
            .OrderBy(p => p.ScheduledAt)
            .ThenBy(p => p.Id)
            .Take(5)
            .ToListAsync(ct);

        if (due.Count == 0) return;

        var settings = await SettingsAccessor.GetAsync(db, ct);

        foreach (var post in due)
        {
            var video = post.Video!;
            if (string.IsNullOrWhiteSpace(video.OutputPath) || !File.Exists(video.OutputPath))
            {
                post.Status = PostStatus.Failed;
                post.ErrorMessage = "Output file is missing.";
                continue;
            }

            var errors = new List<string>();

            if (post.Platform is "facebook" or "both")
            {
                var fb = await graph.PostToFacebookAsync(settings, video.OutputPath, post.Caption, ct);
                if (fb.Success) post.FbPostId = fb.PostId; else errors.Add($"FB: {fb.Error}");
            }

            if (post.Platform is "instagram" or "both")
            {
                var ig = await graph.PostToInstagramAsync(settings, video.OutputPath, post.Caption, ct);
                if (ig.Success) post.IgPostId = ig.PostId; else errors.Add($"IG: {ig.Error}");
            }

            var anySucceeded = post.FbPostId is not null || post.IgPostId is not null;
            if (anySucceeded)
            {
                post.Status = PostStatus.Posted;
                post.PostedAt = DateTime.UtcNow;
                video.Status = VideoStatus.Posted;
                // Partial success still records why the other platform failed.
                post.ErrorMessage = errors.Count > 0 ? string.Join(" | ", errors) : null;
            }
            else
            {
                post.Status = PostStatus.Failed;
                post.ErrorMessage = string.Join(" | ", errors);
                log.LogWarning("Post {Id} failed: {Err}", post.Id, post.ErrorMessage);
            }
        }

        await db.SaveChangesAsync(ct);
    }
}

/// <summary>Loads the singleton settings row, creating defaults on first run.</summary>
public static class SettingsAccessor
{
    public static async Task<AppSettings> GetAsync(CatMemeDbContext db, CancellationToken ct = default)
    {
        var s = await db.Settings.OrderBy(x => x.Id).FirstOrDefaultAsync(ct);
        if (s is not null) return s;

        s = new AppSettings();
        db.Settings.Add(s);
        await db.SaveChangesAsync(ct);
        return s;
    }
}

/// <summary>Maps the configured daily slots onto the next real UTC instant.</summary>
public static class SchedulingService
{
    public static DateTime NextSlotUtc(AppSettings s, DateTime nowUtc)
    {
        var slots = new[] { s.MorningSlotUtc, s.AfternoonSlotUtc, s.EveningSlotUtc }
            .Select(ParseTime)
            .Where(t => t.HasValue)
            .Select(t => t!.Value)
            .OrderBy(t => t)
            .ToList();

        if (slots.Count == 0) return nowUtc.AddMinutes(2);

        for (var day = 0; day < 8; day++)
        {
            var date = nowUtc.Date.AddDays(day);
            foreach (var slot in slots)
            {
                var candidate = DateTime.SpecifyKind(date + slot, DateTimeKind.Utc);
                if (candidate > nowUtc.AddMinutes(1)) return candidate;
            }
        }
        return nowUtc.AddHours(1);
    }

    private static TimeSpan? ParseTime(string? hhmm) =>
        TimeSpan.TryParse(hhmm, out var t) ? t : null;
}
