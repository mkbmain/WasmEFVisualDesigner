using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using EfSchemaVisualizer.Core.Archive;
using EfSchemaVisualizer.Core.Model;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace EfSchemaVisualizer.Core.Tests.Archive;

public class ScaffoldRoundTripTests
{
    private static readonly IReadOnlyList<EntityModel> Entities = new List<EntityModel>
    {
        new("Blog", new List<PropertyModel>()),
        new("Post", new List<PropertyModel>()),
    };

    private const string ConfigSource = "modelBuilder.Entity<Blog>(e => e.HasKey(x => x.Id));";

    [Theory]
    [InlineData(ScaffoldProvider.SqlServer)]
    [InlineData(ScaffoldProvider.PostgreSql)]
    [InlineData(ScaffoldProvider.Sqlite)]
    public void FreshDownload_WithScaffoldEnabled_ProducesAllRunnableFilesParsingCleanly(ScaffoldProvider provider)
    {
        var plan = ScaffoldPlanner.Plan(ConfigSource, passthroughFiles: null);
        var result = ScaffoldGenerator.Generate(plan, ConfigSource, Entities, "MyApp", provider);

        var bytes = ProjectArchiveWriter.Write(
            classSource: "public class Blog { public int Id { get; set; } }\npublic class Post { public int Id { get; set; } }",
            configSource: result.ConfigSource,
            passthroughFiles: result.NewPassthroughFiles);

        using var stream = new MemoryStream(bytes);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);

        var expectedEntries = new[]
        {
            "MyApp.csproj", "Program.cs", "appsettings.json", "README.md", "AppDbContextFactory.cs",
        };
        foreach (var expected in expectedEntries)
        {
            Assert.NotNull(zip.GetEntry(expected));
        }

        foreach (var csFileName in new[] { "Program.cs", "AppDbContextFactory.cs" })
        {
            using var reader = new StreamReader(zip.GetEntry(csFileName)!.Open());
            var source = reader.ReadToEnd();
            var tree = CSharpSyntaxTree.ParseText(source);
            Assert.Empty(tree.GetDiagnostics());
        }

        using var dbContextReader = new StreamReader(zip.GetEntry("DbContext.cs")!.Open());
        var dbContextSource = dbContextReader.ReadToEnd();
        var dbContextTree = CSharpSyntaxTree.ParseText(dbContextSource);
        Assert.Empty(dbContextTree.GetDiagnostics());
        Assert.Contains("public class AppDbContext : DbContext", dbContextSource);
    }

    [Fact]
    public void UploadWithExistingCsproj_ScaffoldEnabled_LeavesExistingCsprojByteForByteUnchanged()
    {
        var existingCsprojBytes = Encoding.UTF8.GetBytes("<Project Sdk=\"Microsoft.NET.Sdk\"><!-- hand customized --></Project>");
        var passthrough = new Dictionary<string, byte[]> { ["MyApp.csproj"] = existingCsprojBytes };

        var plan = ScaffoldPlanner.Plan(ConfigSource, passthrough);
        Assert.False(plan.NeedsCsproj);

        var result = ScaffoldGenerator.Generate(plan, ConfigSource, Entities, "MyApp", ScaffoldProvider.SqlServer);
        Assert.DoesNotContain("MyApp.csproj", result.NewPassthroughFiles.Keys);

        var merged = new Dictionary<string, byte[]>(passthrough);
        foreach (var (path, content) in result.NewPassthroughFiles)
        {
            merged[path] = content;
        }

        var bytes = ProjectArchiveWriter.Write(
            classSource: "public class Blog { public int Id { get; set; } }",
            configSource: result.ConfigSource,
            passthroughFiles: merged);

        using var stream = new MemoryStream(bytes);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        using var reader = new StreamReader(zip.GetEntry("MyApp.csproj")!.Open());

        Assert.Equal(Encoding.UTF8.GetString(existingCsprojBytes), reader.ReadToEnd());
    }
}
