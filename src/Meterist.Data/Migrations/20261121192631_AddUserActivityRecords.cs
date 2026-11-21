using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Meterist.Data.Migrations;

/// <inheritdoc />
public partial class AddUserActivityRecords : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "UserActivityRecords",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                TenantId = table.Column<string>(type: "TEXT", nullable: false),
                VendorId = table.Column<Guid>(type: "TEXT", nullable: false),
                Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                UserId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                UserEmail = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                ChatMessages = table.Column<long>(type: "INTEGER", nullable: false),
                ChatConversations = table.Column<long>(type: "INTEGER", nullable: false),
                ClaudeCodeSessions = table.Column<long>(type: "INTEGER", nullable: false),
                ClaudeCodeCommits = table.Column<long>(type: "INTEGER", nullable: false),
                ClaudeCodePullRequests = table.Column<long>(type: "INTEGER", nullable: false),
                ClaudeCodeLinesAdded = table.Column<long>(type: "INTEGER", nullable: false),
                ClaudeCodeLinesRemoved = table.Column<long>(type: "INTEGER", nullable: false),
                CoworkSessions = table.Column<long>(type: "INTEGER", nullable: false),
                CoworkActions = table.Column<long>(type: "INTEGER", nullable: false),
                CoworkMessages = table.Column<long>(type: "INTEGER", nullable: false),
                OfficeSessions = table.Column<long>(type: "INTEGER", nullable: false),
                OfficeMessages = table.Column<long>(type: "INTEGER", nullable: false),
                DesignMessages = table.Column<long>(type: "INTEGER", nullable: false),
                ScienceMessages = table.Column<long>(type: "INTEGER", nullable: false),
                WebSearches = table.Column<long>(type: "INTEGER", nullable: false),
                DetailJson = table.Column<string>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_UserActivityRecords", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_UserActivityRecords_TenantId_VendorId_Date_UserId",
            table: "UserActivityRecords",
            columns: new[] { "TenantId", "VendorId", "Date", "UserId" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "UserActivityRecords");
    }
}
