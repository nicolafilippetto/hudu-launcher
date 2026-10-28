using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Launcher.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class TicketDirect : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Direct",
                table: "Tickets",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Direct",
                table: "Tickets");
        }
    }
}
