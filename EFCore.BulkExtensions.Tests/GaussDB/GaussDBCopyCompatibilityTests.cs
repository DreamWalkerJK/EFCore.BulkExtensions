using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace EFCore.BulkExtensions.Tests.GaussDB;

public partial class GaussDBBulkTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Copy_FixedWidthCharacterStrings_RoundTrip(bool isAsync)
    {
        using var fixtureContext = _fixture.CreateContext();
        await using var db = new CopyCompatibilityContext(new DbContextOptionsBuilder<CopyCompatibilityContext>()
            .UseGaussDB(fixtureContext.Database.GetConnectionString()!).Options);
        await db.Database.ExecuteSqlRawAsync("""
            DROP TABLE IF EXISTS "GaussDbCopyCompatibility";
            CREATE TABLE "GaussDbCopyCompatibility" ("Id" integer PRIMARY KEY, "Code" char(8), "Alias" character(8));
            """);
        try
        {
            List<CopyCompatibilityRow> rows = [new() { Id = 1, Code = "ZBAA", Alias = "ZSPD" }];
            if (isAsync) await db.BulkInsertAsync(rows);
            else db.BulkInsert(rows);
            var stored = await db.Set<CopyCompatibilityRow>().AsNoTracking().SingleAsync();
            Assert.Equal("ZBAA", stored.Code!.TrimEnd());
            Assert.Equal("ZSPD", stored.Alias!.TrimEnd());
        }
        finally { await db.Database.ExecuteSqlRawAsync("""DROP TABLE "GaussDbCopyCompatibility";"""); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Copy_CommandGuardRejectsWrite_BeforeAnyRowsAreCopied(bool isAsync)
    {
        using var fixtureContext = _fixture.CreateContext();
        var connection = fixtureContext.Database.GetConnectionString()!;
        await fixtureContext.Database.ExecuteSqlRawAsync("""
            DROP TABLE IF EXISTS "GaussDbCopyCompatibility";
            CREATE TABLE "GaussDbCopyCompatibility" ("Id" integer PRIMARY KEY, "Code" char(8), "Alias" character(8));
            """);
        try
        {
            var guard = new RejectCopyGuard();
            await using var db = new CopyCompatibilityContext(new DbContextOptionsBuilder<CopyCompatibilityContext>()
                .UseGaussDB(connection).AddInterceptors(guard).Options);
            List<CopyCompatibilityRow> rows = [new() { Id = 1, Code = "ZBAA" }];
            if (isAsync) await Assert.ThrowsAsync<InvalidOperationException>(() => db.BulkInsertAsync(rows));
            else Assert.Throws<InvalidOperationException>(() => db.BulkInsert(rows));
            Assert.Equal(1, guard.Executions);
            await using var verification = new CopyCompatibilityContext(new DbContextOptionsBuilder<CopyCompatibilityContext>()
                .UseGaussDB(connection).Options);
            Assert.Empty(await verification.Set<CopyCompatibilityRow>().ToListAsync());
            Assert.Null(db.Database.CurrentTransaction);
        }
        finally { await fixtureContext.Database.ExecuteSqlRawAsync("""DROP TABLE "GaussDbCopyCompatibility";"""); }
    }

    private sealed class RejectCopyGuard : DbCommandInterceptor
    {
        public int Executions { get; private set; }
        public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            Executions++;
            throw new InvalidOperationException("The operation is terminal.");
        }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(NonQueryExecuting(command, eventData, result));
    }

    private sealed class CopyCompatibilityContext(DbContextOptions<CopyCompatibilityContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder model)
        {
            var row = model.Entity<CopyCompatibilityRow>();
            row.ToTable("GaussDbCopyCompatibility");
            row.Property(x => x.Id).ValueGeneratedNever();
            row.Property(x => x.Code).HasColumnType("char(8)");
            row.Property(x => x.Alias).HasColumnType("character(8)");
        }
    }
    private sealed class CopyCompatibilityRow
    {
        public int Id { get; set; }
        public string? Code { get; set; }
        public string? Alias { get; set; }
    }
}
