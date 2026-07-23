# Multi-file round-trip compile (F3) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix backlog item F3 — uploading a multi-file EF Core project (more than one entity-class file and/or more than one config file), editing it, and downloading it currently produces a non-compiling `Entities.cs`/`DbContext.cs` (`CS1529` from concatenated `using` blocks, plus multiple illegal `namespace X;` declarations collapsing into one). After this plan, every class/config file an entity originally came from is reproduced as its own valid, compilable file on download, and this survives entity renames.

**Architecture:** A new pure-logic class, `MultiFileSourceMerger`, sits at the read/write boundary only — `DiagramEditor`'s ~30 edit methods and both Roslyn rewriters (`EntityClassRewriter`, `OnModelCreatingRewriter`) are untouched and keep operating on a single merged `ClassSource`/`ConfigSource` string exactly as today. `Merge(fileContents)` folds N original files into one syntactically valid compilation unit (hoisting/deduping `using` directives, converting each file's file-scoped namespace into an equivalent block namespace so multiple files' namespaces can coexist as siblings, keeping top-level statements first). `ProjectArchiveReader` calls `Merge` instead of a naive `string.Join`. `Split(mergedSource, fileOrigins, defaultPath)` reverses this at download time: it re-parses the (possibly edited) merged source and routes each top-level declaration/statement back to its original file via the existing `entityFileOrigins`/`configFileOrigins` maps (entity name → path), grouping same-namespace types under one wrapper per output file. `ProjectArchiveWriter` calls `Split` instead of its current fixed two-entry write. Because renames must keep an entity's file origin attached to its new name, `DiagramEditor` gains ownership of both origin maps (constructor parameters + public properties), reconciling them at the exact same touch points it already reconciles `_entityIds` (rename moves the key, remove drops it, undo/redo snapshot them). `Home.razor` is simplified to read origins off the editor instead of tracking them itself, which incidentally fixes them going stale across a rename.

**Tech Stack:** C# / .NET 10, Roslyn (`Microsoft.CodeAnalysis.CSharp` 5.6.0), `System.IO.Compression.ZipArchive`, xUnit, Blazor WebAssembly.

## Global Constraints

- No changes to `EntityClassRewriter`, `OnModelCreatingRewriter`, `EntityClassParser`, `FluentConfigParser`, `ModelMerger`, or `DiagramModelBuilder` — this is a read/write-boundary fix, not a change to the parse→edit→regenerate engine (which the backlog already confirms is sound).
- `ProjectArchiveWriter.Write`'s existing positional/optional parameters must keep compiling unchanged: `Write(classSource, configSource)` and `Write(classSource, configSource, layout, entityFileOrigins, configFileOrigins, passthroughFiles)` call sites must not break.
- Every new `DiagramEditor` constructor parameter must default to `null` so every existing 2-arg `new DiagramEditor(classSource, configSource)` call site (production and test) keeps compiling unchanged.
- The all-`using`s-into-every-split-file behavior (an intentional, documented over-approximation — safe because unused usings are warnings, not errors) must not be "fixed" into per-file using pruning; that's out of scope.
- A file whose routed member set ends up empty (e.g. its only entity was removed) must be omitted from `Split`'s result entirely — never emit an empty stub file.
- The existing single-file / no-recorded-origin behavior (the common freehand-paste case, and the already-shipped F2 single-class-file/single-config-file case) must be preserved byte-for-byte — those cases must short-circuit before any Roslyn re-parse/reformat happens.
- Run `dotnet test EfSchemaVisualizer.slnx` after every task; all tests (including all pre-existing ones) must stay green.

---

### Task 1: `MultiFileSourceMerger.Merge` — fold N files into one valid compilation unit

**Files:**
- Create: `src/EfSchemaVisualizer.Core/Archive/MultiFileSourceMerger.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Archive/MultiFileSourceMergerTests.cs`

**Interfaces:**
- Produces: `public static class MultiFileSourceMerger { public static string Merge(IReadOnlyList<string> fileContents); }` — consumed by Task 3 (`ProjectArchiveReader`).

- [ ] **Step 1: Write the failing tests**

Create `tests/EfSchemaVisualizer.Core.Tests/Archive/MultiFileSourceMergerTests.cs`:

```csharp
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
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test EfSchemaVisualizer.slnx --filter "FullyQualifiedName~MultiFileSourceMergerTests"`
Expected: FAIL to compile — `MultiFileSourceMerger` does not exist yet.

- [ ] **Step 3: Create `MultiFileSourceMerger` with the `Merge` method**

Create `src/EfSchemaVisualizer.Core/Archive/MultiFileSourceMerger.cs`:

