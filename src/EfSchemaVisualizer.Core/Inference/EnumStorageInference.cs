using System.Collections.Generic;
using System.Linq;
using EfSchemaVisualizer.Core.Model;

namespace EfSchemaVisualizer.Core.Inference;

public static class EnumStorageInference
{
    public static IReadOnlyList<EntityModel> Fold(IReadOnlyList<EntityModel> entities, IReadOnlyDictionary<string, string> enumUnderlyingTypes)
    {
        return entities.Select(entity => Fold(entity, enumUnderlyingTypes)).ToList();
    }

    private static EntityModel Fold(EntityModel entity, IReadOnlyDictionary<string, string> enumUnderlyingTypes)
    {
        var updatedProperties = entity.Properties
            .Select(property => enumUnderlyingTypes.TryGetValue(property.ClrType, out var underlyingType)
                ? property with { IsEnumType = true, EnumUnderlyingClrType = underlyingType }
                : property)
            .ToList();

        return entity with { Properties = updatedProperties };
    }
}
