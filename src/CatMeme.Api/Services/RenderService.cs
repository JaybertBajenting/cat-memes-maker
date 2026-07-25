using System.Globalization;
using System.Text;
using CatMeme.Api.Domain;

namespace CatMeme.Api.Services;

/// <summary>A concrete clip chosen for one role in one visual beat.</summary>
public record RenderCatInput(
    int ClipId,
    string Path,
    ClipAnalysis Analysis,
    bool Mirror = false,
    double PhaseOffsetSec = 0);

/// <summary>
/// A single keyed clip which already contains both characters performing one interaction.
/// Keeping it whole preserves contact timing (for example, a paw landing on the other cat)
/// that cannot be recreated by independently looping two unrelated clips.
/// </summary>
public record RenderInteractionInput(
    int AssetId,
    string Path,
    ClipAnalysis Analysis,
    bool PreserveAudio = true,
    double AudioGainDb = 0,
    double InPointSec = 0,
    double OutPointSec = 0);

/// <summary>
/// The exact part of a paired source that may be decoded for one payoff beat. If the beat
/// is longer than the curated window, the last approved frame is held; the source is never
/// looped back through frames outside that window.
/// </summary>
public sealed record InteractionPlaybackWindow(
    double InPointSec,
    double OutPointSec,
    double DecodeDurationSec,
    double HoldLastFrameSec);

/// <summary>A continuous time window in which the supplied cat state(s) are visible.</summary>
public record RenderVisualBeat(
    double StartSec,
    double EndSec,
    RenderCatInput? CatA,
    RenderCatInput? CatB,
    string? Stage = null,
    string? StoryRole = null,
    bool? TitleVisible = null,
    string? TitleStyle = null,
    bool? LabelsVisible = null,
    string? Lighting = null,
    string? CatScale = null,
    string? Transition = null,
    string? SoundCue = null,
    string? StoryboardProfile = null,
    string? InteractionAction = null,
    RenderInteractionInput? Interaction = null);

public record RenderRequest(
    int VideoId,
    string BackgroundPath,
    string CatAPath,
    string? CatBPath,
    ClipAnalysis CatA,
    ClipAnalysis? CatB,
    Topic Topic,
    int TargetDurationMs,
    IReadOnlyList<RenderVisualBeat>? VisualBeats = null);

public record RenderResult(bool Success, string? OutputPath, int DurationMs, string? Error);

/// <summary>
/// Builds and runs the ffmpeg filter graph that turns a background + keyed cat clip(s)
/// + text into a finished 1080x1920 reel.
/// </summary>
public class RenderService(FFmpegService ff, ProcessRunner runner, PipelinePaths paths, ILogger<RenderService> log)
{
    public const int CanvasW = 1080;
    public const int CanvasH = 1920;

    /// <summary>
    /// BoxW/BoxH is the region the subject must fit inside. Fitting to a box (rather than
    /// forcing a height) is what stops a wide close-up from spilling out of its half and
    /// colliding with the other cat.
    ///
    /// X/Y are *overlay* expressions, where W/H are the canvas and w/h the scaled subject.
    /// They are written around a centre point so a short subject still sits in the middle
    /// of its zone instead of hanging from a fixed top edge.
    ///
    /// LabelX is a *drawtext* expression, where w/h are the frame and text_w/text_h the
    /// glyph box - the two filters do NOT share variable names.
    /// </summary>
    private record Placement(int BoxW, int BoxH, string X, string Y, string LabelX, int LabelY);

    /// <summary>Past roughly 2x, an upscaled clip turns to mush; better small than blurry.</summary>
    private const double MaxUpscale = 2.2;

    /// <summary>
    /// How long a smash-cut's white accent is held. Kept above the visual-QA boundary probe
    /// offset (start + 0.075s) so the effect is measurable, not just present.
    /// </summary>
    private const double SmashCutHoldSec = 0.12;

    /// <summary>
    /// The reel length ffmpeg is actually asked for. A target outside this range is clamped
    /// here, so <see cref="RenderPreflight"/> reconciles it BEFORE the render: the verifier
    /// compares the finished file against the target it was given, and an unreconciled
    /// target would fail every single video on duration drift.
    /// </summary>
    public const int MinDurationMs = 2_000;
    public const int MaxDurationMs = 60_000;

    public static InteractionPlaybackWindow ResolveInteractionWindow(
        RenderInteractionInput interaction,
        double beatDurationSec)
    {
        var start = Math.Max(0, interaction.InPointSec);
        var fallbackDuration = Math.Max(0, interaction.Analysis.DurationMs / 1000.0);
        var configuredEnd = interaction.OutPointSec > start
            ? interaction.OutPointSec
            : start + fallbackDuration;
        var available = Math.Max(0, configuredEnd - start);
        var requested = Math.Max(0, beatDurationSec);
        var decode = Math.Min(requested, available);
        return new InteractionPlaybackWindow(
            start,
            configuredEnd,
            decode,
            Math.Max(0, requested - decode));
    }

    /// <summary>
    /// FFmpeg options for a whole-scene interaction input. Deliberately contains no
    /// <c>-stream_loop</c>; callers can test and audit that curated paired media never wraps.
    /// </summary>
    public static IReadOnlyList<string> InteractionInputOptions(
        RenderInteractionInput interaction,
        InteractionPlaybackWindow playback)
    {
        if (playback.DecodeDurationSec <= 0) return [];
        var options = new List<string>();
        if (playback.InPointSec > 0)
            options.AddRange(["-ss", Fmt(playback.InPointSec)]);
        options.AddRange(["-t", Fmt(playback.DecodeDurationSec), "-i", interaction.Path]);
        return options;
    }

