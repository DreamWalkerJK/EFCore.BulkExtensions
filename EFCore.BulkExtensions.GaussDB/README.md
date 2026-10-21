# EFCore.BulkExtensions.GaussDB

Bulk insert, update, upsert, delete, read and truncate for the GaussDB EF Core provider.
This project targets .NET 10 / EF Core 10 and uses `DotNetCore.EntityFrameworkCore.GaussDB 10.0.0`.
It is referenced by the .NET 10 aggregate project; the .NET 8 projects do not reference it.

## Installation

This fork distributes the provider as `DreamWalkerJK.EFCore.BulkExtensions.GaussDB`:

```shell
dotnet add package DreamWalkerJK.EFCore.BulkExtensions.GaussDB --version 10.0.1-gaussdb.1
```

The package brings in `DreamWalkerJK.EFCore.BulkExtensions.Core 10.0.1-gaussdb.1` and the GaussDB EF Core provider transitively.
Use the matching preview Core dependency instead of combining it with the upstream `EFCore.BulkExtensions.Core` package.
The C# namespace remains `EFCore.BulkExtensions`.

## Usage

Configure `UseGaussDB` to select the adapter automatically. Both synchronous and asynchronous operations are supported.

```csharp
using EFCore.BulkExtensions;
using Microsoft.EntityFrameworkCore;

var options = new DbContextOptionsBuilder<AppDbContext>()
    .UseGaussDB(connectionString)
    .Options;
await using var context = new AppDbContext(options);

await context.BulkInsertAsync(items, new BulkConfig { SetOutputIdentity = true });
await context.BulkUpdateAsync(items);
await context.BulkInsertOrUpdateAsync(items, new BulkConfig { CalculateStats = true });
await context.BulkReadAsync(items);
await context.BulkDeleteAsync(items);
```

Applications whose contexts implement save policies, such as Monica/FIPS audit stamping,
soft deletion, concurrency checks, DataSync, and transactional outbox projections, must keep
that save pipeline. Use the integration setting when calling the bulk-save entry point:

```csharp
await context.BulkSaveChangesAsync(new BulkConfig
{
    PreserveSaveChangesPipeline = true
});
```

This setting delegates to the context's normal EF `SaveChanges` implementation. It preserves
application behavior and uses normal EF write performance; bulk column/output/statistics/progress
settings do not apply. Do not enable it inside a `SaveChanges` override that calls bulk save itself.
The default bulk-save path and explicit `BulkInsert`/`BulkUpdate`/`BulkDelete` bypass save policies.
Explicit bulk operations can still join a caller-owned transaction for application-controlled imports.
The [current Monica validation harness](../validation/monica/README.md) verifies these boundaries
against real production Repository, UnitOfWork, audit policies, and outbox capture.

## Behavior and limitations

Inserts use binary COPY. Staging, DML and output loading share one connection per operation.
Column types, property access paths and value converters are prepared once per batch.
Async operations use asynchronous database I/O and propagate cancellation; owned staging
tables are cleaned up after completion or cancellation. A caller's transaction is never committed
or disposed by the adapter.

`KeepIdentity` can be combined with other `SqlBulkCopyOptions` flags. Without it, mixed upsert
lists containing assigned identities and zero identities generate the new identities per row,
independently of input order. Plain inserts with identity output keep INSERT conflict semantics.
`CalculateStats` reports affected rows; unmatched update/delete inputs are not counted.
`ReplaceReadEntities` requires a `List<T>` and returns every row matching the requested keys.
Nullable matching keys and custom source column mappings are supported for read/update/delete.

For `UseTempDB`, begin a transaction before the bulk operation. Temporary tables are removed
after each operation, allowing the same connection and transaction to be reused.
An upsert using `UpdateByProperties` requires an exact unique key. An existing standalone unique
index is recognized. If none exists, the adapter temporarily creates one; index creation requires
permission and may fail if the current data is not unique. Within a transaction, index creation
uses the non-concurrent form.

GaussDB's `ON DUPLICATE KEY UPDATE` cannot assign primary-key or unique-index columns,
including unique columns outside `UpdateByProperties`. Exclude those additional columns using
`PropertiesToExcludeOnUpdate`, or modify them with a separate `BulkUpdate`.
Upsert matches conflicts on database unique keys; applications with multiple unique constraints
must account for that database behavior. `OnConflictUpdateWhereSql`,
`BulkInsertOrUpdateOrDelete`, and an upsert containing only key columns are unsupported.
The adapter throws instead of silently ignoring these unsupported operations.

Process complex graphs as separate entity sets in an explicit transaction. Insert parents first,
read generated IDs with `SetOutputIdentity`, then assign foreign keys before inserting children.
For deletes, process children before parents or use configured database cascades; index foreign-key
columns for large workloads. `IncludeGraph` is not a general graph synchronization solution for GaussDB.

The 2026-09-16 package integration run with provider `10.0.0` encountered non-composable SQL
while loading output from `BulkInsert` with `SetOutputIdentity = true` for an owned/table-splitting
Flight model. Remaining steps in that scenario were not executed. Verify owned mappings separately;
the successful multi-table graph tests use explicit entity sets and do not establish support for every owned model.

The repository disables StrongNamer for projects consuming the GaussDB provider: rewriting
its unsigned dependency binaries changes their identities and breaks EF query compilation.
BulkExtensions' own assemblies remain signed. Consumers must preserve the provider binaries
distributed by NuGet.

## Validation

Run the integration suite after setting `ConnectionStrings:GaussDB` in the ignored
`EFCore.BulkExtensions.Tests/testsettings.local.json`. The connection string must include
`Database={databaseName}`; the test account needs permission to create and drop the dedicated
`efcore_bulkextensions_gaussdb_tests` database. The fixture recreates that database.
Run only one test process against it at a time.

```powershell
dotnet test EFCore.BulkExtensions.Tests/EFCore.BulkExtensions.Tests.csproj -c Release --filter FullyQualifiedName~GaussDB
```

The adapter validation on 2026-09-12 used the GaussDB provider against the supplied localhost:8888
server, which reports openGauss 6.0.3. See [validation results](VALIDATION.md) for coverage,
the initial performance measurements, packaging checks and environment limits.
The subsequent independent package run on 2026-09-16 completed all eight volume scenarios,
up to 100,000 narrow-table rows and 100,000 roots / 500,000 total rows in the multi-table model.
See [GaussDB preview measurements](../README.md#gaussdb-preview-measurements) for timings and measurement scope.
