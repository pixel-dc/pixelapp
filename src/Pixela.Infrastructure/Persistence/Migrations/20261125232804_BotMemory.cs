using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pixela.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BotMemory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MemoryDays",
                table: "bots",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RememberPastChats",
                table: "bots",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MemoryDays",
                table: "bots");

            migrationBuilder.DropColumn(
                name: "RememberPastChats",
                table: "bots");
        }
    }
}
