using System.Data;
using System.Text.Json;
using EFCore.BulkExtensions;
using GaussDB;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

var testDatabase = $"bulk_current_full_{Guid.NewGuid():N}";
var started = DateTimeOffset.Now;
var baseConnectionString = Environment.GetEnvironmentVariable("GAUSSDB_TEST_CONNECTION")
    ?? throw new InvalidOperationException("Set GAUSSDB_TEST_CONNECTION before running the integration test.");
var adminConnectionString = new GaussDBConnectionStringBuilder(baseConnectionString)
{
    Database = "postgres"
}.ConnectionString;
var connectionString = new GaussDBConnectionStringBuilder(baseConnectionString)
{
    Database = testDatabase
}.ConnectionString;

await RecreateDatabaseAsync(adminConnectionString, testDatabase);

var results = new List<(string Name, string Status, string? Detail)>();
Console.WriteLine($"Started: {started:O}; runtime={Environment.Version}; database={testDatabase}; command timeout={new GaussDBConnectionStringBuilder(connectionString).CommandTimeout}s");
await using var context = new BulkPackageDbContext(connectionString);

try
{
    await Run("Connection and provider detection", async () =>
    {
        await context.Database.OpenConnectionAsync();
        var provider = context.Database.ProviderName ?? "";
        if (!provider.Contains("GaussDB", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Unexpected provider: {provider}");
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT version()";
        var serverVersion = await command.ExecuteScalarAsync();
        await context.Database.CloseConnectionAsync();
        return $"{provider}; {serverVersion}";
    });

    await Run("Create isolated schema", async () =>
    {
        await context.Database.OpenConnectionAsync();
        await context.Database.ExecuteSqlRawAsync("""
            DROP TABLE IF EXISTS "BulkPackageComposite";
            DROP TABLE IF EXISTS "BulkPackageNatural";
            DROP TABLE IF EXISTS "BulkPackageItems";
            DROP TABLE IF EXISTS "BulkFlightLabelNote";
            DROP TABLE IF EXISTS "BulkFlightLabel";
            DROP TABLE IF EXISTS "BulkFlightDepInfo";
            DROP TABLE IF EXISTS "BulkFlight";
            DROP TABLE IF EXISTS "DeepMissionConstraint";
            DROP TABLE IF EXISTS "DeepMissionWaypoint";
            DROP TABLE IF EXISTS "DeepMissionCrew";
            DROP TABLE IF EXISTS "DeepMissionSegment";
            DROP TABLE IF EXISTS "DeepMission";
            DROP TABLE IF EXISTS "BulkAirwaySegmentPoint";
            DROP TABLE IF EXISTS "BulkAirway";
            DROP TABLE IF EXISTS "BulkAirwayPoint";

            CREATE TABLE "BulkPackageItems" (
                "Id" serial PRIMARY KEY,
                "Name" varchar(120) NOT NULL,
                "Description" text NULL,
                "Quantity" integer NOT NULL DEFAULT 42,
                "Amount" numeric(12,2) NOT NULL,
                "Status" varchar(20) NOT NULL,
                "UpdatedAt" timestamp with time zone NOT NULL,
                "Tags" text[] NULL
            );
            CREATE TABLE "BulkPackageNatural" (
                "Id" serial PRIMARY KEY,
                "Code" varchar(64) NOT NULL,
                "Name" varchar(120) NOT NULL,
                "Version" integer NOT NULL
            );
            CREATE UNIQUE INDEX "IX_BulkPackageNatural_Code" ON "BulkPackageNatural" ("Code");
            CREATE TABLE "BulkPackageComposite" (
                "UserId" integer NOT NULL,
                "RoleId" integer NOT NULL,
                "Description" varchar(120) NOT NULL,
                CONSTRAINT "PK_BulkPackageComposite" PRIMARY KEY ("UserId", "RoleId")
            );
            CREATE TABLE "BulkFlight" (
                "Id" serial PRIMARY KEY,
                "FlightNumber" varchar(32) NOT NULL,
                "State" varchar(8) NOT NULL,
                "Aircraft_Code" varchar(16) NULL,
                "Aircraft_Type" varchar(64) NULL,
                "Route_FiledRoute" text NULL,
                "Route_AirwayCodes" text NULL,
                "Plan_ADEP" varchar(4) NULL,
                "Plan_ADES" varchar(4) NULL,
                "Plan_EOBT" timestamp with time zone NULL,
                "CreateTime" timestamp with time zone NOT NULL
            );
            CREATE TABLE "BulkFlightDepInfo" (
                "Id" integer PRIMARY KEY,
                "FlightId" integer NOT NULL,
                "TOBT" timestamp with time zone NULL,
                "DepStand" varchar(8) NULL,
                CONSTRAINT "FK_BulkFlightDepInfo_BulkFlight" FOREIGN KEY ("FlightId") REFERENCES "BulkFlight" ("Id") ON DELETE CASCADE
            );
            CREATE TABLE "BulkFlightLabel" (
                "Id" serial PRIMARY KEY,
                "FlightId" integer NOT NULL,
                "LabelDataId" bigint NOT NULL,
                "Name" varchar(32) NOT NULL,
                "Rank" integer NOT NULL,
                CONSTRAINT "FK_BulkFlightLabel_BulkFlight" FOREIGN KEY ("FlightId") REFERENCES "BulkFlight" ("Id") ON DELETE CASCADE
            );
            CREATE UNIQUE INDEX "IX_BulkFlightLabel_FlightId_LabelDataId" ON "BulkFlightLabel" ("FlightId", "LabelDataId");
            CREATE TABLE "BulkFlightLabelNote" (
                "Id" serial PRIMARY KEY,
                "LabelId" integer NOT NULL,
                "Text" varchar(160) NOT NULL,
                "CreatedAt" timestamp with time zone NOT NULL,
                CONSTRAINT "FK_BulkFlightLabelNote_BulkFlightLabel" FOREIGN KEY ("LabelId") REFERENCES "BulkFlightLabel" ("Id") ON DELETE CASCADE
            );
            CREATE UNIQUE INDEX "IX_BulkFlightDepInfo_FlightId" ON "BulkFlightDepInfo" ("FlightId");

            CREATE TABLE "DeepMission" (
                "Id" serial PRIMARY KEY,
                "Code" varchar(40) NOT NULL,
                "Name" varchar(160) NOT NULL,
                "Status" varchar(24) NOT NULL,
                "Priority" integer NOT NULL,
                "Version" integer NOT NULL,
                "Owner" varchar(120) NOT NULL,
                "Region" varchar(40) NOT NULL,
                "Description" text NULL,
                "StartTime" timestamp with time zone NOT NULL,
                "EndTime" timestamp with time zone NULL,
                "Budget" numeric(18,2) NOT NULL,
                "IsActive" boolean NOT NULL,
                "Score" double precision NOT NULL,
                "Tags" text[] NULL,
                "Metadata" jsonb NULL,
                "ExternalId" uuid NOT NULL,
                "AircraftRegistration" text NOT NULL,
                "AircraftType" text NOT NULL,
                "OperatorCode" text NOT NULL,
                "Departure" text NOT NULL,
                "Arrival" text NOT NULL,
                "Alternate" text NULL,
                "Callsign" text NOT NULL,
                "FlightRules" text NOT NULL,
                "ServiceType" text NOT NULL,
                "PlannedDistance" numeric(18,3) NOT NULL,
                "Fuel" numeric(18,3) NOT NULL,
                "Payload" numeric(18,3) NOT NULL,
                "PassengerCount" integer NOT NULL,
                "CruiseAltitude" integer NOT NULL,
                "CruiseSpeed" integer NOT NULL,
                "Emergency" boolean NOT NULL,
                "Approved" boolean NOT NULL,
                "ApprovalTime" timestamp with time zone NULL,
                "CreatedAt" timestamp with time zone NOT NULL,
                "UpdatedAt" timestamp with time zone NOT NULL,
                "Remarks" text NULL,
                "RevisionHash" text NOT NULL,
                "SnapshotReceived" boolean NULL
            );
            CREATE UNIQUE INDEX "IX_DeepMission_Code" ON "DeepMission" ("Code");
            CREATE TABLE "DeepMissionSegment" (
                "Id" serial PRIMARY KEY,
                "MissionId" integer NOT NULL,
                "Sequence" integer NOT NULL,
                "Code" varchar(40) NOT NULL,
                "Title" varchar(160) NOT NULL,
                "Mode" varchar(32) NOT NULL,
                "Distance" numeric(12,3) NOT NULL,
                "DurationMinutes" integer NOT NULL,
                "StartTime" timestamp with time zone NULL,
                "EndTime" timestamp with time zone NULL,
                CONSTRAINT "FK_DeepMissionSegment_DeepMission" FOREIGN KEY ("MissionId") REFERENCES "DeepMission" ("Id") ON DELETE CASCADE
            );
            CREATE UNIQUE INDEX "IX_DeepMissionSegment_MissionId_Sequence" ON "DeepMissionSegment" ("MissionId", "Sequence");
            CREATE TABLE "DeepMissionWaypoint" (
                "Id" serial PRIMARY KEY,
                "SegmentId" integer NOT NULL,
                "Sequence" integer NOT NULL,
                "Code" varchar(32) NOT NULL,
                "Latitude" numeric(10,6) NOT NULL,
                "Longitude" numeric(10,6) NOT NULL,
                "Altitude" integer NOT NULL,
                "Action" varchar(32) NOT NULL,
                CONSTRAINT "FK_DeepMissionWaypoint_DeepMissionSegment" FOREIGN KEY ("SegmentId") REFERENCES "DeepMissionSegment" ("Id") ON DELETE CASCADE
            );
            CREATE UNIQUE INDEX "IX_DeepMissionWaypoint_SegmentId_Sequence" ON "DeepMissionWaypoint" ("SegmentId", "Sequence");
            CREATE TABLE "DeepMissionConstraint" (
                "Id" serial PRIMARY KEY,
                "WaypointId" integer NOT NULL,
                "Sequence" integer NOT NULL,
                "Rule" varchar(80) NOT NULL,
                "Minimum" numeric(18,3) NOT NULL,
                "Maximum" numeric(18,3) NULL,
                "Enabled" boolean NOT NULL,
                "Reason" text NULL,
                CONSTRAINT "FK_DeepMissionConstraint_DeepMissionWaypoint" FOREIGN KEY ("WaypointId") REFERENCES "DeepMissionWaypoint" ("Id") ON DELETE CASCADE
            );
            CREATE INDEX "IX_DeepMissionConstraint_WaypointId" ON "DeepMissionConstraint" ("WaypointId");
            CREATE TABLE "DeepMissionCrew" (
                "Id" serial PRIMARY KEY,
                "MissionId" integer NOT NULL,
                "Role" varchar(40) NOT NULL,
                "Name" varchar(120) NOT NULL,
                "Email" varchar(160) NULL,
                "Hours" numeric(8,2) NOT NULL,
                CONSTRAINT "FK_DeepMissionCrew_DeepMission" FOREIGN KEY ("MissionId") REFERENCES "DeepMission" ("Id") ON DELETE CASCADE
            );
            CREATE INDEX "IX_DeepMissionCrew_MissionId" ON "DeepMissionCrew" ("MissionId");
            """);
        await AirwayScenarios.CreateSchemaAsync(context);
        await context.Database.CloseConnectionAsync();
        return "Tables created";
    });

    await Run("BulkInsert with generated identities", async () =>
    {
        var rows = CreateItems(20, "insert");
        await context.BulkInsertAsync(rows, new BulkConfig { SetOutputIdentity = true });
        if (rows.Any(x => x.Id <= 0)) throw new InvalidOperationException("Identity output was not populated");
        if (await context.Items.CountAsync() != 20) throw new InvalidOperationException("Inserted row count mismatch");
        return $"20 rows, IDs {rows.Min(x => x.Id)}..{rows.Max(x => x.Id)}";
    });

    await Run("BulkRead with nullable matching key", async () =>
    {
        var request = new List<BulkPackageItem> { new() { Description = null } };
        await context.BulkReadAsync(request, new BulkConfig { UpdateByProperties = [nameof(BulkPackageItem.Description)] });
        if (request[0].Name != "insert-1") throw new InvalidOperationException($"Read returned {request[0].Name}");
        return request[0].Name;
    });

    await Run("BulkUpdate", async () =>
    {
        var rows = await context.Items.OrderBy(x => x.Id).Take(5).ToListAsync();
        foreach (var row in rows) row.Amount += 10.50m;
        await context.BulkUpdateAsync(rows);
        var maxUpdatedId = rows.Max(x => x.Id);
        var persisted = await context.Items.Where(x => x.Id <= maxUpdatedId).ToListAsync();
        if (persisted.Any(x => x.Amount < 10.50m)) throw new InvalidOperationException("Updated amounts were not persisted");
        return $"Updated {rows.Count} rows";
    });

    await Run("BulkInsertOrUpdate by unique natural key", async () =>
    {
        await context.BulkInsertAsync(new List<BulkPackageNatural> { new() { Code = "N-001", Name = "old", Version = 1 } });
        await context.BulkInsertOrUpdateAsync(
            new List<BulkPackageNatural> { new() { Code = "N-001", Name = "new", Version = 2 } },
            new BulkConfig
            {
                UpdateByProperties = [nameof(BulkPackageNatural.Code)],
                PropertiesToExcludeOnUpdate = [nameof(BulkPackageNatural.Version)]
            });
        var value = await context.Natural.SingleAsync(x => x.Code == "N-001");
        if (value.Name != "new" || value.Version != 1) throw new InvalidOperationException("Natural-key upsert mismatch");
        return $"Name={value.Name}, Version={value.Version}";
    });

    await Run("BulkDelete", async () =>
    {
        var rows = await context.Items.Where(x => x.Name.StartsWith("insert-")).Take(3).ToListAsync();
        await context.BulkDeleteAsync(rows);
        if (await context.Items.CountAsync() != 17) throw new InvalidOperationException("Delete count mismatch");
        return $"Deleted {rows.Count} rows";
    });

    await Run("Composite-key bulk insert", async () =>
    {
        var rows = new List<BulkPackageComposite>
        {
            new() { UserId = 1, RoleId = 1, Description = "admin" },
            new() { UserId = 1, RoleId = 2, Description = "reader" }
        };
        await context.BulkInsertAsync(rows);
        if (await context.Composite.CountAsync() != 2) throw new InvalidOperationException("Composite insert mismatch");
        return "2 composite-key rows";
    });

    await Run("External transaction rollback", async () =>
    {
        await using var transaction = await context.Database.BeginTransactionAsync();
        var rows = CreateItems(2, "rollback");
        await context.BulkInsertAsync(rows);
        await transaction.RollbackAsync();
        if (await context.Items.AnyAsync(x => x.Name.StartsWith("rollback-")))
            throw new InvalidOperationException("Rolled-back rows are visible");
        return "Rollback preserved";
    });

    await Run("Async cancellation does not leave staging tables", async () =>
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancellationRows = await context.Items.Take(2).ToListAsync();
        await AssertThrows<OperationCanceledException>(() =>
            context.BulkUpdateAsync(cancellationRows, cancellationToken: cancellation.Token));
        await using var stagingCommand = context.Database.GetDbConnection().CreateCommand();
        stagingCommand.CommandText = "SELECT COUNT(*) FROM pg_tables WHERE schemaname = 'public' AND tablename LIKE 'BulkPackageItemsTemp%'";
        if (stagingCommand.Connection!.State != ConnectionState.Open) await stagingCommand.Connection.OpenAsync();
        var stagingCount = Convert.ToInt64(await stagingCommand.ExecuteScalarAsync());
        if (stagingCount != 0) throw new InvalidOperationException($"Found {stagingCount} staging tables");
        return "Cancellation handled";
    });

    await Run("Complex Flight aggregate bulk operations by entity set", async () =>
    {
        var flights = CreateFlights(3);
        await context.BulkInsertAsync(flights, new BulkConfig { SetOutputIdentity = true });
        if (flights.Any(x => x.Id <= 0)) throw new InvalidOperationException("Flight identities were not populated");

        // BulkExtensions operates on each entity set; persist the nested collection explicitly.
        var depInfos = flights.Select((flight, index) => new BulkFlightDepInfo
        {
            Id = flight.Id,
            FlightId = flight.Id,
            TOBT = flight.Plan.EOBT?.AddMinutes(-30),
            DepStand = $"S{index + 1:00}"
        }).ToList();
        var labels = flights.SelectMany((flight, index) => Enumerable.Range(1, 3).Select(rank => new BulkFlightLabel
        {
            FlightId = flight.Id,
            LabelDataId = index * 100 + rank,
            Name = $"L{index + 1}-{rank}",
            Rank = rank
        })).ToList();
        await context.BulkInsertAsync(depInfos);
        await context.BulkInsertAsync(labels, new BulkConfig { SetOutputIdentity = true });
        var notes = labels.SelectMany(label => new[]
        {
            new BulkFlightLabelNote { LabelId = label.Id, Text = $"note-{label.LabelDataId}-1", CreatedAt = DateTime.UtcNow },
            new BulkFlightLabelNote { LabelId = label.Id, Text = $"note-{label.LabelDataId}-2", CreatedAt = DateTime.UtcNow }
        }).ToList();
        await context.BulkInsertAsync(notes, new BulkConfig { SetOutputIdentity = true });

        List<BulkFlight> loaded;
        try
        {
            loaded = await context.Flights
                .Include(x => x.DepInfo)
                .Include(x => x.Labels).ThenInclude(x => x.Notes)
                .OrderBy(x => x.Id)
                .ToListAsync();
        }
        catch (InvalidOperationException) { throw; }
        if (loaded.Count != 3 || loaded.Any(x => x.DepInfo is null || x.Labels.Count != 3 || x.Labels.Any(label => label.Notes.Count != 2)))
            throw new InvalidOperationException("Nested Flight graph was not materialized");

        // Update both aggregate scalar/owned data and child rows in bulk.
        loaded[0].State = "ARR";
        loaded[0].Route.AirwayCodes = "A1/B2/C3";
        loaded[0].DepInfo!.DepStand = "UPDATED";
        loaded[0].Labels[0].Name = "UPDATED-LABEL";
        loaded[0].Labels[0].Notes[0].Text = "UPDATED-NOTE";
        await context.BulkUpdateAsync([loaded[0]]);
        await context.BulkUpdateAsync([loaded[0].DepInfo!]);
        await context.BulkUpdateAsync([loaded[0].Labels[0]]);
        await context.BulkUpdateAsync([loaded[0].Labels[0].Notes[0]]);

        var verify = await context.Flights.AsNoTracking().SingleAsync(x => x.Id == loaded[0].Id);
        var verifyDep = await context.FlightDepInfos.AsNoTracking().SingleAsync(x => x.FlightId == loaded[0].Id);
        var verifyLabel = await context.FlightLabels.AsNoTracking().SingleAsync(x => x.Id == loaded[0].Labels[0].Id);
        var verifyNote = await context.FlightLabelNotes.AsNoTracking().SingleAsync(x => x.Id == loaded[0].Labels[0].Notes[0].Id);
        if (verify.State != "ARR" || verify.Route.AirwayCodes != "A1/B2/C3" || verifyDep.DepStand != "UPDATED" || verifyLabel.Name != "UPDATED-LABEL" || verifyNote.Text != "UPDATED-NOTE")
            throw new InvalidOperationException("Nested Flight updates were not persisted");

        // Natural key upsert for labels demonstrates child-level batch synchronization.
        var upsertLabels = labels.Take(2).Select(x => new BulkFlightLabel
        {
            FlightId = x.FlightId,
            LabelDataId = x.LabelDataId,
            Name = "UPSERTED",
            Rank = x.Rank + 10
        }).ToList();
        await context.BulkInsertOrUpdateAsync(upsertLabels, new BulkConfig
        {
            UpdateByProperties = [nameof(BulkFlightLabel.FlightId), nameof(BulkFlightLabel.LabelDataId)]
        });
        var upserted = await context.FlightLabels.CountAsync(x => x.Name == "UPSERTED");
        if (upserted != 2) throw new InvalidOperationException("Nested label upsert mismatch");

        // Batch delete child rows followed by the aggregate verifies FK-safe cleanup.
        var deleteFlight = flights[2];
        var childRows = await context.FlightLabels.Where(x => x.FlightId == deleteFlight.Id).ToListAsync();
        await context.BulkDeleteAsync(childRows);
        await context.BulkDeleteAsync([deleteFlight]);
        if (await context.Flights.CountAsync() != 2 || await context.FlightLabels.AnyAsync(x => x.FlightId == deleteFlight.Id) || await context.FlightLabelNotes.AnyAsync(x => childRows.Select(label => label.Id).Contains(x.LabelId)))
            throw new InvalidOperationException("Nested Flight delete mismatch");
        return $"3 flights, {labels.Count} labels, {notes.Count} nested notes; CRUD and child upsert passed";
    });

    await Run("Airway-like reference data ordinary CRUD", () => AirwayScenarios.RunCrudAsync(context));
    await Run("Airway-like reference data bulk CRUD", () => AirwayScenarios.RunBulkCrudAsync(context));

    await Run("Deep complex aggregate EF CRUD", () => DeepMissionScenarios.RunCrudAsync(context));
    await Run("Deep complex aggregate nested bulk CRUD", () => DeepMissionScenarios.RunBulkCrudAsync(context));

    // Large-volume benchmark. Each size is isolated by a unique name prefix and
    // cleaned up after the insert/update/delete cycle so the following size starts
    // with the same database state. Set GAUSSDB_BULK_VOLUME_COUNTS (for example,
    // "10000,20000,50000,100000") to override the defaults. A single Bulk* call is
    // used for each operation; GAUSSDB_BULK_BATCH_SIZE can optionally set the
    // provider's internal BatchSize while preserving one API invocation.
    var volumeCounts = ParseVolumeCounts(Environment.GetEnvironmentVariable("GAUSSDB_BULK_VOLUME_COUNTS"));
    var configuredBatchSize = ParsePositiveInt(Environment.GetEnvironmentVariable("GAUSSDB_BULK_BATCH_SIZE"));
    foreach (var volumeCount in volumeCounts)
    {
        var batchLabel = configuredBatchSize is null ? "provider-default" : $"batch={configuredBatchSize.Value}";
        await Run($"Large-volume BulkInsert/BulkUpdate/BulkDelete ({volumeCount:n0} rows)",
            () => RunLargeVolumeScenarioAsync(context, volumeCount, configuredBatchSize, batchLabel));
        await Run($"Large-volume DeepMission graph bulk CRUD ({volumeCount:n0} roots)",
            () => GraphVolumeScenarios.RunAsync(context, volumeCount, configuredBatchSize));
    }
}
finally
{
    try
    {
        await context.DisposeAsync();
        GaussDBConnection.ClearAllPools();
        await using var admin = new GaussDBConnection(adminConnectionString);
        await admin.OpenAsync();
        await using var drop = admin.CreateCommand();
        drop.CommandText = $"DROP DATABASE \"{testDatabase}\"";
        await drop.ExecuteNonQueryAsync();
        Console.WriteLine($"Cleaned up isolated database: {testDatabase}");
    }
    catch (Exception ex)
    {
        results.Add(("Cleanup isolated database", "FAIL", ex.ToString()));
        Console.Error.WriteLine($"Cleanup failed: {ex.Message}");
    }
}

Console.WriteLine();
Console.WriteLine("=== Package integration result ===");
foreach (var result in results)
    Console.WriteLine($"{result.Status,-10} {result.Name}: {result.Detail}");

var failed = results.Count(x => x.Status == "FAIL");
Console.WriteLine($"Passed: {results.Count(x => x.Status == "PASS")}; Failed: {failed}; Limitations: 0");
var finished = DateTimeOffset.Now;
Console.WriteLine($"Finished: {finished:O}");
var resultsDirectory = Environment.GetEnvironmentVariable("GAUSSDB_RESULTS_DIRECTORY") ?? AppContext.BaseDirectory;
Directory.CreateDirectory(resultsDirectory);
await File.WriteAllTextAsync(Path.Combine(resultsDirectory, "results.json"), JsonSerializer.Serialize(new
{
    started,
    finished,
    runtime = Environment.Version.ToString(),
    database = testDatabase,
    passed = results.Count(x => x.Status == "PASS"),
    failed,
    limitations = 0,
    scenarios = results.Select(x => new { x.Name, x.Status, x.Detail })
}, new JsonSerializerOptions { WriteIndented = true }));
return failed == 0 ? 0 : 1;

async Task Run(string name, Func<Task<string>> action)
{
    context.ChangeTracker.Clear();
    Console.WriteLine($"RUN {DateTimeOffset.Now:O} {name}");
    try
    {
        var detail = await action();
        results.Add((name, "PASS", detail));
    }
    catch (Exception ex)
    {
        results.Add((name, "FAIL", ex.ToString()));
    }
    Console.WriteLine($"{results[^1].Status}: {name}: {results[^1].Detail}");
}

static int[] ParseVolumeCounts(string? value)
{
    if (string.IsNullOrWhiteSpace(value)) return [10_000, 20_000, 50_000, 100_000];
    var values = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(x => int.TryParse(x, out var n) ? n : 0).Where(x => x > 0).Distinct().ToArray();
    return values.Length == 0 ? [10_000, 20_000, 50_000, 100_000] : values;
}

static int? ParsePositiveInt(string? value) => int.TryParse(value, out var n) && n > 0 ? n : null;

static async Task<string> RunLargeVolumeScenarioAsync(BulkPackageDbContext context, int count, int? batchSize, string batchLabel)
{
    var prefix = $"volume-{count}-{Guid.NewGuid():N}";
    var config = batchSize is > 0 ? new BulkConfig { BatchSize = batchSize.Value } : null;
    var rows = CreateItems(count, prefix);
    var sw = System.Diagnostics.Stopwatch.StartNew();
    try
    {
        await context.BulkInsertAsync(rows, config); sw.Stop(); var insertMs = sw.ElapsedMilliseconds;
        context.ChangeTracker.Clear(); rows.Clear();
        var loaded = await context.Items.AsNoTracking().Where(x => x.Name.StartsWith(prefix + "-")).ToListAsync();
        if (loaded.Count != count) throw new InvalidOperationException($"Insert count mismatch: {loaded.Count}/{count}");
        foreach (var row in loaded) { row.Status = "VolumeUpdated"; row.Amount += 1m; row.UpdatedAt = DateTime.UtcNow; }
        sw.Restart(); await context.BulkUpdateAsync(loaded, config); sw.Stop(); var updateMs = sw.ElapsedMilliseconds; context.ChangeTracker.Clear();
        var updated = await context.Items.AsNoTracking().CountAsync(x => x.Name.StartsWith(prefix + "-") && x.Status == "VolumeUpdated" && x.Amount == x.Quantity * 12.34m + 1m);
        if (updated != count) throw new InvalidOperationException($"Update count mismatch: {updated}/{count}");
        sw.Restart(); await context.BulkDeleteAsync(loaded, config); sw.Stop(); var deleteMs = sw.ElapsedMilliseconds; context.ChangeTracker.Clear();
        var remaining = await context.Items.CountAsync(x => x.Name.StartsWith(prefix + "-"));
        if (remaining != 0) throw new InvalidOperationException($"Delete count mismatch: {remaining}");
        static double Rate(int n, long ms) => ms <= 0 ? n : n * 1000d / ms;
        return $"{count:n0} rows; insert={insertMs} ms ({Rate(count, insertMs):n0} rows/s), update={updateMs} ms ({Rate(count, updateMs):n0} rows/s), delete={deleteMs} ms ({Rate(count, deleteMs):n0} rows/s) ({batchLabel})";
    }
    finally
    {
        try { await context.Database.ExecuteSqlRawAsync("DELETE FROM \"BulkPackageItems\" WHERE \"Name\" LIKE {0}", prefix + "-%"); } catch { }
        context.ChangeTracker.Clear(); rows.Clear(); GC.Collect();
    }
}

static async Task RecreateDatabaseAsync(string adminConnectionString, string databaseName)
{
    GaussDBConnection.ClearAllPools();
    await using var connection = new GaussDBConnection(adminConnectionString);
    await connection.OpenAsync();

    await using (var drop = connection.CreateCommand())
    {
        drop.CommandText = $"DROP DATABASE IF EXISTS \"{databaseName}\"";
        await drop.ExecuteNonQueryAsync();
    }

    await using (var create = connection.CreateCommand())
    {
        create.CommandText = $"CREATE DATABASE \"{databaseName}\"";
        await create.ExecuteNonQueryAsync();
    }
}

static async Task AssertThrows<TException>(Func<Task> action) where TException : Exception
{
    try
    {
        await action();
    }
    catch (TException)
    {
        return;
    }
    throw new InvalidOperationException($"Expected {typeof(TException).Name}");
}

static List<BulkPackageItem> CreateItems(int count, string prefix) => Enumerable.Range(1, count)
    .Select(i => new BulkPackageItem
    {
        Name = $"{prefix}-{i}",
        Description = i == 1 ? null : prefix,
        Quantity = i,
        Amount = i * 12.34m,
        Status = i % 2 == 0 ? "Inactive" : "Active",
        UpdatedAt = DateTime.UtcNow,
        Tags = [prefix, $"tag-{i}"]
    })
    .ToList();

static List<BulkFlight> CreateFlights(int count) => Enumerable.Range(1, count)
    .Select(i => new BulkFlight
    {
        FlightNumber = $"CCA{i:000}", State = "DEP", CreateTime = DateTime.UtcNow,
        Aircraft = new BulkFlightAircraft { Code = $"A{i}", Type = "A320" },
        Route = new BulkFlightRoute { FiledRoute = $"FIX{i} DCT FIX{i + 1}", AirwayCodes = "A1/B2" },
        Plan = new BulkFlightPlan { ADEP = "ZBAA", ADES = "ZSSS", EOBT = DateTime.UtcNow.AddHours(i) }
    }).ToList();

public sealed class BulkPackageDbContext(string connectionString) : DbContext
{
    private readonly string _connectionString = connectionString;
    public DbSet<BulkPackageItem> Items => Set<BulkPackageItem>();
    public DbSet<BulkPackageNatural> Natural => Set<BulkPackageNatural>();
    public DbSet<BulkPackageComposite> Composite => Set<BulkPackageComposite>();
    public DbSet<BulkFlight> Flights => Set<BulkFlight>();
    public DbSet<BulkFlightDepInfo> FlightDepInfos => Set<BulkFlightDepInfo>();
    public DbSet<BulkFlightLabel> FlightLabels => Set<BulkFlightLabel>();
    public DbSet<BulkFlightLabelNote> FlightLabelNotes => Set<BulkFlightLabelNote>();
    public DbSet<DeepMission> DeepMissions => Set<DeepMission>();
    public DbSet<DeepMissionSegment> DeepMissionSegments => Set<DeepMissionSegment>();
    public DbSet<DeepMissionWaypoint> DeepMissionWaypoints => Set<DeepMissionWaypoint>();
    public DbSet<DeepMissionCrew> DeepMissionCrew => Set<DeepMissionCrew>();
    public DbSet<DeepMissionConstraint> DeepMissionConstraints => Set<DeepMissionConstraint>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) => optionsBuilder.UseGaussDB(_connectionString);

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        AirwayScenarios.Configure(modelBuilder);
        modelBuilder.Entity<BulkPackageItem>(entity =>
        {
            entity.ToTable("BulkPackageItems");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(120).IsRequired();
            entity.Property(x => x.Description).HasColumnType("text");
            entity.Property(x => x.Amount).HasColumnType("numeric(12,2)");
            entity.Property(x => x.Status).HasMaxLength(20).IsRequired();
            entity.Property(x => x.UpdatedAt).HasColumnType("timestamp with time zone");
            entity.Property(x => x.Tags).HasColumnType("text[]");
        });
        modelBuilder.Entity<BulkPackageNatural>(entity =>
        {
            entity.ToTable("BulkPackageNatural");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.Code).IsUnique();
        });
        modelBuilder.Entity<BulkPackageComposite>(entity =>
        {
            entity.ToTable("BulkPackageComposite");
            entity.HasKey(x => new { x.UserId, x.RoleId });
        });
        modelBuilder.Entity<BulkFlight>(entity =>
        {
            entity.ToTable("BulkFlight");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.FlightNumber).HasMaxLength(32).IsRequired();
            entity.Property(x => x.State).HasMaxLength(8).IsRequired();
            entity.OwnsOne(x => x.Aircraft, owned =>
            {
                owned.Property(x => x.Code).HasColumnName("Aircraft_Code").HasMaxLength(16);
                owned.Property(x => x.Type).HasColumnName("Aircraft_Type").HasMaxLength(64);
            });
            entity.OwnsOne(x => x.Route, owned =>
            {
                owned.Property(x => x.FiledRoute).HasColumnName("Route_FiledRoute").HasColumnType("text");
                owned.Property(x => x.AirwayCodes).HasColumnName("Route_AirwayCodes").HasColumnType("text");
            });
            entity.OwnsOne(x => x.Plan, owned =>
            {
                owned.Property(x => x.ADEP).HasColumnName("Plan_ADEP").HasMaxLength(4);
                owned.Property(x => x.ADES).HasColumnName("Plan_ADES").HasMaxLength(4);
                owned.Property(x => x.EOBT).HasColumnName("Plan_EOBT").HasColumnType("timestamp with time zone");
            });
            entity.HasOne(x => x.DepInfo).WithOne(x => x.Flight).HasForeignKey<BulkFlightDepInfo>(x => x.FlightId);
            entity.HasMany(x => x.Labels).WithOne(x => x.Flight).HasForeignKey(x => x.FlightId);
        });
        modelBuilder.Entity<BulkFlightDepInfo>(entity =>
        {
            entity.ToTable("BulkFlightDepInfo");
            entity.HasKey(x => x.Id);
            // The child shares a client-assigned root key; its table has no identity default.
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.Property(x => x.DepStand).HasMaxLength(8);
            entity.Property(x => x.TOBT).HasColumnType("timestamp with time zone");
        });
        modelBuilder.Entity<BulkFlightLabel>(entity =>
        {
            entity.ToTable("BulkFlightLabel");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(32).IsRequired();
            entity.HasIndex(x => new { x.FlightId, x.LabelDataId }).IsUnique();
            entity.HasMany(x => x.Notes).WithOne(x => x.Label).HasForeignKey(x => x.LabelId);
        });
        modelBuilder.Entity<BulkFlightLabelNote>(entity =>
        {
            entity.ToTable("BulkFlightLabelNote");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Text).HasMaxLength(160).IsRequired();
            entity.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone");
        });
        modelBuilder.Entity<DeepMission>(entity => { entity.ToTable("DeepMission"); entity.HasKey(x => x.Id); entity.HasIndex(x => x.Code).IsUnique(); entity.Property(x => x.Budget).HasColumnType("numeric(18,2)"); entity.Property(x => x.Tags).HasColumnType("text[]"); entity.Property(x => x.Metadata).HasColumnType("jsonb"); entity.HasMany(x => x.Segments).WithOne(x => x.Mission).HasForeignKey(x => x.MissionId); entity.HasMany(x => x.Crew).WithOne(x => x.Mission).HasForeignKey(x => x.MissionId); });
        modelBuilder.Entity<DeepMissionSegment>(entity => { entity.ToTable("DeepMissionSegment"); entity.HasKey(x => x.Id); entity.HasIndex(x => new { x.MissionId, x.Sequence }).IsUnique(); entity.Property(x => x.Distance).HasColumnType("numeric(12,3)"); entity.HasMany(x => x.Waypoints).WithOne(x => x.Segment).HasForeignKey(x => x.SegmentId); });
        modelBuilder.Entity<DeepMissionWaypoint>(entity => { entity.ToTable("DeepMissionWaypoint"); entity.HasKey(x => x.Id); entity.HasIndex(x => new { x.SegmentId, x.Sequence }).IsUnique(); entity.Property(x => x.Latitude).HasColumnType("numeric(10,6)"); entity.Property(x => x.Longitude).HasColumnType("numeric(10,6)"); entity.HasMany(x => x.Constraints).WithOne(x => x.Waypoint).HasForeignKey(x => x.WaypointId); });
        modelBuilder.Entity<DeepMissionCrew>(entity => { entity.ToTable("DeepMissionCrew"); entity.HasKey(x => x.Id); entity.HasIndex(x => x.MissionId); entity.Property(x => x.Hours).HasColumnType("numeric(8,2)"); });
        modelBuilder.Entity<DeepMissionConstraint>(entity => { entity.ToTable("DeepMissionConstraint"); entity.HasKey(x => x.Id); entity.Property(x => x.Minimum).HasColumnType("numeric(18,3)"); entity.Property(x => x.Maximum).HasColumnType("numeric(18,3)"); });
        modelBuilder.Entity<DeepMissionConstraint>(entity => entity.HasIndex(x => x.WaypointId));
    }
}

