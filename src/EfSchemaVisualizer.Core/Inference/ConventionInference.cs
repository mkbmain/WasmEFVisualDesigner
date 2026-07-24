using System;
using System.Collections.Generic;
using System.Linq;
using EfSchemaVisualizer.Core.Model;
using EfSchemaVisualizer.Core.Parsing;

namespace EfSchemaVisualizer.Core.Inference;

public static class ConventionInference
{
    public static EntityModel InferKey(EntityModel entity)
    {
        if (entity.KeyPropertyNames.Count > 0 || entity.IsKeyless)
        {
            return entity;
        }

        var idProperty = entity.Properties
            .FirstOrDefault(p => string.Equals(p.Name, "Id", StringComparison.OrdinalIgnoreCase));

        var typeIdName = entity.Name + "Id";
        var typeIdProperty = entity.Properties
            .FirstOrDefault(p => string.Equals(p.Name, typeIdName, StringComparison.OrdinalIgnoreCase));

        var keyProperty = idProperty ?? typeIdProperty;
        if (keyProperty is null)
        {
            return entity;
        }

        return entity with { KeyPropertyNames = new List<string> { keyProperty.Name }, IsKeyInferred = true };
    }

    public static IReadOnlyList<RelationshipModel> InferRelationships(IReadOnlyList<EntityModel> entities)
    {
        var entitiesByName = entities.ToDictionary(e => e.Name);
        var results = new List<RelationshipModel>();

        foreach (var dependent in entities)
        {
            foreach (var property in dependent.Properties)
            {
                if (!entitiesByName.TryGetValue(property.ClrType, out var principal))
                {
                    continue;
                }

                var fkProperty = FindForeignKeyProperty(dependent, property.Name, principal.Name);
                if (fkProperty is null)
                {
                    continue;
                }

                var (kind, principalNavigation) = FindPrincipalBackReference(principal, dependent.Name, property);

                results.Add(new RelationshipModel(
                    principal.Name,
                    dependent.Name,
                    kind,
                    principalNavigation,
                    property.Name,
                    new List<string> { fkProperty.Name },
                    IsInferred: true));
            }
        }

        return results
            .GroupBy(r => (r.DependentEntity, Fk: string.Join(",", r.ForeignKeyProperties)))
            .Select(g => g.First())
            .ToList();
    }

    private static PropertyModel? FindForeignKeyProperty(
        EntityModel dependent, string navigationPropertyName, string principalTypeName)
    {
        var byNavName = dependent.Properties.FirstOrDefault(p =>
            string.Equals(p.Name, navigationPropertyName + "Id", StringComparison.OrdinalIgnoreCase));
        if (byNavName is not null)
        {
            return byNavName;
        }

        if (string.Equals(navigationPropertyName, principalTypeName, StringComparison.Ordinal))
        {
            return null;
        }

        return dependent.Properties.FirstOrDefault(p =>
            string.Equals(p.Name, principalTypeName + "Id", StringComparison.OrdinalIgnoreCase));
    }

    private static (RelationshipKind Kind, string? PrincipalNavigation) FindPrincipalBackReference(
        EntityModel principalEntity, string dependentEntityName, PropertyModel navigationProperty)
    {
        foreach (var property in principalEntity.Properties)
        {
            if (property == navigationProperty)
            {
                continue;
            }

            var elementTypeName = FluentSyntaxHelpers.TryGetElementTypeName(property.ClrType);

            if (elementTypeName != dependentEntityName)
            {
                continue;
            }

            var isCollection = elementTypeName != property.ClrType;
            return isCollection
                ? (RelationshipKind.OneToMany, property.Name)
                : (RelationshipKind.OneToOne, property.Name);
        }

        return (RelationshipKind.OneToMany, null);
    }
}
