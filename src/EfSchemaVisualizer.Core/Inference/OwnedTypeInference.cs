using System.Collections.Generic;
using System.Linq;
using EfSchemaVisualizer.Core.Merging;
using EfSchemaVisualizer.Core.Model;
using EfSchemaVisualizer.Core.Parsing;

namespace EfSchemaVisualizer.Core.Inference;

public sealed record OwnedTypeFoldResult(
    IReadOnlyList<EntityModel> Entities,
    IReadOnlyList<RelationshipModel> Relationships);

public static class OwnedTypeInference
{
    public static OwnedTypeFoldResult Fold(IReadOnlyList<EntityModel> entities, IReadOnlyList<OwnedTypeConfig> ownedCalls)
    {
        var byName = entities.ToDictionary(e => e.Name);
        var ownsOneByOwner = ownedCalls.Where(c => !c.IsMany).ToLookup(c => c.OwnerEntityName);
        var removedEntityNames = new HashSet<string>();
        var memo = new Dictionary<string, IReadOnlyList<PropertyModel>>();

        // Folds `entityName`'s own OwnsOne targets into it, recursively (root of the recursion is
        // whichever entity a caller starts from; a multi-level owned chain like Order->Address->
        // Country resolves Country and Address fully before Order splices Address's already-folded
        // properties in). `visited` guards one recursion chain against a cycle (A owns B owns A):
        // hitting an entity already on the current path returns null instead of recursing forever,
        // and the caller that receives null leaves that particular nav property un-folded rather
        // than guessing. Memoized so a target reachable from two different owners (or visited twice
        // via the outer per-entity loop below) is only resolved once.
        IReadOnlyList<PropertyModel>? ResolveFoldedProperties(string entityName, HashSet<string> visited)
        {
            if (memo.TryGetValue(entityName, out var cached))
            {
                return cached;
            }

            if (!byName.TryGetValue(entityName, out var entity))
            {
                return null;
            }

            if (!visited.Add(entityName))
            {
                return null;
            }

            IReadOnlyList<PropertyModel> properties = entity.Properties;

            foreach (var call in ownsOneByOwner[entityName])
            {
                var navProperty = properties.FirstOrDefault(p => p.Name == call.NavigationPropertyName);
                if (navProperty is null || !byName.ContainsKey(navProperty.ClrType))
                {
                    continue;
                }

                var targetName = navProperty.ClrType;
                var targetProperties = ResolveFoldedProperties(targetName, visited);

                if (targetProperties is null)
                {
                    continue;
                }

                properties = properties
                    .Where(p => p.Name != call.NavigationPropertyName)
                    .Concat(targetProperties.Select(p => p with
                    {
                        IsOwned = true,
                        OwnerNavigationProperty = call.NavigationPropertyName,
                    }))
                    .ToList();

                removedEntityNames.Add(targetName);
            }

            visited.Remove(entityName);
            memo[entityName] = properties;
            return properties;
        }

        foreach (var entity in entities)
        {
            ResolveFoldedProperties(entity.Name, new HashSet<string>());
        }

        var relationships = new List<RelationshipModel>();

        foreach (var call in ownedCalls.Where(c => c.IsMany))
        {
            if (!byName.TryGetValue(call.OwnerEntityName, out var owner))
            {
                continue;
            }

            var navProperty = owner.Properties.FirstOrDefault(p => p.Name == call.NavigationPropertyName);
            var targetName = navProperty is null ? null : FluentSyntaxHelpers.TryGetElementTypeName(navProperty.ClrType);

            if (targetName is null || !byName.TryGetValue(targetName, out var target))
            {
                continue;
            }

            byName[targetName] = target with { IsOwned = true };
            relationships.Add(new RelationshipModel(
                call.OwnerEntityName,
                targetName,
                RelationshipKind.Owned,
                PrincipalNavigation: call.NavigationPropertyName,
                DependentNavigation: null,
                ForeignKeyProperties: new List<string>(),
                IsInferred: false));
        }

        var foldedEntities = entities
            .Where(e => !removedEntityNames.Contains(e.Name))
            .Select(e =>
            {
                var marked = byName[e.Name]; // same reference as `e` unless the OwnsMany pass mutated it
                var properties = memo.TryGetValue(e.Name, out var p) ? p : e.Properties;

                return ReferenceEquals(properties, e.Properties) && ReferenceEquals(marked, e)
                    ? e
                    : e with { Properties = properties, IsOwned = marked.IsOwned };
            })
            .ToList();

        return new OwnedTypeFoldResult(foldedEntities, relationships);
    }
}
