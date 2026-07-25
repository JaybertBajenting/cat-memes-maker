using System.Globalization;
using System.Text.RegularExpressions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace CatMeme.Api.Services;

public record MediaInfo(int DurationMs, int Width, int Height, bool HasAudio = false);

/// <summary>Everything detected about a green screen clip before it can be keyed.</summary>
/// <param name="SubjectFound">
/// False when no blob in the frame is plausibly a cat - an all-green segment, a transition,
/// or one where the only content is a burned-in overlay. Such segments are not imported.
/// </param>
public record ClipAnalysis(
    string KeyColor, double Similarity, string? CropFilter,
    int Width, int Height, int DurationMs, bool SubjectFound = true, double SubjectFill = 0);

/// <summary>
/// Wraps the ffmpeg binary. Deliberately does NOT depend on ffprobe: the installed
/// gyan.dev build ships ffmpeg.exe only, so duration/size are parsed from ffmpeg's own
/// stderr banner instead.
/// </summary>
public partial class FFmpegService(ProcessRunner runner, IConfiguration cfg, PipelinePaths paths, ILogger<FFmpegService> log)
{
    public string FfmpegPath { get; } = ResolveFfmpeg(cfg);

    private static string ResolveFfmpeg(IConfiguration cfg)
    {
        var p = cfg["FFMPEG_PATH"];
        if (!string.IsNullOrWhiteSpace(p) && (File.Exists(p) || !p.Contains(Path.DirectorySeparatorChar)))
            return p;
        foreach (var c in new[] { @"D:\Tools\ffmpeg\ffmpeg.exe", @"C:\Program Files\ffmpeg\bin\ffmpeg.exe" })
            if (File.Exists(c)) return c;
        return "ffmpeg";
    }

    [GeneratedRegex(@"Duration:\s*(\d+):(\d+):(\d+\.?\d*)")]
    private static partial Regex DurationRx();

    [GeneratedRegex(@"Video:.*?,\s*(\d{2,5})x(\d{2,5})")]
    private static partial Regex SizeRx();

    [GeneratedRegex(@"crop=(\d+):(\d+):(\d+):(\d+)")]
    private static partial Regex CropRx();

    /// <summary>metadata=print emits the timestamp line then the score line; pair them.</summary>
    [GeneratedRegex(@"pts_time:([0-9.]+).*?lavfi\.scene_score=([0-9.]+)", RegexOptions.Singleline)]
    private static partial Regex ScoredCutRx();

    /// <summary>Reads duration + dimensions by parsing the banner ffmpeg prints for any input.</summary>
    public async Task<MediaInfo> ProbeAsync(string file, CancellationToken ct = default)
    {
        // "-i file" with no output makes ffmpeg print stream info then exit non-zero; that's expected.
        var r = await runner.RunAsync(FfmpegPath, ["-hide_banner", "-i", file], TimeSpan.FromSeconds(60), ct: ct);
        var text = r.All;

        var ms = 0;
        var dm = DurationRx().Match(text);
        if (dm.Success)
        {
            var h = int.Parse(dm.Groups[1].Value, CultureInfo.InvariantCulture);
            var m = int.Parse(dm.Groups[2].Value, CultureInfo.InvariantCulture);
            var s = double.Parse(dm.Groups[3].Value, CultureInfo.InvariantCulture);
            ms = (int)((h * 3600 + m * 60 + s) * 1000);
        }

        int w = 0, hgt = 0;
        var sm = SizeRx().Match(text);
        if (sm.Success)
        {
            w = int.Parse(sm.Groups[1].Value, CultureInfo.InvariantCulture);
            hgt = int.Parse(sm.Groups[2].Value, CultureInfo.InvariantCulture);
        }

        if (ms == 0 && w == 0)
            log.LogWarning("Could not probe {File}. ffmpeg said: {Tail}", file, Tail(text));

        return new MediaInfo(ms, w, hgt, text.Contains("Audio:"));
    }

