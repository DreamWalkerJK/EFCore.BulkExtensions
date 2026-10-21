using System.Data;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EFCore.BulkExtensions.Tests.GaussDB;

public partial class GaussDBBulkTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BulkSaveChanges_ExternalTransaction_AcceptsChangesAndPreservesOwnership(bool isAsync)
    {
        await _fixture.ResetSchemaAsync();
        await using var context = _fixture.CreateContext();
        var connection = context.Database.GetDbConnection();
        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            var row = CreateItems(1, "bulk-save-external")[0];
            context.Add(row);
            if (isAsync) await context.BulkSaveChangesAsync();
            else context.BulkSaveChanges();

            Assert.True(row.Id > 0);
            Assert.Equal(EntityState.Unchanged, context.Entry(row).State);
            Assert.Same(transaction, context.Database.CurrentTransaction);
            Assert.Equal(ConnectionState.Open, connection.State);
            Assert.Equal(0, await context.SaveChangesAsync());

            row.Name = "bulk-save-updated";
            if (isAsync) await context.BulkSaveChangesAsync();
            else context.BulkSaveChanges();
            Assert.Equal(EntityState.Unchanged, context.Entry(row).State);
            await transaction.RollbackAsync();
        }
        Assert.Null(context.Database.CurrentTransaction);
        Assert.Equal(ConnectionState.Closed, connection.State);
        Assert.Equal(0, await context.Set<GaussDbItem>().CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BulkSaveChanges_OwnedTransaction_DisposesBeforeNextSave(bool isAsync)
    {
        await _fixture.ResetSchemaAsync();
        await using var context = _fixture.CreateContext();
        var row = CreateItems(1, "bulk-save-owned")[0];
        context.Add(row);
        if (isAsync) await context.BulkSaveChangesAsync();
        else context.BulkSaveChanges();

        Assert.Equal(EntityState.Unchanged, context.Entry(row).State);
        Assert.Null(context.Database.CurrentTransaction);
        Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
        row.Name = "native-after-bulk-save";
        Assert.Equal(1, await context.SaveChangesAsync());
        Assert.Equal("native-after-bulk-save", await context.Set<GaussDbItem>().AsNoTracking().Select(item => item.Name).SingleAsync());
    }
}
