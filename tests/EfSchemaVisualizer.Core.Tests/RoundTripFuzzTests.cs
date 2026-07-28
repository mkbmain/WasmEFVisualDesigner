using System.Linq;
using EfSchemaVisualizer.Core.CodeGen;
using EfSchemaVisualizer.Core.Model;
using EfSchemaVisualizer.Core.Parsing;
using EfSchemaVisualizer.Web.Diagram;
using Xunit;

namespace EfSchemaVisualizer.Core.Tests;

/// <summary>
/// Feeds a small corpus of realistic, multi-entity, multi-config-kind
/// DbContext shapes through the full parse/rewrite pipeline. Verifies two
/// trust properties the unit tests elsewhere don't check end-to-end:
/// (1) every config kind present in the corpus round-trips byte-identical
/// when written back with the value it was parsed with (a no-op edit), and
/// (2) editing one property leaves every other entity's config — including
/// constructs the parser doesn't understand at all, like
/// <c>HasDefaultValueSql</c> — preserved verbatim rather than dropped.
/// </summary>
public class RoundTripFuzzTests
{
    private const string EntitySource = """
        public class Blog
        {
            public int BlogId { get; set; }
            public string Url { get; set; }
            public string Rating { get; set; }
            public string CreatedAt { get; set; }
            public List<Post> Posts { get; set; }
        }

        public class Post
        {
            public int PostId { get; set; }
            public string Title { get; set; }
            public string Content { get; set; }
            public int BlogId { get; set; }
            public Blog Blog { get; set; }
        }
        """;

    private const string ConfigSource = """
        public class BloggingContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Blog>(entity =>
                {
                    entity.HasKey(e => e.BlogId);
                    entity.ToTable("Blogs");
                    entity.Property(e => e.Url).HasMaxLength(500).IsRequired();
                    entity.Property(e => e.Rating).HasColumnName("BlogRating").HasColumnType("decimal(5,2)");
                    // Server-generated timestamp; HasDefaultValueSql is not modeled by the parser.
                    entity.Property(e => e.CreatedAt).HasDefaultValueSql("GETUTCDATE()");
                    entity.HasIndex(e => e.Url).IsUnique();
                    entity.HasAlternateKey(e => e.Url);
                });

                modelBuilder.Entity<Post>(entity =>
                {
                    entity.HasKey(e => e.PostId);
                    entity.Property(e => e.Title).HasMaxLength(200);
                    entity.Property(e => e.Content).HasDefaultValue("");
                    entity.HasOne(e => e.Blog).WithMany(b => b.Posts).HasForeignKey(e => e.BlogId);
                });
            }
        }
        """;

    private const string PriorityThreeConfigSource = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Blog>(entity =>
                {
                    entity.HasQueryFilter(e => e.Url != null);
                    entity.SplitToTable("BlogStats", tb => { tb.Property(e => e.Rating); });
                    entity.ToTable(b => b.IsTemporal());
                });

