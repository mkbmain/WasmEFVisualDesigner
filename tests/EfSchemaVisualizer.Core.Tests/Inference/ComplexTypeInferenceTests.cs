using System.Collections.Generic;
using System.Linq;
using EfSchemaVisualizer.Core.Inference;
using EfSchemaVisualizer.Core.Merging;
using EfSchemaVisualizer.Core.Model;
using Xunit;

namespace EfSchemaVisualizer.Core.Tests.Inference;

public class ComplexTypeInferenceTests
{
    private static PropertyModel Property(string name, string clrType) =>
        new(name, clrType, IsNullable: false, MaxLength: null);

    [Fact]
    public void Fold_NoComplexCalls_ReturnsEntitiesUnchanged()
    {
        var order = new EntityModel("Order", new[] { Property("Id", "int") }, KeyPropertyNames: new[] { "Id" });

        var result = ComplexTypeInference.Fold(new[] { order }, Array.Empty<ComplexTypeConfig>());

        Assert.Same(order, Assert.Single(result.Entities));
    }

    [Fact]
    public void Fold_ComplexProperty_RemovesNavPropertyAndSplicesInComplexProperties()
    {
        var order = new EntityModel(
            "Order",
            new[] { Property("Id", "int"), Property("ShippingAddress", "Address") },
            KeyPropertyNames: new[] { "Id" });
        var address = new EntityModel("Address", new[] { Property("Street", "string"), Property("City", "string") });

        var result = ComplexTypeInference.Fold(
            new[] { order, address },
            new[] { new ComplexTypeConfig("Order", "ShippingAddress") });

        var foldedOrder = Assert.Single(result.Entities);
        Assert.Equal(new[] { "Id", "Street", "City" }, foldedOrder.Properties.Select(p => p.Name));

        var street = foldedOrder.Properties.Single(p => p.Name == "Street");
        Assert.Equal(FoldKind.Complex, street.FoldKind);
        Assert.Equal("ShippingAddress", street.OwnerNavigationProperty);
    }

    [Fact]
    public void Fold_ComplexPropertyTarget_AbsentFromResult()
    {
        var order = new EntityModel("Order", new[] { Property("ShippingAddress", "Address") });
        var address = new EntityModel("Address", new[] { Property("Street", "string") });

        var result = ComplexTypeInference.Fold(
            new[] { order, address },
            new[] { new ComplexTypeConfig("Order", "ShippingAddress") });

        Assert.DoesNotContain(result.Entities, e => e.Name == "Address");
    }

    [Fact]
    public void Fold_MultiLevelComplexChain_FoldsTransitively()
    {
        var order = new EntityModel("Order", new[] { Property("ShippingAddress", "Address") });
        var address = new EntityModel("Address", new[] { Property("Street", "string"), Property("Country", "Country") });
        var country = new EntityModel("Country", new[] { Property("Name", "string") });

        var result = ComplexTypeInference.Fold(
            new[] { order, address, country },
            new[]
            {
                new ComplexTypeConfig("Order", "ShippingAddress"),
                new ComplexTypeConfig("Address", "Country"),
            });

        var foldedOrder = Assert.Single(result.Entities);
        Assert.Equal(new[] { "Street", "Name" }, foldedOrder.Properties.Select(p => p.Name));
    }

    [Fact]
    public void Fold_MalformedComplexCycle_DoesNotThrowAndStopsAtCycle()
    {
        var a = new EntityModel("A", new[] { Property("BNav", "B") });
        var b = new EntityModel("B", new[] { Property("ANav", "A") });

        var result = ComplexTypeInference.Fold(
            new[] { a, b },
            new[]
            {
                new ComplexTypeConfig("A", "BNav"),
                new ComplexTypeConfig("B", "ANav"),
            });

        Assert.True(result.Entities.Count >= 1);
    }
}
