using System;
using System.Collections.Generic;
using System.Linq;
using EfSchemaVisualizer.Core.Model;

namespace EfSchemaVisualizer.Core.Merging;

public static class ModelMerger
{
    public static EntityModel ApplyMaxLengths(EntityModel entity, IReadOnlyList<MaxLengthConfig> configs)
    {
        var byProperty = IndexByProperty(entity.Name, configs, c => c.EntityName, c => c.PropertyName);

        var updatedProperties = entity.Properties
            .Select(property => byProperty.TryGetValue(property.Name, out var config)
                ? property with { MaxLength = config.MaxLength }
                : property)
            .ToList();

        return entity with { Properties = updatedProperties };
    }

    public static EntityModel ApplyIsRequired(EntityModel entity, IReadOnlyList<IsRequiredConfig> configs)
    {
        var byProperty = IndexByProperty(entity.Name, configs, c => c.EntityName, c => c.PropertyName);

        var updatedProperties = entity.Properties
            .Select(property => byProperty.TryGetValue(property.Name, out var config)
                ? property with { IsRequiredOverride = config.IsRequired }
                : property)
            .ToList();

        return entity with { Properties = updatedProperties };
    }

    public static EntityModel ApplyPrecisions(EntityModel entity, IReadOnlyList<PrecisionConfig> configs)
    {
        var byProperty = IndexByProperty(entity.Name, configs, c => c.EntityName, c => c.PropertyName);

        var updatedProperties = entity.Properties
            .Select(property => byProperty.TryGetValue(property.Name, out var config)
                ? property with { Precision = config.Precision, Scale = config.Scale }
                : property)
            .ToList();

        return entity with { Properties = updatedProperties };
    }

    public static EntityModel ApplyKeys(EntityModel entity, IReadOnlyList<KeyConfig> configs)
    {
        var config = configs.FirstOrDefault(c => c.EntityName == entity.Name);

        return config is null ? entity : entity with { KeyPropertyNames = config.PropertyNames };
    }

    public static EntityModel ApplyIndexes(EntityModel entity, IReadOnlyList<IndexConfig> configs)
    {
        var indexes = configs
            .Where(c => c.EntityName == entity.Name)
            .Select(c => new IndexModel(c.PropertyNames, c.IsUnique, c.Name))
            .ToList();

        return entity with { Indexes = indexes };
    }

    public static EntityModel ApplyTableMapping(EntityModel entity, IReadOnlyList<TableConfig> configs)
    {
        var config = configs.FirstOrDefault(c => c.EntityName == entity.Name);

        return config is null ? entity : entity with { TableName = config.TableName, Schema = config.Schema };
    }

    public static EntityModel ApplyColumnNames(EntityModel entity, IReadOnlyList<ColumnNameConfig> configs)
    {
        var byProperty = IndexByProperty(entity.Name, configs, c => c.EntityName, c => c.PropertyName);

        var updatedProperties = entity.Properties
            .Select(property => byProperty.TryGetValue(property.Name, out var config)
                ? property with { ColumnName = config.ColumnName }
                : property)
            .ToList();

        return entity with { Properties = updatedProperties };
    }

    public static EntityModel ApplyColumnTypes(EntityModel entity, IReadOnlyList<ColumnTypeConfig> configs)
    {
        var byProperty = IndexByProperty(entity.Name, configs, c => c.EntityName, c => c.PropertyName);

        var updatedProperties = entity.Properties
            .Select(property => byProperty.TryGetValue(property.Name, out var config)
                ? property with { ColumnType = config.ColumnType }
                : property)
            .ToList();

        return entity with { Properties = updatedProperties };
    }

    public static EntityModel ApplyDefaultValues(EntityModel entity, IReadOnlyList<DefaultValueConfig> configs)
    {
        var byProperty = IndexByProperty(entity.Name, configs, c => c.EntityName, c => c.PropertyName);

        var updatedProperties = entity.Properties
            .Select(property => byProperty.TryGetValue(property.Name, out var config)
                ? property with { DefaultValueLiteral = config.LiteralText }
                : property)
            .ToList();

        return entity with { Properties = updatedProperties };
    }

    public static EntityModel ApplyIgnoredProperties(EntityModel entity, IReadOnlyList<IgnoreConfig> configs)
    {
        var ignoredNames = configs
            .Where(c => c.EntityName == entity.Name)
            .Select(c => c.PropertyName)
            .ToHashSet();

        if (ignoredNames.Count == 0)
        {
            return entity;
        }

        var updatedProperties = entity.Properties
            .Where(property => !ignoredNames.Contains(property.Name))
            .ToList();

        return entity with { Properties = updatedProperties };
    }

    public static EntityModel ApplyValueGeneration(EntityModel entity, IReadOnlyList<ValueGenerationConfig> configs)
    {
        var byProperty = IndexByProperty(entity.Name, configs, c => c.EntityName, c => c.PropertyName);

        var updatedProperties = entity.Properties
            .Select(property => byProperty.TryGetValue(property.Name, out var config)
                ? property with { ValueGenerated = config.Mode }
                : property)
            .ToList();

        return entity with { Properties = updatedProperties };
    }

    public static EntityModel ApplyShadowProperties(EntityModel entity, IReadOnlyList<ShadowPropertyConfig> configs)
    {
        var existingNames = entity.Properties.Select(p => p.Name).ToHashSet();

        var shadowProperties = configs
            .Where(c => c.EntityName == entity.Name && !existingNames.Contains(c.PropertyName))
            .Select(c => new PropertyModel(c.PropertyName, c.ClrType, IsNullable: true, MaxLength: null, IsShadow: true))
            .ToList();

        if (shadowProperties.Count == 0)
        {
            return entity;
        }

        return entity with { Properties = entity.Properties.Concat(shadowProperties).ToList() };
    }

    /// Builds a property-name-keyed lookup of the configs belonging to `entityName`, in a single
    /// pass over `configs`. Where a property has more than one matching config, the first one
    /// (in list order) wins, matching the `FirstOrDefault` semantics this replaces.
    private static Dictionary<string, TConfig> IndexByProperty<TConfig>(
        string entityName,
        IReadOnlyList<TConfig> configs,
        Func<TConfig, string> entityNameSelector,
        Func<TConfig, string> propertyNameSelector)
    {
        var byProperty = new Dictionary<string, TConfig>();

        foreach (var config in configs)
        {
            if (entityNameSelector(config) != entityName)
            {
                continue;
            }

            var propertyName = propertyNameSelector(config);
            if (!byProperty.ContainsKey(propertyName))
            {
                byProperty[propertyName] = config;
            }
        }

        return byProperty;
    }

    public static IReadOnlyList<RelationshipModel> ApplyRelationships(IReadOnlyList<RelationshipConfig> configs)
    {
        return configs
            .Select(c => new RelationshipModel(
                c.PrincipalEntity,
                c.DependentEntity,
                c.Kind,
                c.PrincipalNavigation,
                c.DependentNavigation,
                c.ForeignKeyProperties,
                c.OnDeleteBehavior,
                c.JoinEntityName))
            .ToList();
    }
}
