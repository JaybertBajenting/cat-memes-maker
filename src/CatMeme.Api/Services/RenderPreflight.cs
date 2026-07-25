using System.Text.RegularExpressions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace CatMeme.Api.Services;

/// <summary>
/// Which render input a failure belongs to, so the caller knows what to replace before
/// trying again.
/// </summary>
public enum PreflightFault
{
    Background,
    ClipA,
    ClipB,
    Interaction,
    Duration,
}

public record PreflightIssue(
    PreflightFault Fault,
    string Message,
    int? ClipId = null,
    int? InteractionAssetId = null);

public record PreflightReport(IReadOnlyList<PreflightIssue> Issues, int EffectiveDurationMs)
{
    public bool Ok => Issues.Count == 0;
    public bool Faulted(PreflightFault fault) => Issues.Any(i => i.Fault == fault);
    public string Summary => string.Join(" | ", Issues.Select(i => i.Message));
    public IReadOnlyList<int> RecastClipIds => Issues
        .Where(i => (i.Fault is PreflightFault.ClipA or PreflightFault.ClipB) &&
                    i.ClipId is not null)
        .Select(i => i.ClipId!.Value)
        .Distinct()
        .OrderBy(x => x)
        .ToList();
    public IReadOnlyList<int> RecastInteractionAssetIds => Issues
        .Where(i => i.Fault == PreflightFault.Interaction && i.InteractionAssetId is not null)
        .Select(i => i.InteractionAssetId!.Value)
        .Distinct()
        .OrderBy(x => x)
        .ToList();
}

/// <summary>
/// Validates a <see cref="RenderRequest"/> against the files and values it actually names,
/// immediately before ffmpeg is launched.
///
/// <see cref="ScriptValidator"/> gates what a reel SAYS; this gates what it is MADE OF.
/// Every check here covers a case where ffmpeg would either burn its timeout on an input
/// that was never going to work, or exit 0 having produced a wrong frame:
///
/// - <c>KeyColor</c> and <c>CropFilter</c> are interpolated raw into the filter_complex
///   string, so a malformed value rewrites the graph rather than erroring cleanly.
/// - A crop is measured at import but applied to whatever file carries that name at render
///   time; a re-import can leave it pointing outside the frame.
/// - The renderer clamps the reel to [<see cref="RenderService.MinDurationMs"/>,
///   <see cref="RenderService.MaxDurationMs"/>] while the verifier compares the result
///   against the raw target, so an out-of-range setting fails every video forever.
///
/// It takes the RenderRequest itself rather than loose arguments so that what is checked
/// and what is rendered cannot drift apart.
/// </summary>
public partial class RenderPreflight(FFmpegService ff, ILogger<RenderPreflight> log)
{
    /// <summary>An ffmpeg colour literal, in the form chromakey needs.</summary>
    [GeneratedRegex(@"^0x[0-9A-Fa-f]{6}$")]
    private static partial Regex KeyColorRx();

    /// <summary>w:h:x:y and nothing else - no room to smuggle a second filter into the graph.</summary>
    [GeneratedRegex(@"^(\d{1,5}):(\d{1,5}):(\d{1,5}):(\d{1,5})$")]
    private static partial Regex CropFilterRx();

    /// <summary>Below this a "background" is a failed download, not a plate.</summary>
    private const long MinBackgroundBytes = 4_096;

    /// <summary>A real keyed clip runs far larger; this only catches truncated stubs.</summary>
    private const long MinClipBytes = 16_384;

