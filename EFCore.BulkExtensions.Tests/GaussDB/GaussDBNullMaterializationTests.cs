using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;

namespace EFCore.BulkExtensions.Tests.GaussDB;

public partial class GaussDBBulkTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BulkRead_DatabaseNulls_ClearPreviouslyPopulatedProperties(bool isAsync)
    {
        using var fixtureContext = _fixture.CreateContext();
        await using var context = new NullReadContext(new DbContextOptionsBuilder<NullReadContext>()
            .UseGaussDB(fixtureContext.Database.GetConnectionString()!).Options);
        await context.Database.ExecuteSqlRawAsync("""
            DROP TABLE IF EXISTS "GaussDbNullReadRows";
            CREATE TABLE "GaussDbNullReadRows" (
                "Id" integer PRIMARY KEY, "Text" text NULL, "Number" integer NULL,
                "Date" timestamp without time zone NULL, "Bytes" bytea NULL
            );
            INSERT INTO "GaussDbNullReadRows" ("Id") VALUES (1);
            """);
        try
        {
            List<NullReadRow> requested = [new()
            {
                Id = 1, Text = "stale", Number = 7, Date = new DateTime(2024, 1, 1), Bytes = [1, 2]
            }];
            if (isAsync) await context.BulkReadAsync(requested);
            else context.BulkRead(requested);

            Assert.Null(requested[0].Text);
            Assert.Null(requested[0].Number);
            Assert.Null(requested[0].Date);
            Assert.Null(requested[0].Bytes);
        }
        finally
        {
            await context.Database.ExecuteSqlRawAsync("""DROP TABLE IF EXISTS "GaussDbNullReadRows";""");
        }
    }

    private sealed class NullReadContext(DbContextOptions<NullReadContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<NullReadRow>(entity =>
            {
                entity.ToTable("GaussDbNullReadRows");
                entity.Property(row => row.Id).ValueGeneratedNever();
                entity.Property(row => row.Date).HasColumnType("timestamp without time zone");
            });
        }
    }

    private sealed class NullReadRow
    {
        public int Id { get; set; }
        public string? Text { get; set; }
        public int? Number { get; set; }
        public DateTime? Date { get; set; }
        public byte[]? Bytes { get; set; }
    }
}
