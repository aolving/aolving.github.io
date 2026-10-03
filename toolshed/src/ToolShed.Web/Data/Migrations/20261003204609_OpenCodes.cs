using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ToolShed.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class OpenCodes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Email",
                table: "Invitations",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "TEXT");

            migrationBuilder.AddColumn<string>(
                name: "Label",
                table: "Invitations",
                type: "TEXT",
                maxLength: 80,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "OpenCodeFailures",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AtUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpenCodeFailures", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OpenCodeFailures_AtUtc",
                table: "OpenCodeFailures",
                column: "AtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OpenCodeFailures");

            migrationBuilder.DropColumn(
                name: "Label",
                table: "Invitations");

            migrationBuilder.AlterColumn<string>(
                name: "Email",
                table: "Invitations",
                type: "TEXT",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldNullable: true);
        }
    }
}
