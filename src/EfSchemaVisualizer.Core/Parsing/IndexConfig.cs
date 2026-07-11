using System.Collections.Generic;

namespace EfSchemaVisualizer.Core.Parsing;

public sealed record IndexConfig(
    string EntityName,
    IReadOnlyList<string> PropertyNames,
    bool IsUnique,
    string? Name);