    /// <summary>
    /// Detects letterbox/pillarbox bars. Many clips in the source compilation are a vertical
    /// video padded into a 1280x720 frame with black bars that chroma key cannot remove.
    /// </summary>
    public async Task<string?> DetectCropAsync(string file, int probeSeconds = 3, CancellationToken ct = default)
    {
        var r = await runner.RunAsync(FfmpegPath,
            ["-hide_banner", "-ss", "0.5", "-t", probeSeconds.ToString(), "-i", file,
             "-vf", "cropdetect=limit=24:round=2:reset=0", "-f", "null", "-"],
            TimeSpan.FromMinutes(2), ct: ct);

        var matches = CropRx().Matches(r.All);
        if (matches.Count == 0) return null;

        // Last cropdetect line is the most settled estimate.
        var m = matches[^1];
        int cw = int.Parse(m.Groups[1].Value), ch = int.Parse(m.Groups[2].Value);
        int cx = int.Parse(m.Groups[3].Value), cy = int.Parse(m.Groups[4].Value);
        if (cw <= 16 || ch <= 16) return null;                  // nonsense detection
        return $"{cw}:{ch}:{cx}:{cy}";
    }

    /// <summary>
    /// Samples the border of a frame to find the actual key colour. The compilation mixes
    /// bright #00FF00 with noticeably darker greens, so a hardcoded key fails on many clips.
    /// </summary>
    public async Task<(string KeyColor, double Similarity)> SampleKeyColorAsync(
        string file, string? cropFilter, double atSeconds = 1.0, CancellationToken ct = default)
    {
        var tmp = Path.Combine(paths.Work, $"key_{Guid.NewGuid():N}.png");
        var vf = cropFilter is null ? "scale=320:-2" : $"crop={cropFilter},scale=320:-2";
        var r = await runner.RunAsync(FfmpegPath,
            ["-hide_banner", "-loglevel", "error", "-ss", atSeconds.ToString(CultureInfo.InvariantCulture),
             "-i", file, "-frames:v", "1", "-vf", vf, "-y", tmp],
            TimeSpan.FromSeconds(60), ct: ct);

        if (!File.Exists(tmp))
        {
            log.LogWarning("Key sample failed for {File}: {Err}", file, Tail(r.All));
            return ("0x00FF00", 0.30);
        }

        try
        {
            using var img = await Image.LoadAsync<Rgb24>(tmp, ct);
            // Border pixels are background by construction once bars are cropped away.
            var samples = new List<Rgb24>();
            for (var x = 0; x < img.Width; x += 2)
            {
                samples.Add(img[x, 0]);
                samples.Add(img[x, img.Height - 1]);
            }
            for (var y = 0; y < img.Height; y += 2)
            {
                samples.Add(img[0, y]);
                samples.Add(img[img.Width - 1, y]);
            }

            var greens = samples.Where(IsGreenish).ToList();
            if (greens.Count < samples.Count / 8)
            {
                log.LogInformation("No dominant green border in {File}; defaulting key.", Path.GetFileName(file));
                return ("0x00FF00", 0.30);
            }

            var r0 = (int)greens.Average(p => p.R);
            var g0 = (int)greens.Average(p => p.G);
            var b0 = (int)greens.Average(p => p.B);

            // Spread of the green tells us how tolerant the key must be.
            var spread = Math.Sqrt(greens.Average(p =>
                Math.Pow(p.R - r0, 2) + Math.Pow(p.G - g0, 2) + Math.Pow(p.B - b0, 2)));
            var similarity = Math.Clamp(0.18 + spread / 255.0 * 0.9, 0.18, 0.45);

            return ($"0x{r0:X2}{g0:X2}{b0:X2}", Math.Round(similarity, 3));
        }
        finally
        {
            TryDelete(tmp);
        }
    }

    private static bool IsGreenish(Rgb24 p) =>
        p.G > 70 && p.G > p.R * 1.25 && p.G > p.B * 1.25;

    /// <param name="Fill">
    /// Blob pixels divided by bounding-box area. A cat silhouette leaves large empty corners
    /// and fills roughly half its box; a rectangular video inset pasted onto the green plate
    /// fills nearly all of it and composites as a hard-edged rectangle.
    /// </param>
    private record Box(int X, int Y, int W, int H, double Fill = 0);

