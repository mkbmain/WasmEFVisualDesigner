using System.Collections.Generic;

namespace EfSchemaVisualizer.Core.Merging;

public sealed record IndexConfig(
    string EntityName,
    IReadOnlyList<string> PropertyNames,
    bool IsUnique,
    string? Name,
    string? Filter = null,
    IReadOnlyList<bool>? IsDescending = null,
    IReadOnlyList<string>? IncludeProperties = null);
