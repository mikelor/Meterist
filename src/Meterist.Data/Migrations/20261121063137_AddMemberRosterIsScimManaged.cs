using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Meterist.Data.Migrations;

/// <inheritdoc />
public partial class AddMemberRosterIsScimManaged : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "IsScimManaged",
            table: "MemberRosterRecords",
            type: "INTEGER",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "IsScimManaged",
            table: "MemberRosterRecords");
    }
}
