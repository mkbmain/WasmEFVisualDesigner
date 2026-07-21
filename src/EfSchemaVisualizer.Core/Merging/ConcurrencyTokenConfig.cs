namespace EfSchemaVisualizer.Core.Merging;

public sealed record ConcurrencyTokenConfig(
    string EntityName, string PropertyName, bool IsRowVersion, bool IsConcurrencyToken);
