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

    private sealed class NullableValues
    {
        public int Id { get; set; }
        public string? Text { get; set; }
        public int? Number { get; set; }
        public DateTime? Date { get; set; }
        public byte[]? Bytes { get; set; }
    }
}
