using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RequestRouting.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Experts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Capacity = table.Column<int>(type: "int", nullable: false),
                    OpenCount = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    LastAssignedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Experts", x => x.Id);
                    table.CheckConstraint("CK_Experts_Capacity", "[Capacity] >= 1");
                    table.CheckConstraint("CK_Experts_OpenCount", "[OpenCount] >= 0 AND [OpenCount] <= [Capacity]");
                });

            migrationBuilder.CreateTable(
                name: "ExpertTopics",
                columns: table => new
                {
                    ExpertId = table.Column<int>(type: "int", nullable: false),
                    Topic = table.Column<string>(type: "varchar(50)", unicode: false, maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExpertTopics", x => new { x.ExpertId, x.Topic });
                    table.ForeignKey(
                        name: "FK_ExpertTopics_Experts_ExpertId",
                        column: x => x.ExpertId,
                        principalTable: "Experts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ServiceRequests",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RequestKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Topic = table.Column<string>(type: "varchar(50)", unicode: false, maxLength: 50, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Priority = table.Column<byte>(type: "tinyint", nullable: false),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    ExpertId = table.Column<int>(type: "int", nullable: true),
                    QueueRank = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    AssignedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ClosedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServiceRequests", x => x.Id);
                    table.CheckConstraint("CK_ServiceRequests_StatusExpert", "([Status] = 1 AND [ExpertId] IS NULL) OR ([Status] = 2 AND [ExpertId] IS NOT NULL) OR [Status] = 3");
                    table.ForeignKey(
                        name: "FK_ServiceRequests_Experts_ExpertId",
                        column: x => x.ExpertId,
                        principalTable: "Experts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExpertTopics_Topic_ExpertId",
                table: "ExpertTopics",
                columns: new[] { "Topic", "ExpertId" });

            migrationBuilder.CreateIndex(
                name: "IX_ServiceRequests_ExpertId",
                table: "ServiceRequests",
                column: "ExpertId");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceRequests_Queue",
                table: "ServiceRequests",
                columns: new[] { "QueueRank", "Id" },
                filter: "[Status] = 1")
                .Annotation("SqlServer:Include", new[] { "Topic" });

            migrationBuilder.CreateIndex(
                name: "IX_ServiceRequests_RequestKey",
                table: "ServiceRequests",
                column: "RequestKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ServiceRequests_TopicQueue",
                table: "ServiceRequests",
                columns: new[] { "Topic", "QueueRank", "Id" },
                filter: "[Status] = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExpertTopics");

            migrationBuilder.DropTable(
                name: "ServiceRequests");

            migrationBuilder.DropTable(
                name: "Experts");
        }
    }
}
