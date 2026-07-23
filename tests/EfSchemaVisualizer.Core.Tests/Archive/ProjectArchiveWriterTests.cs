using System.IO;
using System.IO.Compression;
using System.Linq;
using EfSchemaVisualizer.Core.Archive;
using Xunit;

namespace EfSchemaVisualizer.Core.Tests.Archive;

public class ProjectArchiveWriterTests
{
    [Fact]
    public void Write_ProducesZipWithTwoFixedNameEntries_ContainingTheGivenSource()
    {
        var bytes = ProjectArchiveWriter.Write("public class Blog { }", "public class AppDbContext { }");

        using var stream = new MemoryStream(bytes);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);

        Assert.Equal(2, zip.Entries.Count);

        var entities = zip.GetEntry("Entities.cs");
        Assert.NotNull(entities);
        using (var reader = new StreamReader(entities!.Open()))
        {
            Assert.Equal("public class Blog { }", reader.ReadToEnd());
        }

        var dbContext = zip.GetEntry("DbContext.cs");
        Assert.NotNull(dbContext);
        using (var reader = new StreamReader(dbContext!.Open()))
        {
            Assert.Equal("public class AppDbContext { }", reader.ReadToEnd());
        }
    }

    [Fact]
    public void Write_EmptyBlobs_StillWritesBothEntries()
    {
        var bytes = ProjectArchiveWriter.Write("", "");

        using var stream = new MemoryStream(bytes);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);

        Assert.Equal(2, zip.Entries.Count);
        Assert.NotNull(zip.GetEntry("Entities.cs"));
        Assert.NotNull(zip.GetEntry("DbContext.cs"));
    }

    [Fact]
    public void Write_NoLayoutGiven_DoesNotWriteLayoutEntry()
    {
        var bytes = ProjectArchiveWriter.Write("public class Blog { }", "");

        using var stream = new MemoryStream(bytes);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);

        Assert.Equal(2, zip.Entries.Count);
        Assert.Null(zip.GetEntry(ProjectArchiveWriter.LayoutFileName));
    }

    [Fact]
    public void Write_WithLayout_AddsLayoutJsonEntry()
    {
        var layout = new Dictionary<string, EntityPosition> { ["Blog"] = new(10, 20), ["Post"] = new(330, 20) };

        var bytes = ProjectArchiveWriter.Write("public class Blog { }", "", layout);

        using var stream = new MemoryStream(bytes);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);

        Assert.Equal(3, zip.Entries.Count);
        var layoutEntry = zip.GetEntry(ProjectArchiveWriter.LayoutFileName);
        Assert.NotNull(layoutEntry);
        using var reader = new StreamReader(layoutEntry!.Open());
        Assert.Contains("Blog", reader.ReadToEnd());
    }

    [Fact]
    public void Write_EmptyLayout_DoesNotWriteLayoutEntry()
    {
        var bytes = ProjectArchiveWriter.Write("public class Blog { }", "", new Dictionary<string, EntityPosition>());

        using var stream = new MemoryStream(bytes);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);

        Assert.Equal(2, zip.Entries.Count);
    }

    [Fact]
    public void Write_SingleClassOrigin_WritesClassSourceUnderItsOriginalPath()
    {
        var entityFileOrigins = new Dictionary<string, string> { ["Blog"] = "Models/Blog.cs" };

        var bytes = ProjectArchiveWriter.Write(
            "public class Blog { }", "", entityFileOrigins: entityFileOrigins);

        using var stream = new MemoryStream(bytes);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);

        Assert.NotNull(zip.GetEntry("Models/Blog.cs"));
        Assert.Null(zip.GetEntry("Entities.cs"));
    }

    [Fact]
    public void Write_SingleConfigOrigin_WritesConfigSourceUnderItsOriginalPath()
    {
        var configFileOrigins = new Dictionary<string, string> { ["Blog"] = "Data/AppDbContext.cs" };

        var bytes = ProjectArchiveWriter.Write(
            "", "public class AppDbContext { }", configFileOrigins: configFileOrigins);

        using var stream = new MemoryStream(bytes);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);

        Assert.NotNull(zip.GetEntry("Data/AppDbContext.cs"));
        Assert.Null(zip.GetEntry("DbContext.cs"));
    }

    [Fact]
    public void Write_MultipleDistinctClassOrigins_FallsBackToEntitiesCsDefaultName()
    {
        var entityFileOrigins = new Dictionary<string, string>
        {
            ["Blog"] = "Models/Blog.cs",
            ["Post"] = "Models/Post.cs",
        };

        var bytes = ProjectArchiveWriter.Write(
            "public class Blog { }", "", entityFileOrigins: entityFileOrigins);

        using var stream = new MemoryStream(bytes);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);

        Assert.NotNull(zip.GetEntry("Entities.cs"));
        Assert.Null(zip.GetEntry("Models/Blog.cs"));
    }

    [Fact]
    public void Write_WithPassthroughFiles_WritesThemVerbatimAtOriginalPaths()
    {
        var csprojBytes = System.Text.Encoding.UTF8.GetBytes("<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        var passthroughFiles = new Dictionary<string, byte[]> { ["MyApp.csproj"] = csprojBytes };

        var bytes = ProjectArchiveWriter.Write(
            "public class Blog { }", "", passthroughFiles: passthroughFiles);

        using var stream = new MemoryStream(bytes);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);

        var entry = zip.GetEntry("MyApp.csproj");
        Assert.NotNull(entry);
        using var entryStream = entry!.Open();
        using var memory = new MemoryStream();
        entryStream.CopyTo(memory);
        Assert.Equal(csprojBytes, memory.ToArray());
    }

    [Fact]
    public void Write_PassthroughPathCollidesWithChosenClassPath_DoesNotDuplicateEntry()
    {
        var entityFileOrigins = new Dictionary<string, string> { ["Blog"] = "Blog.cs" };
        var passthroughFiles = new Dictionary<string, byte[]> { ["Blog.cs"] = System.Text.Encoding.UTF8.GetBytes("stale") };

        var bytes = ProjectArchiveWriter.Write(
            "public class Blog { }", "", entityFileOrigins: entityFileOrigins, passthroughFiles: passthroughFiles);

        using var stream = new MemoryStream(bytes);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);

        Assert.Equal(1, zip.Entries.Count(e => e.FullName == "Blog.cs"));
        using var reader = new StreamReader(zip.GetEntry("Blog.cs")!.Open());
        Assert.Equal("public class Blog { }", reader.ReadToEnd());
    }

    [Fact]
    public void Write_PassthroughPathCollidesWithChosenConfigPath_DoesNotDuplicateEntry()
    {
        var configFileOrigins = new Dictionary<string, string> { ["Blog"] = "AppDbContext.cs" };
        var passthroughFiles = new Dictionary<string, byte[]> { ["AppDbContext.cs"] = System.Text.Encoding.UTF8.GetBytes("stale") };

        var bytes = ProjectArchiveWriter.Write(
            "", "public class AppDbContext { }", configFileOrigins: configFileOrigins, passthroughFiles: passthroughFiles);

        using var stream = new MemoryStream(bytes);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);

        Assert.Equal(1, zip.Entries.Count(e => e.FullName == "AppDbContext.cs"));
        using var reader = new StreamReader(zip.GetEntry("AppDbContext.cs")!.Open());
        Assert.Equal("public class AppDbContext { }", reader.ReadToEnd());
    }

    [Fact]
    public void Write_PassthroughPathDiffersOnlyByCase_TreatedAsSamePathAndNotDuplicated()
    {
        var entityFileOrigins = new Dictionary<string, string> { ["Blog"] = "Blog.cs" };
        var passthroughFiles = new Dictionary<string, byte[]> { ["blog.CS"] = System.Text.Encoding.UTF8.GetBytes("stale") };

        var bytes = ProjectArchiveWriter.Write(
            "public class Blog { }", "", entityFileOrigins: entityFileOrigins, passthroughFiles: passthroughFiles);

        using var stream = new MemoryStream(bytes);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);

        Assert.Equal(1, zip.Entries.Count(e => string.Equals(e.FullName, "Blog.cs", StringComparison.OrdinalIgnoreCase)));
        using var reader = new StreamReader(zip.GetEntry("Blog.cs")!.Open());
        Assert.Equal("public class Blog { }", reader.ReadToEnd());
    }

    [Fact]
    public void Write_EmptyEntityFileOrigins_BehavesSameAsNull_FallsBackToDefaultName()
    {
        var bytes = ProjectArchiveWriter.Write(
            "public class Blog { }", "", entityFileOrigins: new Dictionary<string, string>());

        using var stream = new MemoryStream(bytes);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);

        Assert.NotNull(zip.GetEntry("Entities.cs"));
    }
}
