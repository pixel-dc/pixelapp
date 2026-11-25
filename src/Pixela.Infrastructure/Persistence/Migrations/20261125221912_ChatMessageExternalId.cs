using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pixela.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ChatMessageExternalId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExternalId",
                table: "chat_messages",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ux_chat_messages_external",
                table: "chat_messages",
                columns: new[] { "ConversationId", "ExternalId" },
                unique: true,
                filter: "\"ExternalId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_chat_messages_external",
                table: "chat_messages");

            migrationBuilder.DropColumn(
                name: "ExternalId",
                table: "chat_messages");
        }
    }
}
