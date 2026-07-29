using System.Collections.Generic;
using System.Linq;
using EfSchemaVisualizer.Core.Model;
using EfSchemaVisualizer.Core.Parsing;
using Microsoft.CodeAnalysis.Text;

namespace EfSchemaVisualizer.Core.Inference;

public sealed record InheritanceFoldResult(
    IReadOnlyList<EntityModel> Entities,
    IReadOnlyList<RelationshipModel> Relationships,
    IReadOnlyList<Diagnostic>? Diagnostics = null)
{
    public IReadOnlyList<Diagnostic> Diagnostics { get; init; } = Diagnostics ?? new List<Diagnostic>();
}

public static class InheritanceInference
{
    public static InheritanceFoldResult Fold(IReadOnlyList<EntityModel> entities)
    {
        var byName = entities.ToDictionary(e => e.Name);
        var rootNameByEntity = entities.ToDictionary(e => e.Name, e => ResolveRootName(e, byName));
        var (resolvedStrategy, diagnostics) = ResolveMappingStrategies(entities, rootNameByEntity);

        var foldedEntities = new List<EntityModel>();
        var relationships = new List<RelationshipModel>();

        foreach (var entity in entities)
        {
            var strategy = resolvedStrategy[rootNameByEntity[entity.Name]];

            if (entity.BaseEntityName is null || !byName.ContainsKey(entity.BaseEntityName))
            {
                foldedEntities.Add(strategy == entity.MappingStrategy ? entity : entity with { MappingStrategy = strategy });
                continue;
            }

            var nearestFirstChain = BuildAncestorChain(entity, byName);

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

            var ownNames = new HashSet<string>(entity.Properties.Select(p => p.Name));
            var foldedProperties = new List<PropertyModel>();

            if (strategy == MappingStrategy.Tpt)
            {
                // TPT: the derived table physically has only its own columns plus the shared
                // PK/FK back to the base table, so fold in just the (possibly-inherited) key
                // property/properties — not the rest of the ancestor's columns.
                foreach (var name in keyPropertyNames)
                {
                    if (ownNames.Contains(name))
                    {
                        continue;
                    }

                    var match = nearestFirstChain
                        .Select(a => (Property: a.Properties.FirstOrDefault(p => p.Name == name), Owner: a))
                        .FirstOrDefault(x => x.Property is not null);

                    if (match.Property is not null)
                    {
                        foldedProperties.Add(match.Property with { DeclaringEntityName = match.Owner.Name });
                    }
                }
            }
            else
            {
                // TPH / TPC: fold every ancestor property into one flat shape (today's behavior).
                // Root-first pass: decide the ORDER ancestor property names first appear in.
                var seenNames = new HashSet<string>(ownNames);
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
                foreach (var name in ancestorPropertyNamesInOrder)
                {
                    var (winningProperty, owner) = nearestFirstChain
                        .Select(a => (Property: a.Properties.FirstOrDefault(p => p.Name == name), Owner: a))
                        .First(x => x.Property is not null);

                    foldedProperties.Add(winningProperty! with { DeclaringEntityName = owner.Name });
                }
            }

            foldedProperties.AddRange(entity.Properties);

            foldedEntities.Add(entity with
            {
                Properties = foldedProperties,
                KeyPropertyNames = keyPropertyNames,
                IsKeyInferred = isKeyInferred,
                MappingStrategy = strategy,
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

        return new InheritanceFoldResult(foldedEntities, relationships, diagnostics);
    }

    /// Resolves one mapping strategy per hierarchy (grouped by root entity name): the root's own
    /// explicit strategy wins if it has one; otherwise the first explicit strategy found among its
    /// descendants (in list order) wins; a hierarchy with no explicit strategy anywhere defaults to
    /// TPH. If more than one DISTINCT explicit strategy is declared across a hierarchy's members,
    /// the resolution above still picks one (root-priority) but an `InconsistentMappingStrategyInHierarchy`
    /// diagnostic is emitted, since that combination is invalid at EF's own model-build time.
    private static (Dictionary<string, MappingStrategy> Resolved, List<Diagnostic> Diagnostics) ResolveMappingStrategies(
        IReadOnlyList<EntityModel> entities, Dictionary<string, string> rootNameByEntity)
    {
        var resolved = new Dictionary<string, MappingStrategy>();
        var diagnostics = new List<Diagnostic>();

        var membersByRoot = entities
            .GroupBy(e => rootNameByEntity[e.Name])
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var (rootName, members) in membersByRoot)
        {
            var ordered = members.OrderBy(m => m.Name == rootName ? 0 : 1).ToList();

            var distinctExplicit = ordered
                .Select(m => m.MappingStrategy)
                .Where(s => s != MappingStrategy.Tph)
                .Distinct()
                .ToList();

            resolved[rootName] = distinctExplicit.FirstOrDefault();

            if (distinctExplicit.Count > 1)
            {
                diagnostics.Add(new Diagnostic(
                    DiagnosticCodes.InconsistentMappingStrategyInHierarchy,
                    $"Entities in the '{rootName}' hierarchy declare more than one mapping strategy; using '{resolved[rootName]}'.",
                    rootName,
                    PropertyName: null,
                    TextSpan.FromBounds(0, 0),
                    DiagnosticCategory.ModelValidity));
            }
        }

        return (resolved, diagnostics);
    }

    /// The topmost ancestor name reachable from `entity` (cycle-guarded via `BuildAncestorChain`),
    /// or `entity`'s own name if it has no resolvable base.
    private static string ResolveRootName(EntityModel entity, Dictionary<string, EntityModel> byName)
    {
        if (entity.BaseEntityName is null || !byName.ContainsKey(entity.BaseEntityName))
        {
            return entity.Name;
        }

        var chain = BuildAncestorChain(entity, byName);
        return chain.Count > 0 ? chain[^1].Name : entity.Name;
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