```csharp
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EfSchemaVisualizer.Core.Archive;

/// Bridges "many original .cs files" and "one editable blob" so DiagramEditor's rewriters, which
/// operate on a single ClassSource/ConfigSource string, can round-trip a multi-file upload without
/// producing illegal C# (multiple `using` blocks or file-scoped `namespace` declarations
/// concatenated into one compilation unit — backlog item F3).
///
/// `Merge` folds N files into one syntactically valid compilation unit: usings are hoisted and
/// deduplicated, every file's own namespace (file-scoped or block-scoped) is preserved as its own
/// block so files that used different namespaces can still coexist as siblings, and bare top-level
/// statements from every file are kept together ahead of any namespace/type declarations (the same
/// ordering C# requires of a single file that mixes top-level statements with extra type
/// declarations).
public static class MultiFileSourceMerger
{
    public static string Merge(IReadOnlyList<string> fileContents)
    {
        if (fileContents.Count == 0)
        {
            return string.Empty;
        }

        if (fileContents.Count == 1)
        {
            return fileContents[0];
        }

        var mergedUsings = new List<UsingDirectiveSyntax>();
        var seenUsings = new HashSet<string>();
        var globalStatements = new List<MemberDeclarationSyntax>();
        var otherMembers = new List<MemberDeclarationSyntax>();

        foreach (var fileContent in fileContents)
        {
            var root = CSharpSyntaxTree.ParseText(fileContent).GetCompilationUnitRoot();

            foreach (var usingDirective in root.Usings)
            {
                if (seenUsings.Add(usingDirective.ToString().Trim()))
                {
                    mergedUsings.Add(usingDirective);
                }
            }

            foreach (var member in root.Members)
            {
                if (member is GlobalStatementSyntax globalStatement)
                {
                    globalStatements.Add(globalStatement);
                }
                else if (member is FileScopedNamespaceDeclarationSyntax fileScopedNamespace)
                {
                    // A compilation unit can only have one file-scoped namespace; converting to
                    // the equivalent block form lets N files' namespaces coexist as siblings.
                    otherMembers.Add(
                        SyntaxFactory.NamespaceDeclaration(fileScopedNamespace.Name)
                            .WithMembers(fileScopedNamespace.Members));
                }
                else
                {
                    otherMembers.Add(member);
                }
            }
        }

        var mergedRoot = SyntaxFactory.CompilationUnit()
            .WithUsings(SyntaxFactory.List(mergedUsings))
            .WithMembers(SyntaxFactory.List(globalStatements.Concat(otherMembers)));

        return mergedRoot.NormalizeWhitespace().ToFullString();
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test EfSchemaVisualizer.slnx --filter "FullyQualifiedName~MultiFileSourceMergerTests"`
Expected: PASS (7 tests)

- [ ] **Step 5: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Archive/MultiFileSourceMerger.cs tests/EfSchemaVisualizer.Core.Tests/Archive/MultiFileSourceMergerTests.cs
git commit -m "Add MultiFileSourceMerger.Merge: fold N source files into one valid compilation unit"
```

---

### Task 2: `MultiFileSourceMerger.Split` — route the merged/edited source back to original files

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/Archive/MultiFileSourceMerger.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Archive/MultiFileSourceMergerTests.cs`

**Interfaces:**
- Consumes: nothing new from Task 1 beyond the same file (same class).
- Produces: `public static IReadOnlyDictionary<string, string> Split(string mergedSource, IReadOnlyDictionary<string, string> fileOrigins, string defaultPath)` — consumed by Task 4 (`ProjectArchiveWriter`).

- [ ] **Step 1: Write the failing tests**

Append to `tests/EfSchemaVisualizer.Core.Tests/Archive/MultiFileSourceMergerTests.cs` (inside the class, after the `Merge` tests):

```csharp
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
```

