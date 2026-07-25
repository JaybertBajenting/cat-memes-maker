using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CatMeme.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTopicActions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CatAAction",
                table: "topics",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CatBAction",
                table: "topics",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CatAAction",
                table: "topics");

            migrationBuilder.DropColumn(
                name: "CatBAction",
                table: "topics");
        }
    }
}
