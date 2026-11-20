using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VpnControl.Data.Migrations
{
    /// <inheritdoc />
    public partial class OutboxDispatch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "attempts",
                table: "outbox",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "backend_instance_id",
                table: "outbox",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "deadline",
                table: "outbox",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "next_retry_at",
                table: "outbox",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "status",
                table: "outbox",
                type: "text",
                nullable: false,
                defaultValue: "pending");

            migrationBuilder.AddColumn<string>(
                name: "stream_epoch",
                table: "outbox",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_outbox_status_next_retry_at",
                table: "outbox",
                columns: new[] { "status", "next_retry_at" },
                filter: "status = 'pending'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_outbox_status_next_retry_at",
                table: "outbox");

            migrationBuilder.DropColumn(
                name: "attempts",
                table: "outbox");

            migrationBuilder.DropColumn(
                name: "backend_instance_id",
                table: "outbox");

            migrationBuilder.DropColumn(
                name: "deadline",
                table: "outbox");

            migrationBuilder.DropColumn(
                name: "next_retry_at",
                table: "outbox");

            migrationBuilder.DropColumn(
                name: "status",
                table: "outbox");

            migrationBuilder.DropColumn(
                name: "stream_epoch",
                table: "outbox");
        }
    }
}
