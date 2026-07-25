using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CatMeme.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddClipSubjectFill : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "SubjectFill",
                table: "cat_clips",
                type: "float",
                nullable: false,
                defaultValue: 0.0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SubjectFill",
                table: "cat_clips");
        }
    }
}
