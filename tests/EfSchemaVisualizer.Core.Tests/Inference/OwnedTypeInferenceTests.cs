using System.Collections.Generic;
using System.Linq;
using EfSchemaVisualizer.Core.Inference;
using EfSchemaVisualizer.Core.Merging;
using EfSchemaVisualizer.Core.Model;
using Xunit;

namespace EfSchemaVisualizer.Core.Tests.Inference;

public class OwnedTypeInferenceTests
{
    private static PropertyModel Property(string name, string clrType) =>
        new(name, clrType, IsNullable: false, MaxLength: null);

    [Fact]
    public void Fold_NoOwnedCalls_ReturnsEntitiesUnchangedAndNoRelationships()
    {
        var order = new EntityModel("Order", new[] { Property("Id", "int") }, KeyPropertyNames: new[] { "Id" });

        var result = OwnedTypeInference.Fold(new[] { order }, Array.Empty<OwnedTypeConfig>());

        Assert.Same(order, Assert.Single(result.Entities));
        Assert.Empty(result.Relationships);
    }

    [Fact]
    public void Fold_OwnsOne_RemovesNavPropertyAndSplicesInOwnedProperties()
    {
        var order = new EntityModel(
            "Order",
            new[] { Property("Id", "int"), Property("ShippingAddress", "Address") },
            KeyPropertyNames: new[] { "Id" });
        var address = new EntityModel("Address", new[] { Property("Street", "string"), Property("City", "string") });

        var result = OwnedTypeInference.Fold(
            new[] { order, address },
            new[] { new OwnedTypeConfig("Order", "ShippingAddress", IsMany: false) });

        var foldedOrder = Assert.Single(result.Entities);
        Assert.Equal("Order", foldedOrder.Name);
        Assert.Equal(new[] { "Id", "Street", "City" }, foldedOrder.Properties.Select(p => p.Name));
        Assert.DoesNotContain(foldedOrder.Properties, p => p.Name == "ShippingAddress");

        var street = foldedOrder.Properties.Single(p => p.Name == "Street");
        Assert.True(street.IsOwned);
        Assert.Equal("ShippingAddress", street.OwnerNavigationProperty);
        Assert.Empty(result.Relationships);
    }

    [Fact]
    public void Fold_OwnsOne_TargetEntityAbsentFromResult()
    {
        var order = new EntityModel("Order", new[] { Property("ShippingAddress", "Address") });
        var address = new EntityModel("Address", new[] { Property("Street", "string") });

        var result = OwnedTypeInference.Fold(
            new[] { order, address },
            new[] { new OwnedTypeConfig("Order", "ShippingAddress", IsMany: false) });

        Assert.DoesNotContain(result.Entities, e => e.Name == "Address");
    }

    [Fact]
    public void Fold_TwoOwnsOneNavsOfSameTargetType_BothGroupsFoldedIndependently()
    {
        var order = new EntityModel(
            "Order",
            new[] { Property("ShippingAddress", "Address"), Property("BillingAddress", "Address") });
        var address = new EntityModel("Address", new[] { Property("Street", "string") });

        var result = OwnedTypeInference.Fold(
            new[] { order, address },
            new[]
            {
                new OwnedTypeConfig("Order", "ShippingAddress", IsMany: false),
                new OwnedTypeConfig("Order", "BillingAddress", IsMany: false),
            });

        var foldedOrder = result.Entities.Single(e => e.Name == "Order");
        var streets = foldedOrder.Properties.Where(p => p.Name == "Street").ToList();
        Assert.Equal(2, streets.Count);
        Assert.Contains(streets, p => p.OwnerNavigationProperty == "ShippingAddress");
        Assert.Contains(streets, p => p.OwnerNavigationProperty == "BillingAddress");
    }

    [Fact]
    public void Fold_MultiLevelOwnedChain_FoldsTransitively()
    {
        var order = new EntityModel("Order", new[] { Property("ShippingAddress", "Address") });
        var address = new EntityModel("Address", new[] { Property("Street", "string"), Property("Country", "Country") });
        var country = new EntityModel("Country", new[] { Property("Name", "string") });

        var result = OwnedTypeInference.Fold(
            new[] { order, address, country },
            new[]
            {
                new OwnedTypeConfig("Order", "ShippingAddress", IsMany: false),
                new OwnedTypeConfig("Address", "Country", IsMany: false),
            });

        var foldedOrder = Assert.Single(result.Entities);
        Assert.Equal(new[] { "Street", "Name" }, foldedOrder.Properties.Select(p => p.Name));
    }

    [Fact]
    public void Fold_MalformedOwnershipCycle_DoesNotThrowAndStopsAtCycle()
    {
        var a = new EntityModel("A", new[] { Property("BNav", "B") });
        var b = new EntityModel("B", new[] { Property("ANav", "A") });

        var result = OwnedTypeInference.Fold(
            new[] { a, b },
            new[]
            {
                new OwnedTypeConfig("A", "BNav", IsMany: false),
                new OwnedTypeConfig("B", "ANav", IsMany: false),
            });

        Assert.True(result.Entities.Count >= 1);
    }

    [Fact]
    public void Fold_NavigationPropertyNotFoundOnOwner_LeavesEntitiesUnchanged()
    {
        var order = new EntityModel("Order", new[] { Property("Id", "int") });
        var address = new EntityModel("Address", new[] { Property("Street", "string") });

        var result = OwnedTypeInference.Fold(
            new[] { order, address },
            new[] { new OwnedTypeConfig("Order", "ShippingAddress", IsMany: false) });

        Assert.Equal(2, result.Entities.Count);
    }

    [Fact]
    public void Fold_TargetEntityTypeNotResolvable_LeavesEntitiesUnchanged()
    {
        var order = new EntityModel("Order", new[] { Property("ShippingAddress", "Address") });

        var result = OwnedTypeInference.Fold(
            new[] { order },
            new[] { new OwnedTypeConfig("Order", "ShippingAddress", IsMany: false) });

        var unchanged = Assert.Single(result.Entities);
        Assert.Contains(unchanged.Properties, p => p.Name == "ShippingAddress");
    }

    [Fact]
    public void Fold_OwnsMany_KeepsTargetStandaloneMarkedOwnedAndEmitsOwnedRelationship()
    {
        var order = new EntityModel("Order", new[] { Property("Notes", "ICollection<OrderNote>") });
        var note = new EntityModel("OrderNote", new[] { Property("Text", "string") });

        var result = OwnedTypeInference.Fold(
            new[] { order, note },
            new[] { new OwnedTypeConfig("Order", "Notes", IsMany: true) });

        Assert.Equal(2, result.Entities.Count);
        var foldedNote = result.Entities.Single(e => e.Name == "OrderNote");
        Assert.True(foldedNote.IsOwned);

        var relationship = Assert.Single(result.Relationships);
        Assert.Equal("Order", relationship.PrincipalEntity);
        Assert.Equal("OrderNote", relationship.DependentEntity);
        Assert.Equal(RelationshipKind.Owned, relationship.Kind);
        Assert.Equal("Notes", relationship.PrincipalNavigation);
        Assert.False(relationship.IsInferred);
    }
}
