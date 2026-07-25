using CatMeme.Api.Domain;
using CatMeme.Api.Services;
using Xunit;

namespace CatMeme.Api.Tests;

public sealed class PairedInteractionAssetTests
{
    [Fact]
    public void Action_matching_is_exact_not_substring_based()
    {
        var asset = Asset(actionTags: "slap,angry-payoff");

        Assert.True(PairedInteractionMatching.HasExactAction(asset, "SLAP"));
        Assert.False(PairedInteractionMatching.HasExactAction(asset, "slapping"));
        Assert.False(PairedInteractionMatching.HasExactAction(asset, "lap"));
    }

    [Fact]
    public void Selector_rejects_inactive_unapproved_short_and_audio_less_assets()
    {
        var candidates = new[]
        {
            Asset(id: 1, isActive: false),
            Asset(id: 2, qualityApproved: false),
            Asset(id: 3, outPointMs: 900),
            Asset(id: 4, hasAudio: false),
            Asset(id: 5, qualityScore: .96),
        };

        var selected = PairedInteractionMatching.SelectBestCandidate(
            candidates,
            new PairedInteractionRequest("slap", MinimumDurationMs: 1_000, RequireAudio: true));

        Assert.NotNull(selected);
        Assert.Equal(5, selected.Id);
    }

    [Fact]
    public void Selector_uses_quality_then_clean_keying_with_stable_ties()
    {
        var selected = PairedInteractionMatching.SelectBestCandidate(
            new[]
            {
                Asset(id: 8, qualityScore: .90, barResidue: .02),
                Asset(id: 7, qualityScore: .98, barResidue: .03),
                Asset(id: 6, qualityScore: .98, barResidue: 0),
                Asset(id: 5, qualityScore: .98, barResidue: 0),
            },
            new PairedInteractionRequest("slap"));

        Assert.NotNull(selected);
        Assert.Equal(5, selected.Id);
    }

    [Fact]
    public void Exclusion_and_in_out_points_are_hard_constraints()
    {
        var first = Asset(id: 1, durationMs: 5_100, inPointMs: 500, outPointMs: 4_000);
        var second = Asset(id: 2, durationMs: 5_100, inPointMs: 1_000, outPointMs: 4_900);

        Assert.Equal(3_500, PairedInteractionMatching.UsableDurationMs(first));
        Assert.Equal(3_900, PairedInteractionMatching.UsableDurationMs(second));

        var selected = PairedInteractionMatching.SelectBestCandidate(
            new[] { first, second },
            new PairedInteractionRequest(
                "slap", MinimumDurationMs: 3_800, ExcludeAssetIds: new[] { 1 }));

        Assert.NotNull(selected);
        Assert.Equal(2, selected.Id);
    }

    private static PairedInteractionAsset Asset(
        int id = 1,
        string actionTags = "slap",
        int durationMs = 5_100,
        int inPointMs = 0,
        int outPointMs = 5_100,
        bool hasAudio = true,
        bool isActive = true,
        bool qualityApproved = true,
        double qualityScore = 1,
        double barResidue = 0) => new()
        {
            Id = id,
            Name = $"asset-{id}",
            Filename = $"asset-{id}.mp4",
            ActionTags = actionTags,
            DurationMs = durationMs,
            InPointMs = inPointMs,
            OutPointMs = outPointMs,
            HasAudio = hasAudio,
            PreserveAudio = hasAudio,
            IsActive = isActive,
            QualityApproved = qualityApproved,
            QualityScore = qualityScore,
            BarResidue = barResidue,
        };
}
