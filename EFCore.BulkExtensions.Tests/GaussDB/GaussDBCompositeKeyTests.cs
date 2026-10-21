using Microsoft.EntityFrameworkCore;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace EFCore.BulkExtensions.Tests.GaussDB;

public partial class GaussDBBulkTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BulkRead_CompositeKeysWithSeparators_PreservesEachEntity(bool isAsync)
    {
        using var fixtureContext = _fixture.CreateContext();
        await using var context = new CompositeMatchContext(new DbContextOptionsBuilder<CompositeMatchContext>()
            .UseGaussDB(fixtureContext.Database.GetConnectionString()!).Options);
        await context.Database.ExecuteSqlRawAsync("""
            DROP TABLE IF EXISTS "GaussDbCompositeMatchRows";
            CREATE TABLE "GaussDbCompositeMatchRows" (
                "Tenant" text NOT NULL, "Code" text NOT NULL, "Value" text NOT NULL,
                PRIMARY KEY ("Tenant", "Code")
            );
            """);
        try
        {
            List<CompositeMatchRow> stored =
            [
                new() { Tenant = "a_b", Code = "c", Value = "first" },
                new() { Tenant = "a", Code = "b_c", Value = "second" },
                new() { Tenant = "", Code = "_", Value = "third" },
                new() { Tenant = "_", Code = "", Value = "fourth" }
            ];
            if (isAsync) await context.BulkInsertAsync(stored);
            else context.BulkInsert(stored);

            var requested = stored.AsEnumerable().Reverse().Select(row => new CompositeMatchRow
            {
                Tenant = row.Tenant, Code = row.Code
            }).ToList();
            if (isAsync) await context.BulkReadAsync(requested);
            else context.BulkRead(requested);

            Assert.Equal(stored.AsEnumerable().Reverse().Select(row => (row.Tenant, row.Code, row.Value)),
                requested.Select(row => (row.Tenant, row.Code, row.Value)));
        }
        finally
        {
            await context.Database.ExecuteSqlRawAsync("""DROP TABLE IF EXISTS "GaussDbCompositeMatchRows";""");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BulkRead_NullableAndArrayMatchKeys_RoundTripDistinctValues(bool isAsync)
    {
        using var fixtureContext = _fixture.CreateContext();
        await using var context = new CompositeMatchContext(new DbContextOptionsBuilder<CompositeMatchContext>()
            .UseGaussDB(fixtureContext.Database.GetConnectionString()!).Options);
        await context.Database.ExecuteSqlRawAsync("""
            DROP TABLE IF EXISTS "GaussDbNullableMatchRows";
            DROP TABLE IF EXISTS "GaussDbArrayMatchRows";
            CREATE TABLE "GaussDbNullableMatchRows" ("Id" integer PRIMARY KEY, "Key" text NULL, "Value" text NOT NULL);
            CREATE TABLE "GaussDbArrayMatchRows" ("Id" integer PRIMARY KEY, "Key" integer[] NOT NULL, "Value" text NOT NULL);
            """);
        try
        {
            List<NullableMatchRow> nullableRows =
            [
                new() { Id = 1, Key = null, Value = "absent" },
                new() { Id = 2, Key = "null", Value = "literal" }
            ];
            List<ArrayMatchRow> arrayRows =
            [
                new() { Id = 1, Key = [1, 23], Value = "first" },
                new() { Id = 2, Key = [12, 3], Value = "second" }
            ];
            if (isAsync)
            {
                await context.BulkInsertAsync(nullableRows);
                await context.BulkInsertAsync(arrayRows);
            }
            else
            {
                context.BulkInsert(nullableRows);
                context.BulkInsert(arrayRows);
            }

            List<NullableMatchRow> requestedNullable = [new() { Key = "null" }, new() { Key = null }];
            List<ArrayMatchRow> requestedArrays = [new() { Key = [12, 3] }, new() { Key = [1, 23] }];
            if (isAsync)
            {
                await context.BulkReadAsync(requestedNullable, new BulkConfig { UpdateByProperties = [nameof(NullableMatchRow.Key)] });
                await context.BulkReadAsync(requestedArrays, new BulkConfig { UpdateByProperties = [nameof(ArrayMatchRow.Key)] });
            }
            else
            {
                context.BulkRead(requestedNullable, new BulkConfig { UpdateByProperties = [nameof(NullableMatchRow.Key)] });
                context.BulkRead(requestedArrays, new BulkConfig { UpdateByProperties = [nameof(ArrayMatchRow.Key)] });
            }

            Assert.Equal(new[] { 2, 1 }, requestedNullable.Select(row => row.Id));
            Assert.Equal(new[] { "literal", "absent" }, requestedNullable.Select(row => row.Value));
            Assert.Equal("null", requestedNullable[0].Key);
            Assert.Null(requestedNullable[1].Key);
            Assert.Equal(new[] { 2, 1 }, requestedArrays.Select(row => row.Id));
            Assert.Equal(new[] { "second", "first" }, requestedArrays.Select(row => row.Value));
            Assert.Equal(new[] { 12, 3 }, requestedArrays[0].Key);
            Assert.Equal(new[] { 1, 23 }, requestedArrays[1].Key);
        }
        finally
        {
            await context.Database.ExecuteSqlRawAsync("""
                DROP TABLE IF EXISTS "GaussDbNullableMatchRows";
                DROP TABLE IF EXISTS "GaussDbArrayMatchRows";
                """);
        }
    }

    [Fact]
    public void UpdateReadEntities_NullAndLiteralNullKeys_RemainDistinct()
    {
        using var context = _fixture.CreateContext();
        var info = CreateNullableMatchInfo();
        List<NullableMatchRow> requested = [new() { Key = "null" }, new() { Key = null }];
        List<NullableMatchRow> returned = [new() { Key = null, Value = "absent" }, new() { Key = "null", Value = "literal" }];

        info.UpdateReadEntities(requested, returned, context);

        Assert.Equal("literal", requested[0].Value);
        Assert.Equal("null", requested[0].Key);
        Assert.Equal("absent", requested[1].Value);
        Assert.Null(requested[1].Key);
    }

    [Fact]
    public void UpdateReadEntities_ArrayKeys_CompareElementsWithoutConcatenation()
    {
        using var context = _fixture.CreateContext();
        var info = new TableInfo
        {
            BulkConfig = new BulkConfig(), NumberOfEntities = 2,
            PrimaryKeysPropertyColumnNameDict = new() { [nameof(ArrayMatchRow.Key)] = "Key" },
            PropertyColumnNamesDict = new() { [nameof(ArrayMatchRow.Key)] = "Key", [nameof(ArrayMatchRow.Value)] = "Value" },
            OutputPropertyColumnNamesDict = new() { [nameof(ArrayMatchRow.Key)] = "Key", [nameof(ArrayMatchRow.Value)] = "Value" },
            FastPropertyDict = typeof(ArrayMatchRow).GetProperties().ToDictionary(property => property.Name, FastProperty.GetOrCreate)
        };
        List<ArrayMatchRow> requested = [new() { Key = [12, 3] }, new() { Key = [1, 23] }];
        List<ArrayMatchRow> returned = [new() { Key = [1, 23], Value = "first" }, new() { Key = [12, 3], Value = "second" }];

        info.UpdateReadEntities(requested, returned, context);

        Assert.Equal("second", requested[0].Value);
        Assert.Equal(new[] { 12, 3 }, requested[0].Key);
        Assert.Equal("first", requested[1].Value);
        Assert.Equal(new[] { 1, 23 }, requested[1].Key);
    }

    private static TableInfo CreateNullableMatchInfo() => new()
    {
        BulkConfig = new BulkConfig(), NumberOfEntities = 2,
        PrimaryKeysPropertyColumnNameDict = new() { [nameof(NullableMatchRow.Key)] = "Key" },
        PropertyColumnNamesDict = new() { [nameof(NullableMatchRow.Key)] = "Key", [nameof(NullableMatchRow.Value)] = "Value" },
        OutputPropertyColumnNamesDict = new() { [nameof(NullableMatchRow.Key)] = "Key", [nameof(NullableMatchRow.Value)] = "Value" },
        FastPropertyDict = typeof(NullableMatchRow).GetProperties().ToDictionary(property => property.Name, FastProperty.GetOrCreate)
    };

    private sealed class CompositeMatchContext(DbContextOptions<CompositeMatchContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<CompositeMatchRow>(entity =>
            {
                entity.ToTable("GaussDbCompositeMatchRows");
                entity.HasKey(row => new { row.Tenant, row.Code });
            });
            modelBuilder.Entity<NullableMatchRow>(entity =>
            {
                entity.ToTable("GaussDbNullableMatchRows");
                entity.Property(row => row.Id).ValueGeneratedNever();
            });
            modelBuilder.Entity<ArrayMatchRow>(entity =>
            {
                entity.ToTable("GaussDbArrayMatchRows");
                entity.Property(row => row.Id).ValueGeneratedNever();
                entity.Property(row => row.Key).HasColumnType("integer[]");
            });
        }
    }

    private sealed class CompositeMatchRow
    {
        public string Tenant { get; set; } = string.Empty;
        public string Code { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
    }

    private sealed class NullableMatchRow
    {
        public int Id { get; set; }
        public string? Key { get; set; }
        public string Value { get; set; } = string.Empty;
    }

    private sealed class ArrayMatchRow
    {
        public int Id { get; set; }
        public int[] Key { get; set; } = [];
        public string Value { get; set; } = string.Empty;
    }
}