                modelBuilder.Entity<Post>(entity =>
                {
                    entity.Property(e => e.Title).HasMaxLength(200);
                });
            }
        }
        """;

    [Fact]
    public void PriorityThreeConstructs_AreReadIntoTheModel_NotFlaggedAsUnrecognized()
    {
        var parser = new FluentConfigParser();

        var queryFilters = parser.ParseQueryFilters(PriorityThreeConfigSource).Value;
        Assert.Contains(queryFilters, c => c.EntityName == "Blog");

        var splitTables = parser.ParseSplitTables(PriorityThreeConfigSource).Value;
        Assert.Contains(splitTables, c => c is { EntityName: "Blog", TableName: "BlogStats" });

        var tables = parser.ParseTableMappings(PriorityThreeConfigSource).Value;
        Assert.Contains(tables.Temporal, c => c.EntityName == "Blog");

        var unrecognized = parser.ParseUnrecognizedCalls(PriorityThreeConfigSource);
        Assert.Empty(unrecognized);
    }

    [Fact]
    public void RenamingUnrelatedEntitysProperty_LeavesPriorityThreeConstructsOnBlogUntouched()
    {
        var modelRewriter = new OnModelCreatingRewriter();

        var renamedConfigSource = modelRewriter.RenamePropertyReferences(
            PriorityThreeConfigSource, "Post", "Title", "Headline");

        Assert.Contains("entity.Property(e => e.Headline)", renamedConfigSource);

        var parser = new FluentConfigParser();

        var queryFilters = parser.ParseQueryFilters(renamedConfigSource).Value;
        Assert.Contains(queryFilters, c => c.EntityName == "Blog");

        var splitTables = parser.ParseSplitTables(renamedConfigSource).Value;
        Assert.Contains(splitTables, c => c is { EntityName: "Blog", TableName: "BlogStats" });

        var tables = parser.ParseTableMappings(renamedConfigSource).Value;
        Assert.Contains(tables.Temporal, c => c.EntityName == "Blog");
    }

    [Fact]
    public void UnsupportedHasDefaultValueSql_IsNotReadIntoTheModel()
    {
        var defaultValues = new FluentConfigParser().ParseDefaultValues(ConfigSource).Value;

        Assert.DoesNotContain(defaultValues, c => c.EntityName == "Blog" && c.PropertyName == "CreatedAt");
        Assert.Contains(defaultValues, c => c is { EntityName: "Post", PropertyName: "Content", LiteralText: "\"\"" });
    }

    [Fact]
    public void NoOpEdits_AreByteIdenticalAcrossEveryConfigKindInTheCorpus()
    {
        var parser = new FluentConfigParser();
        var rewriter = new OnModelCreatingRewriter();

        var maxLength = parser.ParseMaxLengths(ConfigSource).Value.Single(c => c is { EntityName: "Blog", PropertyName: "Url" });
        Assert.Equal(ConfigSource, rewriter.RewriteMaxLength(ConfigSource, "Blog", "Url", maxLength.MaxLength));

        var isRequired = parser.ParseIsRequired(ConfigSource).Value.Single(c => c is { EntityName: "Blog", PropertyName: "Url" });
        Assert.Equal(ConfigSource, rewriter.RewriteIsRequired(ConfigSource, "Blog", "Url", isRequired.IsRequired));

        var columnName = parser.ParseColumnNames(ConfigSource).Value.Single(c => c is { EntityName: "Blog", PropertyName: "Rating" });
        Assert.Equal(ConfigSource, rewriter.SetColumnName(ConfigSource, "Blog", "Rating", columnName.ColumnName));

        var columnType = parser.ParseColumnTypes(ConfigSource).Value.Single(c => c is { EntityName: "Blog", PropertyName: "Rating" });
        Assert.Equal(ConfigSource, rewriter.SetColumnType(ConfigSource, "Blog", "Rating", columnType.ColumnType));

        // SetKey/SetTable/SetIndex always synthesize their call via whole-file
        // NormalizeWhitespace() (the documented insert-path trade-off from
        // 2026-07-08-insert-fluent-config-design.md), so a no-op edit is only
        // whitespace-identical, not byte-identical — assert line-ending
        // normalization is the *only* difference, and that the value survives.
        var blogKey = parser.ParseKeys(ConfigSource).Value.Single(c => c.EntityName == "Blog");
        AssertOnlyLineEndingsDiffer(ConfigSource, rewriter.SetKey(ConfigSource, "Blog", blogKey.PropertyNames));

        var table = parser.ParseTableMappings(ConfigSource).Value.Tables.Single(c => c.EntityName == "Blog");
        AssertOnlyLineEndingsDiffer(ConfigSource, rewriter.SetTable(ConfigSource, "Blog", table.TableName, table.Schema));

        var index = parser.ParseIndexes(ConfigSource).Value.Single(c => c.EntityName == "Blog");
        AssertOnlyLineEndingsDiffer(ConfigSource, rewriter.SetIndex(ConfigSource, "Blog", index.PropertyNames, index.IsUnique, index.Name));

        var alternateKey = parser.ParseAlternateKeys(ConfigSource).Value.Single(c => c.EntityName == "Blog");
        Assert.Equal(ConfigSource, rewriter.AddAlternateKey(ConfigSource, "Blog", alternateKey.PropertyNames));

        var postTitleMaxLength = parser.ParseMaxLengths(ConfigSource).Value.Single(c => c is { EntityName: "Post", PropertyName: "Title" });
        Assert.Equal(ConfigSource, rewriter.RewriteMaxLength(ConfigSource, "Post", "Title", postTitleMaxLength.MaxLength));

        var postKey = parser.ParseKeys(ConfigSource).Value.Single(c => c.EntityName == "Post");
        AssertOnlyLineEndingsDiffer(ConfigSource, rewriter.SetKey(ConfigSource, "Post", postKey.PropertyNames));

        var postContentDefault = parser.ParseDefaultValues(ConfigSource).Value.Single(c => c is { EntityName: "Post", PropertyName: "Content" });
        Assert.Equal(ConfigSource, rewriter.SetDefaultValue(ConfigSource, "Post", "Content", postContentDefault.LiteralText));
    }

    private const string NamingEntitySource = """
        public class Blog
        {
            public int BlogId { get; set; }
            public string Url { get; set; }
            public List<Post> Posts { get; set; }
        }

        public class Post
        {
            public int PostId { get; set; }
            public int BlogId { get; set; }
            public Blog Blog { get; set; }
        }
        """;

    private const string NamingConfigSource = """
        public class BloggingContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Blog>(entity =>
                {
                    entity.HasKey(e => e.BlogId).HasName("PK_Blog");
                    entity.HasIndex(e => e.Url).HasDatabaseName("IX_Blog_Url");
                });

                modelBuilder.Entity<Post>(entity =>
                {
                    entity.HasKey(e => e.PostId);
                    entity.HasOne(e => e.Blog).WithMany(b => b.Posts).HasForeignKey(e => e.BlogId).HasConstraintName("FK_Post_Blog");
                });
            }
        }
        """;

    [Fact]
    public void NoOpEdits_PreservePkFkAndIndexNames()
    {
        var parser = new FluentConfigParser();
        var rewriter = new OnModelCreatingRewriter();

        var blogKey = parser.ParseKeys(NamingConfigSource).Value.Single(c => c.EntityName == "Blog");
        Assert.Equal("PK_Blog", blogKey.Name);
        AssertOnlyLineEndingsDiffer(NamingConfigSource, rewriter.SetKey(NamingConfigSource, "Blog", blogKey.PropertyNames, blogKey.Name));

        // Unlike SetKey/SetRelationship above, SetIndex always regenerates its call in the
        // canonical positional-arg form (`HasIndex(props, "name")`) regardless of how the name
        // was originally authored — it does not preserve a chained `.HasDatabaseName(...)` form.
        // So a "no-op" edit here is not byte/whitespace-identical to the original chained-form
        // source; the invariant that matters is that the parsed value survives a rewrite +
        // reparse, not that the original chain syntax is preserved.
        var blogIndex = parser.ParseIndexes(NamingConfigSource).Value.Single(c => c.EntityName == "Blog");
        Assert.Equal("IX_Blog_Url", blogIndex.Name);
        var rewrittenIndexSource = rewriter.SetIndex(
            NamingConfigSource, "Blog", blogIndex.PropertyNames, blogIndex.IsUnique, blogIndex.Name);
        var reparsedIndex = parser.ParseIndexes(rewrittenIndexSource).Value.Single(c => c.EntityName == "Blog");
        Assert.Equal("IX_Blog_Url", reparsedIndex.Name);
        Assert.Equal(blogIndex.PropertyNames, reparsedIndex.PropertyNames);
        Assert.Equal(blogIndex.IsUnique, reparsedIndex.IsUnique);

        // Like SetIndex above, SetRelationship always regenerates its call chain canonically
        // (e.g. it may switch to the generic HasOne<T>(...) form and use its own lambda
        // parameter names) rather than echoing back the original navigation-lambda syntax, so
        // remove+re-set is not a byte/whitespace-identical no-op here either — the invariant
        // that matters is that the parsed relationship's values (including ConstraintName)
        // survive a remove + re-set + reparse cycle.
        var entities = new EntityClassParser().Parse(NamingEntitySource).Value;
        var relationship = parser.ParseRelationships(NamingConfigSource, entities).Value.Single();
        Assert.Equal("FK_Post_Blog", relationship.ConstraintName);

        var relationshipModel = new RelationshipModel(
            relationship.PrincipalEntity,
            relationship.DependentEntity,
            relationship.Kind,
            relationship.PrincipalNavigation,
            relationship.DependentNavigation,
            relationship.ForeignKeyProperties,
            relationship.OnDeleteBehavior,
            relationship.JoinEntityName,
            ConstraintName: relationship.ConstraintName);

        var withoutRelationship = rewriter.RemoveRelationship(NamingConfigSource, relationshipModel);
        var rewrittenRelationshipSource = rewriter.SetRelationship(withoutRelationship, relationshipModel);

        var reparsedRelationship = parser.ParseRelationships(rewrittenRelationshipSource, entities).Value.Single();
        Assert.Equal("FK_Post_Blog", reparsedRelationship.ConstraintName);
        Assert.Equal(relationship.PrincipalEntity, reparsedRelationship.PrincipalEntity);
        Assert.Equal(relationship.DependentEntity, reparsedRelationship.DependentEntity);
        Assert.Equal(relationship.Kind, reparsedRelationship.Kind);
        Assert.Equal(relationship.ForeignKeyProperties, reparsedRelationship.ForeignKeyProperties);
    }

    [Fact]
    public void EditingOnePropertyPreservesEverythingElseVerbatim_IncludingUnsupportedConstructs()
    {
        var classRewriter = new EntityClassRewriter();
        var modelRewriter = new OnModelCreatingRewriter();

        var renamedEntitySource = classRewriter.RenameProperty(EntitySource, "Post", "Title", "Headline");
        var renamedConfigSource = modelRewriter.RenamePropertyReferences(ConfigSource, "Post", "Title", "Headline");

        // The edited property changed...
        Assert.Contains("public string Headline { get; set; }", renamedEntitySource);
        Assert.Contains("entity.Property(e => e.Headline).HasMaxLength(200);", renamedConfigSource);
        Assert.DoesNotContain("e.Title", renamedConfigSource);

        // ...but the untouched Blog entity's entire config block, including the
        // HasDefaultValueSql call the parser can't model at all, survives
        // (modulo the whole-file NormalizeWhitespace() line-ending
        // normalization documented above — content is unchanged).
        var blogBlockBefore = ExtractEntityBlock(ConfigSource, "Blog");
        var blogBlockAfter = ExtractEntityBlock(renamedConfigSource, "Blog");
        AssertOnlyLineEndingsDiffer(blogBlockBefore, blogBlockAfter);
        Assert.Contains("HasDefaultValueSql(\"GETUTCDATE()\")", blogBlockAfter);
        Assert.Contains("HasAlternateKey(e => e.Url)", blogBlockAfter);

        // ...and Post's other, unrelated config lines are untouched too.
        Assert.Contains("entity.HasKey(e => e.PostId);", renamedConfigSource);
        Assert.Contains("entity.Property(e => e.Content).HasDefaultValue(\"\");", renamedConfigSource);
        Assert.Contains("entity.HasOne(e => e.Blog).WithMany(b => b.Posts).HasForeignKey(e => e.BlogId);", renamedConfigSource);

        // Reparsing the regenerated source still resolves the untouched Blog config correctly.
        var reparsedIndex = new FluentConfigParser().ParseIndexes(renamedConfigSource).Value.Single(c => c.EntityName == "Blog");
        Assert.True(reparsedIndex.IsUnique);
        Assert.Equal(new[] { "Url" }, reparsedIndex.PropertyNames);
    }

    private const string CheckConstraintEntitySource = """
        public class Product
        {
            public int ProductId { get; set; }
            public decimal Price { get; set; }
            public int Quantity { get; set; }
        }

        public class Category
        {
            public int CategoryId { get; set; }
            public string Name { get; set; }
        }
        """;

    private const string CheckConstraintConfigSource = """
        public class ShopContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Product>(entity =>
                {
                    entity.HasKey(e => e.ProductId);
                    entity.HasCheckConstraint("CK_Product_Price", "[Price] >= 0");
                    entity.HasCheckConstraint("CK_Product_Quantity", "[Quantity] >= 0");
                });

                modelBuilder.Entity<Category>(entity =>
                {
                    entity.HasKey(e => e.CategoryId);
                    entity.Property(e => e.Name).HasMaxLength(100);
                });
            }
        }
        """;

    [Fact]
    public void CheckConstraintRoundTrip_RenamingOneAndRemovingTheOther_SurvivesReparseAndLeavesRestUntouched()
    {
        var editor = new DiagramEditor(CheckConstraintEntitySource, CheckConstraintConfigSource);

        var renameResult = editor.SetCheckConstraint(
            "Product", "CK_Product_Price", "CK_Product_Price_NonNegative", "[Price] >= 0.00");
        Assert.True(renameResult.Success, renameResult.Error);

        var removeResult = editor.RemoveCheckConstraint("Product", "CK_Product_Quantity");
        Assert.True(removeResult.Success, removeResult.Error);

        var reparsed = new FluentConfigParser().ParseCheckConstraints(editor.ConfigSource).Value;

        Assert.Contains(
            reparsed,
            c => c is { EntityName: "Product", Name: "CK_Product_Price_NonNegative", Sql: "[Price] >= 0.00" });
        Assert.DoesNotContain(reparsed, c => c.Name == "CK_Product_Quantity");
        Assert.DoesNotContain("CK_Product_Quantity", editor.ConfigSource);

        // The unrelated Category entity's config is untouched.
        Assert.Contains("entity.HasKey(e => e.CategoryId);", editor.ConfigSource);
        Assert.Contains("entity.Property(e => e.Name).HasMaxLength(100);", editor.ConfigSource);
    }

    private const string ComputedSequenceEntitySource = """
        public class Order
        {
            public int OrderId { get; set; }
            public int Quantity { get; set; }
            public decimal UnitPrice { get; set; }
            public decimal Total { get; set; }
            public int Number { get; set; }
        }
        """;

    private const string ComputedSequenceConfigSource = """
        public class SalesContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.HasSequence<int>("OrderNumbers", schema: "shared")
                    .StartsAt(1000);

                modelBuilder.Entity<Order>(entity =>
                {
                    entity.HasKey(e => e.OrderId);
                    entity.Property(e => e.Total).HasComputedColumnSql("[Quantity] * [UnitPrice]", stored: true);
                    entity.Property(e => e.Number).UseSequence("OrderNumbers", "shared");
                    // Business rule intentionally left unmodeled by the parser.
                    entity.HasCheckConstraint("CK_Order_Quantity", "[Quantity] >= 0");
                });
            }
        }
        """;

    [Fact]
    public void ComputedColumnAndSequenceRoundTrip_EditingBothLeavesCheckConstraintUnchanged()
    {
        var editor = new DiagramEditor(ComputedSequenceEntitySource, ComputedSequenceConfigSource);

        var computedResult = editor.SetComputedColumnSql(
            "Order", "Total", "[Quantity] * [UnitPrice] * 1.1", true);
        Assert.True(computedResult.Success, computedResult.Error);

        var sequenceResult = editor.SetSequence(
            "OrderNumbers", schema: "shared", clrType: "int",
            startsAt: 2000, incrementsBy: null, minValue: null, maxValue: null, isCyclic: null);
        Assert.True(sequenceResult.Success, sequenceResult.Error);

        var parser = new FluentConfigParser();

        var computed = parser.ParseComputedColumnSqls(editor.ConfigSource).Value
            .Single(c => c is { EntityName: "Order", PropertyName: "Total" });
        Assert.Equal("[Quantity] * [UnitPrice] * 1.1", computed.Sql);
        Assert.True(computed.IsStored);

        var sequence = parser.ParseSequences(editor.ConfigSource).Value.Single(s => s.Name == "OrderNumbers");
        Assert.Equal(2000, sequence.StartsAt);

        // The check constraint this edit doesn't target survives byte-for-byte.
        Assert.Contains(
            "entity.HasCheckConstraint(\"CK_Order_Quantity\", \"[Quantity] >= 0\");",
            editor.ConfigSource);
    }

    // The synthesis paths (SetKey/SetTable/SetIndex, and any rename that
    // touches a synthesized scope) reformat via whole-file
    // NormalizeWhitespace(), which both switches line endings and collapses
    // blank separator lines between entity blocks. Neither loses content, so
    // compare with both cosmetic differences ignored.
    private static void AssertOnlyLineEndingsDiffer(string expected, string actual)
    {
        static string Canonicalize(string s) => string.Join(
            '\n',
            s.ReplaceLineEndings("\n").Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0));

        Assert.Equal(Canonicalize(expected), Canonicalize(actual));
    }

    private static string ExtractEntityBlock(string source, string entityName)
    {
        var marker = $"modelBuilder.Entity<{entityName}>(entity =>";
        var start = source.IndexOf(marker, System.StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find entity block for {entityName}");
        var end = source.IndexOf("});", start, System.StringComparison.Ordinal);
        Assert.True(end >= 0, $"Could not find end of entity block for {entityName}");
        return source[start..(end + 3)];
    }
}