public sealed class BulkPackageItem
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public int Quantity { get; set; }
    public decimal Amount { get; set; }
    public string Status { get; set; } = "";
    public DateTime UpdatedAt { get; set; }
    public string[]? Tags { get; set; }
}

public sealed class BulkPackageNatural
{
    public int Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public int Version { get; set; }
}

public sealed class BulkPackageComposite
{
    public int UserId { get; set; }
    public int RoleId { get; set; }
    public string Description { get; set; } = "";
}

public sealed class BulkFlight
{
    public int Id { get; set; }
    public string FlightNumber { get; set; } = "";
    public string State { get; set; } = "";
    public DateTime CreateTime { get; set; }
    public BulkFlightAircraft Aircraft { get; set; } = new();
    public BulkFlightRoute Route { get; set; } = new();
    public BulkFlightPlan Plan { get; set; } = new();
    public BulkFlightDepInfo? DepInfo { get; set; }
    public List<BulkFlightLabel> Labels { get; set; } = [];
}

[Owned]
public sealed class BulkFlightAircraft
{
    public string? Code { get; set; }
    public string? Type { get; set; }
}

[Owned]
public sealed class BulkFlightRoute
{
    public string? FiledRoute { get; set; }
    public string? AirwayCodes { get; set; }
}

[Owned]
public sealed class BulkFlightPlan
{
    public string? ADEP { get; set; }
    public string? ADES { get; set; }
    public DateTime? EOBT { get; set; }
}

