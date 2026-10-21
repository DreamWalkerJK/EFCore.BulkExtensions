using EFCore.BulkExtensions;
using GaussDB;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Airway/AirwayPoint/AirwaySegmentPoint coverage modelled after InternalFIPS.
/// The real model uses AirwaySegmentPoint.PointID -> AirwayPoint.PointID (an
/// alternate business key), rather than the surrogate numeric key.  Keeping
/// that relationship here is useful for validating GaussDB bulk upsert and
/// delete behaviour with shared reference data.
/// </summary>
public static class AirwayScenarios
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TestAirway>(entity =>
        {
            entity.ToTable("BulkAirway");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Identifier).HasMaxLength(16).IsRequired();
            entity.Property(x => x.Name).HasMaxLength(32).IsRequired();
            entity.Property(x => x.Type).HasMaxLength(32).IsRequired();
            entity.Property(x => x.AreaName).HasMaxLength(30).IsRequired();
            entity.Property(x => x.PointText).HasMaxLength(1000).IsRequired();
            entity.Property(x => x.Source).HasMaxLength(100).IsRequired();
            entity.Property(x => x.SourceInfo).HasMaxLength(100).IsRequired();
            entity.Property(x => x.SourceId).HasMaxLength(64).IsRequired();
            entity.HasIndex(x => x.Identifier);
            entity.HasMany(x => x.Points).WithOne(x => x.Airway)
                .HasForeignKey(x => x.AirwayId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TestAirwayPoint>(entity =>
        {
            entity.ToTable("BulkAirwayPoint");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.PointId).HasMaxLength(36).IsRequired();
            entity.Property(x => x.Code).HasMaxLength(32).IsRequired();
            entity.Property(x => x.ChineseName).HasMaxLength(32).IsRequired();
            entity.Property(x => x.PointType).HasMaxLength(32).IsRequired();
            entity.Property(x => x.Longitude).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Latitude).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Frequency).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Remarks).HasColumnType("text").IsRequired();
            entity.Property(x => x.Surname).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Source).HasMaxLength(100).IsRequired();
            entity.Property(x => x.SourceInfo).HasMaxLength(100).IsRequired();
            entity.Property(x => x.SourceId).HasMaxLength(64).IsRequired();
            entity.Property(x => x.ControlAreaCode).HasMaxLength(50).IsRequired();
            entity.Property(x => x.DesignateControlAreaCode).HasMaxLength(50).IsRequired();
            entity.Property(x => x.LonUnit).HasMaxLength(1).IsRequired();
            entity.Property(x => x.LatUnit).HasMaxLength(1).IsRequired();
            entity.HasIndex(x => x.PointId).IsUnique();
            entity.HasIndex(x => x.Code);
        });

        modelBuilder.Entity<TestAirwaySegmentPoint>(entity =>
        {
            entity.ToTable("BulkAirwaySegmentPoint");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.PointId).HasMaxLength(36).IsRequired();
            entity.Property(x => x.PointCode).HasMaxLength(32).IsRequired();
            entity.HasIndex(x => new { x.AirwayId, x.Sequence }).IsUnique();
            entity.HasOne(x => x.PointInfo).WithMany(x => x.Segments)
                .HasForeignKey(x => x.PointId).HasPrincipalKey(x => x.PointId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }

    public static async Task CreateSchemaAsync(BulkPackageDbContext context)
    {
        await context.Database.ExecuteSqlRawAsync("""
            DROP TABLE IF EXISTS "BulkAirwaySegmentPoint";
            DROP TABLE IF EXISTS "BulkAirway";
            DROP TABLE IF EXISTS "BulkAirwayPoint";
            CREATE TABLE "BulkAirwayPoint" (
                "Id" serial PRIMARY KEY,
                "PointId" varchar(36) NOT NULL UNIQUE,
                "Code" varchar(32) NOT NULL,
                "ChineseName" varchar(32) NOT NULL,
                "PointType" varchar(32) NOT NULL,
                "Longitude" varchar(64) NOT NULL,
                "Latitude" varchar(64) NOT NULL,
                "Frequency" varchar(64) NOT NULL,
                "Remarks" text NOT NULL,
                "Surname" varchar(64) NOT NULL,
                "StartDate" timestamp with time zone NOT NULL,
                "EndDate" timestamp with time zone NOT NULL,
                "Source" varchar(100) NOT NULL,
                "SourceInfo" varchar(100) NOT NULL,
                "SourceId" varchar(64) NOT NULL,
                "ControlAreaCode" varchar(50) NOT NULL,
                "DesignateControlAreaCode" varchar(50) NOT NULL,
                "LonUnit" varchar(1) NOT NULL,
                "LatUnit" varchar(1) NOT NULL
            );
            CREATE TABLE "BulkAirway" (
                "Id" serial PRIMARY KEY,
                "Identifier" varchar(16) NOT NULL,
                "Name" varchar(32) NOT NULL,
                "Type" varchar(32) NOT NULL,
                "AreaName" varchar(30) NOT NULL,
                "MinAltitude" integer NOT NULL,
                "PointText" varchar(1000) NOT NULL,
                "StartDate" timestamp with time zone NOT NULL,
                "EndDate" timestamp with time zone NOT NULL,
                "Source" varchar(100) NOT NULL,
                "SourceInfo" varchar(100) NOT NULL,
                "SourceId" varchar(64) NOT NULL
            );
            CREATE INDEX "IX_BulkAirway_Identifier" ON "BulkAirway" ("Identifier");
            CREATE TABLE "BulkAirwaySegmentPoint" (
                "Id" serial PRIMARY KEY,
                "AirwayId" integer NOT NULL REFERENCES "BulkAirway"("Id") ON DELETE CASCADE,
                "Sequence" integer NOT NULL,
                "PointId" varchar(36) NOT NULL REFERENCES "BulkAirwayPoint"("PointId") ON DELETE RESTRICT,
                "PointCode" varchar(32) NOT NULL,
                CONSTRAINT "AK_BulkAirwaySegmentPoint_Airway_Sequence" UNIQUE ("AirwayId", "Sequence")
            );
            CREATE INDEX "IX_BulkAirwaySegmentPoint_PointId" ON "BulkAirwaySegmentPoint" ("PointId");
            """);
    }

    public static async Task<string> RunCrudAsync(BulkPackageDbContext context)
    {
        await ResetRowsAsync(context);
        var points = CreatePoints(3, "crud");
        var airway = CreateAirway("CRUD-1", points);
        context.AddRange(points);
        context.Add(airway);
        await context.SaveChangesAsync();

        context.ChangeTracker.Clear();
        var loaded = await context.Set<TestAirway>().Include(x => x.Points).ThenInclude(x => x.PointInfo)
            .SingleAsync(x => x.Identifier == "CRUD-1");
        loaded.Points = loaded.Points.OrderBy(x => x.Sequence).ToList();
        if (loaded.Points.Count != 3 || loaded.Points[1].PointInfo?.Code != "CR02")
            throw new InvalidOperationException("Airway graph query mismatch");

        loaded.Name = "CRUD-UPDATED";
        loaded.Points[0].Sequence = 10;
        loaded.Points[0].PointCode = "CR01-UPDATED";
        loaded.Points[0].PointInfo!.Remarks = "updated ordinary CRUD";
        await context.SaveChangesAsync();
        var persisted = await context.Set<TestAirway>().AsNoTracking().Include(x => x.Points).ThenInclude(x => x.PointInfo)
            .SingleAsync(x => x.Id == airway.Id);
        var persistedPoint = persisted.Points.Single(x => x.Sequence == 10);
        if (persisted.Name != "CRUD-UPDATED" || persistedPoint.PointCode != "CR01-UPDATED"
            || persistedPoint.PointInfo.Remarks != "updated ordinary CRUD")
            throw new InvalidOperationException("Airway ordinary scalar/nested update mismatch");

        context.Remove(loaded.Points[2]);
        await context.SaveChangesAsync();
        context.Remove(loaded);
        await context.SaveChangesAsync();
        if (await context.Set<TestAirway>().AnyAsync() || await context.Set<TestAirwaySegmentPoint>().AnyAsync())
            throw new InvalidOperationException("Airway ordinary delete did not cascade");
        if (await context.Set<TestAirwayPoint>().CountAsync() != 3)
            throw new InvalidOperationException("Shared airway points were unexpectedly deleted");
        context.RemoveRange(await context.Set<TestAirwayPoint>().ToListAsync());
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        return $"ordinary CRUD: 1 airway, {points.Count} shared points; nested query/update and cascade/shared-reference delete passed";
    }

    public static async Task<string> RunBulkCrudAsync(BulkPackageDbContext context)
    {
        await ResetRowsAsync(context);
        var points = CreatePoints(30, "bulk");
        await context.BulkInsertAsync(points, new BulkConfig { SetOutputIdentity = true });
        if (points.Any(x => x.Id <= 0) || points.Select(x => x.Id).Distinct().Count() != points.Count)
            throw new InvalidOperationException("Airway point identities were not populated correctly");
        var airways = Enumerable.Range(1, 5).Select(i => CreateAirway($"BULK-{i:00}", points.Skip((i - 1) * 5).Take(8).ToList())).ToList();
        await context.BulkInsertAsync(airways, new BulkConfig { SetOutputIdentity = true });
        if (airways.Any(x => x.Id <= 0) || airways.Select(x => x.Id).Distinct().Count() != airways.Count)
            throw new InvalidOperationException("Airway identities were not populated correctly");
        // BulkInsert does not walk navigation properties.  Apply generated
        // airway identities before sending the segment rows as their own set.
        foreach (var airway in airways)
            foreach (var segment in airway.Points)
                segment.AirwayId = airway.Id;
        var segments = airways.SelectMany(a => a.Points).ToList();
        await context.BulkInsertAsync(segments, new BulkConfig { SetOutputIdentity = true });
        if (await context.Set<TestAirwaySegmentPoint>().CountAsync() != segments.Count)
            throw new InvalidOperationException("Airway segment bulk insert mismatch");

        foreach (var airway in airways) airway.Name += "-UPD";
        await context.BulkUpdateAsync(airways);
        foreach (var point in points.Take(10)) point.Remarks = "bulk-updated";
        await context.BulkUpdateAsync(points.Take(10).ToList());

        var loaded = await context.Set<TestAirway>().AsNoTracking().Include(x => x.Points).ThenInclude(x => x.PointInfo)
            .OrderBy(x => x.Identifier).ToListAsync();
        if (loaded.Count != airways.Count || loaded.Any(x => x.Name != $"Airway {x.Identifier}-UPD" || x.Points.Count != 8)
            || loaded.SelectMany(x => x.Points).Any(x => x.PointInfo.PointId != x.PointId))
            throw new InvalidOperationException("Airway bulk scalar update or nested alternate-key query mismatch");
        if (await context.Set<TestAirwayPoint>().CountAsync(x => x.Remarks == "bulk-updated") != 10)
            throw new InvalidOperationException("Airway point bulk update mismatch");

        var readRequest = new List<TestAirwayPoint> { new() { PointId = points[0].PointId } };
        await context.BulkReadAsync(readRequest, new BulkConfig { UpdateByProperties = [nameof(TestAirwayPoint.PointId)] });
        if (readRequest[0].Code != points[0].Code) throw new InvalidOperationException("Airway point bulk read mismatch");

        var upsert = segments.Take(6).Select(x => new TestAirwaySegmentPoint
        {
            AirwayId = x.AirwayId, Sequence = x.Sequence, PointId = x.PointId, PointCode = x.PointCode + "-U"
        }).ToList();
        await context.BulkInsertOrUpdateAsync(upsert, new BulkConfig { UpdateByProperties = [nameof(TestAirwaySegmentPoint.AirwayId), nameof(TestAirwaySegmentPoint.Sequence)] });
        var updatedSegments = await context.Set<TestAirwaySegmentPoint>().CountAsync(x => x.PointCode.EndsWith("-U"));
        if (updatedSegments != upsert.Count) throw new InvalidOperationException("Airway segment upsert mismatch");

        // A shared point must not be removable while segments in multiple
        // airways still reference its string alternate key.  Roll back the
        // intentionally failed statement before continuing with real deletes.
        var sharedPoint = points[5];
        if (await context.Set<TestAirwaySegmentPoint>().CountAsync(x => x.PointId == sharedPoint.PointId) != 2)
            throw new InvalidOperationException("Airway shared-reference setup mismatch");
        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            var rejected = false;
            try
            {
                await context.BulkDeleteAsync(new List<TestAirwayPoint> { sharedPoint });
            }
            catch (Exception exception) when (HasSqlState(exception, "23503"))
            {
                rejected = true;
            }
            finally
            {
                await transaction.RollbackAsync();
                context.ChangeTracker.Clear();
            }
            if (!rejected) throw new InvalidOperationException("Referenced airway point deletion was not rejected by its foreign key");
        }

        var deleteSegments = await context.Set<TestAirwaySegmentPoint>().Where(x => x.AirwayId == airways[0].Id).ToListAsync();
        await context.BulkDeleteAsync(deleteSegments);
        await context.BulkDeleteAsync(airways);
        if (await context.Set<TestAirway>().AnyAsync() || await context.Set<TestAirwaySegmentPoint>().AnyAsync())
            throw new InvalidOperationException("Airway bulk delete mismatch");
        if (await context.Set<TestAirwayPoint>().CountAsync() != points.Count)
            throw new InvalidOperationException("Airway bulk delete removed shared point reference data");
        await context.BulkDeleteAsync(points);
        if (await context.Set<TestAirwayPoint>().AnyAsync())
            throw new InvalidOperationException("Airway point bulk delete mismatch");
        context.ChangeTracker.Clear();
        return $"bulk CRUD: {points.Count} points, {airways.Count} airways, {segments.Count} segments; update/read/upsert/delete and shared-reference protection passed";
    }

    private static async Task ResetRowsAsync(BulkPackageDbContext context)
    {
        context.ChangeTracker.Clear();
        await context.Database.ExecuteSqlRawAsync("""
            DELETE FROM "BulkAirwaySegmentPoint";
            DELETE FROM "BulkAirway";
            DELETE FROM "BulkAirwayPoint";
            """);
    }

    private static bool HasSqlState(Exception exception, string sqlState)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
            if (current is PostgresException databaseException && databaseException.SqlState == sqlState)
                return true;
        return false;
    }

    private static List<TestAirwayPoint> CreatePoints(int count, string prefix) => Enumerable.Range(1, count).Select(i => new TestAirwayPoint
    {
        PointId = $"{prefix}-P{i:000}", Code = $"{prefix[..2].ToUpperInvariant()}{i:00}", ChineseName = $"航点{i}", PointType = i % 2 == 0 ? "VOR" : "FIX",
        Longitude = $"120.{i:000}", Latitude = $"30.{i:000}", Frequency = $"11{i:000}", Remarks = "seed", Surname = $"{prefix}-S{i}",
        StartDate = DateTime.UtcNow.AddDays(-1), EndDate = DateTime.UtcNow.AddDays(30), Source = "TEST", SourceInfo = prefix, SourceId = $"{prefix}-{i}",
        ControlAreaCode = "ZTEST", DesignateControlAreaCode = "ZTEST-D", LonUnit = "E", LatUnit = "N"
    }).ToList();

    private static TestAirway CreateAirway(string identifier, IReadOnlyList<TestAirwayPoint> points) => new()
    {
        Identifier = identifier, Name = $"Airway {identifier}", Type = "RNAV", AreaName = "TEST-AREA", MinAltitude = 3000,
        PointText = string.Join(' ', points.Select(x => x.Code)), StartDate = DateTime.UtcNow.AddDays(-1), EndDate = DateTime.UtcNow.AddDays(30),
        Source = "TEST", SourceInfo = "AirwayScenarios", SourceId = identifier,
        Points = points.Select((p, i) => new TestAirwaySegmentPoint { Sequence = i + 1, PointId = p.PointId, PointCode = p.Code }).ToList()
    };
}

