namespace EfSchemaVisualizer.Core.Merging;

public sealed record SequenceConfig(
    string Name,
    string? Schema,
    string? ClrType,
    long? StartsAt,
    int? IncrementsBy,
    long? MinValue,
    long? MaxValue,
    bool? IsCyclic);
