using EfSchemaVisualizer.Core.Model;
using Xunit;

namespace EfSchemaVisualizer.Core.Tests.Model;

public class PropertyModelTests
{
    [Fact]
    public void WithMaxLength_ProducesUpdatedCopy_LeavingOriginalUnchanged()
    {
        var original = new PropertyModel("Name", "string", IsNullable: true, MaxLength: null);

        var updated = original with { MaxLength = 100 };

        Assert.Null(original.MaxLength);
        Assert.Equal(100, updated.MaxLength);
        Assert.Equal(original.Name, updated.Name);
    }

    [Fact]
    public void WithIsRequiredOverride_ProducesUpdatedCopy_LeavingOriginalUnchanged()
    {
        var original = new PropertyModel("Name", "string", IsNullable: true, MaxLength: null);

        var updated = original with { IsRequiredOverride = false };

        Assert.Null(original.IsRequiredOverride);
        Assert.False(updated.IsRequiredOverride);
        Assert.Equal(original.Name, updated.Name);
    }

    [Fact]
    public void WithPrecisionAndScale_ProducesUpdatedCopy_LeavingOriginalUnchanged()
    {
        var original = new PropertyModel("Total", "decimal", IsNullable: false, MaxLength: null);

        var updated = original with { Precision = 18, Scale = 2 };

        Assert.Null(original.Precision);
        Assert.Null(original.Scale);
        Assert.Equal(18, updated.Precision);
        Assert.Equal(2, updated.Scale);
        Assert.Equal(original.Name, updated.Name);
    }

    [Fact]
    public void Precision_And_Scale_DefaultToNull()
    {
        var property = new PropertyModel("Total", "decimal", IsNullable: false, MaxLength: null);

        Assert.Null(property.Precision);
        Assert.Null(property.Scale);
    }

    [Fact]
    public void WithColumnMapping_ProducesUpdatedCopy_LeavingOriginalUnchanged()
    {
        var original = new PropertyModel("Total", "decimal", IsNullable: false, MaxLength: null);

        var updated = original with { ColumnName = "total_amount", ColumnType = "decimal(18,2)", DefaultValueLiteral = "0" };

        Assert.Null(original.ColumnName);
        Assert.Null(original.ColumnType);
        Assert.Null(original.DefaultValueLiteral);
        Assert.Equal("total_amount", updated.ColumnName);
        Assert.Equal("decimal(18,2)", updated.ColumnType);
        Assert.Equal("0", updated.DefaultValueLiteral);
    }

    [Fact]
    public void ColumnMappingFields_DefaultToNull()
    {
        var property = new PropertyModel("Total", "decimal", IsNullable: false, MaxLength: null);

        Assert.Null(property.ColumnName);
        Assert.Null(property.ColumnType);
        Assert.Null(property.DefaultValueLiteral);
    }

    [Fact]
    public void EntityModel_ExposesNameAndProperties()
    {
        var properties = new List<PropertyModel>
        {
            new("Id", "int", IsNullable: false, MaxLength: null),
            new("Name", "string", IsNullable: true, MaxLength: null),
        };

        var entity = new EntityModel("Person", properties);

        Assert.Equal("Person", entity.Name);
        Assert.Equal(2, entity.Properties.Count);
    }

    [Fact]
    public void EntityModel_KeyPropertyNames_DefaultsToEmpty()
    {
        var entity = new EntityModel("Person", new List<PropertyModel>());

        Assert.Empty(entity.KeyPropertyNames);
    }

    [Fact]
    public void EntityModel_WithKeyPropertyNames_ProducesUpdatedCopy_LeavingOriginalUnchanged()
    {
        var original = new EntityModel("Person", new List<PropertyModel>());

        var updated = original with { KeyPropertyNames = new List<string> { "Id" } };

        Assert.Empty(original.KeyPropertyNames);
        Assert.Equal(new[] { "Id" }, updated.KeyPropertyNames);
    }

    [Fact]
    public void EntityModel_Indexes_DefaultsToEmpty()
    {
        var entity = new EntityModel("Person", new List<PropertyModel>());

        Assert.Empty(entity.Indexes);
    }

    [Fact]
    public void EntityModel_WithIndexes_ProducesUpdatedCopy_LeavingOriginalUnchanged()
    {
        var original = new EntityModel("Person", new List<PropertyModel>());
        var index = new IndexModel(new List<string> { "Email" }, IsUnique: true, Name: "IX_Email");

        var updated = original with { Indexes = new List<IndexModel> { index } };

        Assert.Empty(original.Indexes);
        Assert.Single(updated.Indexes);
        Assert.Equal("IX_Email", updated.Indexes[0].Name);
        Assert.True(updated.Indexes[0].IsUnique);
    }

