using System.Linq;
using EfSchemaVisualizer.Core.Model;
using EfSchemaVisualizer.Web;
using Xunit;

namespace EfSchemaVisualizer.Web.Tests;

public class DiagramModelBuilderComplexPropertyTests
{
    [Fact]
    public void Build_ComplexProperty_AddressNotStandaloneAndOrderHasFoldedComplexProperties()
    {
        const string classSource = """
            public class Order
            {
                public int Id { get; set; }
                public Address ShippingAddress { get; set; }
            }

            public class Address
            {
                public string Street { get; set; }
                public string City { get; set; }
            }
            """;

        const string configSource = """
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

        var result = DiagramModelBuilder.Build(classSource, configSource);

        Assert.DoesNotContain(result.Entities, e => e.Name == "Address");
        var order = result.Entities.Single(e => e.Name == "Order");
        Assert.Contains(order.Properties, p => p.Name == "Street" && p.FoldKind == FoldKind.Complex);
        Assert.Contains(order.Properties, p => p.Name == "City" && p.FoldKind == FoldKind.Complex);
        Assert.DoesNotContain(order.Properties, p => p.Name == "ShippingAddress");
        Assert.DoesNotContain(result.Relationships, r => r.DependentEntity == "Address" || r.PrincipalEntity == "Address");
    }
}
