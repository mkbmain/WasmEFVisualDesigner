using System.IO;
using System.IO.Compression;
using EfSchemaVisualizer.Core.Archive;
using EfSchemaVisualizer.Web.Diagram;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
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

    [Fact]
    public void UploadEditDownload_MultiFileProjectWithOwnNamespacesAndConfigFiles_EveryDownloadedFileCompiles()
    {
        const string customerFile = """
            using System;

            namespace MyApp.Entities;

            public class Customer
            {
                public int Id { get; set; }
                public string Name { get; set; } = "";
            }
            """;

        const string orderFile = """
            using System.Collections.Generic;

            namespace MyApp.Entities;

            public class Order
            {
                public int Id { get; set; }
                public int CustomerId { get; set; }
                public Customer Customer { get; set; } = null!;
            }
            """;

        const string customerConfigFile = """
            using Microsoft.EntityFrameworkCore.Metadata.Builders;

            namespace MyApp.Data;

            public class CustomerConfiguration : IEntityTypeConfiguration<Customer>
            {
                public void Configure(EntityTypeBuilder<Customer> builder)
                {
                    builder.HasKey(e => e.Id);
                }
            }
            """;

        const string orderConfigFile = """
            using Microsoft.EntityFrameworkCore.Metadata.Builders;

            namespace MyApp.Data;

            public class OrderConfiguration : IEntityTypeConfiguration<Order>
            {
                public void Configure(EntityTypeBuilder<Order> builder)
                {
                    builder.HasKey(e => e.Id);
                    builder.HasOne(e => e.Customer).WithMany().HasForeignKey(e => e.CustomerId);
                }
            }
            """;

        var csprojBytes = System.Text.Encoding.UTF8.GetBytes("<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");

        using var uploadedZip = CreateZip(
            ("MyApp.csproj", csprojBytes),
            ("Entities/Customer.cs", System.Text.Encoding.UTF8.GetBytes(customerFile)),
            ("Entities/Order.cs", System.Text.Encoding.UTF8.GetBytes(orderFile)),
            ("Data/CustomerConfiguration.cs", System.Text.Encoding.UTF8.GetBytes(customerConfigFile)),
            ("Data/OrderConfiguration.cs", System.Text.Encoding.UTF8.GetBytes(orderConfigFile)));

        var readResult = ProjectArchiveReader.Read(uploadedZip);
        Assert.Empty(readResult.Diagnostics);

        // Exercise an edit (rename), the same way a DBA would before downloading, and confirm the
        // rename doesn't strand either entity's file origin (see Task 5).
        var editor = new DiagramEditor(
            readResult.ClassSource, readResult.ConfigSource,
            readResult.EntityFileOrigins, readResult.ConfigFileOrigins);
        var renameResult = editor.RenameEntity("Customer", "Client");
        Assert.True(renameResult.Success, renameResult.Error);

        var downloadedBytes = ProjectArchiveWriter.Write(
            editor.ClassSource, editor.ConfigSource, entityFileOrigins: editor.EntityFileOrigins,
            configFileOrigins: editor.ConfigFileOrigins, passthroughFiles: readResult.PassthroughFiles);

        using var downloadedStream = new MemoryStream(downloadedBytes);
        using var downloadedZip = new ZipArchive(downloadedStream, ZipArchiveMode.Read);

        // The rename must have kept "Customer"'s original file, now containing "Client".
        var customerEntry = downloadedZip.GetEntry("Entities/Customer.cs");
        Assert.NotNull(customerEntry);
        using (var reader = new StreamReader(customerEntry!.Open()))
        {
            Assert.Contains("class Client", reader.ReadToEnd());
        }

        Assert.NotNull(downloadedZip.GetEntry("Entities/Order.cs"));
        Assert.NotNull(downloadedZip.GetEntry("Data/CustomerConfiguration.cs"));
        Assert.NotNull(downloadedZip.GetEntry("Data/OrderConfiguration.cs"));
        Assert.NotNull(downloadedZip.GetEntry("MyApp.csproj"));

        foreach (var entry in downloadedZip.Entries)
        {
            if (!entry.FullName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            using var reader = new StreamReader(entry.Open());
            var content = reader.ReadToEnd();
            var tree = CSharpSyntaxTree.ParseText(content);
            var errors = tree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
            Assert.True(errors.Count == 0, $"{entry.FullName} has parse errors: {string.Join("; ", errors)}");
        }
    }
}
