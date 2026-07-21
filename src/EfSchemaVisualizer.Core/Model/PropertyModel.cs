namespace EfSchemaVisualizer.Core.Model;

public sealed record PropertyModel(
    string Name,
    string ClrType,
    bool IsNullable,
    int? MaxLength,
    bool? IsRequiredOverride = null,
    int? Precision = null,
    int? Scale = null,
    string? ColumnName = null,
    string? ColumnType = null,
    string? DefaultValueLiteral = null,
    string? ValueGenerated = null,
    bool IsShadow = false,
    bool IsRowVersion = false,
    bool IsConcurrencyToken = false);
