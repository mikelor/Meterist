using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Meterist.Data.Migrations;

/// <inheritdoc />
public partial class AddDailySpendRecordSource : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "Source",
            table: "DailySpendRecords",
            type: "TEXT",
            maxLength: 64,
            nullable: true);

        // Every ChatGPT Enterprise day stored before this column existed came
        // from the COSTS compliance-log export (the Daily Usage API was not
        // wired in yet), so labelling them is a statement of fact, not a guess.
        // Other vendors have a single source and stay null. The GUID is
        // VendorCatalog.ChatGptEnterprise.Id, as EF Core stores it in SQLite.
        migrationBuilder.Sql(
            "UPDATE DailySpendRecords SET Source = 'costs-export' "
            + "WHERE VendorId = '3D6F1C2E-8A4B-4E3A-8B2F-7C5E9A1D4F33';");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "Source",
            table: "DailySpendRecords");
    }
}
