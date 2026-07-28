namespace EfSchemaVisualizer.Core.Model;

public sealed record SequenceModel(
    string Name,
    string? Schema,
    string? ClrType,
    long? StartsAt,
    int? IncrementsBy,
    long? MinValue,
    long? MaxValue,
    bool? IsCyclic);