Add `using System.Collections.Generic;` and `using System.Linq;` to the top of the test file if not already present via implicit usings (the `Core.Tests` project has `ImplicitUsings` enabled the same way `ProjectArchiveWriterTests.cs` does, so this should already resolve — check `EfSchemaVisualizer.Core.Tests.csproj` if the build complains).

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test EfSchemaVisualizer.slnx --filter "FullyQualifiedName~MultiFileSourceMergerTests"`
Expected: FAIL to compile — `MultiFileSourceMerger.Split` does not exist yet.

- [ ] **Step 3: Add the `Split` method**

Append to `src/EfSchemaVisualizer.Core/Archive/MultiFileSourceMerger.cs`, inside the `MultiFileSourceMerger` class, after `Merge`. Also add `using EfSchemaVisualizer.Core.Parsing;` to the file's using list.

```csharp
    /// Reverses `Merge` for the (possibly edited) merged source, routing each top-level
    /// declaration/statement back to its original file via `fileOrigins` (entity/config name ->
    /// path). Anything with no recorded origin (e.g. a brand-new entity added in the editor) falls
    /// back to `defaultPath`. A file that ends up with nothing routed to it is omitted.
    public static IReadOnlyDictionary<string, string> Split(
        string mergedSource,
        IReadOnlyDictionary<string, string> fileOrigins,
        string defaultPath)
    {
        var distinctPaths = fileOrigins.Values.Distinct().ToList();
        if (distinctPaths.Count <= 1)
        {
            var singlePath = distinctPaths.Count == 1 ? distinctPaths[0] : defaultPath;
            return new Dictionary<string, string> { [singlePath] = mergedSource };
        }

        var root = CSharpSyntaxTree.ParseText(mergedSource).GetCompilationUnitRoot();
        var routed = new List<(string Path, string? Namespace, MemberDeclarationSyntax Member)>();

        string ResolvePath(string? name) =>
            name is not null && fileOrigins.TryGetValue(name, out var path) ? path : defaultPath;

        string? ResolveEntityNameForType(TypeDeclarationSyntax type)
        {
            if (type is ClassDeclarationSyntax classDeclaration)
            {
                var configuredEntityName = FluentSyntaxHelpers.TryGetEntityTypeConfigurationEntityName(classDeclaration);
                if (configuredEntityName is not null)
                {
                    return configuredEntityName;
                }
            }

            if (fileOrigins.ContainsKey(type.Identifier.Text))
            {
                return type.Identifier.Text;
            }

            // A DbContext-shaped class configuring one or more entities via bare Entity<T>()
            // calls inside a method body: route the whole class alongside whichever entity it
            // configures (only reachable when every entity inside it shares one origin, since the
            // 2-distinct-path short-circuit above already handled the single-origin case).
            return type.DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Select(FluentSyntaxHelpers.GetConfiguredEntityName)
                .FirstOrDefault(name => name is not null);
        }

        void Route(MemberDeclarationSyntax member, string? namespaceName)
        {
            switch (member)
            {
                case GlobalStatementSyntax globalStatement:
                    var entityName = globalStatement.DescendantNodesAndSelf()
                        .OfType<InvocationExpressionSyntax>()
                        .Select(FluentSyntaxHelpers.GetConfiguredEntityName)
                        .FirstOrDefault(name => name is not null);
                    routed.Add((ResolvePath(entityName), null, member));
                    break;

                case FileScopedNamespaceDeclarationSyntax fileScopedNamespace:
                    foreach (var nested in fileScopedNamespace.Members)
                    {
                        Route(nested, fileScopedNamespace.Name.ToString());
                    }

                    break;

                case NamespaceDeclarationSyntax namespaceDeclaration:
                    foreach (var nested in namespaceDeclaration.Members)
                    {
                        Route(nested, namespaceDeclaration.Name.ToString());
                    }

                    break;

                case TypeDeclarationSyntax typeDeclaration:
                    routed.Add((ResolvePath(ResolveEntityNameForType(typeDeclaration)), namespaceName, member));
                    break;

                default:
                    routed.Add((defaultPath, namespaceName, member));
                    break;
            }
        }

        foreach (var member in root.Members)
        {
            Route(member, namespaceName: null);
        }

        var pathOrder = new List<string>();
        var byPath = new Dictionary<string, List<(string? Namespace, MemberDeclarationSyntax Member)>>();
        foreach (var (path, ns, member) in routed)
        {
            if (!byPath.TryGetValue(path, out var members))
            {
                members = new List<(string? Namespace, MemberDeclarationSyntax Member)>();
                byPath[path] = members;
                pathOrder.Add(path);
            }

            members.Add((ns, member));
        }

        var result = new Dictionary<string, string>();
        foreach (var path in pathOrder)
        {
            result[path] = BuildFileContent(root.Usings, byPath[path]);
        }

        return result;
    }

    private static string BuildFileContent(
        SyntaxList<UsingDirectiveSyntax> usings,
        List<(string? Namespace, MemberDeclarationSyntax Member)> entries)
    {
        var globalStatements = entries.Where(e => e.Member is GlobalStatementSyntax).Select(e => e.Member).ToList();

        var namespaceOrder = new List<string?>();
        var byNamespace = new Dictionary<string?, List<MemberDeclarationSyntax>>();
        foreach (var (ns, member) in entries)
        {
            if (member is GlobalStatementSyntax)
            {
                continue;
            }

            if (!byNamespace.TryGetValue(ns, out var members))
            {
                members = new List<MemberDeclarationSyntax>();
                byNamespace[ns] = members;
                namespaceOrder.Add(ns);
            }

            members.Add(member);
        }

        var finalMembers = new List<MemberDeclarationSyntax>(globalStatements);
        foreach (var ns in namespaceOrder)
        {
            var members = byNamespace[ns];
            if (ns is null)
            {
                finalMembers.AddRange(members);
            }
            else
            {
                finalMembers.Add(
                    SyntaxFactory.NamespaceDeclaration(SyntaxFactory.ParseName(ns))
                        .WithMembers(SyntaxFactory.List(members)));
            }
        }

        var newRoot = SyntaxFactory.CompilationUnit()
            .WithUsings(usings)
            .WithMembers(SyntaxFactory.List(finalMembers));

        return newRoot.NormalizeWhitespace().ToFullString();
    }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test EfSchemaVisualizer.slnx --filter "FullyQualifiedName~MultiFileSourceMergerTests"`
Expected: PASS (all `Merge` + `Split` tests, 15 total)

- [ ] **Step 5: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Archive/MultiFileSourceMerger.cs tests/EfSchemaVisualizer.Core.Tests/Archive/MultiFileSourceMergerTests.cs
git commit -m "Add MultiFileSourceMerger.Split: route merged source back to original files"
```

---

### Task 3: Wire `Merge` into `ProjectArchiveReader`

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/Archive/ProjectArchiveReader.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Archive/ProjectArchiveReaderTests.cs`

**Interfaces:**
- Consumes: `MultiFileSourceMerger.Merge(IReadOnlyList<string>)` from Task 1.
- Produces: `ProjectArchiveResult.ClassSource`/`ConfigSource` are now always parseable even for multi-file uploads (previously could be `CS1529`-broken).

- [ ] **Step 1: Write the failing test**

Append to `tests/EfSchemaVisualizer.Core.Tests/Archive/ProjectArchiveReaderTests.cs` (inside the class):

```csharp
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
```

Check the top of `ProjectArchiveReaderTests.cs` for a `CreateZip` helper matching the one in `ProjectArchiveRoundTripTests.cs` (`params (string Name, string Content)[]` or byte-array overload) — reuse whichever already exists in that file rather than adding a duplicate.

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test EfSchemaVisualizer.slnx --filter "FullyQualifiedName~Read_MultipleClassFilesWithOwnUsingsAndNamespaces"`
Expected: FAIL — `result.ClassSource` has a `CS1529` parse error from the naive `string.Join`.

- [ ] **Step 3: Replace the naive join with `MultiFileSourceMerger.Merge`**

In `src/EfSchemaVisualizer.Core/Archive/ProjectArchiveReader.cs`, replace:

```csharp
        var classSource = string.Join(Environment.NewLine + Environment.NewLine, classFiles);
        var configSource = string.Join(Environment.NewLine + Environment.NewLine, configFiles);
```

with:

```csharp
        var classSource = MultiFileSourceMerger.Merge(classFiles);
        var configSource = MultiFileSourceMerger.Merge(configFiles);
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test EfSchemaVisualizer.slnx --filter "FullyQualifiedName~ProjectArchiveReaderTests"`
Expected: PASS, including all pre-existing `ProjectArchiveReaderTests`.

