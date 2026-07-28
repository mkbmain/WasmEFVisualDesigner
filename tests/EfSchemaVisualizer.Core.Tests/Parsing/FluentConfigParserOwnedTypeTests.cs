using System.Linq;
using EfSchemaVisualizer.Core.Parsing;
using Xunit;

namespace EfSchemaVisualizer.Core.Tests.Parsing;

public class FluentConfigParserOwnedTypeTests
{
    private static readonly FluentConfigParser Parser = new();

    [Fact]
    public void ParseOwnedTypeCalls_OwnsOne_ResolvesOwnerAndNavigationProperty()
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

        var result = Parser.ParseOwnedTypeCalls(source);

        var config = Assert.Single(result.Value);
        Assert.Equal("Order", config.OwnerEntityName);
        Assert.Equal("ShippingAddress", config.NavigationPropertyName);
        Assert.False(config.IsMany);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void ParseOwnedTypeCalls_OwnsMany_SetsIsManyTrue()
    {
        const string source = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Order>(entity =>
                    {
                        entity.OwnsMany(e => e.Notes);
                    });
                }
            }
            """;

        var result = Parser.ParseOwnedTypeCalls(source);

        var config = Assert.Single(result.Value);
        Assert.True(config.IsMany);
        Assert.Equal("Notes", config.NavigationPropertyName);
    }

    [Fact]
    public void ParseOwnedTypeCalls_BuilderLambdaWithToTable_FiresOwnedNestedConfigIgnored()
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
                            b.ToTable("Addresses");
                        });
                    });
                }
            }
            """;

        var result = Parser.ParseOwnedTypeCalls(source);

        Assert.Single(result.Value);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.OwnedNestedConfigIgnored, diagnostic.Code);
    }

    [Fact]
    public void ParseOwnedTypeCalls_BuilderLambdaWithHasMaxLength_NoLongerFiresOwnedNestedConfigIgnored()
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

        var result = Parser.ParseOwnedTypeCalls(source);

        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void ParseOwnedTypeCalls_BuilderLambdaWithNoCalls_NoDiagnostic()
    {
        const string source = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Order>(entity =>
                    {
                        entity.OwnsOne(e => e.ShippingAddress, b => { });
                    });
                }
            }
            """;

        var result = Parser.ParseOwnedTypeCalls(source);

        Assert.Single(result.Value);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void ParseOwnedTypeCalls_ExpressionBodiedBuilderWithHasMaxLength_FiresOwnedNestedConfigIgnored()
    {
        // An expression-bodied builder lambda (`b => b.Property(...).HasMaxLength(100)`) never gets
        // a nested scope from FluentSyntaxHelpers' scope discovery — only block-bodied builders
        // (`b => { ... }`) do. Before this fix, that meant HasMaxLength here was silently dropped
        // with no diagnostic at all (MaxLength reads back null with zero indication why).
        const string source = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Order>(entity =>
                    {
                        entity.OwnsOne(e => e.ShippingAddress, b => b.Property(a => a.Street).HasMaxLength(100));
                    });
                }
            }
            """;

        var result = Parser.ParseOwnedTypeCalls(source);

        Assert.Single(result.Value);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.OwnedNestedConfigIgnored, diagnostic.Code);
    }

    [Fact]
    public void ParseOwnedTypeCalls_ExpressionBodiedBuilderWithNoCalls_NoDiagnostic()
    {
        // An expression-bodied builder with no invocation at all has nothing to lose, so it should
        // not be flagged as "something was ignored" — mirrors the block-bodied empty-builder case
        // above (ParseOwnedTypeCalls_BuilderLambdaWithNoCalls_NoDiagnostic).
        const string source = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Order>(entity =>
                    {
                        entity.OwnsOne(e => e.ShippingAddress, b => b);
                    });
                }
            }
            """;

        var result = Parser.ParseOwnedTypeCalls(source);

        Assert.Single(result.Value);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void ParseUnrecognizedCalls_OwnsOneCall_NoLongerFlagged()
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

        var diagnostics = Parser.ParseUnrecognizedCalls(source);

        Assert.Empty(diagnostics);
    }
}
