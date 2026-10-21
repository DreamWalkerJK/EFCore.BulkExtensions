using EFCore.BulkExtensions;
using Microsoft.EntityFrameworkCore;

internal static class DeepMissionScenarios
{
    public static List<DeepMission> Create(int count, string prefix) => Enumerable.Range(1, count).Select(i =>
    {
        var now = DateTime.UtcNow;
        var mission = new DeepMission { Code = $"{prefix}-{i:000}", Name = $"Mission {prefix} {i}", Status = "NEW", Priority = i, Version = 1, Owner = $"owner-{i}", Region = i % 2 == 0 ? "EAST" : "WEST", Description = $"Long description {i}", StartTime = now.AddHours(i), EndTime = now.AddHours(i + 8), Budget = i * 1000.25m, IsActive = true, Score = i * 0.75, Tags = [prefix, $"tag-{i}"], Metadata = $"{{\"source\":\"{prefix}\",\"index\":{i}}}", ExternalId = Guid.NewGuid(), AircraftRegistration = $"B-{i:0000}", AircraftType = "A350-900", OperatorCode = "CCA", Departure = "ZBAA", Arrival = "ZSPD", Alternate = "ZSHC", Callsign = $"CCA{i:000}", FlightRules = "IFR", ServiceType = "S", PlannedDistance = i * 120.5m, Fuel = i * 800.75m, Payload = i * 1500.4m, PassengerCount = 100 + i, CruiseAltitude = 35000, CruiseSpeed = 480, Emergency = false, Approved = true, ApprovalTime = now, CreatedAt = now, UpdatedAt = now, Remarks = "complex aggregate", RevisionHash = $"rev-{prefix}-{i}", SnapshotReceived = true };
        for (var s = 1; s <= 2; s++) { var segment = new DeepMissionSegment { Sequence = s, Code = $"{mission.Code}-S{s}", Title = $"Segment {s}", Mode = s == 1 ? "AIR" : "GROUND", Distance = s * 12.5m, DurationMinutes = s * 30, StartTime = mission.StartTime.AddMinutes(s), EndTime = mission.StartTime.AddMinutes(s * 30) }; for (var w = 1; w <= 3; w++) { var point = new DeepMissionWaypoint { Sequence = w, Code = $"{segment.Code}-W{w}", Latitude = 20 + i + w / 10m, Longitude = 100 + i + w / 10m, Altitude = 1000 + w * 100, Action = "PASS" }; point.Constraints.Add(new DeepMissionConstraint { Sequence = 1, Rule = "ALTITUDE", Minimum = 500, Maximum = 50000, Enabled = true, Reason = "airspace" }); segment.Waypoints.Add(point); } mission.Segments.Add(segment); }
        for (var c = 1; c <= 3; c++) mission.Crew.Add(new DeepMissionCrew { Role = $"ROLE-{c}", Name = $"Crew {i}-{c}", Email = $"crew{i}_{c}@example.test", Hours = c * 2.5m });
        return mission;
    }).ToList();

    public static async Task<string> RunCrudAsync(BulkPackageDbContext db)
    {
        await ResetRows(db); db.ChangeTracker.Clear();
        var root = Create(1, "EF")[0]; db.Add(root); await db.SaveChangesAsync();
        if (root.Id <= 0 || root.Segments.Any(s => s.Id <= 0) || root.Segments.SelectMany(s => s.Waypoints).Any(w => w.Id <= 0) || root.Segments.SelectMany(s => s.Waypoints).SelectMany(w => w.Constraints).Any(c => c.Id <= 0) || root.Crew.Any(c => c.MissionId != root.Id)) throw new InvalidOperationException("EF graph identity/FK propagation failed");
        db.ChangeTracker.Clear();
        var loaded = await db.DeepMissions.Include(x => x.Segments).ThenInclude(x => x.Waypoints).ThenInclude(x => x.Constraints).Include(x => x.Crew).SingleAsync(x => x.Id == root.Id);
        loaded.Status = "UPDATED"; loaded.Segments[0].Title = "UPDATED-SEGMENT"; loaded.Segments[0].Waypoints[0].Action = "HOLD"; loaded.Segments[0].Waypoints[0].Constraints[0].Reason = "updated"; loaded.Crew[0].Hours += 1; loaded.Budget += 9.99m; loaded.Tags = ["updated", "json"]; loaded.Metadata = "{\"nested\":true}"; await db.SaveChangesAsync();
        db.ChangeTracker.Clear(); var check = await db.DeepMissions.AsNoTracking().Include(x => x.Segments).ThenInclude(x => x.Waypoints).ThenInclude(x => x.Constraints).Include(x => x.Crew).SingleAsync(x => x.Id == root.Id);
        if (check.Status != "UPDATED" || check.Budget != root.Budget + 9.99m || check.Segments[0].Waypoints[0].Constraints[0].Reason != "updated" || check.Tags is null || System.Text.Json.JsonDocument.Parse(check.Metadata ?? "{}").RootElement.GetProperty("nested").GetBoolean() != true) throw new InvalidOperationException($"EF graph update/read verification failed: status={check.Status}, budget={check.Budget}/{root.Budget}, reason={check.Segments[0].Waypoints[0].Constraints[0].Reason}, tags={string.Join(',', check.Tags ?? [])}, metadata={check.Metadata}");
        db.ChangeTracker.Clear(); var remove = await db.DeepMissions.SingleAsync(x => x.Id == root.Id); db.Remove(remove); await db.SaveChangesAsync();
        if (await db.DeepMissions.AnyAsync(x => x.Id == root.Id) || await db.DeepMissionConstraints.AnyAsync()) throw new InvalidOperationException("EF cascade delete failed");
        return "ordinary EF CRUD passed for wide 4-level graph";
    }

