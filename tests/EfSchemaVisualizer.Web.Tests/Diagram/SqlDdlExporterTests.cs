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

    [Fact]
    public void RenderColumnDefinition_SequenceName_SqlServer_EmitsNextValueForDefault()
    {
        var property = Column("OrderNumber", clrType: "int", isNullable: false) with { SequenceName = "OrderNumbers" };

        var line = SqlDdlExporter.RenderColumnDefinition(property, false, ScaffoldProvider.SqlServer);

        Assert.Equal("[OrderNumber] int NOT NULL DEFAULT NEXT VALUE FOR [OrderNumbers]", line);
    }

    [Fact]
    public void RenderColumnDefinition_SequenceNameWithSchema_SqlServer_QualifiesSequenceName()
    {
        var property = Column("OrderNumber", clrType: "int", isNullable: false)
            with { SequenceName = "OrderNumbers", SequenceSchema = "sales" };

        var line = SqlDdlExporter.RenderColumnDefinition(property, false, ScaffoldProvider.SqlServer);

        Assert.Equal("[OrderNumber] int NOT NULL DEFAULT NEXT VALUE FOR [sales].[OrderNumbers]", line);
    }

    [Fact]
    public void RenderColumnDefinition_SequenceName_Postgres_EmitsNextvalDefault()
    {
        var property = Column("OrderNumber", clrType: "int", isNullable: false)
            with { SequenceName = "OrderNumbers", SequenceSchema = "sales" };

        var line = SqlDdlExporter.RenderColumnDefinition(property, false, ScaffoldProvider.PostgreSql);

        Assert.Equal("\"OrderNumber\" integer NOT NULL DEFAULT nextval('sales.OrderNumbers')", line);
    }

    [Fact]
    public void RenderColumnDefinition_SequenceName_Sqlite_HasNoDefaultAndNotesSkip()
    {
        var property = Column("OrderNumber", clrType: "int", isNullable: false) with { SequenceName = "OrderNumbers" };

        var line = SqlDdlExporter.RenderColumnDefinition(property, false, ScaffoldProvider.Sqlite);

        Assert.Equal("\"OrderNumber\" INTEGER NOT NULL -- sequence \"OrderNumbers\" skipped: SQLite has no CREATE SEQUENCE equivalent", line);
    }

    [Fact]
    public void RenderColumnDefinition_DefaultValueLiteralTakesPrecedenceOverSequenceName()
    {
        var property = Column("OrderNumber", clrType: "int", isNullable: false, defaultValueLiteral: "1")
            with { SequenceName = "OrderNumbers" };

        var line = SqlDdlExporter.RenderColumnDefinition(property, false, ScaffoldProvider.SqlServer);

        Assert.Equal("[OrderNumber] int NOT NULL DEFAULT 1", line);
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

    [Fact]
    public void CollectDescendants_MultiLevelHierarchy_ReturnsAllTransitiveDescendants()
    {
        var person = Entity("Person");
        var student = Entity("Student") with { BaseEntityName = "Person" };
        var gradStudent = Entity("GradStudent") with { BaseEntityName = "Student" };
        var all = new[] { person, student, gradStudent };

        var descendants = SqlDdlExporter.CollectDescendants(person, all);

        Assert.Equal(new[] { "Student", "GradStudent" }, descendants.Select(e => e.Name));
    }

    [Fact]
    public void BuildTphMergedEntity_UnionsOwnPropertiesFromEveryDescendant()
    {
        var person = Entity("Person",
                Column("Id", "int", isNullable: false),
                Column("Name", "string", isNullable: false))
            with { KeyPropertyNames = new[] { "Id" } };

        var studentOwn = Column("Course", "string", isNullable: false);
        var student = Entity("Student",
            new PropertyModel("Id", "int", false, null, DeclaringEntityName: "Person"),
            new PropertyModel("Name", "string", false, null, DeclaringEntityName: "Person"),
            studentOwn) with { BaseEntityName = "Person", KeyPropertyNames = new[] { "Id" } };

        var teacherOwn = Column("Salary", "decimal", isNullable: false);
        var teacher = Entity("Teacher",
            new PropertyModel("Id", "int", false, null, DeclaringEntityName: "Person"),
            new PropertyModel("Name", "string", false, null, DeclaringEntityName: "Person"),
            teacherOwn) with { BaseEntityName = "Person", KeyPropertyNames = new[] { "Id" } };

        var merged = SqlDdlExporter.BuildTphMergedEntity(person, new[] { person, student, teacher });

        Assert.Equal(new[] { "Id", "Name", "Course", "Salary", "Discriminator" }, merged.Properties.Select(p => p.Name));
        Assert.True(merged.Properties.First(p => p.Name == "Course").IsNullable);
        Assert.True(merged.Properties.First(p => p.Name == "Salary").IsNullable);
        Assert.False(merged.Properties.First(p => p.Name == "Discriminator").IsNullable);
    }

    [Fact]
    public void BuildTphMergedEntity_UsesConfiguredDiscriminatorNameAndType()
    {
        var person = Entity("Person", Column("Id", "int", isNullable: false))
            with { KeyPropertyNames = new[] { "Id" }, DiscriminatorPropertyName = "PersonType", DiscriminatorClrType = "int" };
        var student = Entity("Student", new PropertyModel("Id", "int", false, null, DeclaringEntityName: "Person"))
            with { BaseEntityName = "Person", KeyPropertyNames = new[] { "Id" } };

        var merged = SqlDdlExporter.BuildTphMergedEntity(person, new[] { person, student });

        var discriminator = merged.Properties.Last();
        Assert.Equal("PersonType", discriminator.Name);
        Assert.Equal("int", discriminator.ClrType);
    }

    [Fact]
    public void BuildTphMergedEntity_DiscriminatorNameMatchesExistingProperty_DoesNotDuplicateColumn()
    {
        var person = Entity("Person", Column("Id", "int", isNullable: false), Column("Type", "string", isNullable: false))
            with { KeyPropertyNames = new[] { "Id" }, DiscriminatorPropertyName = "Type" };
        var student = Entity("Student", new PropertyModel("Id", "int", false, null, DeclaringEntityName: "Person"))
            with { BaseEntityName = "Person", KeyPropertyNames = new[] { "Id" } };

        var merged = SqlDdlExporter.BuildTphMergedEntity(person, new[] { person, student });

        Assert.Equal(1, merged.Properties.Count(p => p.Name == "Type"));
    }

    [Fact]
    public void BuildTphMergedEntity_DescendantCheckConstraint_IsUnionedIntoMerged()
    {
        var person = Entity("Person", Column("Id", "int", isNullable: false))
            with { KeyPropertyNames = new[] { "Id" } };
        var studentCheck = new CheckConstraintModel("CK_Student_Course", "[Course] IS NOT NULL");
        var student = Entity("Student",
                new PropertyModel("Id", "int", false, null, DeclaringEntityName: "Person"),
                Column("Course", "string", isNullable: false))
            with { BaseEntityName = "Person", KeyPropertyNames = new[] { "Id" }, CheckConstraints = new[] { studentCheck } };

        var merged = SqlDdlExporter.BuildTphMergedEntity(person, new[] { person, student });

        Assert.Contains(merged.CheckConstraints, c => c.Name == "CK_Student_Course");
    }

    [Fact]
    public void BuildTphMergedEntity_DescendantIndexAndAlternateKey_AreUnionedIntoMerged()
    {
        var person = Entity("Person", Column("Id", "int", isNullable: false))
            with { KeyPropertyNames = new[] { "Id" } };
        var studentIndex = new IndexModel(new[] { "Course" }, IsUnique: false, Name: "IX_Student_Course");
        var student = Entity("Student",
                new PropertyModel("Id", "int", false, null, DeclaringEntityName: "Person"),
                Column("Course", "string", isNullable: false))
            with
            {
                BaseEntityName = "Person",
                KeyPropertyNames = new[] { "Id" },
                Indexes = new[] { studentIndex },
                AlternateKeys = new IReadOnlyList<string>[] { new[] { "Course" } },
            };

        var merged = SqlDdlExporter.BuildTphMergedEntity(person, new[] { person, student });

        Assert.Contains(merged.Indexes, i => i.Name == "IX_Student_Course");
        Assert.Contains(merged.AlternateKeys, ak => ak.SequenceEqual(new[] { "Course" }));
    }

    [Fact]
    public void RenderCreateIndex_WithExplicitName_UsesIt()
    {
        var entity = Entity("Order", Column("Total", "decimal", isNullable: false));
        var index = new IndexModel(new[] { "Total" }, IsUnique: false, Name: "IX_Order_Total");

        var sql = SqlDdlExporter.RenderCreateIndex(entity, index, ScaffoldProvider.SqlServer);

        Assert.Equal("CREATE INDEX [IX_Order_Total] ON [Order] ([Total]);\n", sql);
    }

    [Fact]
    public void RenderCreateIndex_NoExplicitName_SynthesizesConventionalName()
    {
        var entity = Entity("Order", Column("Total", "decimal", isNullable: false));
        var index = new IndexModel(new[] { "Total" }, IsUnique: false);

        var sql = SqlDdlExporter.RenderCreateIndex(entity, index, ScaffoldProvider.SqlServer);

        Assert.Equal("CREATE INDEX [IX_Order_Total] ON [Order] ([Total]);\n", sql);
    }

    [Fact]
    public void RenderCreateIndex_Unique_EmitsUniqueKeyword()
    {
        var entity = Entity("User", Column("Email", "string", isNullable: false));
        var index = new IndexModel(new[] { "Email" }, IsUnique: true, Name: "IX_User_Email");

        var sql = SqlDdlExporter.RenderCreateIndex(entity, index, ScaffoldProvider.SqlServer);

        Assert.Equal("CREATE UNIQUE INDEX [IX_User_Email] ON [User] ([Email]);\n", sql);
    }

    [Fact]
    public void RenderCreateIndex_MultipleColumns_JoinsWithComma()
    {
        var entity = Entity("OrderLine",
            Column("OrderId", "int", isNullable: false), Column("LineNumber", "int", isNullable: false));
        var index = new IndexModel(new[] { "OrderId", "LineNumber" }, IsUnique: true, Name: "IX_OrderLine_Composite");

        var sql = SqlDdlExporter.RenderCreateIndex(entity, index, ScaffoldProvider.SqlServer);

        Assert.Equal("CREATE UNIQUE INDEX [IX_OrderLine_Composite] ON [OrderLine] ([OrderId], [LineNumber]);\n", sql);
    }

    [Fact]
    public void IsSkippedTphMember_RootEntity_IsFalse()
    {
        var person = Entity("Person") with { MappingStrategy = MappingStrategy.Tph };
        Assert.False(SqlDdlExporter.IsSkippedTphMember(person, new[] { person }));
    }

    [Fact]
    public void IsSkippedTphMember_DerivedTphEntity_IsTrue()
    {
        var person = Entity("Person") with { MappingStrategy = MappingStrategy.Tph };
        var student = Entity("Student") with { BaseEntityName = "Person", MappingStrategy = MappingStrategy.Tph };

        Assert.True(SqlDdlExporter.IsSkippedTphMember(student, new[] { person, student }));
    }

    [Fact]
    public void IsSkippedTphMember_DerivedTptEntity_IsFalse()
    {
        var person = Entity("Person") with { MappingStrategy = MappingStrategy.Tpt };
        var student = Entity("Student") with { BaseEntityName = "Person", MappingStrategy = MappingStrategy.Tpt };

        Assert.False(SqlDdlExporter.IsSkippedTphMember(student, new[] { person, student }));
    }

    [Fact]
    public void RenderSequence_EmitsCreateSequence()
    {
        var sequence = new SequenceModel("OrderNumbers", Schema: null, ClrType: "int", StartsAt: 1000, IncrementsBy: 1, MinValue: null, MaxValue: null, IsCyclic: null);

        var sql = SqlDdlExporter.RenderSequence(sequence, ScaffoldProvider.SqlServer);

        Assert.Equal("CREATE SEQUENCE [OrderNumbers] START WITH 1000 INCREMENT BY 1;\n", sql);
    }

    [Fact]
    public void Export_SimpleOneToMany_EmitsTablesThenForeignKey()
    {
        var blog = Entity("Blog", Column("Id", "int", isNullable: false)) with { KeyPropertyNames = new[] { "Id" } };
        var post = Entity("Post",
                Column("Id", "int", isNullable: false),
                Column("BlogId", "int", isNullable: false))
            with { KeyPropertyNames = new[] { "Id" } };
        var relationship = new RelationshipModel(
            "Blog", "Post", RelationshipKind.OneToMany, PrincipalNavigation: "Posts", DependentNavigation: "Blog",
            ForeignKeyProperties: new[] { "BlogId" }, ConstraintName: "FK_Post_Blog_BlogId");

        var result = new DiagramModelResult(new[] { blog, post }, new[] { relationship }, Array.Empty<Core.Parsing.Diagnostic>(), Array.Empty<SequenceModel>());

        var sql = SqlDdlExporter.Export(result, ScaffoldProvider.SqlServer);

        var blogIndex = sql.IndexOf("CREATE TABLE [Blog]", StringComparison.Ordinal);
        var postIndex = sql.IndexOf("CREATE TABLE [Post]", StringComparison.Ordinal);
        var fkIndex = sql.IndexOf("ALTER TABLE [Post] ADD CONSTRAINT [FK_Post_Blog_BlogId] FOREIGN KEY ([BlogId]) REFERENCES [Blog] ([Id]);", StringComparison.Ordinal);

        Assert.True(blogIndex >= 0 && postIndex > blogIndex, "Blog table must be created before Post table");
        Assert.True(fkIndex > postIndex, "Foreign key must be added after both tables exist");
    }

    [Fact]
    public void Export_ViewMappedEntity_ProducesNoCreateTable()
    {
        var view = Entity("OrderSummary", Column("Total", "decimal")) with { ViewName = "vw_OrderSummary" };
        var result = new DiagramModelResult(new[] { view }, Array.Empty<RelationshipModel>(), Array.Empty<Core.Parsing.Diagnostic>(), Array.Empty<SequenceModel>());

        var sql = SqlDdlExporter.Export(result, ScaffoldProvider.SqlServer);

        Assert.DoesNotContain("CREATE TABLE", sql);
    }

    [Fact]
    public void Export_TphHierarchy_EmitsOneTableForRootOnly()
    {
        var person = Entity("Person", Column("Id", "int", isNullable: false))
            with { KeyPropertyNames = new[] { "Id" }, MappingStrategy = MappingStrategy.Tph };
        var student = Entity("Student",
                new PropertyModel("Id", "int", false, null, DeclaringEntityName: "Person"),
                Column("Course", "string", isNullable: false))
            with { BaseEntityName = "Person", KeyPropertyNames = new[] { "Id" }, MappingStrategy = MappingStrategy.Tph };

        var result = new DiagramModelResult(new[] { person, student }, Array.Empty<RelationshipModel>(), Array.Empty<Core.Parsing.Diagnostic>(), Array.Empty<SequenceModel>());

        var sql = SqlDdlExporter.Export(result, ScaffoldProvider.SqlServer);

        Assert.Contains("CREATE TABLE [Person]", sql);
        Assert.DoesNotContain("CREATE TABLE [Student]", sql);
        Assert.Contains("[Course]", sql);
        Assert.Contains("[Discriminator]", sql);
    }

    [Fact]
    public void Export_TptHierarchy_EmitsOneTablePerEntityAndPkAsFk()
    {
        var person = Entity("Person", Column("Id", "int", isNullable: false))
            with { KeyPropertyNames = new[] { "Id" }, MappingStrategy = MappingStrategy.Tpt };
        var student = Entity("Student",
                new PropertyModel("Id", "int", false, null, DeclaringEntityName: "Person"),
                Column("Course", "string", isNullable: false))
            with { BaseEntityName = "Person", KeyPropertyNames = new[] { "Id" }, MappingStrategy = MappingStrategy.Tpt };
        var inheritance = new RelationshipModel("Person", "Student", RelationshipKind.Inheritance, null, null);

        var result = new DiagramModelResult(new[] { person, student }, new[] { inheritance }, Array.Empty<Core.Parsing.Diagnostic>(), Array.Empty<SequenceModel>());

        var sql = SqlDdlExporter.Export(result, ScaffoldProvider.SqlServer);

        Assert.Contains("CREATE TABLE [Person]", sql);
        Assert.Contains("CREATE TABLE [Student]", sql);
        Assert.Contains("ALTER TABLE [Student] ADD CONSTRAINT [FK_Student_Person] FOREIGN KEY ([Id]) REFERENCES [Person] ([Id]);", sql);

        var studentTableStart = sql.IndexOf("CREATE TABLE [Student]", StringComparison.Ordinal);
        var studentTableEnd = sql.IndexOf(");\n", studentTableStart, StringComparison.Ordinal);
        var studentTableSql = sql[studentTableStart..studentTableEnd];
        Assert.DoesNotContain("IDENTITY", studentTableSql);
        Assert.DoesNotContain("GENERATED ALWAYS AS IDENTITY", studentTableSql);
        Assert.DoesNotContain("AUTOINCREMENT", studentTableSql);
    }

    [Fact]
    public void RenderCreateTable_TptChildEntity_SuppressesIdentityOnSharedPk()
    {
        var student = Entity("Student", Column("Id", "int", isNullable: false), Column("Course", "string", isNullable: false))
            with { BaseEntityName = "Person", MappingStrategy = MappingStrategy.Tpt, KeyPropertyNames = new[] { "Id" } };

        var sqlServerSql = SqlDdlExporter.RenderCreateTable(student, student.KeyPropertyNames, ScaffoldProvider.SqlServer);
        var postgresSql = SqlDdlExporter.RenderCreateTable(student, student.KeyPropertyNames, ScaffoldProvider.PostgreSql);
        var sqliteSql = SqlDdlExporter.RenderCreateTable(student, student.KeyPropertyNames, ScaffoldProvider.Sqlite);

        Assert.DoesNotContain("IDENTITY", sqlServerSql);
        Assert.DoesNotContain("GENERATED ALWAYS AS IDENTITY", postgresSql);
        Assert.DoesNotContain("AUTOINCREMENT", sqliteSql);
    }

    [Fact]
    public void RenderCreateTable_TpcEntity_EmitsCautionaryCommentAboveCreateTable()
    {
        var entity = Entity("Car", Column("Id", "int", isNullable: false))
            with { KeyPropertyNames = new[] { "Id" }, MappingStrategy = MappingStrategy.Tpc };

        var sql = SqlDdlExporter.RenderCreateTable(entity, entity.KeyPropertyNames, ScaffoldProvider.SqlServer);

        Assert.Contains(
            "-- NOTE: TPC identity columns are independent per table; primary keys are not guaranteed unique across the whole hierarchy without a shared sequence.",
            sql);
        Assert.True(sql.IndexOf("-- NOTE", StringComparison.Ordinal) < sql.IndexOf("CREATE TABLE", StringComparison.Ordinal));
    }

    [Fact]
    public void RenderCreateTable_EmptyEntityWithNoConstraints_EmitsCommentInsteadOfInvalidSql()
    {
        var entity = Entity("Empty") with { IsKeyless = true };

        var sql = SqlDdlExporter.RenderCreateTable(entity, Array.Empty<string>(), ScaffoldProvider.SqlServer);

        Assert.StartsWith("--", sql);
        Assert.DoesNotContain("(\n\n)", sql);
        Assert.Contains("Skipped", sql);
    }

    [Fact]
    public void RenderCreateTable_HasColumnName_UsesColumnNameForPkConstraintIndexAndForeignKey()
    {
        var blog = Entity("Blog", Column("Id", "int", isNullable: false) with { ColumnName = "BlogKey" })
            with
            {
                KeyPropertyNames = new[] { "Id" },
                Indexes = new[] { new IndexModel(new[] { "Id" }, IsUnique: false) },
            };
        var post = Entity("Post",
                Column("Id", "int", isNullable: false),
                Column("BlogId", "int", isNullable: false) with { ColumnName = "BlogFk" })
            with { KeyPropertyNames = new[] { "Id" } };

        var createSql = SqlDdlExporter.RenderCreateTable(blog, blog.KeyPropertyNames, ScaffoldProvider.SqlServer);
        Assert.Contains("[BlogKey] int IDENTITY(1,1) NOT NULL", createSql);
        Assert.Contains("CONSTRAINT [PK_Blog] PRIMARY KEY ([BlogKey])", createSql);
        Assert.DoesNotContain("[Id]", createSql);

        var indexSql = SqlDdlExporter.RenderCreateIndex(blog, blog.Indexes[0], ScaffoldProvider.SqlServer);
        Assert.Contains("([BlogKey])", indexSql);

        var relationship = new RelationshipModel(
            "Blog", "Post", OneToMany, PrincipalNavigation: null, DependentNavigation: null,
            ForeignKeyProperties: new[] { "BlogId" }, ConstraintName: "FK_Post_Blog_BlogId");
        var result = new DiagramModelResult(new[] { blog, post }, new[] { relationship }, Array.Empty<Core.Parsing.Diagnostic>(), Array.Empty<SequenceModel>());
        var sql = SqlDdlExporter.Export(result, ScaffoldProvider.SqlServer);

        Assert.Contains("ALTER TABLE [Post] ADD CONSTRAINT [FK_Post_Blog_BlogId] FOREIGN KEY ([BlogFk]) REFERENCES [Blog] ([BlogKey]);", sql);
    }

    [Fact]
    public void RenderCreateTable_AlternateKey_WithColumnName_UsesColumnNameInUniqueConstraint()
    {
        var entity = Entity("User", Column("Email", "string", isNullable: false, maxLength: 200) with { ColumnName = "EmailAddress" })
            with
            {
                KeyPropertyNames = Array.Empty<string>(),
                IsKeyless = true,
                AlternateKeys = new IReadOnlyList<string>[] { new[] { "Email" } },
            };

        var sql = SqlDdlExporter.RenderCreateTable(entity, Array.Empty<string>(), ScaffoldProvider.SqlServer);

        Assert.Contains("CONSTRAINT [AK_User_Email] UNIQUE ([EmailAddress])", sql);
    }

    [Fact]
    public void Export_ManyToMany_EmitsJoinTableWithCompositeKeyAndTwoForeignKeys()
    {
        var post = Entity("Post", Column("Id", "int", isNullable: false)) with { KeyPropertyNames = new[] { "Id" } };
        var tag = Entity("Tag", Column("Id", "int", isNullable: false)) with { KeyPropertyNames = new[] { "Id" } };
        var join = Entity("PostTag",
                Column("PostsId", "int", isNullable: false),
                Column("TagsId", "int", isNullable: false))
            with { IsSharedType = true };
        var relationship = new RelationshipModel(
            "Post", "Tag", RelationshipKind.ManyToMany, PrincipalNavigation: "Tags", DependentNavigation: "Posts",
            JoinEntityName: "PostTag", JoinEntityIsSharedType: true,
            JoinEntityLeftForeignKey: new[] { "PostsId" }, JoinEntityRightForeignKey: new[] { "TagsId" });

        var result = new DiagramModelResult(new[] { post, tag, join }, new[] { relationship }, Array.Empty<Core.Parsing.Diagnostic>(), Array.Empty<SequenceModel>());

        var sql = SqlDdlExporter.Export(result, ScaffoldProvider.SqlServer);

        Assert.Contains("CONSTRAINT [PK_PostTag] PRIMARY KEY ([PostsId], [TagsId])", sql);
        Assert.Contains("ALTER TABLE [PostTag] ADD CONSTRAINT [FK_PostTag_Post_PostsId] FOREIGN KEY ([PostsId]) REFERENCES [Post] ([Id]);", sql);
        Assert.Contains("ALTER TABLE [PostTag] ADD CONSTRAINT [FK_PostTag_Tag_TagsId] FOREIGN KEY ([TagsId]) REFERENCES [Tag] ([Id]);", sql);
    }

    [Fact]
    public void Export_SelfReferencingManyToMany_ProducesTwoDistinctForeignKeyConstraintNames()
    {
        var user = Entity("User", Column("Id", "int", isNullable: false)) with { KeyPropertyNames = new[] { "Id" } };
        var join = Entity("UserFriend",
                Column("UserId", "int", isNullable: false),
                Column("FriendId", "int", isNullable: false))
            with { IsSharedType = true };
        var relationship = new RelationshipModel(
            "User", "User", RelationshipKind.ManyToMany, PrincipalNavigation: "Friends", DependentNavigation: "FriendedBy",
            JoinEntityName: "UserFriend", JoinEntityIsSharedType: true,
            JoinEntityLeftForeignKey: new[] { "UserId" }, JoinEntityRightForeignKey: new[] { "FriendId" });

        var result = new DiagramModelResult(new[] { user, join }, new[] { relationship }, Array.Empty<Core.Parsing.Diagnostic>(), Array.Empty<SequenceModel>());

        var sql = SqlDdlExporter.Export(result, ScaffoldProvider.SqlServer);

        Assert.Contains("ALTER TABLE [UserFriend] ADD CONSTRAINT [FK_UserFriend_User_UserId] FOREIGN KEY ([UserId]) REFERENCES [User] ([Id]);", sql);
        Assert.Contains("ALTER TABLE [UserFriend] ADD CONSTRAINT [FK_UserFriend_User_FriendId] FOREIGN KEY ([FriendId]) REFERENCES [User] ([Id]);", sql);
    }

    [Fact]
    public void ResolveTphTableEntity_RootEntity_ReturnsItself()
    {
        var person = Entity("Person") with { MappingStrategy = MappingStrategy.Tph };

        var resolved = SqlDdlExporter.ResolveTphTableEntity(person, new[] { person });

        Assert.Same(person, resolved);
    }

    [Fact]
    public void ResolveTphTableEntity_DerivedTphEntity_ResolvesToRoot()
    {
        var person = Entity("Person") with { MappingStrategy = MappingStrategy.Tph };
        var student = Entity("Student") with { BaseEntityName = "Person", MappingStrategy = MappingStrategy.Tph };
        var all = new[] { person, student };

        var resolved = SqlDdlExporter.ResolveTphTableEntity(student, all);

        Assert.Equal("Person", resolved.Name);
    }

    [Fact]
    public void Export_ForeignKeyToTphDerivedType_ReferencesRootTableNotDerivedTable()
    {
        var person = Entity("Person", Column("Id", "int", isNullable: false))
            with { KeyPropertyNames = new[] { "Id" }, MappingStrategy = MappingStrategy.Tph };
        var student = Entity("Student",
                new PropertyModel("Id", "int", false, null, DeclaringEntityName: "Person"),
                Column("SchoolId", "int", isNullable: false))
            with { BaseEntityName = "Person", KeyPropertyNames = new[] { "Id" }, MappingStrategy = MappingStrategy.Tph };
        var school = Entity("School", Column("Id", "int", isNullable: false)) with { KeyPropertyNames = new[] { "Id" } };

        var relationship = new RelationshipModel(
            "School", "Student", RelationshipKind.OneToMany, PrincipalNavigation: "Students", DependentNavigation: "School",
            ForeignKeyProperties: new[] { "SchoolId" });

        var result = new DiagramModelResult(
            new[] { person, student, school }, new[] { relationship }, Array.Empty<Core.Parsing.Diagnostic>(), Array.Empty<SequenceModel>());

        var sql = SqlDdlExporter.Export(result, ScaffoldProvider.SqlServer);

        Assert.Contains("ALTER TABLE [Person] ADD CONSTRAINT", sql);
        Assert.DoesNotContain("ALTER TABLE [Student]", sql);
        Assert.Contains("REFERENCES [School] ([Id]);", sql);
    }
}
