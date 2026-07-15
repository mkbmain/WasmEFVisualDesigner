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
}
