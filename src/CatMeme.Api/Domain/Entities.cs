namespace CatMeme.Api.Domain;

public static class VideoStatus
{
    public const string Rendering = "rendering";
    public const string AwaitingApproval = "awaiting_approval";
    public const string Ready = "ready";
    public const string Posted = "posted";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    public const string Rejected = "rejected";
}

public static class TopicStatus
{
    public const string Pending = "pending";
    public const string Editing = "editing";
    public const string Done = "done";
    public const string Failed = "failed";
}

public static class PostStatus
{
    public const string Scheduled = "scheduled";
    public const string Posted = "posted";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
}

/// <summary>
/// Layout of the cat subject(s) over the background.
///
/// "side_by_side" is the default: a survey of 18 reels across all three reference pages
/// shows two or more composited cats in the large majority of posts, including every
/// post above 500K views. single_pov exists for the minority of one-cat jokes.
/// </summary>
public static class Layout
{
    public const string SinglePov = "single_pov";
    public const string SideBySide = "side_by_side";
    public const string TopBottom = "top_bottom";
    public const string OverlaySmall = "overlay_small";
    public const string StackedCenter = "stacked_center";
}

public class AppSettings
{
    public int Id { get; set; }
    public string ScriptProvider { get; set; } = "claude";      // "claude" | "codex"
    public string BackgroundSource { get; set; } = "pexels";   // "pexels" | "codex"
    public string? PexelsApiKey { get; set; }
    public string? FbPageId { get; set; }
    public string? FbAccessToken { get; set; }
    public string? IgUserId { get; set; }
    public string? IgAccessToken { get; set; }
    public string DefaultPostCaptionTemplate { get; set; } = "{topic} {hashtags}";
    public bool AutoUpload { get; set; } = false;
    public string TargetTimezone { get; set; } = "America/New_York";
    public int BatchSize { get; set; } = 5;

    /// <summary>Daily posting slots, stored as UTC "HH:mm" derived from TargetTimezone.</summary>
    public string MorningSlotUtc { get; set; } = "13:00";
    public string AfternoonSlotUtc { get; set; } = "16:00";
    public string EveningSlotUtc { get; set; } = "23:00";

