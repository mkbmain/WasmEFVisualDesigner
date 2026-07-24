using System.Linq;
using EfSchemaVisualizer.Core.Model;
using EfSchemaVisualizer.Web;
using Xunit;

namespace EfSchemaVisualizer.Web.Tests;

public class DiagramModelBuilderOwnedTypeTests
{
    [Fact]
    public void Build_OwnsOne_AddressNotStandaloneAndOrderHasFoldedProperties()
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
                        entity.OwnsOne(e => e.ShippingAddress);
                    });
                }
            }
            """;

        var result = DiagramModelBuilder.Build(classSource, configSource);

        Assert.DoesNotContain(result.Entities, e => e.Name == "Address");
        var order = result.Entities.Single(e => e.Name == "Order");
        Assert.Contains(order.Properties, p => p.Name == "Street" && p.IsOwned);
        Assert.Contains(order.Properties, p => p.Name == "City" && p.IsOwned);
        Assert.DoesNotContain(order.Properties, p => p.Name == "ShippingAddress");
    }

    [Fact]
    public void Build_OwnsMany_TargetKeptStandaloneMarkedOwnedWithOwnedRelationship()
    {
        const string classSource = """
            public class Order
            {
                public int Id { get; set; }
                public ICollection<OrderNote> Notes { get; set; }
            }

            public class OrderNote
            {
                public string Text { get; set; }
            }
            """;

        const string configSource = """
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

        var result = DiagramModelBuilder.Build(classSource, configSource);

        var note = result.Entities.Single(e => e.Name == "OrderNote");
        Assert.True(note.IsOwned);
        Assert.Contains(result.Relationships, r => r.Kind == RelationshipKind.Owned
            && r.PrincipalEntity == "Order" && r.DependentEntity == "OrderNote");
    }
}
