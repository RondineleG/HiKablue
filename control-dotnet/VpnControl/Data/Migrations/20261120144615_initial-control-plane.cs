using System;
using System.Net;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VpnControl.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialControlPlane : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "nodes",
                columns: table => new
                {
                    node_id = table.Column<string>(type: "text", nullable: false),
                    backend_instance_id = table.Column<string>(type: "text", nullable: false),
                    registered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    engine_healthy = table.Column<bool>(type: "boolean", nullable: false),
                    desired_revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nodes", x => x.node_id);
                });

            migrationBuilder.CreateTable(
                name: "outbox",
                columns: table => new
                {
                    command_id = table.Column<Guid>(type: "uuid", nullable: false),
                    node_id = table.Column<string>(type: "text", nullable: false),
                    command_type = table.Column<string>(type: "text", nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    delivered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    acked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outbox", x => x.command_id);
                });

            migrationBuilder.CreateTable(
                name: "sessions",
                columns: table => new
                {
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    idempotency_key = table.Column<string>(type: "text", nullable: true),
                    request_hash = table.Column<string>(type: "text", nullable: true),
                    owner_subject = table.Column<string>(type: "text", nullable: false),
                    peer_public_key = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    virtual_ip = table.Column<IPAddress>(type: "inet", nullable: true),
                    node_id = table.Column<string>(type: "text", nullable: false),
                    lab_session_id = table.Column<int>(type: "integer", nullable: false),
                    key_epoch = table.Column<long>(type: "bigint", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    failure_reason = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sessions", x => x.session_id);
                    table.ForeignKey(
                        name: "FK_sessions_nodes_node_id",
                        column: x => x.node_id,
                        principalTable: "nodes",
                        principalColumn: "node_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ip_leases",
                columns: table => new
                {
                    address = table.Column<IPAddress>(type: "inet", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: true),
                    node_id = table.Column<string>(type: "text", nullable: false),
                    reserved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    // The PK on the address itself is the uniqueness
                    // allocator: two contenders for one IP → one insert
                    // wins, the other sees 23505 (ADR-0001).
                    table.PrimaryKey("PK_ip_leases", x => x.address);
                    table.ForeignKey(
                        name: "FK_ip_leases_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "sessions",
                        principalColumn: "session_id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ip_leases_session_id",
                table: "ip_leases",
                column: "session_id");

            migrationBuilder.CreateIndex(
                name: "IX_sessions_idempotency_key",
                table: "sessions",
                column: "idempotency_key",
                unique: true,
                filter: "idempotency_key IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_sessions_node_id_lab_session_id",
                table: "sessions",
                columns: new[] { "node_id", "lab_session_id" },
                unique: true,
                filter: "status IN ('Pending','Active','Revoking')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ip_leases");

            migrationBuilder.DropTable(
                name: "outbox");

            migrationBuilder.DropTable(
                name: "sessions");

            migrationBuilder.DropTable(
                name: "nodes");
        }
    }
}