    /// <summary>
    /// Finds the cat's bounding box by locating pixels that are not the key colour.
    /// Without this the subject is a small island in a mostly-green frame, and scaling
    /// the whole frame to fit the canvas leaves the cat tiny.
    /// </summary>
    private async Task<Box?> DetectSubjectBoxAsync(
        string file, string? barCrop, string keyColor, int frameW, int frameH,
        double durationSec, CancellationToken ct)
    {
        const int sampleW = 480;
        var key = ParseHex(keyColor);

        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        var sampled = 0;
        var fillSum = 0.0;

        // Union several frames so a moving cat is never clipped mid-render.
        var times = durationSec > 2.5
            ? new[] { durationSec * 0.1, durationSec * 0.3, durationSec * 0.5, durationSec * 0.7, durationSec * 0.9 }
            : new[] { Math.Max(0.1, durationSec * 0.35), Math.Max(0.2, durationSec * 0.7) };

        foreach (var t in times)
        {
            var tmp = Path.Combine(paths.Work, $"box_{Guid.NewGuid():N}.png");
            var vf = barCrop is null ? $"scale={sampleW}:-2" : $"crop={barCrop},scale={sampleW}:-2";
            await runner.RunAsync(FfmpegPath,
                ["-hide_banner", "-loglevel", "error", "-ss", t.ToString("0.###", CultureInfo.InvariantCulture),
                 "-i", file, "-frames:v", "1", "-vf", vf, "-y", tmp],
                TimeSpan.FromSeconds(45), ct: ct);

            if (!File.Exists(tmp)) continue;

            try
            {
                using var img = await Image.LoadAsync<Rgb24>(tmp, ct);

                // Bound only the largest connected blob. Bounding every non-key pixel also
                // captures anything else burned into the plate - the source compilation has
                // a YouTube "SUBSCRIBED" button overlaid on the green in places - which
                // would drag the crop box out to include it.
                var blob = LargestBlobBounds(img, key);
                if (blob is null) continue;

                minX = Math.Min(minX, blob.X);
                minY = Math.Min(minY, blob.Y);
                maxX = Math.Max(maxX, blob.X + blob.W - 1);
                maxY = Math.Max(maxY, blob.Y + blob.H - 1);
                fillSum += blob.Fill;
                sampled++;
            }
            finally { TryDelete(tmp); }
        }

        if (sampled == 0 || maxX < 0 || maxX <= minX || maxY <= minY) return null;

        // Map back to source pixels.
        var scale = (double)(barCrop is null ? frameW : int.Parse(barCrop.Split(':')[0])) / sampleW;
        var bx = (int)(minX * scale);
        var by = (int)(minY * scale);
        var bw = (int)((maxX - minX + 1) * scale);
        var bh = (int)((maxY - minY + 1) * scale);

        // Generous margin. The two failure modes are not symmetric: a box that is too small
        // slices straight through the cat and is obvious in the output, while a box that is
        // too large only includes extra background that the key removes anyway. Desaturated
        // fur (a grey-green tabby) reads as background to the key test, so the detected blob
        // routinely under-measures the animal by 10-20%.
        var pad = (int)(Math.Max(bw, bh) * 0.15);
        bx -= pad; by -= pad; bw += pad * 2; bh += pad * 2;

        var (limW, limH) = barCrop is null
            ? (frameW, frameH)
            : (int.Parse(barCrop.Split(':')[0]), int.Parse(barCrop.Split(':')[1]));

        bx = Math.Max(0, bx);
        by = Math.Max(0, by);
        bw = Math.Min(bw, limW - bx);
        bh = Math.Min(bh, limH - by);

        // libx264 needs even dimensions.
        bw -= bw % 2; bh -= bh % 2; bx -= bx % 2; by -= by % 2;

        if (bw < 64 || bh < 64) return null;
        // If it covers nearly everything there is nothing to gain from cropping.
        if (bw >= limW - 8 && bh >= limH - 8) return null;

        return new Box(bx, by, bw, bh, fillSum / sampled);
    }

