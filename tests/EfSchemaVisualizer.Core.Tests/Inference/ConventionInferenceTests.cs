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
}
