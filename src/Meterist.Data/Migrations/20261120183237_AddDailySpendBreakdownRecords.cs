using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Meterist.Data.Migrations;

/// <inheritdoc />
public partial class AddDailySpendBreakdownRecords : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "DailySpendBreakdownRecords",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                TenantId = table.Column<string>(type: "TEXT", nullable: false),
                VendorId = table.Column<Guid>(type: "TEXT", nullable: false),
                Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                Category = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                Detail = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false, defaultValue: ""),
                Amount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 4, nullable: false),
                IsUsage = table.Column<bool>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_DailySpendBreakdownRecords", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_DailySpendBreakdownRecords_TenantId_VendorId_Date_Category_Detail",
            table: "DailySpendBreakdownRecords",
            columns: new[] { "TenantId", "VendorId", "Date", "Category", "Detail" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "DailySpendBreakdownRecords");
    }
}
