using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CatMeme.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddClipIsRetired : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsRetired",
                table: "cat_clips",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsRetired",
                table: "cat_clips");
        }
    }
}
