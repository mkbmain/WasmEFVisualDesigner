using System.Linq;
using EfSchemaVisualizer.Web.Diagram;
using Xunit;

namespace EfSchemaVisualizer.Web.Tests.Diagram;

public class DiagramEditorOwnedTypeTests
{
    private const string ClassSource = """
        public class Order
        {
            public int Id { get; set; }
            public Address ShippingAddress { get; set; }
        }

        public class Address
        {
            public string Street { get; set; }
        }
        """;

    private const string ConfigSource = """
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

    [Fact]
    public void RenameProperty_FoldedOwnedProperty_RenamesOnAddressClassNotOrder()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);

        var result = editor.RenameProperty("Order", "Street", "StreetLine1");

        Assert.True(result.Success);
        Assert.Contains("public string StreetLine1 { get; set; }", editor.ClassSource);
        Assert.DoesNotContain("public string Street {", editor.ClassSource);

        var order = editor.Current.Entities.Single(e => e.Name == "Order");
        Assert.Contains(order.Properties, p => p.Name == "StreetLine1");
    }

    [Fact]
    public void RemoveProperty_FoldedOwnedProperty_RemovesFromAddressClass()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);

        var result = editor.RemoveProperty("Order", "Street");

        Assert.True(result.Success);
        Assert.DoesNotContain("public string Street { get; set; }", editor.ClassSource);

        var order = editor.Current.Entities.Single(e => e.Name == "Order");
        Assert.DoesNotContain(order.Properties, p => p.Name == "Street");
    }

    [Fact]
    public void ChangePropertyType_FoldedOwnedProperty_ChangesOnAddressClassNotOrder()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);

        var result = editor.ChangePropertyType("Order", "Street", "string", newIsNullable: true);

        Assert.True(result.Success);
        Assert.Contains("public string? Street { get; set; }", editor.ClassSource);
    }
}
