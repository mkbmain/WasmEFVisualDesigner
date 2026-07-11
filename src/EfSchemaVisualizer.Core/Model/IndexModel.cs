using System.Collections.Generic;

namespace EfSchemaVisualizer.Core.Model;

public sealed record IndexModel(
    IReadOnlyList<string> PropertyNames,
    bool IsUnique,
    string? Name = null);
