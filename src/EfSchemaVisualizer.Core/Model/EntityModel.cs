using System.Collections.Generic;

namespace EfSchemaVisualizer.Core.Model;

public sealed record EntityModel(
    string Name,
    IReadOnlyList<PropertyModel> Properties,
    IReadOnlyList<string>? KeyPropertyNames = null,
    IReadOnlyList<IndexModel>? Indexes = null,
    string? TableName = null,
    string? Schema = null,
    bool IsKeyless = false,
    bool IsKeyInferred = false,
    string? ViewName = null,
    string? SqlQuery = null,
    IReadOnlyList<IReadOnlyList<string>>? AlternateKeys = null,
    bool HasQueryFilter = false,
    string? Comment = null,
    bool IsJson = false,
    string? JsonColumnName = null,
    bool IsTemporal = false,
    IReadOnlyList<string>? SplitTables = null,
    string? BaseEntityName = null,
    bool IsOwned = false,
    string? KeyName = null,
    IReadOnlyList<CheckConstraintModel>? CheckConstraints = null,
    MappingStrategy MappingStrategy = MappingStrategy.Tph,
    string? DiscriminatorPropertyName = null,
    string? DiscriminatorClrType = null,
    string? DiscriminatorValue = null,
    bool IsSharedType = false,
    string? FunctionName = null,
    IReadOnlyList<string>? PartitionKeyPropertyNames = null,
    IReadOnlyList<AnnotationModel>? Annotations = null)
{
    public IReadOnlyList<string> KeyPropertyNames { get; init; } = KeyPropertyNames ?? new List<string>();
    public IReadOnlyList<IndexModel> Indexes { get; init; } = Indexes ?? new List<IndexModel>();
    public IReadOnlyList<IReadOnlyList<string>> AlternateKeys { get; init; } = AlternateKeys ?? new List<IReadOnlyList<string>>();
    public IReadOnlyList<string> SplitTables { get; init; } = SplitTables ?? new List<string>();
    public IReadOnlyList<CheckConstraintModel> CheckConstraints { get; init; } = CheckConstraints ?? new List<CheckConstraintModel>();
    public IReadOnlyList<string> PartitionKeyPropertyNames { get; init; } = PartitionKeyPropertyNames ?? new List<string>();
    public IReadOnlyList<AnnotationModel> Annotations { get; init; } = Annotations ?? new List<AnnotationModel>();
}
