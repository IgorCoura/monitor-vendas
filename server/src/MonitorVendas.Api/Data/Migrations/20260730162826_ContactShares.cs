using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MonitorVendas.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class ContactShares : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "contact_shares",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SenderNumberId = table.Column<Guid>(type: "uuid", nullable: false),
                    Destination = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TotalContacts = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_contact_shares", x => x.Id);
                    table.ForeignKey(
                        name: "FK_contact_shares_whatsapp_numbers_SenderNumberId",
                        column: x => x.SenderNumberId,
                        principalTable: "whatsapp_numbers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "contact_share_messages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ContactShareId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    Body = table.Column<string>(type: "text", nullable: false),
                    SentAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    Error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    WaMessageId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_contact_share_messages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_contact_share_messages_contact_shares_ContactShareId",
                        column: x => x.ContactShareId,
                        principalTable: "contact_shares",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_contact_share_messages_ContactShareId_Sequence",
                table: "contact_share_messages",
                columns: new[] { "ContactShareId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_contact_share_messages_WaMessageId",
                table: "contact_share_messages",
                column: "WaMessageId");

            migrationBuilder.CreateIndex(
                name: "IX_contact_shares_SenderNumberId",
                table: "contact_shares",
                column: "SenderNumberId");

            migrationBuilder.CreateIndex(
                name: "IX_contact_shares_Status_CreatedAt",
                table: "contact_shares",
                columns: new[] { "Status", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "contact_share_messages");

            migrationBuilder.DropTable(
                name: "contact_shares");
        }
    }
}
