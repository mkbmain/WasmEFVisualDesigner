using EfSchemaVisualizer.Core.CodeGen;
using EfSchemaVisualizer.Core.Parsing;
using Xunit;

namespace EfSchemaVisualizer.Core.Tests.CodeGen;

public class OnModelCreatingRewriterTests
{
    private const string Source = """
        public class AppDbContext : DbContext
        {
            // unrelated comment that must survive untouched
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.Property(e => e.Name).HasMaxLength(100);
                    entity.Property(e => e.Email).HasMaxLength(255);
                });

                modelBuilder.Entity<Address>(entity =>
                {
                    entity.Property(e => e.Line1).HasMaxLength(200);
                });
            }
        }
        """;

    [Fact]
    public void RewriteMaxLength_ChangesOnlyTargetedCall_LeavesEverythingElseIdentical()
    {
        var result = new OnModelCreatingRewriter()
            .RewriteMaxLength(Source, entityName: "Person", propertyName: "Name", newMaxLength: 150);

        Assert.Contains("entity.Property(e => e.Name).HasMaxLength(150)", result);

        // Untouched: Person.Email, Address.Line1, and the unrelated comment.
        Assert.Contains("entity.Property(e => e.Email).HasMaxLength(255)", result);
        Assert.Contains("entity.Property(e => e.Line1).HasMaxLength(200)", result);
        Assert.Contains("// unrelated comment that must survive untouched", result);

        Assert.DoesNotContain("HasMaxLength(100)", result);
    }

    [Fact]
    public void RewriteMaxLength_UnknownEntity_InsertsNewEntityBlock()
    {
        var result = new OnModelCreatingRewriter()
            .RewriteMaxLength(Source, entityName: "Vehicle", propertyName: "Name", newMaxLength: 10);

        Assert.Contains("modelBuilder.Entity<Vehicle>(entity =>", result);
        Assert.Contains("entity.Property(e => e.Name).HasMaxLength(10)", result);

        var configs = new FluentConfigParser().ParseMaxLengths(result).Value;
        Assert.Contains(configs, c => c is { EntityName: "Vehicle", PropertyName: "Name", MaxLength: 10 });
        Assert.Contains(configs, c => c is { EntityName: "Person", PropertyName: "Name", MaxLength: 100 });
        Assert.Contains(configs, c => c is { EntityName: "Address", PropertyName: "Line1", MaxLength: 200 });
    }

