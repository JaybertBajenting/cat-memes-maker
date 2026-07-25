using CatMeme.Api.Data;
using CatMeme.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace CatMeme.Api.Services;

public record ImportResult(int Imported, int Skipped, string? Error);

/// <summary>
/// The outcome of casting a script. <paramref name="Error"/> is null on success and
/// otherwise explains exactly which requirement the library could not meet.
/// </summary>
public record CastingResult(CatClip? A, CatClip? B, string? Error)
{
    public bool Ok => Error is null;
}

/// <summary>
/// Turns a long green screen compilation into an indexed library of individual clips.
/// Each clip is analysed on import so the renderer knows its own key colour and crop box.
/// </summary>
public class ClipLibraryService(
    CatMemeDbContext db,
    FFmpegService ff,
    PipelinePaths paths,
    IConfiguration cfg,
    ILogger<ClipLibraryService> log)
{
    /// <summary>Distinct action tags across active clips, for casting error messages.</summary>
    public async Task<string> AvailableActionsAsync(CancellationToken ct = default) =>
        ActionsOf(await db.CatClips.Where(c => c.IsActive && !c.IsRetired).ToListAsync(ct));

    /// <summary>
    /// Every compilation clips are allowed to come from, from COMPILATION_PATHS (semicolon
    /// separated), falling back to the single DEFAULT_COMPILATION_PATH.
    ///
    /// This used to be one path, and reanalyze retired anything cut from a different file.
    /// That guaranteed a single-source library but capped variety at whatever one video
    /// contained: the 3:20 source yielded 11 usable clips, one per action tag, which is not
    /// enough to cut between shots inside a single reel. Several approved sources still give
    /// the same protection - a file that is not on the list is retired - without the cap.
    /// </summary>
    private HashSet<string> AllowedSourcePaths()
    {
        var raw = cfg["COMPILATION_PATHS"];
        if (string.IsNullOrWhiteSpace(raw)) raw = cfg["DEFAULT_COMPILATION_PATH"];

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(raw)) return set;

        foreach (var part in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try { set.Add(Path.GetFullPath(part)); }
            catch (ArgumentException) { log.LogWarning("Ignoring malformed compilation path: {Path}", part); }
        }
        return set;
    }

    private const double MinClipSec = 1.6;
    private const double MaxClipSec = 12.0;

    /// <summary>
    /// Scene-splits a compilation, then analyses and registers every usable segment.
    /// </summary>
    public async Task<ImportResult> ImportCompilationAsync(
        string sourceFile, bool reset = false, CancellationToken ct = default)
    {
        if (!File.Exists(sourceFile))
            return new ImportResult(0, 0, $"Source not found: {sourceFile}");

        var full = Path.GetFullPath(sourceFile);

        if (reset)
        {
            // Clips still referenced by a rendered video are kept; the rest are removed so a
            // re-import after tuning does not leave the previous attempt's segments behind.
            var previous = await db.CatClips.Where(c => c.SourceFile == full).ToListAsync(ct);
            var referenced = await db.Videos
                .Where(v => v.CatAClipId != null || v.CatBClipId != null)
                .Select(v => new { v.CatAClipId, v.CatBClipId })
                .ToListAsync(ct);
            var inUse = referenced.SelectMany(r => new[] { r.CatAClipId, r.CatBClipId })
                .Where(id => id.HasValue).Select(id => id!.Value).ToHashSet();

            foreach (var c in previous)
            {
                // A clip a rendered video still points at cannot be deleted, so it is
                // retired instead - permanently, so reanalyze will not revive it.
                if (inUse.Contains(c.Id))
                {
                    c.IsActive = false;
                    c.IsRetired = true;
                    c.DisabledReason = "superseded by re-import";
                    continue;
                }
                FFmpegService.TryDelete(paths.CatClip(c.Filename));
                db.CatClips.Remove(c);
            }
            await db.SaveChangesAsync(ct);
            log.LogInformation("Reset: cleared {N} previously imported clips for this source", previous.Count);
        }

        var info = await ff.ProbeAsync(sourceFile, ct);
        if (info.DurationMs <= 0)
            return new ImportResult(0, 0, "Could not read duration from source file.");

        var totalSec = info.DurationMs / 1000.0;
        log.LogInformation("Scene-splitting {File} ({Sec:0.0}s)...", Path.GetFileName(sourceFile), totalSec);

        var cuts = await ff.DetectSceneCutsAsync(sourceFile, totalSec, ct);
        var bounds = BuildSegments(cuts, totalSec);
        log.LogInformation("Found {Cuts} scene cuts -> {Segs} candidate segments", cuts.Count, bounds.Count);

        // Filenames carry a per-run token. Without it a re-import collides with the previous
        // run's names and silently skips those segments, quietly shrinking the library.
        var stem = $"{SanitizeStem(Path.GetFileNameWithoutExtension(sourceFile))}_{DateTime.UtcNow:HHmmss}";
        int imported = 0, skipped = 0;

        foreach (var (start, dur) in bounds)
        {
            ct.ThrowIfCancellationRequested();

            var filename = $"{stem}_{imported + skipped:D3}.mp4";
            var dest = paths.CatClip(filename);

            if (await db.CatClips.AnyAsync(c => c.Filename == filename, ct)) { skipped++; continue; }

            if (!await ff.ExtractSegmentAsync(sourceFile, dest, start, dur, ct))
            {
                skipped++;
                continue;
            }

            var analysis = await ff.AnalyzeClipAsync(dest, ct);

            // Nothing in the frame is shaped like a cat: an all-green gap, a transition, or a
            // segment whose only content is a burned-in overlay.
            if (!analysis.SubjectFound)
            {
                log.LogDebug("Dropping segment at {Start:0.0}s: no plausible subject", start);
                FFmpegService.TryDelete(dest);
                skipped++;
                continue;
            }

            // A segment with no green border is a title card or transition, not a usable cat.
            if (analysis.KeyColor == "0x00FF00" && analysis.CropFilter is null && !await LooksKeyableAsync(dest, ct))
            {
                log.LogDebug("Dropping non-keyable segment at {Start:0.0}s", start);
                FFmpegService.TryDelete(dest);
                skipped++;
                continue;
            }

            // Tune the chroma similarity against this clip's own imperfections.
            var tunedAt = analysis.DurationMs > 2000 ? 1.0 : 0.2;
            var tunedSim = await ff.TuneChromaSimilarityAsync(dest, analysis.CropFilter, analysis.KeyColor, tunedAt, ct);

            db.CatClips.Add(new CatClip
            {
                Filename = filename,
                SourceFile = Path.GetFullPath(sourceFile),
                DurationMs = analysis.DurationMs > 0 ? analysis.DurationMs : (int)(dur * 1000),
                MoodTags = InferMoodTags(dur),
                Facing = "neutral",
                KeyColor = analysis.KeyColor,
                // Stored as the FINAL chroma similarity the renderer should use.
                KeySimilarity = tunedSim,
                CropFilter = analysis.CropFilter,
                SourceWidth = analysis.Width,
                SourceHeight = analysis.Height,
                SubjectFill = Math.Round(analysis.SubjectFill, 4),
                IsActive = true,
            });
            imported++;

            if (imported % 20 == 0)
            {
                await db.SaveChangesAsync(ct);
                log.LogInformation("Imported {N} clips so far...", imported);
            }
        }

        await db.SaveChangesAsync(ct);
        log.LogInformation("Clip import complete: {Imported} imported, {Skipped} skipped", imported, skipped);
        return new ImportResult(imported, skipped, null);
    }

    /// <summary>Confirms a green border really exists before keeping a segment.</summary>
    private async Task<bool> LooksKeyableAsync(string file, CancellationToken ct)
    {
        var (key, _) = await ff.SampleKeyColorAsync(file, null, 0.4, ct);
        return key != "0x00FF00" || await HasGreenAsync(file, ct);
    }

    private async Task<bool> HasGreenAsync(string file, CancellationToken ct)
    {
        var (key, sim) = await ff.SampleKeyColorAsync(file, null, 0.9, ct);
        return sim > 0.18 && key != "0x00FF00";
    }

    /// <summary>Converts scene-cut timestamps into [start,duration) windows within limits.</summary>
    internal static List<(double Start, double Duration)> BuildSegments(List<double> cuts, double totalSec)
    {
        var points = new List<double> { 0 };
        points.AddRange(cuts.Where(c => c > 0.5 && c < totalSec - 0.5));
        points.Add(totalSec);

        var result = new List<(double, double)>();
        for (var i = 0; i < points.Count - 1; i++)
        {
            var start = points[i];
            var dur = points[i + 1] - start;
            if (dur < MinClipSec) continue;          // too short to be a usable clip

            // Split anything unusually long into even sub-clips rather than discarding it.
            if (dur > MaxClipSec)
            {
                var n = (int)Math.Ceiling(dur / MaxClipSec);
                var each = dur / n;
                for (var k = 0; k < n; k++) result.Add((start + k * each, each));
            }
            else
            {
                result.Add((start, dur));
            }
        }
        return result;
    }

    /// <summary>
    /// Share of the frame border that may be a non-keyable backdrop.
    ///
    /// Set from the measured distribution (P50 0.002, P75 0.066, P90 0.167): clips that
    /// are not green screen at all cluster at and above P90, and are the same ones import
    /// logs as "no dominant green border" before falling back to a default key. 0.15 sits
    /// just under that cluster and keeps 78 of 97 clips.
    /// </summary>
    public const double BackdropLimit = 0.15;

    /// <summary>Share of the subject that may be trapped letterbox black.</summary>
    public const double TrappedBarLimit = 0.015;

    /// <summary>Share of the padded margin around the cat that may be non-green.</summary>
    public const double SubjectRingLimit = 0.40;

    /// <summary>
    /// True only for the reasons reanalyze writes itself.
    ///
    /// A later run may undo its own verdicts, but it must NEVER re-enable a clip a person
    /// disabled from the contact sheet. The metrics measure keying, not content, and are
    /// blind to everything that pass judges by eye: a husky, a goat, a cat in a banana
    /// costume and a near-duplicate all score as perfectly clean cut-outs. Re-enabling them
    /// silently overrode a human decision and put 52 rejected clips back in circulation.
    /// </summary>
    private static bool IsMetricReason(string? reason) =>
        reason is not null &&
        (reason.StartsWith("backdrop is not green", StringComparison.Ordinal) ||
         reason.StartsWith("black bars inside subject", StringComparison.Ordinal) ||
         reason.StartsWith("not a clean cut-out", StringComparison.Ordinal));

    /// <summary>
    /// Re-measures every clip and disables the ones whose bars would show through.
    /// Runs without re-extracting, so it is cheap to re-run after tuning the limit.
    /// </summary>
    public async Task<object> ReanalyzeAsync(bool apply, CancellationToken ct = default)
    {
        var clips = await db.CatClips.ToListAsync(ct);
        var measured = new List<(CatClip Clip, FFmpegService.Contamination Score)>();

        var allowed = AllowedSourcePaths();
        var retired = 0;

        foreach (var c in clips)
        {
            ct.ThrowIfCancellationRequested();

            // Retirement is permanent: a clip from a superseded compilation, or one replaced
            // by a later re-import, must never be revived by a quality re-measurement.
            var fromOtherSource = allowed.Count > 0 &&
                !allowed.Contains(c.SourceFile ?? "");

            if (c.IsRetired || fromOtherSource)
            {
                if (apply)
                {
                    c.IsActive = false;
                    c.IsRetired = true;
                    c.DisabledReason ??= "retired: cut from a previous source video";
                }
                retired++;
                continue;
            }

            var path = paths.CatClip(c.Filename);
            if (!File.Exists(path)) continue;

            var at = c.DurationMs > 2000 ? 1.0 : 0.2;
            var score = await ff.MeasureContaminationAsync(path, c.CropFilter, c.KeyColor, at, ct);
            c.BarResidue = Math.Round(score.Worst, 4);
            measured.Add((c, score));
        }

        var disabled = 0;
        if (apply)
        {
            foreach (var (clip, score) in measured)
            {
                var reason =
                    score.Backdrop > BackdropLimit ? $"backdrop is not green ({score.Backdrop:P0} of border survives)"
                    : score.TrappedBars > TrappedBarLimit ? $"black bars inside subject ({score.TrappedBars:P0})"
                    : score.SubjectRing > SubjectRingLimit ? $"not a clean cut-out ({score.SubjectRing:P0} of the margin around the cat is not green)"
                    : null;

                if (reason is not null)
                {
                    clip.IsActive = false;
                    clip.DisabledReason = reason;
                    disabled++;
                }
                else if (IsMetricReason(clip.DisabledReason))
                {
                    // Re-enable only what a previous, stricter run of THIS check disabled.
                    clip.IsActive = true;
                    clip.DisabledReason = null;
                }
            }
        }

        await db.SaveChangesAsync(ct);

        double Pct(IEnumerable<double> xs, double p)
        {
            var s = xs.OrderBy(x => x).ToList();
            return s.Count == 0 ? 0 : Math.Round(s[Math.Min(s.Count - 1, (int)(s.Count * p))], 4);
        }

        var backdrops = measured.Select(m => m.Score.Backdrop).ToList();
        var bars = measured.Select(m => m.Score.TrappedBars).ToList();
        var rings = measured.Select(m => m.Score.SubjectRing).ToList();

        return new
        {
            Measured = measured.Count,
            Disabled = disabled,
            Retired = retired,
            Sources = allowed.ToArray(),
            BackdropLimit,
            TrappedBarLimit,
            SubjectRingLimit,
            Backdrop = new { P50 = Pct(backdrops, 0.5), P75 = Pct(backdrops, 0.75), P90 = Pct(backdrops, 0.9) },
            TrappedBars = new { P50 = Pct(bars, 0.5), P75 = Pct(bars, 0.75), P90 = Pct(bars, 0.9) },
            SubjectRing = new { P50 = Pct(rings, 0.5), P75 = Pct(rings, 0.75), P90 = Pct(rings, 0.9) },
            Worst = measured.OrderByDescending(m => m.Score.Worst).Take(10)
                .Select(m => new
                {
                    m.Clip.Filename,
                    Backdrop = Math.Round(m.Score.Backdrop, 4),
                    TrappedBars = Math.Round(m.Score.TrappedBars, 4),
                    SubjectRing = Math.Round(m.Score.SubjectRing, 4),
                }),
        };
    }

    private static string InferMoodTags(double durSec) => durSec switch
    {
        < 3.0 => "funny,energetic",
        < 6.0 => "funny,wholesome",
        _ => "dramatic,wholesome",
    };

    private static string SanitizeStem(string s)
    {
        var cleaned = new string(s.Where(c => char.IsLetterOrDigit(c) || c is '_' or '-').ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "clip" : cleaned.ToLowerInvariant()[..Math.Min(24, cleaned.Length)];
    }

    /// <summary>
    /// Casts BOTH cats in one pass, so the whole script is satisfied or none of it is.
    ///
    /// Casting them separately - cat A first, then cat B from whatever is left - strands
    /// cat B whenever the one clip it needs was also a candidate for cat A. That is the
    /// common case, not an edge case: nearly every action tag in the curated library is
    /// backed by a single clip, so greedily taking one for cat A can leave cat B with
    /// nothing while a perfectly good pairing existed.
    ///
    /// The requested actions stay HARD, never a preference: an action with no matching
    /// clip refuses instead of degrading. A silent fallback here is how a yelling cat once
    /// got cast as the character who "goes quiet", and matching on "mood" inferred from
    /// clip duration is how a gagging cat fronted a hoodie-borrowing joke. The action IS
    /// part of the punchline.
    /// </summary>
    /// <param name="actionB">Null or empty for a one-cat layout.</param>
    /// <param name="exclude">
    /// Clips this render has already ruled out - anything preflight rejected on an
    /// earlier attempt.
    /// </param>
    public async Task<CastingResult> CastAsync(
        string actionA, string? actionB, int minDurationMs,
        IReadOnlyCollection<int>? exclude = null, CancellationToken ct = default)
        => await CastCoreAsync(actionA, actionB, minDurationMs, exclude, null, ct);

    /// <summary>
    /// Casts a complete topic with the role, dialogue and mood available to the ranker.
    ///
    /// The action remains a hard constraint. Context only decides WHICH clip performing
    /// that action fits the character: for example, an innocent cat tagged "caught" is a
    /// better response to an accusation, while an asking cat tagged "pleading" is a better
    /// fit for "please wake me". This is deliberately separate from action validation so
    /// semantic ranking can never turn an incompatible cat into an eligible one.
    /// </summary>
    public Task<CastingResult> CastAsync(
        Topic topic, int minDurationMs,
        IReadOnlyCollection<int>? exclude = null, CancellationToken ct = default)
    {
        var actionB = topic.Layout == Layout.SinglePov ? null : topic.CatBAction;
        return CastCoreAsync(topic.CatAAction ?? "", actionB, minDurationMs, exclude, topic, ct);
    }

    /// <summary>
    /// Casts one visual beat while retaining the parent topic as semantic context. Beat
    /// actions may differ from the topic's default pair, which is how a conversation can
    /// move from asking/attentive to sleeping/sleeping to angry/innocent.
    /// </summary>
    public Task<CastingResult> CastBeatAsync(
        Topic topic, string actionA, string? actionB, int minDurationMs,
        IReadOnlyCollection<int>? exclude = null, CancellationToken ct = default) =>
        CastCoreAsync(actionA, actionB, minDurationMs, exclude, topic, ct);

    /// <summary>
    /// Casts only the roles that are visible in a visual beat. A B-only beat must not
    /// reserve an invisible A clip (or fail because that unnecessary pair cannot be
    /// formed), and its semantic ranking must still use B's role and dialogue context.
    /// Exact action-tag eligibility remains the gate for every visible role.
    /// </summary>
    public Task<CastingResult> CastBeatAsync(
        Topic topic, string actionA, string? actionB, bool showA, bool showB,
        int minDurationMs, IReadOnlyCollection<int>? exclude = null,
        CancellationToken ct = default) =>
        CastCoreAsync(actionA, actionB, minDurationMs, exclude, topic, ct, showA, showB);

    private async Task<CastingResult> CastCoreAsync(
        string actionA, string? actionB, int minDurationMs,
        IReadOnlyCollection<int>? exclude, Topic? topic, CancellationToken ct,
        bool showA = true, bool? showB = null)
    {
        var castB = showB ?? !string.IsNullOrWhiteSpace(actionB);
        if (!showA && !castB)
            return new CastingResult(null, null, "a visual beat must show at least one cat");

        var skip = exclude is null || exclude.Count == 0 ? null : exclude.ToHashSet();

        var pool = await db.CatClips
            .Where(c => c.IsActive && !c.IsRetired)
            .ToListAsync(ct);
        if (skip is not null) pool = pool.Where(c => !skip.Contains(c.Id)).ToList();
        if (pool.Count == 0)
            return new CastingResult(null, null, "the clip library has no active clips left to cast");

        // How recently each clip was used, as a video id (higher = more recent).
        var lastUsed = await db.Videos
            .Where(v => v.CatAClipId != null || v.CatBClipId != null)
            .Select(v => new { v.Id, v.CatAClipId, v.CatBClipId })
            .ToListAsync(ct);

        var recency = new Dictionary<int, int>();
        foreach (var v in lastUsed)
        {
            foreach (var id in new[] { v.CatAClipId, v.CatBClipId })
            {
                if (id is not int clipId) continue;
                recency[clipId] = Math.Max(recency.GetValueOrDefault(clipId), v.Id);
            }
        }

        List<RankedClip> Rank(IEnumerable<CatClip> candidates, string action, bool isA) => candidates
            .Select(c => new RankedClip(
                c,
                SemanticScore(c, action, topic, isA),
                FacingScore(c, topic, isA),
                c.DurationMs >= minDurationMs,
                recency.GetValueOrDefault(c.Id, 0),
                StableTieBreak(topic, action, isA, c.Id)))
            // Appropriateness is deliberately ahead of freshness. The old order made a
            // recently used perfect reaction lose to an unrelated cat merely for novelty.
            // Appropriateness is BANDED, not exact. Ranking on the raw semantic score put
            // freshness dead last, so whichever clip scored highest for a common action won
            // every time: clip 340 (staring,grumpy,angry) ended up cast in 15 of 37 queued
            // videos - roughly 40% of output from a 44-clip library. Collapsing near-equal
            // fits into one band lets recency actually break the tie, so comparably suitable
            // cats take turns instead of one monopolising the channel.
            .OrderByDescending(c => SemanticBand(c.SemanticScore))
            .ThenByDescending(c => c.FacingScore)
            .ThenBy(c => c.LastUsedVideoId)
            .ThenByDescending(c => c.DurationFit)
            .ThenByDescending(c => c.Clip.DurationMs)
            .ThenBy(c => c.Clip.BarResidue)
            // Topic-derived, so different topics still spread across clips that tie on
            // everything above, and re-rendering the SAME topic stays exact.
            .ThenBy(c => c.StableTieBreak)
            .ToList();

        List<CatClip> Performing(string action)
        {
            var wanted = action.Trim();
            return pool.Where(c => TagsOf(c.MoodTags).Contains(wanted)).ToList();
        }

        List<RankedClip>? rankedA = null;
        if (showA)
        {
            rankedA = Rank(string.IsNullOrWhiteSpace(actionA) ? pool : Performing(actionA), actionA, true);
            if (rankedA.Count == 0)
                return new CastingResult(null, null,
                    $"no active clip performs '{actionA}'. Available actions: {ActionsOf(pool)}");
        }

        List<RankedClip>? rankedB = null;
        if (castB)
        {
            if (string.IsNullOrWhiteSpace(actionB))
                return new CastingResult(null, null,
                    "a visible cat B needs an action to cast");

            rankedB = Rank(Performing(actionB), actionB, false);
            if (rankedB.Count == 0)
                return new CastingResult(null, null,
                    $"no active clip performs '{actionB}'. Available actions: {ActionsOf(pool)}");
        }

        if (!showA)
            return new CastingResult(null, rankedB![0].Clip, null);

        if (!castB)
            return new CastingResult(rankedA![0].Clip, null, null);

        var pairA = rankedA!;
        var pairB = rankedB!;
        var pairActionB = actionB!;

        // Score the PAIR, not A and then whatever remains for B. Apart from avoiding the
        // same clip twice, this lets a strong role fit on B win even when A has several
        // interchangeable choices. Every tie breaker is stable; identical inputs produce
        // the same cast instead of rolling Random.Shared and visibly changing characters.
        var mayDuplicateSleeping = actionA.Equals("sleeping", StringComparison.OrdinalIgnoreCase) &&
                                   pairActionB.Equals("sleeping", StringComparison.OrdinalIgnoreCase);

        var pairs = (from a in pairA
                     from b in pairB
                     where a.Clip.Id != b.Clip.Id || mayDuplicateSleeping
                     select new
                     {
                         A = a,
                         B = b,
                         Semantic = SemanticBand(a.SemanticScore) + SemanticBand(b.SemanticScore),
                         Facing = a.FacingScore + b.FacingScore,
                         DurationFits = (a.DurationFit ? 1 : 0) + (b.DurationFit ? 1 : 0),
                         ShortestDuration = Math.Min(a.Clip.DurationMs, b.Clip.DurationMs),
                         WorstRecency = Math.Max(a.LastUsedVideoId, b.LastUsedVideoId),
                         TotalRecency = a.LastUsedVideoId + b.LastUsedVideoId,
                         Stable = a.StableTieBreak ^ RotateLeft(b.StableTieBreak, 1),
                     })
            // Same banding as the single-clip ranking: once a pair is comparably appropriate,
            // the freshest pair wins. Recency ahead of the stable key is what stops one strong
            // pair from being re-cast batch after batch.
            .OrderByDescending(p => p.Semantic)
            .ThenByDescending(p => p.Facing)
            .ThenBy(p => p.WorstRecency)
            .ThenBy(p => p.TotalRecency)
            .ThenByDescending(p => p.DurationFits)
            .ThenByDescending(p => p.ShortestDuration)
            .ThenBy(p => p.Stable)
            .ToList();

        if (pairs.Count == 0)
            return new CastingResult(null, null,
                $"'{actionA}' and '{pairActionB}' are performed by the same single clip " +
                $"({pairB[0].Clip.Filename}), so they cannot play two different cats. " +
                $"Tag another clip with one of them, or rewrite the script to use two " +
                $"distinct actions. Available actions: {ActionsOf(pool)}");

        var pick = pairs[0];
        log.LogDebug(
            "Cast '{Topic}': A={AId} {ATags} (semantic {AScore}), B={BId} {BTags} (semantic {BScore})",
            topic?.TopicText ?? "action-only feasibility check",
            pick.A.Clip.Id, pick.A.Clip.MoodTags, pick.A.SemanticScore,
            pick.B.Clip.Id, pick.B.Clip.MoodTags, pick.B.SemanticScore);
        return new CastingResult(pick.A.Clip, pick.B.Clip, null);
    }

    private sealed record RankedClip(
        CatClip Clip,
        int SemanticScore,
        int FacingScore,
        bool DurationFit,
        int LastUsedVideoId,
        ulong StableTieBreak);

    /// <summary>
    /// Scores descriptive secondary tags against one role's words and the overall scene.
    /// Action eligibility is checked separately and is never softened by this score.
    ///
    /// MoodTags is intentionally treated as a small vocabulary rather than free-text
    /// substring matching. A clip tagged "caught" must not accidentally match a future
    /// action named "cat", and semantic tags can safely coexist with action tags.
    /// </summary>
    /// <summary>
    /// Width of one appropriateness band. Semantic scores inside the same band count as
    /// equally suitable, which hands the decision to recency instead of letting a one-point
    /// scoring edge win the same clip forever.
    /// </summary>
    private const int SemanticBandSize = 3;

    /// <summary>
    /// Collapses a raw semantic score into a coarse suitability band. Negative scores (an
    /// actively wrong cat) stay in their own band below zero so they can never be promoted
    /// over a genuine fit just for being fresh.
    /// </summary>
    internal static int SemanticBand(int score) =>
        score < 0
            ? (score - SemanticBandSize + 1) / SemanticBandSize
            : score / SemanticBandSize;

    internal static int SemanticScore(CatClip clip, string action, Topic? topic, bool isA)
    {
        if (topic is null) return 0;

        var tags = TagsOf(clip.MoodTags);
        var score = 0;
        void Reward(int points, params string[] wanted)
        {
            foreach (var tag in wanted)
                if (tags.Contains(tag)) score += points;
        }
        void Avoid(int points, params string[] unwanted)
        {
            foreach (var tag in unwanted)
                if (tags.Contains(tag)) score -= points;
        }

        var title = topic.TopicText.ToLowerInvariant();
        var label = (isA ? topic.CatALabel : topic.CatBLabel).ToLowerInvariant();
        var rawSpoken = string.Join(' ', topic.Dialogue()
            .Where(d => d.IsA == isA)
            .Select(d => d.Text));
        var spoken = rawSpoken.ToLowerInvariant();
        var sceneDialogue = string.Join(' ', topic.Dialogue().Select(d => d.Text)).ToLowerInvariant();
        var roleText = $"{label} {spoken}";
        var sceneText = $"{title} {sceneDialogue}";
        var normalizedAction = action.Trim().ToLowerInvariant();

        // Fine-grained variants of the same visible action. These are deliberately modest;
        // explicit words below carry more weight than a generic action prior.
        switch (normalizedAction)
        {
            case "asking": Reward(5, "pleading", "hopeful"); break;
            case "begging": Reward(6, "pleading"); break;
            case "dancing": Reward(5, "happy", "celebrating"); break;
            case "eating": Reward(4, "hungry"); break;
            case "gagging": Reward(5, "disgusted"); break;
            case "silly": Reward(5, "celebrating", "happy", "goofy"); break;
            case "angry": Reward(4, "grumpy", "complaining", "offended", "yelling"); break;
            case "vibing": Reward(3, "calm", "relaxed"); break;
            case "shocked": Reward(4, "surprised"); break;
            case "sleeping": Reward(8, "sleepy", "snoring", "lying-down", "tired"); break;
        }

        if (ContainsAny(sceneText, "food", "snack", "pizza", "breakfast", "dinner", "lunch", "eat", "hungry"))
            Reward(12, "hungry");

        if (ContainsAny(sceneText, "ignore", "ignoring", "no reply", "left on read", "doesn't answer", "doesnt answer"))
            Reward(13, "ignoring");

        if (ContainsAny(sceneText, "sleep", "asleep", "bed", "blanket", "alarm", "wake", "waking", "am tomorrow", "a.m. tomorrow"))
            Reward(12, "sleeping", "sleepy", "tired", "drowsy", "lying-down", "calm");

        if (ContainsAny(roleText, "please", "can you", "could you", "would you", "i need", "i want") ||
            (spoken.Contains('?') && normalizedAction is "asking" or "begging"))
            Reward(12, "pleading", "hopeful");

        if (ContainsAny(roleText, "why", "didn't", "didnt", "never", "seriously", "mad", "angry", "hate", "stop") ||
            spoken.Count(c => c == '!') >= 1 || HasShouting(rawSpoken))
        {
            Reward(12, "complaining", "grumpy", "offended", "yelling");
            Avoid(5, "happy", "celebrating", "calm");
        }

        if (ContainsAny(sceneText, "caught", "blame", "my fault", "you did", "you ate", "you stole", "why did", "why didn't", "why didnt"))
        {
            if (normalizedAction is "innocent" or "shocked") Reward(11, "caught", "confused", "nervous");
            if (normalizedAction is "angry" or "talking" or "staring") Reward(8, "complaining", "grumpy", "offended");
        }

        if (ContainsAny(sceneText, "win", "won", "finally", "best", "yes", "lets go", "let's go", "celebrate"))
            Reward(9, "happy", "celebrating");

        if (ContainsAny(roleText, "gross", "ew", "disgust", "nasty", "smells"))
            Reward(12, "disgusted");

        if (topic.Mood.Equals("wholesome", StringComparison.OrdinalIgnoreCase))
            Reward(3, "happy", "calm", "gentle");
        else if (topic.Mood.Equals("dramatic", StringComparison.OrdinalIgnoreCase))
            Reward(3, "offended", "grumpy", "complaining", "nervous");

        return score;
    }

    /// <summary>Rewards cats that look toward their scene partner, when facing is curated.</summary>
    private static int FacingScore(CatClip clip, Topic? topic, bool isA)
    {
        if (topic is null || clip.Facing.Equals("neutral", StringComparison.OrdinalIgnoreCase)) return 0;

        var position = isA ? topic.CatAPosition : topic.CatBPosition;
        var wanted = position.Equals("left", StringComparison.OrdinalIgnoreCase) ? "right"
            : position.Equals("right", StringComparison.OrdinalIgnoreCase) ? "left"
            : null;
        if (wanted is null) return 0;
        return clip.Facing.Equals(wanted, StringComparison.OrdinalIgnoreCase) ? 2 : -2;
    }

    internal static HashSet<string> TagsOf(string? raw) => new(
        (raw ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.ToLowerInvariant()),
        StringComparer.OrdinalIgnoreCase);

    private static bool ContainsAny(string text, params string[] needles) =>
        needles.Any(text.Contains);

    private static bool HasShouting(string text)
    {
        var letters = text.Where(char.IsLetter).ToArray();
        return letters.Length >= 5 && letters.Count(char.IsUpper) >= letters.Length * 0.75;
    }

    /// <summary>Process-stable FNV-1a key; string.GetHashCode is randomised per process.</summary>
    private static ulong StableTieBreak(Topic? topic, string action, bool isA, int clipId)
    {
        var input = $"{topic?.TopicText}|{(isA ? topic?.CatALabel : topic?.CatBLabel)}|{action}|{clipId}";
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var hash = offset;
        foreach (var c in input.ToLowerInvariant())
        {
            hash ^= c;
            hash *= prime;
        }
        return hash;
    }

    private static ulong RotateLeft(ulong value, int count) =>
        (value << count) | (value >> (64 - count));

    /// <summary>
    /// Returns the cast a video already has, so a re-render reproduces it exactly.
    ///
    /// Null means "cast fresh": no cast recorded, a clip has since been disabled or deleted,
    /// or preflight ruled one out on an earlier attempt. Reproducibility must never resurrect
    /// a clip the library has since rejected.
    /// </summary>
    public async Task<(CatClip A, CatClip? B)?> PinnedCastAsync(
        int? clipAId, int? clipBId, IReadOnlyCollection<int>? exclude = null, CancellationToken ct = default)
    {
        if (clipAId is not int a) return null;

        var skip = exclude is null || exclude.Count == 0 ? null : exclude.ToHashSet();
        if (skip is not null && (skip.Contains(a) || (clipBId is int bx && skip.Contains(bx)))) return null;

        var wanted = clipBId is int b ? new[] { a, b } : [a];
        var found = await db.CatClips
            .Where(c => wanted.Contains(c.Id) && c.IsActive && !c.IsRetired)
            .ToListAsync(ct);

        var pinnedA = found.FirstOrDefault(c => c.Id == a);
        if (pinnedA is null) return null;

        if (clipBId is not int wantB) return (pinnedA, null);

        var pinnedB = found.FirstOrDefault(c => c.Id == wantB);
        return pinnedB is null ? null : (pinnedA, pinnedB);
    }

    /// <summary>Distinct action tags across a set of clips, for casting error messages.</summary>
    private static string ActionsOf(IEnumerable<CatClip> clips) => string.Join(", ", clips
        .SelectMany(c => TagsOf(c.MoodTags))
        .Where(t => ClaudeCliService.ClipActions.Contains(t, StringComparer.OrdinalIgnoreCase))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(x => x));
}
