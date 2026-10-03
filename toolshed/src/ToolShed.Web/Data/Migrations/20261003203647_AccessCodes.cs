using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ToolShed.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AccessCodes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "TokenHash",
                table: "Invitations",
                newName: "CodeHash");

            migrationBuilder.RenameIndex(
                name: "IX_Invitations_TokenHash",
                table: "Invitations",
                newName: "IX_Invitations_CodeHash");

            migrationBuilder.AddColumn<int>(
                name: "FailedAttempts",
                table: "Invitations",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "LockedUntilUtc",
                table: "Invitations",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FailedAttempts",
                table: "Invitations");

            migrationBuilder.DropColumn(
                name: "LockedUntilUtc",
                table: "Invitations");

            migrationBuilder.RenameColumn(
                name: "CodeHash",
                table: "Invitations",
                newName: "TokenHash");

            migrationBuilder.RenameIndex(
                name: "IX_Invitations_CodeHash",
                table: "Invitations",
                newName: "IX_Invitations_TokenHash");
        }
    }
}
