using System.Net.Http.Headers;
using System.Text.Json;
using CatMeme.Api.Domain;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace CatMeme.Api.Services;

/// <summary>
/// Produces the 1080x1920 background plate: Pexels stock, Codex CLI generation,
/// or a generated gradient so the pipeline still runs with no API keys configured.
/// </summary>
public class BackgroundAssetService(
    IHttpClientFactory httpFactory,
    ProcessRunner runner,
    PipelinePaths paths,
    IConfiguration cfg,
    ILogger<BackgroundAssetService> log)
{
    private const int W = RenderService.CanvasW;
    private const int H = RenderService.CanvasH;

    /// <summary>Reference reels are shot in ordinary domestic rooms, so queries stay indoors.</summary>
    private static readonly Dictionary<string, string[]> MoodQueries = new()
    {
        ["funny"] = ["cozy living room", "messy bedroom interior", "modern apartment couch"],
        ["wholesome"] = ["sunlit bedroom", "cozy blanket bed", "warm home interior"],
        ["dramatic"] = ["dark bedroom night", "rainy window room", "moody apartment interior"],
    };

    /// <summary>
    /// Picks the room from the joke before falling back to its broad mood.  A "funny"
    /// wake-up script is still a bedroom scene; treating mood as the only signal is how the
    /// 5 AM reel ended up on a kitchen counter and a living-room sofa.
    /// </summary>
    internal static string SceneFor(Topic topic)
    {
        var text = topic.TopicText.ToLowerInvariant();

        static bool Has(string haystack, params string[] needles) =>
            needles.Any(haystack.Contains);

        if (Has(text, "wake", "woke", "sleep", "asleep", "bed", "blanket", "alarm",
                "snor", "5 am", "5am", "morning"))
            return "cozy dim bedroom at night with an unmade bed and bedside lamp";

        if (Has(text, "cook", "food", "snack", "pizza", "dinner", "breakfast", "kitchen"))
            return "cozy lived-in home kitchen";

        if (Has(text, "game", "gaming", "controller", "console", "remote", "tv"))
            return "cozy apartment living room with a television";

        if (Has(text, "work", "meeting", "email", "office", "study", "homework"))
            return "small lived-in home office";

        if (Has(text, "gym", "workout", "exercise", "weights"))
            return "quiet apartment gym interior";

        var choices = MoodQueries.TryGetValue(topic.Mood, out var q) ? q : MoodQueries["funny"];
        // Stable across retries: a failed render should not silently move to another room.
        var hash = topic.TopicText.Aggregate(17, (value, ch) => unchecked(value * 31 + ch));
        return choices[(hash & int.MaxValue) % choices.Length];
    }

    public async Task<string> GetBackgroundAsync(AppSettings settings, Topic topic, CancellationToken ct = default)
    {
        var dest = Path.Combine(paths.Backgrounds, $"bg_{topic.Id}_{Guid.NewGuid():N}.png");

        try
        {
            if (settings.BackgroundSource == "codex")
            {
                if (await TryCodexAsync(topic, dest, ct)) return dest;
                log.LogWarning("Codex background generation failed; falling back to gradient.");
            }
            else if (!string.IsNullOrWhiteSpace(settings.PexelsApiKey))
            {
                if (await TryPexelsAsync(settings.PexelsApiKey!, topic, dest, ct)) return dest;
                log.LogWarning("Pexels fetch failed; falling back to gradient.");
            }
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Background source threw; falling back to gradient.");
        }

        await GenerateGradientAsync(topic, dest, ct);
        return dest;
    }

    private async Task<bool> TryPexelsAsync(string apiKey, Topic topic, string dest, CancellationToken ct)
    {
        var query = SceneFor(topic);

        var http = httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(45);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(apiKey);

        var url = $"https://api.pexels.com/v1/search?query={Uri.EscapeDataString(query)}&orientation=portrait&per_page=20";
        using var resp = await http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode)
        {
            log.LogWarning("Pexels returned {Status} for '{Query}'", resp.StatusCode, query);
            return false;
        }

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (!doc.RootElement.TryGetProperty("photos", out var photos) || photos.GetArrayLength() == 0)
            return false;

        var pick = photos[Random.Shared.Next(photos.GetArrayLength())];
        var src = pick.GetProperty("src");
        var imgUrl = src.TryGetProperty("portrait", out var p) ? p.GetString()
                   : src.GetProperty("large").GetString();
        if (imgUrl is null) return false;

        var bytes = await http.GetByteArrayAsync(imgUrl, ct);
        await CropToCanvasAsync(bytes, dest, ct);
        return File.Exists(dest);
    }

    private async Task<bool> TryCodexAsync(Topic topic, string dest, CancellationToken ct)
    {
        var cli = cfg["CODEX_CLI_PATH"] ?? "codex";
        var scene = SceneFor(topic);
        var prompt = $"Generate a photorealistic vertical 9:16 background image of {scene}. " +
                     $"Cinematic lighting, no people, no text. Save it to: {dest}";

        var r = await runner.RunAsync(cli, ["exec", "--full-auto", prompt], TimeSpan.FromMinutes(8), ct: ct);
        if (!File.Exists(dest))
        {
            log.LogWarning("Codex CLI produced no file at {Dest}. Output: {Out}", dest, FFmpegService.Tail(r.All, 300));
            return false;
        }

        var bytes = await File.ReadAllBytesAsync(dest, ct);
        await CropToCanvasAsync(bytes, dest, ct);
        return true;
    }

    /// <summary>
    /// Blur radius applied to a photo plate. Measured against a reference reel: the band
    /// behind its dialogue scores a Laplacian variance of ~5, while an untouched Pexels photo
    /// scores 1100-1800. A sharp plate is not "more detail", it is competition - small white
    /// dialogue text lands on top of foliage and furniture edges and stops being readable.
    /// </summary>
    private const float PlateBlurSigma = 9f;

    /// <summary>
    /// Brightness multiplier. The same reference measures ~101 mean luminance against our
    /// ~170, so the subject and text carry the frame rather than fighting the wallpaper.
    /// </summary>
    private const float PlateBrightness = 0.62f;

    /// <summary>Pulled down slightly with the light, so the plate reads as a backdrop.</summary>
    private const float PlateSaturation = 0.85f;

    private static async Task CropToCanvasAsync(byte[] bytes, string dest, CancellationToken ct)
    {
        using var img = Image.Load<Rgba32>(bytes);
        img.Mutate(x => x
            .Resize(new ResizeOptions
            {
                Size = new Size(W, H),
                Mode = ResizeMode.Crop,       // cover-fit, never letterbox
                Position = AnchorPositionMode.Center,
            })
            // Order matters: blur first, then darken. Darkening first would compress the
            // range the blur averages over and leave a flatter, muddier plate.
            .GaussianBlur(PlateBlurSigma)
            .Brightness(PlateBrightness)
            .Saturate(PlateSaturation));
        await img.SaveAsPngAsync(dest, ct);
    }

    /// <summary>
    /// Deterministic per-mood gradient so a keyless install still renders real videos.
    /// Written with core ImageSharp pixel access - the Drawing package is commercially
    /// licensed and a vertical gradient does not justify that dependency.
    /// </summary>
    private static async Task GenerateGradientAsync(Topic topic, string dest, CancellationToken ct)
    {
        var (top, bottom) = topic.Mood switch
        {
            "dramatic" => ((27, 42, 74), (10, 15, 28)),
            "wholesome" => ((255, 217, 160), (201, 123, 90)),
            _ => ((243, 198, 232), (123, 94, 167)),
        };

        using var img = new Image<Rgba32>(W, H);
        img.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var t = (float)y / (accessor.Height - 1);

                // Darken the top so white title text stays readable over the plate.
                var shade = 1f - 0.28f * MathF.Max(0f, 1f - t / 0.45f);

                var r = (byte)((top.Item1 + (bottom.Item1 - top.Item1) * t) * shade);
                var g = (byte)((top.Item2 + (bottom.Item2 - top.Item2) * t) * shade);
                var b = (byte)((top.Item3 + (bottom.Item3 - top.Item3) * t) * shade);

                var row = accessor.GetRowSpan(y);
                row.Fill(new Rgba32(r, g, b, 255));
            }
        });

        await img.SaveAsPngAsync(dest, ct);
    }
}
