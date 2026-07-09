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
}
