using System.Linq;
using EfSchemaVisualizer.Core.CodeGen;
using EfSchemaVisualizer.Core.Parsing;
using Microsoft.CodeAnalysis.CSharp;
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

        var root = CSharpSyntaxTree.ParseText(newSource).GetCompilationUnitRoot();
        var addressScope = FluentSyntaxHelpers.FindConfigurationScopes(root).ToList();
        // Re-parse via the standard entity-name-keyed lookup won't find "Address" without the
        // entities list, so assert on raw text instead — the important thing is the shape:
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
}
