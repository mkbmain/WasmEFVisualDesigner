using System.Collections.Generic;

namespace EfSchemaVisualizer.Core.Merging;

public sealed record KeyConfig(string EntityName, IReadOnlyList<string> PropertyNames);
