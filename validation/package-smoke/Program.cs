using EFCore.BulkExtensions;
using GaussDB;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

var source = Environment.GetEnvironmentVariable("GAUSSDB_TEST_CONNECTION") ?? throw new InvalidOperationException("Set GAUSSDB_TEST_CONNECTION.");
var database = "bulk_package_smoke_" + Guid.NewGuid().ToString("N");
var admin = new GaussDBConnectionStringBuilder(source) { Database = "postgres", Pooling = false }.ConnectionString;
var connection = new GaussDBConnectionStringBuilder(source) { Database = database, Pooling = false }.ConnectionString;
await ExecuteAdmin($"CREATE DATABASE \"{database}\" DBCOMPATIBILITY = 'PG'");
try
{
    await using var context = new SmokeContext(new DbContextOptionsBuilder<SmokeContext>().UseGaussDB(connection).Options);
    await context.Database.ExecuteSqlRawAsync("""
        CREATE TABLE "Rows" ("Id" serial PRIMARY KEY, "Name" text NOT NULL, "Day" date NOT NULL, "Details_Code" char(8));
        """);
    List<SmokeRow> rows = [new() { Name = "package", Day = new DateOnly(2026, 10, 21), Details = new() { Code = "ZBAA" } }];
    await context.BulkInsertAsync(rows, new BulkConfig { SetOutputIdentity = true });
    Check(rows[0].Id > 0, "Generated identity missing.");
    var stored = await context.Rows.AsNoTracking().SingleAsync();
    Check(stored.Day == rows[0].Day && stored.Details!.Code!.TrimEnd() == "ZBAA", "Date/char/owned package round trip failed.");
    rows[0].Name = "updated";
    await context.BulkUpdateAsync(rows, new BulkConfig { PropertiesToIncludeOnUpdate = [nameof(SmokeRow.Name)] });
    var probes = new List<SmokeRow> { new() { Id = rows[0].Id } };
    await context.BulkReadAsync(probes);
    Check(probes[0].Name == "updated", "BulkRead/update failed.");
    await using (var transaction = await context.Database.BeginTransactionAsync())
    {
        await context.BulkInsertAsync(new[] { new SmokeRow { Name = "rollback", Day = new DateOnly(2026, 10, 21), Details = new() { Code = "ZSPD" } } });
        await transaction.RollbackAsync();
    }
    Check(await context.Rows.CountAsync() == 1, "Caller transaction did not roll back.");
    await context.BulkDeleteAsync(rows);
    Check(!await context.Rows.AnyAsync(), "Bulk delete failed.");
    var assemblies = AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name!.StartsWith("EFCore.BulkExtensions")).ToArray();
    Check(assemblies.Count(a => a.GetName().Name == "EFCore.BulkExtensions.Core") == 1, "Multiple Core assemblies loaded.");
    await File.WriteAllTextAsync("results.json", JsonSerializer.Serialize(new { Status = "PASS", PackageVersion = "10.0.1-gaussdb.2", Runtime = Environment.Version.ToString(), Assemblies = assemblies.Select(a => new { a.FullName, a.Location }) }, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine("PASS package-only consumer: generated Owned output, char/date, update/read/delete and transaction rollback.");
}
finally { GaussDBConnection.ClearAllPools(); await ExecuteAdmin($"DROP DATABASE \"{database}\""); }
// Isolate the supplied server's A-mode DateOnly behavior using only the driver.
// No Bulk API participates in this probe.
var aDatabase = "bulk_native_a_probe_" + Guid.NewGuid().ToString("N");
await ExecuteAdmin($"CREATE DATABASE \"{aDatabase}\" DBCOMPATIBILITY = 'A'");
try
{
    await using var native = new GaussDBConnection(new GaussDBConnectionStringBuilder(source) { Database = aDatabase, Pooling = false }.ConnectionString);
    await native.OpenAsync();
    await using var typeCommand = new GaussDBCommand("SELECT pg_typeof(DATE '2026-10-21')::text", native);
    var dateType = await typeCommand.ExecuteScalarAsync();
    string? failure = null;
    try
    {
        await using var dateCommand = new GaussDBCommand("SELECT @day::date", native);
        dateCommand.Parameters.AddWithValue("day", new DateOnly(2026, 10, 21));
        await dateCommand.ExecuteScalarAsync();
    }
    catch (Exception ex) { failure = ex.GetType().FullName + ": " + ex.Message; }
    string? readFailure = null;
    try
    {
        await using var readCommand = new GaussDBCommand("SELECT DATE '2026-10-21'", native);
        await using var reader = await readCommand.ExecuteReaderAsync();
        await reader.ReadAsync();
        _ = reader.GetFieldValue<DateOnly>(0);
    }
    catch (Exception ex) { readFailure = ex.GetType().FullName + ": " + ex.Message; }
    await using (var create = new GaussDBCommand("CREATE TABLE probe (day date)", native)) await create.ExecuteNonQueryAsync();
    string? copyFailure = null;
    try
    {
        await using var importer = await native.BeginBinaryImportAsync("COPY probe (day) FROM STDIN (FORMAT BINARY)");
        await importer.StartRowAsync();
        await importer.WriteAsync(new DateOnly(2026, 10, 21), "date");
        await importer.CompleteAsync();
    }
    catch (Exception ex) { copyFailure = ex.GetType().FullName + ": " + ex.Message; }
    await File.WriteAllTextAsync("native-a-probe.json", JsonSerializer.Serialize(new { Compatibility = "A", DateLiteralType = dateType, NativeParameterWriteError = failure, NativeDateOnlyReadError = readFailure, NativeBinaryCopyError = copyFailure, BulkApiUsed = false }, new JsonSerializerOptions { WriteIndented = true }));
}
finally { GaussDBConnection.ClearAllPools(); await ExecuteAdmin($"DROP DATABASE \"{aDatabase}\""); }
async Task ExecuteAdmin(string sql)
{
    await using var connection = new GaussDBConnection(admin);
    await connection.OpenAsync();
    await using var command = new GaussDBCommand(sql, connection);
    await command.ExecuteNonQueryAsync();
}
static void Check(bool success, string message) { if (!success) throw new InvalidOperationException(message); }
public sealed class SmokeContext(DbContextOptions<SmokeContext> options) : DbContext(options)
{
    public DbSet<SmokeRow> Rows => Set<SmokeRow>();
    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<SmokeRow>().OwnsOne(x => x.Details);
        model.Entity<SmokeRow>().Property(x => x.Id).UseSerialColumn();
        model.Entity<SmokeRow>().OwnsOne(x => x.Details).Property(x => x.Code).HasColumnType("char(8)");
    }
}
public sealed class SmokeRow
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public DateOnly Day { get; set; }
    public SmokeDetails? Details { get; set; }
}
public sealed class SmokeDetails { public string? Code { get; set; } }
