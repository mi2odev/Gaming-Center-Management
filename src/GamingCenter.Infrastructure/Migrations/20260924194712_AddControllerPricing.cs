using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GamingCenter.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddControllerPricing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "ExtraControllerRate",
                table: "Stations",
                type: "TEXT",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<int>(
                name: "MaxControllers",
                table: "Stations",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Controllers",
                table: "Sessions",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SessionRateChanges",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SessionId = table.Column<int>(type: "INTEGER", nullable: false),
                    At = table.Column<DateTime>(type: "TEXT", nullable: false),
                    OldRate = table.Column<decimal>(type: "TEXT", nullable: false),
                    NewRate = table.Column<decimal>(type: "TEXT", nullable: false),
                    OldControllers = table.Column<int>(type: "INTEGER", nullable: true),
                    NewControllers = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SessionRateChanges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SessionRateChanges_Sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "Sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SessionRateChanges_SessionId",
                table: "SessionRateChanges",
                column: "SessionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SessionRateChanges");

            migrationBuilder.DropColumn(
                name: "ExtraControllerRate",
                table: "Stations");

            migrationBuilder.DropColumn(
                name: "MaxControllers",
                table: "Stations");

            migrationBuilder.DropColumn(
                name: "Controllers",
                table: "Sessions");
        }
    }
}
