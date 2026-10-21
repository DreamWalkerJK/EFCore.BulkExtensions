using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using EFCore.BulkExtensions;
using GaussDB;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Monica.Authority.Identity.Abstractions;
using Monica.Authority.Identity.Models;
using Monica.Authority.Identity.Services;
using Monica.Core.Modularity.Extensions;
using Monica.DependencyInjection.Abstractions;
using Monica.EventBus.Annotations;
using Monica.Modules;
using Monica.Repository.Entity.Abstractions;
using Monica.Repository.Outbox.Models;
using Monica.Repository.Persistence.Abstractions;
using Monica.Repository.Persistence.Models;
using Monica.Repository.Persistence.Services;
using Monica.Repository.UnitOfWork.Abstractions;

const string databaseName = "bulk_monica_current20261021";
var original = Environment.GetEnvironmentVariable("GAUSSDB_TEST_CONNECTION")
    ?? throw new InvalidOperationException("Set GAUSSDB_TEST_CONNECTION before running integration validation.");
var connection = new GaussDBConnectionStringBuilder(original) { Database = databaseName, Pooling = false, CommandTimeout = 60 };
await using (var admin = new GaussDBConnection(new GaussDBConnectionStringBuilder(original) { Database = "postgres", Pooling = false }.ConnectionString))
{
    await admin.OpenAsync();
    await using var exists = new GaussDBCommand("SELECT COUNT(*) FROM pg_database WHERE datname = @name", admin);
    exists.Parameters.AddWithValue("name", databaseName);
    if (Convert.ToInt32(await exists.ExecuteScalarAsync()) == 0)
        await new GaussDBCommand($"CREATE DATABASE {databaseName}", admin).ExecuteNonQueryAsync();
}
var builder = Host.CreateApplicationBuilder();
builder.Logging.ClearProviders();
builder.AddMonica(monica =>
{
    monica.AddSnowflake(options => { options.WorkerId = 15; options.DatacenterId = 15; });
    monica.AddEventBus().UseNoOpDistributedEventBus();
    monica.AddRepository(options => options.EnableSensitiveDataLogging = false)
        .AddRepositoryDbContext<ValidationContext>((_, db) => db.UseGaussDB(connection.ConnectionString))
        .AddOutbox<ValidationContext>(options => options.EnableWorker = false)
        .AddEntityEventProjections<ValidationContext>(options =>
            options.RegisterEntity<ValidationRow, RowChanged>((_, change) => new RowChanged(
                ((ValidationRow)change.Entry.Entity).Id,
                change.Kind.ToString(),
                ((ValidationRow)change.Entry.Entity).Name)));
});
builder.Services.AddSingleton<ICurrentUser>(new CurrentUser(new ClaimsPrincipal(new ClaimsIdentity([
    new Claim(AuthorityClaimTypes.UserId, "current-validator"), new Claim(AuthorityClaimTypes.Username, "validator")], "test"))));
