using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace EFCore.BulkExtensions.Tests;

public class BulkSaveChangesPipelineTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreservePipeline_RunsPoliciesAndInterceptors_InsideCallerTransaction(bool isAsync)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var interceptor = new SaveObserver();
        var options = new DbContextOptionsBuilder<PolicyContext>().UseSqlite(connection).AddInterceptors(interceptor).Options;
        await using var context = new PolicyContext(options);
        await context.Database.EnsureCreatedAsync();
        await using var transaction = await context.Database.BeginTransactionAsync();
        var row = new PolicyRow { Id = 1, Name = "created" };
        context.Add(row);
        var config = new BulkConfig { PreserveSaveChangesPipeline = true };
        if (isAsync) await context.BulkSaveChangesAsync(config);
        else context.BulkSaveChanges(config);

        Assert.True(row.Audited);
        Assert.Equal(EntityState.Unchanged, context.Entry(row).State);
        Assert.Equal(1, context.SaveCount);
        Assert.Equal(1, interceptor.SaveCount);
        Assert.Same(transaction, context.Database.CurrentTransaction);

        context.Remove(row);
        if (isAsync) await context.BulkSaveChangesAsync(config);
        else context.BulkSaveChanges(config);
        Assert.True(row.IsDeleted);
        Assert.Equal(0, await context.Rows.CountAsync());
        Assert.Equal(1, await context.Rows.IgnoreQueryFilters().CountAsync());

        await transaction.RollbackAsync();
        await using var verification = new PolicyContext(options);
        Assert.Empty(await verification.Rows.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task PreservePipeline_PropagatesCancellation_BeforeExecutingContextSave()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = new PolicyContext(new DbContextOptionsBuilder<PolicyContext>().UseSqlite(connection).Options);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => context.BulkSaveChangesAsync(
            new BulkConfig { PreserveSaveChangesPipeline = true }, cancellationToken: cancellation.Token));
        Assert.Equal(0, context.SaveCount);
    }

    private sealed class PolicyRow
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public bool Audited { get; set; }
        public bool IsDeleted { get; set; }
    }

    private sealed class PolicyContext(DbContextOptions<PolicyContext> options) : DbContext(options)
    {
        public DbSet<PolicyRow> Rows => Set<PolicyRow>();
        public int SaveCount { get; private set; }
        protected override void OnModelCreating(ModelBuilder builder)
            => builder.Entity<PolicyRow>().HasQueryFilter(row => !row.IsDeleted);
        private void ApplyPolicy()
        {
            SaveCount++;
            foreach (var entry in ChangeTracker.Entries<PolicyRow>().ToArray())
            {
                if (entry.State == EntityState.Added) entry.Entity.Audited = true;
                if (entry.State != EntityState.Deleted) continue;
                entry.State = EntityState.Modified;
                entry.Entity.IsDeleted = true;
            }
        }
        public override int SaveChanges(bool acceptAllChangesOnSuccess)
        {
            ApplyPolicy();
            return base.SaveChanges(acceptAllChangesOnSuccess);
        }
        public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
        {
            ApplyPolicy();
            return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        }
    }

    private sealed class SaveObserver : SaveChangesInterceptor
    {
        public int SaveCount { get; private set; }
        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
        {
            SaveCount++;
            return result;
        }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(SavingChanges(eventData, result));
    }
}
