using System.Collections.Generic;

namespace EfSchemaVisualizer.Core.Merging;

public sealed record AlternateKeyConfig(string EntityName, IReadOnlyList<string> PropertyNames);