builder.Services.AddSingleton<IPersistencePolicy, DataSyncPolicy>();
using var host = builder.Build();
await host.StartAsync();
await using (var setupScope = host.Services.CreateAsyncScope())
{
    var db = setupScope.ServiceProvider.GetRequiredService<ValidationContext>();
    await db.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS \"CurrentRows\", \"MonicaOutbox\" CASCADE");
    var script = db.Database.GenerateCreateScript();
    await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "monica-schema.sql"), script);
    await db.Database.ExecuteSqlRawAsync(script);
}
var results = new List<object>();
var failures = 0;
async Task Run(string name, Func<Task<object>> work)
{
    var timer = Stopwatch.StartNew();
    try
    {
        var evidence = await work();
        results.Add(new { name, status = "PASS", elapsedMs = timer.ElapsedMilliseconds, evidence });
        Console.WriteLine($"PASS {name}: {JsonSerializer.Serialize(evidence)}");
    }
    catch (Exception error)
    {
        failures++;
        results.Add(new { name, status = "FAIL", elapsedMs = timer.ElapsedMilliseconds, exception = error.ToString() });
        Console.WriteLine($"FAIL {name}: {error}");
    }
}
static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
async Task<ValidationRow?> Read(long id)
{
    await using var scope = host.Services.CreateAsyncScope();
    return await scope.ServiceProvider.GetRequiredService<ValidationContext>().Rows.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(row => row.Id == id);
}
async Task<int> EventCount(long id)
{
    await using var scope = host.Services.CreateAsyncScope();
    var rows = await scope.ServiceProvider.GetRequiredService<ValidationContext>().Set<OutboxMessage>().AsNoTracking().ToListAsync();
    return rows.Count(row => JsonDocument.Parse(row.Body).RootElement.GetProperty("id").ToString() == id.ToString(System.Globalization.CultureInfo.InvariantCulture));
}
async Task WithOperation(Func<ValidationContext, Task> work)
{
    await using var scope = host.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<ValidationContext>();
    await scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>().RunAsync(() => work(db));
}
await Run("Current Monica native CRUD preserves audit, policy, soft delete and durable projections", async () =>
{
    await WithOperation(db => { db.Add(new ValidationRow { Id = 101, Name = "native" }); return Task.CompletedTask; });
    var inserted = (await Read(101))!;
    Check(inserted.CreationTime != default && inserted.CreatorId == "current-validator", "Creation audit missing.");
    Check(inserted.DataSyncFlags == 3 && inserted.DataSyncSource == "validation-node", "Persistence policy missing.");
    Check(await EventCount(101) == 1, "Created outbox snapshot missing or duplicated.");
    await WithOperation(async db => { var row = await db.Rows.SingleAsync(x => x.Id == 101); row.Name = "updated"; });
    var updated = (await Read(101))!;
    Check(updated.LastModifierId == "current-validator" && updated.EntityVersion == 1 && updated.ConcurrencyStamp != inserted.ConcurrencyStamp, "Update policies missing.");
    await WithOperation(async db => db.Remove(await db.Rows.SingleAsync(x => x.Id == 101)));
    var deleted = (await Read(101))!;
    Check(deleted.IsDeleted && deleted.DeleterId == "current-validator" && deleted.DeletionTime.HasValue, "Soft deletion missing.");
    Check(await EventCount(101) == 3, "CRUD must produce exactly three immutable outbox snapshots.");
    return new { inserted.CreatorId, updated.EntityVersion, deleted.IsDeleted, events = 3 };
});
await Run("BulkSaveChanges preserves current Monica creation policies and exactly one projection", async () =>
{
    await WithOperation(async db =>
    {
        db.Add(new ValidationRow { Id = 201, Name = "bulk-save" });
        await db.BulkSaveChangesAsync(new BulkConfig { PreserveSaveChangesPipeline = true });
    });
    var stored = (await Read(201))!;
    Check(stored.CreationTime != default && stored.CreatorId == "current-validator", "BulkSaveChanges bypasses Monica creation audit.");
    Check(stored.DataSyncFlags == 3 && !string.IsNullOrEmpty(stored.ConcurrencyStamp), "BulkSaveChanges bypasses Monica policy/concurrency initialization.");
    Check(await EventCount(201) == 1, "BulkSaveChanges bypasses entity-event outbox projection.");
    return new { stored.CreatorId, stored.DataSyncFlags, events = 1 };
});
await Run("BulkSaveChanges preserves current Monica modification and soft deletion", async () =>
{
    await WithOperation(db => { db.Add(new ValidationRow { Id = 301, Name = "bulk-update" }); return Task.CompletedTask; });
    var originalRow = (await Read(301))!;
    await WithOperation(async db => { var row = await db.Rows.SingleAsync(x => x.Id == 301); row.Name = "bulk-updated"; await db.BulkSaveChangesAsync(new BulkConfig { PreserveSaveChangesPipeline = true }); });
    var updated = (await Read(301))!;
    Check(updated.EntityVersion == 1 && updated.LastModifierId == "current-validator" && updated.ConcurrencyStamp != originalRow.ConcurrencyStamp, "BulkSaveChanges bypasses modification policies.");
    await WithOperation(async db => { db.Remove(await db.Rows.SingleAsync(x => x.Id == 301)); await db.BulkSaveChangesAsync(new BulkConfig { PreserveSaveChangesPipeline = true }); });
    var deleted = await Read(301);
    Check(deleted is { IsDeleted: true, DeleterId: "current-validator" }, "BulkSaveChanges physically deletes a Monica soft-delete entity.");
    Check(await EventCount(301) == 3, "BulkSaveChanges CRUD outbox snapshots missing.");
    return new { updated.EntityVersion, deleted!.IsDeleted, events = 3 };
});
await Run("BulkSaveChanges failure rolls back business and outbox after an early flush", async () =>
{
    try
    {
        await WithOperation(async db => { db.Add(new ValidationRow { Id = 401, Name = "rollback" }); await db.BulkSaveChangesAsync(new BulkConfig { PreserveSaveChangesPipeline = true }); throw new ApplicationException("expected rollback"); });
    }
    catch (ApplicationException error) when (error.Message == "expected rollback") { }
    Check(await Read(401) is null && await EventCount(401) == 0, "Rollback left business or outbox rows.");
    return new { businessRows = 0, outboxRows = 0 };
});
await Run("BulkSaveChanges honors stale concurrency tokens", async () =>
{
    await WithOperation(db => { db.Add(new ValidationRow { Id = 501, Name = "concurrency" }); return Task.CompletedTask; });
    await using var scope = host.Services.CreateAsyncScope();
    var staleDb = scope.ServiceProvider.GetRequiredService<ValidationContext>();
    var stale = await staleDb.Rows.SingleAsync(x => x.Id == 501);
    await WithOperation(async db => { (await db.Rows.SingleAsync(x => x.Id == 501)).Name = "winner"; });
    stale.Name = "stale";
    var rejected = false;
    try { await scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>().RunAsync(() => staleDb.BulkSaveChangesAsync(new BulkConfig { PreserveSaveChangesPipeline = true })); }
    catch (DbUpdateConcurrencyException) { rejected = true; }
    Check(rejected && (await Read(501))!.Name == "winner", "BulkSaveChanges ignored stale Monica ConcurrencyStamp.");
    return new { staleRejected = rejected };
});
await Run("Terminal Monica operation rejects subsequent BulkSaveChanges", async () =>
{
    await using var scope = host.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<ValidationContext>();
    await scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>().RunAsync(() => Task.CompletedTask);
    db.Add(new ValidationRow { Id = 601, Name = "terminal" });
    var rejected = false;
    try { await db.BulkSaveChangesAsync(new BulkConfig { PreserveSaveChangesPipeline = true }); } catch (InvalidOperationException) { rejected = true; }
    Check(rejected && await Read(601) is null, "BulkSaveChanges escaped terminal operation lifetime.");
    return new { rejected };
});
await Run("Explicit bulk COPY participates in current Monica transaction rollback", async () =>
{
    try
    {
        await WithOperation(async db => { await db.BulkInsertAsync(new[] { new ValidationRow { Id = 701, Name = "direct-rollback", ConcurrencyStamp = "raw" } }); throw new ApplicationException("direct rollback"); });
    }
    catch (ApplicationException error) when (error.Message == "direct rollback") { }
    Check(await Read(701) is null, "Explicit COPY did not enlist in Monica transaction.");
    return new { businessRows = 0 };
});
await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "monica-results.json"), JsonSerializer.Serialize(new { sourceCommit = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "source-commit.txt")).Trim(), failures, results }, new JsonSerializerOptions { WriteIndented = true }));
await host.StopAsync();
Environment.ExitCode = failures == 0 ? 0 : 1;

