using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CatMeme.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddClipBarResidue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "BarResidue",
                table: "cat_clips",
                type: "float",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<string>(
                name: "DisabledReason",
                table: "cat_clips",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BarResidue",
                table: "cat_clips");

            migrationBuilder.DropColumn(
                name: "DisabledReason",
                table: "cat_clips");
        }
    }
}
