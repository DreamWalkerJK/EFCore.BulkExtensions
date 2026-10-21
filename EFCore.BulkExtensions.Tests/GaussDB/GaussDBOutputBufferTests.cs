using GaussDB;
using Microsoft.EntityFrameworkCore;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace EFCore.BulkExtensions.Tests.GaussDB;

public partial class GaussDBBulkTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BulkOutput_OwnedTempTables_ReuseCallerTransactionAndRollback(bool isAsync)
    {
        using var connectionContext = _fixture.CreateContext();
        using var db = new OwnedOutputContext(connectionContext.Database.GetConnectionString()!);
        await ResetOwnedOutputTable(db);
        await using var transaction = await db.Database.BeginTransactionAsync();
        var config = new BulkConfig
        {
            SetOutputIdentity = true, CalculateStats = true, UseTempDB = true, UniqueTableNameTempDb = false
        };
        List<OwnedOutputRow> rows = [new() { Name = "transaction", Hidden = true, Details = new() { Code = "A" } }];

        if (isAsync) await db.BulkInsertAsync(rows, config);
        else db.BulkInsert(rows, config);
        Assert.True(rows[0].Id > 0);
        Assert.Equal(1, config.StatsInfo!.StatsNumberInserted);
        rows[0].Name = "updated";
        if (isAsync) await db.BulkUpdateAsync(rows, config);
        else db.BulkUpdate(rows, config);
        Assert.Equal(1, config.StatsInfo!.StatsNumberUpdated);
        Assert.Same(transaction, db.Database.CurrentTransaction);
        Assert.Equal("updated", (await db.Set<OwnedOutputRow>().IgnoreQueryFilters().AsNoTracking().SingleAsync()).Name);

        await transaction.RollbackAsync();
        Assert.Equal(0, await db.Set<OwnedOutputRow>().IgnoreQueryFilters().CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BulkOutput_OwnedMixedIdentityUpsert_AssignsEachReturnedRow(bool newRowFirst)
    {
        using var connectionContext = _fixture.CreateContext();
        using var db = new OwnedOutputContext(connectionContext.Database.GetConnectionString()!);
        await ResetOwnedOutputTable(db);
        var existing = new OwnedOutputRow { Name = "existing", Hidden = true, Details = new() { Code = "old" } };
        await db.BulkInsertAsync(new List<OwnedOutputRow> { existing }, new BulkConfig { SetOutputIdentity = true });
        var existingId = existing.Id;
        existing.Details!.Code = "changed";
        var added = new OwnedOutputRow { Name = "added", Details = new() { Code = "new" } };
        List<OwnedOutputRow> rows = newRowFirst ? [added, existing] : [existing, added];
        var config = new BulkConfig { SetOutputIdentity = true, CalculateStats = true };

        await db.BulkInsertOrUpdateAsync(rows, config);

        Assert.Equal(existingId, existing.Id);
        Assert.True(added.Id > existingId);
        Assert.Equal("existing", existing.Name);
        Assert.Equal("added", added.Name);
        Assert.Equal(1, config.StatsInfo!.StatsNumberInserted);
        Assert.Equal(1, config.StatsInfo!.StatsNumberUpdated);
        Assert.Equal("changed", (await db.Set<OwnedOutputRow>().IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(x => x.Id == existingId)).Details!.Code);
    }

    [Fact]
    public async Task BulkOutput_PostProcessFailure_CleansResultAndStagingTables()
    {
        using var connectionContext = _fixture.CreateContext();
        using var db = new OwnedOutputContext(connectionContext.Database.GetConnectionString()!);
        await ResetOwnedOutputTable(db);
        List<OwnedOutputRow> rows = [new() { Name = "failure", Details = new() { Code = "A" } }];
        var config = new BulkConfig
        {
            SetOutputIdentity = true, CalculateStats = true,
            CustomSqlPostProcess = "SELECT * FROM \"BulkExtensions_Missing_PostProcess_Table\""
        };

        var error = await Assert.ThrowsAsync<PostgresException>(() => db.BulkInsertAsync(rows, config));

        Assert.Equal("42P01", error.SqlState);
        Assert.Equal(ConnectionState.Closed, db.Database.GetDbConnection().State);
        Assert.Equal(0, _fixture.ExecuteScalar<long>(
            """SELECT COUNT(*) FROM pg_tables WHERE schemaname = 'public' AND tablename LIKE 'OwnedOutputTemp%'"""));
    }

    private static Task ResetOwnedOutputTable(DbContext db) => db.Database.ExecuteSqlRawAsync("""
        DROP TABLE IF EXISTS "OwnedOutput";
        CREATE TABLE "OwnedOutput" (
            "Id" serial PRIMARY KEY, "Name" text NOT NULL, "Hidden" boolean NOT NULL,
            "Details_Code" text NULL, "Details_Nested_Value" text NULL);
        """);
}
