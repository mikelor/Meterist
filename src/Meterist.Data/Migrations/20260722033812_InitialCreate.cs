using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Meterist.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DailySpendRecords",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TenantId = table.Column<string>(type: "TEXT", nullable: false),
                    VendorId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    SeatFee = table.Column<decimal>(type: "TEXT", precision: 18, scale: 4, nullable: false),
                    UsageOrOverage = table.Column<decimal>(type: "TEXT", precision: 18, scale: 4, nullable: false),
                    GrossSpend = table.Column<decimal>(type: "TEXT", precision: 18, scale: 4, nullable: false),
                    CreditsApplied = table.Column<decimal>(type: "TEXT", precision: 18, scale: 4, nullable: false),
                    NetSpend = table.Column<decimal>(type: "TEXT", precision: 18, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DailySpendRecords", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RawDailyExtractionRecords",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TenantId = table.Column<string>(type: "TEXT", nullable: false),
                    VendorId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    ExtractedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RecordsJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RawDailyExtractionRecords", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "VendorRateConfigs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TenantId = table.Column<string>(type: "TEXT", nullable: true),
                    VendorId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RateType = table.Column<string>(type: "TEXT", nullable: false),
                    ModelOrSku = table.Column<string>(type: "TEXT", nullable: true),
                    Rate = table.Column<decimal>(type: "TEXT", precision: 18, scale: 6, nullable: false),
                    SeatCount = table.Column<int>(type: "INTEGER", nullable: true),
                    BillingCadence = table.Column<int>(type: "INTEGER", nullable: true),
                    EffectiveFrom = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    EffectiveTo = table.Column<DateOnly>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VendorRateConfigs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DailySpendRecords_TenantId_VendorId_Date",
                table: "DailySpendRecords",
                columns: new[] { "TenantId", "VendorId", "Date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RawDailyExtractionRecords_TenantId_VendorId_Date",
                table: "RawDailyExtractionRecords",
                columns: new[] { "TenantId", "VendorId", "Date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VendorRateConfigs_TenantId_VendorId_ModelOrSku_EffectiveFrom",
                table: "VendorRateConfigs",
                columns: new[] { "TenantId", "VendorId", "ModelOrSku", "EffectiveFrom" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DailySpendRecords");

            migrationBuilder.DropTable(
                name: "RawDailyExtractionRecords");

            migrationBuilder.DropTable(
                name: "VendorRateConfigs");
        }
    }
}
