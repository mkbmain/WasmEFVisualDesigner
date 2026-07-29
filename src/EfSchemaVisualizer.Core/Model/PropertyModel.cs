namespace EfSchemaVisualizer.Core.Model;

public enum FoldKind
{
    None,
    Owned,
    Complex,
}

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
    string? DefaultValueSql = null,
    string? ValueGenerated = null,
    bool IsShadow = false,
    bool IsRowVersion = false,
    bool IsConcurrencyToken = false,
    string? Comment = null,
    bool? IsUnicode = null,
    bool? IsFixedLength = null,
    string? Collation = null,
    string? InverseProperty = null,
    string? DeclaringEntityName = null,
    FoldKind FoldKind = FoldKind.None,
    string? OwnerNavigationProperty = null,
    string? ComputedColumnSql = null,
    bool? ComputedColumnSqlIsStored = null,
    string? SequenceName = null,
    string? SequenceSchema = null,
    string? ConversionProviderClrType = null,
    bool? ConversionIsCustomLambda = null,
    bool IsEnumType = false,
    string? EnumUnderlyingClrType = null);
