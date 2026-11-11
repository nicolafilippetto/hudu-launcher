using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Launcher.Server.Data.Migrations;

/// <inheritdoc />
public partial class ConnectionTabs : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "DatabaseKind",
            table: "ExtraConnections",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "DatabaseName",
            table: "ExtraConnections",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<bool>(
            name: "Hidden",
            table: "ExtraConnections",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<bool>(
            name: "IsConsole",
            table: "ExtraConnections",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<string>(
            name: "RdpOptions",
            table: "ExtraConnections",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "WebOptions",
            table: "ExtraConnections",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "DatabaseKind",
            table: "AssetSettings",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "DatabaseName",
            table: "AssetSettings",
            type: "TEXT",
            nullable: true);

        // Le opzioni RDP e web valevano per tutto l'asset: ora ogni connessione ha le sue. Le connessioni
        // aggiuntive esistenti ricevono le opzioni dell'asset, così si comportano come prima.
        migrationBuilder.Sql("""
            UPDATE ExtraConnections
            SET RdpOptions = (SELECT s.RdpOptions FROM AssetSettings s WHERE s.AssetId = ExtraConnections.AssetId),
                WebOptions = (SELECT s.WebOptions FROM AssetSettings s WHERE s.AssetId = ExtraConnections.AssetId);
            """);

        // La console suggerita da Hudu usava le opzioni web dell'asset (es. certificato dell'iDRAC): le
        // conserva in una modifica della console che lascia indirizzo e protocollo (0) a Hudu.
        migrationBuilder.Sql("""
            INSERT INTO ExtraConnections (AssetId, Label, Protocol, IsConsole, Hidden, WebOptions, UpdatedAt, UpdatedBy)
            SELECT s.AssetId, 'Console', 0, 1, 0, s.WebOptions, s.UpdatedAt, 'aggiornamento 1.5.2'
            FROM AssetSettings s
            WHERE s.WebOptions IS NOT NULL;
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DELETE FROM ExtraConnections WHERE IsConsole = 1 OR Protocol = 0 OR Protocol > 5;");

        migrationBuilder.DropColumn(
            name: "DatabaseKind",
            table: "ExtraConnections");

        migrationBuilder.DropColumn(
            name: "DatabaseName",
            table: "ExtraConnections");

        migrationBuilder.DropColumn(
            name: "Hidden",
            table: "ExtraConnections");

        migrationBuilder.DropColumn(
            name: "IsConsole",
            table: "ExtraConnections");

        migrationBuilder.DropColumn(
            name: "RdpOptions",
            table: "ExtraConnections");

        migrationBuilder.DropColumn(
            name: "WebOptions",
            table: "ExtraConnections");

        migrationBuilder.DropColumn(
            name: "DatabaseKind",
            table: "AssetSettings");

        migrationBuilder.DropColumn(
            name: "DatabaseName",
            table: "AssetSettings");
    }
}
