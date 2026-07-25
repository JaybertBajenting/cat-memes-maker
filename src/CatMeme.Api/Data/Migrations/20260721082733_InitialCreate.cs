using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CatMeme.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "cat_clips",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Filename = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DurationMs = table.Column<int>(type: "int", nullable: false),
                    MoodTags = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Facing = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    KeyColor = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    KeySimilarity = table.Column<double>(type: "float", nullable: false),
                    CropFilter = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SourceWidth = table.Column<int>(type: "int", nullable: false),
                    SourceHeight = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cat_clips", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "settings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    BackgroundSource = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    PexelsApiKey = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    FbPageId = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    FbAccessToken = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IgUserId = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IgAccessToken = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DefaultPostCaptionTemplate = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    AutoUpload = table.Column<bool>(type: "bit", nullable: false),
                    TargetTimezone = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    BatchSize = table.Column<int>(type: "int", nullable: false),
                    MorningSlotUtc = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    AfternoonSlotUtc = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    EveningSlotUtc = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    TargetDurationMs = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_settings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "topics",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TopicText = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CatALabel = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CatBLabel = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CatAPosition = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CatBPosition = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CaptionTop = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CaptionBottom = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Mood = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Layout = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    BatchId = table.Column<string>(type: "nvarchar(450)", nullable: true),
                    GeneratedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_topics", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "videos",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TopicId = table.Column<int>(type: "int", nullable: false),
                    CatAClipId = table.Column<int>(type: "int", nullable: true),
                    CatBClipId = table.Column<int>(type: "int", nullable: true),
                    BackgroundPath = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    OutputPath = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ThumbnailPath = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DurationMs = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    HangfireJobId = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    FfmpegPid = table.Column<int>(type: "int", nullable: true),
                    ErrorMessage = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    BatchId = table.Column<string>(type: "nvarchar(450)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ApprovedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ApprovedBy = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_videos", x => x.Id);
                    table.ForeignKey(
                        name: "FK_videos_cat_clips_CatAClipId",
                        column: x => x.CatAClipId,
                        principalTable: "cat_clips",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_videos_cat_clips_CatBClipId",
                        column: x => x.CatBClipId,
                        principalTable: "cat_clips",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_videos_topics_TopicId",
                        column: x => x.TopicId,
                        principalTable: "topics",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "posts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    VideoId = table.Column<int>(type: "int", nullable: false),
                    Platform = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Caption = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Hashtags = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ScheduledAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ScheduledAtTimezone = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    PostedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    FbPostId = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IgPostId = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    ErrorMessage = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_posts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_posts_videos_VideoId",
                        column: x => x.VideoId,
                        principalTable: "videos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_cat_clips_IsActive",
                table: "cat_clips",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_posts_ScheduledAt",
                table: "posts",
                column: "ScheduledAt");

            migrationBuilder.CreateIndex(
                name: "IX_posts_Status",
                table: "posts",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_posts_VideoId",
                table: "posts",
                column: "VideoId");

            migrationBuilder.CreateIndex(
                name: "IX_topics_BatchId",
                table: "topics",
                column: "BatchId");

            migrationBuilder.CreateIndex(
                name: "IX_topics_Status",
                table: "topics",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_videos_BatchId",
                table: "videos",
                column: "BatchId");

            migrationBuilder.CreateIndex(
                name: "IX_videos_CatAClipId",
                table: "videos",
                column: "CatAClipId");

            migrationBuilder.CreateIndex(
                name: "IX_videos_CatBClipId",
                table: "videos",
                column: "CatBClipId");

            migrationBuilder.CreateIndex(
                name: "IX_videos_Status",
                table: "videos",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_videos_TopicId",
                table: "videos",
                column: "TopicId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "posts");

            migrationBuilder.DropTable(
                name: "settings");

            migrationBuilder.DropTable(
                name: "videos");

            migrationBuilder.DropTable(
                name: "cat_clips");

            migrationBuilder.DropTable(
                name: "topics");
        }
    }
}
