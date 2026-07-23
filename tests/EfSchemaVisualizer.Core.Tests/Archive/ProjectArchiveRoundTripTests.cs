using System.IO;
using EfSchemaVisualizer.Core.Archive;
using Xunit;

namespace EfSchemaVisualizer.Core.Tests.Archive;

public class ProjectArchiveRoundTripTests
{
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
