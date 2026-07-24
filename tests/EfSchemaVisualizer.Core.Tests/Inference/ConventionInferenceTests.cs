using System.Collections.Generic;
using System.Linq;
using EfSchemaVisualizer.Core.Inference;
using EfSchemaVisualizer.Core.Model;
using Xunit;

namespace EfSchemaVisualizer.Core.Tests.Inference;

public class ConventionInferenceTests
{
    private static PropertyModel Property(string name, string clrType, bool isNullable = false) =>
        new(name, clrType, isNullable, MaxLength: null);

    private static EntityModel Entity(string name, params PropertyModel[] properties) =>
        new(name, properties);

    [Fact]
    public void InferKey_PropertyNamedId_InfersItAsKey()
    {
        var entity = new EntityModel("Customer", new[] { Property("Id", "int"), Property("Name", "string") });

        var result = ConventionInference.InferKey(entity);

        Assert.Equal(new[] { "Id" }, result.KeyPropertyNames);
        Assert.True(result.IsKeyInferred);
    }

    [Fact]
    public void InferKey_PropertyNamedIdCaseInsensitive_InfersItAsKey()
    {
        var entity = new EntityModel("Customer", new[] { Property("ID", "int") });

        var result = ConventionInference.InferKey(entity);

        Assert.Equal(new[] { "ID" }, result.KeyPropertyNames);
        Assert.True(result.IsKeyInferred);
    }

    [Fact]
    public void InferKey_NoIdButTypeNameIdPresent_InfersTypeNameIdAsKey()
    {
        var entity = new EntityModel("Customer", new[] { Property("CustomerId", "int"), Property("Name", "string") });

        var result = ConventionInference.InferKey(entity);

        Assert.Equal(new[] { "CustomerId" }, result.KeyPropertyNames);
        Assert.True(result.IsKeyInferred);
    }

    [Fact]
    public void InferKey_BothIdAndTypeNameIdPresent_IdWins()
    {
        var entity = new EntityModel("Customer", new[] { Property("Id", "int"), Property("CustomerId", "int") });

        var result = ConventionInference.InferKey(entity);

        Assert.Equal(new[] { "Id" }, result.KeyPropertyNames);
    }

    [Fact]
    public void InferKey_NeitherPatternMatches_NoKeyInferred()
    {
        var entity = new EntityModel("Customer", new[] { Property("Name", "string") });

        var result = ConventionInference.InferKey(entity);

        Assert.Empty(result.KeyPropertyNames);
        Assert.False(result.IsKeyInferred);
    }

    [Fact]
    public void InferKey_ExplicitKeyAlreadyPresent_LeavesEntityUnchanged()
    {
        var entity = new EntityModel(
            "Customer",
            new[] { Property("Id", "int"), Property("Ssn", "string") },
            KeyPropertyNames: new[] { "Ssn" });

        var result = ConventionInference.InferKey(entity);

        Assert.Equal(new[] { "Ssn" }, result.KeyPropertyNames);
        Assert.False(result.IsKeyInferred);
    }

    [Fact]
    public void InferKey_EntityIsKeyless_LeavesEntityUnchanged()
    {
        var entity = new EntityModel("Customer", new[] { Property("Id", "int") }, IsKeyless: true);

        var result = ConventionInference.InferKey(entity);

        Assert.Empty(result.KeyPropertyNames);
        Assert.False(result.IsKeyInferred);
        Assert.True(result.IsKeyless);
    }

    [Fact]
    public void InferRelationships_NavigationPlusMatchingNavNameIdProperty_InfersOneToMany()
    {
        var customer = Entity("Customer", Property("Id", "int"));
        var order = Entity("Order",
            Property("Id", "int"),
            Property("CustomerId", "int"),
            Property("Customer", "Customer"));

        var relationships = ConventionInference.InferRelationships(new[] { customer, order });

        var relationship = Assert.Single(relationships);
        Assert.Equal("Customer", relationship.PrincipalEntity);
        Assert.Equal("Order", relationship.DependentEntity);
        Assert.Equal(RelationshipKind.OneToMany, relationship.Kind);
        Assert.Null(relationship.PrincipalNavigation);
        Assert.Equal("Customer", relationship.DependentNavigation);
        Assert.Equal(new[] { "CustomerId" }, relationship.ForeignKeyProperties);
        Assert.True(relationship.IsInferred);
    }

