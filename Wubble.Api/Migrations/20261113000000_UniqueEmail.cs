using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Wubble.Api.Data;

namespace Wubble.Api.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20261113000000_UniqueEmail")]
public sealed class UniqueEmail : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(name: "EmailIndex", table: "AspNetUsers");
        migrationBuilder.CreateIndex(name: "EmailIndex", table: "AspNetUsers",
            column: "NormalizedEmail", unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(name: "EmailIndex", table: "AspNetUsers");
        migrationBuilder.CreateIndex(name: "EmailIndex", table: "AspNetUsers",
            column: "NormalizedEmail");
    }
}