    /// <param name="Backdrop">
    /// Share of the FULL frame's outer border that is neither key colour nor letterbox
    /// black. A green screen clip keys its border away; a clip shot over a flat orange
    /// plate does not, and renders as a coloured rectangle around the cat.
    /// </param>
    /// <param name="TrappedBars">
    /// Share of the SUBJECT box's surviving pixels that are pure black - letterbox bars
    /// caught inside the crop, which render as a black rectangle.
    /// </param>
    /// <param name="SubjectRing">
    /// Share of the SUBJECT box's outer ring that is not key colour. The box is padded well
    /// clear of the animal, so on a clean green screen clip that ring is almost entirely
    /// key. When the shot is not really a green screen - a cat held in someone's hand, or
    /// wrapped in a blanket - the ring stays opaque and composites as a hard rectangle.
    /// This only became measurable once the padding was widened; against a tight box the
    /// cat itself sat on the ring and swamped the signal.
    /// </param>
    public record Contamination(double Backdrop, double TrappedBars, double SubjectRing)
    {
        public double Worst => Math.Max(Backdrop, Math.Max(TrappedBars, SubjectRing));
    }

    /// <summary>
    /// Scores the two ways a clip renders as a rectangle instead of a clean cut-out.
    ///
    /// They need separate tests. The backdrop check must run on the uncropped frame,
    /// because the subject box is deliberately tight and the cat itself sits on its border.
    /// The trapped-bar check must run inside the subject box, because that is the only
    /// place the bars survive. Neither failure can be keyed away at render time: a second
    /// key pass wide enough to remove them also eats matching fur and shadow.
    /// </summary>
    public async Task<Contamination> MeasureContaminationAsync(
        string file, string? subjectCrop, string keyColor, double atSeconds = 1.0, CancellationToken ct = default)
    {
        var key = ParseHex(keyColor);

        var backdrop = await ScoreAsync(null, img =>
        {
            var bx = Math.Max(2, (int)(img.Width * 0.06));
            var by = Math.Max(2, (int)(img.Height * 0.06));
            long total = 0, foreign = 0;

            for (var y = 0; y < img.Height; y++)
            for (var x = 0; x < img.Width; x++)
            {
                if (x >= bx && y >= by && x < img.Width - bx && y < img.Height - by) continue;
                total++;
                var p = img[x, y];
                // Letterbox black is fine here - the bar crop removes it later.
                if (!IsKeyLike(p, key) && !IsBarBlack(p)) foreign++;
            }
            return total == 0 ? 0 : (double)foreign / total;
        });

        var trapped = await ScoreAsync(subjectCrop, img =>
        {
            long kept = 0, black = 0;
            for (var y = 0; y < img.Height; y++)
            for (var x = 0; x < img.Width; x++)
            {
                var p = img[x, y];
                if (IsKeyLike(p, key)) continue;
                kept++;
                if (IsBarBlack(p)) black++;
            }
            return kept == 0 ? 0 : (double)black / kept;
        });

        var ring = await ScoreAsync(subjectCrop, img =>
        {
            // Thinner than the padding, so the ring sits in margin the cat should not reach.
            var bx = Math.Max(1, (int)(img.Width * 0.05));
            var by = Math.Max(1, (int)(img.Height * 0.05));
            long total = 0, opaque = 0;

            for (var y = 0; y < img.Height; y++)
            for (var x = 0; x < img.Width; x++)
            {
                if (x >= bx && y >= by && x < img.Width - bx && y < img.Height - by) continue;
                total++;
                if (!IsKeyLike(img[x, y], key)) opaque++;
            }
            return total == 0 ? 0 : (double)opaque / total;
        });

        return new Contamination(backdrop, trapped, ring);

        async Task<double> ScoreAsync(string? crop, Func<Image<Rgb24>, double> score)
        {
            var tmp = Path.Combine(paths.Work, $"res_{Guid.NewGuid():N}.png");
            var vf = crop is null ? "scale=320:-2" : $"crop={crop},scale=320:-2";
            await runner.RunAsync(FfmpegPath,
                ["-hide_banner", "-loglevel", "error", "-ss", atSeconds.ToString(CultureInfo.InvariantCulture),
                 "-i", file, "-frames:v", "1", "-vf", vf, "-y", tmp],
                TimeSpan.FromSeconds(45), ct: ct);

            if (!File.Exists(tmp)) return 0;
            try
            {
                using var img = await Image.LoadAsync<Rgb24>(tmp, ct);
                return score(img);
            }
            finally { TryDelete(tmp); }
        }
    }

