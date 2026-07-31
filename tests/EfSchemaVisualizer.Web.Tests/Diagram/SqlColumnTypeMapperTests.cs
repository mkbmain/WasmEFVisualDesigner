using EfSchemaVisualizer.Core.Archive;
using EfSchemaVisualizer.Core.Model;
using EfSchemaVisualizer.Web.Diagram;

namespace EfSchemaVisualizer.Web.Tests.Diagram;

public class SqlColumnTypeMapperTests
{
    private static PropertyModel Prop(
        string clrType,
        int? maxLength = null,
        int? precision = null,
        int? scale = null,
        bool? isUnicode = null,
        bool? isFixedLength = null,
        string? columnType = null,
        bool isEnumType = false,
        string? enumUnderlyingClrType = null) =>
        new("Value", clrType, IsNullable: true, maxLength,
            ColumnType: columnType, Precision: precision, Scale: scale,
            IsUnicode: isUnicode, IsFixedLength: isFixedLength,
            IsEnumType: isEnumType, EnumUnderlyingClrType: enumUnderlyingClrType);

    [Theory]
    [InlineData("int", ScaffoldProvider.SqlServer, "int")]
    [InlineData("int", ScaffoldProvider.PostgreSql, "integer")]
    [InlineData("int", ScaffoldProvider.Sqlite, "INTEGER")]
    [InlineData("long", ScaffoldProvider.SqlServer, "bigint")]
    [InlineData("long", ScaffoldProvider.PostgreSql, "bigint")]
    [InlineData("long", ScaffoldProvider.Sqlite, "INTEGER")]
    [InlineData("short", ScaffoldProvider.SqlServer, "smallint")]
    [InlineData("short", ScaffoldProvider.PostgreSql, "smallint")]
    [InlineData("short", ScaffoldProvider.Sqlite, "INTEGER")]
    [InlineData("byte", ScaffoldProvider.SqlServer, "tinyint")]
    [InlineData("byte", ScaffoldProvider.PostgreSql, "smallint")]
    [InlineData("byte", ScaffoldProvider.Sqlite, "INTEGER")]
    [InlineData("bool", ScaffoldProvider.SqlServer, "bit")]
    [InlineData("bool", ScaffoldProvider.PostgreSql, "boolean")]
    [InlineData("bool", ScaffoldProvider.Sqlite, "INTEGER")]
    [InlineData("double", ScaffoldProvider.SqlServer, "float")]
    [InlineData("double", ScaffoldProvider.PostgreSql, "double precision")]
    [InlineData("double", ScaffoldProvider.Sqlite, "REAL")]
    [InlineData("float", ScaffoldProvider.SqlServer, "real")]
    [InlineData("float", ScaffoldProvider.PostgreSql, "real")]
    [InlineData("float", ScaffoldProvider.Sqlite, "REAL")]
    [InlineData("Guid", ScaffoldProvider.SqlServer, "uniqueidentifier")]
    [InlineData("Guid", ScaffoldProvider.PostgreSql, "uuid")]
    [InlineData("Guid", ScaffoldProvider.Sqlite, "TEXT")]
    [InlineData("DateTime", ScaffoldProvider.SqlServer, "datetime2")]
    [InlineData("DateTime", ScaffoldProvider.PostgreSql, "timestamp")]
    [InlineData("DateTime", ScaffoldProvider.Sqlite, "TEXT")]
    [InlineData("DateTimeOffset", ScaffoldProvider.SqlServer, "datetimeoffset")]
    [InlineData("DateTimeOffset", ScaffoldProvider.PostgreSql, "timestamptz")]
    [InlineData("DateTimeOffset", ScaffoldProvider.Sqlite, "TEXT")]
    [InlineData("DateOnly", ScaffoldProvider.SqlServer, "date")]
    [InlineData("DateOnly", ScaffoldProvider.PostgreSql, "date")]
    [InlineData("DateOnly", ScaffoldProvider.Sqlite, "TEXT")]
    [InlineData("TimeOnly", ScaffoldProvider.SqlServer, "time")]
    [InlineData("TimeSpan", ScaffoldProvider.PostgreSql, "time")]
    public void MapType_SimpleClrTypes_MapsToExpectedSqlType(string clrType, ScaffoldProvider provider, string expected)
    {
        Assert.Equal(expected, SqlColumnTypeMapper.MapType(Prop(clrType), provider));
    }

    [Theory]
    [InlineData(ScaffoldProvider.SqlServer, "decimal(18,2)")]
    [InlineData(ScaffoldProvider.PostgreSql, "numeric")]
    [InlineData(ScaffoldProvider.Sqlite, "NUMERIC")]
    public void MapType_DecimalWithNoPrecisionOrScale_UsesDialectDefault(ScaffoldProvider provider, string expected)
    {
        Assert.Equal(expected, SqlColumnTypeMapper.MapType(Prop("decimal"), provider));
    }

