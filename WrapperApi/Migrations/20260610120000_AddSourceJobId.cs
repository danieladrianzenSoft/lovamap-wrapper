using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WrapperApi.Migrations
{
    /// <inheritdoc />
    public partial class AddSourceJobId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SourceJobId",
                table: "Jobs",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SourceJobId",
                table: "Jobs");
        }
    }
}
