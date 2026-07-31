using System.Text;
using EfSchemaVisualizer.Core.Archive;
using EfSchemaVisualizer.Core.Model;

namespace EfSchemaVisualizer.Web.Diagram;

/// <summary>
/// Renders a <see cref="DiagramModelResult"/> as dialect-specific SQL DDL text (SQL Server /
/// PostgreSQL / SQLite). Pure string generation from the parsed model, same posture as
/// <see cref="MermaidExporter"/> — no dependency on the live <c>BlazorDiagram</c>.
/// </summary>
public static class SqlDdlExporter
{
    internal static string QuoteIdentifier(string name, ScaffoldProvider provider) => provider switch
    {
        ScaffoldProvider.SqlServer => $"[{name}]",
        _ => $"\"{name}\"",
    };

    internal static string PhysicalTableName(EntityModel entity) => entity.TableName ?? entity.Name;

    internal static string QualifiedTableName(EntityModel entity, ScaffoldProvider provider)
    {
        var table = QuoteIdentifier(PhysicalTableName(entity), provider);

        if (provider == ScaffoldProvider.Sqlite || entity.Schema is null)
        {
            return table;
        }

        return $"{QuoteIdentifier(entity.Schema, provider)}.{table}";
    }
}
