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

    [Fact]
    public void Read_MultipleClassFiles_RecordsEachEntitysOriginFilename()
    {
        const string blogFile = """
            public class Blog
            {
                public int Id { get; set; }
            }
            """;

        const string postFile = """
            public class Post
            {
                public int Id { get; set; }
            }
            """;

        using var zip = CreateZip(("Models/Blog.cs", blogFile), ("Models/Post.cs", postFile));

        var result = ProjectArchiveReader.Read(zip);

        Assert.Equal("Models/Blog.cs", result.EntityFileOrigins["Blog"]);
        Assert.Equal("Models/Post.cs", result.EntityFileOrigins["Post"]);
    }

    [Fact]
    public void Read_SharedOnModelCreatingFile_RecordsSameFilenameForEveryConfiguredEntity()
    {
        const string classFile = """
            public class Blog { public int Id { get; set; } }
            public class Post { public int Id { get; set; } }
            """;

        const string configFile = """
            public class AppDbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Blog>(entity => entity.HasKey(e => e.Id));
                    modelBuilder.Entity<Post>(entity => entity.HasKey(e => e.Id));
                }
            }
            """;

        using var zip = CreateZip(("Entities.cs", classFile), ("Data/AppDbContext.cs", configFile));

        var result = ProjectArchiveReader.Read(zip);

        Assert.Equal("Data/AppDbContext.cs", result.ConfigFileOrigins["Blog"]);
        Assert.Equal("Data/AppDbContext.cs", result.ConfigFileOrigins["Post"]);
    }

    [Fact]
    public void Read_IEntityTypeConfigurationPerFile_RecordsEachEntitysOwnConfigFilename()
    {
        const string classFile = "public class Blog { public int Id { get; set; } }";

        const string configFile = """
            public class BlogConfiguration : IEntityTypeConfiguration<Blog>
            {
                public void Configure(EntityTypeBuilder<Blog> builder)
                {
                    builder.HasKey(e => e.Id);
                }
            }
            """;

        using var zip = CreateZip(("Blog.cs", classFile), ("Configurations/BlogConfiguration.cs", configFile));

        var result = ProjectArchiveReader.Read(zip);

        Assert.Equal("Configurations/BlogConfiguration.cs", result.ConfigFileOrigins["Blog"]);
    }

    [Fact]
    public void Read_NonCsFile_IsCapturedAsPassthroughBytes()
    {
        const string classFile = "public class Blog { public int Id { get; set; } }";
        var csprojBytes = System.Text.Encoding.UTF8.GetBytes("<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");

        using var zip = CreateZip(("Blog.cs", classFile));
        // Add a non-.cs entry directly since CreateZip only supports text .cs entries.
        zip.Position = 0;
        using var zipWithExtra = new MemoryStream();
        using (var source = new ZipArchive(zip, ZipArchiveMode.Read, leaveOpen: true))
        using (var dest = new ZipArchive(zipWithExtra, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in source.Entries)
            {
                var destEntry = dest.CreateEntry(entry.FullName);
                using var destStream = destEntry.Open();
                using var srcStream = entry.Open();
                srcStream.CopyTo(destStream);
            }

            var projEntry = dest.CreateEntry("MyProject.csproj");
            using var projStream = projEntry.Open();
            projStream.Write(csprojBytes);
        }

        zipWithExtra.Position = 0;
        var result = ProjectArchiveReader.Read(zipWithExtra);

        Assert.True(result.PassthroughFiles.ContainsKey("MyProject.csproj"));
        Assert.Equal(csprojBytes, result.PassthroughFiles["MyProject.csproj"]);
    }

    [Fact]
    public void Read_UnclassifiableCsFile_IsCapturedAsPassthroughBytes()
    {
        const string classFile = "public class Blog { public int Id { get; set; } }";
        const string enumOnlyFile = "public enum Status { Active, Inactive }";

        using var zip = CreateZip(("Blog.cs", classFile), ("Status.cs", enumOnlyFile));

        var result = ProjectArchiveReader.Read(zip);

        Assert.True(result.PassthroughFiles.ContainsKey("Status.cs"));
        Assert.Contains("enum Status", System.Text.Encoding.UTF8.GetString(result.PassthroughFiles["Status.cs"]));
    }

    [Fact]
    public void Read_DirectoryEntry_IsNotCapturedAsPassthrough()
    {
        const string classFile = "public class Blog { public int Id { get; set; } }";

        using var zip = CreateZip(("Blog.cs", classFile));
        zip.Position = 0;
        using var zipWithDir = new MemoryStream();
        using (var source = new ZipArchive(zip, ZipArchiveMode.Read, leaveOpen: true))
        using (var dest = new ZipArchive(zipWithDir, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in source.Entries)
            {
                var destEntry = dest.CreateEntry(entry.FullName);
                using var destStream = destEntry.Open();
                using var srcStream = entry.Open();
                srcStream.CopyTo(destStream);
            }

            dest.CreateEntry("Models/");
        }

        zipWithDir.Position = 0;
        var result = ProjectArchiveReader.Read(zipWithDir);

        Assert.False(result.PassthroughFiles.ContainsKey("Models/"));
    }

    [Fact]
    public void Read_MigrationFile_IsExcludedFromClassSource_AndPreservedAsPassthrough()
    {
        const string blogFile = "public class Blog { public int Id { get; set; } }";
        const string migrationFile = """
            public partial class Init : Migration
            {
                protected override void Up(MigrationBuilder migrationBuilder) { }
            }
            """;

        using var zip = CreateZip(("Blog.cs", blogFile), ("Migrations/20240101_Init.cs", migrationFile));

        var result = ProjectArchiveReader.Read(zip);

        Assert.Contains("class Blog", result.ClassSource);
        Assert.DoesNotContain("class Init", result.ClassSource);
        Assert.True(result.PassthroughFiles.ContainsKey("Migrations/20240101_Init.cs"));
        Assert.Contains(
            "class Init",
            System.Text.Encoding.UTF8.GetString(result.PassthroughFiles["Migrations/20240101_Init.cs"]));
        Assert.Contains(result.Diagnostics, d => d.Code == DiagnosticCodes.ArchiveGeneratedFileExcluded);
    }

    [Fact]
    public void Read_ModelSnapshotFile_IsExcludedFromClassSource_AndPreservedAsPassthrough()
    {
        const string blogFile = "public class Blog { public int Id { get; set; } }";
        const string snapshotFile = """
            partial class AppDbContextModelSnapshot : ModelSnapshot
            {
                protected override void BuildModel(ModelBuilder modelBuilder) { }
            }
            """;

        using var zip = CreateZip(("Blog.cs", blogFile), ("Migrations/AppDbContextModelSnapshot.cs", snapshotFile));

        var result = ProjectArchiveReader.Read(zip);

        Assert.DoesNotContain("ModelSnapshot", result.ClassSource);
        Assert.True(result.PassthroughFiles.ContainsKey("Migrations/AppDbContextModelSnapshot.cs"));
    }

    [Fact]
    public void Read_DesignerFile_IsExcludedFromClassSource_AndPreservedAsPassthrough()
    {
        const string blogFile = "public class Blog { public int Id { get; set; } }";
        const string designerFile = """
            partial class Init
            {
                protected override void BuildTargetModel(ModelBuilder modelBuilder) { }
            }
            """;

        using var zip = CreateZip(("Blog.cs", blogFile), ("Migrations/20240101_Init.Designer.cs", designerFile));

        var result = ProjectArchiveReader.Read(zip);

        Assert.DoesNotContain("BuildTargetModel", result.ClassSource);
        Assert.True(result.PassthroughFiles.ContainsKey("Migrations/20240101_Init.Designer.cs"));
    }

    [Fact]
    public void Read_ObjFolderCsFile_IsDroppedEntirely_NotParsedNorPreserved()
    {
        const string blogFile = "public class Blog { public int Id { get; set; } }";
        const string generatedFile = "public class MyAppAssemblyInfo { }";

        using var zip = CreateZip(
            ("Blog.cs", blogFile),
            ("obj/Debug/net8.0/MyApp.AssemblyInfo.cs", generatedFile));

        var result = ProjectArchiveReader.Read(zip);

        Assert.DoesNotContain("MyAppAssemblyInfo", result.ClassSource);
        Assert.False(result.PassthroughFiles.ContainsKey("obj/Debug/net8.0/MyApp.AssemblyInfo.cs"));
        Assert.Contains(result.Diagnostics, d => d.Code == DiagnosticCodes.ArchiveBuildArtifactSkipped);
    }

    [Fact]
    public void Read_BinFolderFile_IsDroppedEntirely_RegardlessOfExtension()
    {
        const string blogFile = "public class Blog { public int Id { get; set; } }";

        using var zip = CreateZip(
            ("Blog.cs", blogFile),
            ("bin/Debug/net8.0/MyApp.dll", "fake binary content"));

        var result = ProjectArchiveReader.Read(zip);

        Assert.False(result.PassthroughFiles.ContainsKey("bin/Debug/net8.0/MyApp.dll"));
    }

    [Fact]
    public void Read_MultipleClassFilesWithOwnUsingsAndNamespaces_ProducesParseableClassSource()
    {
        const string customerFile = """
            using System;

            namespace MyApp.Entities;

            public class Customer
            {
                public int Id { get; set; }
            }
            """;

        const string orderFile = """
            using System.Collections.Generic;

            namespace MyApp.Entities;

            public class Order
            {
                public int Id { get; set; }
                public int CustomerId { get; set; }
            }
            """;

        using var zip = CreateZip(("Entities/Customer.cs", customerFile), ("Entities/Order.cs", orderFile));

        var result = ProjectArchiveReader.Read(zip);

        var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(result.ClassSource);
        var errors = tree.GetDiagnostics().Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0, "Expected no parse errors, got: " + string.Join("; ", errors));
        Assert.Contains("class Customer", result.ClassSource);
        Assert.Contains("class Order", result.ClassSource);
    }
}
