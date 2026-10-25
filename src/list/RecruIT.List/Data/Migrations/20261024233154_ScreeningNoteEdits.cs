using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RecruIT.List.Data.Migrations
{
    /// <inheritdoc />
    public partial class ScreeningNoteEdits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "updated_at",
                table: "screening_notes",
                type: "timestamp with time zone",
                nullable: true
            );

            migrationBuilder.CreateTable(
                name: "screening_note_revisions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    screening_note_id = table.Column<Guid>(type: "uuid", nullable: false),
                    text = table.Column<string>(type: "text", nullable: false),
                    written_at = table.Column<DateTimeOffset>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_screening_note_revisions", x => x.id);
                    table.ForeignKey(
                        name: "fk_screening_note_revisions_screening_notes_screening_note_id",
                        column: x => x.screening_note_id,
                        principalTable: "screening_notes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict
                    );
                }
            );

            migrationBuilder.CreateIndex(
                name: "ix_screening_note_revisions_screening_note_id",
                table: "screening_note_revisions",
                column: "screening_note_id"
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "screening_note_revisions");

            migrationBuilder.DropColumn(name: "updated_at", table: "screening_notes");
        }
    }
}
