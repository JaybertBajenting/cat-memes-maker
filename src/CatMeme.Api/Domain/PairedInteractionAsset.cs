namespace CatMeme.Api.Domain;

/// <summary>
/// A whole-scene clip containing both cats and the interaction between them. It is kept in
/// a separate table from <see cref="CatClip"/> because splitting a slap, chase or cuddle
/// into two independently keyed actors destroys the timing that makes the action readable.
/// Ordinary single-cat casting never queries this entity.
/// </summary>
public sealed class PairedInteractionAsset
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Filename { get; set; } = "";

    /// <summary>
    /// Comma-separated exact action tokens. These are eligibility constraints, not fuzzy
    /// mood hints: a request for "slap" must not accidentally match "slapping".
    /// </summary>
    public string ActionTags { get; set; } = "";

    /// <summary>
    /// Provenance only. This deliberately is not a foreign key: retiring/re-importing the
    /// ordinary clip library must not delete or activate this independently approved asset.
    /// </summary>
    public int? SourceCatClipId { get; set; }

    public int DurationMs { get; set; }
    public int InPointMs { get; set; }
    public int OutPointMs { get; set; }

    public bool HasAudio { get; set; }
    public bool PreserveAudio { get; set; }
    public double AudioGainDb { get; set; }

    public string KeyColor { get; set; } = "0x00FF00";
    public double KeySimilarity { get; set; } = 0.30;
    public string? CropFilter { get; set; }
    public int SourceWidth { get; set; }
    public int SourceHeight { get; set; }

    public double BarResidue { get; set; }
    public double SubjectFill { get; set; }
    public double QualityScore { get; set; } = 1.0;
    public bool QualityApproved { get; set; }
    public bool IsActive { get; set; } = true;
    public string? DisabledReason { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
