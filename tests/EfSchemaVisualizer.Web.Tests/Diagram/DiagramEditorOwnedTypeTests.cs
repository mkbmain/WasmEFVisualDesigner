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
    public void RenameProperty_FoldedOwnedProperty_UpdatesExistingConfigReferenceAndKeepsItsValue()
    {
        const string configWithExistingMaxLength = """
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
        var editor = new DiagramEditor(ClassSource, configWithExistingMaxLength);

        var result = editor.RenameProperty("Order", "Street", "StreetLine1");

        Assert.True(result.Success);
        Assert.Contains("public string StreetLine1 { get; set; }", editor.ClassSource);
        Assert.DoesNotContain("public string Street {", editor.ClassSource);

        // The config's property selector must follow the rename, not just the class declaration —
        // otherwise `b.Property(a => a.Street)` would reference a property that no longer exists
        // (non-compiling) and its HasMaxLength(100) would silently stop applying.
        Assert.Contains("a.StreetLine1", editor.ConfigSource);
        Assert.DoesNotContain("a.Street)", editor.ConfigSource);
        Assert.Contains("HasMaxLength(100)", editor.ConfigSource);

        var street = editor.Current.Entities.Single(e => e.Name == "Order").Properties.Single(p => p.Name == "StreetLine1");
        Assert.Equal(100, street.MaxLength);
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
    public void SetValueConversion_FoldedOwnedProperty_WritesIntoOwnsOneBuilderLambda()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);

        var result = editor.SetValueConversion("Order", "Street", "string");

        Assert.True(result.Success);
        Assert.DoesNotContain("Entity<Address>", editor.ConfigSource);
        Assert.Contains("OwnsOne(e => e.ShippingAddress, b =>", editor.ConfigSource);
        Assert.Contains("HasConversion<string>()", editor.ConfigSource);

        var street = editor.Current.Entities.Single(e => e.Name == "Order").Properties.Single(p => p.Name == "Street");
        Assert.Equal("string", street.ConversionProviderClrType);
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

    [Fact]
    public void SetRequiredOverride_FoldedOwnedProperty_WritesIntoOwnsOneBuilderLambda()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);

        var result = editor.SetRequiredOverride("Order", "Street", true);

        Assert.True(result.Success);
        Assert.DoesNotContain("Entity<Address>", editor.ConfigSource);
        Assert.Contains("OwnsOne(e => e.ShippingAddress, b =>", editor.ConfigSource);
        Assert.Contains("IsRequired()", editor.ConfigSource);

        var street = editor.Current.Entities.Single(e => e.Name == "Order").Properties.Single(p => p.Name == "Street");
        Assert.True(street.IsRequiredOverride);

        var clearResult = editor.SetRequiredOverride("Order", "Street", null);

        Assert.True(clearResult.Success);
        Assert.DoesNotContain("IsRequired()", editor.ConfigSource);

        var street2 = editor.Current.Entities.Single(e => e.Name == "Order").Properties.Single(p => p.Name == "Street");
        Assert.Null(street2.IsRequiredOverride);
    }

    // Order owns Address (nav "ShippingAddress") via a bare OwnsOne call on Order's own scope, and
    // Address separately owns Country (nav "Country") via its own top-level Entity<Address>() scope.
    // OwnedTypeInference.Fold resolves this transitively so Country.Code ends up folded all the way
    // onto Order — but (by design, for UI-grouping purposes only — see OwnedTypeInference.Fold's
    // re-stamping comment) Code.OwnerNavigationProperty gets overwritten to the OUTERMOST nav
    // ("ShippingAddress") while Code.DeclaringEntityName correctly stays "Country". Routing a fluent
    // edit for "Code" through "ShippingAddress" would resolve Address's OwnsOne builder lambda, not
    // Country's — Address has no Code property, so the edit would be bogus and silently lost. Street
    // (declared directly on Address, the immediate target of ShippingAddress) is the control case:
    // single-level from Order's perspective, so it must remain fully editable in this same fixture.
    private const string MultiLevelClassSource = """
        public class Order
        {
            public int Id { get; set; }
            public Address ShippingAddress { get; set; }
        }

        public class Address
        {
            public string Street { get; set; }
            public Country Country { get; set; }
        }

        public class Country
        {
            public string Code { get; set; }
        }
        """;

    private const string MultiLevelConfigSource = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Order>(entity =>
                {
                    entity.OwnsOne(e => e.ShippingAddress);
                });

                modelBuilder.Entity<Address>(entity =>
                {
                    entity.OwnsOne(a => a.Country);
                });
            }
        }
        """;

    [Fact]
    public void SetColumnName_MultiLevelFoldedProperty_FailsCleanlyWithoutCorruptingConfig()
    {
        var editor = new DiagramEditor(MultiLevelClassSource, MultiLevelConfigSource);

        var code = editor.Current.Entities.Single(e => e.Name == "Order").Properties.Single(p => p.Name == "Code");
        Assert.Equal("Country", code.DeclaringEntityName);
        Assert.Equal("ShippingAddress", code.OwnerNavigationProperty);

        var result = editor.SetColumnName("Order", "Code", "country_code");

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Equal(MultiLevelConfigSource, editor.ConfigSource);

        var codeAfter = editor.Current.Entities.Single(e => e.Name == "Order").Properties.Single(p => p.Name == "Code");
        Assert.Null(codeAfter.ColumnName);
    }

    [Fact]
    public void RenameProperty_OwnerNavigationProperty_PatchesOuterOwnsOneCallAndPropertyDeclaration()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);

        var result = editor.RenameProperty("Order", "ShippingAddress", "DeliveryAddress");

        Assert.True(result.Success);
        Assert.Contains("public Address DeliveryAddress { get; set; }", editor.ClassSource);
        Assert.Contains("OwnsOne(e => e.DeliveryAddress", editor.ConfigSource);
        Assert.DoesNotContain("ShippingAddress", editor.ClassSource);
        Assert.DoesNotContain("ShippingAddress", editor.ConfigSource);

        var order = editor.Current.Entities.Single(e => e.Name == "Order");
        Assert.Contains(order.Properties, p => p.Name == "Street" && p.OwnerNavigationProperty == "DeliveryAddress");
    }

    // Regression for a review finding: the guard that lets an owner's own nav property (folded out of
    // Properties entirely) through must stay precise — it must not also admit a genuinely unknown
    // property name that never appears in the model at all, in either the plain-property or the
    // OwnerNavigationProperty-stamped form.
    [Fact]
    public void RenameProperty_UnknownProperty_StillFails()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);

        var result = editor.RenameProperty("Order", "DoesNotExist", "Whatever");

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Equal(ConfigSource, editor.ConfigSource);
        Assert.Equal(ClassSource, editor.ClassSource);
    }

    // Regression for a review finding: an Ignore()d/[NotMapped] property is also removed from
    // entity.Properties by ModelMerger.ApplyIgnoredProperties — the same symptom as a folded-away
    // owner nav property, but for a different reason. Renaming through the class-declares-it fallback
    // let this through incorrectly, leaving a dangling Ignore(e => e.OldName) reference in config
    // after rename. An ignored property never stamps any OwnerNavigationProperty on another property
    // (that's only stamped by OwnedTypeInference.Fold/ComplexTypeInference.Fold), so it must still be
    // rejected.
    [Fact]
    public void RenameProperty_IgnoredProperty_FailsRatherThanLeavingDanglingIgnoreReference()
    {
        const string classSourceWithDiscount = """
            public class Order
            {
                public int Id { get; set; }
                public string Discount { get; set; }
            }
            """;
        const string configSourceIgnoringDiscount = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Order>(entity =>
                    {
                        entity.Ignore(e => e.Discount);
                    });
                }
            }
            """;
        var editor = new DiagramEditor(classSourceWithDiscount, configSourceIgnoringDiscount);

        var order = editor.Current.Entities.Single(e => e.Name == "Order");
        Assert.DoesNotContain(order.Properties, p => p.Name == "Discount");

        var result = editor.RenameProperty("Order", "Discount", "Rebate");

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Equal(classSourceWithDiscount, editor.ClassSource);
        Assert.Equal(configSourceIgnoringDiscount, editor.ConfigSource);
    }

    [Fact]
    public void SetColumnName_SingleLevelFoldedPropertyInMultiLevelFixture_StillSucceedsAndRoundTrips()
    {
        var editor = new DiagramEditor(MultiLevelClassSource, MultiLevelConfigSource);

        var result = editor.SetColumnName("Order", "Street", "shipping_street");

        Assert.True(result.Success);
        Assert.Contains("HasColumnName(\"shipping_street\")", editor.ConfigSource);

        var street = editor.Current.Entities.Single(e => e.Name == "Order").Properties.Single(p => p.Name == "Street");
        Assert.Equal("shipping_street", street.ColumnName);
    }
}
