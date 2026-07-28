namespace EfSchemaVisualizer.Core.Merging;

public sealed record UseSequenceConfig(string EntityName, string PropertyName, string SequenceName, string? Schema);
