using System.Collections.Generic;

namespace EfSchemaVisualizer.Core.Parsing;

public sealed record KeyConfig(string EntityName, IReadOnlyList<string> PropertyNames);
