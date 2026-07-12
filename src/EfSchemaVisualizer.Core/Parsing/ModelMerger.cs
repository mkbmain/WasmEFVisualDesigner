using System.Collections.Generic;
using System.Linq;
using EfSchemaVisualizer.Core.Model;

namespace EfSchemaVisualizer.Core.Parsing;

public static class ModelMerger
{
    public static EntityModel ApplyMaxLengths(EntityModel entity, IReadOnlyList<MaxLengthConfig> configs)
    {
        var updatedProperties = entity.Properties
            .Select(property =>
            {
                var config = configs.FirstOrDefault(c =>
                    c.EntityName == entity.Name && c.PropertyName == property.Name);

                return config is null ? property : property with { MaxLength = config.MaxLength };
            })
            .ToList();

        return entity with { Properties = updatedProperties };
    }

    public static EntityModel ApplyIsRequired(EntityModel entity, IReadOnlyList<IsRequiredConfig> configs)
    {
        var updatedProperties = entity.Properties
            .Select(property =>
            {
                var config = configs.FirstOrDefault(c =>
                    c.EntityName == entity.Name && c.PropertyName == property.Name);

                return config is null ? property : property with { IsRequiredOverride = config.IsRequired };
            })
            .ToList();

        return entity with { Properties = updatedProperties };
    }

    public static EntityModel ApplyPrecisions(EntityModel entity, IReadOnlyList<PrecisionConfig> configs)
    {
        var updatedProperties = entity.Properties
            .Select(property =>
            {
                var config = configs.FirstOrDefault(c =>
                    c.EntityName == entity.Name && c.PropertyName == property.Name);

                return config is null ? property : property with { Precision = config.Precision, Scale = config.Scale };
            })
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
        var updatedProperties = entity.Properties
            .Select(property =>
            {
                var config = configs.FirstOrDefault(c =>
                    c.EntityName == entity.Name && c.PropertyName == property.Name);

                return config is null ? property : property with { ColumnName = config.ColumnName };
            })
            .ToList();

        return entity with { Properties = updatedProperties };
    }

    public static EntityModel ApplyColumnTypes(EntityModel entity, IReadOnlyList<ColumnTypeConfig> configs)
    {
        var updatedProperties = entity.Properties
            .Select(property =>
            {
                var config = configs.FirstOrDefault(c =>
                    c.EntityName == entity.Name && c.PropertyName == property.Name);

                return config is null ? property : property with { ColumnType = config.ColumnType };
            })
            .ToList();

        return entity with { Properties = updatedProperties };
    }

    public static EntityModel ApplyDefaultValues(EntityModel entity, IReadOnlyList<DefaultValueConfig> configs)
    {
        var updatedProperties = entity.Properties
            .Select(property =>
            {
                var config = configs.FirstOrDefault(c =>
                    c.EntityName == entity.Name && c.PropertyName == property.Name);

                return config is null ? property : property with { DefaultValueLiteral = config.LiteralText };
            })
            .ToList();

        return entity with { Properties = updatedProperties };
    }
}