public sealed class BulkFlightDepInfo
{
    public int Id { get; set; }
    public int FlightId { get; set; }
    public DateTime? TOBT { get; set; }
    public string? DepStand { get; set; }
    public BulkFlight Flight { get; set; } = null!;
}

public sealed class BulkFlightLabel
{
    public int Id { get; set; }
    public int FlightId { get; set; }
    public long LabelDataId { get; set; }
    public string Name { get; set; } = "";
    public int Rank { get; set; }
    public BulkFlight Flight { get; set; } = null!;
    public List<BulkFlightLabelNote> Notes { get; set; } = [];
}

public sealed class BulkFlightLabelNote
{
    public int Id { get; set; }
    public int LabelId { get; set; }
    public string Text { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public BulkFlightLabel Label { get; set; } = null!;
}

public sealed class DeepMission
{
    public int Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Status { get; set; } = "";
    public int Priority { get; set; }
    public int Version { get; set; }
    public string Owner { get; set; } = "";
    public string Region { get; set; } = "";
    public string? Description { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public decimal Budget { get; set; }
    public bool IsActive { get; set; }
    public double Score { get; set; }
    public string[]? Tags { get; set; }
    public string? Metadata { get; set; }
    public Guid ExternalId { get; set; }
    public string AircraftRegistration { get; set; } = "";
    public string AircraftType { get; set; } = "";
    public string OperatorCode { get; set; } = "";
    public string Departure { get; set; } = "";
    public string Arrival { get; set; } = "";
    public string? Alternate { get; set; }
    public string Callsign { get; set; } = "";
    public string FlightRules { get; set; } = "";
    public string ServiceType { get; set; } = "";
    public decimal PlannedDistance { get; set; }
    public decimal Fuel { get; set; }
    public decimal Payload { get; set; }
    public int PassengerCount { get; set; }
    public int CruiseAltitude { get; set; }
    public int CruiseSpeed { get; set; }
    public bool Emergency { get; set; }
    public bool Approved { get; set; }
    public DateTime? ApprovalTime { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string? Remarks { get; set; }
    public string RevisionHash { get; set; } = "";
    public bool? SnapshotReceived { get; set; }
    public List<DeepMissionSegment> Segments { get; set; } = [];
    public List<DeepMissionCrew> Crew { get; set; } = [];
}
public sealed class DeepMissionSegment
{
    public int Id { get; set; }
    public int MissionId { get; set; }
    public int Sequence { get; set; }
    public string Code { get; set; } = "";
    public string Title { get; set; } = "";
    public string Mode { get; set; } = "";
    public decimal Distance { get; set; }
    public int DurationMinutes { get; set; }
    public DateTime? StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public DeepMission Mission { get; set; } = null!;
    public List<DeepMissionWaypoint> Waypoints { get; set; } = [];
}
public sealed class DeepMissionWaypoint
{
    public int Id { get; set; }
    public int SegmentId { get; set; }
    public int Sequence { get; set; }
    public string Code { get; set; } = "";
    public decimal Latitude { get; set; }
    public decimal Longitude { get; set; }
    public int Altitude { get; set; }
    public string Action { get; set; } = "";
    public DeepMissionSegment Segment { get; set; } = null!;
    public List<DeepMissionConstraint> Constraints { get; set; } = [];
}
public sealed class DeepMissionCrew
{
    public int Id { get; set; }
    public int MissionId { get; set; }
    public string Role { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Email { get; set; }
    public decimal Hours { get; set; }
    public DeepMission Mission { get; set; } = null!;
}

public sealed class DeepMissionConstraint
{
    public int Id { get; set; }
    public int WaypointId { get; set; }
    public int Sequence { get; set; }
    public string Rule { get; set; } = "";
    public decimal Minimum { get; set; }
    public decimal? Maximum { get; set; }
    public bool Enabled { get; set; }
    public string? Reason { get; set; }
    public DeepMissionWaypoint Waypoint { get; set; } = null!;
}
