using System.Collections.Generic;
using System.Linq;
using EfSchemaVisualizer.Core.Merging;
using EfSchemaVisualizer.Core.Model;

namespace EfSchemaVisualizer.Core.Inference;

public sealed record ComplexTypeFoldResult(IReadOnlyList<EntityModel> Entities);

/// Sibling to `OwnedTypeInference`, sharing the same splice/cycle-guard recursion shape but with
/// no `IsMany` dimension and no `RelationshipModel` emission: a complex type is never its own
/// table, so unlike `OwnsMany` there is nothing to draw an edge to — every `ComplexProperty` call
/// always folds (or is filtered out upstream by `ParseComplexPropertyCalls` for collection-typed
/// targets before it ever reaches here).
public static class ComplexTypeInference
{
    public static ComplexTypeFoldResult Fold(IReadOnlyList<EntityModel> entities, IReadOnlyList<ComplexTypeConfig> complexCalls)
    {
        var byName = entities.ToDictionary(e => e.Name);
        var callsByOwner = complexCalls.ToLookup(c => c.OwnerEntityName);
        var removedEntityNames = new HashSet<string>();
        var memo = new Dictionary<string, IReadOnlyList<PropertyModel>>();

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

            foreach (var call in callsByOwner[entityName])
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
                        FoldKind = FoldKind.Complex,
                        OwnerNavigationProperty = call.NavigationPropertyName,
                        DeclaringEntityName = p.DeclaringEntityName ?? targetName,
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

        var foldedEntities = entities
            .Where(e => !removedEntityNames.Contains(e.Name))
            .Select(e =>
            {
                var properties = memo.TryGetValue(e.Name, out var p) ? p : e.Properties;
                return ReferenceEquals(properties, e.Properties) ? e : e with { Properties = properties };
            })
            .ToList();

        return new ComplexTypeFoldResult(foldedEntities);
    }
}
