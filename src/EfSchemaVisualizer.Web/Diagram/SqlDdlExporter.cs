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

    internal static string RenderCreateTable(EntityModel entity, IReadOnlyList<string> primaryKeyColumnNames, ScaffoldProvider provider)
    {
        var sb = new StringBuilder();
        sb.Append("CREATE TABLE ").Append(QualifiedTableName(entity, provider)).Append(" (\n");

        var sqliteInlineAutoIncrement =
            provider == ScaffoldProvider.Sqlite &&
            primaryKeyColumnNames.Count == 1 &&
            entity.Properties.FirstOrDefault(p => p.Name == primaryKeyColumnNames[0]) is { } solePk &&
            IsIdentityCandidate(solePk);

        var lines = new List<string>();

        foreach (var property in entity.Properties)
        {
            var isSolePk = primaryKeyColumnNames.Count == 1 && property.Name == primaryKeyColumnNames[0];

            if (sqliteInlineAutoIncrement && isSolePk)
            {
                var name = QuoteIdentifier(property.ColumnName ?? property.Name, provider);
                lines.Add($"    {name} INTEGER PRIMARY KEY AUTOINCREMENT");
                continue;
            }

            var isIdentityColumn = isSolePk && provider != ScaffoldProvider.Sqlite && IsIdentityCandidate(property);
            lines.Add("    " + RenderColumnDefinition(property, isIdentityColumn, provider));
        }

        if (!entity.IsKeyless && primaryKeyColumnNames.Count > 0 && !sqliteInlineAutoIncrement)
        {
            var keyName = entity.KeyName ?? $"PK_{PhysicalTableName(entity)}";
            var columns = string.Join(", ", primaryKeyColumnNames.Select(c => QuoteIdentifier(c, provider)));
            lines.Add($"    CONSTRAINT {QuoteIdentifier(keyName, provider)} PRIMARY KEY ({columns})");
        }

        foreach (var check in entity.CheckConstraints)
        {
            lines.Add($"    CONSTRAINT {QuoteIdentifier(check.Name, provider)} CHECK ({check.Sql})");
        }

        foreach (var alternateKey in entity.AlternateKeys)
        {
            var akName = $"AK_{PhysicalTableName(entity)}_{string.Join("_", alternateKey)}";
            var columns = string.Join(", ", alternateKey.Select(c => QuoteIdentifier(c, provider)));
            lines.Add($"    CONSTRAINT {QuoteIdentifier(akName, provider)} UNIQUE ({columns})");
        }

        sb.Append(string.Join(",\n", lines));
        sb.Append("\n);\n");
        return sb.ToString();
    }

    internal static List<EntityModel> SelectPhysicalEntities(IReadOnlyList<EntityModel> entities) =>
        entities.Where(e => e.ViewName is null && e.FunctionName is null).ToList();

    internal static List<EntityModel> OrderTablesByDependency(
        IReadOnlyList<EntityModel> physicalEntities, IReadOnlyList<RelationshipModel> relationships)
    {
        var layers = DiagramAutoLayout.ComputeLayers(physicalEntities, relationships);

        return physicalEntities
            .Select((entity, index) => (entity, index))
            .OrderBy(t => layers.GetValueOrDefault(t.entity.Name, 0))
            .ThenBy(t => t.index)
            .Select(t => t.entity)
            .ToList();
    }
}
