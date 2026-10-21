#if NET8_0_OR_GREATER
using Medallion.Collections; // uses StrongNamer nuget to sign ref. with Strong Name
#endif
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Transactions;

namespace EFCore.BulkExtensions;

internal static class DbContextBulkTransactionSaveChanges
{
    #region SaveChanges
    public static void SaveChanges(DbContext dbContext, BulkConfig? bulkConfig, Action<decimal>? progress)
    {
        SaveChangesAsync(dbContext, bulkConfig, progress, isAsync: false, CancellationToken.None).GetAwaiter().GetResult();
    }

    public static async Task SaveChangesAsync(DbContext dbContext, BulkConfig? bulkConfig, Action<decimal>? progress, CancellationToken cancellationToken)
    {
        await SaveChangesAsync(dbContext, bulkConfig, progress, isAsync: true, cancellationToken).ConfigureAwait(false);
    }

    private static async Task SaveChangesAsync(DbContext dbContext, BulkConfig? bulkConfig, Action<decimal>? progress, bool isAsync, CancellationToken cancellationToken)
    {
        // Framework contexts can own policies and transaction coordination inside SaveChanges.
        // Those cannot be reproduced by writing the tracker directly through COPY/MERGE.
        if (bulkConfig?.PreserveSaveChangesPipeline == true)
        {
            if (isAsync)
                await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            else
                dbContext.SaveChanges();
            return;
        }

        // 2 ways:
        // OPTION 1) iteration with Dic and Fast member
        // OPTION 2) using Node model (here setting FK still not implemented)
        int option = 1;

        if (bulkConfig == null)
        {
            bulkConfig = new BulkConfig { };
        }
        if (bulkConfig.OnSaveChangesSetFK && bulkConfig.SetOutputIdentity == false) // When FK is set by DB then SetOutput is required
        {
            bulkConfig.SetOutputIdentity = true;
        }

        var entries = GetEntriesToSave(dbContext);
        var entriesGroupedByEntity = entries.GroupBy(a => new { EntityType = a.Entry.Metadata, a.State },
            (entry, group) => new
            {
                entry.State,
                Entities = group.Select(a => a.Entry.Entity).ToList(),
                EntryType = entry.EntityType.ClrType,
                EntityType = entry.EntityType,
            })
        .ToList();

        // Function to get FKs of an entity type, except self-referencies
        Func<IEntityType, IEnumerable<IEntityType>> getFks = e => e.GetForeignKeys()
            .Where(x => x.PrincipalEntityType != e)
            .Select(x => x.PrincipalEntityType);

        // Topologically sort insert operations by FK
        var added = entriesGroupedByEntity.Where(x => x.State == EntityState.Added);
        var addedLookup = added.ToLookup(x => x.EntityType);
#if NET8_0_OR_GREATER
        var sortedAdded = added.OrderTopologicallyBy(g => getFks(g.EntityType).SelectMany(x => addedLookup[x]));
#else
        var sortedAdded = added;
#endif

        // Topologically sort delete operations by reverse FK
        var deleted = entriesGroupedByEntity.Where(x => x.State == EntityState.Deleted);
        var deletedLookup = deleted.ToLookup(x => x.EntityType);
#if NET8_0_OR_GREATER
        var sortedDeleted = deleted.OrderTopologicallyBy(g => getFks(g.EntityType).SelectMany(x => deletedLookup[x])).Reverse();
#else
        var sortedDeleted = deleted;
#endif

        var sortedGroups = sortedAdded
            .Concat(entriesGroupedByEntity.Where(x => x.State == EntityState.Modified))
            .Concat(sortedDeleted)
            .ToList();

        if (isAsync)
        {
            await dbContext.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            dbContext.Database.OpenConnection();
        }
        var connection = dbContext.GetUnderlyingConnection(bulkConfig);

        var hasExistingTransaction = dbContext.Database.CurrentTransaction != null || Transaction.Current != null;
        IDbContextTransaction? transaction = null;
        try
        {
            if (!hasExistingTransaction)
            {
                transaction = isAsync
                    ? await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)
                    : dbContext.Database.BeginTransaction();
            }

            if (option == 1)
            {
                foreach (var entryGroup in sortedGroups)
                {
                    Type entityType = entryGroup.EntryType;
                    if (bulkConfig.OnSaveChangesSetFK)
                    {
                        var navigations = entryGroup.EntityType.GetNavigations()
                            .Where(x => x.IsOnDependent && !x.ForeignKey.IsOwnership && x.PropertyInfo != null);
                        foreach (var navigation in navigations)
                        {
                            var navigationAccessor = FastProperty.GetOrCreate(navigation.PropertyInfo!);
                            var keyProperties = navigation.ForeignKey.Properties.Select((foreignKey, index) => new
                            {
                                ForeignKey = foreignKey,
                                PrincipalKey = navigation.ForeignKey.PrincipalKey.Properties[index],
                                ForeignAccessor = foreignKey.PropertyInfo == null ? null : FastProperty.GetOrCreate(foreignKey.PropertyInfo),
                                PrincipalAccessor = navigation.ForeignKey.PrincipalKey.Properties[index].PropertyInfo == null
                                    ? null : FastProperty.GetOrCreate(navigation.ForeignKey.PrincipalKey.Properties[index].PropertyInfo!),
                            }).ToArray();
                            foreach (var entity in entryGroup.Entities)
                            {
                                var parentEntity = navigationAccessor.Get(entity);
                                if (parentEntity is null) continue;
                                foreach (var keyProperty in keyProperties)
                                {
                                    var principalProperty = keyProperty.PrincipalKey;
                                    var principalValue = keyProperty.PrincipalAccessor != null
                                        ? keyProperty.PrincipalAccessor.Get(parentEntity)
                                        : dbContext.Entry(parentEntity).Property(principalProperty.Name).CurrentValue;
                                    var foreignProperty = keyProperty.ForeignKey;
                                    if (keyProperty.ForeignAccessor != null)
                                        keyProperty.ForeignAccessor.Set(entity, principalValue);
                                    else
                                        dbContext.Entry(entity).Property(foreignProperty.Name).CurrentValue = principalValue;
                                }
                            }
                        }
                    }

                    string methodName = EntityStateBulkMethodDict[entryGroup.State].Key;
                    if (isAsync)
                    {
                        await InvokeBulkMethod(dbContext, entryGroup.Entities, entityType, methodName, bulkConfig, progress, isAsync: true, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        InvokeBulkMethod(dbContext, entryGroup.Entities, entityType, methodName, bulkConfig, progress, isAsync: false, cancellationToken).GetAwaiter().GetResult();
                    }
                }
            }
            else if (option == 2)
            {
                List<BulkMethodEntries> bulkMethodEntriesList = GetBulkMethodEntries(entries.Select(entry => entry.Entry));
                foreach (var bulkMethod in bulkMethodEntriesList)
                {
                    if (isAsync)
                    {
                        await InvokeBulkMethod(dbContext, bulkMethod.Entries, bulkMethod.Type, bulkMethod.MethodName, bulkConfig, progress, isAsync: true, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        InvokeBulkMethod(dbContext, bulkMethod.Entries, bulkMethod.Type, bulkMethod.MethodName, bulkConfig, progress, isAsync: false, cancellationToken).GetAwaiter().GetResult();
                    }
                }
            }
            // Output hydration sets CLR keys. Let EF propagate those keys while
            // added dependents still accept generated values.
            dbContext.ChangeTracker.DetectChanges();
            if (!hasExistingTransaction)
            {
                if (isAsync) await transaction!.CommitAsync(cancellationToken).ConfigureAwait(false);
                else transaction!.Commit();
            }
            // Like EF SaveChanges, successful flushes accept changes even when the caller owns the transaction.
            dbContext.ChangeTracker.AcceptAllChanges();
        }
        finally
        {
            try
            {
                if (transaction is not null)
                {
                    if (isAsync) await transaction.DisposeAsync().ConfigureAwait(false);
                    else transaction.Dispose();
                }
            }
            finally
            {
                // Balance this method's OpenConnection call without closing a caller-owned open connection.
                if (isAsync) await dbContext.Database.CloseConnectionAsync().ConfigureAwait(false);
                else dbContext.Database.CloseConnection();
            }
        }
    }

    private static IReadOnlyCollection<(EntityEntry Entry, EntityState State)> GetEntriesToSave(DbContext dbContext)
    {
        var trackedEntries = dbContext.ChangeTracker.Entries().ToArray();
        var entries = trackedEntries.Where(entry => entry.State != EntityState.Unchanged)
            .ToDictionary(entry => entry.Entity, entry => (Entry: entry, State: entry.State), ReferenceEqualityComparer.Instance);
        var ownerIndexes = new Dictionary<IForeignKey, Dictionary<PrimaryKeysPropertyColumnNameValues, EntityEntry>>();
        foreach (var entry in trackedEntries.Where(entry => entry.State != EntityState.Unchanged && entry.Metadata.IsOwned()))
        {
            var ownerEntry = entry;
            while (ownerEntry.Metadata.IsOwned())
            {
                if (!OwnedTypeUtil.IsOwnedInSameTableAsOwner(ownerEntry.Metadata))
                    throw new NotSupportedException("BulkSaveChanges supports owned entities sharing their owner's table. Use PreserveSaveChangesPipeline for owned entities mapped to separate tables.");
                var ownership = ownerEntry.Metadata.FindOwnership()!;
                if (!ownerIndexes.TryGetValue(ownership, out var owners))
                {
                    owners = new Dictionary<PrimaryKeysPropertyColumnNameValues, EntityEntry>();
                    foreach (var candidate in trackedEntries.Where(candidate => ownership.PrincipalEntityType.IsAssignableFrom(candidate.Metadata)))
                    {
                        var key = new PrimaryKeysPropertyColumnNameValues(ownership.PrincipalKey.Properties
                            .Select(property => candidate.Property(property.Name).CurrentValue));
                        // Replacing an owned instance can track both the old and new instance
                        // with the same ownership key. Both lead to the same table owner.
                        owners.TryAdd(key, candidate);
                    }
                    ownerIndexes.Add(ownership, owners);
                }
                var ownerKey = new PrimaryKeysPropertyColumnNameValues(ownership.Properties
                    .Select(property => ownerEntry.Property(property.Name).CurrentValue));
                if (!owners.TryGetValue(ownerKey, out var owner))
                    throw new InvalidOperationException("The tracked owner of an owned entity could not be found.");
                ownerEntry = owner;
            }
            entries.Remove(entry.Entity);
            if (!entries.ContainsKey(ownerEntry.Entity))
                entries.Add(ownerEntry.Entity, (ownerEntry, EntityState.Modified));
        }
        return entries.Values;
    }

    private static async Task InvokeBulkMethod(DbContext context, List<object> entities, Type entityType, string methodName, BulkConfig bulkConfig, Action<decimal>? progress, bool isAsync, CancellationToken cancellationToken)
    {
        methodName += isAsync ? "Async" : "";
        MethodInfo? bulkMethod = typeof(DbContextBulkExtensions)
            .GetMethods()
            .Where(a => a.Name == methodName)
            .FirstOrDefault();

        bulkMethod = bulkMethod?.MakeGenericMethod(typeof(object));

        var arguments = new List<object?> { context, entities, bulkConfig, progress, entityType, cancellationToken };
        if (isAsync)
        {
            var methodArguments = arguments.ToArray();
            if (bulkMethod is not null)
            {
                var task = (Task?)bulkMethod.Invoke(null, methodArguments);
                if (task != null)
                {
                    await task.ConfigureAwait(false);
                }
            }

        }
        else
        {
            arguments.RemoveAt(arguments.Count - 1); // removes cancellationToken
            var methodArguments = arguments.ToArray();
            bulkMethod?.Invoke(null, methodArguments);
        }
    }

    private static Dictionary<EntityState, KeyValuePair<string, int>> EntityStateBulkMethodDict => new()
    {
        { EntityState.Deleted, new KeyValuePair<string, int>(nameof(DbContextBulkExtensions.BulkDelete), 1) },
        { EntityState.Modified, new KeyValuePair<string, int>(nameof(DbContextBulkExtensions.BulkUpdate), 2) },
        { EntityState.Added, new KeyValuePair<string, int>(nameof(DbContextBulkExtensions.BulkInsert), 3)},
    };
#endregion

    private static List<BulkMethodEntries> GetBulkMethodEntries(IEnumerable<EntityEntry> entries)
    {
        EntityEntry[] entryList = entries.ToArray();
        var tree = new Dictionary<Type, DbNode>();

        for (int i = 0; i < entryList.Length; i++)
        {
            EntityEntry entry = entryList[i];

            Type type = GetNonProxyType(entry.Entity.GetType());

            if (!tree.TryGetValue(type, out DbNode? node))
            {
                node = new DbNode() { Type = type };
                tree.TryAdd(type, node);
            }

            node.AddEntry(entry);

            var navigations = entry.Navigations.Where(a => a.IsLoaded);

            foreach (var n in navigations.Where(a => a.Metadata is IReadOnlyPropertyBase p && p.IsCollection))
            {
                Type navType = GetNonProxyType(n.Metadata.ClrType.GenericTypeArguments.Single());
                if (!tree.TryGetValue(navType, out DbNode? childNode))
                {
                    childNode = new DbNode() { Type = navType };

                    tree.TryAdd(navType, childNode);
                };

                if (!childNode.Parents.Any(a => a.Type == node.Type))
                {
                    childNode.Parents.Add(node);
                }

                if (!node.Children.Any(a => a.Type == navType))
                {
                    node.Children.Add(childNode);
                }
            }

            foreach (var n in navigations.Where(a => !a.Metadata.IsCollection))
            {
                Type navType = GetNonProxyType(n.Metadata.ClrType);
                if (!tree.TryGetValue(navType, out DbNode? parentNode))
                {
                    parentNode = new DbNode() { Type = navType };
                    tree.TryAdd(navType, parentNode);
                };

                if (!parentNode.Children.Any(a => a.Type == node.Type))
                {
                    parentNode.Children.Add(node);
                }

                if (!node.Parents.Any(a => a.Type == parentNode.Type))
                {
                    node.Parents.Add(parentNode);
                }
            }
        }

        var rootNodes = tree.Where(a => a.Value.Parents.Count == 0);
        var handledTypes = new Dictionary<Type, bool>();
        var bulkMehodEntriesList = new List<BulkMethodEntries>();

        bool TryAddNode(DbNode node)
        {
            if (node.Parents.All(a => handledTypes.TryGetValue(a.Type, out bool exists)))
            {
                if (!handledTypes.TryGetValue(node.Type, out bool exists))
                {
                    handledTypes.Add(node.Type, true);

                    foreach (var me in node.MethodEntries)
                    {
                        if (me.Value != null && me.Value.Count > 0)
                        {
                            bulkMehodEntriesList.Add(new BulkMethodEntries()
                            {
                                Type = node.Type,
                                MethodName = me.Key,
                                Entries = me.Value,
                            });
                        }
                    }
                }

                foreach (var p in node.Children)
                {
                    TryAddNode(p);
                }

                return exists;
            }

            return false;
        }

        foreach (var r in rootNodes)
        {
            TryAddNode(r.Value);
        }

        return bulkMehodEntriesList;
    }

    private static Type GetNonProxyType(Type type) => type.Namespace == "Castle.Proxies" ? type.BaseType! : type;


    internal class BulkMethodEntries
    {
        public BulkMethodEntries()
        {
            Entries = new List<object>();
        }

        public string MethodName { get; set; } = null!;

        public Type Type { get; set; } = null!;

        public List<object> Entries { get; set; }
    }

    internal class DbNode
    {
        public DbNode()
        {
            Parents = new List<DbNode>();
            Children = new List<DbNode>();
            MethodEntries = new KeyValuePair<string, List<object>>[EntityStateBulkMethodDict.Count];
        }

        public Type Type { get; set; } = null!;

        public List<DbNode> Parents { get; set; }

        public List<DbNode> Children { get; set; }

        public KeyValuePair<string, List<object>>[] MethodEntries { get; private set; }

        public void AddEntry(EntityEntry entry)
        {
            if (EntityStateBulkMethodDict.TryGetValue(entry.State, out KeyValuePair<string, int> method))
            {
                var methodEntry = MethodEntries.FirstOrDefault(a => a.Key == method.Key);
                if (methodEntry.Key == null)
                {
                    methodEntry = new KeyValuePair<string, List<object>>(method.Key, new List<object>());
                    MethodEntries[method.Value - 1] = methodEntry;
                }

                methodEntry.Value.Add(entry.Entity);
            }
        }
    }
}
