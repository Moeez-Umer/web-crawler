using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Crawler.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "crawl_jobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    url = table.Column<string>(type: "text", nullable: false),
                    max_depth = table.Column<int>(type: "integer", nullable: false),
                    max_pages = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    started_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    failure_reason = table.Column<string>(type: "text", nullable: true),
                    pending_tasks = table.Column<int>(type: "integer", nullable: false),
                    pages_crawled = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_crawl_jobs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "processed_messages",
                columns: table => new
                {
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    processed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_processed_messages", x => x.message_id);
                });

            migrationBuilder.CreateTable(
                name: "crawled_pages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    normalized_url = table.Column<string>(type: "text", nullable: false),
                    depth = table.Column<int>(type: "integer", nullable: false),
                    domain_link_ratio = table.Column<double>(type: "double precision", nullable: false),
                    total_links = table.Column<int>(type: "integer", nullable: false),
                    internal_links = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    error = table.Column<string>(type: "text", nullable: true),
                    crawled_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_crawled_pages", x => x.id);
                    table.ForeignKey(
                        name: "FK_crawled_pages_crawl_jobs_job_id",
                        column: x => x.job_id,
                        principalTable: "crawl_jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "page_edges",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    parent_page_id = table.Column<Guid>(type: "uuid", nullable: false),
                    child_url = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_page_edges", x => x.id);
                    table.ForeignKey(
                        name: "FK_page_edges_crawl_jobs_job_id",
                        column: x => x.job_id,
                        principalTable: "crawl_jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_page_edges_crawled_pages_parent_page_id",
                        column: x => x.parent_page_id,
                        principalTable: "crawled_pages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_crawl_jobs_created_at",
                table: "crawl_jobs",
                column: "created_at",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_crawled_pages_job_id",
                table: "crawled_pages",
                column: "job_id");

            migrationBuilder.CreateIndex(
                name: "ix_crawled_pages_job_id_normalized_url",
                table: "crawled_pages",
                columns: new[] { "job_id", "normalized_url" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_page_edges_job_id_parent_page_id",
                table: "page_edges",
                columns: new[] { "job_id", "parent_page_id" });

            migrationBuilder.CreateIndex(
                name: "ix_page_edges_job_id_parent_page_id_child_url",
                table: "page_edges",
                columns: new[] { "job_id", "parent_page_id", "child_url" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_page_edges_parent_page_id",
                table: "page_edges",
                column: "parent_page_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "page_edges");

            migrationBuilder.DropTable(
                name: "processed_messages");

            migrationBuilder.DropTable(
                name: "crawled_pages");

            migrationBuilder.DropTable(
                name: "crawl_jobs");
        }
    }
}
