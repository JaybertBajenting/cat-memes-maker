using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CatMeme.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPairedInteractionAssets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "paired_interaction_assets",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Filename = table.Column<string>(type: "nvarchar(260)", maxLength: 260, nullable: false),
                    ActionTags = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    SourceCatClipId = table.Column<int>(type: "int", nullable: true),
                    DurationMs = table.Column<int>(type: "int", nullable: false),
                    InPointMs = table.Column<int>(type: "int", nullable: false),
                    OutPointMs = table.Column<int>(type: "int", nullable: false),
                    HasAudio = table.Column<bool>(type: "bit", nullable: false),
                    PreserveAudio = table.Column<bool>(type: "bit", nullable: false),
                    AudioGainDb = table.Column<double>(type: "float", nullable: false),
                    KeyColor = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    KeySimilarity = table.Column<double>(type: "float", nullable: false),
                    CropFilter = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    SourceWidth = table.Column<int>(type: "int", nullable: false),
                    SourceHeight = table.Column<int>(type: "int", nullable: false),
                    BarResidue = table.Column<double>(type: "float", nullable: false),
                    SubjectFill = table.Column<double>(type: "float", nullable: false),
                    QualityScore = table.Column<double>(type: "float", nullable: false),
                    QualityApproved = table.Column<bool>(type: "bit", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    DisabledReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_paired_interaction_assets", x => x.Id);
                    table.CheckConstraint("CK_paired_interaction_assets_quality", "[QualityScore] >= 0 AND [QualityScore] <= 1 AND [KeySimilarity] >= 0 AND [KeySimilarity] <= 1");
                    table.CheckConstraint("CK_paired_interaction_assets_timing", "[DurationMs] > 0 AND [InPointMs] >= 0 AND ([OutPointMs] = 0 OR ([OutPointMs] > [InPointMs] AND [OutPointMs] <= [DurationMs]))");
                });

            migrationBuilder.CreateIndex(
                name: "IX_paired_interaction_assets_IsActive_QualityApproved",
                table: "paired_interaction_assets",
                columns: new[] { "IsActive", "QualityApproved" });

            migrationBuilder.CreateIndex(
                name: "IX_paired_interaction_assets_SourceCatClipId",
                table: "paired_interaction_assets",
                column: "SourceCatClipId",
                unique: true,
                filter: "[SourceCatClipId] IS NOT NULL");

            // Clip-library row 369 is the visually audited, clean two-cat slap scene used
            // by the reference reproduction. Register a separate whole-scene asset without
            // changing that CatClip row's inactive state, so ordinary one-cat casting can
            // never choose a frame that already contains both characters.
            migrationBuilder.InsertData(
                table: "paired_interaction_assets",
                columns: new[]
                {
                    "Name", "Filename", "ActionTags", "SourceCatClipId",
                    "DurationMs", "InPointMs", "OutPointMs",
                    "HasAudio", "PreserveAudio", "AudioGainDb",
                    "KeyColor", "KeySimilarity", "CropFilter", "SourceWidth", "SourceHeight",
                    "BarResidue", "SubjectFill", "QualityScore",
                    "QualityApproved", "IsActive", "DisabledReason", "CreatedAt", "UpdatedAt"
                },
                values: new object[]
                {
                    "Two-cat slap payoff", "fullallcatgreenscreenmem_150838_033.mp4", "slap", 369,
                    5100, 0, 5100,
                    true, true, 0d,
                    "0x00FB00", 0.10d, "620:644:328:30", 620, 644,
                    0d, 0.4903d, 1d,
                    true, true, null,
                    new DateTime(2026, 7, 22, 5, 55, 53, DateTimeKind.Utc),
                    new DateTime(2026, 7, 22, 5, 55, 53, DateTimeKind.Utc)
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "paired_interaction_assets");
        }
    }
}
