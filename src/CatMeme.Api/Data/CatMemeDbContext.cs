using CatMeme.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace CatMeme.Api.Data;

public class CatMemeDbContext(DbContextOptions<CatMemeDbContext> options) : DbContext(options)
{
    public DbSet<AppSettings> Settings => Set<AppSettings>();
    public DbSet<CatClip> CatClips => Set<CatClip>();
    public DbSet<PairedInteractionAsset> PairedInteractionAssets => Set<PairedInteractionAsset>();
    public DbSet<Topic> Topics => Set<Topic>();
    public DbSet<Video> Videos => Set<Video>();
    public DbSet<Post> Posts => Set<Post>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<AppSettings>(e =>
        {
            e.ToTable("settings");
            // Existing installations must keep using Claude after this column is added.
            e.Property(x => x.ScriptProvider).HasDefaultValue("claude");
        });
        b.Entity<CatClip>(e =>
        {
            e.ToTable("cat_clips");
            e.HasIndex(x => x.IsActive);
        });

        b.Entity<PairedInteractionAsset>(e =>
        {
            e.ToTable("paired_interaction_assets", table =>
            {
                table.HasCheckConstraint(
                    "CK_paired_interaction_assets_timing",
                    "[DurationMs] > 0 AND [InPointMs] >= 0 AND " +
                    "([OutPointMs] = 0 OR ([OutPointMs] > [InPointMs] AND [OutPointMs] <= [DurationMs]))");
                table.HasCheckConstraint(
                    "CK_paired_interaction_assets_quality",
                    "[QualityScore] >= 0 AND [QualityScore] <= 1 AND [KeySimilarity] >= 0 AND [KeySimilarity] <= 1");
            });
            e.Property(x => x.Name).HasMaxLength(120);
            e.Property(x => x.Filename).HasMaxLength(260);
            e.Property(x => x.ActionTags).HasMaxLength(256);
            e.Property(x => x.KeyColor).HasMaxLength(16);
            e.Property(x => x.CropFilter).HasMaxLength(128);
            e.Property(x => x.DisabledReason).HasMaxLength(500);
            e.HasIndex(x => new { x.IsActive, x.QualityApproved });
            e.HasIndex(x => x.SourceCatClipId)
                .IsUnique()
                .HasFilter("[SourceCatClipId] IS NOT NULL");
        });

        b.Entity<Topic>(e =>
        {
            e.ToTable("topics");
            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.BatchId);
        });

        b.Entity<Video>(e =>
        {
            e.ToTable("videos");
            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.BatchId);

            e.HasOne(x => x.Topic).WithMany(t => t.Videos)
                .HasForeignKey(x => x.TopicId).OnDelete(DeleteBehavior.Cascade);

            // Restrict: deleting a clip must not cascade-delete rendered videos.
            e.HasOne(x => x.CatAClip).WithMany()
                .HasForeignKey(x => x.CatAClipId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.CatBClip).WithMany()
                .HasForeignKey(x => x.CatBClipId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Post>(e =>
        {
            e.ToTable("posts");
            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.ScheduledAt);

            e.HasOne(x => x.Video).WithMany(v => v.Posts)
                .HasForeignKey(x => x.VideoId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
