using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yastık6.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddLastPriceAndShields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "LastPrice",
                table: "Prices",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastPrice",
                table: "Prices");
        }
    }
}
