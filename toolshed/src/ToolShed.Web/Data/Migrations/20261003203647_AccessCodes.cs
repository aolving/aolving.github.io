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

            // Invitations issued before access codes existed carry a hash of a long link token, not of
            // a six-digit code, so none of them can be redeemed any more. Retire the unspent ones
            // visibly (as revoked) rather than leave them showing as "Waiting". Timestamps are stored
            // as .NET ticks; 621355968000000000 is the Unix epoch in ticks.
            migrationBuilder.Sql(
                "UPDATE \"Invitations\" " +
                "SET \"RevokedUtc\" = (CAST(strftime('%s', 'now') AS INTEGER) * 10000000) + 621355968000000000 " +
                "WHERE \"RedeemedUtc\" IS NULL AND \"RevokedUtc\" IS NULL;");

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
