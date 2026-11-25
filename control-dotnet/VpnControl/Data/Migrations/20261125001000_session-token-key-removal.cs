using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VpnControl.Data.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(ControlDbContext))]
    [Migration("20261125001000_session-token-key-removal")]
    public partial class SessionTokenKeyRemoval : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "session_token_key",
                table: "sessions");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "session_token_key",
                table: "sessions",
                type: "bytea",
                nullable: true);
        }
    }
}
