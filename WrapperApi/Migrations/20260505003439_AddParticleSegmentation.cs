using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WrapperApi.Migrations
{
    /// <inheritdoc />
    public partial class AddParticleSegmentation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SegmentationParams",
                table: "Jobs",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SegmentationParams",
                table: "Jobs");
        }
    }
}
