using System.Collections.Generic;

namespace EfSchemaVisualizer.Core.Merging;

public sealed record IndexConfig(
    string EntityName,
    IReadOnlyList<string> PropertyNames,
    bool IsUnique,
    string? Name);
