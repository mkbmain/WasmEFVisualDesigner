using EfSchemaVisualizer.Core.CodeGen;
using Xunit;

namespace EfSchemaVisualizer.Core.Tests.CodeGen;

public class OnModelCreatingRewriterOwnedConfigScopeTests
{
    private readonly OnModelCreatingRewriter _rewriter = new();

    [Fact]
    public void SetColumnName_OnFoldedOwnedProperty_SynthesizesBuilderLambdaOnBareOwnsOneCall()
    {
        const string source = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Order>(entity =>
                    {
                        entity.OwnsOne(e => e.ShippingAddress);
                    });
                }
            }
            """;

        var newSource = _rewriter.SetColumnNameOnOwnedProperty(source, "Order", "ShippingAddress", "Street", "shipping_street");

        Assert.Contains("OwnsOne(e => e.ShippingAddress, b =>", newSource);
        Assert.Contains("HasColumnName(\"shipping_street\")", newSource);
    }

    [Fact]
    public void SetColumnName_OnFoldedOwnedProperty_ExistingBuilderLambda_AppendsToIt()
    {
        const string source = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Order>(entity =>
                    {
                        entity.OwnsOne(e => e.ShippingAddress, b =>
                        {
                            b.Property(a => a.Street).HasMaxLength(100);
                        });
                    });
                }
            }
            """;

        var newSource = _rewriter.SetColumnNameOnOwnedProperty(source, "Order", "ShippingAddress", "Street", "shipping_street");

        Assert.Contains("HasMaxLength(100)", newSource);
        Assert.Contains("HasColumnName(\"shipping_street\")", newSource);
    }

    [Fact]
    public void SetColumnName_OnStringOverloadOwnsOneWithExistingBuilderLambda_DoesNotDuplicateBuilderLambda()
    {
        // Regression test for a defect where the builder-lambda detector counted LAMBDAS instead
        // of ARGUMENT POSITIONS: the string-overload nav selector `OwnsOne("ShippingAddress", b =>
        // {...})` has only one lambda in the whole call (the builder), so the old
        // `.OfType<AnonymousFunctionExpressionSyntax>().Skip(1)` logic skipped past it, found
        // nothing, wrongly concluded the call was "bare", and appended a SECOND builder lambda —
        // producing non-compiling output with two `b =>` builder lambdas on one OwnsOne call.
        const string source = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Order>(entity =>
                    {
                        entity.OwnsOne("ShippingAddress", b =>
                        {
                            b.Property(a => a.Street).HasMaxLength(100);
                        });
                    });
                }
            }
            """;

        var newSource = _rewriter.SetColumnNameOnOwnedProperty(source, "Order", "ShippingAddress", "Street", "shipping_street");

        Assert.Contains("HasMaxLength(100)", newSource);
        Assert.Contains("HasColumnName(\"shipping_street\")", newSource);

        var builderLambdaCount = newSource.Split("b =>").Length - 1;
        Assert.Equal(1, builderLambdaCount);

        var ownsOneCallCount = newSource.Split("OwnsOne(").Length - 1;
        Assert.Equal(1, ownsOneCallCount);
    }

    [Fact]
    public void SetColumnName_OnOwnsOneWithParenthesizedBuilderLambda_ResolvesScopeWithoutThrowing()
    {
        // Regression test for a defect where GetScopeBlockAndReceiver only matched a builder-lambda
        // block whose parent was a SimpleLambdaExpressionSyntax (`b => {...}`), hard-throwing
        // `InvalidOperationException: Unsupported configuration scope node type: BlockSyntax` for a
        // parenthesized builder lambda (`(b) => {...}`) — equally valid, idiomatic C# that can
        // appear in user-authored/parsed source.
        const string source = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Order>(entity =>
                    {
                        entity.OwnsOne(e => e.ShippingAddress, (b) =>
                        {
                            b.Property(a => a.Street).HasMaxLength(100);
                        });
                    });
                }
            }
            """;

        var newSource = _rewriter.SetColumnNameOnOwnedProperty(source, "Order", "ShippingAddress", "Street", "shipping_street");

        Assert.Contains("HasMaxLength(100)", newSource);
        Assert.Contains("HasColumnName(\"shipping_street\")", newSource);
    }
}
