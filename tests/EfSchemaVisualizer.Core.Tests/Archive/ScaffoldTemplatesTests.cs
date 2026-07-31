using EfSchemaVisualizer.Core.Archive;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace EfSchemaVisualizer.Core.Tests.Archive;

public class ScaffoldTemplatesTests
{
    [Theory]
    [InlineData(ScaffoldProvider.SqlServer, "Microsoft.EntityFrameworkCore.SqlServer")]
    [InlineData(ScaffoldProvider.PostgreSql, "Npgsql.EntityFrameworkCore.PostgreSQL")]
    [InlineData(ScaffoldProvider.Sqlite, "Microsoft.EntityFrameworkCore.Sqlite")]
    public void Csproj_ReferencesCorrectProviderPackage(ScaffoldProvider provider, string expectedPackage)
    {
        var csproj = ScaffoldTemplates.Csproj("MyApp", provider);

        Assert.Contains(expectedPackage, csproj);
        Assert.Contains("Microsoft.EntityFrameworkCore.Design", csproj);
        Assert.Contains("<TargetFramework>net10.0</TargetFramework>", csproj);
        Assert.Contains("<RootNamespace>MyApp</RootNamespace>", csproj);
        Assert.Contains("<ImplicitUsings>enable</ImplicitUsings>", csproj);
        Assert.Contains("appsettings.json", csproj);
    }

    [Theory]
    [InlineData(ScaffoldProvider.SqlServer, "Server=.;Database=MyApp;")]
    [InlineData(ScaffoldProvider.PostgreSql, "Host=localhost;Database=MyApp;")]
    [InlineData(ScaffoldProvider.Sqlite, "Data Source=MyApp.db")]
    public void AppSettings_UsesProviderSpecificConnectionString(ScaffoldProvider provider, string expectedFragment)
    {
        var json = ScaffoldTemplates.AppSettings("MyApp", provider);

        Assert.Contains(expectedFragment, json);
        Assert.Contains("\"ConnectionStrings\"", json);
        Assert.Contains("\"DefaultConnection\"", json);
    }

    [Theory]
    [InlineData(ScaffoldProvider.SqlServer, "UseSqlServer")]
    [InlineData(ScaffoldProvider.PostgreSql, "UseNpgsql")]
    [InlineData(ScaffoldProvider.Sqlite, "UseSqlite")]
    public void DbContextFactory_CallsProviderSpecificUseMethod(ScaffoldProvider provider, string expectedMethod)
    {
        var factory = ScaffoldTemplates.DbContextFactory("MyApp", provider);

        var tree = CSharpSyntaxTree.ParseText(factory);
        Assert.Empty(tree.GetDiagnostics());

        Assert.Contains($"optionsBuilder.{expectedMethod}(connectionString);", factory);
        Assert.Contains("IDesignTimeDbContextFactory<AppDbContext>", factory);
        Assert.Contains("namespace MyApp;", factory);
    }

    [Fact]
    public void Program_BuildsContextAndParsesCleanly()
    {
        var program = ScaffoldTemplates.Program("MyApp");

        var tree = CSharpSyntaxTree.ParseText(program);
        Assert.Empty(tree.GetDiagnostics());
        Assert.Contains("new AppDbContextFactory().CreateDbContext(args)", program);
        Assert.Contains("using MyApp;", program);
    }

    [Fact]
    public void Readme_ContainsTheThreeCommands()
    {
        var readme = ScaffoldTemplates.Readme("MyApp");

        Assert.Contains("dotnet restore", readme);
        Assert.Contains("dotnet ef migrations add Init", readme);
        Assert.Contains("dotnet ef database update", readme);
        Assert.Contains("# MyApp", readme);
    }
}
