using System.Collections.Generic;
using System.Linq;
using EfSchemaVisualizer.Core.Inference;
using EfSchemaVisualizer.Core.Model;
using Xunit;

namespace EfSchemaVisualizer.Core.Tests.Inference;

public class EnumStorageInferenceTests
{
    [Fact]
    public void Fold_PropertyClrTypeMatchesKnownEnum_SetsIsEnumTypeAndUnderlyingType()
    {
        var entity = new EntityModel("Person", new List<PropertyModel>
        {
            new("Status", "Status", IsNullable: false, MaxLength: null),
            new("Name", "string", IsNullable: false, MaxLength: null),
        });

        var enumUnderlyingTypes = new Dictionary<string, string> { ["Status"] = "int" };

        var result = EnumStorageInference.Fold(new[] { entity }, enumUnderlyingTypes);

        var status = result.Single().Properties.Single(p => p.Name == "Status");
        Assert.True(status.IsEnumType);
        Assert.Equal("int", status.EnumUnderlyingClrType);

        var name = result.Single().Properties.Single(p => p.Name == "Name");
        Assert.False(name.IsEnumType);
        Assert.Null(name.EnumUnderlyingClrType);
    }

    [Fact]
    public void Fold_NoMatchingEnum_LeavesPropertyUnchanged()
    {
        var entity = new EntityModel("Person", new List<PropertyModel>
        {
            new("Status", "UnknownType", IsNullable: false, MaxLength: null),
        });

        var result = EnumStorageInference.Fold(new[] { entity }, new Dictionary<string, string>());

        var status = result.Single().Properties.Single(p => p.Name == "Status");
        Assert.False(status.IsEnumType);
    }
}