    public async Task<PreflightReport> CheckAsync(RenderRequest req, CancellationToken ct = default)
    {
        var issues = new List<PreflightIssue>();

        await CheckBackgroundAsync(req.BackgroundPath, issues, ct);
        if (req.VisualBeats is { Count: > 0 })
        {
            var seen = new HashSet<(PreflightFault Fault, int ClipId, string Path)>();
            foreach (var beat in req.VisualBeats)
            {
                foreach (var (fault, cat) in new[]
                         {
                             (PreflightFault.ClipA, beat.CatA),
                             (PreflightFault.ClipB, beat.CatB),
                         })
                {
                    if (cat is null || !seen.Add((fault, cat.ClipId, cat.Path))) continue;
                    await CheckClipAsync(fault, cat.Path, cat.Analysis, issues, ct, cat.ClipId);
                }

                if (beat.Interaction is { } interaction &&
                    seen.Add((PreflightFault.Interaction, interaction.AssetId, interaction.Path)))
                {
                    await CheckClipAsync(
                        PreflightFault.Interaction,
                        interaction.Path,
                        interaction.Analysis,
                        issues,
                        ct,
                        interaction.AssetId,
                        interaction);
                }
            }
        }
        else
        {
            await CheckClipAsync(PreflightFault.ClipA, req.CatAPath, req.CatA, issues, ct);

            if (req.CatBPath is not null && req.CatB is not null)
                await CheckClipAsync(PreflightFault.ClipB, req.CatBPath, req.CatB, issues, ct);
        }

        var effective = Math.Clamp(
            req.TargetDurationMs, RenderService.MinDurationMs, RenderService.MaxDurationMs);

        if (effective != req.TargetDurationMs)
            issues.Add(new PreflightIssue(PreflightFault.Duration,
                $"target duration {req.TargetDurationMs}ms is outside the " +
                $"{RenderService.MinDurationMs}-{RenderService.MaxDurationMs}ms the renderer can honour; " +
                $"{effective}ms is the longest it would actually produce"));

        if (issues.Count > 0)
            log.LogWarning("Preflight blocked video {Id}: {Issues}",
                req.VideoId, string.Join(" | ", issues.Select(i => i.Message)));

        return new PreflightReport(issues, effective);
    }

    /// <summary>
    /// The plate is decoded in full, not just header-identified: the failure this catches
    /// is a half-written PNG, whose header is perfectly valid.
    /// </summary>
    private static async Task CheckBackgroundAsync(string? path, List<PreflightIssue> issues, CancellationToken ct)
    {
        void Fail(string msg) => issues.Add(new PreflightIssue(PreflightFault.Background, $"background: {msg}"));

        if (string.IsNullOrWhiteSpace(path)) { Fail("no plate was produced"); return; }
        if (!File.Exists(path)) { Fail($"file is missing ({Path.GetFileName(path)})"); return; }

        var bytes = new FileInfo(path).Length;
        if (bytes < MinBackgroundBytes) { Fail($"file is only {bytes} bytes"); return; }

        try
        {
            using var img = await Image.LoadAsync<Rgba32>(path, ct);
            if (img.Width != RenderService.CanvasW || img.Height != RenderService.CanvasH)
                Fail($"plate is {img.Width}x{img.Height}, expected " +
                     $"{RenderService.CanvasW}x{RenderService.CanvasH}");
        }
        catch (Exception ex)
        {
            Fail($"file is not a readable image ({ex.GetType().Name})");
        }
    }

