using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Launcher.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class LauncherClients : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LauncherClients",
                columns: table => new
                {
                    Email = table.Column<string>(type: "TEXT", nullable: false),
                    Version = table.Column<string>(type: "TEXT", nullable: true),
                    Machine = table.Column<string>(type: "TEXT", nullable: true),
                    LastSeenAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LauncherClients", x => x.Email);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LauncherClients");
        }
    }
}
