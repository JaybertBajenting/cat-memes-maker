using CatMeme.Api.Domain;

namespace CatMeme.Api.Services;

public sealed record StoryboardCastabilityResult(bool Ok, IReadOnlyList<string> Errors)
{
    public string Summary => Errors.Count == 0 ? "ok" : string.Join("; ", Errors);
}

/// <summary>
/// Proves that every authored visual beat can be performed by the assets which exist now.
/// The model is never allowed to treat the action vocabulary as a promise that every pair
/// or whole-scene interaction is actually available.
/// </summary>
public sealed class StoryboardCastabilityService(
    ClipLibraryService clips,
    IPairedInteractionAssetSelector interactions)
{
    public async Task<StoryboardCastabilityResult> CheckAsync(
        Topic topic,
        int targetDurationMs,
        CancellationToken ct = default)
    {
        var errors = new List<string>();
        var durationSec = Math.Clamp(
            targetDurationMs / 1000.0,
            RenderService.MinDurationMs / 1000.0,
            RenderService.MaxDurationMs / 1000.0);
        var preferredClipMs = Math.Max(0, targetDurationMs / 3);

        // The base cast remains relevant for legacy/static requests and is also the pinned
        // cast stored on Video. A profiled storyboard must satisfy this plus every override.
        var baseCast = await clips.CastAsync(topic, preferredClipMs, null, ct);
        if (!baseCast.Ok)
            errors.Add(baseCast.Error ?? "the base cat cast is unavailable");

        var drafts = VisualBeatPlanner.Build(topic, durationSec);
        var checkedStates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < drafts.Count; i++)
        {
            var beat = drafts[i];
            var interaction = beat.InteractionAction?.Trim().ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(interaction) &&
                interaction != StoryboardInteractionAction.None)
            {
                var minimumMs = Math.Min(
                    4_500,
                    Math.Max(1_000, (int)Math.Round((beat.EndSec - beat.StartSec) * 1000)));
                var selection = await interactions.SelectAsync(new PairedInteractionRequest(
                    interaction,
                    MinimumDurationMs: minimumMs,
                    RequireAudio: interaction == StoryboardInteractionAction.Slap), ct);
                if (!selection.Ok)
                    errors.Add($"beat {i + 1} interaction '{interaction}': {selection.Error}");
                continue;
            }

            if (!beat.ShowA && !beat.ShowB) continue;
            var key = $"{beat.ShowA}:{beat.ActionA}|{beat.ShowB}:{beat.ActionB}";
            if (!checkedStates.Add(key)) continue;

            var cast = await clips.CastBeatAsync(
                topic,
                beat.ActionA,
                beat.ShowB ? beat.ActionB : null,
                beat.ShowA,
                beat.ShowB,
                preferredClipMs,
                null,
                ct);
            if (!cast.Ok)
                errors.Add($"beat {i + 1} ({key}): {cast.Error}");
        }

        return new StoryboardCastabilityResult(errors.Count == 0, errors);
    }
}
