using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WrapperApi.Migrations
{
    /// <inheritdoc />
    public partial class AddGenerateMesh : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "GenerateMesh",
                table: "Jobs",
                type: "boolean",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GenerateMesh",
                table: "Jobs");
        }
    }
}
