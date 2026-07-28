using System.Linq;
using EfSchemaVisualizer.Core.Model;
using EfSchemaVisualizer.Core.Parsing;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace EfSchemaVisualizer.Core.Tests.Parsing;

public class FluentSyntaxHelpersNestedScopeTests
{
    private static PropertyModel Property(string name, string clrType) =>
        new(name, clrType, IsNullable: false, MaxLength: null);

    [Fact]
    public void FindConfigurationScopes_WithEntities_YieldsOwnsOneBuilderLambdaKeyedByTargetTypeName()
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

        var root = CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot();
        var entities = new[]
        {
            new EntityModel("Order", new[] { Property("ShippingAddress", "Address") }),
            new EntityModel("Address", new[] { Property("Street", "string") }),
        };

        var scopes = FluentSyntaxHelpers.FindConfigurationScopes(root, entities).ToList();

        Assert.Contains(scopes, s => s.EntityName == "Order");
        Assert.Contains(scopes, s => s.EntityName == "Address");

        var addressScope = scopes.Single(s => s.EntityName == "Address").Scope;
        var maxLengthCall = Assert.Single(FluentSyntaxHelpers.FindCallsNamed(addressScope, "HasMaxLength"));
        Assert.Equal("Street", FluentSyntaxHelpers.GetPropertyNameFor(maxLengthCall));
    }

    [Fact]
    public void FindConfigurationScopes_WithoutEntities_BehavesExactlyAsBefore()
    {
        const string source = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Order>(entity => { entity.OwnsOne(e => e.ShippingAddress, b => { b.Property(a => a.Street).HasMaxLength(100); }); });
                }
            }
            """;

        var root = CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot();

        var scopes = FluentSyntaxHelpers.FindConfigurationScopes(root).ToList();

        Assert.Single(scopes);
        Assert.Equal("Order", scopes[0].EntityName);
    }

    [Fact]
    public void FindCallsNamed_OuterEntityScope_DoesNotDescendIntoOwnsOneBuilderLambdaButStillFindsTheOwnsOneCallItself()
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

        var root = CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot();
        var entities = new[]
        {
            new EntityModel("Order", new[] { Property("ShippingAddress", "Address") }),
            new EntityModel("Address", new[] { Property("Street", "string") }),
        };

        var orderScope = FluentSyntaxHelpers.FindConfigurationScopes(root, entities)
            .Single(s => s.EntityName == "Order").Scope;

        // The OwnsOne call itself is still found directly on the outer (Order) scope...
        Assert.Single(FluentSyntaxHelpers.FindCallsNamed(orderScope, "OwnsOne"));
        // ...but HasMaxLength inside its builder lambda is NOT double-counted against Order's scope,
        // since it now belongs to the separately-yielded Address scope.
        Assert.Empty(FluentSyntaxHelpers.FindCallsNamed(orderScope, "HasMaxLength"));
    }
}
