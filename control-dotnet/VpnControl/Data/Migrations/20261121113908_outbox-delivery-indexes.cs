using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VpnControl.Data.Migrations
{
    /// <inheritdoc />
    public partial class OutboxDeliveryIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_outbox_deadline",
                table: "outbox",
                column: "deadline",
                filter: "status = 'sent'");

            migrationBuilder.CreateIndex(
                name: "IX_outbox_node_id_created_at_command_id",
                table: "outbox",
                columns: new[] { "node_id", "created_at", "command_id" },
                filter: "status IN ('pending', 'sent')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_outbox_deadline",
                table: "outbox");

            migrationBuilder.DropIndex(
                name: "IX_outbox_node_id_created_at_command_id",
                table: "outbox");
        }
    }
}
