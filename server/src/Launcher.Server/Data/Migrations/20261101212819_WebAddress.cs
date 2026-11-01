using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Launcher.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class WebAddress : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "WebAddress",
                table: "AssetSettings",
                type: "TEXT",
                nullable: true);

            // L'URL scritto a mano decideva anche il protocollo: gli asset con protocollo "Automatico"
            // restano pagine web (HTTPS = 5, HTTP = 4) ora che l'indirizzo viene dall'FQDN dell'IPAM.
            migrationBuilder.Sql("UPDATE AssetSettings SET Protocol = 5 WHERE Protocol IS NULL AND lower(WebUrl) LIKE 'https://%';");
            migrationBuilder.Sql("UPDATE AssetSettings SET Protocol = 4 WHERE Protocol IS NULL AND lower(WebUrl) LIKE 'http://%';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "WebAddress",
                table: "AssetSettings");
        }
    }
}
