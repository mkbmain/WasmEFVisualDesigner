using System.IO;
using System.IO.Compression;
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
}
