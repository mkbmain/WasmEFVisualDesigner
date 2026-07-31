using System.Collections.Generic;
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

    private static readonly HashSet<string> IntegerClrTypes = new() { "int", "long", "short", "byte" };

    internal static bool IsIdentityCandidate(PropertyModel property) =>
        property.ValueGenerated != "Never" && IntegerClrTypes.Contains(property.ClrType.TrimEnd('?'));

    internal static string RenderColumnDefinition(PropertyModel property, bool isSoleIntegerIdentityPrimaryKey, ScaffoldProvider provider)
    {
        var name = QuoteIdentifier(property.ColumnName ?? property.Name, provider);
        var sqlType = SqlColumnTypeMapper.MapType(property, provider);
        var nullability = property.IsNullable && property.IsRequiredOverride != true ? "NULL" : "NOT NULL";

        if (property.ComputedColumnSql is not null)
        {
            return RenderComputedColumn(name, sqlType, nullability, property, provider);
        }

        var identity = isSoleIntegerIdentityPrimaryKey ? IdentityClause(provider) : "";
        var defaultClause = RenderDefaultClause(property);

        return $"{name} {sqlType}{identity} {nullability}{defaultClause}";
    }

    private static string RenderComputedColumn(string name, string sqlType, string nullability, PropertyModel property, ScaffoldProvider provider)
    {
        return provider switch
        {
            ScaffoldProvider.SqlServer => property.ComputedColumnSqlIsStored != false
                ? $"{name} {sqlType} {nullability} AS ({property.ComputedColumnSql}) PERSISTED"
                : $"{name} {sqlType} {nullability} AS ({property.ComputedColumnSql})",
            _ => $"{name} {sqlType} GENERATED ALWAYS AS ({property.ComputedColumnSql}) STORED",
        };
    }

    private static string IdentityClause(ScaffoldProvider provider) => provider switch
    {
        ScaffoldProvider.SqlServer => " IDENTITY(1,1)",
        ScaffoldProvider.PostgreSql => " GENERATED ALWAYS AS IDENTITY",
        _ => "",
    };

    private static string RenderDefaultClause(PropertyModel property)
    {
        if (property.DefaultValueLiteral is not null)
        {
            return $" DEFAULT {property.DefaultValueLiteral}";
        }

        if (property.DefaultValueSql is not null)
        {
            return $" DEFAULT ({property.DefaultValueSql})";
        }

        return "";
    }
}