    private static bool IsBarBlack(Rgb24 p) => p is { R: < 20, G: < 20, B: < 20 };

    /// <summary>
    /// Finds the chroma similarity that actually cleans this clip.
    ///
    /// Some clips carry a rectangle of slightly-off green around the cat (the compilation
    /// pastes a video tile onto its plate). One global similarity cannot handle this: too
    /// low leaves the rectangle, too high erases desaturated cats entirely. So each clip is
    /// keyed at increasing similarity until the ring around the subject box keys away,
    /// stopping before the subject itself starts eroding.
    /// </summary>
    public async Task<double> TuneChromaSimilarityAsync(
        string file, string? subjectCrop, string keyColor, double atSeconds = 1.0, CancellationToken ct = default)
    {
        double[] steps = [0.10, 0.14, 0.18, 0.22, 0.26, 0.30, 0.34];
        long baselineSubject = -1;
        var best = steps[0];
        var bestRing = double.MaxValue;

        foreach (var sim in steps)
        {
            var (subject, ring) = await MeasureKeyedAlphaAsync(file, subjectCrop, keyColor, sim, atSeconds, ct);
            if (subject <= 0) continue;

            if (baselineSubject < 0) baselineSubject = subject;

            // Stop if this similarity is eating the cat.
            if (subject < baselineSubject * 0.85) break;

            if (ring < bestRing)
            {
                bestRing = ring;
                best = sim;
            }
            // Ring is clean; no reason to key harder.
            if (ring < 0.04) break;
        }

        return best;
    }

    /// <summary>Keys one frame and returns (opaque subject pixels, opaque fraction of the border ring).</summary>
    private async Task<(long Subject, double Ring)> MeasureKeyedAlphaAsync(
        string file, string? crop, string keyColor, double sim, double atSeconds, CancellationToken ct)
    {
        var tmp = Path.Combine(paths.Work, $"tune_{Guid.NewGuid():N}.png");
        var pre = crop is null ? "" : $"crop={crop},";
        var vf = $"{pre}format=yuva420p,chromakey={keyColor}:{sim.ToString("0.###", CultureInfo.InvariantCulture)}:0.04,alphaextract,scale=240:-2";

        await runner.RunAsync(FfmpegPath,
            ["-hide_banner", "-loglevel", "error", "-ss", atSeconds.ToString("0.###", CultureInfo.InvariantCulture),
             "-i", file, "-frames:v", "1", "-vf", vf, "-y", tmp],
            TimeSpan.FromSeconds(45), ct: ct);

        if (!File.Exists(tmp)) return (0, 1);

        try
        {
            using var img = await Image.LoadAsync<L8>(tmp, ct);
            var bx = Math.Max(1, (int)(img.Width * 0.05));
            var by = Math.Max(1, (int)(img.Height * 0.05));

            long subject = 0, ringTotal = 0, ringOpaque = 0;
            for (var y = 0; y < img.Height; y++)
            for (var x = 0; x < img.Width; x++)
            {
                var opaque = img[x, y].PackedValue > 128;
                if (opaque) subject++;

                var onRing = x < bx || y < by || x >= img.Width - bx || y >= img.Height - by;
                if (!onRing) continue;
                ringTotal++;
                if (opaque) ringOpaque++;
            }
            return (subject, ringTotal == 0 ? 1 : (double)ringOpaque / ringTotal);
        }
        finally { TryDelete(tmp); }
    }

    /// <summary>
    /// A cat's bounding box is never a wide flat strip. Measured across the shipped library,
    /// real subjects run 0.50-1.27 aspect and occupy 54-99% of frame height; the YouTube
    /// "SUBSCRIBED" overlay burned into the source measures 3.86 aspect at 8% height. These
    /// bounds sit in the gap, so the overlay is rejected and the cat is not.
    /// </summary>
    private const double MaxSubjectAspect = 2.0;
    private const double MinSubjectAspect = 0.25;
    private const double MinSubjectHeightFraction = 0.20;

    private static bool IsPlausibleSubject(Box b, int frameHeight)
    {
        if (b.H <= 0 || b.W <= 0) return false;
        var aspect = (double)b.W / b.H;
        if (aspect > MaxSubjectAspect || aspect < MinSubjectAspect) return false;
        return b.H >= frameHeight * MinSubjectHeightFraction;
    }

