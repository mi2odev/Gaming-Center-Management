using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GamingCenter.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class TurnOffDefaultPrinting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Receipts are printed only when the operator ticks "Print thermal receipt" on the bill.
            migrationBuilder.Sql("UPDATE Settings SET Value = 'False' WHERE Key = 'PrintReceiptByDefault';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
