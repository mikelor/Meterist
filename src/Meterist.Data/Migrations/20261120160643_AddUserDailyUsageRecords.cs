using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Meterist.Data.Migrations;

/// <inheritdoc />
public partial class AddUserDailyUsageRecords : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "UserDailyUsageRecords",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                TenantId = table.Column<string>(type: "TEXT", nullable: false),
                VendorId = table.Column<Guid>(type: "TEXT", nullable: false),
                Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                UserId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                UserEmail = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                UserName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                Amount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 4, nullable: false),
                Credits = table.Column<decimal>(type: "TEXT", precision: 18, scale: 4, nullable: true),
                Requests = table.Column<long>(type: "INTEGER", nullable: true),
                IsEstimated = table.Column<bool>(type: "INTEGER", nullable: false),
                Attribution = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_UserDailyUsageRecords", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_UserDailyUsageRecords_TenantId_VendorId_Date_UserId",
            table: "UserDailyUsageRecords",
            columns: new[] { "TenantId", "VendorId", "Date", "UserId" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "UserDailyUsageRecords");
    }
}
