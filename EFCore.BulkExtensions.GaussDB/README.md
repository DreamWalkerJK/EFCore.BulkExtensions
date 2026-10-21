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

The 2026-10-21 validation fixes the earlier non-composable output failure for owned/table-splitting
models. Nested and repeated owned types, renamed converted columns, generated output with query
filters, selective owned updates, and output-table cleanup now have database regressions. Complex
output is materialized through a result table; simple output retains the direct path.

Fixed-width `char(n)`/`character(n)` COPY columns use the driver's `bpchar` type. Direct COPY first
executes an intercepted `SELECT 1` so EF command guards can reject an invalid context lifetime.
This guard does not execute SaveChanges policies or rewrite the COPY command through interceptors.

The supplied openGauss server defaults new databases to compatibility mode `A`, where SQL `date`
is a timestamp. Native driver `DateOnly` reads and binary date COPY fail in that mode even without
BulkExtensions. Models using native `DateOnly` require a PostgreSQL-compatible date type (the
production-model harness creates a disposable `DBCOMPATIBILITY = 'PG'` database), or an explicit
application mapping compatible with its existing schema. Do not change an existing database mode
as an implicit side effect of installing this library.

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

This branch builds preview `10.0.1-gaussdb.2`; the installation example above refers to the earlier
published preview. The new Core and GaussDB packages were packed locally and tested by an
independent package-only consumer; publishing a release remains a separate step.

Current verification: **113 GaussDB cases**, **143 total selected Core/query-builder cases**, a
SQLite regression, **23 functional/volume scenarios**, **9 actual FIPS model scenarios**, and
**7 real-host Monica scenarios**. The original FIPS shared suite has the same three failures before
and after dependency replacement. Read [the current integration report](../validation/REPORT-20261021.md)
for the exact results, existing application defects, performance and integration conditions.

Build and test do not pack transitive provider projects. Use `dotnet pack` explicitly to create packages.

The adapter validation on 2026-09-12 used the GaussDB provider against the supplied localhost:8888
server, which reports openGauss 6.0.3. See [validation results](VALIDATION.md) for coverage,
the initial performance measurements, packaging checks and environment limits.
The subsequent independent package run on 2026-09-16 completed all eight volume scenarios,
up to 100,000 narrow-table rows and 100,000 roots / 500,000 total rows in the multi-table model.
See [GaussDB preview measurements](../README.md#gaussdb-preview-measurements) for timings and measurement scope.
