using System.Text.Json.Serialization;

namespace CatMeme.Api.Domain;

/// <summary>
/// Versioned quality contract embedded in DialogueJson. Keeping the marker in the JSON
/// avoids a database migration and lets unmarked historical scripts retain legacy rules.
/// </summary>
public sealed record StoryboardQualityProfile(
    string Id,
    int MinBeats,
    int MaxBeats,
    bool RequireCompleteDirectives,
    bool RequireStrongPayoff)
{
    public static StoryboardQualityProfile Default { get; } = new(
        "reference-default-v1",
        MinBeats: 6,
        MaxBeats: 9,
        RequireCompleteDirectives: true,
        RequireStrongPayoff: true);

    public static StoryboardQualityProfile? Resolve(string? id) =>
        string.Equals(id?.Trim(), Default.Id, StringComparison.OrdinalIgnoreCase)
            ? Default
            : null;
}

/// <summary>Narrative purpose of an authored beat.</summary>
public static class StoryboardRole
{
    public const string Setup = "setup";
    public const string Escalation = "escalation";
    public const string TimeChange = "time_change";
    public const string Reaction = "reaction";
    public const string Payoff = "payoff";

    public static readonly HashSet<string> All = new(
        [Setup, Escalation, TimeChange, Reaction, Payoff],
        StringComparer.OrdinalIgnoreCase);
}

public static class StoryboardTitleStyle
{
    public const string Standard = "standard";
    public const string Minimal = "minimal";
    public const string TimeCard = "time_card";
    public const string Payoff = "payoff";

    public static readonly HashSet<string> All = new(
        [Standard, Minimal, TimeCard, Payoff],
        StringComparer.OrdinalIgnoreCase);
}

public static class StoryboardLighting
{
    public const string Normal = "normal";
    public const string Dim = "dim";
    public const string Dark = "dark";
    public const string Flash = "flash";

    public static readonly HashSet<string> All = new(
        [Normal, Dim, Dark, Flash],
        StringComparer.OrdinalIgnoreCase);
}

public static class StoryboardCatScale
{
    public const string Normal = "normal";
    public const string CloseA = "close_a";
    public const string CloseB = "close_b";
    public const string Wide = "wide";

    public static readonly HashSet<string> All = new(
        [Normal, CloseA, CloseB, Wide],
        StringComparer.OrdinalIgnoreCase);
}

public static class StoryboardTransition
{
    public const string Cut = "cut";
    public const string Fade = "fade";
    public const string Flash = "flash";
    public const string TimeCard = "time_card";
    public const string SmashCut = "smash_cut";

    public static readonly HashSet<string> All = new(
        [Cut, Fade, Flash, TimeCard, SmashCut],
        StringComparer.OrdinalIgnoreCase);
}

public static class StoryboardSoundCue
{
    public const string None = "none";
    public const string Pop = "pop";
    public const string Whoosh = "whoosh";
    public const string Alarm = "alarm";
    public const string Impact = "impact";
    public const string RecordScratch = "record_scratch";

    public static readonly HashSet<string> All = new(
        [None, Pop, Whoosh, Alarm, Impact, RecordScratch],
        StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Curated paired-cat animation selectors. These are action tags resolved by the paired
/// interaction asset layer, never filenames supplied by generated text.
/// </summary>
public static class StoryboardInteractionAction
{
    public const string None = "none";
    public const string Slap = "slap";

    public static readonly HashSet<string> All = new(
        [None, Slap],
        StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Declarative, renderer-safe controls for one beat. Every value is either a boolean or a
/// closed vocabulary token; generated text can never inject a filter, path, font, or command.
/// Nullable booleans distinguish an omitted field from an intentional false for strict
/// profile validation while remaining deserializable from old JSON.
/// </summary>
public sealed record StoryboardDirectives(
    [property: JsonPropertyName("title_visible")] bool? TitleVisible = null,
    [property: JsonPropertyName("title_style")] string? TitleStyle = null,
    [property: JsonPropertyName("labels_visible")] bool? LabelsVisible = null,
    [property: JsonPropertyName("lighting")] string? Lighting = null,
    [property: JsonPropertyName("cat_scale")] string? CatScale = null,
    [property: JsonPropertyName("transition")] string? Transition = null,
    [property: JsonPropertyName("sound_cue")] string? SoundCue = null,
    [property: JsonPropertyName("interaction_action")] string? InteractionAction = null)
{
    [JsonIgnore]
    public bool IsComplete =>
        TitleVisible.HasValue &&
        LabelsVisible.HasValue &&
        !string.IsNullOrWhiteSpace(TitleStyle) &&
        !string.IsNullOrWhiteSpace(Lighting) &&
        !string.IsNullOrWhiteSpace(CatScale) &&
        !string.IsNullOrWhiteSpace(Transition) &&
        !string.IsNullOrWhiteSpace(SoundCue) &&
        !string.IsNullOrWhiteSpace(InteractionAction);

    [JsonIgnore]
    public bool IsSafe =>
        IsComplete &&
        StoryboardTitleStyle.All.Contains(TitleStyle!) &&
        StoryboardLighting.All.Contains(Lighting!) &&
        StoryboardCatScale.All.Contains(CatScale!) &&
        StoryboardTransition.All.Contains(Transition!) &&
        StoryboardSoundCue.All.Contains(SoundCue!) &&
        (string.IsNullOrWhiteSpace(InteractionAction) ||
         StoryboardInteractionAction.All.Contains(InteractionAction));

    [JsonIgnore]
    public bool IsStrongPayoff =>
        (TitleVisible == true &&
         string.Equals(TitleStyle, StoryboardTitleStyle.Payoff, StringComparison.OrdinalIgnoreCase)) ||
        string.Equals(Transition, StoryboardTransition.SmashCut, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(SoundCue, StoryboardSoundCue.Impact, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(SoundCue, StoryboardSoundCue.RecordScratch, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(InteractionAction, StoryboardInteractionAction.Slap, StringComparison.OrdinalIgnoreCase);
}