    /// <summary>
    /// Flood-fills the non-key pixels into connected blobs and returns the bounds of the
    /// biggest one that could actually be a cat. Taking the biggest blob outright is not
    /// enough: in segments where the cat is small, a burned-in UI overlay wins on size.
    /// Runs on the downscaled sample frame, so the scan is cheap.
    /// </summary>
    private static Box? LargestBlobBounds(Image<Rgb24> img, Rgb24 key)
    {
        var w = img.Width;
        var h = img.Height;
        var seen = new bool[w * h];
        var stack = new Stack<int>();
        Box? best = null;
        var bestSize = 0;

        for (var start = 0; start < seen.Length; start++)
        {
            if (seen[start]) continue;
            seen[start] = true;
            if (IsKeyLike(img[start % w, start / w], key)) continue;

            int minX = w, minY = h, maxX = -1, maxY = -1, size = 0;
            stack.Push(start);

            while (stack.Count > 0)
            {
                var idx = stack.Pop();
                var x = idx % w;
                var y = idx / w;

                size++;
                if (x < minX) minX = x;
                if (y < minY) minY = y;
                if (x > maxX) maxX = x;
                if (y > maxY) maxY = y;

                // 4-connected neighbours
                if (x > 0) Push(idx - 1);
                if (x < w - 1) Push(idx + 1);
                if (y > 0) Push(idx - w);
                if (y < h - 1) Push(idx + w);
            }

            var bw = maxX - minX + 1;
            var bh = maxY - minY + 1;
            var bounds = new Box(minX, minY, bw, bh, (double)size / (bw * bh));
            if (size > bestSize && IsPlausibleSubject(bounds, h))
            {
                bestSize = size;
                best = bounds;
            }

            void Push(int n)
            {
                if (seen[n]) return;
                seen[n] = true;
                if (IsKeyLike(img[n % w, n / w], key)) return;
                stack.Push(n);
            }
        }

        // A blob smaller than this is noise, not a subject.
        return bestSize < w * h / 400 ? null : best;
    }

    private static bool IsKeyLike(Rgb24 p, Rgb24 key)
    {
        // Generous threshold: soft key edges should count as background, not subject.
        var d = Math.Abs(p.R - key.R) + Math.Abs(p.G - key.G) + Math.Abs(p.B - key.B);
        return d < 120 || IsGreenish(p);
    }

    private static Rgb24 ParseHex(string hex)
    {
        var h = hex.Replace("0x", "").Replace("#", "");
        if (h.Length != 6) return new Rgb24(0, 255, 0);
        return new Rgb24(
            Convert.ToByte(h[..2], 16),
            Convert.ToByte(h.Substring(2, 2), 16),
            Convert.ToByte(h.Substring(4, 2), 16));
    }

    public async Task<ClipAnalysis> AnalyzeClipAsync(string file, CancellationToken ct = default)
    {
        var info = await ProbeAsync(file, ct);
        var barCrop = await DetectCropAsync(file, ct: ct);

        // Ignore a crop that barely trims anything - not worth an extra filter stage.
        if (barCrop is not null && info.Width > 0)
        {
            var parts = barCrop.Split(':').Select(int.Parse).ToArray();
            if (parts[0] >= info.Width - 4 && parts[1] >= info.Height - 4) barCrop = null;
        }

        var at = info.DurationMs > 2000 ? 1.0 : 0.2;
        var (key, sim) = await SampleKeyColorAsync(file, barCrop, at, ct);

        var (baseW, baseH) = barCrop is null
            ? (info.Width, info.Height)
            : (int.Parse(barCrop.Split(':')[0]), int.Parse(barCrop.Split(':')[1]));
        var (baseX, baseY) = barCrop is null
            ? (0, 0)
            : (int.Parse(barCrop.Split(':')[2]), int.Parse(barCrop.Split(':')[3]));

        var box = await DetectSubjectBoxAsync(
            file, barCrop, key, info.Width, info.Height, info.DurationMs / 1000.0, ct);

        // Compose the bar crop and the subject box into a single crop filter.
        string? finalCrop = barCrop;
        var outW = baseW; var outH = baseH;
        if (box is not null)
        {
            finalCrop = $"{box.W}:{box.H}:{baseX + box.X}:{baseY + box.Y}";
            outW = box.W; outH = box.H;
        }

        return new ClipAnalysis(key, sim, finalCrop, outW, outH, info.DurationMs,
            box is not null, box?.Fill ?? 0);
    }

