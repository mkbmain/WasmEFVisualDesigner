using EfSchemaVisualizer.Core.Archive;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace EfSchemaVisualizer.Core.Tests.Archive;

public class MultiFileSourceMergerTests
{
    private static void AssertParsesWithoutErrors(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var errors = tree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0, "Expected no parse errors, got: " + string.Join("; ", errors));
    }

    [Fact]
    public void Merge_NoFiles_ReturnsEmptyString()
    {
        Assert.Equal(string.Empty, MultiFileSourceMerger.Merge(Array.Empty<string>()));
    }

    [Fact]
    public void Merge_OneFile_ReturnsItUnchanged()
    {
        const string source = "public class Blog { public int Id { get; set; } }";
        Assert.Equal(source, MultiFileSourceMerger.Merge(new[] { source }));
    }

    [Fact]
    public void Merge_TwoFilesWithDifferentFileScopedNamespaces_ProducesParseableSource()
    {
        const string blogFile = """
            using System;

            namespace MyApp.Blogging;

            public class Blog
            {
                public int Id { get; set; }
            }
            """;

        const string tagFile = """
            namespace MyApp.Tagging;

            public class Tag
            {
                public int Id { get; set; }
            }
            """;

        var merged = MultiFileSourceMerger.Merge(new[] { blogFile, tagFile });

        AssertParsesWithoutErrors(merged);
        Assert.Contains("class Blog", merged);
        Assert.Contains("class Tag", merged);
    }

    [Fact]
    public void Merge_TwoFilesWithSameFileScopedNamespace_ProducesParseableSource()
    {
        const string customerFile = """
            namespace MyApp.Entities;

            public class Customer
            {
                public int Id { get; set; }
            }
            """;

        const string orderFile = """
            namespace MyApp.Entities;

            public class Order
            {
                public int Id { get; set; }
                public int CustomerId { get; set; }
            }
            """;

        var merged = MultiFileSourceMerger.Merge(new[] { customerFile, orderFile });

        AssertParsesWithoutErrors(merged);
        Assert.Contains("class Customer", merged);
        Assert.Contains("class Order", merged);
    }

    [Fact]
    public void Merge_DuplicateUsingAcrossFiles_AppearsOnlyOnce()
    {
        const string fileA = "using System;\npublic class A { }";
        const string fileB = "using System;\npublic class B { }";

        var merged = MultiFileSourceMerger.Merge(new[] { fileA, fileB });

        AssertParsesWithoutErrors(merged);
        var occurrences = merged.Split("using System;").Length - 1;
        Assert.Equal(1, occurrences);
    }

    [Fact]
    public void Merge_FilesWithNoNamespaceAtAll_ProducesParseableSource()
    {
        const string fileA = "public class A { public int Id { get; set; } }";
        const string fileB = "public class B { public int Id { get; set; } }";

        var merged = MultiFileSourceMerger.Merge(new[] { fileA, fileB });

        AssertParsesWithoutErrors(merged);
        Assert.Contains("class A", merged);
        Assert.Contains("class B", merged);
    }

    [Fact]
    public void Merge_BareTopLevelStatementFiles_KeepsStatementsBeforeAnyTypeDeclarations()
    {
        const string fileA = "modelBuilder.Entity<Blog>(entity => entity.HasKey(e => e.Id));";
        const string fileB = "modelBuilder.Entity<Post>(entity => entity.HasKey(e => e.Id));";

        var merged = MultiFileSourceMerger.Merge(new[] { fileA, fileB });

        AssertParsesWithoutErrors(merged);
        Assert.Contains("Entity<Blog>", merged);
        Assert.Contains("Entity<Post>", merged);
    }

    [Fact]
    public void Merge_IEntityTypeConfigurationFilesWithDifferentNamespaces_ProducesParseableSource()
    {
        const string customerConfig = """
            using Microsoft.EntityFrameworkCore.Metadata.Builders;

            namespace MyApp.Data.Customers;

            public class CustomerConfiguration : IEntityTypeConfiguration<Customer>
            {
                public void Configure(EntityTypeBuilder<Customer> builder)
                {
                    builder.HasKey(e => e.Id);
                }
            }
            """;

        const string orderConfig = """
            using Microsoft.EntityFrameworkCore.Metadata.Builders;

            namespace MyApp.Data.Orders;

            public class OrderConfiguration : IEntityTypeConfiguration<Order>
            {
                public void Configure(EntityTypeBuilder<Order> builder)
                {
                    builder.HasKey(e => e.Id);
                }
            }
            """;

        var merged = MultiFileSourceMerger.Merge(new[] { customerConfig, orderConfig });

        AssertParsesWithoutErrors(merged);
        Assert.Contains("CustomerConfiguration", merged);
        Assert.Contains("OrderConfiguration", merged);
    }
}
