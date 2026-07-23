using System.Collections.Generic;
using System.Linq;
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

    [Fact]
    public void Split_ZeroOrOneDistinctOrigin_ShortCircuitsToUnchangedSource()
    {
        const string source = "public class Blog { public int Id { get; set; } }";

        var noOrigins = MultiFileSourceMerger.Split(source, new Dictionary<string, string>(), "Entities.cs");
        Assert.Equal(source, Assert.Single(noOrigins).Value);
        Assert.Equal("Entities.cs", Assert.Single(noOrigins).Key);

        var oneOrigin = MultiFileSourceMerger.Split(
            source, new Dictionary<string, string> { ["Blog"] = "Models/Blog.cs" }, "Entities.cs");
        Assert.Equal(source, Assert.Single(oneOrigin).Value);
        Assert.Equal("Models/Blog.cs", Assert.Single(oneOrigin).Key);
    }

    [Fact]
    public void Split_TwoClassFilesSameNamespace_RoutesEachTypeToItsOwnFileAndBothCompile()
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
                public Customer Customer { get; set; } = null!;
            }
            """;

        var merged = MultiFileSourceMerger.Merge(new[] { customerFile, orderFile });
        var origins = new Dictionary<string, string>
        {
            ["Customer"] = "Entities/Customer.cs",
            ["Order"] = "Entities/Order.cs",
        };

        var split = MultiFileSourceMerger.Split(merged, origins, "Entities.cs");

        Assert.Equal(2, split.Count);
        AssertParsesWithoutErrors(split["Entities/Customer.cs"]);
        AssertParsesWithoutErrors(split["Entities/Order.cs"]);
        Assert.Contains("class Customer", split["Entities/Customer.cs"]);
        Assert.DoesNotContain("class Order", split["Entities/Customer.cs"]);
        Assert.Contains("class Order", split["Entities/Order.cs"]);
        Assert.DoesNotContain("class Customer {", split["Entities/Order.cs"]);
    }

    [Fact]
    public void Split_TwoClassFilesDifferentNamespaces_EachOutputKeepsItsOwnNamespace()
    {
        const string blogFile = "namespace MyApp.Blogging;\npublic class Blog { public int Id { get; set; } }";
        const string tagFile = "namespace MyApp.Tagging;\npublic class Tag { public int Id { get; set; } }";

        var merged = MultiFileSourceMerger.Merge(new[] { blogFile, tagFile });
        var origins = new Dictionary<string, string> { ["Blog"] = "Blog.cs", ["Tag"] = "Tag.cs" };

        var split = MultiFileSourceMerger.Split(merged, origins, "Entities.cs");

        AssertParsesWithoutErrors(split["Blog.cs"]);
        AssertParsesWithoutErrors(split["Tag.cs"]);
        Assert.Contains("MyApp.Blogging", split["Blog.cs"]);
        Assert.Contains("MyApp.Tagging", split["Tag.cs"]);
    }

    [Fact]
    public void Split_TwoIEntityTypeConfigurationFiles_RoutesEachByItsEntityTypeArgument()
    {
        const string customerConfig = """
            namespace MyApp.Data;

            public class CustomerConfiguration : IEntityTypeConfiguration<Customer>
            {
                public void Configure(EntityTypeBuilder<Customer> builder)
                {
                    builder.HasKey(e => e.Id);
                }
            }
            """;

        const string orderConfig = """
            namespace MyApp.Data;

            public class OrderConfiguration : IEntityTypeConfiguration<Order>
            {
                public void Configure(EntityTypeBuilder<Order> builder)
                {
                    builder.HasKey(e => e.Id);
                }
            }
            """;

        var merged = MultiFileSourceMerger.Merge(new[] { customerConfig, orderConfig });
        var origins = new Dictionary<string, string>
        {
            ["Customer"] = "Data/CustomerConfiguration.cs",
            ["Order"] = "Data/OrderConfiguration.cs",
        };

        var split = MultiFileSourceMerger.Split(merged, origins, "DbContext.cs");

        Assert.Equal(2, split.Count);
        AssertParsesWithoutErrors(split["Data/CustomerConfiguration.cs"]);
        AssertParsesWithoutErrors(split["Data/OrderConfiguration.cs"]);
        Assert.Contains("CustomerConfiguration", split["Data/CustomerConfiguration.cs"]);
        Assert.Contains("OrderConfiguration", split["Data/OrderConfiguration.cs"]);
    }

    [Fact]
    public void Split_BareTopLevelEntityStatementsWithDistinctOrigins_RoutesEachStatementByItsEntityTypeArgument()
    {
        const string bareStatements = """
            modelBuilder.Entity<Customer>(entity => entity.HasKey(e => e.Id));
            modelBuilder.Entity<Order>(entity => entity.HasKey(e => e.Id));
            """;

        var origins = new Dictionary<string, string>
        {
            ["Customer"] = "Data/CustomerConfig.cs",
            ["Order"] = "Data/OrderConfig.cs",
        };

        var split = MultiFileSourceMerger.Split(bareStatements, origins, "DbContext.cs");

        Assert.Equal(2, split.Count);
        AssertParsesWithoutErrors(split["Data/CustomerConfig.cs"]);
        AssertParsesWithoutErrors(split["Data/OrderConfig.cs"]);
        Assert.Contains("Entity<Customer>", split["Data/CustomerConfig.cs"]);
        Assert.DoesNotContain("Entity<Order>", split["Data/CustomerConfig.cs"]);
        Assert.Contains("Entity<Order>", split["Data/OrderConfig.cs"]);
    }

    [Fact]
    public void Split_EntityWithNoRecordedOrigin_FallsBackToDefaultPath()
    {
        const string source = """
            namespace MyApp.Entities;

            public class Customer
            {
                public int Id { get; set; }
            }

            public class NewEntity
            {
                public int Id { get; set; }
            }
            """;

        var origins = new Dictionary<string, string>
        {
            ["Customer"] = "Entities/Customer.cs",
            // NewEntity intentionally has no recorded origin, plus one more distinct path so the
            // 2-file short-circuit doesn't apply and NewEntity actually exercises the fallback.
            ["Other"] = "Entities/Other.cs",
        };

        var split = MultiFileSourceMerger.Split(source, origins, "Entities.cs");

        Assert.Contains("Entities.cs", split.Keys);
        Assert.Contains("NewEntity", split["Entities.cs"]);
    }

    [Fact]
    public void Split_EntityNoLongerPresentInSource_OmitsItsFileEntirely()
    {
        const string source = "namespace MyApp.Entities;\npublic class Customer { public int Id { get; set; } }";

        var origins = new Dictionary<string, string>
        {
            ["Customer"] = "Entities/Customer.cs",
            ["Order"] = "Entities/Order.cs",
        };

        var split = MultiFileSourceMerger.Split(source, origins, "Entities.cs");

        Assert.Single(split);
        Assert.DoesNotContain("Entities/Order.cs", split.Keys);
    }

    [Fact]
    public void Split_NamespacelessTypesWithTwoDistinctOrigins_DoesNotThrow()
    {
        const string source = """
            public class Blog
            {
                public int Id { get; set; }
            }

            public class Tag
            {
                public int Id { get; set; }
            }
            """;

        var origins = new Dictionary<string, string> { ["Blog"] = "Blog.cs", ["Tag"] = "Tag.cs" };

        var split = MultiFileSourceMerger.Split(source, origins, "Entities.cs");

        AssertParsesWithoutErrors(split["Blog.cs"]);
        AssertParsesWithoutErrors(split["Tag.cs"]);
        Assert.Contains("class Blog", split["Blog.cs"]);
        Assert.Contains("class Tag", split["Tag.cs"]);
    }
}
