using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RtspQrApi.Migrations
{
    /// <inheritdoc />
    public partial class InitialQrScanRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "QrScanRecords",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CameraId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Value = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                    DetectedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    FrameAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QrScanRecords", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_QrScanRecords_CameraId_DetectedAt",
                table: "QrScanRecords",
                columns: new[] { "CameraId", "DetectedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "QrScanRecords");
        }
    }
}
