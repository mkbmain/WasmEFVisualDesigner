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
}
