namespace EfSchemaVisualizer.Core.Parsing;

public sealed record PrecisionConfig(string EntityName, string PropertyName, int Precision, int? Scale);