    [Theory]
    [InlineData(ScaffoldProvider.SqlServer, "decimal(10,3)")]
    [InlineData(ScaffoldProvider.PostgreSql, "numeric(10,3)")]
    [InlineData(ScaffoldProvider.Sqlite, "NUMERIC")]
    public void MapType_DecimalWithExplicitPrecisionAndScale_UsesThem(ScaffoldProvider provider, string expected)
    {
        Assert.Equal(expected, SqlColumnTypeMapper.MapType(Prop("decimal", precision: 10, scale: 3), provider));
    }

    [Fact]
    public void MapType_StringWithNoMaxLength_SqlServer_UsesNvarcharMax()
    {
        Assert.Equal("nvarchar(max)", SqlColumnTypeMapper.MapType(Prop("string"), ScaffoldProvider.SqlServer));
    }

    [Fact]
    public void MapType_StringWithMaxLength_SqlServer_UsesBoundedNvarchar()
    {
        Assert.Equal("nvarchar(100)", SqlColumnTypeMapper.MapType(Prop("string", maxLength: 100), ScaffoldProvider.SqlServer));
    }

    [Fact]
    public void MapType_StringWithIsUnicodeFalse_SqlServer_UsesVarchar()
    {
        Assert.Equal("varchar(50)", SqlColumnTypeMapper.MapType(Prop("string", maxLength: 50, isUnicode: false), ScaffoldProvider.SqlServer));
    }

    [Fact]
    public void MapType_StringWithIsFixedLengthTrue_SqlServer_UsesNchar()
    {
        Assert.Equal("nchar(10)", SqlColumnTypeMapper.MapType(Prop("string", maxLength: 10, isFixedLength: true), ScaffoldProvider.SqlServer));
    }

    [Fact]
    public void MapType_StringWithIsUnicodeFalseAndIsFixedLengthTrue_SqlServer_UsesChar()
    {
        Assert.Equal("char(10)", SqlColumnTypeMapper.MapType(
            Prop("string", maxLength: 10, isUnicode: false, isFixedLength: true), ScaffoldProvider.SqlServer));
    }

    [Fact]
    public void MapType_StringWithNoMaxLength_Postgres_UsesText()
    {
        Assert.Equal("text", SqlColumnTypeMapper.MapType(Prop("string"), ScaffoldProvider.PostgreSql));
    }

    [Fact]
    public void MapType_StringWithMaxLength_Postgres_UsesVarchar()
    {
        Assert.Equal("varchar(100)", SqlColumnTypeMapper.MapType(Prop("string", maxLength: 100), ScaffoldProvider.PostgreSql));
    }

    [Fact]
    public void MapType_StringWithMaxLengthAndFixedLength_Postgres_UsesChar()
    {
        Assert.Equal("char(10)", SqlColumnTypeMapper.MapType(Prop("string", maxLength: 10, isFixedLength: true), ScaffoldProvider.PostgreSql));
    }

    [Fact]
    public void MapType_String_Sqlite_AlwaysText()
    {
        Assert.Equal("TEXT", SqlColumnTypeMapper.MapType(Prop("string", maxLength: 100), ScaffoldProvider.Sqlite));
    }

    [Theory]
    [InlineData(ScaffoldProvider.SqlServer, "varbinary(max)")]
    [InlineData(ScaffoldProvider.PostgreSql, "bytea")]
    [InlineData(ScaffoldProvider.Sqlite, "BLOB")]
    public void MapType_ByteArrayWithNoMaxLength_UsesDialectDefault(ScaffoldProvider provider, string expected)
    {
        Assert.Equal(expected, SqlColumnTypeMapper.MapType(Prop("byte[]"), provider));
    }

    [Fact]
    public void MapType_ByteArrayWithMaxLength_SqlServer_UsesBoundedVarbinary()
    {
        Assert.Equal("varbinary(16)", SqlColumnTypeMapper.MapType(Prop("byte[]", maxLength: 16), ScaffoldProvider.SqlServer));
    }

    [Fact]
    public void MapType_ExplicitColumnType_WinsOverEverything()
    {
        Assert.Equal("money", SqlColumnTypeMapper.MapType(Prop("decimal", columnType: "money"), ScaffoldProvider.SqlServer));
    }

    [Fact]
    public void MapType_EnumTypeWithNoConversion_UsesUnderlyingClrType()
    {
        Assert.Equal("tinyint", SqlColumnTypeMapper.MapType(
            Prop("Status", isEnumType: true, enumUnderlyingClrType: "byte"), ScaffoldProvider.SqlServer));
    }

    [Fact]
    public void MapType_UnrecognizedClrType_FallsBackToWidestStringType()
    {
        Assert.Equal("nvarchar(max)", SqlColumnTypeMapper.MapType(Prop("SomeUnknownType"), ScaffoldProvider.SqlServer));
        Assert.Equal("text", SqlColumnTypeMapper.MapType(Prop("SomeUnknownType"), ScaffoldProvider.PostgreSql));
        Assert.Equal("TEXT", SqlColumnTypeMapper.MapType(Prop("SomeUnknownType"), ScaffoldProvider.Sqlite));
    }
}
