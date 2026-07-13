using System.Collections.Generic;
using System.Linq;
using EfSchemaVisualizer.Core.Parsing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace EfSchemaVisualizer.Core.Tests.Parsing;

public class FluentSyntaxHelpersTests
{
    private static InvocationExpressionSyntax ParseSingleInvocation(string callExpression)
    {
        var wrapped = $$"""
            public class C
            {
                void M()
                {
                    {{callExpression}};
                }
            }
            """;
        var tree = CSharpSyntaxTree.ParseText(wrapped);
        var root = tree.GetCompilationUnitRoot();
        return root.DescendantNodes().OfType<InvocationExpressionSyntax>().Last();
    }

    [Fact]
    public void TryReadPropertyNameList_SingleLambda_ReturnsSingleName()
    {
        var invocation = ParseSingleInvocation("entity.HasKey(e => e.Id)");

        var names = FluentSyntaxHelpers.TryReadPropertyNameList(invocation);

        Assert.Equal(new[] { "Id" }, names);
    }

    [Fact]
    public void TryReadPropertyNameList_CompositeLambda_ReturnsNamesInOrder()
    {
        var invocation = ParseSingleInvocation("entity.HasKey(e => new { e.A, e.B })");

        var names = FluentSyntaxHelpers.TryReadPropertyNameList(invocation);

        Assert.Equal(new[] { "A", "B" }, names);
    }

    [Fact]
    public void TryReadPropertyNameList_BareStringParams_ReturnsNamesInOrder()
    {
        var invocation = ParseSingleInvocation("""entity.HasKey("A", "B")""");

        var names = FluentSyntaxHelpers.TryReadPropertyNameList(invocation);

        Assert.Equal(new[] { "A", "B" }, names);
    }

    [Fact]
    public void TryReadPropertyNameList_ExplicitNameAnonymousMember_ReturnsNull()
    {
        var invocation = ParseSingleInvocation("entity.HasKey(e => new { K = e.A })");

        var names = FluentSyntaxHelpers.TryReadPropertyNameList(invocation);

        Assert.Null(names);
    }

    [Fact]
    public void TryReadPropertyNameList_NoArguments_ReturnsNull()
    {
        var invocation = ParseSingleInvocation("entity.HasKey()");

        var names = FluentSyntaxHelpers.TryReadPropertyNameList(invocation);

        Assert.Null(names);
    }

    [Fact]
    public void FindChainedCall_ImmediateNextCallMatches_ReturnsIt()
    {
        var wrapped = """
            public class C
            {
                void M()
                {
                    entity.HasOne(d => d.Customer).WithMany(p => p.Orders);
                }
            }
            """;
        var tree = CSharpSyntaxTree.ParseText(wrapped);
        var root = tree.GetCompilationUnitRoot();
        var hasOneCall = root.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(i => i.Expression is MemberAccessExpressionSyntax { Name.Identifier.Text: "HasOne" });

        var chained = EfSchemaVisualizer.Core.Parsing.FluentSyntaxHelpers.FindChainedCall(hasOneCall, "WithMany");

        Assert.NotNull(chained);
        Assert.Equal("WithMany", ((MemberAccessExpressionSyntax)chained!.Expression).Name.Identifier.Text);
    }

    [Fact]
    public void FindChainedCall_NextCallHasDifferentName_ReturnsNull()
    {
        var wrapped = """
            public class C
            {
                void M()
                {
                    entity.HasOne(d => d.Customer).WithOne(p => p.Person);
                }
            }
            """;
        var tree = CSharpSyntaxTree.ParseText(wrapped);
        var root = tree.GetCompilationUnitRoot();
        var hasOneCall = root.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(i => i.Expression is MemberAccessExpressionSyntax { Name.Identifier.Text: "HasOne" });

        var chained = EfSchemaVisualizer.Core.Parsing.FluentSyntaxHelpers.FindChainedCall(hasOneCall, "WithMany");

        Assert.Null(chained);
    }

    [Fact]
    public void FindChainedCall_NothingChained_ReturnsNull()
    {
        var invocation = ParseSingleInvocation("entity.HasOne(d => d.Customer)");

        var chained = EfSchemaVisualizer.Core.Parsing.FluentSyntaxHelpers.FindChainedCall(invocation, "WithMany");

        Assert.Null(chained);
    }

