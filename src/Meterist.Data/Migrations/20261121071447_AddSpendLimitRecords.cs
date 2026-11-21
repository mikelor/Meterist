using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Meterist.Data.Migrations;

/// <inheritdoc />
public partial class AddSpendLimitRecords : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "SpendLimitRecords",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                TenantId = table.Column<string>(type: "TEXT", nullable: false),
                VendorId = table.Column<Guid>(type: "TEXT", nullable: false),
                SnapshotDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                MemberId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                Email = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                LimitAmount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 4, nullable: true),
                PeriodToDateSpend = table.Column<decimal>(type: "TEXT", precision: 18, scale: 4, nullable: true),
                Currency = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                Period = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                SourceType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                SourceDetail = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                SpendLimitId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_SpendLimitRecords", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_SpendLimitRecords_TenantId_VendorId_SnapshotDate_MemberId",
            table: "SpendLimitRecords",
            columns: new[] { "TenantId", "VendorId", "SnapshotDate", "MemberId" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "SpendLimitRecords");
    }
}
