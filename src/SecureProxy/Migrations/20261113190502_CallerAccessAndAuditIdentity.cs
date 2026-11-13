using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SecureProxy.Migrations
{
    /// <inheritdoc />
    public partial class CallerAccessAndAuditIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CallerId",
                table: "ExecutionLogs",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TraceId",
                table: "ExecutionLogs",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ActorId",
                table: "ConfigurationChanges",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "Callers",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Enabled = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Callers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CallerOperationGrants",
                columns: table => new
                {
                    CallerId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false, collation: "Latin1_General_100_BIN2"),
                    OperationId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CallerOperationGrants", x => new { x.CallerId, x.OperationId });
                    table.ForeignKey(
                        name: "FK_CallerOperationGrants_Callers_CallerId",
                        column: x => x.CallerId,
                        principalTable: "Callers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CallerOperationGrants_Operations_OperationId",
                        column: x => x.OperationId,
                        principalTable: "Operations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CallerOperationGrants_OperationId",
                table: "CallerOperationGrants",
                column: "OperationId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CallerOperationGrants");

            migrationBuilder.DropTable(
                name: "Callers");

            migrationBuilder.DropColumn(
                name: "CallerId",
                table: "ExecutionLogs");

            migrationBuilder.DropColumn(
                name: "TraceId",
                table: "ExecutionLogs");

            migrationBuilder.DropColumn(
                name: "ActorId",
                table: "ConfigurationChanges");
        }
    }
}
