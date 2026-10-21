using System.Diagnostics;
using System.Text.Json;
using EFCore.BulkExtensions;
using FlightService.Domain.Entities;
using GaussDB;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Monica.Core.Modularity.Abstractions;
using Monica.DependencyInjection.Abstractions;
using Monica.Modules;
using Monica.Repository.Entity.Abstractions;
using Monica.Repository.Persistence.Abstractions;
using Monica.Testing.Hosting;
using Platform.BuildingBlocks.DataSync;
using Platform.BuildingBlocks.DataSync.Interfaces;
using Platform.BuildingBlocks.Persistence;
using UserService.Domain.Entities;
using FileService.Domain.Entities;
using MilitaryFlightService.Domain.Entities;
using MilitaryFlightService.Domain.EFCoreEncryption;
using MilitaryFlightService.Domain.EFCoreEncryption.Providers;
using DataExchangeService.Domain.Entities.MessageExchange;
using File = System.IO.File;

var baseConnection = Environment.GetEnvironmentVariable("GAUSSDB_TEST_CONNECTION")
    ?? throw new InvalidOperationException("请设置 GAUSSDB_TEST_CONNECTION。");
var database = "bulk_current_fips_" + Guid.NewGuid().ToString("N");
var adminString = new GaussDBConnectionStringBuilder(baseConnection) { Database = "postgres", Pooling = false }.ConnectionString;
var connection = new GaussDBConnectionStringBuilder(baseConnection) { Database = database, Pooling = false }.ConnectionString;
var results = new List<object>();
int failures = 0;
// DateOnly requires a native date type. In openGauss A mode, date aliases timestamp.
await Admin($"CREATE DATABASE \"{database}\" DBCOMPATIBILITY = 'PG'");
try
{
    await using var app = await new CurrentFipsFactory(connection).CreateAsync();
    await app.VerifyAsync<CurrentFipsContext>(async (db, token) =>
    {
        // The provider's HasTables probe sees openGauss system tables. Apply the
        // production model DDL explicitly in this newly created disposable database.
        var schema = db.Database.GenerateCreateScript();
        await File.WriteAllTextAsync("schema.sql", schema, token);
        await db.Database.ExecuteSqlRawAsync(schema, token);
        var root = db.Model.FindEntityType(typeof(Flight))!;
        var owned = root.GetNavigations().Where(x => x.TargetEntityType.IsOwned()).ToArray();
        Console.WriteLine($"真实 Flight 模型: {root.GetProperties().Count()} 根标量，{owned.Sum(x => x.TargetEntityType.GetProperties().Count())} Owned 标量，{owned.Length} Owned 对象。");
        await File.WriteAllTextAsync("model-evidence.json", JsonSerializer.Serialize(new
        {
            Root = root.Name, Table = root.GetTableName(),
            Properties = root.GetProperties().Select(p => new { p.Name, Column = p.GetColumnName(), Type = p.GetColumnType() }),
            Owned = owned.Select(n => new { n.Name, Properties = n.TargetEntityType.GetProperties().Select(p => new { p.Name, Column = p.GetColumnName(), Type = p.GetColumnType() }) }),
            LabelKey = db.Model.FindEntityType(typeof(FlightLabel))!.FindPrimaryKey()!.Properties.Select(p => p.Name),
            CoreAssemblies = new[] { typeof(BulkConfig).Assembly, typeof(EFCore.BulkExtensions.SqlAdapters.GaussDB.GaussDBAdapter).Assembly }.Select(a => new { a.FullName, a.Location })
        }, new JsonSerializerOptions { WriteIndented = true }), token);
    });
    await Run("真实 Flight 完整图普通保存、同步策略与查询", async () =>
    {
        await app.ExecuteAsync(scope =>
        {
            scope.Resolve<CurrentFipsContext>().Add(CreateFlight(1001));
            return Task.CompletedTask;
        });
        await app.VerifyAsync<CurrentFipsContext>(async (db, token) =>
        {
            var row = await Graph(db).SingleAsync(x => x.Id == 1001, token);
            Check(row.Plan.Callsign == "TEST1001" && row.Plan.ADEP == "ZBAA", "Owned 计划字段不匹配");
            Check(row.DepInfo != null && row.ArrInfo != null && row.RteInfo != null && row.FlightLabels.Count == 2, "图不完整");
            Check(row.DataSyncSource == "TEST" && row.DataSyncFlags.HasFlag(ESystemDataSpecialFlags.SelfHosted), "真实 FIPS 同步策略未运行");
        });
    });
    await Run("真实 Flight 宽字段多表 Bulk CRUD、选择性 Owned 更新与复合键", async () =>
    {
        var rows = Enumerable.Range(0, 20).Select(i => CreateFlight(2000 + i)).ToList();
        await app.ExecuteAsync(async scope =>
        {
            var db = scope.Resolve<CurrentFipsContext>();
            await db.BulkInsertAsync(rows);
            await db.BulkInsertAsync(rows.Select(x => x.DepInfo!).ToList());
            await db.BulkInsertAsync(rows.Select(x => x.ArrInfo!).ToList());
            await db.BulkInsertAsync(rows.Select(x => x.RteInfo!).ToList());
            await db.BulkInsertAsync(rows.SelectMany(x => x.FlightLabels).ToList());
        });
        await app.VerifyAsync<CurrentFipsContext>(async (db, token) =>
        {
            var stored = await Graph(db).Where(x => x.Id >= 2000 && x.Id < 2020).ToListAsync(token);
            Check(stored.Count == 20 && stored.All(x => x.FlightLabels.Count == 2 && x.DepInfo != null && x.ArrInfo != null && x.RteInfo != null), "Bulk 图/FK 数据不完整");
            Check(stored.All(x => x.DataSyncSource != "TEST"), "技术 Bulk 应明确绕过普通保存策略");
            Check(stored[0].Plan.TotalEET == TimeSpan.FromMinutes(85), "interval 往返不匹配");
        });
        await app.ExecuteAsync(async scope =>
        {
            var db = scope.Resolve<CurrentFipsContext>();
            foreach (var row in rows) { row.Plan.Callsign = "CHANGED"; row.RegMarking = "UNWANTED"; }
            await db.BulkUpdateAsync(rows, new BulkConfig { PropertiesToIncludeOnUpdate = ["Plan.Callsign"], SetOutputIdentity = true });
            var probes = rows.Select(x => new Flight { Id = x.Id, Plan = new() }).ToList();
            await db.BulkReadAsync(probes);
            Check(probes.All(x => x.Plan.Callsign == "CHANGED" && x.RegMarking != "UNWANTED"), "选择性更新修改了其他字段");
        });
        await app.ExecuteAsync(async scope =>
        {
            var db = scope.Resolve<CurrentFipsContext>();
            await db.BulkDeleteAsync(rows.SelectMany(x => x.FlightLabels).ToList());
            await db.BulkDeleteAsync(rows.Select(x => x.RteInfo!).ToList());
            await db.BulkDeleteAsync(rows.Select(x => x.DepInfo!).ToList());
            await db.BulkDeleteAsync(rows.Select(x => x.ArrInfo!).ToList());
            await db.BulkDeleteAsync(rows);
        });
        await app.VerifyAsync<CurrentFipsContext>(async (db, token) => Check(await db.Flights.CountAsync(token) == 1, "Bulk 删除污染普通保存数据"));
    });
    await Run("真实 Flight 普通保存与 Bulk 共事务失败回滚", async () =>
    {
        try
        {
            await app.ExecuteAsync(async scope =>
            {
                var db = scope.Resolve<CurrentFipsContext>();
                db.Add(CreateFlight(3001));
                await db.SaveChangesAsync();
                await db.BulkInsertAsync(new List<Flight> { CreateFlight(3002, false) });
                throw new ApplicationException("预期回滚");
            });
            throw new InvalidOperationException("预期异常未发生");
        }
        catch (ApplicationException) { }
        await app.VerifyAsync<CurrentFipsContext>(async (db, token) => Check(!await db.Flights.AnyAsync(x => x.Id >= 3000, token), "提前 flush 或 Bulk 未回滚"));
    });
    await Run("Bulk 后普通 Flight Owned 跟踪修改和同步策略仍工作", async () =>
    {
        await app.ExecuteAsync(async scope =>
        {
            var db = scope.Resolve<CurrentFipsContext>();
            var flight = await db.Flights.SingleAsync(x => x.Id == 1001);
            flight.Plan.Callsign = "NORMAL";
            flight.RouteInfo.AirwayCodes = "A1/A2";
        });
        await app.VerifyAsync<CurrentFipsContext>(async (db, token) =>
        {
            var row = await db.Flights.SingleAsync(x => x.Id == 1001, token);
            Check(row.Plan.Callsign == "NORMAL" && row.RouteInfo.AirwayCodes == "A1/A2" && row.DataSyncSource == "TEST", "普通跟踪保存回归");
        });
    });
    await Run("真实 Flight BulkSaveChanges 保留生产同步和保存管线", async () =>
    {
        await app.ExecuteAsync(async scope =>
        {
            var db = scope.Resolve<CurrentFipsContext>();
            db.Add(CreateFlight(4001));
            await db.BulkSaveChangesAsync(new BulkConfig { PreserveSaveChangesPipeline = true });
            Check(!db.ChangeTracker.HasChanges(), "保存后跟踪状态未接受");
        });
        await app.VerifyAsync<CurrentFipsContext>(async (db, token) =>
        {
            var row = await Graph(db).SingleAsync(x => x.Id == 4001, token);
            Check(row.DataSyncSource == "TEST" && row.FlightLabels.Count == 2 && row.DepInfo != null, "生产保存策略或实体图丢失");
        });
    });
    await Run("真实 FIPS 结束或失败工作单元禁止后续同步及异步 COPY", async () =>
    {
        foreach (var fail in new[] { false, true })
        foreach (var isAsync in new[] { false, true })
        {
            await using var scope = app.CreateScope();
            var db = scope.Resolve<CurrentFipsContext>();
            var manager = scope.Resolve<Monica.Repository.UnitOfWork.Abstractions.IUnitOfWorkManager>();
            try { await manager.RunAsync(() => fail ? Task.FromException(new ApplicationException("expected")) : Task.CompletedTask); }
            catch (ApplicationException) { }
            var rejected = false;
            try
            {
                var row = new List<Flight> { CreateFlight(5000 + (fail ? 10 : 0) + (isAsync ? 1 : 0), false) };
                if (isAsync) await db.BulkInsertAsync(row); else db.BulkInsert(row);
            }
            catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "COPY 逃逸工作单元生命周期");
        }
        await app.VerifyAsync<CurrentFipsContext>(async (db, token) => Check(!await db.Flights.AnyAsync(x => x.Id >= 5000, token), "终态写入泄漏"));
    });
    await Run("真实 User 和 FileTemplate JSON 深层对象、集合、空值往返", async () =>
    {
        var user = new User { Id = Guid.NewGuid(), Username = "bulk01", Nickname = "bulk", Password = "fixture", UserDesc = "fixture",
            CreationTime = new DateTime(2026, 10, 21), UserInfo = new() { Gender = "X", Email = "test@example.invalid", BirthDay = new DateOnly(2000, 1, 1), Area = new() { Province = "P", City = "C", District = "D" } } };
        var template = new FileTemplate { Id = 6001, TemplateName = "bulk-json", Version = 1, CreationTime = new DateTime(2026, 10, 21),
            Content = new() { HeaderRowIndex = 2, Items = [new() { Name = "flight", TargetField = "Plan.Callsign", ColumnIndex = 3, Priority = 9 }] } };
        await app.ExecuteAsync(async scope =>
        {
            var db = scope.Resolve<CurrentFipsContext>();
            await db.BulkInsertAsync(new[] { user });
            await db.BulkInsertAsync(new[] { template });
        });
        await app.VerifyAsync<CurrentFipsContext>(async (db, token) =>
        {
            var stored = await db.Users.AsNoTracking().SingleAsync(x => x.Id == user.Id, token);
            Check(stored.UserInfo?.Area?.District == "D" && stored.UserInfo.BirthDay == user.UserInfo.BirthDay, "用户 JSON 深层内容不匹配");
            var file = await db.Templates.AsNoTracking().SingleAsync(x => x.Id == 6001, token);
            Check(file.Content.Items.Single().TargetField == "Plan.Callsign" && file.Content.Items[0].Priority == 9, "模板 JSON 集合不匹配");
        });
        user.UserInfo = null;
        template.Content.Items[0].Priority = 17;
        await app.ExecuteAsync(async scope =>
        {
            var db = scope.Resolve<CurrentFipsContext>();
            await db.BulkUpdateAsync(new[] { user });
            await db.BulkUpdateAsync(new[] { template });
            var probe = new[] { new User { Id = user.Id, UserInfo = new() { Area = new() { City = "stale" } } } };
            await db.BulkReadAsync(probe);
            Check(probe[0].UserInfo == null, "JSON null 未清除旧对象");
        });
        await app.VerifyAsync<CurrentFipsContext>(async (db, token) => Check((await db.Templates.AsNoTracking().SingleAsync(x => x.Id == 6001, token)).Content.Items.Single().Priority == 17, "嵌套 JSON 修改丢失"));
    });
    await Run("真实 MAPlan 加密 converter COPY、物理密文与 EF 解密", async () =>
    {
        var plan = new MAPlan { Id = 7001, Callsign = "SECRET", PlanType = "TEST", PlanUser = "UNIT", PlanDate = new DateOnly(2026, 10, 21), CreationTime = new DateTime(2026, 10, 21) };
        await app.ExecuteAsync(scope => scope.Resolve<CurrentFipsContext>().BulkInsertAsync(new[] { plan }));
        await app.VerifyAsync<CurrentFipsContext>(async (db, token) =>
        {
            var row = await db.MilitaryPlans.AsNoTracking().SingleAsync(x => x.Id == 7001 && x.Callsign == "SECRET", token);
            Check(row.PlanType == "TEST" && row.PlanUser == "UNIT", "EF 解密或加密查询参数失败");
            await db.Database.OpenConnectionAsync(token);
            await using var command = db.Database.GetDbConnection().CreateCommand();
            var table = db.Model.FindEntityType(typeof(MAPlan))!.GetTableName()!;
            command.CommandText = $"SELECT \"Callsign\" FROM \"{table}\" WHERE \"PlanID\" = 7001";
            var encrypted = (string)(await command.ExecuteScalarAsync(token))!;
            Check(encrypted != "SECRET" && Convert.FromBase64String(encrypted).Length > 0, "数据库意外保存明文");
        });
        plan.Callsign = "CHANGED";
        await app.ExecuteAsync(async scope =>
        {
            var db = scope.Resolve<CurrentFipsContext>();
            await db.BulkUpdateAsync(new[] { plan });
            var probe = new[] { new MAPlan { Id = 7001 } };
            await db.BulkReadAsync(probe);
            Check(probe[0].Callsign == "CHANGED", "加密属性 BulkRead 回填失败");
        });
    });
    await Run("真实 MessageCircuitState Revision 并发通过保留保存管线拒绝旧快照", async () =>
    {
        await app.ExecuteAsync(scope => { scope.Resolve<CurrentFipsContext>().Add(new MessageCircuitState("fixture-channel") { Id = 8001 }); return Task.CompletedTask; });
        await using var staleScope = app.CreateScope();
        var staleDb = staleScope.Resolve<CurrentFipsContext>();
        var stale = await staleDb.Circuits.SingleAsync(x => x.Id == 8001);
        await app.ExecuteAsync(async scope =>
        {
            var db = scope.Resolve<CurrentFipsContext>();
            (await db.Circuits.SingleAsync(x => x.Id == 8001)).ReserveNextSequence(3, new DateTime(2026, 10, 21));
            await db.BulkSaveChangesAsync(new BulkConfig { PreserveSaveChangesPipeline = true });
        });
        stale.ReserveNextSequence(3, new DateTime(2026, 10, 21));
        var rejected = false;
        try { await staleScope.Resolve<Monica.Repository.UnitOfWork.Abstractions.IUnitOfWorkManager>().RunAsync(() => staleDb.BulkSaveChangesAsync(new BulkConfig { PreserveSaveChangesPipeline = true })); }
        catch (DbUpdateConcurrencyException) { rejected = true; }
        Check(rejected, "自定义 Revision 并发令牌未执行");
        await app.VerifyAsync<CurrentFipsContext>(async (db, token) => Check((await db.Circuits.AsNoTracking().SingleAsync(x => x.Id == 8001, token)).Revision == 1, "旧快照覆盖获胜者"));
    });
}
finally { GaussDBConnection.ClearAllPools(); await Admin($"DROP DATABASE \"{database}\""); }
await File.WriteAllTextAsync("results.json", JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
return failures == 0 ? 0 : 1;

async Task Run(string name, Func<Task> test)
{
    var timer = Stopwatch.StartNew();
    try { await test(); results.Add(new { name, status = "PASS", elapsedMs = timer.ElapsedMilliseconds }); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failures++; results.Add(new { name, status = "FAIL", error = ex.ToString() }); Console.WriteLine("FAIL " + name + ": " + ex); }
}
async Task Admin(string sql)
{
    await using var db = new GaussDBConnection(adminString);
    await db.OpenAsync();
    await using var command = db.CreateCommand(); command.CommandText = sql; await command.ExecuteNonQueryAsync();
}
static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
static IQueryable<Flight> Graph(CurrentFipsContext db) => db.Flights.AsNoTracking().Include(x => x.DepInfo).Include(x => x.ArrInfo).Include(x => x.RteInfo).Include(x => x.FlightLabels);
static Flight CreateFlight(long id, bool children = true)
{
    var flight = new Flight
    {
    Id = id, Plan = new() { Callsign = "TEST" + id, ADEP = "ZBAA", ADES = "ZSPD", SOBT = new DateTime(2026, 10, 21, 8, 0, 0), TotalEET = TimeSpan.FromMinutes(85) },
    RegMarking = "B1234", AircraftInfo = new() { AircraftCode = "ABC123" }, RouteInfo = new() { AirwayCodes = "A1", FiledRoute = "POINT1 POINT2" },
    DepInfo = children ? new() { Id = id, TOBT = new DateTime(2026, 10, 21, 8, 0, 0) } : null,
    ArrInfo = children ? new() { Id = id } : null,
    RteInfo = children ? new() { Id = id + 100000, FPID = id, RtePoints = "[]" } : null,
    FlightLabels = children ? [new() { FlightId = id, LabelDataId = 10 }, new() { FlightId = id, LabelDataId = 20 }] : []
    };
    // Production entities use empty string defaults as generated-value sentinels.
    // Populate required strings in the fixture so optional owned rows are present.
    foreach (var entity in new object?[] { flight, flight.Plan, flight.AircraftInfo, flight.RouteInfo, flight.DepInfo, flight.ArrInfo, flight.RteInfo }.OfType<object>())
        foreach (var property in entity.GetType().GetProperties().Where(p => p.PropertyType == typeof(string) && p.CanWrite))
            if (string.IsNullOrEmpty((string?)property.GetValue(entity))) property.SetValue(entity, "X");
    flight.ControlAreas = "TEST";
    flight.DistributeNodes = "TEST";
    return flight;
}

public sealed class CurrentFipsContext(DbContextOptions<CurrentFipsContext> options, ICachedServiceProvider provider)
    : FipsDbContext<CurrentFipsContext>(options, provider)
{
    public DbSet<Flight> Flights => Set<Flight>();
    public DbSet<User> Users => Set<User>();
    public DbSet<FileTemplate> Templates => Set<FileTemplate>();
    public DbSet<MAPlan> MilitaryPlans => Set<MAPlan>();
    public DbSet<MessageCircuitState> Circuits => Set<MessageCircuitState>();
    protected override void OnModelCreating(ModelBuilder builder)
    {
        // Test-only encryption keys; exercise the unchanged production extension.
        builder.UseEncryption(new AesProvider(new byte[32], new byte[16]));
        base.OnModelCreating(builder);
    }
}
public sealed class CurrentFipsFactory(string connection) : MonicaTestApplicationFactory<Flight>
{
    protected override void ConfigureMonica(IMonicaBuilder monica) => monica.AddRepository().AddRepositoryDbContext<CurrentFipsContext>((_, options) => options.UseGaussDB(connection));
    protected override void ConfigureServices(IServiceCollection services)
    {
        base.ConfigureServices(services);
        services.AddLogging(options => options.SetMinimumLevel(LogLevel.Warning));
        services.UseRealTestDatabase<CurrentFipsContext>((_, options) => options.UseGaussDB(connection));
        services.AddSingleton<IDataSyncFunctions, TestSyncFunctions>();
        services.AddTransient<IDataSyncPropertySetter, MoDataSyncPropertySetter>();
        services.AddScoped<IPersistencePolicy, FipsPersistencePolicy>();
    }
}
public sealed class TestSyncFunctions : IDataSyncFunctions
{
    public void SetAsSelfHost() { }
    public bool IsSelfHostOperation() => true;
    public bool IsUploading() => false;
    public bool IsSelfHostRequest() => true;
    public string GetSelfHostHeaderKey() => "test";
    public string GetLocalAddress() => "TEST";
}
