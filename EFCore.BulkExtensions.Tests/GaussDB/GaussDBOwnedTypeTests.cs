using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace EFCore.BulkExtensions.Tests.GaussDB;

public partial class GaussDBBulkTests
{
    [Fact]
    public void TableInfo_ReusedNestedOwnedTypes_MapsEachNavigationAndConverter()
    {
        using var fixtureContext = _fixture.CreateContext();
        using var context = CreateOwnedContext(fixtureContext.Database.GetConnectionString()!);
        var info = TableInfo.CreateInstance(BulkContext.Create(context), typeof(OwnedDocument),
            new List<OwnedDocument> { new() }, OperationType.Insert, new BulkConfig());

        Assert.Equal("home_state_db", info.PropertyColumnNamesDict["Home.State"]);
        Assert.Equal("work_state_db", info.PropertyColumnNamesDict["Work.State"]);
        Assert.Equal("Home_Position_Latitude", info.PropertyColumnNamesDict["Home.Position.Latitude"]);
        Assert.Equal("Work_Position_Latitude", info.PropertyColumnNamesDict["Work.Position.Latitude"]);
        Assert.Equal("state-Active", info.ConvertibleColumnConverterDict["home_state_db"].ConvertToProvider(GaussDbStatus.Active));
        Assert.Equal("state-Archived", info.ConvertibleColumnConverterDict["work_state_db"].ConvertToProvider(GaussDbStatus.Archived));
    }

    [Fact]
    public void TableInfo_OwnedUpdateSelection_OnlyIncludesRequestedOwnedColumn()
    {
        using var fixtureContext = _fixture.CreateContext();
        using var context = CreateOwnedContext(fixtureContext.Database.GetConnectionString()!);
        var info = TableInfo.CreateInstance(BulkContext.Create(context), typeof(OwnedDocument),
            new List<OwnedDocument> { new() }, OperationType.Update,
            new BulkConfig { PropertiesToIncludeOnUpdate = ["Home.State"] });

        Assert.Equal(new[] { "home_state_db" }, info.PropertyColumnNamesUpdateDict.Values);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BulkOperations_ReusedNestedOwnedTypes_RoundTripAndRespectUpdateSelection(bool isAsync)
    {
        _fixture.ResetSchema();
        using var fixtureContext = _fixture.CreateContext();
        using var context = CreateOwnedContext(fixtureContext.Database.GetConnectionString()!);
        context.Database.ExecuteSqlRaw("""
            DROP TABLE IF EXISTS "GaussDbOwnedDocuments";
            CREATE TABLE "GaussDbOwnedDocuments" (
                "Id" integer PRIMARY KEY,
                "Name" text NOT NULL,
                "home_state_db" text NOT NULL, "Home_Position_Latitude" numeric NOT NULL,
                "work_state_db" text NOT NULL, "Work_Position_Latitude" numeric NOT NULL
            );
            """);
        try
        {
            List<OwnedDocument> rows = [new()
            {
                Id = 1, Name = "before",
                Home = new() { State = GaussDbStatus.Active, Position = new() { Latitude = 12.5m } },
                Work = new() { State = GaussDbStatus.Inactive, Position = new() { Latitude = 47.5m } }
            }];
            if (isAsync) await context.BulkInsertAsync(rows);
            else context.BulkInsert(rows);

            var saved = await context.Set<OwnedDocument>().AsNoTracking().SingleAsync();
            Assert.Equal(12.5m, saved.Home.Position.Latitude);
            Assert.Equal(47.5m, saved.Work.Position.Latitude);
            Assert.Equal(GaussDbStatus.Active, saved.Home.State);
            Assert.Equal(GaussDbStatus.Inactive, saved.Work.State);

            rows[0].Name = "must-stay-before";
            rows[0].Home.State = GaussDbStatus.Archived;
            rows[0].Home.Position.Latitude = 99m;
            rows[0].Work.State = GaussDbStatus.Archived;
            var config = new BulkConfig { PropertiesToIncludeOnUpdate = ["Home.State"] };
            if (isAsync) await context.BulkUpdateAsync(rows, config);
            else context.BulkUpdate(rows, config);

            saved = await context.Set<OwnedDocument>().AsNoTracking().SingleAsync();
            Assert.Equal("before", saved.Name);
            Assert.Equal(GaussDbStatus.Archived, saved.Home.State);
            Assert.Equal(12.5m, saved.Home.Position.Latitude);
            Assert.Equal(GaussDbStatus.Inactive, saved.Work.State);

            List<OwnedDocument> read = [new() { Id = 1 }];
            if (isAsync) await context.BulkReadAsync(read);
            else context.BulkRead(read);
            Assert.Equal(GaussDbStatus.Archived, read[0].Home.State);
            Assert.Equal(47.5m, read[0].Work.Position.Latitude);
        }
        finally
        {
            context.Database.ExecuteSqlRaw("""DROP TABLE IF EXISTS "GaussDbOwnedDocuments";""");
        }
    }

    private static OwnedDocumentContext CreateOwnedContext(string connectionString)
        => new(new DbContextOptionsBuilder<OwnedDocumentContext>().UseGaussDB(connectionString).Options);

    private sealed class OwnedDocumentContext(DbContextOptions<OwnedDocumentContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<OwnedDocument>(entity =>
            {
                entity.ToTable("GaussDbOwnedDocuments");
                entity.Property(x => x.Id).ValueGeneratedNever();
                entity.OwnsOne(x => x.Home, owned =>
                {
                    owned.Property(x => x.State).HasConversion(v => "state-" + v, v => Enum.Parse<GaussDbStatus>(v.Substring(6)))
                        .HasColumnName("home_state_db").HasColumnType("text");
                    owned.OwnsOne(x => x.Position);
                });
                entity.OwnsOne(x => x.Work, owned =>
                {
                    owned.Property(x => x.State).HasConversion(v => "state-" + v, v => Enum.Parse<GaussDbStatus>(v.Substring(6)))
                        .HasColumnName("work_state_db").HasColumnType("text");
                    owned.OwnsOne(x => x.Position);
                });
                entity.Navigation(x => x.Home).IsRequired();
                entity.Navigation(x => x.Work).IsRequired();
            });
        }
    }

    private sealed class OwnedDocument
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public OwnedAddress Home { get; set; } = new();
        public OwnedAddress Work { get; set; } = new();
    }

    private sealed class OwnedAddress
    {
        public GaussDbStatus State { get; set; }
        public OwnedPosition Position { get; set; } = new();
    }

    private sealed class OwnedPosition
    {
        public decimal Latitude { get; set; }
    }
}
