using Microsoft.EntityFrameworkCore;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace EFCore.BulkExtensions.Tests.GaussDB;

public partial class GaussDBBulkTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task BulkOutput_WithOwnedObjectsAndQueryFilter_RoundTrips(bool isAsync, bool stats)
    {
        using var connectionContext = _fixture.CreateContext();
        using var db = new OwnedOutputContext(connectionContext.Database.GetConnectionString()!);
        await db.Database.ExecuteSqlRawAsync("""
            DROP TABLE IF EXISTS "OwnedOutput";
            CREATE TABLE "OwnedOutput" (
                "Id" serial PRIMARY KEY, "Name" text NOT NULL, "Hidden" boolean NOT NULL,
                "Details_Code" text NULL, "Details_Nested_Value" text NULL);
            """);
        List<OwnedOutputRow> rows = [new() { Name = "first", Details = new() { Code = "A", Nested = new() { Value = "nested" } } },
            new() { Name = "second", Hidden = true, Details = new() { Code = "B" } }];
        var config = new BulkConfig { SetOutputIdentity = true, CalculateStats = stats };
        if (isAsync) await db.BulkInsertAsync(rows, config);
        else db.BulkInsert(rows, config);
        Assert.All(rows, row => Assert.True(row.Id > 0));
        Assert.Equal(2, rows.Select(row => row.Id).Distinct().Count());
        Assert.Equal("nested", rows[0].Details!.Nested!.Value);
        if (stats) Assert.Equal(2, config.StatsInfo!.StatsNumberInserted);
        rows[0].Name = "changed";
        if (isAsync) await db.BulkUpdateAsync(rows, new BulkConfig { SetOutputIdentity = true });
        else db.BulkUpdate(rows, new BulkConfig { SetOutputIdentity = true });
        Assert.Equal(2, await db.Set<OwnedOutputRow>().IgnoreQueryFilters().CountAsync());
        List<OwnedOutputRow> probes = [new() { Id = rows[0].Id }, new() { Id = rows[1].Id }];
        await db.BulkReadAsync(probes);
        Assert.Equal("changed", probes[0].Name);
        Assert.Equal("", probes[1].Name);
        await db.BulkReadAsync(probes, new BulkConfig { IgnoreGlobalQueryFilters = true });
        Assert.Equal("second", probes[1].Name);
        Assert.Equal("nested", probes[0].Details!.Nested!.Value);
        await db.BulkDeleteAsync(rows);
        Assert.Equal(0, await db.Set<OwnedOutputRow>().IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task BulkInsert_OwnedConverterWithRenamedColumn_UsesProviderValue()
    {
        using var connectionContext = _fixture.CreateContext();
        using var db = new OwnedConverterContext(connectionContext.Database.GetConnectionString()!);
        await db.Database.ExecuteSqlRawAsync("""
            DROP TABLE IF EXISTS "OwnedConverter";
            CREATE TABLE "OwnedConverter" ("Id" integer PRIMARY KEY, "custom_status" text NULL);
            """);
        var rows = new List<OwnedConverterRow> { new() { Id = 1, Details = new() { State = GaussDbStatus.Archived } } };
        await db.BulkInsertAsync(rows);
        var stored = await db.Set<OwnedConverterRow>().AsNoTracking().SingleAsync();
        Assert.Equal(GaussDbStatus.Archived, stored.Details!.State);
    }

    public sealed class OwnedOutputContext(string connection) : DbContext
    {
        protected override void OnConfiguring(DbContextOptionsBuilder options) => options.UseGaussDB(connection);
        protected override void OnModelCreating(ModelBuilder model)
        {
            var row = model.Entity<OwnedOutputRow>();
            row.ToTable("OwnedOutput");
            Microsoft.EntityFrameworkCore.GaussDBPropertyBuilderExtensions.UseIdentityColumn(row.Property(x => x.Id));
            row.HasQueryFilter(x => !x.Hidden);
            row.OwnsOne(x => x.Details, details => details.OwnsOne(x => x.Nested));
            row.Navigation(x => x.Details).IsRequired();
            row.Navigation(x => x.Details).IsRequired();
        }
    }

    public sealed class OwnedOutputRow
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public bool Hidden { get; set; }
        public OwnedOutputDetails? Details { get; set; }
    }
    public sealed class OwnedOutputDetails
    {
        public string? Code { get; set; }
        public OwnedOutputNested? Nested { get; set; }
    }
    public sealed class OwnedOutputNested { public string? Value { get; set; } }

    public sealed class OwnedConverterContext(string connection) : DbContext
    {
        protected override void OnConfiguring(DbContextOptionsBuilder options) => options.UseGaussDB(connection);
        protected override void OnModelCreating(ModelBuilder model)
        {
            var row = model.Entity<OwnedConverterRow>();
            row.ToTable("OwnedConverter");
            row.Property(x => x.Id).ValueGeneratedNever();
            row.OwnsOne(x => x.Details, details => details.Property(x => x.State)
                .HasColumnName("custom_status").HasConversion(x => "state-" + x, x => System.Enum.Parse<GaussDbStatus>(x.Substring(6))));
        }
    }
    public sealed class OwnedConverterRow
    {
        public int Id { get; set; }
        public OwnedConverterDetails? Details { get; set; }
    }
    public sealed class OwnedConverterDetails { public GaussDbStatus State { get; set; } }
}
