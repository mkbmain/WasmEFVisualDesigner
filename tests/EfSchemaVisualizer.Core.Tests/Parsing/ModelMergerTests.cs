using System.Collections.Generic;
using System.Linq;
using EfSchemaVisualizer.Core.Model;
using EfSchemaVisualizer.Core.Parsing;
using Xunit;

namespace EfSchemaVisualizer.Core.Tests.Parsing;

public class ModelMergerTests
{
    [Fact]
    public void ApplyMaxLengths_SetsMaxLengthOnMatchingProperty_LeavesOthersUntouched()
    {
        var entity = new EntityModel("Person", new List<PropertyModel>
        {
            new("Id", "int", IsNullable: false, MaxLength: null),
            new("Name", "string", IsNullable: true, MaxLength: null),
        });

        var configs = new List<MaxLengthConfig>
        {
            new("Person", "Name", 100),
            new("Address", "Line1", 200), // different entity, must not affect Person
        };

        var merged = ModelMerger.ApplyMaxLengths(entity, configs);

        Assert.Null(merged.Properties.Single(p => p.Name == "Id").MaxLength);
        Assert.Equal(100, merged.Properties.Single(p => p.Name == "Name").MaxLength);
    }

    [Fact]
    public void ApplyIsRequired_SetsIsRequiredOverrideOnMatchingProperty_LeavesOthersUntouched()
    {
        var entity = new EntityModel("Person", new List<PropertyModel>
        {
            new("Id", "int", IsNullable: false, MaxLength: null),
            new("Name", "string", IsNullable: true, MaxLength: null),
        });

        var configs = new List<IsRequiredConfig>
        {
            new("Person", "Name", true),
            new("Address", "Line1", false), // different entity, must not affect Person
        };

        var merged = ModelMerger.ApplyIsRequired(entity, configs);

        Assert.Null(merged.Properties.Single(p => p.Name == "Id").IsRequiredOverride);
        Assert.True(merged.Properties.Single(p => p.Name == "Name").IsRequiredOverride);
        // CLR-derived IsNullable is untouched by the fluent override.
        Assert.True(merged.Properties.Single(p => p.Name == "Name").IsNullable);
    }

    [Fact]
    public void ApplyKeys_SetsKeyPropertyNamesOnMatchingEntity_LeavesOthersUntouched()
    {
        var entity = new EntityModel("Person", new List<PropertyModel>
        {
            new("Id", "int", IsNullable: false, MaxLength: null),
            new("Name", "string", IsNullable: true, MaxLength: null),
        });

        var configs = new List<KeyConfig>
        {
            new("Person", new List<string> { "Id" }),
            new("Address", new List<string> { "Id" }), // different entity, must not affect Person
        };

        var merged = ModelMerger.ApplyKeys(entity, configs);

        Assert.Equal(new[] { "Id" }, merged.KeyPropertyNames);
        // Properties themselves are untouched by the merge.
        Assert.Equal(2, merged.Properties.Count);
    }

    [Fact]
    public void ApplyKeys_NoMatchingConfig_LeavesKeyPropertyNamesEmpty()
    {
        var entity = new EntityModel("Person", new List<PropertyModel>
        {
            new("Id", "int", IsNullable: false, MaxLength: null),
        });

        var configs = new List<KeyConfig>
        {
            new("Address", new List<string> { "Id" }),
        };

        var merged = ModelMerger.ApplyKeys(entity, configs);

        Assert.Empty(merged.KeyPropertyNames);
    }
}
