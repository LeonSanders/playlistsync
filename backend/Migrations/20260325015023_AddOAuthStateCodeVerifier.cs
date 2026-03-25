using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlaylistSync.Migrations
{
    /// <inheritdoc />
    public partial class AddOAuthStateCodeVerifier : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CodeVerifier",
                table: "OAuthStates",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CodeVerifier",
                table: "OAuthStates");
        }
    }
}