    public static async Task<string> RunBulkCrudAsync(BulkPackageDbContext db)
    {
        await ResetRows(db); db.ChangeTracker.Clear();
        var roots = Create(4, "BULK"); await db.BulkInsertAsync(roots, new BulkConfig { SetOutputIdentity = true });
        var segments = roots.SelectMany(r => r.Segments).ToList(); foreach (var r in roots) foreach (var s in r.Segments) s.MissionId = r.Id; await db.BulkInsertAsync(segments, new BulkConfig { SetOutputIdentity = true });
        var points = segments.SelectMany(s => s.Waypoints).ToList(); foreach (var s in segments) foreach (var p in s.Waypoints) p.SegmentId = s.Id; await db.BulkInsertAsync(points, new BulkConfig { SetOutputIdentity = true });
        var constraints = points.SelectMany(p => p.Constraints).ToList(); foreach (var p in points) foreach (var c in p.Constraints) c.WaypointId = p.Id; await db.BulkInsertAsync(constraints, new BulkConfig { SetOutputIdentity = true });
        var crew = roots.SelectMany(r => r.Crew).ToList(); foreach (var r in roots) foreach (var c in r.Crew) c.MissionId = r.Id; await db.BulkInsertAsync(crew, new BulkConfig { SetOutputIdentity = true });
        foreach (var r in roots) { r.Status = "BULK-UPDATED"; r.Priority += 100; r.Metadata = "{\"bulk\":true}"; } foreach (var s in segments) s.Distance += 1; foreach (var p in points) p.Action = "UPDATED"; foreach (var c in constraints) c.Enabled = false; foreach (var c in crew) c.Hours += 2;
        await db.BulkUpdateAsync(roots); await db.BulkUpdateAsync(segments); await db.BulkUpdateAsync(points); await db.BulkUpdateAsync(constraints); await db.BulkUpdateAsync(crew);
        db.ChangeTracker.Clear(); var verified = await db.DeepMissions.AsNoTracking().Include(x => x.Segments).ThenInclude(x => x.Waypoints).ThenInclude(x => x.Constraints).Include(x => x.Crew).Where(x => x.Code.StartsWith("BULK-")).ToListAsync();
        if (verified.Count != 4 || verified.Any(r => r.Status != "BULK-UPDATED" || System.Text.Json.JsonDocument.Parse(r.Metadata ?? "{}").RootElement.GetProperty("bulk").GetBoolean() != true || r.Segments.Count != 2 || r.Segments.Any(s => s.Waypoints.Count != 3 || s.Waypoints.Any(p => p.Action != "UPDATED" || p.Constraints.Any(c => c.Enabled))) || r.Crew.Any(c => c.Hours <= 2.5m))) throw new InvalidOperationException($"Nested bulk update/read verification failed: roots={verified.Count}, statuses={string.Join(',', verified.Select(r => r.Status))}, metadata={string.Join('|', verified.Select(r => r.Metadata))}");
        var read = roots.Select(r => new DeepMission { Code = r.Code }).ToList(); await db.BulkReadAsync(read, new BulkConfig { UpdateByProperties = [nameof(DeepMission.Code)] }); if (read.Any(r => r.Id <= 0 || string.IsNullOrWhiteSpace(r.Name))) throw new InvalidOperationException("BulkRead natural key failed");
        await using (var tx = await db.Database.BeginTransactionAsync()) { var rollback = Create(2, "ROLLBACK"); await db.BulkInsertAsync(rollback); await tx.RollbackAsync(); } if (await db.DeepMissions.AnyAsync(x => x.Code.StartsWith("ROLLBACK-"))) throw new InvalidOperationException("Bulk transaction rollback failed");
        var deleted = roots[0]; var deletedPointIds = deleted.Segments.SelectMany(s => s.Waypoints).Select(p => p.Id).ToList(); await db.BulkDeleteAsync([deleted]); if (await db.DeepMissions.AnyAsync(x => x.Id == deleted.Id) || await db.DeepMissionConstraints.AnyAsync(c => deletedPointIds.Contains(c.WaypointId))) throw new InvalidOperationException("Bulk cascade delete failed");
        return $"bulk CRUD passed for {roots.Count} roots/{segments.Count} segments/{points.Count} waypoints/{constraints.Count} constraints/{crew.Count} crew";
    }

    private static Task ResetRows(BulkPackageDbContext db) => db.Database.ExecuteSqlRawAsync("DELETE FROM \"DeepMission\"");
}