Run: `dotnet test EfSchemaVisualizer.slnx`
Expected: full suite green (this changes shared behavior — confirm no other test regressed).

- [ ] **Step 5: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Archive/ProjectArchiveReader.cs tests/EfSchemaVisualizer.Core.Tests/Archive/ProjectArchiveReaderTests.cs
git commit -m "ProjectArchiveReader: merge multi-file uploads into a valid compilation unit"
```

---

### Task 4: Wire `Split` into `ProjectArchiveWriter`

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/Archive/ProjectArchiveWriter.cs`
- Modify: `tests/EfSchemaVisualizer.Core.Tests/Archive/ProjectArchiveWriterTests.cs`

**Interfaces:**
- Consumes: `MultiFileSourceMerger.Split(string, IReadOnlyDictionary<string,string>, string)` from Task 2.
- Produces: `ProjectArchiveWriter.Write` now emits one compilable file per distinct class/config origin instead of collapsing to `Entities.cs`/`DbContext.cs` — no public signature change.

- [ ] **Step 1: Replace the outdated fallback test**

The existing test asserts the old (now-superseded) F2-scoped fallback behavior. In `tests/EfSchemaVisualizer.Core.Tests/Archive/ProjectArchiveWriterTests.cs`, replace:

```csharp
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
```

with:

```csharp
    [Fact]
    public void Write_MultipleDistinctClassOrigins_SplitsEachEntityBackToItsOwnOriginalFile()
    {
        var entityFileOrigins = new Dictionary<string, string>
        {
            ["Blog"] = "Models/Blog.cs",
            ["Post"] = "Models/Post.cs",
        };

        var bytes = ProjectArchiveWriter.Write(
            "public class Blog { } public class Post { }", "", entityFileOrigins: entityFileOrigins);

        using var stream = new MemoryStream(bytes);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);

        Assert.Null(zip.GetEntry("Entities.cs"));

        var blogEntry = zip.GetEntry("Models/Blog.cs");
        Assert.NotNull(blogEntry);
        using (var reader = new StreamReader(blogEntry!.Open()))
        {
            var content = reader.ReadToEnd();
            Assert.Contains("class Blog", content);
            Assert.DoesNotContain("class Post", content);
        }

        var postEntry = zip.GetEntry("Models/Post.cs");
        Assert.NotNull(postEntry);
        using (var reader = new StreamReader(postEntry!.Open()))
        {
            var content = reader.ReadToEnd();
            Assert.Contains("class Post", content);
        }
    }
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test EfSchemaVisualizer.slnx --filter "FullyQualifiedName~Write_MultipleDistinctClassOrigins"`
Expected: FAIL — writer still falls back to `Entities.cs` for 2+ distinct origins.

- [ ] **Step 3: Rewrite `ProjectArchiveWriter.Write` to use `Split`**

Replace the full contents of `src/EfSchemaVisualizer.Core/Archive/ProjectArchiveWriter.cs` with:

```csharp
using System.IO.Compression;
using System.Text.Json;

namespace EfSchemaVisualizer.Core.Archive;

public static class ProjectArchiveWriter
{
    public const string LayoutFileName = "diagram-layout.json";

    public static byte[] Write(
        string classSource,
        string configSource,
        IReadOnlyDictionary<string, EntityPosition>? layout = null,
        IReadOnlyDictionary<string, string>? entityFileOrigins = null,
        IReadOnlyDictionary<string, string>? configFileOrigins = null,
        IReadOnlyDictionary<string, byte[]>? passthroughFiles = null)
    {
        using var stream = new MemoryStream();

        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var writtenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var classFiles = MultiFileSourceMerger.Split(
                classSource, entityFileOrigins ?? new Dictionary<string, string>(), "Entities.cs");
            foreach (var (path, content) in classFiles)
            {
                WriteEntry(zip, path, content);
                writtenPaths.Add(path);
            }

            var configFiles = MultiFileSourceMerger.Split(
                configSource, configFileOrigins ?? new Dictionary<string, string>(), "DbContext.cs");
            foreach (var (path, content) in configFiles)
            {
                WriteEntry(zip, path, content);
                writtenPaths.Add(path);
            }

            if (passthroughFiles is not null)
            {
                foreach (var (path, bytes) in passthroughFiles)
                {
                    // The chosen class/config paths always carry the current (possibly edited)
                    // source; a stale passthrough entry at the same path must not overwrite it.
                    if (writtenPaths.Add(path))
                    {
                        WriteEntry(zip, path, bytes);
                    }
                }
            }

            if (layout is { Count: > 0 } && writtenPaths.Add(LayoutFileName))
            {
                WriteEntry(zip, LayoutFileName, JsonSerializer.Serialize(layout));
            }
        }

        return stream.ToArray();
    }

    private static void WriteEntry(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }

    private static void WriteEntry(ZipArchive zip, string name, byte[] content)
    {
        var entry = zip.CreateEntry(name);
        using var entryStream = entry.Open();
        entryStream.Write(content, 0, content.Length);
    }
}
```

Note `SingleOriginPathOrDefault` is gone — `MultiFileSourceMerger.Split` now owns that exact short-circuit (0-or-1-distinct-origin case) internally, so behavior for every existing test (`Write_ProducesZipWithTwoFixedNameEntries...`, `Write_EmptyBlobs_StillWritesBothEntries`, `Write_SingleClassOrigin_WritesClassSourceUnderItsOriginalPath`, `Write_EmptyEntityFileOrigins_BehavesSameAsNull_FallsBackToDefaultName`) is unchanged.

- [ ] **Step 4: Run the full Archive test suite**

