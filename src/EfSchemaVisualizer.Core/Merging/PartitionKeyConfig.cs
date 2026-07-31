using System.Collections.Generic;

namespace EfSchemaVisualizer.Core.Merging;

public sealed record PartitionKeyConfig(string EntityName, IReadOnlyList<string> PropertyNames);
