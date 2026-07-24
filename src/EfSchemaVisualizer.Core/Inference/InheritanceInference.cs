using System.Collections.Generic;
using System.Linq;
using EfSchemaVisualizer.Core.Model;

namespace EfSchemaVisualizer.Core.Inference;

public sealed record InheritanceFoldResult(
    IReadOnlyList<EntityModel> Entities,
    IReadOnlyList<RelationshipModel> Relationships);

public static class InheritanceInference
{
    public static InheritanceFoldResult Fold(IReadOnlyList<EntityModel> entities)
    {
        var byName = entities.ToDictionary(e => e.Name);
        var foldedEntities = new List<EntityModel>();
        var relationships = new List<RelationshipModel>();

        foreach (var entity in entities)
        {
            if (entity.BaseEntityName is null || !byName.ContainsKey(entity.BaseEntityName))
            {
                foldedEntities.Add(entity);
                continue;
            }

            var nearestFirstChain = BuildAncestorChain(entity, byName);

            // Root-first pass: decide the ORDER ancestor property names first appear in.
            // Own properties are excluded from this ordering (they're appended separately
            // below) but seed the seen-set so an own-property's name isn't folded in here.
            var seenNames = new HashSet<string>(entity.Properties.Select(p => p.Name));
            var ancestorPropertyNamesInOrder = new List<string>();

            foreach (var ancestor in nearestFirstChain.AsEnumerable().Reverse())
            {
                foreach (var property in ancestor.Properties)
                {
                    if (seenNames.Add(property.Name))
                    {
                        ancestorPropertyNamesInOrder.Add(property.Name);
                    }
                }
            }

            // Nearest-first pass: for each name, the NEAREST ancestor that declares it wins
            // (shadowing), even though the further ancestor may have declared it first.
            var foldedProperties = new List<PropertyModel>();
            foreach (var name in ancestorPropertyNamesInOrder)
            {
                var (winningProperty, owner) = nearestFirstChain
                    .Select(a => (Property: a.Properties.FirstOrDefault(p => p.Name == name), Owner: a))
                    .First(x => x.Property is not null);

                foldedProperties.Add(winningProperty! with { DeclaringEntityName = owner.Name });
            }

            foldedProperties.AddRange(entity.Properties);

            var keyPropertyNames = entity.KeyPropertyNames;
            var isKeyInferred = entity.IsKeyInferred;
            if (keyPropertyNames.Count == 0 && !entity.IsKeyless)
            {
                var nearestKeyedAncestor = nearestFirstChain.FirstOrDefault(a => a.KeyPropertyNames.Count > 0);
                if (nearestKeyedAncestor is not null)
                {
                    keyPropertyNames = nearestKeyedAncestor.KeyPropertyNames;
                    isKeyInferred = true;
                }
            }

            foldedEntities.Add(entity with
            {
                Properties = foldedProperties,
                KeyPropertyNames = keyPropertyNames,
                IsKeyInferred = isKeyInferred,
            });

            var directBase = byName[entity.BaseEntityName];
            relationships.Add(new RelationshipModel(
                directBase.Name,
                entity.Name,
                RelationshipKind.Inheritance,
                PrincipalNavigation: null,
                DependentNavigation: null,
                ForeignKeyProperties: new List<string>(),
                IsInferred: false));
        }

        return new InheritanceFoldResult(foldedEntities, relationships);
    }

    /// Nearest-ancestor-first (immediate parent, grandparent, ...). Cycle-guarded: a
    /// malformed `BaseEntityName` loop stops instead of looping forever.
    private static List<EntityModel> BuildAncestorChain(
        EntityModel entity, Dictionary<string, EntityModel> byName)
    {
        var chain = new List<EntityModel>();
        var visited = new HashSet<string> { entity.Name };
        var current = entity;

        while (current.BaseEntityName is not null && byName.TryGetValue(current.BaseEntityName, out var ancestor))
        {
            if (!visited.Add(ancestor.Name))
            {
                break;
            }

            chain.Add(ancestor);
            current = ancestor;
        }

        return chain;
    }
}
