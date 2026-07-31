using EfSchemaVisualizer.Core.Archive;
using EfSchemaVisualizer.Core.Model;

namespace EfSchemaVisualizer.Web.Diagram;

/// <summary>
/// Maps a parsed <see cref="PropertyModel"/> to a dialect-specific SQL column type token
/// (e.g. <c>"nvarchar(100)"</c>) for <see cref="SqlDdlExporter"/>. An explicit
/// <see cref="PropertyModel.ColumnType"/> (from <c>HasColumnType</c>) always wins outright,
/// since it's already a raw SQL type string.
/// </summary>
internal static class SqlColumnTypeMapper
{
    public static string MapType(PropertyModel property, ScaffoldProvider provider)
    {
        if (!string.IsNullOrWhiteSpace(property.ColumnType))
        {
            return property.ColumnType!;
        }

        var clrType = property.IsEnumType
            ? property.EnumUnderlyingClrType ?? "int"
            : property.ClrType.TrimEnd('?');

        return provider switch
        {
            ScaffoldProvider.SqlServer => MapSqlServer(clrType, property),
            ScaffoldProvider.PostgreSql => MapPostgres(clrType, property),
            ScaffoldProvider.Sqlite => MapSqlite(clrType),
            _ => throw new ArgumentOutOfRangeException(nameof(provider)),
        };
    }

    private static string MapSqlServer(string clrType, PropertyModel property) => clrType switch
    {
        "int" => "int",
        "long" => "bigint",
        "short" => "smallint",
        "byte" => "tinyint",
        "bool" => "bit",
        "decimal" => $"decimal({property.Precision ?? 18},{property.Scale ?? 2})",
        "double" => "float",
        "float" => "real",
        "string" => SqlServerStringType(property),
        "Guid" => "uniqueidentifier",
        "DateTime" => "datetime2",
        "DateTimeOffset" => "datetimeoffset",
        "DateOnly" => "date",
        "TimeOnly" or "TimeSpan" => "time",
        "byte[]" => property.MaxLength is int n ? $"varbinary({n})" : "varbinary(max)",
        _ => "nvarchar(max)",
    };

    private static string SqlServerStringType(PropertyModel property)
    {
        var basePrefix = property.IsUnicode == false ? "" : "n";
        var kind = property.IsFixedLength == true ? "char" : "varchar";
        var length = property.MaxLength is int n ? n.ToString() : "max";
        return kind == "char" && length == "max" ? $"{basePrefix}varchar(max)" : $"{basePrefix}{kind}({length})";
    }

    private static string MapPostgres(string clrType, PropertyModel property) => clrType switch
    {
        "int" => "integer",
        "long" => "bigint",
        "short" => "smallint",
        "byte" => "smallint",
        "bool" => "boolean",
        "decimal" => property.Precision is int p ? $"numeric({p},{property.Scale ?? 0})" : "numeric",
        "double" => "double precision",
        "float" => "real",
        "string" => PostgresStringType(property),
        "Guid" => "uuid",
        "DateTime" => "timestamp",
        "DateTimeOffset" => "timestamptz",
        "DateOnly" => "date",
        "TimeOnly" or "TimeSpan" => "time",
        "byte[]" => "bytea",
        _ => "text",
    };

    private static string PostgresStringType(PropertyModel property)
    {
        if (property.MaxLength is not int n)
        {
            return "text";
        }

        return property.IsFixedLength == true ? $"char({n})" : $"varchar({n})";
    }

    private static string MapSqlite(string clrType) => clrType switch
    {
        "int" or "long" or "short" or "byte" or "bool" => "INTEGER",
        "double" or "float" => "REAL",
        "decimal" => "NUMERIC",
        "byte[]" => "BLOB",
        _ => "TEXT",
    };
}
