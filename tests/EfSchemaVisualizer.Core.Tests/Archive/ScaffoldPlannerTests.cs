using System.Collections.Generic;
using System.Text;
using EfSchemaVisualizer.Core.Archive;
using Xunit;

namespace EfSchemaVisualizer.Core.Tests.Archive;

public class ScaffoldPlannerTests
{
    private static IReadOnlyDictionary<string, byte[]> Files(params (string Path, string Content)[] files)
    {
        var dict = new Dictionary<string, byte[]>();
        foreach (var (path, content) in files)
        {
            dict[path] = Encoding.UTF8.GetBytes(content);
        }

        return dict;
    }

    [Fact]
    public void Plan_NoPassthroughFiles_NeedsEverything()
    {
        var plan = ScaffoldPlanner.Plan(
            "modelBuilder.Entity<Blog>(entity => entity.HasKey(e => e.Id));",
            passthroughFiles: null);

        Assert.True(plan.NeedsCsproj);
        Assert.True(plan.NeedsProgram);
        Assert.True(plan.NeedsAppSettings);
        Assert.True(plan.NeedsReadme);
        Assert.True(plan.NeedsDbContextWrapper);
        Assert.Null(plan.DetectedProvider);
        Assert.True(plan.NeedsDbContextFactory);
    }

    [Fact]
    public void Plan_CsprojAlreadyPresent_NeedsCsprojIsFalse()
    {
        var files = Files(("MyApp.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>"));

        var plan = ScaffoldPlanner.Plan("modelBuilder.Entity<Blog>(e => e.HasKey(x => x.Id));", files);

        Assert.False(plan.NeedsCsproj);
    }

    [Fact]
    public void Plan_ProgramCsAlreadyPresentAtNestedPath_NeedsProgramIsFalse()
    {
        var files = Files(("src/Program.cs", "// existing"));

        var plan = ScaffoldPlanner.Plan("modelBuilder.Entity<Blog>(e => e.HasKey(x => x.Id));", files);

        Assert.False(plan.NeedsProgram);
    }

    [Fact]
    public void Plan_AppSettingsAlreadyPresent_NeedsAppSettingsIsFalse()
    {
        var files = Files(("appsettings.json", "{}"));

        var plan = ScaffoldPlanner.Plan("modelBuilder.Entity<Blog>(e => e.HasKey(x => x.Id));", files);

        Assert.False(plan.NeedsAppSettings);
    }

    [Fact]
    public void Plan_ReadmeAlreadyPresent_NeedsReadmeIsFalse()
    {
        var files = Files(("README.md", "# existing"));

        var plan = ScaffoldPlanner.Plan("modelBuilder.Entity<Blog>(e => e.HasKey(x => x.Id));", files);

        Assert.False(plan.NeedsReadme);
    }

    [Fact]
    public void Plan_DbContextFactoryAlreadyPresentAtNestedPath_NeedsDbContextFactoryIsFalse()
    {
        var files = Files(("Data/AppDbContextFactory.cs", "// existing"));

        var plan = ScaffoldPlanner.Plan("modelBuilder.Entity<Blog>(e => e.HasKey(x => x.Id));", files);

        Assert.False(plan.NeedsDbContextFactory);
    }

    [Fact]
    public void Plan_DbContextFactoryNotPresent_NeedsDbContextFactoryIsTrue()
    {
        var files = Files(("MyApp.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>"));

        var plan = ScaffoldPlanner.Plan("modelBuilder.Entity<Blog>(e => e.HasKey(x => x.Id));", files);

        Assert.True(plan.NeedsDbContextFactory);
    }

    [Fact]
    public void Plan_ConfigSourceIsRealDbContextClass_NeedsDbContextWrapperIsFalse()
    {
        const string configSource = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Blog>(e => e.HasKey(x => x.Id));
                }
            }
            """;

        var plan = ScaffoldPlanner.Plan(configSource, passthroughFiles: null);

        Assert.False(plan.NeedsDbContextWrapper);
    }

    [Fact]
    public void Plan_ConfigSourceIsBareTopLevelStatements_NeedsDbContextWrapperIsTrue()
    {
        var plan = ScaffoldPlanner.Plan(
            "modelBuilder.Entity<Blog>(e => e.HasKey(x => x.Id));",
            passthroughFiles: null);

        Assert.True(plan.NeedsDbContextWrapper);
    }

    [Theory]
    [InlineData("Microsoft.EntityFrameworkCore.SqlServer", ScaffoldProvider.SqlServer)]
    [InlineData("Npgsql.EntityFrameworkCore.PostgreSQL", ScaffoldProvider.PostgreSql)]
    [InlineData("Microsoft.EntityFrameworkCore.Sqlite", ScaffoldProvider.Sqlite)]
    public void Plan_CsprojReferencesKnownProvider_DetectsIt(string packageName, ScaffoldProvider expected)
    {
        var files = Files(("MyApp.csproj",
            $"<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><PackageReference Include=\"{packageName}\" Version=\"10.0.0\" /></ItemGroup></Project>"));

        var plan = ScaffoldPlanner.Plan("modelBuilder.Entity<Blog>(e => e.HasKey(x => x.Id));", files);

        Assert.Equal(expected, plan.DetectedProvider);
    }

    [Fact]
    public void Plan_CsprojReferencesNoKnownProvider_DetectedProviderIsNull()
    {
        var files = Files(("MyApp.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>"));

        var plan = ScaffoldPlanner.Plan("modelBuilder.Entity<Blog>(e => e.HasKey(x => x.Id));", files);

        Assert.Null(plan.DetectedProvider);
    }
}
