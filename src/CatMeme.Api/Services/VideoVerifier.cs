using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace CatMeme.Api.Services;

public record VerifyReport(bool Ok, List<string> Errors, List<string> Warnings, double Sharpness)
{
    public string Summary =>
        Errors.Count > 0 ? string.Join(" | ", Errors)
        : Warnings.Count > 0 ? string.Join(" | ", Warnings)
        : "ok";
}

/// <summary>
/// Checks a finished reel before it is offered for approval. Catches the failure modes
/// that still produce a valid mp4 - wrong canvas, missing audio track, a subject upscaled
/// into mush - which a plain "did ffmpeg exit 0" check happily lets through.
/// </summary>
public class VideoVerifier(FFmpegService ff, ProcessRunner runner, PipelinePaths paths, ILogger<VideoVerifier> log)
{
    /// <summary>Laplacian variance below this reads as visibly soft at reel scale.</summary>
    private const double BlurThreshold = 55.0;

    public async Task<VerifyReport> VerifyAsync(string videoPath, int expectedDurationMs, CancellationToken ct = default)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        if (!File.Exists(videoPath))
            return new VerifyReport(false, ["output file does not exist"], warnings, 0);

        var size = new FileInfo(videoPath).Length;
        if (size < 20_000)
            errors.Add($"output is only {size} bytes");

        // ffmpeg's banner carries dimensions, duration and the stream list.
        var probe = await runner.RunAsync(ff.FfmpegPath, ["-hide_banner", "-i", videoPath],
            TimeSpan.FromSeconds(45), ct: ct);
        var text = probe.All;

        if (!text.Contains("Video:")) errors.Add("no video stream");
        if (!text.Contains("Audio:")) errors.Add("no audio stream (Reels require one)");

        var info = await ff.ProbeAsync(videoPath, ct);

        if (info.Width != RenderService.CanvasW || info.Height != RenderService.CanvasH)
            errors.Add($"expected {RenderService.CanvasW}x{RenderService.CanvasH}, got {info.Width}x{info.Height}");

        if (expectedDurationMs > 0 && info.DurationMs > 0)
        {
            var drift = Math.Abs(info.DurationMs - expectedDurationMs) / (double)expectedDurationMs;
            if (drift > 0.15)
                errors.Add($"duration {info.DurationMs}ms differs from target {expectedDurationMs}ms");
        }

        var sharpness = await MeasureSharpnessAsync(videoPath, ct);
        if (sharpness > 0 && sharpness < BlurThreshold)
            warnings.Add($"frame looks soft (sharpness {sharpness:0.0}); a source clip was probably upscaled too far");

        var ok = errors.Count == 0;
        if (!ok) log.LogWarning("Verification failed for {Path}: {Errors}", videoPath, string.Join(" | ", errors));
        else if (warnings.Count > 0) log.LogInformation("Verification warnings for {Path}: {W}", videoPath, string.Join(" | ", warnings));

        return new VerifyReport(ok, errors, warnings, sharpness);
    }

    /// <summary>
    /// Variance of the Laplacian on a mid-video frame. A sharp photo background plus a
    /// crisp subject scores high; a heavily upscaled clip flattens the second derivative.
    /// </summary>
    private async Task<double> MeasureSharpnessAsync(string videoPath, CancellationToken ct)
    {
        var tmp = Path.Combine(paths.Work, $"verify_{Guid.NewGuid():N}.png");
        try
        {
            // Sample the centre band, where the subject sits, not the whole canvas.
            await runner.RunAsync(ff.FfmpegPath,
                ["-hide_banner", "-loglevel", "error", "-ss", "2", "-i", videoPath,
                 "-frames:v", "1", "-vf", "crop=1080:900:0:600,scale=360:-2", "-y", tmp],
                TimeSpan.FromSeconds(45), ct: ct);

            if (!File.Exists(tmp)) return 0;

            using var img = await Image.LoadAsync<L8>(tmp, ct);
            var w = img.Width;
            var h = img.Height;
            if (w < 8 || h < 8) return 0;

            var vals = new double[(w - 2) * (h - 2)];
            var i = 0;
            for (var y = 1; y < h - 1; y++)
            for (var x = 1; x < w - 1; x++)
            {
                // 4-neighbour Laplacian
                var lap = img[x, y - 1].PackedValue
                        + img[x, y + 1].PackedValue
                        + img[x - 1, y].PackedValue
                        + img[x + 1, y].PackedValue
                        - 4 * img[x, y].PackedValue;
                vals[i++] = lap;
            }

            var mean = vals.Average();
            return vals.Sum(v => (v - mean) * (v - mean)) / vals.Length;
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Sharpness measurement failed; treating as unknown.");
            return 0;
        }
        finally
        {
            FFmpegService.TryDelete(tmp);
        }
    }
}