    [Fact]
    public void InferRelationships_NavNamedDifferentlyFromType_FallsBackToPrincipalTypeNameId()
    {
        var customer = Entity("Customer", Property("Id", "int"));
        var order = Entity("Order",
            Property("Id", "int"),
            Property("CustomerId", "int"),
            Property("Buyer", "Customer"));

        var relationships = ConventionInference.InferRelationships(new[] { customer, order });

        var relationship = Assert.Single(relationships);
        Assert.Equal(new[] { "CustomerId" }, relationship.ForeignKeyProperties);
        Assert.Equal("Buyer", relationship.DependentNavigation);
    }

    [Fact]
    public void InferRelationships_PrincipalHasCollectionBackReference_ResolvesOneToManyWithPrincipalNavigation()
    {
        var customer = Entity("Customer", Property("Id", "int"), Property("Orders", "ICollection<Order>"));
        var order = Entity("Order", Property("Id", "int"), Property("CustomerId", "int"), Property("Customer", "Customer"));

        var relationships = ConventionInference.InferRelationships(new[] { customer, order });

        var relationship = Assert.Single(relationships);
        Assert.Equal(RelationshipKind.OneToMany, relationship.Kind);
        Assert.Equal("Orders", relationship.PrincipalNavigation);
    }

    [Fact]
    public void InferRelationships_PrincipalHasScalarBackReference_ResolvesOneToOne()
    {
        var customer = Entity("Customer", Property("Id", "int"), Property("FeaturedOrder", "Order"));
        var order = Entity("Order", Property("Id", "int"), Property("CustomerId", "int"), Property("Customer", "Customer"));

        var relationships = ConventionInference.InferRelationships(new[] { customer, order });

        var relationship = Assert.Single(relationships);
        Assert.Equal(RelationshipKind.OneToOne, relationship.Kind);
        Assert.Equal("FeaturedOrder", relationship.PrincipalNavigation);
    }

    [Fact]
    public void InferRelationships_SelfReferencingEntity_ResolvesWithoutMatchingItsOwnNavigationProperty()
    {
        var employee = Entity("Employee",
            Property("Id", "int"),
            Property("ManagerId", "int", isNullable: true),
            Property("Manager", "Employee", isNullable: true),
            Property("Reports", "ICollection<Employee>"));

        var relationships = ConventionInference.InferRelationships(new[] { employee });

        var relationship = Assert.Single(relationships);
        Assert.Equal("Employee", relationship.PrincipalEntity);
        Assert.Equal("Employee", relationship.DependentEntity);
        Assert.Equal(RelationshipKind.OneToMany, relationship.Kind);
        Assert.Equal("Reports", relationship.PrincipalNavigation);
        Assert.Equal("Manager", relationship.DependentNavigation);
    }

    [Fact]
    public void InferRelationships_NoMatchingForeignKeyProperty_InfersNothing()
    {
        var customer = Entity("Customer", Property("Id", "int"));
        var order = Entity("Order", Property("Id", "int"), Property("Customer", "Customer"));

        var relationships = ConventionInference.InferRelationships(new[] { customer, order });

        Assert.Empty(relationships);
    }

    [Fact]
    public void InferRelationships_NoNavigationProperty_InfersNothingEvenWithFkShapedProperty()
    {
        var customer = Entity("Customer", Property("Id", "int"));
        var order = Entity("Order", Property("Id", "int"), Property("CustomerId", "int"));

        var relationships = ConventionInference.InferRelationships(new[] { customer, order });

        Assert.Empty(relationships);
    }
}
