using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Meterist.Data.Migrations;

/// <inheritdoc />
public partial class AddSpendLimitPriorPeriodSpend : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<decimal>(
            name: "PriorPeriodSpend",
            table: "SpendLimitRecords",
            type: "TEXT",
            precision: 18,
            scale: 4,
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "PriorPeriodSpend",
            table: "SpendLimitRecords");
    }
}