    public async Task<RenderResult> RenderAsync(
        RenderRequest req,
        Action<int>? onFfmpegStarted = null,
        CancellationToken ct = default)
    {
        var outPath = paths.OutputVideo(req.VideoId);
        var workDir = Path.Combine(paths.Work, $"v{req.VideoId}");
        Directory.CreateDirectory(workDir);

        var durationSec = Math.Clamp(
            req.TargetDurationMs / 1000.0, MinDurationMs / 1000.0, MaxDurationMs / 1000.0);
        var visualBeats = (req.VisualBeats ?? [])
            .Where(b => b.EndSec > b.StartSec)
            .OrderBy(b => b.StartSec)
            .ToList();
        var isMultiBeat = visualBeats.Count > 0;
        var hasB = isMultiBeat
            ? req.Topic.Layout != Layout.SinglePov
            : req.CatBPath is not null && req.CatB is not null;
        var layout = NormalizeLayout(req.Topic.Layout, hasB);

        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin" };

        // 0: background still, looped for the whole reel
        args.AddRange(["-loop", "1", "-t", Fmt(durationSec), "-i", req.BackgroundPath]);

        // Each timed state gets its own input. Reusing one decoded stream in several overlay
        // branches requires a split graph and careful timestamp resets; separate looped inputs
        // are cheap at this library size and make every beat begin from a valid stream.
        var timedInputs = new List<(
            RenderCatInput Cat, double Start, double End, bool IsA, int InputIndex,
            string? Scale, string? Transition)>();
        var interactionInputs = new List<(
            RenderInteractionInput Interaction, double Start, double End, int InputIndex,
            string? Transition, InteractionPlaybackWindow Playback)>();
        var nextInput = 1;

        void AddTimedInput(
            RenderCatInput cat, double start, double end, bool isA,
            string? scale = null, string? transition = null)
        {
            start = Math.Max(0, start);
            end = Math.Min(durationSec, end);
            if (end <= start) return;

            // Decode only the media this beat can actually show.  The filter chain shifts
            // the resulting local 0-based timestamps into the beat's reel-time window.
            // Using the whole reel duration here made every short reaction clip loop and
            // decode for 20-60 seconds even when it appeared for only a few seconds.
            var beatDuration = end - start;
            args.AddRange(["-stream_loop", "-1"]);
            if (cat.PhaseOffsetSec > 0) args.AddRange(["-ss", Fmt(cat.PhaseOffsetSec)]);
            args.AddRange(["-t", Fmt(beatDuration), "-i", cat.Path]);
            timedInputs.Add((cat, start, end, isA, nextInput++, scale, transition));
        }

        void AddInteractionInput(
            RenderInteractionInput interaction, double start, double end, string? transition)
        {
            start = Math.Max(0, start);
            end = Math.Min(durationSec, end);
            if (end <= start) return;

            var beatDuration = end - start;
            var playback = ResolveInteractionWindow(interaction, beatDuration);
            if (playback.DecodeDurationSec <= 0) return;

            // A paired interaction is a curated continuous performance, not a generic cat
            // loop. Decode only [in,out); if the authored beat is longer, the filter graph
            // holds its final approved frame instead of wrapping to unapproved source time.
            args.AddRange(InteractionInputOptions(interaction, playback));
            interactionInputs.Add((
                interaction, start, end, nextInput++, transition, playback));
        }

        if (isMultiBeat)
        {
            foreach (var beat in visualBeats)
            {
                if (beat.Interaction is not null)
                {
                    AddInteractionInput(beat.Interaction, beat.StartSec, beat.EndSec, beat.Transition);
                    continue;
                }

                if (beat.CatA is not null)
                    AddTimedInput(
                        beat.CatA, beat.StartSec, beat.EndSec, true,
                        beat.CatScale, beat.Transition);
                if (beat.CatB is not null)
                    AddTimedInput(
                        beat.CatB, beat.StartSec, beat.EndSec, false,
                        beat.CatScale, beat.Transition);
            }
        }
        else
        {
            AddTimedInput(new RenderCatInput(0, req.CatAPath, req.CatA), 0, durationSec, true);
            if (hasB)
            {
                var duplicateSleep = Path.GetFullPath(req.CatAPath)
                        .Equals(Path.GetFullPath(req.CatBPath!), StringComparison.OrdinalIgnoreCase) &&
                    req.Topic.CatAAction?.Equals("sleeping", StringComparison.OrdinalIgnoreCase) == true &&
                    req.Topic.CatBAction?.Equals("sleeping", StringComparison.OrdinalIgnoreCase) == true;
                AddTimedInput(new RenderCatInput(
                    0, req.CatBPath!, req.CatB!, Mirror: duplicateSleep,
                    PhaseOffsetSec: duplicateSleep ? 0.35 : 0), 0, durationSec, false);
            }
        }

        // Authored cues are generated locally and deterministically. They are deliberately
        // short accents, not a soundtrack, so source-clip audio remains the primary sound.
        var cueInputs = new List<(string Cue, double Start, double Duration, int InputIndex)>();
        foreach (var beat in visualBeats)
        {
            var cue = NormalizeSoundCue(beat.SoundCue);
            if (cue is null) continue;
            var (source, cueDuration) = SoundCueSource(cue);
            args.AddRange(["-f", "lavfi", "-t", Fmt(cueDuration), "-i", source]);
            cueInputs.Add((cue, beat.StartSec, cueDuration, nextInput++));
        }

        // Silent bed, always present: it pads/fallbacks when a clip carries no audio track,
        // and guarantees the output has an audio stream (Reels reject silent-less files).
        args.AddRange(["-f", "lavfi", "-t", Fmt(durationSec), "-i", "anullsrc=channel_layout=stereo:sample_rate=44100"]);
        var nullIdx = nextInput;

        // Probe each physical file once even when it reappears in several beats.
        var audioByPath = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var input in timedInputs)
        {
            if (!audioByPath.ContainsKey(input.Cat.Path))
                audioByPath[input.Cat.Path] = (await ff.ProbeAsync(input.Cat.Path, ct)).HasAudio;
        }
        foreach (var input in interactionInputs)
        {
            if (!audioByPath.ContainsKey(input.Interaction.Path))
                audioByPath[input.Interaction.Path] =
                    (await ff.ProbeAsync(input.Interaction.Path, ct)).HasAudio;
        }

        var fc = new StringBuilder();

        // Background: cover-fit to 1080x1920 then hard crop, so no bars ever appear.
        fc.Append($"[0:v]scale={CanvasW}:{CanvasH}:force_original_aspect_ratio=increase,")
          .Append($"crop={CanvasW}:{CanvasH},setsar=1,format=rgba[bg];");

        var (pa, pb) = GetPlacements(layout);

        var lastStage = "bg";
        var visualIndex = 0;
        foreach (var input in timedInputs)
        {
            var basePlacement = input.IsA ? pa : pb;
            var placement = basePlacement is null
                ? null
                : ScalePlacement(basePlacement, input.Scale, input.IsA);
            if (placement is null) continue;

            var catLabel = $"c{++visualIndex}";
            var nextStage = $"s{visualIndex}";
            fc.Append(BuildCatChain(
                input.InputIndex, input.Cat.Analysis, placement, catLabel,
                input.Start, input.Cat.Mirror));
            fc.Append('[').Append(lastStage).Append("][").Append(catLabel).Append("]overlay=")
              .Append(placement.X).Append(':').Append(placement.Y).Append(":format=auto:eof_action=pass");
            if (isMultiBeat)
                fc.Append($":enable='between(t,{Fmt(input.Start)},{Fmt(input.End)})'");
            fc.Append('[').Append(nextStage).Append("]; ");
            lastStage = nextStage;
        }


