using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Meterist.Data.Migrations;

/// <inheritdoc />
public partial class AddActivitySummaryRecords : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "ActivitySummaryRecords",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                TenantId = table.Column<string>(type: "TEXT", nullable: false),
                VendorId = table.Column<Guid>(type: "TEXT", nullable: false),
                Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                Product = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false, defaultValue: "all"),
                DailyActiveUsers = table.Column<int>(type: "INTEGER", nullable: true),
                WeeklyActiveUsers = table.Column<int>(type: "INTEGER", nullable: true),
                MonthlyActiveUsers = table.Column<int>(type: "INTEGER", nullable: true),
                AssignedSeatCount = table.Column<int>(type: "INTEGER", nullable: true),
                PendingInviteCount = table.Column<int>(type: "INTEGER", nullable: true),
                DailyAdoptionRate = table.Column<decimal>(type: "TEXT", precision: 9, scale: 4, nullable: true),
                WeeklyAdoptionRate = table.Column<decimal>(type: "TEXT", precision: 9, scale: 4, nullable: true),
                MonthlyAdoptionRate = table.Column<decimal>(type: "TEXT", precision: 9, scale: 4, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ActivitySummaryRecords", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_ActivitySummaryRecords_TenantId_VendorId_Date_Product",
            table: "ActivitySummaryRecords",
            columns: new[] { "TenantId", "VendorId", "Date", "Product" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "ActivitySummaryRecords");
    }
}