    [Theory]
    [InlineData("ICollection<Order>", "Order")]
    [InlineData("IList<Order>", "Order")]
    [InlineData("List<Order>", "Order")]
    [InlineData("IEnumerable<Order>", "Order")]
    [InlineData("HashSet<Order>", "Order")]
    [InlineData("ISet<Order>", "Order")]
    [InlineData("Order[]", "Order")]
    [InlineData("Order", "Order")]
    public void TryGetElementTypeName_RecognizedShapes_ReturnsElementType(string clrType, string expected)
    {
        var result = EfSchemaVisualizer.Core.Parsing.FluentSyntaxHelpers.TryGetElementTypeName(clrType);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void TryGetElementTypeName_UnrecognizedGenericWrapper_ReturnsNull()
    {
        var result = EfSchemaVisualizer.Core.Parsing.FluentSyntaxHelpers.TryGetElementTypeName("IQueryable<Order>");

        Assert.Null(result);
    }

    private static CompilationUnitSyntax ParseRoot(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        return tree.GetCompilationUnitRoot();
    }

    [Fact]
    public void FindConfigurationScopes_EntityGenericStyle_ReturnsInvocationScope()
    {
        var root = ParseRoot("""
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
            """);

        var scopes = FluentSyntaxHelpers.FindConfigurationScopes(root).ToList();

        var scope = Assert.Single(scopes);
        Assert.Equal("Person", scope.EntityName);
        Assert.IsType<InvocationExpressionSyntax>(scope.Scope);
    }

    [Fact]
    public void FindConfigurationScopes_EntityTypeConfigurationClass_ReturnsConfigureMethodScope()
    {
        var root = ParseRoot("""
            public class PersonConfiguration : IEntityTypeConfiguration<Person>
            {
                public void Configure(EntityTypeBuilder<Person> builder)
                {
                    builder.Property(e => e.Name).HasMaxLength(100);
                }
            }
            """);

        var scopes = FluentSyntaxHelpers.FindConfigurationScopes(root).ToList();

        var scope = Assert.Single(scopes);
        Assert.Equal("Person", scope.EntityName);
        Assert.IsType<MethodDeclarationSyntax>(scope.Scope);
        Assert.Equal("Configure", ((MethodDeclarationSyntax)scope.Scope).Identifier.Text);
    }

    [Fact]
    public void FindConfigurationScopes_MultipleEntityTypeConfigurationClasses_ReturnsAllScopes()
    {
        var root = ParseRoot("""
            public class PersonConfiguration : IEntityTypeConfiguration<Person>
            {
                public void Configure(EntityTypeBuilder<Person> builder)
                {
                    builder.Property(e => e.Name).HasMaxLength(100);
                }
            }

            public class AddressConfiguration : IEntityTypeConfiguration<Address>
            {
                public void Configure(EntityTypeBuilder<Address> builder)
                {
                    builder.Property(e => e.Line1).HasMaxLength(200);
                }
            }
            """);

        var scopes = FluentSyntaxHelpers.FindConfigurationScopes(root).ToList();

        Assert.Equal(2, scopes.Count);
        Assert.Contains(scopes, s => s.EntityName == "Person");
        Assert.Contains(scopes, s => s.EntityName == "Address");
    }

    [Fact]
    public void FindConfigurationScopes_MixedStyles_ReturnsBoth()
    {
        var root = ParseRoot("""
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

            public class AddressConfiguration : IEntityTypeConfiguration<Address>
            {
                public void Configure(EntityTypeBuilder<Address> builder)
                {
                    builder.Property(e => e.Line1).HasMaxLength(200);
                }
            }
            """);

        var scopes = FluentSyntaxHelpers.FindConfigurationScopes(root).ToList();

        Assert.Equal(2, scopes.Count);
        Assert.Contains(scopes, s => s.EntityName == "Person" && s.Scope is InvocationExpressionSyntax);
        Assert.Contains(scopes, s => s.EntityName == "Address" && s.Scope is MethodDeclarationSyntax);
    }

    [Fact]
    public void FindConfigurationScopes_QualifiedInterfaceName_ResolvesSameAsBareForm()
    {
        var root = ParseRoot("""
            public class PersonConfiguration : Microsoft.EntityFrameworkCore.IEntityTypeConfiguration<Person>
            {
                public void Configure(EntityTypeBuilder<Person> builder)
                {
                    builder.Property(e => e.Name).HasMaxLength(100);
                }
            }
            """);

        var scopes = FluentSyntaxHelpers.FindConfigurationScopes(root).ToList();

        var scope = Assert.Single(scopes);
        Assert.Equal("Person", scope.EntityName);
    }

    [Fact]
    public void FindConfigurationScopes_ClassImplementsInterfaceWithNoConfigureMethod_YieldsNoScope()
    {
        var root = ParseRoot("""
            public class PersonConfiguration : IEntityTypeConfiguration<Person>
            {
            }
            """);

        var scopes = FluentSyntaxHelpers.FindConfigurationScopes(root).ToList();

        Assert.Empty(scopes);
    }

    [Fact]
    public void FindConfigurationScopes_UnrelatedGenericInterface_IsIgnored()
    {
        var root = ParseRoot("""
            public class PersonValidator : IValidatableObject<Person>
            {
                public void Configure(EntityTypeBuilder<Person> builder)
                {
                }
            }
            """);

        var scopes = FluentSyntaxHelpers.FindConfigurationScopes(root).ToList();

        Assert.Empty(scopes);
    }
}
