using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Launcher.Server.Data.Migrations;

/// <inheritdoc />
public partial class NoGateway : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "NoGateway",
            table: "ExtraConnections",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<bool>(
            name: "NoRustDesk",
            table: "CompanySettings",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<bool>(
            name: "NoGateway",
            table: "AssetSettings",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<bool>(
            name: "NoPassword",
            table: "AssetSettings",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "NoGateway",
            table: "ExtraConnections");

        migrationBuilder.DropColumn(
            name: "NoRustDesk",
            table: "CompanySettings");

        migrationBuilder.DropColumn(
            name: "NoGateway",
            table: "AssetSettings");

        migrationBuilder.DropColumn(
            name: "NoPassword",
            table: "AssetSettings");
    }
}
