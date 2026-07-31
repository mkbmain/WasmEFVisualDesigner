using EfSchemaVisualizer.Core.Archive;
using EfSchemaVisualizer.Core.Model;
using EfSchemaVisualizer.Web.Diagram;
using static EfSchemaVisualizer.Core.Model.RelationshipKind;

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

    private static PropertyModel Column(
        string name,
        string clrType = "string",
        bool isNullable = true,
        int? maxLength = null,
        string? defaultValueLiteral = null,
        string? defaultValueSql = null,
        string? computedColumnSql = null,
        bool? computedColumnSqlIsStored = null,
        string? valueGenerated = null) =>
        new(name, clrType, isNullable, maxLength,
            DefaultValueLiteral: defaultValueLiteral, DefaultValueSql: defaultValueSql,
            ComputedColumnSql: computedColumnSql, ComputedColumnSqlIsStored: computedColumnSqlIsStored,
            ValueGenerated: valueGenerated);

    [Fact]
    public void RenderColumnDefinition_NullableProperty_EmitsNull()
    {
        var line = SqlDdlExporter.RenderColumnDefinition(Column("Nickname", isNullable: true), false, ScaffoldProvider.SqlServer);
        Assert.Equal("[Nickname] nvarchar(max) NULL", line);
    }

    [Fact]
    public void RenderColumnDefinition_NonNullableProperty_EmitsNotNull()
    {
        var line = SqlDdlExporter.RenderColumnDefinition(Column("Name", isNullable: false, maxLength: 100), false, ScaffoldProvider.SqlServer);
        Assert.Equal("[Name] nvarchar(100) NOT NULL", line);
    }

    [Fact]
    public void RenderColumnDefinition_DefaultValueLiteral_AppendsDefaultClause()
    {
        var line = SqlDdlExporter.RenderColumnDefinition(
            Column("Status", clrType: "int", isNullable: false, defaultValueLiteral: "1"), false, ScaffoldProvider.SqlServer);
        Assert.Equal("[Status] int NOT NULL DEFAULT 1", line);
    }

    [Fact]
    public void RenderColumnDefinition_DefaultValueSql_AppendsDefaultWithParens()
    {
        var line = SqlDdlExporter.RenderColumnDefinition(
            Column("CreatedAt", clrType: "DateTime", isNullable: false, defaultValueSql: "GETUTCDATE()"), false, ScaffoldProvider.SqlServer);
        Assert.Equal("[CreatedAt] datetime2 NOT NULL DEFAULT (GETUTCDATE())", line);
    }

    [Theory]
    [InlineData(ScaffoldProvider.SqlServer, "[Total] int NOT NULL AS ([Price] * [Qty]) PERSISTED")]
    [InlineData(ScaffoldProvider.PostgreSql, "\"Total\" integer GENERATED ALWAYS AS ([Price] * [Qty]) STORED")]
    [InlineData(ScaffoldProvider.Sqlite, "\"Total\" INTEGER GENERATED ALWAYS AS ([Price] * [Qty]) STORED")]
    public void RenderColumnDefinition_ComputedColumnStored_EmitsPerDialectSyntax(ScaffoldProvider provider, string expected)
    {
        var line = SqlDdlExporter.RenderColumnDefinition(
            Column("Total", clrType: "int", isNullable: false, computedColumnSql: "[Price] * [Qty]", computedColumnSqlIsStored: true),
            false, provider);
        Assert.Equal(expected, line);
    }

    [Fact]
    public void RenderColumnDefinition_ComputedColumnNotStored_SqlServer_OmitsPersisted()
    {
        var line = SqlDdlExporter.RenderColumnDefinition(
            Column("Total", clrType: "int", isNullable: false, computedColumnSql: "[Price] * [Qty]", computedColumnSqlIsStored: false),
            false, ScaffoldProvider.SqlServer);
        Assert.Equal("[Total] int NOT NULL AS ([Price] * [Qty])", line);
    }

    [Theory]
    [InlineData(ScaffoldProvider.SqlServer, "[Id] int IDENTITY(1,1) NOT NULL")]
    [InlineData(ScaffoldProvider.PostgreSql, "\"Id\" integer GENERATED ALWAYS AS IDENTITY NOT NULL")]
    public void RenderColumnDefinition_SoleIntegerIdentityPrimaryKey_EmitsIdentitySyntax(ScaffoldProvider provider, string expected)
    {
        var line = SqlDdlExporter.RenderColumnDefinition(Column("Id", clrType: "int", isNullable: false), true, provider);
        Assert.Equal(expected, line);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("Never", false)]
    [InlineData("OnAdd", true)]
    [InlineData("Identity", true)]
    public void IsIdentityCandidate_IntegerType_RespectsValueGeneratedNever(string? valueGenerated, bool expected)
    {
        Assert.Equal(expected, SqlDdlExporter.IsIdentityCandidate(Column("Id", clrType: "int", valueGenerated: valueGenerated)));
    }

    [Fact]
    public void IsIdentityCandidate_NonIntegerType_IsFalse()
    {
        Assert.False(SqlDdlExporter.IsIdentityCandidate(Column("Id", clrType: "Guid")));
    }

    [Fact]
    public void SelectPhysicalEntities_ExcludesViewMappedAndFunctionMappedEntities()
    {
        var table = Entity("Order");
        var view = Entity("OrderSummary") with { ViewName = "vw_OrderSummary" };
        var function = Entity("ActiveOrders") with { FunctionName = "fn_ActiveOrders" };

        var result = SqlDdlExporter.SelectPhysicalEntities(new[] { table, view, function });

        Assert.Equal(new[] { "Order" }, result.Select(e => e.Name));
    }

    [Fact]
    public void OrderTablesByDependency_PrincipalBeforeDependent()
    {
        var blog = Entity("Blog", Column("Id", "int", isNullable: false));
        var post = Entity("Post", Column("Id", "int", isNullable: false), Column("BlogId", "int", isNullable: false));
        var relationship = new RelationshipModel(
            "Blog", "Post", OneToMany, PrincipalNavigation: null, DependentNavigation: null,
            ForeignKeyProperties: new[] { "BlogId" });

        var ordered = SqlDdlExporter.OrderTablesByDependency(new[] { post, blog }, new[] { relationship });

        Assert.Equal(new[] { "Blog", "Post" }, ordered.Select(e => e.Name));
    }

    [Fact]
    public void OrderTablesByDependency_NoRelationships_PreservesOriginalOrder()
    {
        var a = Entity("A");
        var b = Entity("B");

        var ordered = SqlDdlExporter.OrderTablesByDependency(new[] { a, b }, Array.Empty<RelationshipModel>());

        Assert.Equal(new[] { "A", "B" }, ordered.Select(e => e.Name));
    }

    [Fact]
    public void RenderCreateTable_SimpleEntity_SqlServer_EmitsColumnsAndPrimaryKey()
    {
        var entity = Entity("Blog",
                Column("Id", "int", isNullable: false),
                Column("Title", "string", isNullable: false, maxLength: 200))
            with { KeyPropertyNames = new[] { "Id" } };

        var sql = SqlDdlExporter.RenderCreateTable(entity, entity.KeyPropertyNames, ScaffoldProvider.SqlServer);

        Assert.Equal(
            "CREATE TABLE [Blog] (\n" +
            "    [Id] int IDENTITY(1,1) NOT NULL,\n" +
            "    [Title] nvarchar(200) NOT NULL,\n" +
            "    CONSTRAINT [PK_Blog] PRIMARY KEY ([Id])\n" +
            ");\n",
            sql);
    }

    [Fact]
    public void RenderCreateTable_ExplicitKeyName_UsesItForConstraintName()
    {
        var entity = Entity("Blog", Column("Id", "int", isNullable: false))
            with { KeyPropertyNames = new[] { "Id" }, KeyName = "PK_MyBlog" };

        var sql = SqlDdlExporter.RenderCreateTable(entity, entity.KeyPropertyNames, ScaffoldProvider.SqlServer);

        Assert.Contains("CONSTRAINT [PK_MyBlog] PRIMARY KEY ([Id])", sql);
    }

    [Fact]
    public void RenderCreateTable_KeylessEntity_EmitsNoPrimaryKeyClause()
    {
        var entity = Entity("Report", Column("Value", "int", isNullable: false)) with { IsKeyless = true };

        var sql = SqlDdlExporter.RenderCreateTable(entity, Array.Empty<string>(), ScaffoldProvider.SqlServer);

        Assert.DoesNotContain("PRIMARY KEY", sql);
    }

    [Fact]
    public void RenderCreateTable_CheckConstraint_IsIncluded()
    {
        var entity = Entity("Order", Column("Total", "decimal", isNullable: false))
            with
            {
                KeyPropertyNames = Array.Empty<string>(),
                IsKeyless = true,
                CheckConstraints = new[] { new CheckConstraintModel("CK_Order_Total", "[Total] >= 0") },
            };

        var sql = SqlDdlExporter.RenderCreateTable(entity, Array.Empty<string>(), ScaffoldProvider.SqlServer);

        Assert.Contains("CONSTRAINT [CK_Order_Total] CHECK ([Total] >= 0)", sql);
    }

    [Fact]
    public void RenderCreateTable_AlternateKey_EmitsUniqueConstraint()
    {
        var entity = Entity("User", Column("Email", "string", isNullable: false, maxLength: 200))
            with
            {
                KeyPropertyNames = Array.Empty<string>(),
                IsKeyless = true,
                AlternateKeys = new IReadOnlyList<string>[] { new[] { "Email" } },
            };

        var sql = SqlDdlExporter.RenderCreateTable(entity, Array.Empty<string>(), ScaffoldProvider.SqlServer);

        Assert.Contains("CONSTRAINT [AK_User_Email] UNIQUE ([Email])", sql);
    }

    [Fact]
    public void RenderCreateTable_Sqlite_SoleIntegerIdentityPrimaryKey_UsesInlineAutoincrement()
    {
        var entity = Entity("Blog",
                Column("Id", "int", isNullable: false),
                Column("Title", "string", isNullable: false))
            with { KeyPropertyNames = new[] { "Id" } };

        var sql = SqlDdlExporter.RenderCreateTable(entity, entity.KeyPropertyNames, ScaffoldProvider.Sqlite);

        Assert.Equal(
            "CREATE TABLE \"Blog\" (\n" +
            "    \"Id\" INTEGER PRIMARY KEY AUTOINCREMENT,\n" +
            "    \"Title\" TEXT NOT NULL\n" +
            ");\n",
            sql);
    }

    [Fact]
    public void RenderCreateTable_Sqlite_CompositePrimaryKey_UsesTrailingConstraintNotInline()
    {
        var entity = Entity("OrderLine",
                Column("OrderId", "int", isNullable: false),
                Column("LineNumber", "int", isNullable: false))
            with { KeyPropertyNames = new[] { "OrderId", "LineNumber" } };

        var sql = SqlDdlExporter.RenderCreateTable(entity, entity.KeyPropertyNames, ScaffoldProvider.Sqlite);

        Assert.Contains("PRIMARY KEY (\"OrderId\", \"LineNumber\")", sql);
        Assert.DoesNotContain("AUTOINCREMENT", sql);
    }
}
