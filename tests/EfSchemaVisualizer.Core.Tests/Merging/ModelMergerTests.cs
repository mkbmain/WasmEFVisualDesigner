using System.Collections.Generic;
using System.Linq;
using EfSchemaVisualizer.Core.Merging;
using EfSchemaVisualizer.Core.Model;
using Xunit;

namespace EfSchemaVisualizer.Core.Tests.Merging;

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
    public void ApplyIndexes_PropagatesFilterIsDescendingAndIncludeProperties()
    {
        var entity = new EntityModel("Person", new List<PropertyModel>
        {
            new("Email", "string", IsNullable: true, MaxLength: null)
        });
        var configs = new List<IndexConfig>
        {
            new("Person", new List<string> { "Email" }, IsUnique: true, Name: "IX_Person_Email",
                Filter: "[Email] IS NOT NULL", IsDescending: new[] { true }, IncludeProperties: new[] { "FirstName" })
        };

        var result = ModelMerger.ApplyIndexes(entity, configs);

        var index = Assert.Single(result.Indexes);
        Assert.Equal("[Email] IS NOT NULL", index.Filter);
        Assert.Equal(new[] { true }, index.IsDescending);
        Assert.Equal(new[] { "FirstName" }, index.IncludeProperties);
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

    // ─── ApplyAlternateKeys ─────────────────────────────────────────────────

    [Fact]
    public void ApplyAlternateKeys_PopulatesAlternateKeysFromMatchingConfig()
    {
        var entity = new EntityModel("Person", new List<PropertyModel>
        {
            new("Email", "string", IsNullable: true, MaxLength: null)
        });
        var configs = new List<AlternateKeyConfig>
        {
            new("Person", new List<string> { "Email" })
        };

        var result = ModelMerger.ApplyAlternateKeys(entity, configs);

        var alternateKey = Assert.Single(result.AlternateKeys);
        Assert.Equal(new[] { "Email" }, alternateKey);
    }

    [Fact]
    public void ApplyAlternateKeys_CollectsAllMatchingConfigsForSameEntity()
    {
        var entity = new EntityModel("Person", new List<PropertyModel>());
        var configs = new List<AlternateKeyConfig>
        {
            new("Person", new List<string> { "Email" }),
            new("Person", new List<string> { "TenantId", "Ssn" })
        };

        var result = ModelMerger.ApplyAlternateKeys(entity, configs);

        Assert.Equal(2, result.AlternateKeys.Count);
        Assert.Equal(new[] { "Email" }, result.AlternateKeys[0]);
        Assert.Equal(new[] { "TenantId", "Ssn" }, result.AlternateKeys[1]);
    }

    [Fact]
    public void ApplyAlternateKeys_NoMatchingConfig_LeavesAlternateKeysEmpty()
    {
        var entity = new EntityModel("Person", new List<PropertyModel>());

        var result = ModelMerger.ApplyAlternateKeys(entity, new List<AlternateKeyConfig>());

        Assert.Empty(result.AlternateKeys);
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
    public void ApplyViewMapping_SetsViewNameAndSchema_OnMatchingEntity()
    {
        var entity = new EntityModel("Person", new List<PropertyModel>());

        var configs = new List<ViewConfig>
        {
            new("Person", "PeopleView", "dbo"),
            new("Address", "AddressesView", null), // different entity, must not affect Person
        };

        var merged = ModelMerger.ApplyViewMapping(entity, configs);

        Assert.Equal("PeopleView", merged.ViewName);
        Assert.Equal("dbo", merged.Schema);
    }

    [Fact]
    public void ApplyViewMapping_NoMatchingConfig_LeavesViewNameNull()
    {
        var entity = new EntityModel("Person", new List<PropertyModel>());

        var merged = ModelMerger.ApplyViewMapping(entity, new List<ViewConfig>());

        Assert.Null(merged.ViewName);
    }

    [Fact]
    public void ApplySqlQuery_SetsSqlQuery_OnMatchingEntity()
    {
        var entity = new EntityModel("Person", new List<PropertyModel>());

        var configs = new List<SqlQueryConfig>
        {
            new("Person", "SELECT * FROM People"),
            new("Address", "SELECT * FROM Addresses"), // different entity, must not affect Person
        };

        var merged = ModelMerger.ApplySqlQuery(entity, configs);

        Assert.Equal("SELECT * FROM People", merged.SqlQuery);
    }

    [Fact]
    public void ApplySqlQuery_NoMatchingConfig_LeavesSqlQueryNull()
    {
        var entity = new EntityModel("Person", new List<PropertyModel>());

        var merged = ModelMerger.ApplySqlQuery(entity, new List<SqlQueryConfig>());

        Assert.Null(merged.SqlQuery);
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

    // ─── ApplyRelationships ─────────────────────────────────────────────────────

    [Fact]
    public void ApplyRelationships_MapsConfigsToModels_FieldForField()
    {
        var configs = new List<RelationshipConfig>
        {
            new("Customer", "Order", RelationshipKind.OneToMany,
                PrincipalNavigation: "Orders", DependentNavigation: "Customer",
                ForeignKeyProperties: new List<string> { "CustomerId" },
                OnDeleteBehavior: "Cascade"),
        };

        var result = ModelMerger.ApplyRelationships(configs);

        var relationship = Assert.Single(result);
        Assert.Equal("Customer", relationship.PrincipalEntity);
        Assert.Equal("Order", relationship.DependentEntity);
        Assert.Equal(RelationshipKind.OneToMany, relationship.Kind);
        Assert.Equal("Orders", relationship.PrincipalNavigation);
        Assert.Equal("Customer", relationship.DependentNavigation);
        Assert.Equal(new[] { "CustomerId" }, relationship.ForeignKeyProperties);
        Assert.Equal("Cascade", relationship.OnDeleteBehavior);
    }

    [Fact]
    public void ApplyRelationships_EmptyInput_ReturnsEmpty()
    {
        var result = ModelMerger.ApplyRelationships(new List<RelationshipConfig>());

        Assert.Empty(result);
    }

    // ─── ApplyIgnoredProperties ────────────────────────────────────────────────────

    [Fact]
    public void ApplyIgnoredProperties_RemovesMatchingProperty_LeavesOthersUntouched()
    {
        var entity = new EntityModel("Person", new List<PropertyModel>
        {
            new("Id", "int", IsNullable: false, MaxLength: null),
            new("Notes", "string", IsNullable: true, MaxLength: null),
        });

        var configs = new List<IgnoreConfig>
        {
            new("Person", "Notes"),
            new("Address", "Line1"), // different entity, must not affect Person
        };

        var merged = ModelMerger.ApplyIgnoredProperties(entity, configs);

        Assert.Single(merged.Properties);
        Assert.Equal("Id", merged.Properties[0].Name);
    }

    [Fact]
    public void ApplyIgnoredProperties_NoMatchingConfig_ReturnsEntityUnchanged()
    {
        var entity = new EntityModel("Person", new List<PropertyModel>
        {
            new("Id", "int", IsNullable: false, MaxLength: null),
        });

        var merged = ModelMerger.ApplyIgnoredProperties(entity, new List<IgnoreConfig>());

        Assert.Single(merged.Properties);
    }

    // ─── ApplyValueGeneration ──────────────────────────────────────────────────────

    [Fact]
    public void ApplyValueGeneration_SetsValueGeneratedOnMatchingProperty_LeavesOthersUntouched()
    {
        var entity = new EntityModel("Person", new List<PropertyModel>
        {
            new("Id", "int", IsNullable: false, MaxLength: null),
            new("Name", "string", IsNullable: true, MaxLength: null),
        });

        var configs = new List<ValueGenerationConfig>
        {
            new("Person", "Id", "Identity"),
            new("Address", "Line1", "OnAdd"), // different entity, must not affect Person
        };

        var merged = ModelMerger.ApplyValueGeneration(entity, configs);

        Assert.Equal("Identity", merged.Properties.Single(p => p.Name == "Id").ValueGenerated);
        Assert.Null(merged.Properties.Single(p => p.Name == "Name").ValueGenerated);
    }

    // ─── ApplyConcurrencyTokens ────────────────────────────────────────────────────

    [Fact]
    public void ApplyConcurrencyTokens_SetsFlagsOnMatchingProperty_LeavesOthersUntouched()
    {
        var entity = new EntityModel("Person", new List<PropertyModel>
        {
            new("Id", "int", IsNullable: false, MaxLength: null),
            new("RowVersion", "byte[]", IsNullable: false, MaxLength: null),
            new("Name", "string", IsNullable: true, MaxLength: null),
        });

        var configs = new List<ConcurrencyTokenConfig>
        {
            new("Person", "RowVersion", IsRowVersion: true, IsConcurrencyToken: false),
            new("Address", "Line1", IsRowVersion: true, IsConcurrencyToken: false), // different entity, must not affect Person
        };

        var merged = ModelMerger.ApplyConcurrencyTokens(entity, configs);

        var rowVersion = merged.Properties.Single(p => p.Name == "RowVersion");
        Assert.True(rowVersion.IsRowVersion);
        Assert.False(rowVersion.IsConcurrencyToken);
        Assert.False(merged.Properties.Single(p => p.Name == "Name").IsRowVersion);
    }

    [Fact]
    public void ApplyConcurrencyTokens_BothFlagsOnSameConfig_SetsBoth()
    {
        var entity = new EntityModel("Person", new List<PropertyModel>
        {
            new("Id", "int", IsNullable: false, MaxLength: null),
            new("RowVersion", "byte[]", IsNullable: false, MaxLength: null),
        });

        var configs = new List<ConcurrencyTokenConfig>
        {
            new("Person", "RowVersion", IsRowVersion: true, IsConcurrencyToken: true),
        };

        var merged = ModelMerger.ApplyConcurrencyTokens(entity, configs);

        var rowVersion = merged.Properties.Single(p => p.Name == "RowVersion");
        Assert.True(rowVersion.IsRowVersion);
        Assert.True(rowVersion.IsConcurrencyToken);
    }

    [Fact]
    public void ApplyConcurrencyTokens_AttributeSeededTrue_NotDowngradedByAbsentFluentConfig()
    {
        var entity = new EntityModel("Person", new List<PropertyModel>
        {
            new("Id", "int", IsNullable: false, MaxLength: null),
            new("RowVersion", "byte[]", IsNullable: false, MaxLength: null, IsRowVersion: true),
        });

        var merged = ModelMerger.ApplyConcurrencyTokens(entity, new List<ConcurrencyTokenConfig>());

        Assert.True(merged.Properties.Single(p => p.Name == "RowVersion").IsRowVersion);
    }

    // ─── ApplyShadowProperties ─────────────────────────────────────────────────────

    [Fact]
    public void ApplyShadowProperties_AppendsSynthesizedPropertyForUnmatchedConfig()
    {
        var entity = new EntityModel("Person", new List<PropertyModel>
        {
            new("Id", "int", IsNullable: false, MaxLength: null),
        });

        var configs = new List<ShadowPropertyConfig>
        {
            new("Person", "CreatedBy", "string"),
            new("Address", "Line1", "string"), // different entity, must not affect Person
        };

        var merged = ModelMerger.ApplyShadowProperties(entity, configs);

        Assert.Equal(2, merged.Properties.Count);
        var shadow = merged.Properties.Single(p => p.Name == "CreatedBy");
        Assert.Equal("string", shadow.ClrType);
        Assert.True(shadow.IsShadow);
    }

    [Fact]
    public void ApplyShadowProperties_NameCollidesWithExistingProperty_DoesNotSynthesizeDuplicate()
    {
        var entity = new EntityModel("Person", new List<PropertyModel>
        {
            new("Id", "int", IsNullable: false, MaxLength: null),
            new("CreatedBy", "string", IsNullable: true, MaxLength: null),
        });

        var configs = new List<ShadowPropertyConfig>
        {
            new("Person", "CreatedBy", "string"),
        };

        var merged = ModelMerger.ApplyShadowProperties(entity, configs);

        Assert.Equal(2, merged.Properties.Count);
        Assert.False(merged.Properties.Single(p => p.Name == "CreatedBy").IsShadow);
    }

    [Fact]
    public void ApplyShadowProperties_NoMatchingConfig_ReturnsEntityUnchanged()
    {
        var entity = new EntityModel("Person", new List<PropertyModel>
        {
            new("Id", "int", IsNullable: false, MaxLength: null),
        });

        var merged = ModelMerger.ApplyShadowProperties(entity, new List<ShadowPropertyConfig>());

        Assert.Single(merged.Properties);
    }

    // ─── ApplyQueryFilters ─────────────────────────────────────────────────────────

    [Fact]
    public void ApplyQueryFilters_SetsFlagWhenEntityMatches_LeavesOtherEntitiesUntouched()
    {
        var entity = new EntityModel("Person", new List<PropertyModel>
        {
            new("Id", "int", IsNullable: false, MaxLength: null),
        });

        var configs = new List<QueryFilterConfig>
        {
            new("Person"),
            new("Address"), // different entity, must not affect Person's own flag beyond matching
        };

        var merged = ModelMerger.ApplyQueryFilters(entity, configs);

        Assert.True(merged.HasQueryFilter);
    }

    [Fact]
    public void ApplyQueryFilters_NoMatchingConfig_LeavesFlagFalse()
    {
        var entity = new EntityModel("Person", new List<PropertyModel>
        {
            new("Id", "int", IsNullable: false, MaxLength: null),
        });

        var merged = ModelMerger.ApplyQueryFilters(entity, new List<QueryFilterConfig> { new("Address") });

        Assert.False(merged.HasQueryFilter);
    }

    // ─── ApplyEntityComments / ApplyPropertyComments ──────────────────────────────

    [Fact]
    public void ApplyEntityComments_SetsCommentWhenEntityMatches()
    {
        var entity = new EntityModel("Person", new List<PropertyModel>());

        var merged = ModelMerger.ApplyEntityComments(entity, new List<EntityCommentConfig>
        {
            new("Person", "People in the system."),
            new("Address", "Should not apply."),
        });

        Assert.Equal("People in the system.", merged.Comment);
    }

    [Fact]
    public void ApplyPropertyComments_SetsCommentOnMatchingProperty_LeavesOthersUntouched()
    {
        var entity = new EntityModel("Person", new List<PropertyModel>
        {
            new("Id", "int", IsNullable: false, MaxLength: null),
            new("Name", "string", IsNullable: true, MaxLength: null),
        });

        var merged = ModelMerger.ApplyPropertyComments(entity, new List<PropertyCommentConfig>
        {
            new("Person", "Name", "Full display name."),
        });

        Assert.Null(merged.Properties.Single(p => p.Name == "Id").Comment);
        Assert.Equal("Full display name.", merged.Properties.Single(p => p.Name == "Name").Comment);
    }

    // ─── ApplyUnicodeFlags ─────────────────────────────────────────────────────────

    [Fact]
    public void ApplyUnicodeFlags_SetsFlagOnMatchingProperty_LeavesOthersUntouched()
    {
        var entity = new EntityModel("Person", new List<PropertyModel>
        {
            new("Id", "int", IsNullable: false, MaxLength: null),
            new("Name", "string", IsNullable: true, MaxLength: null),
        });

        var merged = ModelMerger.ApplyUnicodeFlags(entity, new List<UnicodeConfig>
        {
            new("Person", "Name", false),
        });

        Assert.Null(merged.Properties.Single(p => p.Name == "Id").IsUnicode);
        Assert.False(merged.Properties.Single(p => p.Name == "Name").IsUnicode);
    }
}