public sealed class TestAirway
{
    public int Id { get; set; }
    public string Identifier { get; set; } = "";
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public string AreaName { get; set; } = "";
    public int MinAltitude { get; set; }
    public string PointText { get; set; } = "";
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public string Source { get; set; } = "";
    public string SourceInfo { get; set; } = "";
    public string SourceId { get; set; } = "";
    public List<TestAirwaySegmentPoint> Points { get; set; } = [];
}

public sealed class TestAirwayPoint
{
    public int Id { get; set; }
    public string PointId { get; set; } = "";
    public string Code { get; set; } = "";
    public string ChineseName { get; set; } = "";
    public string PointType { get; set; } = "";
    public string Longitude { get; set; } = "";
    public string Latitude { get; set; } = "";
    public string Frequency { get; set; } = "";
    public string Remarks { get; set; } = "";
    public string Surname { get; set; } = "";
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public string Source { get; set; } = "";
    public string SourceInfo { get; set; } = "";
    public string SourceId { get; set; } = "";
    public string ControlAreaCode { get; set; } = "";
    public string DesignateControlAreaCode { get; set; } = "";
    public string LonUnit { get; set; } = "";
    public string LatUnit { get; set; } = "";
    public List<TestAirwaySegmentPoint> Segments { get; set; } = [];
}

public sealed class TestAirwaySegmentPoint
{
    public int Id { get; set; }
    public int AirwayId { get; set; }
    public int Sequence { get; set; }
    public string PointId { get; set; } = "";
    public string PointCode { get; set; } = "";
    public TestAirway Airway { get; set; } = null!;
    public TestAirwayPoint PointInfo { get; set; } = null!;
}