public sealed class ValidationRow : FullAuditedEntity<long>, IHasConcurrencyStamp, IHasEntityVersion
{
    public string Name { get; set; } = "";
    public string ConcurrencyStamp { get; set; } = "";
    public int EntityVersion { get; set; }
    public int DataSyncFlags { get; set; }
    public string DataSyncSource { get; set; } = "";
}
[Outbox, EventName("validation.current.row.v1")]
public sealed record RowChanged(long Id, string Kind, string Name);
public sealed class DataSyncPolicy : IPersistencePolicy
{
    public void Apply(DbContext context, IReadOnlyList<PersistenceChange> changes)
    {
        foreach (var row in changes.Select(change => change.Entry.Entity).OfType<ValidationRow>())
        { row.DataSyncFlags |= 3; row.DataSyncSource = "validation-node"; }
    }
}
public sealed class ValidationContext(DbContextOptions<ValidationContext> options, ICachedServiceProvider services)
    : RepositoryDbContext<ValidationContext>(options, services)
{
    public DbSet<ValidationRow> Rows => Set<ValidationRow>();
    protected override void OnModelCreating(ModelBuilder model)
    {
        model.UseSerialColumns();
        model.Entity<ValidationRow>(entity => { entity.ToTable("CurrentRows"); entity.HasKey(row => row.Id); entity.Property(row => row.Id).ValueGeneratedNever(); });
        base.OnModelCreating(model);
        // Monica timestamps are UTC by default; use provider-supported instant storage for audit values.
        foreach (var property in model.Model.FindEntityType(typeof(ValidationRow))!.GetProperties().Where(property => property.ClrType == typeof(DateTime) || property.ClrType == typeof(DateTime?)))
            property.SetColumnType("timestamp with time zone");
    }
}
