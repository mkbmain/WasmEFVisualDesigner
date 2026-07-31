using System.Collections.Generic;
using System.Linq;
using EfSchemaVisualizer.Core.Archive;
using EfSchemaVisualizer.Core.Model;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace EfSchemaVisualizer.Core.Tests.Archive;

public class ScaffoldGeneratorTests
{
    [Theory]
    [InlineData("Blog", "Blogs")]
    [InlineData("Box", "Boxes")]
    [InlineData("Category", "Categories")]
    [InlineData("Address", "Addresses")]
    [InlineData("Bus", "Buses")]
    [InlineData("Key", "Keys")]
    public void Pluralize_AppliesHeuristicRules(string singular, string expectedPlural)
    {
        Assert.Equal(expectedPlural, ScaffoldGenerator.Pluralize(singular));
    }

    [Fact]
    public void BuildDbContextWrapper_ProducesParseableClassWithNoDiagnostics()
    {
        var entities = new List<EntityModel>
        {
            new("Blog", new List<PropertyModel>()),
            new("Post", new List<PropertyModel>()),
        };

        const string configSource = """
            modelBuilder.Entity<Blog>(entity =>
            {
                entity.HasKey(e => e.Id);
            });
            """;

        var wrapper = ScaffoldGenerator.BuildDbContextWrapper(configSource, entities, "MyApp");

        var tree = CSharpSyntaxTree.ParseText(wrapper);
        Assert.Empty(tree.GetDiagnostics());

        Assert.Contains("namespace MyApp;", wrapper);
        Assert.Contains("public class AppDbContext : DbContext", wrapper);
        Assert.Contains("public DbSet<Blog> Blogs => Set<Blog>();", wrapper);
        Assert.Contains("public DbSet<Post> Posts => Set<Post>();", wrapper);
        Assert.Contains("entity.HasKey(e => e.Id);", wrapper);
    }

    [Fact]
    public void BuildDbContextWrapper_ExcludesOwnedEntitiesFromDbSets()
    {
        var entities = new List<EntityModel>
        {
            new("Order", new List<PropertyModel>()),
            new("Address", new List<PropertyModel>()) { IsOwned = true },
        };

        var wrapper = ScaffoldGenerator.BuildDbContextWrapper(
            "modelBuilder.Entity<Order>(e => e.HasKey(x => x.Id));", entities, "MyApp");

        Assert.Contains("public DbSet<Order> Orders => Set<Order>();", wrapper);
        Assert.DoesNotContain("DbSet<Address>", wrapper);
    }

    [Fact]
    public void Generate_AllPiecesMissing_ProducesAllFilesAndWrapsConfigSource()
    {
        var plan = new ScaffoldPlan(
            NeedsCsproj: true, NeedsProgram: true, NeedsAppSettings: true,
            NeedsReadme: true, NeedsDbContextWrapper: true, DetectedProvider: null);
        var entities = new List<EntityModel> { new("Blog", new List<PropertyModel>()) };

        var result = ScaffoldGenerator.Generate(
            plan, "modelBuilder.Entity<Blog>(e => e.HasKey(x => x.Id));",
            entities, "MyApp", ScaffoldProvider.SqlServer);

        Assert.Contains("public class AppDbContext : DbContext", result.ConfigSource);
        Assert.Contains("MyApp.csproj", result.NewPassthroughFiles.Keys);
        Assert.Contains("appsettings.json", result.NewPassthroughFiles.Keys);
        Assert.Contains("Program.cs", result.NewPassthroughFiles.Keys);
        Assert.Contains("README.md", result.NewPassthroughFiles.Keys);
        Assert.Contains("AppDbContextFactory.cs", result.NewPassthroughFiles.Keys);
    }

    [Fact]
    public void Generate_NothingMissingAndRealDbContextAlreadyExists_ProducesNoNewFiles()
    {
        var plan = new ScaffoldPlan(
            NeedsCsproj: false, NeedsProgram: false, NeedsAppSettings: false,
            NeedsReadme: false, NeedsDbContextWrapper: false, DetectedProvider: ScaffoldProvider.SqlServer);
        var entities = new List<EntityModel> { new("Blog", new List<PropertyModel>()) };
        const string existingConfigSource = "public class AppDbContext : DbContext { }";

        var result = ScaffoldGenerator.Generate(
            plan, existingConfigSource, entities, "MyApp", ScaffoldProvider.SqlServer);

        Assert.Equal(existingConfigSource, result.ConfigSource);
        Assert.Empty(result.NewPassthroughFiles);
    }

    [Fact]
    public void Generate_OnlyCsprojMissing_ProducesOnlyCsprojAndFactory()
    {
        var plan = new ScaffoldPlan(
            NeedsCsproj: true, NeedsProgram: false, NeedsAppSettings: false,
            NeedsReadme: false, NeedsDbContextWrapper: false, DetectedProvider: null);
        var entities = new List<EntityModel> { new("Blog", new List<PropertyModel>()) };
        const string existingConfigSource = "public class AppDbContext : DbContext { }";

        var result = ScaffoldGenerator.Generate(
            plan, existingConfigSource, entities, "MyApp", ScaffoldProvider.Sqlite);

        Assert.Equal(existingConfigSource, result.ConfigSource);
        Assert.Equal(new[] { "MyApp.csproj", "AppDbContextFactory.cs" }.OrderBy(x => x),
            result.NewPassthroughFiles.Keys.OrderBy(x => x));
    }
}
