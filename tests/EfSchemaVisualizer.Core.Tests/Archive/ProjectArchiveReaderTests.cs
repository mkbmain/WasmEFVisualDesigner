using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using EfSchemaVisualizer.Core.Archive;
using EfSchemaVisualizer.Core.Parsing;
using Xunit;

namespace EfSchemaVisualizer.Core.Tests.Archive;

public class ProjectArchiveReaderTests
{
    private static MemoryStream CreateZip(params (string Name, string Content)[] files)
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in files)
            {
                var entry = zip.CreateEntry(name);
                using var writer = new StreamWriter(entry.Open());
                writer.Write(content);
            }
        }

        stream.Position = 0;
        return stream;
    }

    [Fact]
    public void Read_BucketsOnModelCreatingFileAsConfig_AndPlainClassFileAsClass()
    {
        const string classFile = """
            public class Blog
            {
                public int Id { get; set; }
            }
            """;

        const string configFile = """
            public class AppDbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Blog>(entity => entity.HasKey(e => e.Id));
                }
            }
            """;

        using var zip = CreateZip(("Blog.cs", classFile), ("AppDbContext.cs", configFile));

        var result = ProjectArchiveReader.Read(zip);

        Assert.Contains("class Blog", result.ClassSource);
        Assert.Contains("OnModelCreating", result.ConfigSource);
        Assert.DoesNotContain("OnModelCreating", result.ClassSource);
        Assert.DoesNotContain("class Blog", result.ConfigSource);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Read_BucketsIEntityTypeConfigurationFileAsConfig()
    {
        const string configFile = """
            public class BlogConfiguration : IEntityTypeConfiguration<Blog>
            {
                public void Configure(EntityTypeBuilder<Blog> builder)
                {
                    builder.HasKey(e => e.Id);
                }
            }
            """;

        using var zip = CreateZip(("BlogConfiguration.cs", configFile));

        var result = ProjectArchiveReader.Read(zip);

        Assert.Contains("BlogConfiguration", result.ConfigSource);
        Assert.Equal("", result.ClassSource);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Read_BucketsBareFluentStatements_AsConfig_NoOnModelCreatingWrapperNeeded()
    {
        const string bareConfigFile = """
            modelBuilder.Entity<Blog>(entity =>
            {
                entity.HasKey(e => e.Id);
            });
            """;

        using var zip = CreateZip(("DbContext.cs", bareConfigFile));

        var result = ProjectArchiveReader.Read(zip);

        Assert.Contains("modelBuilder.Entity<Blog>", result.ConfigSource);
        Assert.Equal("", result.ClassSource);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Read_ConcatenatesMultipleClassFiles_InEntryOrder()
    {
        const string blogFile = "public class Blog { public int Id { get; set; } }";
        const string postFile = "public class Post { public int Id { get; set; } }";

        using var zip = CreateZip(("Blog.cs", blogFile), ("Post.cs", postFile));

        var result = ProjectArchiveReader.Read(zip);

        var blogIndex = result.ClassSource.IndexOf("class Blog", StringComparison.Ordinal);
        var postIndex = result.ClassSource.IndexOf("class Post", StringComparison.Ordinal);
        Assert.True(blogIndex >= 0 && postIndex >= 0 && blogIndex < postIndex);
    }

    [Fact]
    public void Read_IgnoresNonCsFiles()
    {
        const string blogFile = "public class Blog { public int Id { get; set; } }";

        using var zip = CreateZip(("readme.txt", "not code"), ("Blog.cs", blogFile));

        var result = ProjectArchiveReader.Read(zip);

        Assert.DoesNotContain("not code", result.ClassSource);
        Assert.DoesNotContain("not code", result.ConfigSource);
        Assert.Contains("class Blog", result.ClassSource);
    }

    [Fact]
    public void Read_IgnoresEnumOnlyFile_AndReportsNoContentFound()
    {
        const string enumFile = """
            public enum Status
            {
                Active,
                Inactive
            }
            """;

        using var zip = CreateZip(("Status.cs", enumFile));

        var result = ProjectArchiveReader.Read(zip);

        Assert.Equal("", result.ClassSource);
        Assert.Equal("", result.ConfigSource);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.ArchiveNoContentFound, diagnostic.Code);
    }

    [Fact]
    public void Read_EmptyZip_ReturnsDiagnostic_NoThrow()
    {
        using var zip = CreateZip();

        var result = ProjectArchiveReader.Read(zip);

        Assert.Equal("", result.ClassSource);
        Assert.Equal("", result.ConfigSource);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.ArchiveNoContentFound, diagnostic.Code);
    }

    [Fact]
    public void Read_CorruptStream_ThrowsInvalidDataException()
    {
        using var garbage = new MemoryStream(new byte[] { 1, 2, 3, 4, 5 });

        Assert.Throws<InvalidDataException>(() => ProjectArchiveReader.Read(garbage));
    }

    [Fact]
    public void Read_NoLayoutEntry_ReturnsEmptyLayout()
    {
        using var zip = CreateZip(("Blog.cs", "public class Blog { public int Id { get; set; } }"));

        var result = ProjectArchiveReader.Read(zip);

        Assert.Empty(result.Layout);
    }

    [Fact]
    public void Read_LayoutEntry_ParsesEntityPositions()
    {
        using var zip = CreateZip(
            ("Blog.cs", "public class Blog { public int Id { get; set; } }"),
            (ProjectArchiveWriter.LayoutFileName, """{"Blog":{"X":15,"Y":25}}"""));

        var result = ProjectArchiveReader.Read(zip);

        var entry = Assert.Single(result.Layout);
        Assert.Equal("Blog", entry.Key);
        Assert.Equal(15, entry.Value.X);
        Assert.Equal(25, entry.Value.Y);
    }

    [Fact]
    public void Read_MalformedLayoutEntry_IsIgnoredWithoutThrowing()
    {
        using var zip = CreateZip(
            ("Blog.cs", "public class Blog { public int Id { get; set; } }"),
            (ProjectArchiveWriter.LayoutFileName, "not valid json"));

        var result = ProjectArchiveReader.Read(zip);

        Assert.Empty(result.Layout);
        Assert.Contains("class Blog", result.ClassSource);
    }
}
