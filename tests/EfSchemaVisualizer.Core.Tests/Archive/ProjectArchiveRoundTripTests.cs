using System.IO;
using System.IO.Compression;
using EfSchemaVisualizer.Core.Archive;
using Xunit;

namespace EfSchemaVisualizer.Core.Tests.Archive;

public class ProjectArchiveRoundTripTests
{
    private static MemoryStream CreateZip(params (string Name, byte[] Content)[] files)
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in files)
            {
                var entry = zip.CreateEntry(name);
                using var entryStream = entry.Open();
                entryStream.Write(content, 0, content.Length);
            }
        }

        stream.Position = 0;
        return stream;
    }

    [Fact]
    public void ReadThenWrite_SingleFileProjectWithPassthroughFiles_PreservesOriginalPathsAndContent()
    {
        var csprojBytes = System.Text.Encoding.UTF8.GetBytes("<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        var classFileBytes = System.Text.Encoding.UTF8.GetBytes(
            "public class Blog { public int Id { get; set; } }");
        var configFileBytes = System.Text.Encoding.UTF8.GetBytes(
            "modelBuilder.Entity<Blog>(entity => entity.HasKey(e => e.Id));");

        using var uploadedZip = CreateZip(
            ("MyApp.csproj", csprojBytes),
            ("Entities/Blog.cs", classFileBytes),
            ("Data/AppDbContext.cs", configFileBytes));

        var readResult = ProjectArchiveReader.Read(uploadedZip);

        var downloadedBytes = ProjectArchiveWriter.Write(
            readResult.ClassSource,
            readResult.ConfigSource,
            entityFileOrigins: readResult.EntityFileOrigins,
            configFileOrigins: readResult.ConfigFileOrigins,
            passthroughFiles: readResult.PassthroughFiles);

        using var downloadedStream = new MemoryStream(downloadedBytes);
        using var downloadedZip = new ZipArchive(downloadedStream, ZipArchiveMode.Read);

        Assert.NotNull(downloadedZip.GetEntry("MyApp.csproj"));
        Assert.NotNull(downloadedZip.GetEntry("Entities/Blog.cs"));
        Assert.NotNull(downloadedZip.GetEntry("Data/AppDbContext.cs"));

        using var csprojReader = new StreamReader(downloadedZip.GetEntry("MyApp.csproj")!.Open());
        Assert.Equal("<Project Sdk=\"Microsoft.NET.Sdk\"></Project>", csprojReader.ReadToEnd());

        using var classReader = new StreamReader(downloadedZip.GetEntry("Entities/Blog.cs")!.Open());
        Assert.Contains("class Blog", classReader.ReadToEnd());
    }

    [Fact]
    public void WriteThenRead_PreservesClassAndConfigSource()
    {
        const string classSource = "public class Blog { public int Id { get; set; } }";
        const string configSource = """
            modelBuilder.Entity<Blog>(entity =>
            {
                entity.HasKey(e => e.Id);
            });
            """;

        var bytes = ProjectArchiveWriter.Write(classSource, configSource);

        using var stream = new MemoryStream(bytes);
        var result = ProjectArchiveReader.Read(stream);

        Assert.Equal(classSource, result.ClassSource);
        Assert.Equal(configSource, result.ConfigSource);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void WriteThenRead_PreservesLayout_WhenGiven()
    {
        const string classSource = "public class Blog { public int Id { get; set; } }";
        const string configSource = "modelBuilder.Entity<Blog>(entity => entity.HasKey(e => e.Id));";
        var layout = new Dictionary<string, EntityPosition> { ["Blog"] = new(120, 340) };

        var bytes = ProjectArchiveWriter.Write(classSource, configSource, layout);

        using var stream = new MemoryStream(bytes);
        var result = ProjectArchiveReader.Read(stream);

        var entry = Assert.Single(result.Layout);
        Assert.Equal("Blog", entry.Key);
        Assert.Equal(120, entry.Value.X);
        Assert.Equal(340, entry.Value.Y);
    }

    [Fact]
    public void WriteThenRead_NoLayoutGiven_ReturnsEmptyLayout()
    {
        var bytes = ProjectArchiveWriter.Write("public class Blog { }", "");

        using var stream = new MemoryStream(bytes);
        var result = ProjectArchiveReader.Read(stream);

        Assert.Empty(result.Layout);
    }
}