    /// <summary>
    /// Target reel duration. Across 18 reference reels the mean is 13.3s and every post
    /// above 500K views runs 15-30s; the two shortest (4.6s, 4.9s) were the worst
    /// performers in the sample at 592 and 260 views.
    /// </summary>
    public int TargetDurationMs { get; set; } = 13000;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class CatClip
{
    public int Id { get; set; }
    public string Filename { get; set; } = "";

    /// <summary>
    /// Compilation this clip was cut from. Clips from any source other than the configured
    /// current one are retired, so swapping source videos does not silently mix libraries.
    /// </summary>
    public string SourceFile { get; set; } = "";
    public int DurationMs { get; set; }
    public string MoodTags { get; set; } = "";      // comma separated: "funny,energetic"
    public string Facing { get; set; } = "neutral"; // "left" | "right" | "neutral"
    public bool IsActive { get; set; } = true;

    /// <summary>Detected chroma key colour for THIS clip (e.g. "0x1FD01F"). Greens vary per clip.</summary>
    public string KeyColor { get; set; } = "0x00FF00";

    /// <summary>Similarity value for the chromakey filter, tuned per clip.</summary>
    public double KeySimilarity { get; set; } = 0.30;

    /// <summary>ffmpeg crop expression removing letterbox bars, e.g. "480:720:400:0". Null = no crop.</summary>
    public string? CropFilter { get; set; }

    public int SourceWidth { get; set; }
    public int SourceHeight { get; set; }

    /// <summary>
    /// Fraction of the keyed subject that is pure black - letterbox bars trapped inside
    /// the subject box. High values render as a black rectangle around the cat.
    /// </summary>
    public double BarResidue { get; set; }

    /// <summary>
    /// How completely the subject fills its bounding box. A cat silhouette leaves empty
    /// corners (~0.5); a rectangular video inset pasted onto the green plate approaches 1.0
    /// and composites as a hard-edged rectangle.
    /// </summary>
    public double SubjectFill { get; set; }

    /// <summary>Why the clip was auto-disabled, shown in the Clips table.</summary>
    public string? DisabledReason { get; set; }

    /// <summary>
    /// Permanently withdrawn: cut from a superseded source video, or replaced by a later
    /// re-import. Distinct from IsActive, which reanalyze flips both ways as clip quality
    /// is re-measured. Retirement is never undone automatically.
    /// </summary>
    public bool IsRetired { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// One turn in a reel's conversation. Timing is NOT stored: it is derived at render time
/// from the line lengths and the reel's duration, so the same script still reads correctly
/// if the target duration changes.
/// </summary>
/// <param name="Speaker">"a" or "b" - which cat is talking, and therefore which side the line sits on.</param>
/// <param name="CatAAction">
/// Optional visual-state change for cat A at this turn. Null keeps its previous action.
/// </param>
/// <param name="CatBAction">
/// Optional visual-state change for cat B at this turn. Null keeps its previous action.
/// </param>
/// <param name="Stage">
/// Optional non-cat stage cue, such as hide_both during a pause or transition for a
/// centred time-card. It applies to this beat only; later beats show both cats by default.
/// </param>
public record DialogueLine(
    string Speaker,
    string Text,
    [property: System.Text.Json.Serialization.JsonPropertyName("cat_a_action")] string? CatAAction = null,
    [property: System.Text.Json.Serialization.JsonPropertyName("cat_b_action")] string? CatBAction = null,
    [property: System.Text.Json.Serialization.JsonPropertyName("stage")] string? Stage = null,
    [property: System.Text.Json.Serialization.JsonPropertyName("story_role")] string? StoryRole = null,
    [property: System.Text.Json.Serialization.JsonPropertyName("directives")] StoryboardDirectives? Directives = null,
    [property: System.Text.Json.Serialization.JsonPropertyName("storyboard_profile")] string? StoryboardProfile = null)
{
    public const string SpeakerA = "a";
    public const string SpeakerB = "b";

    /// <summary>Computed, so it must not be written into the stored JSON.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsA => string.Equals(Speaker, SpeakerA, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when this turn changes what is visible, not only what is written.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasVisualDirective =>
        !string.IsNullOrWhiteSpace(CatAAction) ||
        !string.IsNullOrWhiteSpace(CatBAction) ||
        !string.IsNullOrWhiteSpace(Stage) ||
        Directives is not null;

    public static IReadOnlyList<DialogueLine> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<DialogueLine>>(json,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        }
        catch (System.Text.Json.JsonException)
        {
            // A malformed blob must not take a render down; it degrades to the static style.
            return [];
        }
    }

    public static string Serialize(IEnumerable<DialogueLine> lines) =>
        System.Text.Json.JsonSerializer.Serialize(lines);

    /// <summary>
    /// Parses hand-written dialogue, one turn per line: "a: is that my name?".
    ///
    /// Deliberately forgiving about the speaker token - "a", "A", "cat a", "left" and "1"
    /// all mean the same cat - because this is typed into a textarea by hand. A line with
    /// no recognisable prefix is skipped rather than guessed at, so a stray note in the box
    /// cannot silently become a spoken turn.
    /// </summary>
    public static IReadOnlyList<DialogueLine> ParseLines(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        var result = new List<DialogueLine>();
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            var colon = line.IndexOf(':');
            if (colon <= 0) continue;

            var who = line[..colon].Trim().ToLowerInvariant();
            var said = line[(colon + 1)..].Trim();
            if (said.Length == 0) continue;

            var speaker = who switch
            {
                "a" or "cat a" or "cat_a" or "left" or "1" => SpeakerA,
                "b" or "cat b" or "cat_b" or "right" or "2" => SpeakerB,
                _ => null,
            };
            if (speaker is null) continue;

            result.Add(new DialogueLine(speaker, said));
        }
        return result;
    }
}

/// <summary>Safe stage instructions accepted on a dialogue beat.</summary>
public static class DialogueStage
{
    public const string HideBoth = "hide_both";
    public const string HideA = "hide_a";
    public const string HideB = "hide_b";
    public const string ShowBoth = "show_both";
    public const string ShowA = "show_a";
    public const string ShowB = "show_b";
    public const string Transition = "transition";

