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
}
