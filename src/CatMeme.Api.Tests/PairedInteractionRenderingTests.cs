using CatMeme.Api.Services;
using Xunit;

namespace CatMeme.Api.Tests;

public sealed class PairedInteractionRenderingTests
{
    [Fact]
    public void Beat_shorter_than_curated_window_decodes_only_the_beat()
    {
        var input = Interaction(inPointSec: 1.2, outPointSec: 4.7, usableMs: 3_500);

        var window = RenderService.ResolveInteractionWindow(input, beatDurationSec: 2.0);

        Assert.Equal(1.2, window.InPointSec, 3);
        Assert.Equal(4.7, window.OutPointSec, 3);
        Assert.Equal(2.0, window.DecodeDurationSec, 3);
        Assert.Equal(0, window.HoldLastFrameSec, 3);
    }

    [Fact]
    public void Beat_longer_than_curated_window_holds_last_frame_instead_of_looping()
    {
        var input = Interaction(inPointSec: 1.2, outPointSec: 4.7, usableMs: 3_500);

        var window = RenderService.ResolveInteractionWindow(input, beatDurationSec: 5.0);

        Assert.Equal(3.5, window.DecodeDurationSec, 3);
        Assert.Equal(1.5, window.HoldLastFrameSec, 3);

        var options = RenderService.InteractionInputOptions(input, window);
        Assert.DoesNotContain("-stream_loop", options);
        Assert.Equal(new[] { "-ss", "1.2", "-t", "3.5", "-i", "paired.mp4" }, options);
    }

    [Fact]
    public void Preflight_accepts_window_that_matches_source_and_usable_duration()
    {
        var errors = RenderPreflight.ValidateInteractionWindow(
            Interaction(inPointSec: 1.2, outPointSec: 4.7, usableMs: 3_500),
            sourceDurationMs: 5_100);

        Assert.Empty(errors);
    }

    [Fact]
    public void Preflight_rejects_out_point_beyond_source_and_duration_disagreement()
    {
        var errors = RenderPreflight.ValidateInteractionWindow(
            Interaction(inPointSec: 1.2, outPointSec: 5.8, usableMs: 3_500),
            sourceDurationMs: 5_100);

        Assert.Contains(errors, e => e.Contains("exceeds", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, e => e.Contains("declares", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Preflight_reports_cat_and_paired_asset_recasts_in_separate_id_sets()
    {
        var report = new PreflightReport(
            [
                new PreflightIssue(PreflightFault.ClipA, "bad cat", ClipId: 7),
                new PreflightIssue(
                    PreflightFault.Interaction,
                    "bad pair",
                    InteractionAssetId: 7),
            ],
            EffectiveDurationMs: 5_000);

        Assert.Equal(new[] { 7 }, report.RecastClipIds);
        Assert.Equal(new[] { 7 }, report.RecastInteractionAssetIds);
        Assert.Null(report.Issues[1].ClipId);
    }

    private static RenderInteractionInput Interaction(
        double inPointSec,
        double outPointSec,
        int usableMs) => new(
            7,
            "paired.mp4",
            new ClipAnalysis("0x00FB00", 0.10, "620:644:328:30", 620, 644, usableMs),
            PreserveAudio: true,
            AudioGainDb: 0,
            InPointSec: inPointSec,
            OutPointSec: outPointSec);
}
