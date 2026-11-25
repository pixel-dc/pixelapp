using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pixela.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ChannelProviderAccountId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ProviderAccountId",
                table: "channels",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ProviderAccountId",
                table: "channels");
        }
    }
}