    /// <summary>
    /// Checks the clip file exists and decodes, and that the analysis stored against it in
    /// the database still describes THAT file. The stored crop is the load-bearing value:
    /// it is measured once at import and then trusted by every render afterwards.
    /// </summary>
    private async Task CheckClipAsync(
        PreflightFault fault, string path, ClipAnalysis clip, List<PreflightIssue> issues,
        CancellationToken ct, int? inputId = null, RenderInteractionInput? interaction = null)
    {
        var who = fault switch
        {
            PreflightFault.ClipA => "cat A",
            PreflightFault.ClipB => "cat B",
            PreflightFault.Interaction => "paired interaction",
            _ => "clip",
        };
        var name = Path.GetFileName(path);
        void Fail(string msg) => issues.Add(new PreflightIssue(
            fault,
            $"{who} ({name}): {msg}",
            ClipId: fault == PreflightFault.Interaction ? null : inputId,
            InteractionAssetId: fault == PreflightFault.Interaction ? inputId : null));

        if (!File.Exists(path)) { Fail("clip file is missing"); return; }

        var bytes = new FileInfo(path).Length;
        if (bytes < MinClipBytes) { Fail($"clip file is only {bytes} bytes"); return; }

        // These two go into the filtergraph verbatim, so they are checked before anything
        // that costs an ffmpeg call.
        if (!KeyColorRx().IsMatch(clip.KeyColor ?? ""))
            Fail($"key colour '{clip.KeyColor}' is not a 0xRRGGBB literal");

        if (clip.Similarity is <= 0 or > 1 || double.IsNaN(clip.Similarity))
            Fail($"chroma similarity {clip.Similarity} is outside (0,1]");

        var info = await ff.ProbeAsync(path, ct);
        if (info.Width <= 0 || info.Height <= 0) { Fail("file carries no readable video stream"); return; }

        if (interaction is not null)
        {
            foreach (var problem in ValidateInteractionWindow(interaction, info.DurationMs))
                Fail(problem);
        }

        if (clip.CropFilter is null)
        {
            // With no crop the renderer scales the whole frame, so the stored size must be it.
            if (clip.Width != info.Width || clip.Height != info.Height)
                Fail($"stored size {clip.Width}x{clip.Height} does not match the file's {info.Width}x{info.Height}");
            return;
        }

        var m = CropFilterRx().Match(clip.CropFilter);
        if (!m.Success) { Fail($"crop filter '{clip.CropFilter}' is not w:h:x:y"); return; }

        var w = int.Parse(m.Groups[1].Value);
        var h = int.Parse(m.Groups[2].Value);
        var x = int.Parse(m.Groups[3].Value);
        var y = int.Parse(m.Groups[4].Value);

        if (w < 2 || h < 2)
            Fail($"crop {clip.CropFilter} is degenerate");
        else if (x + w > info.Width || y + h > info.Height)
            Fail($"crop {clip.CropFilter} falls outside the file's {info.Width}x{info.Height} frame");
        else if (w != clip.Width || h != clip.Height)
            // Import writes the crop and the subject size together; if they disagree the row
            // is describing a different file than the one on disk.
            Fail($"crop is {w}x{h} but the stored subject size is {clip.Width}x{clip.Height}");
    }

    /// <summary>
    /// Verifies that the approved interaction window is wholly inside the source and agrees
    /// with the usable duration passed to rendering. Kept pure for focused regression tests.
    /// </summary>
    public static IReadOnlyList<string> ValidateInteractionWindow(
        RenderInteractionInput interaction,
        int sourceDurationMs)
    {
        var errors = new List<string>();
        var start = interaction.InPointSec;
        var outPoint = interaction.OutPointSec;
        var declaredUsable = interaction.Analysis.DurationMs / 1000.0;

        if (!double.IsFinite(start) || start < 0)
            errors.Add($"in-point {start} is invalid");
        if (!double.IsFinite(outPoint) || outPoint < 0)
            errors.Add($"out-point {outPoint} is invalid");
        if (!double.IsFinite(declaredUsable) || declaredUsable <= 0)
            errors.Add($"usable duration {declaredUsable:0.###}s is invalid");
        if (errors.Count > 0) return errors;

        var effectiveOut = outPoint > 0 ? outPoint : start + declaredUsable;
        if (effectiveOut <= start)
            errors.Add($"out-point {effectiveOut:0.###}s is not after in-point {start:0.###}s");

        var sourceDuration = Math.Max(0, sourceDurationMs / 1000.0);
        const double probeToleranceSec = 0.12;
        if (start >= sourceDuration + probeToleranceSec)
            errors.Add($"in-point {start:0.###}s falls outside the {sourceDuration:0.###}s source");
        if (effectiveOut > sourceDuration + probeToleranceSec)
            errors.Add($"out-point {effectiveOut:0.###}s exceeds the {sourceDuration:0.###}s source");

        var windowDuration = effectiveOut - start;
        if (windowDuration > 0 && Math.Abs(windowDuration - declaredUsable) > probeToleranceSec)
            errors.Add(
                $"curated window is {windowDuration:0.###}s but render metadata declares " +
                $"{declaredUsable:0.###}s usable");

        return errors;
    }
}