    private const string SourceWithRenamedBuilderAndNoConfig = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder builder)
            {
                builder.Entity<Person>(entity =>
                {
                    entity.Property(e => e.Name).HasMaxLength(100);
                });
            }
        }
        """;

    [Fact]
    public void RewriteMaxLength_UnknownEntity_RenamedModelBuilderParameter_UsesSameReceiverName()
    {
        var result = new OnModelCreatingRewriter()
            .RewriteMaxLength(SourceWithRenamedBuilderAndNoConfig, entityName: "Vehicle", propertyName: "Name", newMaxLength: 10);

        Assert.Contains("builder.Entity<Vehicle>(entity =>", result);
        Assert.DoesNotContain("modelBuilder.Entity<Vehicle>", result);
    }

    private const string SourceWithoutOnModelCreating = """
        public class AppDbContext : DbContext
        {
        }
        """;

    [Fact]
    public void RewriteMaxLength_OnModelCreatingMissing_Throws()
    {
        var rewriter = new OnModelCreatingRewriter();

        Assert.Throws<InvalidOperationException>(() =>
            rewriter.RewriteMaxLength(SourceWithoutOnModelCreating, entityName: "Vehicle", propertyName: "Name", newMaxLength: 10));
    }

    private const string SourceWithUnconfiguredProperty = """
        public class AppDbContext : DbContext
        {
            // unrelated comment that must survive untouched
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.Property(e => e.Name).HasMaxLength(100);
                    entity.Property(e => e.Email);
                });

                modelBuilder.Entity<Address>(entity =>
                {
                    entity.Property(e => e.Line1).HasMaxLength(200);
                });
            }
        }
        """;

    [Fact]
    public void RewriteMaxLength_PropertyExistsWithoutHasMaxLength_AppendsHasMaxLengthCall()
    {
        var result = new OnModelCreatingRewriter()
            .RewriteMaxLength(SourceWithUnconfiguredProperty, entityName: "Person", propertyName: "Email", newMaxLength: 50);

        Assert.Contains("entity.Property(e => e.Email).HasMaxLength(50)", result);
        Assert.Contains("// unrelated comment that must survive untouched", result);

        var configs = new FluentConfigParser().ParseMaxLengths(result).Value;
        Assert.Contains(configs, c => c is { EntityName: "Person", PropertyName: "Name", MaxLength: 100 });
        Assert.Contains(configs, c => c is { EntityName: "Person", PropertyName: "Email", MaxLength: 50 });
        Assert.Contains(configs, c => c is { EntityName: "Address", PropertyName: "Line1", MaxLength: 200 });
    }

    private const string SourceWithNestedEntityConfig = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.Property(e => e.Name).HasMaxLength(100);

                    modelBuilder.Entity<Address>(nested =>
                    {
                        nested.Property(e => e.Line1).HasMaxLength(200);
                    });
                });
            }
        }
        """;

    [Fact]
    public void RewriteMaxLength_PropertyOnlyPresentInNestedConfig_InsertsNewStatementIntoOuterScope()
    {
        var rewriter = new OnModelCreatingRewriter();

        // Person has no Line1 property in this shape - Line1 belongs to the nested Address config.
        // RewriteMaxLength is purely syntactic (it doesn't cross-check property names against a
        // parsed EntityModel), so it inserts a new statement into Person's own scope rather than
        // reaching into the nested Address block.
        var result = rewriter.RewriteMaxLength(
            SourceWithNestedEntityConfig, entityName: "Person", propertyName: "Line1", newMaxLength: 999);

        Assert.Contains("entity.Property(e => e.Line1).HasMaxLength(999)", result);

        var configs = new FluentConfigParser().ParseMaxLengths(result).Value;
        Assert.Contains(configs, c => c is { EntityName: "Person", PropertyName: "Line1", MaxLength: 999 });
        Assert.Contains(configs, c => c is { EntityName: "Address", PropertyName: "Line1", MaxLength: 200 });
    }

    private const string SourceWithMissingPropertyMention = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.Property(e => e.Name).HasMaxLength(100);
                });
            }
        }
        """;

    [Fact]
    public void RewriteMaxLength_PropertyNeverMentioned_InsertsNewStatementAtEndOfBlock()
    {
        var result = new OnModelCreatingRewriter()
            .RewriteMaxLength(SourceWithMissingPropertyMention, entityName: "Person", propertyName: "Email", newMaxLength: 75);

        Assert.Contains("entity.Property(e => e.Email).HasMaxLength(75)", result);

        var configs = new FluentConfigParser().ParseMaxLengths(result).Value;
        Assert.Contains(configs, c => c is { EntityName: "Person", PropertyName: "Name", MaxLength: 100 });
        Assert.Contains(configs, c => c is { EntityName: "Person", PropertyName: "Email", MaxLength: 75 });
    }

    private const string SourceWithNonDefaultLambdaParam = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.Property(x => x.Name).HasMaxLength(100);
                });
            }
        }
        """;

    [Fact]
    public void RewriteMaxLength_PropertyNeverMentioned_MatchesSiblingLambdaParameterName()
    {
        var result = new OnModelCreatingRewriter()
            .RewriteMaxLength(SourceWithNonDefaultLambdaParam, entityName: "Person", propertyName: "Email", newMaxLength: 75);

        Assert.Contains("entity.Property(x => x.Email).HasMaxLength(75)", result);
    }

    private const string SourceWithEmptyEntityBlock = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                });
            }
        }
        """;

    [Fact]
    public void RewriteMaxLength_EmptyEntityBlock_FallsBackToDefaultLambdaParameterName()
    {
        var result = new OnModelCreatingRewriter()
            .RewriteMaxLength(SourceWithEmptyEntityBlock, entityName: "Person", propertyName: "Name", newMaxLength: 40);

        Assert.Contains("entity.Property(e => e.Name).HasMaxLength(40)", result);
    }
}