    /// <summary>
    /// A safe fallback when no sleeping clip is curated. It shows only the bedroom plate
    /// rather than visibly awake cats pretending to sleep.
    /// </summary>
    public const string SleepPause = "sleep_pause";

    public static readonly HashSet<string> All = new(
        [HideBoth, HideA, HideB, ShowBoth, ShowA, ShowB, Transition, SleepPause],
        StringComparer.OrdinalIgnoreCase);
}

public class Topic
{
    public int Id { get; set; }
    public string TopicText { get; set; } = "";
    public string CatALabel { get; set; } = "me:";
    public string CatBLabel { get; set; } = "her:";
    public string CatAPosition { get; set; } = "left";
    public string CatBPosition { get; set; } = "right";
    public string? CaptionTop { get; set; }
    public string? CaptionBottom { get; set; }
    public string Mood { get; set; } = "funny";     // funny | wholesome | dramatic

    /// <summary>
    /// What each cat should visibly be DOING, from the clip library's action vocabulary
    /// (talking, gagging, shocked, innocent...). Without this the picker once paired a
    /// gagging cat with a hoodie-borrowing joke - the action is part of the punchline.
    /// </summary>
    public string? CatAAction { get; set; }
    public string? CatBAction { get; set; }

    public string Layout { get; set; } = Domain.Layout.SideBySide;
    public string Status { get; set; } = TopicStatus.Pending;
    public string? BatchId { get; set; }
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Optional back-and-forth conversation, as a JSON array of <see cref="DialogueLine"/>.
    /// Null or empty renders the original static style: one title and two standing labels.
    ///
    /// The reference reels that read as conversations do it with timed on-screen text, not
    /// narration - a 15.9s example runs seven lines, each 1-4s, with ~0.5s of silence
    /// between turns, and the speaker is identified purely by which side the line sits on.
    /// </summary>
    public string? DialogueJson { get; set; }

    public IReadOnlyList<DialogueLine> Dialogue() => DialogueLine.Parse(DialogueJson);

    public ICollection<Video> Videos { get; set; } = new List<Video>();
}

public class Video
{
    public int Id { get; set; }
    public int TopicId { get; set; }
    public Topic? Topic { get; set; }

    public int? CatAClipId { get; set; }
    public CatClip? CatAClip { get; set; }
    public int? CatBClipId { get; set; }
    public CatClip? CatBClip { get; set; }

    public string? BackgroundPath { get; set; }
    public string? OutputPath { get; set; }
    public string? ThumbnailPath { get; set; }
    public int DurationMs { get; set; }
    public string Status { get; set; } = VideoStatus.Rendering;

    public string? HangfireJobId { get; set; }
    public int? FfmpegPid { get; set; }
    public string? ErrorMessage { get; set; }
    public string? BatchId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public string? ApprovedBy { get; set; }

    public ICollection<Post> Posts { get; set; } = new List<Post>();
}

public class Post
{
    public int Id { get; set; }
    public int VideoId { get; set; }
    public Video? Video { get; set; }

    public string Platform { get; set; } = "both";  // facebook | instagram | both
    public string Caption { get; set; } = "";
    public string Hashtags { get; set; } = "";
    public DateTime? ScheduledAt { get; set; }                 // UTC
    public string ScheduledAtTimezone { get; set; } = "America/New_York";
    public DateTime? PostedAt { get; set; }
    public string? FbPostId { get; set; }
    public string? IgPostId { get; set; }
    public string Status { get; set; } = PostStatus.Scheduled;
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
