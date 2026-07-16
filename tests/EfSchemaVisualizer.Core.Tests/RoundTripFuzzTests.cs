using System.Linq;
using EfSchemaVisualizer.Core.CodeGen;
using EfSchemaVisualizer.Core.Parsing;
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

        var table = parser.ParseTableMappings(ConfigSource).Value.Single(c => c.EntityName == "Blog");
        AssertOnlyLineEndingsDiffer(ConfigSource, rewriter.SetTable(ConfigSource, "Blog", table.TableName, table.Schema));

        var index = parser.ParseIndexes(ConfigSource).Value.Single(c => c.EntityName == "Blog");
        AssertOnlyLineEndingsDiffer(ConfigSource, rewriter.SetIndex(ConfigSource, "Blog", index.PropertyNames, index.IsUnique, index.Name));

        var postTitleMaxLength = parser.ParseMaxLengths(ConfigSource).Value.Single(c => c is { EntityName: "Post", PropertyName: "Title" });
        Assert.Equal(ConfigSource, rewriter.RewriteMaxLength(ConfigSource, "Post", "Title", postTitleMaxLength.MaxLength));

        var postKey = parser.ParseKeys(ConfigSource).Value.Single(c => c.EntityName == "Post");
        AssertOnlyLineEndingsDiffer(ConfigSource, rewriter.SetKey(ConfigSource, "Post", postKey.PropertyNames));

        var postContentDefault = parser.ParseDefaultValues(ConfigSource).Value.Single(c => c is { EntityName: "Post", PropertyName: "Content" });
        Assert.Equal(ConfigSource, rewriter.SetDefaultValue(ConfigSource, "Post", "Content", postContentDefault.LiteralText));
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

        // ...and Post's other, unrelated config lines are untouched too.
        Assert.Contains("entity.HasKey(e => e.PostId);", renamedConfigSource);
        Assert.Contains("entity.Property(e => e.Content).HasDefaultValue(\"\");", renamedConfigSource);
        Assert.Contains("entity.HasOne(e => e.Blog).WithMany(b => b.Posts).HasForeignKey(e => e.BlogId);", renamedConfigSource);

        // Reparsing the regenerated source still resolves the untouched Blog config correctly.
        var reparsedIndex = new FluentConfigParser().ParseIndexes(renamedConfigSource).Value.Single(c => c.EntityName == "Blog");
        Assert.True(reparsedIndex.IsUnique);
        Assert.Equal(new[] { "Url" }, reparsedIndex.PropertyNames);
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
