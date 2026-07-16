namespace EfSchemaVisualizer.Core.Merging;

public sealed record PrecisionConfig(string EntityName, string PropertyName, int Precision, int? Scale);
