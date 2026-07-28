namespace EfSchemaVisualizer.Core.Merging;

public sealed record ComputedColumnSqlConfig(string EntityName, string PropertyName, string Sql, bool? IsStored);
