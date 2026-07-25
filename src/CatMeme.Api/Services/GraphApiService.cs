using System.Text.Json;
using CatMeme.Api.Domain;
using RestSharp;

namespace CatMeme.Api.Services;

public record PublishResult(bool Success, string? PostId, string? Error);

/// <summary>
/// Publishes finished reels to Facebook and Instagram via the Graph API.
///
/// Facebook takes a direct file upload. Instagram does NOT - it requires a publicly
/// reachable HTTPS url, so IG publishing needs PublicMediaBaseUrl configured to a
/// tunnel or CDN. Without it we fail loudly rather than silently skipping.
/// </summary>
public class GraphApiService(IConfiguration cfg, ILogger<GraphApiService> log)
{
    private const string GraphBase = "https://graph.facebook.com/v21.0";

    public async Task<PublishResult> PostToFacebookAsync(AppSettings s, string videoPath, string caption, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(s.FbPageId) || string.IsNullOrWhiteSpace(s.FbAccessToken))
            return new PublishResult(false, null, "Facebook page id or access token is not configured.");
        if (!File.Exists(videoPath))
            return new PublishResult(false, null, $"Video file missing: {videoPath}");

        try
        {
            using var client = new RestClient(new RestClientOptions { Timeout = TimeSpan.FromMinutes(10) });

            // 1. start a reel upload session
            var startReq = new RestRequest($"{GraphBase}/{s.FbPageId}/video_reels", Method.Post)
                .AddParameter("upload_phase", "start")
                .AddParameter("access_token", s.FbAccessToken);

            var startResp = await client.ExecuteAsync(startReq, ct);
            if (!startResp.IsSuccessful)
                return new PublishResult(false, null, $"FB start failed: {Describe(startResp)}");

            var videoId = ReadProp(startResp.Content, "video_id");
            var uploadUrl = ReadProp(startResp.Content, "upload_url");
            if (videoId is null || uploadUrl is null)
                return new PublishResult(false, null, $"FB start returned no upload session: {Trim(startResp.Content)}");

            // 2. upload the bytes
            var bytes = await File.ReadAllBytesAsync(videoPath, ct);
            var upReq = new RestRequest(uploadUrl, Method.Post);
            upReq.AddHeader("Authorization", $"OAuth {s.FbAccessToken}");
            upReq.AddHeader("offset", "0");
            upReq.AddHeader("file_size", bytes.Length.ToString());
            upReq.AddHeader("Content-Type", "application/octet-stream");
            upReq.AddBody(bytes, "application/octet-stream");

            var upResp = await client.ExecuteAsync(upReq, ct);
            if (!upResp.IsSuccessful)
                return new PublishResult(false, null, $"FB upload failed: {Describe(upResp)}");

            // 3. publish
            var finishReq = new RestRequest($"{GraphBase}/{s.FbPageId}/video_reels", Method.Post)
                .AddParameter("upload_phase", "finish")
                .AddParameter("video_id", videoId)
                .AddParameter("video_state", "PUBLISHED")
                .AddParameter("description", caption)
                .AddParameter("access_token", s.FbAccessToken);

            var finishResp = await client.ExecuteAsync(finishReq, ct);
            if (!finishResp.IsSuccessful)
                return new PublishResult(false, null, $"FB finish failed: {Describe(finishResp)}");

            log.LogInformation("Published reel {VideoId} to Facebook page {Page}", videoId, s.FbPageId);
            return new PublishResult(true, videoId, null);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Facebook publish threw");
            return new PublishResult(false, null, ex.Message);
        }
    }

    public async Task<PublishResult> PostToInstagramAsync(AppSettings s, string videoPath, string caption, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(s.IgUserId) || string.IsNullOrWhiteSpace(s.IgAccessToken))
            return new PublishResult(false, null, "Instagram user id or access token is not configured.");

        var publicBase = cfg["PublicMediaBaseUrl"];
        if (string.IsNullOrWhiteSpace(publicBase))
            return new PublishResult(false, null,
                "Instagram requires a public HTTPS video url. Set PublicMediaBaseUrl in .env to a reachable host.");

        try
        {
            var videoUrl = $"{publicBase.TrimEnd('/')}/media/{Path.GetFileName(videoPath)}";
            using var client = new RestClient(new RestClientOptions { Timeout = TimeSpan.FromMinutes(10) });

            // 1. container
            var createReq = new RestRequest($"{GraphBase}/{s.IgUserId}/media", Method.Post)
                .AddParameter("media_type", "REELS")
                .AddParameter("video_url", videoUrl)
                .AddParameter("caption", caption)
                .AddParameter("access_token", s.IgAccessToken);

            var createResp = await client.ExecuteAsync(createReq, ct);
            if (!createResp.IsSuccessful)
                return new PublishResult(false, null, $"IG container failed: {Describe(createResp)}");

            var containerId = ReadProp(createResp.Content, "id");
            if (containerId is null)
                return new PublishResult(false, null, $"IG container returned no id: {Trim(createResp.Content)}");

            // 2. wait for the container to finish transcoding
            var (ready, statusError) = await WaitForContainerAsync(client, containerId, s.IgAccessToken!, ct);
            if (!ready) return new PublishResult(false, null, statusError);

            // 3. publish
            var pubReq = new RestRequest($"{GraphBase}/{s.IgUserId}/media_publish", Method.Post)
                .AddParameter("creation_id", containerId)
                .AddParameter("access_token", s.IgAccessToken);

            var pubResp = await client.ExecuteAsync(pubReq, ct);
            if (!pubResp.IsSuccessful)
                return new PublishResult(false, null, $"IG publish failed: {Describe(pubResp)}");

            var postId = ReadProp(pubResp.Content, "id");
            log.LogInformation("Published reel {PostId} to Instagram {User}", postId, s.IgUserId);
            return new PublishResult(true, postId, null);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Instagram publish threw");
            return new PublishResult(false, null, ex.Message);
        }
    }

    /// <summary>IG containers transcode asynchronously; publishing early returns an error.</summary>
    private static async Task<(bool Ready, string? Error)> WaitForContainerAsync(
        RestClient client, string containerId, string token, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);

            var req = new RestRequest($"{GraphBase}/{containerId}", Method.Get)
                .AddParameter("fields", "status_code,status")
                .AddParameter("access_token", token);

            var resp = await client.ExecuteAsync(req, ct);
            var status = ReadProp(resp.Content, "status_code");

            if (status == "FINISHED") return (true, null);
            if (status is "ERROR" or "EXPIRED")
                return (false, $"IG container {status}: {Trim(resp.Content)}");
        }
        return (false, "IG container did not finish transcoding within 150s.");
    }

    private static string? ReadProp(string? json, string prop)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty(prop, out var v) ? v.ToString() : null;
        }
        catch (JsonException) { return null; }
    }

    private static string Describe(RestResponse r) =>
        $"HTTP {(int)r.StatusCode} {Trim(r.Content) ?? r.ErrorMessage}";

    private static string? Trim(string? s) =>
        s is null ? null : s.Length <= 400 ? s : s[..400];
}
