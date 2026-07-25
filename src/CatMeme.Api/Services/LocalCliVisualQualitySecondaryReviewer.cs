using System.Text.Json;
using CatMeme.Api.Jobs;

namespace CatMeme.Api.Services;

/// <summary>
/// Semantic second opinion using the locally authenticated Codex or Claude CLI selected in
/// Settings. It runs only when the review request enables it; deterministic pixel/metadata
/// checks remain primary and a high-confidence semantic rejection can only make QA stricter.
/// </summary>
public sealed class LocalCliVisualQualitySecondaryReviewer(
    ProcessRunner runner,
    CatMeme.Api.Data.CatMemeDbContext db,
    IConfiguration configuration,
    ILogger<LocalCliVisualQualitySecondaryReviewer> log) : IVisualQualitySecondaryReviewer
{
    public async Task<VisualQualitySecondaryResult> ReviewAsync(
        VisualQualitySecondaryContext context,
        CancellationToken ct = default)
    {
        // Visual semantics use the same authenticated CLI selected in Settings for script
        // generation. An environment override remains useful for headless deployments, but
        // users do not need to configure the provider twice in the desktop app.
        var settings = await SettingsAccessor.GetAsync(db, ct);
        var provider = (configuration["VISUAL_QA_CLI_PROVIDER"] ?? settings.ScriptProvider)
            .Trim()
            .ToLowerInvariant();
        if (provider is not ("codex" or "claude"))
        {
            return Unavailable(
                "local-cli",
                "The selected visual-review provider is not 'codex' or 'claude'.");
        }

        var prompt = BuildPrompt(context);
        var workingDirectory = Path.GetDirectoryName(context.ContactSheetPath)
                               ?? Environment.CurrentDirectory;
        string raw;

        try
        {
            if (provider == "codex")
            {
                var command = configuration["CODEX_CLI_PATH"] ?? "codex";
                var output = Path.Combine(workingDirectory, $"secondary-{Guid.NewGuid():N}.json");
                try
                {
                    var result = await runner.RunAsync(command,
                        ["exec", "--ignore-user-config", "--ephemeral", "--sandbox", "read-only", "--skip-git-repo-check",
                         "--color", "never", "--output-last-message", output, "-"],
                        TimeSpan.FromMinutes(4), stdin: prompt, workingDirectory: workingDirectory, ct: ct);
                    if (!result.Ok)
                        return Unavailable(provider, $"CLI failed: {FFmpegService.Tail(result.All)}");
                    raw = File.Exists(output) ? await File.ReadAllTextAsync(output, ct) : result.StdOut;
                }
                finally
                {
                    FFmpegService.TryDelete(output);
                }
            }
            else
            {
                var command = configuration["CLAUDE_CLI_PATH"] ?? "claude";
                var result = await runner.RunAsync(command,
                    ["-p", "--output-format", "text"],
                    TimeSpan.FromMinutes(4), stdin: prompt, workingDirectory: workingDirectory, ct: ct);
                if (!result.Ok)
                    return Unavailable(provider, $"CLI failed: {FFmpegService.Tail(result.All)}");
                raw = result.StdOut;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Optional {Provider} visual reviewer was unavailable", provider);
            return Unavailable(provider, ex.Message);
        }

        try
        {
            var json = ExtractJson(raw);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var passed = root.TryGetProperty("passed", out var passNode) && passNode.GetBoolean();
            var confidence = root.TryGetProperty("confidence", out var confidenceNode)
                ? Math.Clamp(confidenceNode.GetDouble(), 0, 1)
                : 0;
            var notes = root.TryGetProperty("notes", out var notesNode)
                ? notesNode.GetString() ?? "No notes supplied."
                : "No notes supplied.";
            var suspects = root.TryGetProperty("suspect_clip_ids", out var suspectNode) &&
                           suspectNode.ValueKind == JsonValueKind.Array
                ? suspectNode.EnumerateArray()
                    .Where(x => x.ValueKind == JsonValueKind.Number && x.TryGetInt32(out _))
                    .Select(x => x.GetInt32())
                    .Distinct()
                    .OrderBy(x => x)
                    .ToList()
                : [];
            return new VisualQualitySecondaryResult(provider, passed, confidence, notes, suspects);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            log.LogWarning(ex, "Optional {Provider} visual reviewer returned invalid JSON", provider);
            return Unavailable(provider, "CLI returned an invalid review payload.");
        }
    }

    private static string BuildPrompt(VisualQualitySecondaryContext context)
    {
        var beats = string.Join('\n', context.DeterministicBeats.Select(b =>
            $"beat {b.BeatIndex}: {b.StartSec:0.##}-{b.EndSec:0.##}s " +
            $"stage={b.Stage ?? "normal"} expectedA={b.ExpectedActionA}/{b.ExpectedA} " +
            $"expectedB={b.ExpectedActionB}/{b.ExpectedB} deterministic={b.Score:0.###} " +
            $"clips=[{string.Join(',', b.Regions.Select(r =>
                $"{r.Role}:{r.ClipId?.ToString() ?? "none"}/{r.ExpectedAction}"))}]"));

        return $$"""
            You are a SECONDARY visual reviewer. Inspect the local contact sheet at:
            {{context.ContactSheetPath}}

            Topic: {{context.TopicText}}

            Storyboard evidence:
            {{beats}}

            Open and inspect the contact-sheet image with your image tool. Tiles are ordered
            left-to-right, top-to-bottom, with two sampled tiles per normal-length beat.
            Judge only obvious semantic mismatches the deterministic checks cannot see:
            wrong animal/pose/emotion, incoherent shot progression, or an inappropriate cat
            for the stated action. Do not reject typography or minor compositing details.
            Return JSON only:
            {"passed":true,"confidence":0.0,"notes":"short reason","suspect_clip_ids":[]}
            """;
    }

    private static string ExtractJson(string raw)
    {
        var text = raw.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var firstLine = text.IndexOf('\n');
            var lastFence = text.LastIndexOf("```", StringComparison.Ordinal);
            if (firstLine >= 0 && lastFence > firstLine)
                text = text[(firstLine + 1)..lastFence].Trim();
        }

        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : text;
    }

    private static VisualQualitySecondaryResult Unavailable(string reviewer, string reason) =>
        // The reference profile promises an actual semantic frame review. Failing open here
        // would silently reduce "appropriate cat" to trusting possibly-wrong database tags.
        new(reviewer, false, 1, $"Semantic reviewer unavailable: {reason}", []);
}
