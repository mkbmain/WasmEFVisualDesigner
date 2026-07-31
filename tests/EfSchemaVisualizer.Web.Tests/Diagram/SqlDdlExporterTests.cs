using EfSchemaVisualizer.Core.Archive;
using EfSchemaVisualizer.Core.Model;
using EfSchemaVisualizer.Web.Diagram;

namespace EfSchemaVisualizer.Web.Tests.Diagram;

public class SqlDdlExporterTests
{
    private static EntityModel Entity(string name, params PropertyModel[] properties) =>
        new(name, properties);

    [Theory]
    [InlineData(ScaffoldProvider.SqlServer, "[Name]")]
    [InlineData(ScaffoldProvider.PostgreSql, "\"Name\"")]
    [InlineData(ScaffoldProvider.Sqlite, "\"Name\"")]
    public void QuoteIdentifier_QuotesPerDialect(ScaffoldProvider provider, string expected)
    {
        Assert.Equal(expected, SqlDdlExporter.QuoteIdentifier("Name", provider));
    }

    [Fact]
    public void PhysicalTableName_UsesExplicitTableNameWhenSet()
    {
        var entity = Entity("Blog") with { TableName = "Blogs" };
        Assert.Equal("Blogs", SqlDdlExporter.PhysicalTableName(entity));
    }

    [Fact]
    public void PhysicalTableName_FallsBackToEntityNameWhenTableNameNotSet()
    {
        Assert.Equal("Blog", SqlDdlExporter.PhysicalTableName(Entity("Blog")));
    }

    [Theory]
    [InlineData(ScaffoldProvider.SqlServer, "[sales].[Order]")]
    [InlineData(ScaffoldProvider.PostgreSql, "\"sales\".\"Order\"")]
    public void QualifiedTableName_WithSchema_QualifiesPerDialect(ScaffoldProvider provider, string expected)
    {
        var entity = Entity("Order") with { Schema = "sales" };
        Assert.Equal(expected, SqlDdlExporter.QualifiedTableName(entity, provider));
    }

    [Fact]
    public void QualifiedTableName_Sqlite_NeverQualifiesWithSchema()
    {
        var entity = Entity("Order") with { Schema = "sales" };
        Assert.Equal("\"Order\"", SqlDdlExporter.QualifiedTableName(entity, ScaffoldProvider.Sqlite));
    }

    [Theory]
    [InlineData(ScaffoldProvider.SqlServer, "[Order]")]
    [InlineData(ScaffoldProvider.PostgreSql, "\"Order\"")]
    public void QualifiedTableName_WithNoSchema_OmitsSchemaPrefix(ScaffoldProvider provider, string expected)
    {
        Assert.Equal(expected, SqlDdlExporter.QualifiedTableName(Entity("Order"), provider));
    }
}
