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

    [Fact]
    public void CommitColumnName_FoldedOwnedProperty_WritesIntoOwnsOneBuilderLambdaNotABogusEntityBlock()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);

        var street = editor.Current.Entities.Single(e => e.Name == "Order").Properties.Single(p => p.Name == "Street");
        var result = editor.SetColumnName("Order", street.Name, "shipping_street");

        Assert.True(result.Success);
        Assert.DoesNotContain("Entity<Address>", editor.ConfigSource);
        Assert.Contains("OwnsOne(e => e.ShippingAddress, b =>", editor.ConfigSource);
        Assert.Contains("HasColumnName(\"shipping_street\")", editor.ConfigSource);

        var street2 = editor.Current.Entities.Single(e => e.Name == "Order").Properties.Single(p => p.Name == "Street");
        Assert.Equal("shipping_street", street2.ColumnName);
    }

    [Fact]
    public void SetMaxLength_FoldedOwnedProperty_WritesIntoOwnsOneBuilderLambda()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);

        var result = editor.SetMaxLength("Order", "Street", 100);

        Assert.True(result.Success);
        Assert.DoesNotContain("Entity<Address>", editor.ConfigSource);
        Assert.Contains("OwnsOne(e => e.ShippingAddress, b =>", editor.ConfigSource);
        Assert.Contains("HasMaxLength(100)", editor.ConfigSource);

        var street = editor.Current.Entities.Single(e => e.Name == "Order").Properties.Single(p => p.Name == "Street");
        Assert.Equal(100, street.MaxLength);
    }

    [Fact]
    public void SetRowVersion_FoldedOwnedProperty_WritesIntoOwnsOneBuilderLambda()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);

        var result = editor.SetRowVersion("Order", "Street", true);

        Assert.True(result.Success);
        Assert.DoesNotContain("Entity<Address>", editor.ConfigSource);
        Assert.Contains("OwnsOne(e => e.ShippingAddress, b =>", editor.ConfigSource);
        Assert.Contains("IsRowVersion()", editor.ConfigSource);

        var street = editor.Current.Entities.Single(e => e.Name == "Order").Properties.Single(p => p.Name == "Street");
        Assert.True(street.IsRowVersion);
    }

    [Fact]
    public void SetDefaultValueSql_FoldedOwnedProperty_WritesIntoOwnsOneBuilderLambda()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);

        var result = editor.SetDefaultValueSql("Order", "Street", "GETDATE()");

        Assert.True(result.Success);
        Assert.DoesNotContain("Entity<Address>", editor.ConfigSource);
        Assert.Contains("OwnsOne(e => e.ShippingAddress, b =>", editor.ConfigSource);
        Assert.Contains("HasDefaultValueSql(\"GETDATE()\")", editor.ConfigSource);

        var street = editor.Current.Entities.Single(e => e.Name == "Order").Properties.Single(p => p.Name == "Street");
        Assert.Equal("GETDATE()", street.DefaultValueSql);
    }

    [Fact]
    public void SetColumnName_FoldedOwnedProperty_ExistingConfig_MutatesInPlace()
    {
        const string configWithExistingColumnName = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Order>(entity =>
                    {
                        entity.OwnsOne(e => e.ShippingAddress, b =>
                        {
                            b.Property(a => a.Street).HasColumnName("old_street");
                        });
                    });
                }
            }
            """;
        var editor = new DiagramEditor(ClassSource, configWithExistingColumnName);

        var result = editor.SetColumnName("Order", "Street", "new_street");

        Assert.True(result.Success);
        Assert.DoesNotContain("old_street", editor.ConfigSource);
        Assert.Contains("HasColumnName(\"new_street\")", editor.ConfigSource);
    }

    [Fact]
    public void SetColumnName_FoldedOwnedProperty_NullClearsExistingColumnName()
    {
        const string configWithExistingColumnName = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Order>(entity =>
                    {
                        entity.OwnsOne(e => e.ShippingAddress, b =>
                        {
                            b.Property(a => a.Street).HasColumnName("shipping_street");
                        });
                    });
                }
            }
            """;
        var editor = new DiagramEditor(ClassSource, configWithExistingColumnName);

        var result = editor.SetColumnName("Order", "Street", null);

        Assert.True(result.Success);
        Assert.DoesNotContain("HasColumnName", editor.ConfigSource);

        var street = editor.Current.Entities.Single(e => e.Name == "Order").Properties.Single(p => p.Name == "Street");
        Assert.Null(street.ColumnName);
    }
}
