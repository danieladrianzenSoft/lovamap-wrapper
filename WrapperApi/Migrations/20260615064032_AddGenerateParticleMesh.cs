using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WrapperApi.Migrations
{
    /// <inheritdoc />
    public partial class AddGenerateParticleMesh : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "GenerateParticleMesh",
                table: "Jobs",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GenerateParticleMesh",
                table: "Jobs");
        }
    }
}
