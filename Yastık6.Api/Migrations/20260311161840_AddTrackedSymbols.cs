using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Yastık6.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddTrackedSymbols : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TrackedSymbols",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Code = table.Column<string>(type: "TEXT", nullable: false),
                    SymbolType = table.Column<string>(type: "TEXT", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrackedSymbols", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "TrackedSymbols",
                columns: new[] { "Id", "Code", "IsActive", "SymbolType" },
                values: new object[,]
                {
                    { new Guid("a1b2c3d4-e5f6-7a8b-9c0d-1e2f3a4b5c6d"), "USDTRY=X", true, "FOREX" },
                    { new Guid("b2c3d4e5-f6a7-8b9c-0d1e-2f3a4b5c6d7e"), "EURTRY=X", true, "FOREX" },
                    { new Guid("c3d4e5f6-a7b8-9c0d-1e2f-3a4b5c6d7e8f"), "THYAO.IS", true, "BIST" },
                    { new Guid("d4e5f6a7-b8c9-0d1e-2f3a-4b5c6d7e8f9a"), "EREGL.IS", true, "BIST" },
                    { new Guid("e5f6a7b8-c9d0-1e2f-3a4b-5c6d7e8f9a0b"), "TUPRS.IS", true, "BIST" }
                });

            migrationBuilder.CreateIndex(
                name: "IX_TrackedSymbols_Code",
                table: "TrackedSymbols",
                column: "Code",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TrackedSymbols");
        }
    }
}