Run: `dotnet test EfSchemaVisualizer.slnx --filter "FullyQualifiedName~Archive"`
Expected: PASS — every pre-existing `ProjectArchiveWriterTests`/`ProjectArchiveReaderTests`/`ProjectArchiveRoundTripTests`/`MultiFileSourceMergerTests` test green, plus the new multi-origin split test.

Run: `dotnet test EfSchemaVisualizer.slnx`
Expected: full suite green.

- [ ] **Step 5: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Archive/ProjectArchiveWriter.cs tests/EfSchemaVisualizer.Core.Tests/Archive/ProjectArchiveWriterTests.cs
git commit -m "ProjectArchiveWriter: split multi-file downloads back to their original files"
```

---

### Task 5: `DiagramEditor` owns and reconciles entity file origins through rename/add/remove/undo/redo

**Files:**
- Modify: `src/EfSchemaVisualizer.Web/Diagram/DiagramEditor.cs`
- Test: `tests/EfSchemaVisualizer.Web.Tests/Diagram/DiagramEditorTests.cs`

**Interfaces:**
- Produces: `DiagramEditor` gains a new constructor overload accepting `entityFileOrigins`/`configFileOrigins`, and public properties `EntityFileOrigins`/`ConfigFileOrigins` (both `IReadOnlyDictionary<string,string>`) that stay in sync with entity renames/removals exactly like the existing `EntityIds` property — consumed by Task 6 (`Home.razor`).

**Why this task exists:** `Home.razor` currently tracks `_entityFileOrigins`/`_configFileOrigins` as plain fields, disconnected from `DiagramEditor`'s rename logic. After a rename, the origin map still has the *old* entity name as its key, so a subsequent download would misroute (or lose) that entity's original file. Moving ownership into `DiagramEditor`, alongside the already-existing `_entityIds` reconciliation, fixes this the same way `_entityIds` itself is kept in sync today.

- [ ] **Step 1: Write the failing tests**

Append to `tests/EfSchemaVisualizer.Web.Tests/Diagram/DiagramEditorTests.cs` (inside the class):

```csharp
    [Fact]
    public void Constructor_WithFileOrigins_ExposesThemUnchanged()
    {
        var entityFileOrigins = new Dictionary<string, string> { ["Blog"] = "Models/Blog.cs" };
        var configFileOrigins = new Dictionary<string, string> { ["Blog"] = "Data/AppDbContext.cs" };

        var editor = new DiagramEditor(ClassSource, ConfigSource, entityFileOrigins, configFileOrigins);

        Assert.Equal("Models/Blog.cs", editor.EntityFileOrigins["Blog"]);
        Assert.Equal("Data/AppDbContext.cs", editor.ConfigFileOrigins["Blog"]);
    }

    [Fact]
    public void Constructor_WithoutFileOrigins_ExposesEmptyMaps()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);

        Assert.Empty(editor.EntityFileOrigins);
        Assert.Empty(editor.ConfigFileOrigins);
    }

    [Fact]
    public void RenameEntity_MovesFileOriginToTheNewName()
    {
        var entityFileOrigins = new Dictionary<string, string> { ["Blog"] = "Models/Blog.cs" };
        var configFileOrigins = new Dictionary<string, string> { ["Blog"] = "Data/AppDbContext.cs" };
        var editor = new DiagramEditor(ClassSource, ConfigSource, entityFileOrigins, configFileOrigins);

        editor.RenameEntity("Blog", "Post");

        Assert.False(editor.EntityFileOrigins.ContainsKey("Blog"));
        Assert.Equal("Models/Blog.cs", editor.EntityFileOrigins["Post"]);
        Assert.False(editor.ConfigFileOrigins.ContainsKey("Blog"));
        Assert.Equal("Data/AppDbContext.cs", editor.ConfigFileOrigins["Post"]);
    }

    [Fact]
    public void RemoveEntity_DropsItsFileOrigins()
    {
        const string classSource = """
            public class Blog
            {
                public int Id { get; set; }
            }

            public class Post
            {
                public int Id { get; set; }
                public int BlogId { get; set; }
            }
            """;
        const string configSource = """
            modelBuilder.Entity<Blog>(entity => entity.HasKey(e => e.Id));
            modelBuilder.Entity<Post>(entity => entity.HasKey(e => e.Id));
            """;
        var entityFileOrigins = new Dictionary<string, string> { ["Post"] = "Models/Post.cs" };
        var editor = new DiagramEditor(classSource, configSource, entityFileOrigins);

        editor.RemoveEntity("Post");

        Assert.False(editor.EntityFileOrigins.ContainsKey("Post"));
    }

    [Fact]
    public void Undo_AfterRename_RestoresPreviousFileOrigins()
    {
        var entityFileOrigins = new Dictionary<string, string> { ["Blog"] = "Models/Blog.cs" };
        var editor = new DiagramEditor(ClassSource, ConfigSource, entityFileOrigins);

        editor.RenameEntity("Blog", "Post");
        editor.Undo();

        Assert.Equal("Models/Blog.cs", editor.EntityFileOrigins["Blog"]);
        Assert.False(editor.EntityFileOrigins.ContainsKey("Post"));
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test EfSchemaVisualizer.slnx --filter "FullyQualifiedName~DiagramEditorTests"`
Expected: FAIL to compile — no `EntityFileOrigins`/`ConfigFileOrigins` members or matching constructor overload yet.

- [ ] **Step 3: Add origin tracking to `DiagramEditor`**

In `src/EfSchemaVisualizer.Web/Diagram/DiagramEditor.cs`:

Replace the field/constructor/snapshot block at the top of the class:

```csharp
public sealed class DiagramEditor
{
    private readonly EntityClassRewriter _classRewriter = new();
    private readonly OnModelCreatingRewriter _configRewriter = new();
    private readonly Dictionary<string, Guid> _entityIds = new();
    private readonly Dictionary<string, string> _entityFileOrigins;
    private readonly Dictionary<string, string> _configFileOrigins;
    private readonly Stack<Snapshot> _undoStack = new();
    private readonly Stack<Snapshot> _redoStack = new();

    private sealed record Snapshot(
        string ClassSource,
        string ConfigSource,
        Dictionary<string, Guid> EntityIds,
        Dictionary<string, string> EntityFileOrigins,
        Dictionary<string, string> ConfigFileOrigins);

    public DiagramEditor(
        string classSource,
        string configSource,
        IReadOnlyDictionary<string, string>? entityFileOrigins = null,
        IReadOnlyDictionary<string, string>? configFileOrigins = null)
    {
        ClassSource = classSource;
        ConfigSource = configSource;
        Current = DiagramModelBuilder.Build(classSource, configSource);
        _entityFileOrigins = entityFileOrigins is null ? new() : new(entityFileOrigins);
        _configFileOrigins = configFileOrigins is null ? new() : new(configFileOrigins);

        foreach (var entity in Current.Entities)
        {
            _entityIds[entity.Name] = Guid.NewGuid();
        }
    }

    public string ClassSource { get; private set; }
    public string ConfigSource { get; private set; }
    public DiagramModelResult Current { get; private set; }
    public IReadOnlyDictionary<string, Guid> EntityIds => _entityIds;
    public IReadOnlyDictionary<string, string> EntityFileOrigins => _entityFileOrigins;
    public IReadOnlyDictionary<string, string> ConfigFileOrigins => _configFileOrigins;
```

In `RenameEntity`, find this block (which already re-keys `_entityIds`):

```csharp
        // Re-key before Apply so its entity-id reconciliation (which only fills in
        // missing names and drops stale ones) sees the rename as already accounted
        // for, instead of dropping oldName and minting a fresh Guid for newName.
        if (_entityIds.Remove(oldName, out var entityId))
        {
            _entityIds[newName] = entityId;
        }
        else
        {
            _entityIds[newName] = Guid.NewGuid();
        }

        Apply(newClassSource, newConfigSource);
```

and change it to also re-key both origin maps:

```csharp
        // Re-key before Apply so its entity-id reconciliation (which only fills in
        // missing names and drops stale ones) sees the rename as already accounted
        // for, instead of dropping oldName and minting a fresh Guid for newName.
        if (_entityIds.Remove(oldName, out var entityId))
        {
            _entityIds[newName] = entityId;
        }
        else
        {
            _entityIds[newName] = Guid.NewGuid();
        }

        if (_entityFileOrigins.Remove(oldName, out var entityFileOrigin))
        {
            _entityFileOrigins[newName] = entityFileOrigin;
        }

        if (_configFileOrigins.Remove(oldName, out var configFileOrigin))
        {
            _configFileOrigins[newName] = configFileOrigin;
        }

        Apply(newClassSource, newConfigSource);
```

In `RemoveEntity`, find:

```csharp
        var newClassSource = _classRewriter.RemoveClass(ClassSource, entityName);
        var newConfigSource = _configRewriter.RemoveEntity(ConfigSource, entityName);
        Apply(newClassSource, newConfigSource);
        _entityIds.Remove(entityName);
        return DiagramEditResult.Ok();
```

and change it to:

```csharp
        var newClassSource = _classRewriter.RemoveClass(ClassSource, entityName);
        var newConfigSource = _configRewriter.RemoveEntity(ConfigSource, entityName);
        Apply(newClassSource, newConfigSource);
        _entityIds.Remove(entityName);
        _entityFileOrigins.Remove(entityName);
        _configFileOrigins.Remove(entityName);
        return DiagramEditResult.Ok();
```

Update `CurrentSnapshot` and `Restore` to carry the origin maps:

```csharp
    private Snapshot CurrentSnapshot() => new(
        ClassSource,
        ConfigSource,
        new Dictionary<string, Guid>(_entityIds),
        new Dictionary<string, string>(_entityFileOrigins),
        new Dictionary<string, string>(_configFileOrigins));

    private void Restore(Snapshot snapshot)
    {
        ClassSource = snapshot.ClassSource;
        ConfigSource = snapshot.ConfigSource;
        _entityIds.Clear();
        foreach (var (name, id) in snapshot.EntityIds)
        {
            _entityIds[name] = id;
        }

        _entityFileOrigins.Clear();
        foreach (var (name, path) in snapshot.EntityFileOrigins)
        {
            _entityFileOrigins[name] = path;
        }

        _configFileOrigins.Clear();
        foreach (var (name, path) in snapshot.ConfigFileOrigins)
        {
            _configFileOrigins[name] = path;
        }

        Current = DiagramModelBuilder.Build(ClassSource, ConfigSource);
    }
```

Leave `Apply`'s existing `_entityIds` reconciliation loop untouched — deliberately do **not** add reconciliation for `_entityFileOrigins`/`_configFileOrigins` there (a hand-edited/new entity with no known origin should keep falling back to the default path in `ProjectArchiveWriter`, not get a fabricated one), but do drop stale entries the same defensive way:

```csharp
        var currentNames = Current.Entities.Select(e => e.Name).ToHashSet();
        foreach (var name in currentNames)
        {
            if (!_entityIds.ContainsKey(name))
            {
                _entityIds[name] = Guid.NewGuid();
            }
        }

        foreach (var staleName in _entityIds.Keys.Where(name => !currentNames.Contains(name)).ToList())
        {
            _entityIds.Remove(staleName);
        }

        foreach (var staleName in _entityFileOrigins.Keys.Where(name => !currentNames.Contains(name)).ToList())
        {
            _entityFileOrigins.Remove(staleName);
        }

        foreach (var staleName in _configFileOrigins.Keys.Where(name => !currentNames.Contains(name)).ToList())
        {
            _configFileOrigins.Remove(staleName);
        }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test EfSchemaVisualizer.slnx --filter "FullyQualifiedName~DiagramEditorTests"`
Expected: PASS.

Run: `dotnet test EfSchemaVisualizer.slnx`
Expected: full suite green (existing `DiagramEditor*Tests` files must be unaffected since the new constructor params default to `null`).

- [ ] **Step 5: Commit**

```bash
git add src/EfSchemaVisualizer.Web/Diagram/DiagramEditor.cs tests/EfSchemaVisualizer.Web.Tests/Diagram/DiagramEditorTests.cs
git commit -m "DiagramEditor: own and reconcile entity/config file origins across rename/remove/undo"
```

---

### Task 6: `Home.razor` reads file origins from `DiagramEditor` instead of tracking them itself

**Files:**
- Modify: `src/EfSchemaVisualizer.Web/Pages/Home.razor`

**Interfaces:**
- Consumes: `DiagramEditor(classSource, configSource, entityFileOrigins, configFileOrigins)` constructor and `editor.EntityFileOrigins`/`editor.ConfigFileOrigins` from Task 5.

**Why this task exists:** Today `_entityFileOrigins`/`_configFileOrigins` are plain `Home.razor` fields set once from `ProjectArchiveReader.Read` and never updated again — Task 5 makes `DiagramEditor` the source of truth that stays correct across renames, so `Home.razor` should read from it instead of keeping its own now-redundant (and rename-stale) copy.

There is no bUnit/component-render test harness in this repo for `Home.razor` (confirmed: no `RenderComponent`/bUnit usage anywhere under `tests/`), so this task is verified by a successful build plus the full test suite staying green, consistent with how this file's other logic is already handled in this codebase.

- [ ] **Step 1: Remove the now-redundant fields and thread origins through the constructor**

In `src/EfSchemaVisualizer.Web/Pages/Home.razor`, remove these three fields:

```csharp
    // Populated from an uploaded zip so DownloadZip can write files back under their original
    // names/paths instead of collapsing everything into Entities.cs/DbContext.cs (see F2). Reset
    // whenever the diagram is rendered from freehand pasted text, since that content is no longer
    // tied to any uploaded file's structure.
    private IReadOnlyDictionary<string, string>? _entityFileOrigins;
    private IReadOnlyDictionary<string, string>? _configFileOrigins;
    private IReadOnlyDictionary<string, byte[]>? _passthroughFiles;
```

and replace with just:

```csharp
    // Populated from an uploaded zip so DownloadZip can write files back under their original
    // names/paths instead of collapsing everything into Entities.cs/DbContext.cs (see F2/F3).
    // Reset whenever the diagram is rendered from freehand pasted text, since that content is no
    // longer tied to any uploaded file's structure. Entity/config origins themselves live on
    // `_editor` from construction onward (see F3), which keeps them correct across renames.
    private IReadOnlyDictionary<string, byte[]>? _passthroughFiles;
```

In `OnZipSelected`, find:

```csharp
            var archiveResult = ProjectArchiveReader.Read(memory);
            _classSource = archiveResult.ClassSource;
            _configSource = archiveResult.ConfigSource;

            await RenderDiagramAsync();

            // Must be set after RenderDiagramAsync, which clears them at the start of every render.
            _entityFileOrigins = archiveResult.EntityFileOrigins;
            _configFileOrigins = archiveResult.ConfigFileOrigins;
            _passthroughFiles = archiveResult.PassthroughFiles;
```

and replace with:

```csharp
            var archiveResult = ProjectArchiveReader.Read(memory);
            _classSource = archiveResult.ClassSource;
            _configSource = archiveResult.ConfigSource;

            await RenderDiagramAsync(archiveResult.EntityFileOrigins, archiveResult.ConfigFileOrigins);

            // Must be set after RenderDiagramAsync, which clears it at the start of every render.
            _passthroughFiles = archiveResult.PassthroughFiles;
```

In `RenderDiagramAsync`, find:

```csharp
    private async Task RenderDiagramAsync()
    {
        _error = null;
        _diagnostics = null;
        _diagram = null;
        _editContext = null;
        _entityFileOrigins = null;
        _configFileOrigins = null;
        _passthroughFiles = null;

        try
        {
            _editor = new DiagramEditor(_classSource, _configSource);
```

and replace with:

```csharp
    private async Task RenderDiagramAsync(
        IReadOnlyDictionary<string, string>? entityFileOrigins = null,
        IReadOnlyDictionary<string, string>? configFileOrigins = null)
    {
        _error = null;
        _diagnostics = null;
        _diagram = null;
        _editContext = null;
        _passthroughFiles = null;

        try
        {
            _editor = new DiagramEditor(_classSource, _configSource, entityFileOrigins, configFileOrigins);
```

`RenderDiagramAsync` is also bound directly to the "Render Diagram" button for the freehand-paste flow. **C# method-group-to-delegate conversion does not drop trailing optional parameters** (confirmed: `Func<Task> f = SomeMethodWithOptionalParams;` is `CS0123` when the method has any parameters, optional or not) — a bare `@onclick="RenderDiagramAsync"` binding will fail to compile once the method takes parameters. Find the button markup:

```razor
<button id="render-diagram" class="btn btn-primary" @onclick="RenderDiagramAsync">Render Diagram</button>
```

and change the binding to a zero-arg lambda, which is always a valid `Func<Task>` regardless of the target method's optional parameters:

```razor
<button id="render-diagram" class="btn btn-primary" @onclick="() => RenderDiagramAsync()">Render Diagram</button>
```

This keeps the freehand-paste flow calling `RenderDiagramAsync()` with both new parameters defaulting to `null`, matching `DiagramEditor`'s own defaults, so that flow keeps starting with empty origin maps exactly as before.

In `DownloadZip`, find:

```csharp
        var layout = _diagram is not null ? DiagramLayout.Capture(_diagram) : null;
        var bytes = ProjectArchiveWriter.Write(
            _editor.ClassSource, _editor.ConfigSource, layout,
            _entityFileOrigins, _configFileOrigins, _passthroughFiles);
```

and replace with:

```csharp
        var layout = _diagram is not null ? DiagramLayout.Capture(_diagram) : null;
        var bytes = ProjectArchiveWriter.Write(
            _editor.ClassSource, _editor.ConfigSource, layout,
            _editor.EntityFileOrigins, _editor.ConfigFileOrigins, _passthroughFiles);
```

- [ ] **Step 2: Build the Web project**

Run: `dotnet build src/EfSchemaVisualizer.Web/EfSchemaVisualizer.Web.csproj`
Expected: builds with no errors (Blazor `.razor` files compile as part of this build).

- [ ] **Step 3: Run the full test suite**

Run: `dotnet test EfSchemaVisualizer.slnx`
Expected: full suite green.

- [ ] **Step 4: Commit**

```bash
git add src/EfSchemaVisualizer.Web/Pages/Home.razor
git commit -m "Home.razor: source entity/config file origins from DiagramEditor, not a stale local copy"
```

---

### Task 7: End-to-end regression test reproducing the exact F3 scenario, and close out the backlog item

**Files:**
- Test: `tests/EfSchemaVisualizer.Core.Tests/Archive/ProjectArchiveRoundTripTests.cs`
- Modify: `docs/backlog.md`

**Interfaces:**
- Consumes: `ProjectArchiveReader.Read` (Task 3), `EfSchemaVisualizer.Web.Diagram.DiagramEditor` (Task 5), `ProjectArchiveWriter.Write` (Task 4) — this test exercises the full upload → edit → download pipeline the same way the backlog's original F3 finding was verified.

Note: `ProjectArchiveRoundTripTests.cs` lives in `EfSchemaVisualizer.Core.Tests`, which does not reference `EfSchemaVisualizer.Web` (where `DiagramEditor` lives). Check `tests/EfSchemaVisualizer.Core.Tests/EfSchemaVisualizer.Core.Tests.csproj` — if it has no project reference to `EfSchemaVisualizer.Web`, add one before writing this test, since exercising the *edit* step (not just read/write) needs `DiagramEditor`.

- [ ] **Step 1: Add the project reference if missing**

Run: `grep -q "EfSchemaVisualizer.Web.csproj" tests/EfSchemaVisualizer.Core.Tests/EfSchemaVisualizer.Core.Tests.csproj && echo present || echo missing`

If `missing`, run:

```bash
dotnet add tests/EfSchemaVisualizer.Core.Tests/EfSchemaVisualizer.Core.Tests.csproj reference src/EfSchemaVisualizer.Web/EfSchemaVisualizer.Web.csproj
```

- [ ] **Step 2: Write the failing end-to-end test**

Append to `tests/EfSchemaVisualizer.Core.Tests/Archive/ProjectArchiveRoundTripTests.cs` (inside the class; add `using EfSchemaVisualizer.Web.Diagram;` and `using Microsoft.CodeAnalysis.CSharp;` and `using Microsoft.CodeAnalysis;` to the top of the file):

```csharp
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
```

(This test uses the `CreateZip(params (string Name, byte[] Content)[])` overload already defined at the top of this file.)

- [ ] **Step 3: Run the test to verify it fails on `main` before this plan's earlier tasks (sanity check only if not already applied)**

If Tasks 1–6 are already applied in this branch, skip this step — the test should already pass. Otherwise:

Run: `dotnet test EfSchemaVisualizer.slnx --filter "FullyQualifiedName~UploadEditDownload_MultiFileProjectWithOwnNamespacesAndConfigFiles"`
Expected (pre-fix): FAIL — `Entities/Customer.cs` (or another split file) has `CS1529`/namespace-collision parse errors.

- [ ] **Step 4: Run the full suite to confirm it passes**

Run: `dotnet test EfSchemaVisualizer.slnx`
Expected: full suite green, including the new end-to-end test.

- [ ] **Step 5: Update `docs/backlog.md` to close F3**

In `docs/backlog.md`, change the F3 entry's checkbox from `- [ ]` to `- [x]` and append a closing note in the same style as F1/F2 (see the existing F1/F2 entries just above it for the exact format), summarizing: `MultiFileSourceMerger` now merges multi-file uploads into one valid compilation unit at read time and splits the edited result back to original files at write time (via `ProjectArchiveReader`/`ProjectArchiveWriter`), `DiagramEditor` now owns and reconciles file origins across renames, and the fix is verified by an end-to-end upload→rename→download test where every downloaded `.cs` file parses with zero Roslyn error diagnostics. Note explicitly, as a documented limitation (not a defect): all of a merged document's `using` directives are re-emitted into every split file (a safe over-approximation — unused usings warn, they don't fail to compile), and a model-level (non-entity-scoped) config statement with no single resolvable entity falls back to the default config file rather than its original one.

- [ ] **Step 6: Commit**

```bash
git add tests/EfSchemaVisualizer.Core.Tests/Archive/ProjectArchiveRoundTripTests.cs tests/EfSchemaVisualizer.Core.Tests/EfSchemaVisualizer.Core.Tests.csproj docs/backlog.md
git commit -m "Close F3: multi-file round trip now produces compilable output end to end"
```
