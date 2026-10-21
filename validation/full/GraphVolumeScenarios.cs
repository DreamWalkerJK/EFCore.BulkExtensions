using System.Diagnostics;
using EFCore.BulkExtensions;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Large-volume tests for the complete DeepMission graph. Each root has one
/// segment, one waypoint, one constraint and one crew row (five rows per graph).
/// The scenario intentionally issues one BulkExtensions call per entity type and
/// does not split a type into application-level chunks.
/// </summary>
public static class GraphVolumeScenarios
{
    public static async Task<string> RunAsync(BulkPackageDbContext context, int count, int? batchSize = null)
    {
        if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));

        var token = Guid.NewGuid().ToString("N")[..8];
        var codePrefix = $"GV{token}";
        var config = batchSize is > 0 ? new BulkConfig { BatchSize = batchSize.Value } : null;
        var timings = new List<string>();
        var totalRows = count * 5L;
        var baselineCounts = await TableCountsAsync(context);
        var cycle = Stopwatch.StartNew();

        try
        {
            await InsertGraphAsync(context, count, codePrefix, config, timings);
            await ReadAndVerifyGraphAsync(context, count, codePrefix, timings, updated: false);
            await UpdateGraphAsync(context, count, codePrefix, config, timings);
            await ReadAndVerifyGraphAsync(context, count, codePrefix, timings);
            await DeleteGraphAsync(context, count, codePrefix, config, timings);
            var remainingCounts = await TableCountsAsync(context);
            if (!baselineCounts.SequenceEqual(remainingCounts))
                throw new InvalidOperationException($"Graph deletion left rows: baseline={string.Join(',', baselineCounts)} actual={string.Join(',', remainingCounts)}");
            cycle.Stop();
            return $"{count:n0} roots / {totalRows:n0} graph rows ({count:n0} each in root, segment, waypoint, constraint, crew); " + string.Join(", ", timings) + $"; verified all 5 table counts restored; cycle={cycle.ElapsedMilliseconds} ms";
        }
        finally
        {
            // Root FK cascades remove any partially inserted child graph after a failure.
            try
            {
                await context.Database.ExecuteSqlRawAsync(
                    "DELETE FROM \"DeepMission\" WHERE \"Code\" LIKE {0}", codePrefix + "%");
                context.ChangeTracker.Clear();
            }
            catch (Exception cleanupError)
            {
                Console.Error.WriteLine($"Graph volume cleanup failed for {codePrefix}: {cleanupError.Message}");
            }
        }
    }

    private static async Task InsertGraphAsync(BulkPackageDbContext context, int count, string prefix, BulkConfig? config, List<string> timings)
    {
        await using var tx = await context.Database.BeginTransactionAsync();
        var now = DateTime.UtcNow;
        var missions = Enumerable.Range(1, count).Select(i => CreateMission(i, count, prefix, now)).ToList();
        await TimedAsync("insert roots", count, () => context.BulkInsertAsync(missions, Merge(config, setOutputIdentity: true)), timings);

        var segments = missions.Select(m => new DeepMissionSegment
        {
            MissionId = m.Id, Sequence = 1, Code = $"{m.Code}-S", Title = "Primary segment", Mode = "AIR",
            Distance = 12.5m, DurationMinutes = 30, StartTime = m.StartTime, EndTime = m.StartTime.AddMinutes(30)
        }).ToList();
        missions.Clear();
        await TimedAsync("insert segments", count, () => context.BulkInsertAsync(segments, Merge(config, setOutputIdentity: true)), timings);

        var waypoints = segments.Select(s => new DeepMissionWaypoint
        {
            SegmentId = s.Id, Sequence = 1, Code = $"{s.Code}-W", Latitude = 30.123456m,
            Longitude = 110.654321m, Altitude = 12000, Action = "PASS"
        }).ToList();
        segments.Clear();
        await TimedAsync("insert waypoints", count, () => context.BulkInsertAsync(waypoints, Merge(config, setOutputIdentity: true)), timings);

        var constraints = waypoints.Select(w => new DeepMissionConstraint
        {
            WaypointId = w.Id, Sequence = 1, Rule = "ALTITUDE", Minimum = 500m,
            Maximum = 50000m, Enabled = true, Reason = "graph-volume"
        }).ToList();
        await TimedAsync("insert constraints", count, () => context.BulkInsertAsync(constraints, Merge(config, setOutputIdentity: true)), timings);

        // Crew uses the same root FK as segments; recover it through the segment list
        // before releasing the waypoint/constraint payloads.
        var missionIds = await context.DeepMissionSegments.AsNoTracking()
            .Where(s => s.Code.StartsWith(prefix)).Select(s => s.MissionId).ToListAsync();
        var crew = missionIds.Select((missionId, i) => new DeepMissionCrew
        {
            MissionId = missionId, Role = "PIC", Name = $"Crew-{i + 1}", Email = $"gv{i + 1}@example.test", Hours = 8m
        }).ToList();
        waypoints.Clear(); constraints.Clear(); missionIds.Clear();
        await TimedAsync("insert crew", count, () => context.BulkInsertAsync(crew, Merge(config, setOutputIdentity: true)), timings);
        crew.Clear();
        await tx.CommitAsync();
        context.ChangeTracker.Clear();
    }

    private static async Task UpdateGraphAsync(BulkPackageDbContext context, int count, string prefix, BulkConfig? config, List<string> timings)
    {
        await using var tx = await context.Database.BeginTransactionAsync();
        var rootIds = context.DeepMissions.Where(m => m.Code.StartsWith(prefix)).Select(m => m.Id);
        var missions = await context.DeepMissions.Where(m => rootIds.Contains(m.Id)).ToListAsync();
        foreach (var m in missions) { m.Status = "GV-UPDATED"; m.Priority += 1; }
        await TimedAsync("update roots", count, () => context.BulkUpdateAsync(missions, config), timings);
        missions.Clear(); context.ChangeTracker.Clear();

        var segments = await context.DeepMissionSegments.Where(s => rootIds.Contains(s.MissionId)).ToListAsync();
        foreach (var s in segments) s.Distance += 1m;
        await TimedAsync("update segments", count, () => context.BulkUpdateAsync(segments, config), timings);
        segments.Clear(); context.ChangeTracker.Clear();

        var segmentIds = context.DeepMissionSegments.Where(s => rootIds.Contains(s.MissionId)).Select(s => s.Id);
        var waypoints = await context.DeepMissionWaypoints.Where(w => segmentIds.Contains(w.SegmentId)).ToListAsync();
        foreach (var w in waypoints) w.Action = "HOLD";
        await TimedAsync("update waypoints", count, () => context.BulkUpdateAsync(waypoints, config), timings);
        waypoints.Clear(); context.ChangeTracker.Clear();

        var waypointIds = context.DeepMissionWaypoints.Where(w => segmentIds.Contains(w.SegmentId)).Select(w => w.Id);
        var constraints = await context.DeepMissionConstraints.Where(c => waypointIds.Contains(c.WaypointId)).ToListAsync();
        foreach (var c in constraints) c.Enabled = false;
        await TimedAsync("update constraints", count, () => context.BulkUpdateAsync(constraints, config), timings);
        constraints.Clear(); context.ChangeTracker.Clear();

        var crew = await context.DeepMissionCrew.Where(c => rootIds.Contains(c.MissionId)).ToListAsync();
        foreach (var c in crew) c.Hours += 1m;
        await TimedAsync("update crew", count, () => context.BulkUpdateAsync(crew, config), timings);
        crew.Clear(); context.ChangeTracker.Clear();
        await tx.CommitAsync();
    }

    private static async Task ReadAndVerifyGraphAsync(BulkPackageDbContext context, int count, string prefix, List<string> timings, bool updated = true)
    {
        await using var tx = await context.Database.BeginTransactionAsync();
        var rootIds = context.DeepMissions.Where(m => m.Code.StartsWith(prefix)).Select(m => m.Id);
        var segmentIds = context.DeepMissionSegments.Where(s => rootIds.Contains(s.MissionId)).Select(s => s.Id);
        var waypointIds = context.DeepMissionWaypoints.Where(w => segmentIds.Contains(w.SegmentId)).Select(w => w.Id);
        var sw = Stopwatch.StartNew();
        var status = updated ? "GV-UPDATED" : "NEW";
        var priorityOffset = updated ? 999m : 1000m;
        var distance = updated ? 13.5m : 12.5m;
        var action = updated ? "HOLD" : "PASS";
        var enabled = !updated;
        var hours = updated ? 9m : 8m;
        var roots = await context.DeepMissions.AsNoTracking().CountAsync(m => m.Code.StartsWith(prefix) && m.Status == status && m.Priority == m.Budget - priorityOffset);
        var segments = await context.DeepMissionSegments.AsNoTracking().CountAsync(s => rootIds.Contains(s.MissionId) && s.Distance == distance);
        var waypoints = await context.DeepMissionWaypoints.AsNoTracking().CountAsync(w => segmentIds.Contains(w.SegmentId) && w.Action == action);
        var constraints = await context.DeepMissionConstraints.AsNoTracking().CountAsync(c => waypointIds.Contains(c.WaypointId) && c.Enabled == enabled);
        var crew = await context.DeepMissionCrew.AsNoTracking().CountAsync(c => rootIds.Contains(c.MissionId) && c.Hours == hours);
        sw.Stop();
        if (roots != count || segments != count || waypoints != count || constraints != count || crew != count)
            throw new InvalidOperationException($"Graph read verification mismatch: roots={roots}, segments={segments}, waypoints={waypoints}, constraints={constraints}, crew={crew}; expected {count}");
        timings.Add($"{(updated ? "read graph" : "verify inserted graph")}={sw.ElapsedMilliseconds} ms ({RowsPerSecond(count * 5L, sw.ElapsedMilliseconds):n0} rows/s)");
        await tx.CommitAsync();
    }

    private static async Task DeleteGraphAsync(BulkPackageDbContext context, int count, string prefix, BulkConfig? config, List<string> timings)
    {
        await using var tx = await context.Database.BeginTransactionAsync();
        var rootIds = context.DeepMissions.Where(m => m.Code.StartsWith(prefix)).Select(m => m.Id);
        var segmentIds = context.DeepMissionSegments.Where(s => rootIds.Contains(s.MissionId)).Select(s => s.Id);
        var waypointIds = context.DeepMissionWaypoints.Where(w => segmentIds.Contains(w.SegmentId)).Select(w => w.Id);

        var constraints = await context.DeepMissionConstraints.Where(c => waypointIds.Contains(c.WaypointId)).ToListAsync();
        await TimedAsync("delete constraints", count, () => context.BulkDeleteAsync(constraints, config), timings); constraints.Clear(); context.ChangeTracker.Clear();
        var waypoints = await context.DeepMissionWaypoints.Where(w => segmentIds.Contains(w.SegmentId)).ToListAsync();
        await TimedAsync("delete waypoints", count, () => context.BulkDeleteAsync(waypoints, config), timings); waypoints.Clear(); context.ChangeTracker.Clear();
        var segments = await context.DeepMissionSegments.Where(s => rootIds.Contains(s.MissionId)).ToListAsync();
        await TimedAsync("delete segments", count, () => context.BulkDeleteAsync(segments, config), timings); segments.Clear(); context.ChangeTracker.Clear();
        var crew = await context.DeepMissionCrew.Where(c => rootIds.Contains(c.MissionId)).ToListAsync();
        await TimedAsync("delete crew", count, () => context.BulkDeleteAsync(crew, config), timings); crew.Clear(); context.ChangeTracker.Clear();
        var missions = await context.DeepMissions.Where(m => m.Code.StartsWith(prefix)).ToListAsync();
        await TimedAsync("delete roots", count, () => context.BulkDeleteAsync(missions, config), timings); missions.Clear(); context.ChangeTracker.Clear();
        await tx.CommitAsync();
    }

    private static DeepMission CreateMission(int index, int count, string prefix, DateTime now) => new()
    {
        Code = $"{prefix}{index:D6}", Name = $"Graph volume mission {index}", Status = "NEW", Priority = index,
        Version = 1, Owner = "graph-volume", Region = index % 2 == 0 ? "EAST" : "WEST", Description = "large graph",
        StartTime = now, EndTime = now.AddHours(8), Budget = 1000m + index, IsActive = true, Score = 0.75,
        Tags = ["graph-volume", prefix], Metadata = "{\"source\":\"GraphVolume\"}", ExternalId = Guid.NewGuid(),
        AircraftRegistration = $"GV-{index:D6}", AircraftType = "A350", OperatorCode = "GV", Departure = "ZBAA", Arrival = "ZSPD",
        Alternate = "ZSHC", Callsign = $"GV{index:D6}", FlightRules = "IFR", ServiceType = "S", PlannedDistance = 120m,
        Fuel = 800m, Payload = 1500m, PassengerCount = 100, CruiseAltitude = 35000, CruiseSpeed = 480,
        Emergency = false, Approved = true, ApprovalTime = now, CreatedAt = now, UpdatedAt = now,
        Remarks = "graph volume", RevisionHash = $"{prefix}-{index:D6}", SnapshotReceived = true
    };

    private static async Task TimedAsync(string operation, int rows, Func<Task> action, List<string> timings)
    {
        Console.WriteLine($"  BEGIN {rows:n0} {operation}");
        var sw = Stopwatch.StartNew();
        try { await action(); }
        catch (Exception exception)
        {
            throw new InvalidOperationException($"{operation} ({rows:n0} rows) failed after {sw.ElapsedMilliseconds} ms; completed: {string.Join(", ", timings)}", exception);
        }
        sw.Stop();
        timings.Add($"{operation}={sw.ElapsedMilliseconds} ms ({RowsPerSecond(rows, sw.ElapsedMilliseconds):n0} rows/s)");
        Console.WriteLine($"  DONE {rows:n0} {timings[^1]}");
    }

    private static async Task<int[]> TableCountsAsync(BulkPackageDbContext context) =>
    [
        await context.DeepMissions.CountAsync(),
        await context.DeepMissionSegments.CountAsync(),
        await context.DeepMissionWaypoints.CountAsync(),
        await context.DeepMissionConstraints.CountAsync(),
        await context.DeepMissionCrew.CountAsync()
    ];

    private static BulkConfig? Merge(BulkConfig? config, bool setOutputIdentity)
    {
        if (config is null) return setOutputIdentity ? new BulkConfig { SetOutputIdentity = true } : null;
        return new BulkConfig { BatchSize = config.BatchSize, SetOutputIdentity = setOutputIdentity };
    }

    private static double RowsPerSecond(long rows, long milliseconds)
        => milliseconds <= 0 ? rows : rows * 1000d / milliseconds;
}