    /// <summary>
    /// Finds scene-change timestamps that split a compilation into individual clips.
    ///
    /// A fixed threshold does not transfer between source videos. In a green screen
    /// compilation the background is identical on both sides of every cut, so only the cat
    /// changes and scene scores stay low: one source needed 0.35 while another produced
    /// *zero* cuts at 0.30 and 72 at 0.05. Instead of guessing, this collects every
    /// candidate with its score in a single pass and then keeps the strongest N, choosing N
    /// so the average segment lands in the range a meme clip actually occupies.
    /// </summary>
    public async Task<List<double>> DetectSceneCutsAsync(
        string file, double totalSeconds, CancellationToken ct = default)
    {
        const double floor = 0.03;          // low enough to catch soft wipes
        const double targetSegmentSec = 3.0;

        var r = await runner.RunAsync(FfmpegPath,
            ["-hide_banner", "-i", file,
             "-filter_complex", $"select='gt(scene,{floor.ToString(CultureInfo.InvariantCulture)})',metadata=print",
             "-an", "-f", "null", "-"],
            TimeSpan.FromMinutes(45), ct: ct);

        var candidates = ScoredCutRx().Matches(r.All)
            .Select(m => (
                Time: double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                Score: double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)))
            .ToList();

        if (candidates.Count == 0)
        {
            log.LogWarning("No scene cuts found in {File} even at threshold {Floor}.",
                Path.GetFileName(file), floor);
            return [];
        }

        var wanted = Math.Max(1, (int)(totalSeconds / targetSegmentSec) - 1);
        var kept = candidates
            .OrderByDescending(c => c.Score)
            .Take(wanted)
            .Select(c => c.Time)
            .Distinct()
            .OrderBy(t => t)
            .ToList();

        log.LogInformation(
            "Scene detection on {File}: {Found} candidates >= {Floor}, kept strongest {Kept} (~{Avg:0.0}s per clip)",
            Path.GetFileName(file), candidates.Count, floor, kept.Count, totalSeconds / (kept.Count + 1));

        return kept;
    }

    /// <summary>
    /// Cuts [start,start+duration) out of a source file, re-encoded so cuts are frame
    /// accurate. Audio is kept: cat meme clips are audio-driven (the meow/huh/scream IS the
    /// joke), and the reference reels all carry the clips' original sound.
    /// </summary>
    public async Task<bool> ExtractSegmentAsync(string src, string dest, double startSec, double durSec, CancellationToken ct = default)
    {
        var r = await runner.RunAsync(FfmpegPath,
            ["-hide_banner", "-loglevel", "error",
             "-ss", startSec.ToString("0.###", CultureInfo.InvariantCulture),
             "-i", src,
             "-t", durSec.ToString("0.###", CultureInfo.InvariantCulture),
             "-c:v", "libx264", "-preset", "veryfast", "-crf", "18",
             "-pix_fmt", "yuv420p",
             "-c:a", "aac", "-b:a", "128k", "-ar", "44100",
             "-y", dest],
            TimeSpan.FromMinutes(5), ct: ct);

        if (!r.Ok) log.LogWarning("Segment extract failed: {Err}", Tail(r.All));
        return r.Ok && File.Exists(dest);
    }

    public async Task<bool> GenerateThumbnailAsync(string video, string dest, CancellationToken ct = default)
    {
        var r = await runner.RunAsync(FfmpegPath,
            ["-hide_banner", "-loglevel", "error", "-ss", "1", "-i", video,
             "-frames:v", "1", "-vf", "scale=270:480", "-y", dest],
            TimeSpan.FromSeconds(90), ct: ct);
        return r.Ok && File.Exists(dest);
    }

    internal static string Tail(string s, int n = 600) =>
        string.IsNullOrEmpty(s) ? "" : s.Length <= n ? s.Trim() : s[^n..].Trim();

    internal static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}
