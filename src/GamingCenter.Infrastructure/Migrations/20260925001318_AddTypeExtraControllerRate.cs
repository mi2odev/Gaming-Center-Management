using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GamingCenter.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTypeExtraControllerRate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "ExtraControllerRate",
                table: "StationTypes",
                type: "TEXT",
                nullable: false,
                defaultValue: 0m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExtraControllerRate",
                table: "StationTypes");
        }
    }
}
