namespace EfSchemaVisualizer.Core.Merging;

public sealed record ValueConversionConfig(string EntityName, string PropertyName, string? ProviderClrType, bool IsCustomLambda);
