using GaussDB;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace EFCore.BulkExtensions.Tests.GaussDB;

public class GaussDBSaveChangesGraphTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BulkSaveChanges_RepeatedNestedOwnedTypes_InsertsUpdatesReplacesAndDeletes(bool isAsync)
    {
        await WithDatabase(async context =>
        {
            var row = new OwnedRoot
            {
                Name = "owned-root",
                Home = new Address { City = "home", Position = new Position { Code = "first" } },
                Work = new Address { City = "work", Position = new Position { Code = "second" } },
            };
            var originalHome = row.Home;
            var originalPosition = row.Home.Position;
            context.Add(row);
            await Save(context, isAsync);
            Assert.True(row.Id > 0);
            Assert.Same(originalHome, row.Home);
            Assert.Same(originalPosition, row.Home.Position);
            Assert.All(context.ChangeTracker.Entries(), entry => Assert.Equal(EntityState.Unchanged, entry.State));

            row.Home.Position!.Code = "nested-only";
            context.ChangeTracker.DetectChanges();
            Assert.Equal(EntityState.Unchanged, context.Entry(row).State);
            await Save(context, isAsync);
            var stored = await context.Set<OwnedRoot>().AsNoTracking().SingleAsync();
            Assert.Equal("nested-only", stored.Home!.Position!.Code);
            Assert.Equal("second", stored.Work!.Position!.Code);

            row.Home = new Address { City = "replacement", Position = new Position { Code = "replacement-position" } };
            var replacement = row.Home;
            row.Work = null;
            await Save(context, isAsync);
            Assert.Same(replacement, row.Home);
            stored = await context.Set<OwnedRoot>().AsNoTracking().SingleAsync();
            Assert.Equal("replacement", stored.Home!.City);
            Assert.Equal("replacement-position", stored.Home.Position!.Code);
            Assert.Null(stored.Work);
            Assert.All(context.ChangeTracker.Entries(), entry => Assert.Equal(EntityState.Unchanged, entry.State));

            context.Remove(row);
            await Save(context, isAsync);
            Assert.Empty(await context.Set<OwnedRoot>().ToListAsync());
            Assert.Empty(context.ChangeTracker.Entries());
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BulkSaveChanges_CompositeGeneratedKeys_PropagatesEveryForeignKeyComponent(bool isAsync)
    {
        await WithDatabase(async context =>
        {
            var parent = new CompositeParent { Tenant = "tenant", Name = "parent" };
            var child = new CompositeChild { Name = "child", Parent = parent };
            context.Add(child);
            await Save(context, isAsync);
            Assert.True(parent.Id > 0);
            Assert.True(child.Id > 0);
            Assert.Equal(parent.Id, child.ParentId);
            Assert.Equal(parent.Tenant, child.ParentTenant);
            var stored = await context.Set<CompositeChild>().AsNoTracking().Include(row => row.Parent).SingleAsync();
            Assert.Equal("parent", stored.Parent.Name);
            Assert.Equal(parent.Id, stored.ParentId);
            Assert.Equal(0, await context.SaveChangesAsync());
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BulkSaveChanges_CompositeShadowForeignKeys_PropagatesGeneratedValues(bool isAsync)
    {
        await WithDatabase(async context =>
        {
            var parent = new CompositeParent { Tenant = "shadow-tenant", Name = "shadow-parent" };
            var child = new ShadowChild { Name = "shadow-child", Parent = parent };
            context.Add(child);
            await Save(context, isAsync);
            Assert.True(parent.Id > 0);
            Assert.Equal(parent.Id, context.Entry(child).Property<int>("ParentId").CurrentValue);
            Assert.Equal(parent.Tenant, context.Entry(child).Property<string>("ParentTenant").CurrentValue);
            var stored = await context.Set<ShadowChild>().AsNoTracking().Include(row => row.Parent).SingleAsync();
            Assert.Equal(parent.Id, stored.Parent.Id);
            Assert.Equal(0, await context.SaveChangesAsync());
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BulkSaveChanges_OwnedAndCompositeGraph_PreservesCallerTransactionAndRollback(bool isAsync)
    {
        await WithDatabase(async context =>
        {
            await using (var transaction = await context.Database.BeginTransactionAsync())
            {
                context.Add(new OwnedRoot { Name = "rollback-owned", Home = new Address { City = "rollback-home" } });
                context.Add(new CompositeChild { Name = "rollback-child", Parent = new CompositeParent { Tenant = "rollback-tenant", Name = "rollback-parent" } });
                await Save(context, isAsync);
                Assert.Same(transaction, context.Database.CurrentTransaction);
                Assert.Equal(1, await context.Set<OwnedRoot>().CountAsync());
                Assert.Equal(1, await context.Set<CompositeChild>().CountAsync());
                Assert.Equal(0, await context.SaveChangesAsync());
                await transaction.RollbackAsync();
            }
            Assert.Null(context.Database.CurrentTransaction);
            Assert.Empty(await context.Set<OwnedRoot>().AsNoTracking().ToListAsync());
            Assert.Empty(await context.Set<CompositeChild>().AsNoTracking().ToListAsync());
            Assert.Empty(await context.Set<CompositeParent>().AsNoTracking().ToListAsync());
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BulkSaveChanges_SeparateOwnedTables_FailsBeforeWritesAndSupportsPreservedPipeline(bool isAsync)
    {
        await WithDatabase(async context =>
        {
            var root = new SeparateRoot { Name = "separate-root", Children = [new SeparateChild { Name = "separate-child" }] };
            context.Add(root);
            if (isAsync)
                await Assert.ThrowsAsync<NotSupportedException>(() => context.BulkSaveChangesAsync());
            else
                Assert.Throws<NotSupportedException>(() => context.BulkSaveChanges());
            Assert.Empty(await context.Set<SeparateRoot>().AsNoTracking().ToListAsync());
            Assert.Null(context.Database.CurrentTransaction);
            var config = new BulkConfig { PreserveSaveChangesPipeline = true };
            if (isAsync) await context.BulkSaveChangesAsync(config);
            else context.BulkSaveChanges(config);
            var stored = await context.Set<SeparateRoot>().AsNoTracking().Include(row => row.Children).SingleAsync();
            Assert.Equal("separate-child", Assert.Single(stored.Children).Name);
        });
    }

    private static Task Save(DbContext context, bool isAsync)
    {
        if (isAsync) return context.BulkSaveChangesAsync();
        context.BulkSaveChanges();
        return Task.CompletedTask;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BulkSaveChanges_PrincipalOnlyNavigation_PropagatesCompositeShadowForeignKeys(bool isAsync)
    {
        await WithDatabase(async context =>
        {
            var child = new ForwardChild { Name = "forward-child" };
            var parent = new CompositeParent { Tenant = "forward-tenant", Name = "forward-parent", Children = [child] };
            context.Add(parent);
            await Save(context, isAsync);
            Assert.True(parent.Id > 0);
            Assert.Equal(parent.Id, context.Entry(child).Property<int>("ParentId").CurrentValue);
            Assert.Equal(parent.Tenant, context.Entry(child).Property<string>("ParentTenant").CurrentValue);
            context.ChangeTracker.Clear();
            var stored = await context.Set<CompositeParent>().Include(row => row.Children).SingleAsync();
            Assert.Equal("forward-child", Assert.Single(stored.Children).Name);
            Assert.Equal(0, await context.SaveChangesAsync());
        });
    }

    private static async Task WithDatabase(Func<SaveGraphContext, Task> test)
    {
        var name = "bulk_save_graph_" + Guid.NewGuid().ToString("N");
        await using var admin = new GaussDBConnection(ContextUtil.GetGaussDBConnectionString("postgres"));
        await admin.OpenAsync();
        await using (var command = new GaussDBCommand($"CREATE DATABASE \"{name}\"", admin))
            await command.ExecuteNonQueryAsync();
        var connectionString = new GaussDBConnectionStringBuilder(ContextUtil.GetGaussDBConnectionString(name)) { Pooling = false }.ConnectionString;
        try
        {
            await using var context = new SaveGraphContext(new DbContextOptionsBuilder<SaveGraphContext>().UseGaussDB(connectionString).Options);
            await context.Database.ExecuteSqlRawAsync(context.Database.GenerateCreateScript());
            await test(context);
        }
        finally
        {
            await using var command = new GaussDBCommand($"DROP DATABASE \"{name}\"", admin);
            await command.ExecuteNonQueryAsync();
        }
    }

    private sealed class SaveGraphContext(DbContextOptions<SaveGraphContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder builder)
        {
            GaussDBModelBuilderExtensions.UseSerialColumns(builder);
            builder.Entity<OwnedRoot>(entity =>
            {
                entity.OwnsOne(row => row.Home, owned => owned.OwnsOne(row => row.Position));
                entity.OwnsOne(row => row.Work, owned => owned.OwnsOne(row => row.Position));
            });
            builder.Entity<CompositeParent>(entity =>
            {
                entity.HasKey(row => new { row.Tenant, row.Id });
                GaussDBPropertyBuilderExtensions.UseSerialColumn(entity.Property(row => row.Id));
            });
            builder.Entity<CompositeChild>().HasOne(row => row.Parent).WithMany()
                .HasForeignKey(row => new { row.ParentTenant, row.ParentId });
            builder.Entity<ShadowChild>(entity =>
            {
                entity.Property<string>("ParentTenant").IsRequired();
                entity.Property<int>("ParentId");
                entity.HasOne(row => row.Parent).WithMany().HasForeignKey("ParentTenant", "ParentId");
            });
            builder.Entity<ForwardChild>(entity =>
            {
                entity.Property<string>("ParentTenant").IsRequired();
                entity.Property<int>("ParentId");
            });
            builder.Entity<CompositeParent>().HasMany(row => row.Children).WithOne().HasForeignKey("ParentTenant", "ParentId");
            builder.Entity<SeparateRoot>().OwnsMany(row => row.Children,
                owned => GaussDBPropertyBuilderExtensions.UseSerialColumn(owned.Property(row => row.Id)));
        }
    }

    private sealed class OwnedRoot
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public Address? Home { get; set; }
        public Address? Work { get; set; }
    }
    private sealed class Address
    {
        public string City { get; set; } = "";
        public Position? Position { get; set; }
    }
    private sealed class Position { public string Code { get; set; } = ""; }
    private sealed class CompositeParent
    {
        public int Id { get; set; }
        public string Tenant { get; set; } = "";
        public string Name { get; set; } = "";
        public List<ForwardChild> Children { get; set; } = [];
    }
    private sealed class CompositeChild
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string ParentTenant { get; set; } = "";
        public int ParentId { get; set; }
        public CompositeParent Parent { get; set; } = null!;
    }
    private sealed class ShadowChild
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public CompositeParent Parent { get; set; } = null!;
    }
    private sealed class ForwardChild
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
    }
    private sealed class SeparateRoot
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public List<SeparateChild> Children { get; set; } = [];
    }
    private sealed class SeparateChild
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
    }
}
