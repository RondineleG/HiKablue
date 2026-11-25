using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VpnControl.Data.Migrations
{
    /// <inheritdoc />
    public partial class SessionAccessToken : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "session_token_hash",
                table: "sessions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "session_token_hash",
                table: "sessions");
        }
    }
}
