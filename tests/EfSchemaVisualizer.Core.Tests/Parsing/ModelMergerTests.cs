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
    public void ApplyPrecisions_SetsPrecisionAndScaleOnMatchingProperty_LeavesOthersUntouched()
    {
        var entity = new EntityModel("Order", new List<PropertyModel>
        {
            new("Id", "int", IsNullable: false, MaxLength: null),
            new("Total", "decimal", IsNullable: false, MaxLength: null),
        });

        var configs = new List<PrecisionConfig>
        {
            new("Order", "Total", 18, 2),
            new("Address", "Line1", 10, null), // different entity, must not affect Order
        };

        var merged = ModelMerger.ApplyPrecisions(entity, configs);

        Assert.Null(merged.Properties.Single(p => p.Name == "Id").Precision);
        Assert.Equal(18, merged.Properties.Single(p => p.Name == "Total").Precision);
        Assert.Equal(2, merged.Properties.Single(p => p.Name == "Total").Scale);
    }

    [Fact]
    public void ApplyPrecisions_NoMatchingConfig_LeavesPrecisionAndScaleNull()
    {
        var entity = new EntityModel("Order", new List<PropertyModel>
        {
            new("Rate", "decimal", IsNullable: false, MaxLength: null),
        });

        var merged = ModelMerger.ApplyPrecisions(entity, new List<PrecisionConfig>());

        Assert.Null(merged.Properties.Single(p => p.Name == "Rate").Precision);
        Assert.Null(merged.Properties.Single(p => p.Name == "Rate").Scale);
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

    // ─── ApplyIndexes ────────────────────────────────────────────────────────────

    [Fact]
    public void ApplyIndexes_PopulatesIndexesFromMatchingConfig()
    {
        var entity = new EntityModel("Person", new List<PropertyModel>
        {
            new("Email", "string", IsNullable: true, MaxLength: null)
        });
        var configs = new List<IndexConfig>
        {
            new("Person", new List<string> { "Email" }, IsUnique: true, Name: "IX_Person_Email")
        };

        var result = ModelMerger.ApplyIndexes(entity, configs);

        var index = Assert.Single(result.Indexes);
        Assert.Equal(new[] { "Email" }, index.PropertyNames);
        Assert.True(index.IsUnique);
        Assert.Equal("IX_Person_Email", index.Name);
    }

    [Fact]
    public void ApplyIndexes_CollectsAllMatchingConfigsForSameEntity()
    {
        var entity = new EntityModel("Person", new List<PropertyModel>());
        var configs = new List<IndexConfig>
        {
            new("Person", new List<string> { "Email" }, IsUnique: true, Name: null),
            new("Person", new List<string> { "LastName", "FirstName" }, IsUnique: false, Name: null)
        };

        var result = ModelMerger.ApplyIndexes(entity, configs);

        Assert.Equal(2, result.Indexes.Count);
        Assert.Equal(new[] { "Email" }, result.Indexes[0].PropertyNames);
        Assert.Equal(new[] { "LastName", "FirstName" }, result.Indexes[1].PropertyNames);
    }

    [Fact]
    public void ApplyIndexes_LeavesIndexesEmptyWhenNoMatchingConfigs()
    {
        var entity = new EntityModel("Person", new List<PropertyModel>());
        var configs = new List<IndexConfig>
        {
            new("Order", new List<string> { "OrderDate" }, IsUnique: false, Name: null)
        };

        var result = ModelMerger.ApplyIndexes(entity, configs);

        Assert.Empty(result.Indexes);
    }

    [Fact]
    public void ApplyIndexes_DoesNotAffectPropertiesOrKeyPropertyNames()
    {
        var prop = new PropertyModel("Email", "string", IsNullable: false, MaxLength: null);
        var entity = new EntityModel("Person", new List<PropertyModel> { prop },
            KeyPropertyNames: new List<string> { "Id" });
        var configs = new List<IndexConfig>
        {
            new("Person", new List<string> { "Email" }, IsUnique: true, Name: null)
        };

        var result = ModelMerger.ApplyIndexes(entity, configs);

        Assert.Single(result.Properties);
        Assert.Equal("Email", result.Properties[0].Name);
        Assert.Equal(new[] { "Id" }, result.KeyPropertyNames);
    }

    [Fact]
    public void ApplyTableMapping_SetsTableNameAndSchema_OnMatchingEntity()
    {
        var entity = new EntityModel("Person", new List<PropertyModel>());

        var configs = new List<TableConfig>
        {
            new("Person", "People", "dbo"),
            new("Address", "Addresses", null), // different entity, must not affect Person
        };

        var merged = ModelMerger.ApplyTableMapping(entity, configs);

        Assert.Equal("People", merged.TableName);
        Assert.Equal("dbo", merged.Schema);
    }

    [Fact]
    public void ApplyTableMapping_NoMatchingConfig_LeavesTableNameAndSchemaNull()
    {
        var entity = new EntityModel("Person", new List<PropertyModel>());

        var merged = ModelMerger.ApplyTableMapping(entity, new List<TableConfig>());

        Assert.Null(merged.TableName);
        Assert.Null(merged.Schema);
    }

    [Fact]
    public void ApplyColumnNames_SetsColumnNameOnMatchingProperty_LeavesOthersUntouched()
    {
        var entity = new EntityModel("Person", new List<PropertyModel>
        {
            new("Id", "int", IsNullable: false, MaxLength: null),
            new("Name", "string", IsNullable: true, MaxLength: null),
        });

        var configs = new List<ColumnNameConfig>
        {
            new("Person", "Name", "full_name"),
            new("Address", "Line1", "line_1"), // different entity, must not affect Person
        };

        var merged = ModelMerger.ApplyColumnNames(entity, configs);

        Assert.Null(merged.Properties.Single(p => p.Name == "Id").ColumnName);
        Assert.Equal("full_name", merged.Properties.Single(p => p.Name == "Name").ColumnName);
    }

    [Fact]
    public void ApplyColumnTypes_SetsColumnTypeOnMatchingProperty_LeavesOthersUntouched()
    {
        var entity = new EntityModel("Order", new List<PropertyModel>
        {
            new("Id", "int", IsNullable: false, MaxLength: null),
            new("Total", "decimal", IsNullable: false, MaxLength: null),
        });

        var configs = new List<ColumnTypeConfig>
        {
            new("Order", "Total", "decimal(18,2)"),
        };

        var merged = ModelMerger.ApplyColumnTypes(entity, configs);

        Assert.Null(merged.Properties.Single(p => p.Name == "Id").ColumnType);
        Assert.Equal("decimal(18,2)", merged.Properties.Single(p => p.Name == "Total").ColumnType);
    }

    [Fact]
    public void ApplyDefaultValues_SetsDefaultValueLiteralOnMatchingProperty_LeavesOthersUntouched()
    {
        var entity = new EntityModel("Order", new List<PropertyModel>
        {
            new("Id", "int", IsNullable: false, MaxLength: null),
            new("Quantity", "int", IsNullable: false, MaxLength: null),
        });

        var configs = new List<DefaultValueConfig>
        {
            new("Order", "Quantity", "1"),
        };

        var merged = ModelMerger.ApplyDefaultValues(entity, configs);

        Assert.Null(merged.Properties.Single(p => p.Name == "Id").DefaultValueLiteral);
        Assert.Equal("1", merged.Properties.Single(p => p.Name == "Quantity").DefaultValueLiteral);
    }
}
