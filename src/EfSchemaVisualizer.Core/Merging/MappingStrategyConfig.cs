using EfSchemaVisualizer.Core.Model;

namespace EfSchemaVisualizer.Core.Merging;

public sealed record MappingStrategyConfig(string EntityName, MappingStrategy Strategy);
