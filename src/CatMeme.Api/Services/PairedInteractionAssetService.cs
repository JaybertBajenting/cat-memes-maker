using CatMeme.Api.Data;
using CatMeme.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace CatMeme.Api.Services;

/// <summary>A hard-constrained request for a complete two-cat interaction shot.</summary>
public sealed record PairedInteractionRequest(
    string ActionTag,
    int MinimumDurationMs = 0,
    bool RequireAudio = false,
    IReadOnlyCollection<int>? ExcludeAssetIds = null);

/// <summary>A render-ready whole-scene asset, or an actionable reason none is eligible.</summary>
public sealed record PairedInteractionSelection(
    PairedInteractionAsset? Asset,
    string? FilePath,
    string? Error)
{
    public bool Ok => Asset is not null && FilePath is not null && Error is null;
}

public interface IPairedInteractionAssetSelector
{
    Task<PairedInteractionSelection> SelectAsync(
        PairedInteractionRequest request,
        CancellationToken ct = default);
}

/// <summary>
/// Exact matching and deterministic quality ranking shared by the database selector and
/// focused tests. There is intentionally no semantic fallback: the interaction itself is
/// the payoff, so substituting a merely angry or energetic scene would break the joke.
/// </summary>
public static class PairedInteractionMatching
{
    public static bool HasExactAction(PairedInteractionAsset asset, string action) =>
        TagsOf(asset.ActionTags).Contains(action.Trim());

    public static int UsableDurationMs(PairedInteractionAsset asset)
    {
        var start = Math.Clamp(asset.InPointMs, 0, Math.Max(0, asset.DurationMs));
        var configuredEnd = asset.OutPointMs <= 0 ? asset.DurationMs : asset.OutPointMs;
        var end = Math.Clamp(configuredEnd, start, Math.Max(start, asset.DurationMs));
        return end - start;
    }

    public static PairedInteractionAsset? SelectBestCandidate(
        IEnumerable<PairedInteractionAsset> assets,
        PairedInteractionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ActionTag) || request.MinimumDurationMs < 0)
            return null;

        var excluded = request.ExcludeAssetIds is { Count: > 0 }
            ? request.ExcludeAssetIds.ToHashSet()
            : null;

        return assets
            .Where(a => a.IsActive && a.QualityApproved)
            .Where(a => string.IsNullOrWhiteSpace(a.DisabledReason))
            .Where(a => excluded is null || !excluded.Contains(a.Id))
            .Where(a => HasExactAction(a, request.ActionTag))
            .Where(a => UsableDurationMs(a) >= request.MinimumDurationMs)
            .Where(a => !request.RequireAudio || (a.HasAudio && a.PreserveAudio))
            .OrderByDescending(a => a.QualityScore)
            .ThenBy(a => a.BarResidue)
            .ThenByDescending(a => UsableDurationMs(a))
            .ThenBy(a => a.Id)
            .FirstOrDefault();
    }

    public static HashSet<string> TagsOf(string? raw) => new(
        (raw ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.ToLowerInvariant()),
        StringComparer.OrdinalIgnoreCase);
}

public sealed class PairedInteractionAssetService(
    CatMemeDbContext db,
    PipelinePaths paths,
    ILogger<PairedInteractionAssetService> log) : IPairedInteractionAssetSelector
{
    public async Task<PairedInteractionSelection> SelectAsync(
        PairedInteractionRequest request,
        CancellationToken ct = default)
    {
        var action = request.ActionTag?.Trim() ?? "";
        if (action.Length == 0)
            return Failed("a paired interaction action is required");
        if (request.MinimumDurationMs < 0)
            return Failed("minimum interaction duration cannot be negative");

        var assets = await db.PairedInteractionAssets.AsNoTracking()
            .Where(a => a.IsActive && a.QualityApproved)
            .ToListAsync(ct);

        // Missing media is never returned to the renderer. Keep looking so one stale row
        // cannot hide another approved asset performing the same exact action.
        var files = assets
            .Select(a => new { Asset = a, Path = paths.CatClip(a.Filename) })
            .Where(x => File.Exists(x.Path))
            .ToList();

        var selected = PairedInteractionMatching.SelectBestCandidate(
            files.Select(x => x.Asset), request with { ActionTag = action });
        if (selected is not null)
        {
            var path = files.First(x => x.Asset.Id == selected.Id).Path;
            log.LogInformation(
                "Paired interaction '{Action}' selected asset {AssetId} ({Name})",
                action, selected.Id, selected.Name);
            return new PairedInteractionSelection(selected, path, null);
        }

        var exact = assets.Where(a => PairedInteractionMatching.HasExactAction(a, action)).ToList();
        if (exact.Count > 0 && exact.All(a => !File.Exists(paths.CatClip(a.Filename))))
            return Failed($"paired interaction '{action}' is registered but its media file is missing");

        var available = string.Join(", ", assets
            .SelectMany(a => PairedInteractionMatching.TagsOf(a.ActionTags))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x));
        var suffix = available.Length == 0 ? "none" : available;
        return Failed(
            $"no active quality-approved paired interaction performs exact action '{action}'. " +
            $"Available actions: {suffix}");
    }

    private static PairedInteractionSelection Failed(string error) =>
        new(null, null, error);
}