    [Fact]
    public void EntityModel_TableNameAndSchema_DefaultToNull()
    {
        var entity = new EntityModel("Person", new List<PropertyModel>());

        Assert.Null(entity.TableName);
        Assert.Null(entity.Schema);
    }

    [Fact]
    public void EntityModel_WithTableNameAndSchema_ProducesUpdatedCopy_LeavingOriginalUnchanged()
    {
        var original = new EntityModel("Person", new List<PropertyModel>());

        var updated = original with { TableName = "People", Schema = "dbo" };

        Assert.Null(original.TableName);
        Assert.Equal("People", updated.TableName);
        Assert.Equal("dbo", updated.Schema);
    }

    [Fact]
    public void IndexModel_Name_DefaultsToNull()
    {
        var index = new IndexModel(new List<string> { "Email" }, IsUnique: false);

        Assert.Null(index.Name);
    }

    [Fact]
    public void RelationshipModel_ForeignKeyProperties_DefaultsToEmpty()
    {
        var relationship = new RelationshipModel(
            "Customer", "Order", RelationshipKind.OneToMany,
            PrincipalNavigation: "Orders", DependentNavigation: "Customer");

        Assert.Empty(relationship.ForeignKeyProperties);
        Assert.Null(relationship.OnDeleteBehavior);
        Assert.Null(relationship.JoinEntityName);
    }

    [Fact]
    public void RelationshipModel_WithForeignKeyProperties_ProducesUpdatedCopy_LeavingOriginalUnchanged()
    {
        var original = new RelationshipModel(
            "Customer", "Order", RelationshipKind.OneToMany,
            PrincipalNavigation: "Orders", DependentNavigation: "Customer");

        var updated = original with { ForeignKeyProperties = new List<string> { "CustomerId" }, OnDeleteBehavior = "Cascade" };

        Assert.Empty(original.ForeignKeyProperties);
        Assert.Equal(new[] { "CustomerId" }, updated.ForeignKeyProperties);
        Assert.Equal("Cascade", updated.OnDeleteBehavior);
        Assert.Equal(original.PrincipalEntity, updated.PrincipalEntity);
    }

    [Fact]
    public void ValueGenerated_DefaultsToNull()
    {
        var property = new PropertyModel("Id", "int", IsNullable: false, MaxLength: null);

        Assert.Null(property.ValueGenerated);
    }

    [Fact]
    public void WithValueGenerated_ProducesUpdatedCopy_LeavingOriginalUnchanged()
    {
        var original = new PropertyModel("Id", "int", IsNullable: false, MaxLength: null);

        var updated = original with { ValueGenerated = "Identity" };

        Assert.Null(original.ValueGenerated);
        Assert.Equal("Identity", updated.ValueGenerated);
        Assert.Equal(original.Name, updated.Name);
    }

    [Fact]
    public void IsShadow_DefaultsToFalse()
    {
        var property = new PropertyModel("Id", "int", IsNullable: false, MaxLength: null);

        Assert.False(property.IsShadow);
    }

    [Fact]
    public void WithIsShadow_ProducesUpdatedCopy_LeavingOriginalUnchanged()
    {
        var original = new PropertyModel("CreatedBy", "string", IsNullable: true, MaxLength: null);

        var updated = original with { IsShadow = true };

        Assert.False(original.IsShadow);
        Assert.True(updated.IsShadow);
    }

    [Fact]
    public void DeclaringEntityName_DefaultsToNull()
    {
        var property = new PropertyModel("Id", "int", IsNullable: false, MaxLength: null);

        Assert.Null(property.DeclaringEntityName);
    }

    [Fact]
    public void WithDeclaringEntityName_ProducesUpdatedCopy_LeavingOriginalUnchanged()
    {
        var original = new PropertyModel("Id", "int", IsNullable: false, MaxLength: null);

        var updated = original with { DeclaringEntityName = "Person" };

        Assert.Null(original.DeclaringEntityName);
        Assert.Equal("Person", updated.DeclaringEntityName);
    }

    [Fact]
    public void EntityModel_BaseEntityName_DefaultsToNull()
    {
        var entity = new EntityModel("Student", new List<PropertyModel>());

        Assert.Null(entity.BaseEntityName);
    }

    [Fact]
    public void EntityModel_WithBaseEntityName_ProducesUpdatedCopy_LeavingOriginalUnchanged()
    {
        var original = new EntityModel("Student", new List<PropertyModel>());

        var updated = original with { BaseEntityName = "Person" };

        Assert.Null(original.BaseEntityName);
        Assert.Equal("Person", updated.BaseEntityName);
    }

    [Fact]
    public void RelationshipKind_HasInheritanceMember()
    {
        var relationship = new RelationshipModel(
            "Person", "Student", RelationshipKind.Inheritance,
            PrincipalNavigation: null, DependentNavigation: null);

        Assert.Equal(RelationshipKind.Inheritance, relationship.Kind);
    }
}
