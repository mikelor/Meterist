using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Meterist.Data.Migrations;

/// <inheritdoc />
public partial class AddMemberRosterRecords : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "MemberRosterRecords",
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
                Role = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                SeatType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                AddedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_MemberRosterRecords", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_MemberRosterRecords_TenantId_VendorId_SnapshotDate_MemberId",
            table: "MemberRosterRecords",
            columns: new[] { "TenantId", "VendorId", "SnapshotDate", "MemberId" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "MemberRosterRecords");
    }
}
