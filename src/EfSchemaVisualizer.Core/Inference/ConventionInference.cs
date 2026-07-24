using System;
using System.Collections.Generic;
using System.Linq;
using EfSchemaVisualizer.Core.Model;

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
}
