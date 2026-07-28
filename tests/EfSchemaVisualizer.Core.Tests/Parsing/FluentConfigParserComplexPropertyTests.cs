using System.Linq;
using EfSchemaVisualizer.Core.Model;
using EfSchemaVisualizer.Core.Parsing;
using Xunit;

namespace EfSchemaVisualizer.Core.Tests.Parsing;

public class FluentConfigParserComplexPropertyTests
{
    private static readonly FluentConfigParser Parser = new();

    private static PropertyModel Property(string name, string clrType) =>
        new(name, clrType, IsNullable: false, MaxLength: null);

    [Fact]
    public void ParseComplexPropertyCalls_SingularTarget_ResolvesOwnerAndNavigationProperty()
    {
        const string source = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Order>(entity =>
                    {
                        entity.ComplexProperty(e => e.ShippingAddress);
                    });
                }
            }
            """;

        var entities = new[]
        {
            new EntityModel("Order", new[] { Property("ShippingAddress", "Address") }),
            new EntityModel("Address", new[] { Property("Street", "string") }),
        };

        var result = Parser.ParseComplexPropertyCalls(source, entities);

        var config = Assert.Single(result.Value);
        Assert.Equal("Order", config.OwnerEntityName);
        Assert.Equal("ShippingAddress", config.NavigationPropertyName);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void ParseComplexPropertyCalls_CollectionTypedTarget_FlagsCollectionUnsupportedAndDoesNotFold()
    {
        const string source = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Order>(entity =>
                    {
                        entity.ComplexProperty(e => e.Tags);
                    });
                }
            }
            """;

        var entities = new[]
        {
            new EntityModel("Order", new[] { Property("Tags", "List<Tag>") }),
            new EntityModel("Tag", new[] { Property("Value", "string") }),
        };

        var result = Parser.ParseComplexPropertyCalls(source, entities);

        Assert.Empty(result.Value);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.ComplexPropertyCollectionUnsupported, diagnostic.Code);
        Assert.Equal("Order", diagnostic.EntityName);
        Assert.Equal("Tags", diagnostic.PropertyName);
    }

    [Fact]
    public void ParseUnrecognizedCalls_ComplexPropertyCall_NoLongerFlagged()
    {
        const string source = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Order>(entity =>
                    {
                        entity.ComplexProperty(e => e.ShippingAddress);
                    });
                }
            }
            """;

        Assert.Empty(Parser.ParseUnrecognizedCalls(source));
    }

    [Fact]
    public void DiagnosticCodes_ComplexNestedConfigIgnored_Exists()
    {
        Assert.Equal("ComplexNestedConfigIgnored", DiagnosticCodes.ComplexNestedConfigIgnored);
    }

    [Fact]
    public void ParseComplexPropertyCalls_BuilderLambdaWithWithOwner_FiresComplexNestedConfigIgnored()
    {
        const string source = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Order>(entity =>
                    {
                        entity.ComplexProperty(e => e.ShippingAddress, b =>
                        {
                            b.WithOwner();
                        });
                    });
                }
            }
            """;

        var entities = new[]
        {
            new EntityModel("Order", new[] { Property("ShippingAddress", "Address") }),
            new EntityModel("Address", new[] { Property("Street", "string") }),
        };

        var result = Parser.ParseComplexPropertyCalls(source, entities);

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.ComplexNestedConfigIgnored, diagnostic.Code);
    }

    [Fact]
    public void ParseComplexPropertyCalls_BuilderLambdaWithHasColumnName_NoLongerFiresComplexNestedConfigIgnored()
    {
        const string source = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Order>(entity =>
                    {
                        entity.ComplexProperty(e => e.ShippingAddress, b =>
                        {
                            b.Property(a => a.Street).HasColumnName("street_name");
                        });
                    });
                }
            }
            """;

        var entities = new[]
        {
            new EntityModel("Order", new[] { Property("ShippingAddress", "Address") }),
            new EntityModel("Address", new[] { Property("Street", "string") }),
        };

        var result = Parser.ParseComplexPropertyCalls(source, entities);

        Assert.Empty(result.Diagnostics);
    }
}