        foreach (var input in interactionInputs)
        {
            var placement = InteractionPlacement(input.Transition);
            var pairLabel = $"pair{++visualIndex}";
            var nextStage = $"s{visualIndex}";
            fc.Append(BuildCatChain(
                input.InputIndex, input.Interaction.Analysis, placement, pairLabel,
                input.Start, holdLastFrameSec: input.Playback.HoldLastFrameSec));
            fc.Append('[').Append(lastStage).Append("][").Append(pairLabel).Append("]overlay=")
              .Append(placement.X).Append(':').Append(placement.Y)
              .Append(":format=auto:eof_action=pass:enable='between(t,")
              .Append(Fmt(input.Start)).Append(',').Append(Fmt(input.End)).Append(")'[")
              .Append(nextStage).Append("]; ");
            lastStage = nextStage;
        }

        // Lighting and flash/fade accents belong to the complete photographed scene, not
        // only the plate. Applying them after foreground compositing keeps cats grounded in
        // a dark room and lets a flash actually cover the whole frame.
        var frameEffectIndex = 0;
        foreach (var beat in visualBeats)
        {
            foreach (var effect in FrameEffects(beat))
            {
                var nextStage = $"fx{++frameEffectIndex}";
                fc.Append('[').Append(lastStage).Append("]drawbox=x=0:y=0:w=iw:h=ih:color=")
                  .Append(effect.Color).Append(":t=fill:enable='between(t,")
                  .Append(Fmt(effect.Start)).Append(',').Append(Fmt(effect.End))
                  .Append(")'[").Append(nextStage).Append("]; ");
                lastStage = nextStage;
            }
        }

        // Text is passed via textfile= so colons/quotes in a POV title can't break the filtergraph.
        var textStages = BuildTextStages(
            req.Topic, layout, pa, pb, workDir, lastStage, durationSec,
            isMultiBeat ? visualBeats : null);
        var textFiles = textStages.Files;
        fc.Append(textStages.Graph);

        fc.Append($"[{textStages.OutLabel}]format=yuv420p[vout];");

        // Shift each beat-sized audio input into the same reel-time window as its visual.
        // A duplicate sleeping source contributes audio only once, avoiding doubled/phasey
        // snoring.  Input -t already bounds decoding; atrim is a defensive final clamp.
        var audioLabels = new List<string>();
        var heard = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var audioIndex = 0;
        foreach (var input in timedInputs.Where(i => audioByPath.GetValueOrDefault(i.Cat.Path)))
        {
            var key = $"{Path.GetFullPath(input.Cat.Path)}|{Fmt(input.Start)}|{Fmt(input.End)}";
            if (!heard.Add(key)) continue;

            var label = $"a{++audioIndex}";
            var beatDuration = input.End - input.Start;
            var delayMs = (int)Math.Round(input.Start * 1000, MidpointRounding.AwayFromZero);
            fc.Append($"[{input.InputIndex}:a]atrim=duration={Fmt(beatDuration)},")
              // amix consumes each input from its first sample and does not preserve an
              // initial timestamp offset consistently across FFmpeg builds. adelay makes
              // the intended reel-time placement explicit in samples.
              .Append($"asetpts=PTS-STARTPTS,adelay={delayMs}:all=1[{label}];");
            audioLabels.Add(label);
        }

        foreach (var input in interactionInputs.Where(i =>
                     i.Interaction.PreserveAudio &&
                     audioByPath.GetValueOrDefault(i.Interaction.Path)))
        {
            var label = $"a{++audioIndex}";
            var delayMs = (int)Math.Round(input.Start * 1000, MidpointRounding.AwayFromZero);
            var gain = Math.Clamp(input.Interaction.AudioGainDb, -24, 12);
            fc.Append($"[{input.InputIndex}:a]atrim=duration={Fmt(input.Playback.DecodeDurationSec)},")
              .Append($"asetpts=PTS-STARTPTS,volume={Fmt(Math.Pow(10, gain / 20.0))},")
              .Append($"adelay={delayMs}:all=1[{label}];");
            audioLabels.Add(label);
        }

        foreach (var cue in cueInputs)
        {
            var label = $"a{++audioIndex}";
            var delayMs = (int)Math.Round(cue.Start * 1000, MidpointRounding.AwayFromZero);
            fc.Append($"[{cue.InputIndex}:a]atrim=duration={Fmt(cue.Duration)},")
              .Append($"asetpts=PTS-STARTPTS,adelay={delayMs}:all=1[{label}];");
            audioLabels.Add(label);
        }

        if (audioLabels.Count == 0)
            fc.Append($"[{nullIdx}:a]anull[aout]");
        else
        {
            fc.Append($"[{nullIdx}:a]");
            foreach (var label in audioLabels) fc.Append('[').Append(label).Append(']');
            fc.Append($"amix=inputs={audioLabels.Count + 1}:duration=first:normalize=0,")
              .Append("alimiter=limit=0.9[aout]");
        }

        args.AddRange([
            "-filter_complex", fc.ToString(),
            "-map", "[vout]",
            "-map", "[aout]",
            "-t", Fmt(durationSec),
            "-r", "30",
            "-c:v", "libx264", "-crf", "23", "-preset", "fast", "-profile:v", "high", "-pix_fmt", "yuv420p",
            "-c:a", "aac", "-b:a", "128k", "-ar", "44100",
            "-movflags", "+faststart",
            "-y", outPath
        ]);

        log.LogInformation("Rendering video {Id} layout={Layout} dur={Dur}s visualBeats={Beats}",
            req.VideoId, layout, durationSec, visualBeats.Count);

