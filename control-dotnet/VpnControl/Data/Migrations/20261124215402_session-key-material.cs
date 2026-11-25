using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VpnControl.Data.Migrations
{
    /// <inheritdoc />
    public partial class SessionKeyMaterial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "egress_key",
                table: "sessions",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "ingress_key",
                table: "sessions",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "peer_index",
                table: "sessions",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "sender_index",
                table: "sessions",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_sessions_peer_index",
                table: "sessions",
                column: "peer_index",
                unique: true,
                filter: "peer_index IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_sessions_sender_index",
                table: "sessions",
                column: "sender_index",
                unique: true,
                filter: "sender_index IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_sessions_peer_index",
                table: "sessions");

            migrationBuilder.DropIndex(
                name: "IX_sessions_sender_index",
                table: "sessions");

            migrationBuilder.DropColumn(
                name: "egress_key",
                table: "sessions");

            migrationBuilder.DropColumn(
                name: "ingress_key",
                table: "sessions");

            migrationBuilder.DropColumn(
                name: "peer_index",
                table: "sessions");

            migrationBuilder.DropColumn(
                name: "sender_index",
                table: "sessions");
        }
    }
}
