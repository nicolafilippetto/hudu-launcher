using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Launcher.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AssetRdpOptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RdpOptions",
                table: "AssetSettings",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RdpOptions",
                table: "AssetSettings");
        }
    }
}