        try
        {
            var r = await runner.RunAsync(ff.FfmpegPath, args, TimeSpan.FromMinutes(15), onFfmpegStarted, ct: ct);

            if (!r.Ok || !File.Exists(outPath))
            {
                var err = FFmpegService.Tail(r.All, 900);
                log.LogError("Render failed for video {Id}: {Err}", req.VideoId, err);
                return new RenderResult(false, null, 0, string.IsNullOrWhiteSpace(err) ? "ffmpeg failed" : err);
            }

            var info = await ff.ProbeAsync(outPath, ct);
            await ff.GenerateThumbnailAsync(outPath, paths.Thumbnail(req.VideoId), ct);
            return new RenderResult(true, outPath, info.DurationMs, null);
        }
        finally
        {
            foreach (var f in textFiles) FFmpegService.TryDelete(f);
            try { Directory.Delete(workDir, true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// crop to the subject box -> add alpha -> key the clip's own green -> despill -> scale.
    ///
    /// Uses chromakey (YUV), not colorkey (RGB). colorkey measures full RGB distance, which
    /// includes luminance, so a grey/white cat's fur and dark eyes fall inside the threshold
    /// and get punched out along with the background. chromakey compares chroma only, so a
    /// desaturated subject survives regardless of how bright or dark it is.
    /// </summary>
    private sealed record FrameEffectWindow(string Color, double Start, double End);

    private static IEnumerable<FrameEffectWindow> FrameEffects(RenderVisualBeat beat)
    {
        var start = Math.Max(0, beat.StartSec);
        var end = Math.Max(start, beat.EndSec);
        if (end <= start) yield break;

        switch (beat.Lighting?.Trim().ToLowerInvariant())
        {
            case "dim":
                yield return new FrameEffectWindow("black@0.22", start, end);
                break;
            case "dark":
                yield return new FrameEffectWindow("black@0.52", start, end);
                break;
            case "flash":
                yield return new FrameEffectWindow("white@0.12", start, end);
                break;
        }

        // A cut is deliberately effect-free. The other choices are restrained boundary
        // accents: enough to make the authored transition legible without covering the gag.
        // Boundary effects must also stay observable: visual QA probes a single frame at
        // start + 0.075s, so anything shorter than that reads as "effect missing".
        var boundary = Math.Min(end, start + Math.Min(0.20, (end - start) / 3));
        switch (beat.Transition?.Trim().ToLowerInvariant())
        {
            case "fade":
                yield return new FrameEffectWindow("black@0.42", start, boundary);
                break;
            case "flash":
                yield return new FrameEffectWindow("white@0.70", start, Math.Min(boundary, start + 0.11));
                break;
            case "smash_cut":
                // Must outlast the QA boundary probe, which samples at start + 0.075s. At
                // 0.055s the accent was real but unobservable: every review reported
                // "boundary is not visible (luminance ratio 1.00)" because the sample landed
                // after the flash had already ended.
                yield return new FrameEffectWindow(
                    "white@0.30", start, Math.Min(boundary, start + SmashCutHoldSec));
                break;
        }
    }

    private static Placement ScalePlacement(Placement placement, string? scale, bool isA)
    {
        // These were briefly pushed to 1.38/0.58 chasing "cat_scale_mismatch" failures. That
        // was a misdiagnosis: the QA metric behind those failures saturates on any beat
        // carrying a lighting effect (it segments the subject by differencing against the
        // UNLIT plate, so dim/dark/flash makes every pixel read as subject and both cats
        // measure ~1.0). On beats where the metric is not saturated the original spread
        // already reads correctly, so the framing is left alone and the metric is the thing
        // that needs fixing.
        var factor = scale?.Trim().ToLowerInvariant() switch
        {
            "wide" => 0.82,
            "close_a" when isA => 1.28,
            "close_a" => 0.90,
            "close_b" when !isA => 1.28,
            "close_b" => 0.90,
            _ => 1.0,
        };

        return placement with
        {
            BoxW = Math.Clamp((int)Math.Round(placement.BoxW * factor), 220, CanvasW - 30),
            BoxH = Math.Clamp((int)Math.Round(placement.BoxH * factor), 300, 1_050),
        };
    }

    private static Placement InteractionPlacement(string? transition)
    {
        var smash = transition?.Equals("smash_cut", StringComparison.OrdinalIgnoreCase) == true;
        return new Placement(
            smash ? 950 : 880,
            smash ? 900 : 830,
            "(W-w)/2",
            "1020+(830-h)/2",
            "(w-text_w)/2",
            1520);
    }

    private static string? NormalizeSoundCue(string? cue)
    {
        var normalized = cue?.Trim().ToLowerInvariant();
        return normalized is "pop" or "whoosh" or "alarm" or "impact" or "record_scratch"
            ? normalized
            : null;
    }

    private static (string Source, double Duration) SoundCueSource(string cue) => cue switch
    {
        "pop" => (
            "sine=frequency=880:sample_rate=44100:duration=0.12,afade=t=out:st=0.04:d=0.08,volume=0.32",
            0.12),
        "whoosh" => (
            "anoisesrc=color=pink:sample_rate=44100:duration=0.28:amplitude=0.18,highpass=f=450,lowpass=f=6000,afade=t=out:st=0.05:d=0.23",
            0.28),
        "alarm" => (
            "sine=frequency=1050:sample_rate=44100:duration=0.46,volume=0.28",
            0.46),
        "impact" => (
            "sine=frequency=72:sample_rate=44100:duration=0.24,afade=t=out:st=0.03:d=0.21,volume=0.55",
            0.24),
        _ => (
            "anoisesrc=color=white:sample_rate=44100:duration=0.24:amplitude=0.12,highpass=f=1200,afade=t=out:st=0.04:d=0.20",
            0.24),
    };

    private static string BuildCatChain(
        int inputIndex, ClipAnalysis clip, Placement p, string outLabel,
        double startSec = 0, bool mirror = false, double holdLastFrameSec = 0)
    {
        var sb = new StringBuilder();
        sb.Append($"[{inputIndex}:v]");
        if (clip.CropFilter is not null) sb.Append($"crop={clip.CropFilter},");
        sb.Append("format=yuva420p,")
          .Append($"chromakey={clip.KeyColor}:{Fmt(clip.Similarity)}:0.04,")
          .Append("despill=type=green:mix=0.5:expand=0,")
          .Append($"scale={FitBox(clip, p)}:force_original_aspect_ratio=decrease:flags=lanczos,");
        if (mirror) sb.Append("hflip,");
        if (holdLastFrameSec > 0)
            sb.Append($"tpad=stop_mode=clone:stop_duration={Fmt(holdLastFrameSec)},");
        sb.Append($"setsar=1,setpts=PTS-STARTPTS+{Fmt(startSec)}/TB[{outLabel}];");
        return sb.ToString();
    }

    /// <summary>
    /// Fits the subject inside its box without exceeding <see cref="MaxUpscale"/>.
    /// force_original_aspect_ratio=decrease then shrinks to whichever edge binds first,
    /// so both width and height are respected whatever the clip's aspect ratio is.
    /// </summary>
    private static string FitBox(ClipAnalysis clip, Placement p) => FitBox(clip.Width, clip.Height, p.BoxW, p.BoxH);

    internal static string FitBox(int clipW, int clipH, int boxW, int boxH)
    {
        if (clipW <= 0 || clipH <= 0) return $"{boxW}:{boxH}";

        var capW = (int)(clipW * MaxUpscale);
        var capH = (int)(clipH * MaxUpscale);
        var w = Math.Max(2, Math.Min(boxW, capW));
        var h = Math.Max(2, Math.Min(boxH, capH));
        return $"{w - w % 2}:{h - h % 2}";
    }


    private record TextStages(string Graph, string OutLabel, List<string> Files);

    internal readonly record struct QualityTitlePresentation(int FontSize, int Y, int MaxChars);

    /// <summary>
    /// Minimal is an intentionally quieter title treatment, not an alias for standard.
    /// It is smaller, sits slightly lower, wraps less aggressively, and uses a thinner,
    /// translucent outline. Payoff remains the largest treatment.
    /// </summary>
    internal static QualityTitlePresentation QualityTitlePresentationFor(string? style) =>
        style?.Trim().ToLowerInvariant() switch
        {
            StoryboardTitleStyle.Minimal => new(44, 104, 38),
            StoryboardTitleStyle.Payoff => new(64, 70, 27),
            _ => new(58, 80, 30),
        };

    internal static string TextDecorationFor(bool pill, string? textStyle)
    {
        if (pill)
            return ":fontcolor=black:box=1:boxcolor=white@0.94:boxborderw=24";

        return textStyle?.Trim().ToLowerInvariant() switch
        {
            StoryboardTitleStyle.TimeCard =>
                ":fontcolor=0xffd5d9:borderw=5:bordercolor=0x5d0713@0.95" +
                ":shadowcolor=black@0.45:shadowx=0:shadowy=4",
            StoryboardTitleStyle.Payoff =>
                ":fontcolor=white:borderw=7:bordercolor=black@0.98" +
                ":shadowcolor=black@0.65:shadowx=0:shadowy=5",
            StoryboardTitleStyle.Minimal =>
                ":fontcolor=white@0.88:borderw=2:bordercolor=black@0.68",
            _ =>
                ":fontcolor=white:borderw=5:bordercolor=black@0.9" +
                ":shadowcolor=black@0.5:shadowx=0:shadowy=3",
        };
    }

    private TextStages BuildTextStages(
        Topic topic, string layout, Placement pa, Placement? pb,
        string workDir, string inLabel, double durationSec,
        IReadOnlyList<RenderVisualBeat>? visualBeats = null)
    {
        var textFiles = new List<string>();
        var font = EscapePath(FontFile());
        var sb = new StringBuilder();
        var idx = 0;
        var current = inLabel;

        string Next() => $"t{++idx}";

        // Title uses a white card with dark text - the style the highest-viewed reels use.
        // Labels and captions stay white-on-outline so they read over any background.
        void Add(string text, int fontSize, int y, bool pill, string? xExpr = null,
                 int? maxChars = null, (double Start, double End)? window = null,
                 string? textStyle = null)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            var wrapped = Wrap(text, maxChars ?? MaxCharsFor(fontSize, pill));
            var file = Path.Combine(workDir, $"txt{idx}_{Guid.NewGuid():N}.txt");
            // BOM-less UTF-8: ffmpeg renders a BOM as a visible glyph.
            File.WriteAllText(file, wrapped, new UTF8Encoding(false));
            textFiles.Add(file);

            var next = Next();
            var x = xExpr ?? "(w-text_w)/2";
            sb.Append($"[{current}]drawtext=fontfile='{font}':textfile='{EscapePath(file)}'")
              .Append($":fontsize={fontSize}:line_spacing=10:x={x}:y={y}");

            // A windowed stage is present for the whole reel but only drawn inside its turn.
            if (window is { } w)
                sb.Append($":enable='between(t,{Fmt(w.Start)},{Fmt(w.End)})'");

            sb.Append(TextDecorationFor(pill, textStyle));

            sb.Append($"[{next}];");
            current = next;
        }

        void AddTransitionCard((double Start, double End) window)
        {
            // The card is deliberately appended after the persistent title and composited
            // cats.  Its opaque first layer therefore replaces the entire scene for this
            // window instead of looking like one more caption floating over the normal reel.
            // The restrained grid adds enough texture to read as a time/interstitial card
            // without coupling the renderer to any particular topic or phrase.
            var card = Next();
            sb.Append($"[{current}]drawbox=x=0:y=0:w=iw:h=ih:color=0x8f1d17:t=fill")
              .Append($":enable='between(t,{Fmt(window.Start)},{Fmt(window.End)})'[{card}];");
            current = card;

            var textured = Next();
            sb.Append($"[{current}]drawgrid=width=180:height=180:thickness=5:color=0x4c0d0a@0.42")
              .Append($":enable='between(t,{Fmt(window.Start)},{Fmt(window.End)})'[{textured}];");
            current = textured;
        }

        var usesQualityProfile = visualBeats?.Any(b =>
            b.StoryboardProfile?.Equals(
                "reference-default-v1", StringComparison.OrdinalIgnoreCase) == true) == true;

        // The benchmark profile uses the reference reel's clean outlined title and can hide
        // it for a time card or final reveal. Legacy stored stories retain their white pill.
        const int legacyTitleY = 300, titleSize = 54, titleBoxPad = 24;
        const int qualityTitleY = 80;
        if (usesQualityProfile)
        {
            foreach (var beat in visualBeats!)
            {
                if (beat.TitleVisible == false ||
                    beat.TitleStyle?.Equals("time_card", StringComparison.OrdinalIgnoreCase) == true)
                    continue;

                // The POV title is an enclosed white card with dark text, matching the
                // reference reels. It was outlined white-on-transparent, which reads as one
                // more caption floating in the scene rather than as the reel's header.
                var presentation = QualityTitlePresentationFor(beat.TitleStyle);
                Add(topic.TopicText, presentation.FontSize, presentation.Y, pill: true,
                    maxChars: presentation.MaxChars, window: (beat.StartSec, beat.EndSec),
                    textStyle: beat.TitleStyle ?? "standard");
            }
        }
        else
        {
            Add(topic.TopicText, titleSize, legacyTitleY, pill: true);
        }

        // Where the title actually ends, not where a one-line title would. A longer topic
        // wraps to three lines and grows downwards; with a fixed dialogue position the first
        // turn lands on the title card instead of below it.
        var titleLines = string.IsNullOrWhiteSpace(topic.TopicText)
            ? 0
            : Wrap(topic.TopicText, MaxCharsFor(titleSize, pill: true)).Split('\n').Length;
        var titleBottom = titleLines == 0
            ? (usesQualityProfile ? qualityTitleY : legacyTitleY)
            : (usesQualityProfile ? qualityTitleY : legacyTitleY)
              + titleLines * (titleSize + 10) + titleBoxPad;

        var dialogueY = Math.Max(DialogueY, titleBottom + 46);

        var dialogue = topic.Dialogue();

        // A conversation replaces the captions rather than sharing the band with them:
        // both want the same strip of canvas between the title and the cats.
        if (dialogue.Count > 0)
        {
            foreach (var (line, start, end) in ScheduleDialogue(dialogue, durationSec))
            {
                var authoredBeat = visualBeats?.FirstOrDefault(b =>
                    start >= b.StartSec - 0.001 && start < b.EndSec);

                // A transition is a stage card, not something either cat says. Centre it
                // and omit quotation marks while the beat planner hides the subjects.
                if (line.Stage?.Equals(DialogueStage.Transition, StringComparison.OrdinalIgnoreCase) == true ||
                    authoredBeat?.Transition?.Equals("time_card", StringComparison.OrdinalIgnoreCase) == true ||
                    authoredBeat?.TitleStyle?.Equals("time_card", StringComparison.OrdinalIgnoreCase) == true)
                {
                    // Visual beats include the intentional inter-turn gap; use that full
                    // window so the normal background/title cannot flash for 450ms at the
                    // tail of an otherwise full-screen card.
                    var transitionBeat = authoredBeat;
                    var cardWindow = transitionBeat is null
                        ? (start, end)
                        : (transitionBeat.StartSec, transitionBeat.EndSec);

                    AddTransitionCard(cardWindow);
                    Add((line.Text ?? "").Trim().ToUpperInvariant(), 74, 790, pill: false,
                        maxChars: 20, window: cardWindow, textStyle: "time_card");
                    continue;
                }

                // A purely visual beat (most often sleeping) intentionally has no text.
                // Wrapping it in quotation marks would render a stray pair of quotes.
                if (string.IsNullOrWhiteSpace(line.Text)) continue;

                // Which side the line sits on IS how the viewer knows who is speaking, so
                // each turn is centred inside its own half rather than across the canvas.
                var half = CanvasW / 2;
                var xExpr = line.IsA
                    ? $"({half}-text_w)/2"
                    : $"{half}+({half}-text_w)/2";

                if (usesQualityProfile)
                {
                    if (authoredBeat?.StoryRole?.Equals(
                            "payoff", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        Add(line.Text.Trim(), 58, 430, pill: false,
                            xExpr: "(w-text_w)/2", maxChars: 28,
                            window: (start, end), textStyle: "payoff");
                    }
                    else
                    {
                        Add(line.Text.Trim(), 43, dialogueY,
                            pill: false, xExpr: xExpr, maxChars: DialogueMaxChars,
                            window: (start, end));
                    }
                    continue;
                }

                Add($"“{line.Text.Trim()}”", DialogueFontSize, dialogueY,
                    pill: false, xExpr: xExpr, maxChars: DialogueMaxChars,
                    window: (start, end));
            }
        }
        else if (!string.IsNullOrWhiteSpace(topic.CaptionTop))
        {
            Add(topic.CaptionTop!, 42, 520, pill: false);
        }

        // Role labels sit beside their subject, but never inside the conversation: a long
        // turn wraps to three lines and would otherwise run straight through them.
        // Profiled beats render dialogue larger and wider than the legacy caption settings,
        // so measuring with DialogueMaxChars/DialogueFontSize underestimates how far the text
        // actually reaches - a three-line turn ran straight into the role tags ("SO important"
        // sitting on top of "Gf"). Measure with the widest settings either path can produce.
        var dialogueLineHeight = Math.Max(DialogueFontSize, QualityDialogueFontSize) + 12;
        var dialogueWrapChars = Math.Min(DialogueMaxChars, QualityDialogueMaxChars);
        var dialogueBottom = dialogue.Count == 0
            ? 0
            : dialogueY + dialogue
                .Max(l => Wrap($"“{l.Text?.Trim()}”", dialogueWrapChars).Split('\n').Length)
                * dialogueLineHeight;

        // A full line of clear air under the conversation, so a tag never crowds the words.
        var labelAY = Math.Max(pa.LabelY, dialogueBottom + LabelClearanceY);

        if (visualBeats is { Count: > 0 })
        {
            foreach (var beat in visualBeats)
            {
                // A transition card owns the complete frame, including the label layer.
                // This also keeps custom RenderRequest callers safe if they accidentally
                // attach a cat input to a transition beat.
                if (beat.Stage?.Equals(DialogueStage.Transition, StringComparison.OrdinalIgnoreCase) == true)
                    continue;

                // Quality-profile dialogue already identifies the speaker by which half of
                // the frame owns the caption. Keep role labels off unless explicitly asked.
                // Labels are ON unless a beat explicitly hides them. This used to be
                // `!= true`, which hid them whenever the field was merely unset - so every
                // profiled reel shipped with no "me"/"her" tags at all. The reference reel
                // this profile is modelled on keeps both labels up for its full runtime; the
                // caption side tells you who is SPEAKING, but the labels are what tell you
                // who each cat IS. Only an explicit false suppresses them now.
                // A time card replaces the entire scene, so role tags must never sit on top
                // of it - checked on the style as well as the stage, because a card beat can
                // be authored either way and only one of them was being caught.
                if (beat.TitleStyle?.Equals(StoryboardTitleStyle.TimeCard, StringComparison.OrdinalIgnoreCase) == true)
                    continue;

                if (usesQualityProfile && beat.LabelsVisible == false)
                    continue;

                if (beat.CatA is not null && !string.IsNullOrWhiteSpace(topic.CatALabel))
                    Add(topic.CatALabel!, 44, labelAY, pill: false, xExpr: pa.LabelX,
                        window: (beat.StartSec, beat.EndSec));

                if (beat.CatB is not null && !string.IsNullOrWhiteSpace(topic.CatBLabel))
                {
                    var bY = pb is not null ? Math.Max(pb.LabelY, dialogueBottom + LabelClearanceY) : labelAY + 560;
                    var bX = pb?.LabelX ?? "90";
                    Add(topic.CatBLabel!, 44, bY, pill: false, xExpr: bX,
                        window: (beat.StartSec, beat.EndSec));
                }
            }
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(topic.CatALabel))
                Add(topic.CatALabel!, 44, labelAY, pill: false, xExpr: pa.LabelX);

            if (!string.IsNullOrWhiteSpace(topic.CatBLabel))
            {
                // With one subject on screen the second label still reads as dialogue,
                // so it goes lower down rather than being dropped.
                var bY = pb is not null ? Math.Max(pb.LabelY, dialogueBottom + LabelClearanceY) : labelAY + 560;
                var bX = pb?.LabelX ?? "90";
                Add(topic.CatBLabel!, 44, bY, pill: false, xExpr: bX);
            }
        }

        if (dialogue.Count == 0 && !string.IsNullOrWhiteSpace(topic.CaptionBottom))
            Add(topic.CaptionBottom!, 42, CanvasH - 320, pill: false);

        // With no text at all the stage passes through untouched.
        return idx == 0
            ? new TextStages("", inLabel, textFiles)
            : new TextStages(sb.ToString(), current, textFiles);
    }

    /// <summary>Dialogue sits between the title and the cats, above the role labels.</summary>
    private const int DialogueY = 560;
    private const int DialogueFontSize = 40;

    /// <summary>Each turn is confined to half the canvas, so it wraps far tighter than a caption.</summary>
    private const int DialogueMaxChars = 22;

    /// <summary>Widest settings the profiled beat path can produce, used for clearance math.</summary>
    private const int QualityDialogueFontSize = 58;
    private const int QualityDialogueMaxChars = 18;

    /// <summary>Clear air between the last dialogue line and the role tags below it.</summary>
    private const int LabelClearanceY = 58;

    /// <summary>Silence between turns. Without it the lines read as one block, not an exchange.</summary>
    private const double TurnGapSec = 0.45;

    private const double MinTurnSec = 0.9;
    private const double MaxTurnSec = 4.5;

    /// <summary>
    /// A textless visual instruction is an authored reaction/establishing shot, not a
    /// four-character caption.  Three and a half seconds makes sleeping, staring, and
    /// similar pauses legible while staying inside the usual 3-4 second meme-video hold.
    /// </summary>
    private const double SilentVisualHoldSec = 3.5;
    private const double MaxSilentVisualHoldSec = 4.0;

    /// <summary>
    /// Returns the shortest sensible reel duration for the authored conversation. This is
    /// intentionally independent of the configured default: callers can keep a longer house
    /// style, but a multi-beat story is never crushed into that default when its reading and
    /// visual-hold budget needs more room.
    /// </summary>
    public static int RecommendedDurationMs(Topic topic)
    {
        var lines = topic.Dialogue();
        if (lines.Count == 0) return 0;

        var usesQualityProfile = lines.Any(line =>
            StoryboardQualityProfile.Resolve(line.StoryboardProfile) is not null);
        var turns = lines.Select((line, index) =>
        {
            var seconds = RecommendedTurnSec(line);
            if (!usesQualityProfile) return seconds;

            // The reference-standard story is visual comedy, not a rapid subtitle dump.
            // Ordinary beats need recognition time; time cards need a clean read; and the
            // final physical payoff needs enough room for the complete interaction clip.
            var profiledFloor = line.StoryRole?.Trim().ToLowerInvariant() switch
            {
                StoryboardRole.Payoff when index == lines.Count - 1 => 4.25,
                StoryboardRole.TimeChange => 2.50,
                _ => 3.00,
            };
            return Math.Max(seconds, profiledFloor);
        }).Sum();
        var gaps = TurnGapSec * Math.Max(0, lines.Count - 1);
        const double loopBreathSec = 0.35;
        var totalSec = turns + gaps + loopBreathSec;

        // Quarter-second rounding keeps generated durations stable and FFmpeg-friendly.
        var roundedMs = (int)(Math.Ceiling(totalSec * 4) * 250);
        return Math.Clamp(roundedMs, MinDurationMs, MaxDurationMs);
    }

    private static double RecommendedTurnSec(DialogueLine line)
    {
        if (string.IsNullOrWhiteSpace(line.Text) && line.HasVisualDirective)
            return SilentVisualHoldSec;

        // Roughly 15 visible characters per second plus recognition time. The 2.15s floor
        // is important for short replies such as "Okay": seven quick turns plus their gaps
        // still need about 18s, not a compressed 13s montage.
        var chars = line.Text?.Trim().Length ?? 0;
        return Math.Clamp(1.0 + chars / 15.0, 2.15, MaxTurnSec);
    }

    /// <summary>
    /// Spreads the conversation across the reel, giving longer lines more screen time.
    ///
    /// Timing is derived rather than authored: the model is bad at inventing timestamps that
    /// add up, and a script written for a 13s reel would otherwise break when the target
    /// duration changes. Proportional-to-length matches the reference reel measured for this
    /// feature, where a 15.9s conversation ran 1-4s per line with ~0.5s gaps.
    /// </summary>
    internal static List<(DialogueLine Line, double Start, double End)> ScheduleDialogue(
        IReadOnlyList<DialogueLine> lines, double durationSec)
    {
        var result = new List<(DialogueLine, double, double)>();
        if (lines.Count == 0) return result;

        // Leave a beat at the end so the last line is readable before the reel loops.
        var usable = Math.Max(1.0, durationSec - 0.35);
        var gaps = TurnGapSec * (lines.Count - 1);
        var available = Math.Max(0.1, usable - gaps);
        var silentVisual = lines
            .Select(l => string.IsNullOrWhiteSpace(l.Text) && l.HasVisualDirective)
            .ToList();
        var weights = lines
            .Select(l => (double)Math.Max(4, l.Text?.Trim().Length ?? 0))
            .ToList();

        // Reserve a real visual hold before distributing reading time. Previously an empty
        // sleeping/reaction beat had weight 4 and was routinely squeezed to ~0.9s between
        // dialogue lines, too short for the viewer to register the changed cat pose.
        var durations = silentVisual
            .Select(isSilent => isSilent ? SilentVisualHoldSec : MinTurnSec)
            .ToList();

        var baseTotal = durations.Sum();
        if (baseTotal > available)
        {
            // A very short requested reel may make the preferred holds physically
            // impossible. Preserve their relative emphasis while fitting the hard limit.
            var scale = available / baseTotal;
            durations = durations.Select(d => d * scale).ToList();
        }
        else
        {
            var remaining = available - baseTotal;

            void DistributeExtra(IEnumerable<int> candidates, Func<int, double> ceiling)
            {
                var active = candidates.Where(i => durations[i] + 0.001 < ceiling(i)).ToList();
                while (remaining > 0.001 && active.Count > 0)
                {
                    var totalWeight = active.Sum(i => weights[i]);
                    var before = remaining;
                    var grants = active.ToDictionary(
                        i => i,
                        i => Math.Min(ceiling(i) - durations[i], before * weights[i] / totalWeight));

                    foreach (var (i, grant) in grants)
                    {
                        durations[i] += grant;
                        remaining -= grant;
                    }

                    if (before - remaining < 0.001) break;
                    active = active.Where(i => durations[i] + 0.001 < ceiling(i)).ToList();
                }
            }

            // Dialogue receives the flexible reading time. Silent beats already have their
            // preferred hold and only stretch toward four seconds if all dialogue is capped.
            DistributeExtra(Enumerable.Range(0, lines.Count).Where(i => !silentVisual[i]),
                _ => MaxTurnSec);
            DistributeExtra(Enumerable.Range(0, lines.Count).Where(i => silentVisual[i]),
                _ => MaxSilentVisualHoldSec);
        }

        var t = 0.0;
        for (var i = 0; i < lines.Count; i++)
        {
            var end = Math.Min(t + durations[i], durationSec);
            if (end <= t) break;                 // out of room; drop the tail rather than overlap
            result.Add((lines[i], t, end));
            t = end + TurnGapSec;
            if (t >= durationSec) break;
        }

        // Hold the punchline until the reel ends. A short exchange otherwise finishes early
        // and leaves the cats sitting under a blank canvas for the remainder - the reference
        // reel keeps its last line up to the final frame.
        if (result.Count > 0)
        {
            var (line, start, end) = result[^1];
            result[^1] = (line, start, Math.Max(end, durationSec - 0.15));
        }

        return result;
    }

    /// <summary>
    /// Conservative glyph-width estimate for a heavy sans face. Pill text wraps tighter
    /// because the box adds boxborderw padding on both sides beyond the glyph box.
    /// </summary>
    private static int MaxCharsFor(int fontSize, bool pill = false) =>
        Math.Max(10, (int)(CanvasW * (pill ? 0.76 : 0.88) / (fontSize * 0.56)));

    /// <summary>
    /// drawtext has no auto-wrap, so lines are broken before they reach ffmpeg. Emoji and
    /// other non-BMP glyphs are stripped first: the burn-in font cannot render them and
    /// they appear as tofu boxes in the finished reel.
    /// </summary>
    internal static string Wrap(string text, int maxChars)
    {
        text = new string(text.Where(c => c < 0xD800 && c != 0xFFFD && !(c >= 0x2190 && c <= 0x2BFF)).ToArray());
        var words = text.Replace("\r", "").Replace("\n", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var lines = new List<string>();
        var line = new StringBuilder();
        foreach (var w in words)
        {
            if (line.Length > 0 && line.Length + 1 + w.Length > maxChars)
            {
                lines.Add(line.ToString());
                line.Clear();
            }
            if (line.Length > 0) line.Append(' ');
            line.Append(w);
        }
        if (line.Length > 0) lines.Add(line.ToString());
        return string.Join('\n', lines);
    }

    // A label is centred on the same point its subject is centred on. Left-aligning them to
    // the edge of each zone (as this used to) pushed "Gf" nearly 20% of the frame away from
    // the cat it names, close enough to the centre line to read as the other cat's label.
    private static (Placement A, Placement? B) GetPlacements(string layout) => layout switch
    {
        // Each cat owns half the width; centred in its own half so they can never overlap.
        Layout.SideBySide => (
            new Placement(500, 640, "270-w/2", "1090-h/2", "270-text_w/2", 700),
            new Placement(500, 640, "810-w/2", "1090-h/2", "810-text_w/2", 700)),

        Layout.TopBottom => (
            new Placement(900, 520, "(W-w)/2", "600-h/2", "(w-text_w)/2", 300),
            new Placement(900, 520, "(W-w)/2", "1320-h/2", "(w-text_w)/2", 1020)),

        Layout.OverlaySmall => (
            new Placement(940, 1000, "(W-w)/2", "1120-h/2", "(w-text_w)/2", 560),
            new Placement(320, 340, "W-w-50", "1600-h/2", "w-text_w-50", 1400)),

        Layout.StackedCenter => (
            new Placement(760, 460, "(W-w)/2", "800-h/2", "(w-text_w)/2", 520),
            new Placement(760, 460, "(W-w)/2", "1380-h/2", "(w-text_w)/2", 1100)),

        // single_pov: one subject, centred, with the role tag on the left like the reference reels.
        _ => (new Placement(880, 1020, "(W-w)/2", "1190-h/2", "90", 660), null),
    };

    private static string NormalizeLayout(string? layout, bool hasB)
    {
        if (!hasB) return Layout.SinglePov;
        return layout switch
        {
            Layout.SideBySide or Layout.TopBottom or Layout.OverlaySmall or Layout.StackedCenter => layout,
            _ => Layout.SideBySide,
        };
    }

    private string FontFile()
    {
        var candidates = new[]
        {
            Path.Combine(paths.Fonts, "reel.ttf"),
            @"C:\Windows\Fonts\seguibl.ttf",   // Segoe UI Black - closest to the reference look
            @"C:\Windows\Fonts\arialbd.ttf",
            @"C:\Windows\Fonts\impact.ttf",
        };
        foreach (var c in candidates)
            if (File.Exists(c)) return c;
        return @"C:\Windows\Fonts\arial.ttf";
    }

    /// <summary>ffmpeg's filter parser treats ':' and '\' specially inside option values.</summary>
    internal static string EscapePath(string p) =>
        p.Replace('\\', '/').Replace(":", "\\:");

    private static string Fmt(double d) => d.ToString("0.###", CultureInfo.InvariantCulture);
}
