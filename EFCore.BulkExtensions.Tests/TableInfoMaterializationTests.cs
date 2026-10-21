using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace EFCore.BulkExtensions.Tests;

public class TableInfoMaterializationTests
{
    [Fact]
    public void UpdateReadEntities_DatabaseNulls_ClearExistingReferenceAndNullableValues()
    {
        using var context = new DbContext(new DbContextOptionsBuilder()
            .UseGaussDB("Host=localhost;Database=materialization_only").Options);
        var info = CreateInfo();
        List<NullableValues> requested = [new()
        {
            Id = 1, Text = "stale", Number = 7, Date = new DateTime(2024, 1, 1), Bytes = [1, 2]
        }];
        List<NullableValues> returned = [new() { Id = 1 }];

        info.UpdateReadEntities(requested, returned, context);

        Assert.Null(requested[0].Text);
        Assert.Null(requested[0].Number);
        Assert.Null(requested[0].Date);
        Assert.Null(requested[0].Bytes);
    }

    [Fact]
    public void UpdateReadEntities_IncludedColumns_DoesNotClearExcludedValues()
    {
        using var context = new DbContext(new DbContextOptionsBuilder()
            .UseGaussDB("Host=localhost;Database=materialization_only").Options);
        var info = CreateInfo();
        info.BulkConfig.LoadOnlyIncludedColumns = true;
        info.PropertyColumnNamesDict = new() { [nameof(NullableValues.Id)] = "Id", [nameof(NullableValues.Text)] = "Text" };
        List<NullableValues> requested = [new() { Id = 1, Text = "stale", Number = 7, Bytes = [1, 2] }];

        info.UpdateReadEntities(requested, new List<NullableValues> { new() { Id = 1 } }, context);

        Assert.Null(requested[0].Text);
        Assert.Equal(7, requested[0].Number);
        Assert.Equal(new byte[] { 1, 2 }, requested[0].Bytes);
    }

    private static TableInfo CreateInfo()
    {
        var properties = typeof(NullableValues).GetProperties();
        return new()
        {
            NumberOfEntities = 1, BulkConfig = new BulkConfig(),
            PrimaryKeysPropertyColumnNameDict = new() { [nameof(NullableValues.Id)] = "Id" },
            PropertyColumnNamesDict = properties.ToDictionary(property => property.Name, property => property.Name),
            OutputPropertyColumnNamesDict = properties.ToDictionary(property => property.Name, property => property.Name),
            FastPropertyDict = properties.ToDictionary(property => property.Name, FastProperty.GetOrCreate)
        };
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UpdateReadEntities_PartialOwnedProjection_PreservesOtherValuesAndReferences(bool databaseNull)
    {
        using var context = new OwnedContext(new DbContextOptionsBuilder<OwnedContext>()
            .UseGaussDB("Host=localhost;Database=materialization_only").Options);
        var entity = new OwnedRoot { Id = 1, Home = new() { City = "stale", Level = 7, Extra = "keep", Position = new() { Code = "nested-keep" } } };
        var home = entity.Home;
        var position = home.Position;
        var info = TableInfo.CreateInstance(BulkContext.Create(context), typeof(OwnedRoot),
            new List<OwnedRoot> { entity }, OperationType.Read, new BulkConfig());
        info.BulkConfig.LoadOnlyIncludedColumns = true;
        info.PropertyColumnNamesDict = new() { ["Id"] = "Id", ["Home.City"] = "Home_City", ["Home.Level"] = "Home_Level" };
        var output = new OwnedRoot { Id = 1, Home = databaseNull ? null : new() { City = "loaded", Level = 3 } };

        info.UpdateReadEntities(new[] { entity }, new[] { output }, context);

        Assert.Same(home, entity.Home);
        Assert.Same(position, entity.Home!.Position);
        Assert.Equal(databaseNull ? null : "loaded", entity.Home.City);
        Assert.Equal(databaseNull ? 0 : 3, entity.Home.Level);
        Assert.Equal("keep", entity.Home.Extra);
        Assert.Equal("nested-keep", entity.Home.Position!.Code);
    }

    private sealed class OwnedContext(DbContextOptions<OwnedContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<OwnedRoot>().OwnsOne(row => row.Home, owned =>
            {
                owned.WithOwner().HasForeignKey(row => row.OwnerId);
                owned.Property(row => row.Extra).IsRequired();
                owned.OwnsOne(row => row.Position);
            });
    }
    private sealed class OwnedRoot
    {
        public int Id { get; set; }
        public OwnedAddress? Home { get; set; }
    }
    private sealed class OwnedAddress
    {
        public int OwnerId { get; set; }
        public int Level { get; set; }
        public string? City { get; set; }
        public string? Extra { get; set; }
        public OwnedPosition? Position { get; set; }
    }
    private sealed class OwnedPosition { public string? Code { get; set; } }

    [Fact]
    public void UpdateReadEntities_FullOwnedProjectionWithClrOwnershipKey_ClearsMissingOwnedValue()
    {
        using var context = new OwnedContext(new DbContextOptionsBuilder<OwnedContext>()
            .UseGaussDB("Host=localhost;Database=materialization_only").Options);
        var entity = new OwnedRoot { Id = 1, Home = new() { OwnerId = 1, City = "stale", Extra = "stale", Position = new() { Code = "stale" } } };
        var info = TableInfo.CreateInstance(BulkContext.Create(context), typeof(OwnedRoot),
            new[] { entity }, OperationType.Read, new BulkConfig());
        Assert.DoesNotContain("Home.OwnerId", info.OutputPropertyColumnNamesDict.Keys);

        info.UpdateReadEntities(new[] { entity }, new[] { new OwnedRoot { Id = 1 } }, context);

        Assert.Null(entity.Home);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TableInfo_AutomaticTimestampExclusion_DoesNotPolluteReusableConfiguration(bool ignoreRowVersion)
    {
        using var context = new TimestampContext(new DbContextOptionsBuilder<TimestampContext>()
            .UseGaussDB("Host=localhost;Database=materialization_only").Options);
        var config = new BulkConfig { IgnoreRowVersion = ignoreRowVersion, PropertiesToInclude = ["Id", "Name"] };
        var versioned = TableInfo.CreateInstance(BulkContext.Create(context), typeof(VersionedRoot),
            new[] { new VersionedRoot { Id = 1, Name = "versioned" } }, OperationType.Update, config);
        Assert.DoesNotContain("Version", versioned.PropertyColumnNamesDict.Keys);
        Assert.Null(config.PropertiesToExclude);
        var plain = TableInfo.CreateInstance(BulkContext.Create(context), typeof(PlainRoot),
            new[] { new PlainRoot { Id = 2, Name = "plain" } }, OperationType.Update, config);
        Assert.Contains("Name", plain.PropertyColumnNamesUpdateDict.Keys);
        Assert.Null(config.PropertiesToExclude);
    }

    private sealed class TimestampContext(DbContextOptions<TimestampContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<VersionedRoot>().Property(row => row.Version).IsRowVersion();
            modelBuilder.Entity<PlainRoot>();
        }
    }
    private sealed class VersionedRoot
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public byte[] Version { get; set; } = [];
    }
    private sealed class PlainRoot
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
    }

    private sealed class NullableValues
    {
        public int Id { get; set; }
        public string? Text { get; set; }
        public int? Number { get; set; }
        public DateTime? Date { get; set; }
        public byte[]? Bytes { get; set; }
    }
}
