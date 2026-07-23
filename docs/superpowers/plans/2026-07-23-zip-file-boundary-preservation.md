# Zip file-boundary preservation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the app's `.zip` upload/download round-trip preserve original file boundaries (each entity/config lands back in its original file) and pass through non-`.cs`/unrecognized files byte-for-byte, instead of always flattening to `Entities.cs` + `DbContext.cs`.

**Architecture:** `ClassSource`/`ConfigSource` remain single concatenated strings and no parser/rewriter changes are needed. `ProjectArchiveReader` gains a bookkeeping pass that records, per entity/config name, which zip entry it came from, plus a passthrough byte-dictionary for everything else. `DiagramEditor` carries those two name→filename maps alongside its existing `_entityIds` identity map, updating them at the same rename/add/remove/undo/redo touch points. `ProjectArchiveWriter` gains a new `FileBoundarySplitter` helper that re-parses the current (possibly edited) source and regroups per-entity chunks by tracked filename, falling back to `<EntityName>.cs` / a shared `DbContext.cs` for anything with no tracked origin. Plain paste-flow (no zip ever uploaded) is completely unaffected — empty origin maps route through the exact same two-file code path as today.

**Tech Stack:** C# / .NET, Roslyn (`Microsoft.CodeAnalysis.CSharp`), `System.IO.Compression.ZipArchive`, xUnit.

## Global Constraints

- Every new/changed public API on `ProjectArchiveWriter.Write` must preserve existing positional call sites: `Write(classSource, configSource)` and `Write(classSource, configSource, layout)` must keep compiling unchanged — new parameters are appended **after** `layout`, never inserted before it.
- Every new constructor parameter on `DiagramEditor` must default to `null` so all existing 2-arg `new DiagramEditor(classSource, configSource)` call sites (production and test) keep compiling unchanged.
- No changes to `EntityClassParser`, `FluentConfigParser`, `ModelMerger`, `EntityClassRewriter`, or `OnModelCreatingRewriter` — this feature is bookkeeping + reconstruction around the existing engine, not a change to it.
- Original `using`/`namespace` wrappers are explicitly NOT preserved on write-back — each entity/config class is written back as a bare declaration (matches today's paste-flow shape). Do not attempt to reconstruct them.
- A zip entry name ending in `/` is a directory entry and must never be written to `PassthroughFiles` or any output zip.
- Run `dotnet test EfSchemaVisualizer.slnx` after every task; all tests (including pre-existing ones) must stay green.

---

### Task 1: `ProjectArchiveReader` records file origins and captures passthrough bytes

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/Archive/ProjectArchiveReader.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Archive/ProjectArchiveReaderTests.cs`

**Interfaces:**
- Produces: `ProjectArchiveResult` gains three new fields — `IReadOnlyDictionary<string, string> EntityFileOrigins`, `IReadOnlyDictionary<string, string> ConfigFileOrigins`, `IReadOnlyDictionary<string, byte[]> PassthroughFiles` — consumed by Task 3 (`ProjectArchiveWriter`) and Task 6 (`Home.razor`).

- [ ] **Step 1: Write the failing tests**

Append to `tests/EfSchemaVisualizer.Core.Tests/Archive/ProjectArchiveReaderTests.cs` (inside the `ProjectArchiveReaderTests` class, after the existing tests):

```csharp
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
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test EfSchemaVisualizer.slnx --filter "FullyQualifiedName~ProjectArchiveReaderTests"`
Expected: FAIL to compile — `ProjectArchiveResult` has no `EntityFileOrigins`/`ConfigFileOrigins`/`PassthroughFiles` members yet.

- [ ] **Step 3: Extend `ProjectArchiveResult` and rewrite `Read`**

Replace the full contents of `src/EfSchemaVisualizer.Core/Archive/ProjectArchiveReader.cs` with:

```csharp
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using EfSchemaVisualizer.Core.Parsing;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace EfSchemaVisualizer.Core.Archive;

public sealed record ProjectArchiveResult(
    string ClassSource,
    string ConfigSource,
    IReadOnlyList<Diagnostic> Diagnostics,
    IReadOnlyDictionary<string, EntityPosition> Layout,
    IReadOnlyDictionary<string, string> EntityFileOrigins,
    IReadOnlyDictionary<string, string> ConfigFileOrigins,
    IReadOnlyDictionary<string, byte[]> PassthroughFiles);

public static class ProjectArchiveReader
{
    public static ProjectArchiveResult Read(Stream zipStream)
    {
        using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);

        var classFiles = new List<string>();
        var configFiles = new List<string>();
        var layout = new Dictionary<string, EntityPosition>();
        var entityFileOrigins = new Dictionary<string, string>();
        var entityStyleConfigOrigins = new Dictionary<string, string>();
        var classStyleConfigOrigins = new Dictionary<string, string>();
        var passthroughFiles = new Dictionary<string, byte[]>();

        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.EndsWith('/'))
            {
                continue;
            }

            if (entry.FullName.Equals(ProjectArchiveWriter.LayoutFileName, StringComparison.OrdinalIgnoreCase))
            {
                layout = ReadLayout(entry);
                continue;
            }

            if (!entry.FullName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                passthroughFiles[entry.FullName] = ReadAllBytes(entry);
                continue;
            }

            using var reader = new StreamReader(entry.Open());
            var text = reader.ReadToEnd();

            switch (Classify(text))
            {
                case FileKind.Config:
                    configFiles.Add(text);
                    RecordConfigOrigins(text, entry.FullName, entityStyleConfigOrigins, classStyleConfigOrigins);
                    break;
                case FileKind.Class:
                    classFiles.Add(text);
                    RecordClassOrigins(text, entry.FullName, entityFileOrigins);
                    break;
                case FileKind.Ignored:
                    passthroughFiles[entry.FullName] = Encoding.UTF8.GetBytes(text);
                    break;
            }
        }

        // Entity<T>() style always wins over IEntityTypeConfiguration<T> style when an
        // entity is (unusually) configured both ways, matching the existing rewriter
        // precedent that the Entity<T>() block is authoritative for that entity.
        var configFileOrigins = new Dictionary<string, string>(classStyleConfigOrigins);
        foreach (var (entityName, fileName) in entityStyleConfigOrigins)
        {
            configFileOrigins[entityName] = fileName;
        }

        var classSource = string.Join(Environment.NewLine + Environment.NewLine, classFiles);
        var configSource = string.Join(Environment.NewLine + Environment.NewLine, configFiles);

        var diagnostics = new List<Diagnostic>();
        if (classFiles.Count == 0 && configFiles.Count == 0)
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticCodes.ArchiveNoContentFound,
                "No entity classes or configuration found in the uploaded zip.",
                EntityName: null,
                PropertyName: null,
                new TextSpan(0, 0)));
        }

        return new ProjectArchiveResult(
            classSource, configSource, diagnostics, layout, entityFileOrigins, configFileOrigins, passthroughFiles);
    }

    private static byte[] ReadAllBytes(ZipArchiveEntry entry)
    {
        using var entryStream = entry.Open();
        using var memory = new MemoryStream();
        entryStream.CopyTo(memory);
        return memory.ToArray();
    }

    private static void RecordClassOrigins(string sourceText, string fileName, Dictionary<string, string> entityFileOrigins)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceText);
        var root = tree.GetCompilationUnitRoot();

        var typeDeclarations = root.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .Where(t => t is ClassDeclarationSyntax or RecordDeclarationSyntax or StructDeclarationSyntax)
            .Where(t => !t.Ancestors().OfType<TypeDeclarationSyntax>().Any());

        foreach (var typeDeclaration in typeDeclarations)
        {
            entityFileOrigins[typeDeclaration.Identifier.Text] = fileName;
        }
    }

    private static void RecordConfigOrigins(
        string sourceText,
        string fileName,
        Dictionary<string, string> entityStyleOrigins,
        Dictionary<string, string> classStyleOrigins)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceText);
        var root = tree.GetCompilationUnitRoot();

        foreach (var (entityName, scope) in FluentSyntaxHelpers.FindConfigurationScopes(root))
        {
            if (scope is InvocationExpressionSyntax)
            {
                entityStyleOrigins[entityName] = fileName;
            }
            else
            {
                classStyleOrigins[entityName] = fileName;
            }
        }
    }

    private static Dictionary<string, EntityPosition> ReadLayout(ZipArchiveEntry entry)
    {
        try
        {
            using var reader = new StreamReader(entry.Open());
            var json = reader.ReadToEnd();
            return JsonSerializer.Deserialize<Dictionary<string, EntityPosition>>(json) ?? new Dictionary<string, EntityPosition>();
        }
        catch (JsonException)
        {
            // A malformed layout sidecar only affects where entities are drawn, not the
            // parsed model, so it's dropped rather than surfaced as a data-loss diagnostic.
            return new Dictionary<string, EntityPosition>();
        }
    }

    private enum FileKind
    {
        Ignored,
        Class,
        Config,
    }

    private static FileKind Classify(string sourceText)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceText);
        var root = tree.GetCompilationUnitRoot();

        if (FluentSyntaxHelpers.FindConfigurationScopes(root).Any())
        {
            return FileKind.Config;
        }

        var hasTypeDeclaration = root.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .Any(t => t is ClassDeclarationSyntax or RecordDeclarationSyntax or StructDeclarationSyntax);

        return hasTypeDeclaration ? FileKind.Class : FileKind.Ignored;
    }
}
```

Note: this references `System.Linq` implicitly via LINQ extension methods (`Where`, `Any`, `OfType`) — confirm the project has implicit usings enabled (it already does; the original file used `.Any()`/`.OfType<>()` without an explicit `using System.Linq;`, so no new using is needed).

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test EfSchemaVisualizer.slnx --filter "FullyQualifiedName~ProjectArchiveReaderTests"`
Expected: PASS (all new tests, plus all pre-existing `ProjectArchiveReaderTests` still green).

- [ ] **Step 5: Run the full test suite**

Run: `dotnet test EfSchemaVisualizer.slnx`
Expected: PASS — this change alone should not break `ProjectArchiveWriterTests`/`ProjectArchiveRoundTripTests` since `ProjectArchiveWriter.Write` hasn't changed yet and nothing else constructs `ProjectArchiveResult` directly. If those tests fail to compile because they access `ProjectArchiveResult` positionally, note it and continue — Task 3 will address `ProjectArchiveWriter` in tandem.

- [ ] **Step 6: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Archive/ProjectArchiveReader.cs tests/EfSchemaVisualizer.Core.Tests/Archive/ProjectArchiveReaderTests.cs
git commit -m "$(cat <<'EOF'
Track per-entity file origins and capture passthrough files on zip upload

ProjectArchiveReader now records which zip entry each class/config entity
came from, and captures non-.cs / unclassifiable .cs files as raw
passthrough bytes instead of silently dropping them.
EOF
)"
```

---

### Task 2: `FileBoundarySplitter` — regroup edited source back into per-file chunks

**Files:**
- Create: `src/EfSchemaVisualizer.Core/Archive/FileBoundarySplitter.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Archive/FileBoundarySplitterTests.cs`

**Interfaces:**
- Consumes: `FluentSyntaxHelpers.FindConfigurationScopes(CompilationUnitSyntax root)` (existing, `internal`, same assembly) → `IEnumerable<(string EntityName, SyntaxNode Scope)>`.
- Produces: `FileBoundarySplitter.SplitClassSource(string classSource, IReadOnlyDictionary<string, string> entityFileOrigins) : IReadOnlyDictionary<string, string>` and `FileBoundarySplitter.SplitConfigSource(string configSource, IReadOnlyDictionary<string, string> configFileOrigins, string fallbackFileName) : IReadOnlyDictionary<string, string>`, both consumed by Task 3 (`ProjectArchiveWriter`).

- [ ] **Step 1: Write the failing tests**

Create `tests/EfSchemaVisualizer.Core.Tests/Archive/FileBoundarySplitterTests.cs`:

```csharp
using EfSchemaVisualizer.Core.Archive;
using Xunit;

namespace EfSchemaVisualizer.Core.Tests.Archive;

public class FileBoundarySplitterTests
{
    [Fact]
    public void SplitClassSource_KnownOrigin_GroupsDeclarationUnderTrackedFilename()
    {
        const string classSource = """
            public class Blog { public int Id { get; set; } }

            public class Post { public int Id { get; set; } }
            """;

        var origins = new Dictionary<string, string> { ["Blog"] = "Models/Blog.cs", ["Post"] = "Models/Post.cs" };

        var result = FileBoundarySplitter.SplitClassSource(classSource, origins);

        Assert.Equal(2, result.Count);
        Assert.Contains("class Blog", result["Models/Blog.cs"]);
        Assert.DoesNotContain("class Post", result["Models/Blog.cs"]);
        Assert.Contains("class Post", result["Models/Post.cs"]);
    }

    [Fact]
    public void SplitClassSource_UnknownEntity_FallsBackToFileNamedAfterEntity()
    {
        const string classSource = "public class Order { public int Id { get; set; } }";

        var result = FileBoundarySplitter.SplitClassSource(classSource, new Dictionary<string, string>());

        var entry = Assert.Single(result);
        Assert.Equal("Order.cs", entry.Key);
        Assert.Contains("class Order", entry.Value);
    }

    [Fact]
    public void SplitClassSource_SharedOriginFile_ConcatenatesBothDeclarations()
    {
        const string classSource = """
            public class Blog { public int Id { get; set; } }

            public class Post { public int Id { get; set; } }
            """;

        var origins = new Dictionary<string, string> { ["Blog"] = "Entities.cs", ["Post"] = "Entities.cs" };

        var result = FileBoundarySplitter.SplitClassSource(classSource, origins);

        var entry = Assert.Single(result);
        Assert.Equal("Entities.cs", entry.Key);
        Assert.Contains("class Blog", entry.Value);
        Assert.Contains("class Post", entry.Value);
    }

    [Fact]
    public void SplitConfigSource_SharedOnModelCreatingFile_ConcatenatesBothEntitiesConfig()
    {
        const string configSource = """
            modelBuilder.Entity<Blog>(entity => entity.HasKey(e => e.Id));

            modelBuilder.Entity<Post>(entity => entity.HasKey(e => e.Id));
            """;

        var origins = new Dictionary<string, string> { ["Blog"] = "Data/AppDbContext.cs", ["Post"] = "Data/AppDbContext.cs" };

        var result = FileBoundarySplitter.SplitConfigSource(configSource, origins, "DbContext.cs");

        var entry = Assert.Single(result);
        Assert.Equal("Data/AppDbContext.cs", entry.Key);
        Assert.Contains("Entity<Blog>", entry.Value);
        Assert.Contains("Entity<Post>", entry.Value);
    }

    [Fact]
    public void SplitConfigSource_IEntityTypeConfigurationStyle_WritesWholeClassPerFile()
    {
        const string configSource = """
            public class BlogConfiguration : IEntityTypeConfiguration<Blog>
            {
                public void Configure(EntityTypeBuilder<Blog> builder)
                {
                    builder.HasKey(e => e.Id);
                }
            }
            """;

        var origins = new Dictionary<string, string> { ["Blog"] = "Configurations/BlogConfiguration.cs" };

        var result = FileBoundarySplitter.SplitConfigSource(configSource, origins, "DbContext.cs");

        var entry = Assert.Single(result);
        Assert.Equal("Configurations/BlogConfiguration.cs", entry.Key);
        Assert.Contains("class BlogConfiguration", entry.Value);
        Assert.Contains("IEntityTypeConfiguration<Blog>", entry.Value);
    }

    [Fact]
    public void SplitConfigSource_UnknownEntity_FallsBackToGivenFileName()
    {
        const string configSource = "modelBuilder.Entity<Order>(entity => entity.HasKey(e => e.Id));";

        var result = FileBoundarySplitter.SplitConfigSource(configSource, new Dictionary<string, string>(), "DbContext.cs");

        var entry = Assert.Single(result);
        Assert.Equal("DbContext.cs", entry.Key);
        Assert.Contains("Entity<Order>", entry.Value);
    }

    [Fact]
    public void SplitClassSource_NoDeclarations_ReturnsEmpty()
    {
        var result = FileBoundarySplitter.SplitClassSource("", new Dictionary<string, string>());

        Assert.Empty(result);
    }

    [Fact]
    public void SplitConfigSource_NoConfig_ReturnsEmpty()
    {
        var result = FileBoundarySplitter.SplitConfigSource("", new Dictionary<string, string>(), "DbContext.cs");

        Assert.Empty(result);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test EfSchemaVisualizer.slnx --filter "FullyQualifiedName~FileBoundarySplitterTests"`
Expected: FAIL to compile — `EfSchemaVisualizer.Core.Archive.FileBoundarySplitter` does not exist yet.

- [ ] **Step 3: Implement `FileBoundarySplitter`**

Create `src/EfSchemaVisualizer.Core/Archive/FileBoundarySplitter.cs`:

```csharp
using EfSchemaVisualizer.Core.Parsing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EfSchemaVisualizer.Core.Archive;

internal static class FileBoundarySplitter
{
    public static IReadOnlyDictionary<string, string> SplitClassSource(
        string classSource, IReadOnlyDictionary<string, string> entityFileOrigins)
    {
        var tree = CSharpSyntaxTree.ParseText(classSource);
        var root = tree.GetCompilationUnitRoot();

        var typeDeclarations = root.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .Where(t => t is ClassDeclarationSyntax or RecordDeclarationSyntax or StructDeclarationSyntax)
            .Where(t => !t.Ancestors().OfType<TypeDeclarationSyntax>().Any());

        var buffers = new Dictionary<string, List<string>>();

        foreach (var typeDeclaration in typeDeclarations)
        {
            var name = typeDeclaration.Identifier.Text;
            var fileName = entityFileOrigins.TryGetValue(name, out var origin) ? origin : $"{name}.cs";

            AppendTo(buffers, fileName, typeDeclaration.ToFullString().Trim());
        }

        return Join(buffers);
    }

    public static IReadOnlyDictionary<string, string> SplitConfigSource(
        string configSource, IReadOnlyDictionary<string, string> configFileOrigins, string fallbackFileName)
    {
        var tree = CSharpSyntaxTree.ParseText(configSource);
        var root = tree.GetCompilationUnitRoot();

        var buffers = new Dictionary<string, List<string>>();

        foreach (var (entityName, scope) in FluentSyntaxHelpers.FindConfigurationScopes(root))
        {
            var fileName = configFileOrigins.TryGetValue(entityName, out var origin) ? origin : fallbackFileName;
            AppendTo(buffers, fileName, GetScopeText(scope));
        }

        return Join(buffers);
    }

    private static string GetScopeText(SyntaxNode scope)
    {
        if (scope is InvocationExpressionSyntax invocation)
        {
            // Bare Entity<T>() style: take the enclosing statement so the trailing `;`
            // (and any chained calls beyond the scope invocation itself) is included.
            var statement = invocation.Ancestors().OfType<StatementSyntax>().FirstOrDefault();
            return (statement as SyntaxNode ?? invocation).ToFullString().Trim();
        }

        if (scope is MethodDeclarationSyntax configureMethod)
        {
            // IEntityTypeConfiguration<T> style: take the whole wrapping class.
            var classDeclaration = configureMethod.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault();
            return (classDeclaration as SyntaxNode ?? configureMethod).ToFullString().Trim();
        }

        return scope.ToFullString().Trim();
    }

    private static void AppendTo(Dictionary<string, List<string>> buffers, string fileName, string text)
    {
        if (!buffers.TryGetValue(fileName, out var list))
        {
            list = new List<string>();
            buffers[fileName] = list;
        }

        list.Add(text);
    }

    private static IReadOnlyDictionary<string, string> Join(Dictionary<string, List<string>> buffers)
    {
        return buffers.ToDictionary(kvp => kvp.Key, kvp => string.Join(Environment.NewLine + Environment.NewLine, kvp.Value));
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test EfSchemaVisualizer.slnx --filter "FullyQualifiedName~FileBoundarySplitterTests"`
Expected: PASS.

- [ ] **Step 5: Run the full test suite**

Run: `dotnet test EfSchemaVisualizer.slnx`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Archive/FileBoundarySplitter.cs tests/EfSchemaVisualizer.Core.Tests/Archive/FileBoundarySplitterTests.cs
git commit -m "$(cat <<'EOF'
Add FileBoundarySplitter to regroup edited source by tracked file origin

Pure function over the current ClassSource/ConfigSource text plus the
name-to-filename origin maps: splits top-level class declarations and
per-entity config scopes back into per-file buffers, falling back to a
new file (classes) or a shared fallback file (config) for anything with
no tracked origin.
EOF
)"
```

---

### Task 3: `ProjectArchiveWriter` writes multi-file zips when origins are tracked

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/Archive/ProjectArchiveWriter.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Archive/ProjectArchiveWriterTests.cs`

**Interfaces:**
- Consumes: `FileBoundarySplitter.SplitClassSource`/`SplitConfigSource` (Task 2).
- Produces: `ProjectArchiveWriter.Write(string classSource, string configSource, IReadOnlyDictionary<string, EntityPosition>? layout = null, IReadOnlyDictionary<string, string>? entityFileOrigins = null, IReadOnlyDictionary<string, string>? configFileOrigins = null, IReadOnlyDictionary<string, byte[]>? passthroughFiles = null) : byte[]`, consumed by Task 6 (`Home.razor`).

- [ ] **Step 1: Write the failing tests**

Append to `tests/EfSchemaVisualizer.Core.Tests/Archive/ProjectArchiveWriterTests.cs` (inside the class):

```csharp
    [Fact]
    public void Write_NoOriginsGiven_StillProducesTwoFixedNameEntries()
    {
        var bytes = ProjectArchiveWriter.Write("public class Blog { }", "public class AppDbContext { }", layout: null);

        using var stream = new MemoryStream(bytes);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);

        Assert.Equal(2, zip.Entries.Count);
        Assert.NotNull(zip.GetEntry("Entities.cs"));
        Assert.NotNull(zip.GetEntry("DbContext.cs"));
    }

    [Fact]
    public void Write_WithEntityFileOrigins_WritesEachEntityToItsTrackedFile()
    {
        const string classSource = """
            public class Blog { public int Id { get; set; } }

            public class Post { public int Id { get; set; } }
            """;

        var origins = new Dictionary<string, string> { ["Blog"] = "Models/Blog.cs", ["Post"] = "Models/Post.cs" };

        var bytes = ProjectArchiveWriter.Write(classSource, "", layout: null, entityFileOrigins: origins);

        using var stream = new MemoryStream(bytes);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);

        Assert.NotNull(zip.GetEntry("Models/Blog.cs"));
        Assert.NotNull(zip.GetEntry("Models/Post.cs"));
        Assert.Null(zip.GetEntry("Entities.cs"));
    }

    [Fact]
    public void Write_EntityWithNoTrackedOrigin_FallsBackToFileNamedAfterEntity()
    {
        var origins = new Dictionary<string, string> { ["Blog"] = "Models/Blog.cs" };

        var bytes = ProjectArchiveWriter.Write(
            "public class Blog { } public class Order { }", "", layout: null, entityFileOrigins: origins);

        using var stream = new MemoryStream(bytes);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);

        Assert.NotNull(zip.GetEntry("Models/Blog.cs"));
        Assert.NotNull(zip.GetEntry("Order.cs"));
    }

    [Fact]
    public void Write_WithPassthroughFiles_WritesThemVerbatim()
    {
        var passthrough = new Dictionary<string, byte[]> { ["MyProject.csproj"] = System.Text.Encoding.UTF8.GetBytes("<Project/>") };

        var bytes = ProjectArchiveWriter.Write(
            "public class Blog { }", "", layout: null, passthroughFiles: passthrough);

        using var stream = new MemoryStream(bytes);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);

        var entry = zip.GetEntry("MyProject.csproj");
        Assert.NotNull(entry);
        using var entryStream = entry!.Open();
        using var memory = new MemoryStream();
        entryStream.CopyTo(memory);
        Assert.Equal("<Project/>", System.Text.Encoding.UTF8.GetString(memory.ToArray()));
    }

    [Fact]
    public void Write_ConfigWithNoTrackedOrigins_FallsBackToDbContextCs()
    {
        const string configSource = "modelBuilder.Entity<Blog>(entity => entity.HasKey(e => e.Id));";

        var bytes = ProjectArchiveWriter.Write(
            "public class Blog { }", configSource, layout: null,
            entityFileOrigins: new Dictionary<string, string> { ["Blog"] = "Blog.cs" });

        using var stream = new MemoryStream(bytes);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);

        Assert.NotNull(zip.GetEntry("DbContext.cs"));
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test EfSchemaVisualizer.slnx --filter "FullyQualifiedName~ProjectArchiveWriterTests"`
Expected: FAIL to compile — `Write` has no `entityFileOrigins`/`configFileOrigins`/`passthroughFiles` parameters yet.

- [ ] **Step 3: Extend `ProjectArchiveWriter.Write`**

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
            var hasOriginTracking = entityFileOrigins is { Count: > 0 } || configFileOrigins is { Count: > 0 };

            if (hasOriginTracking)
            {
                var classFiles = FileBoundarySplitter.SplitClassSource(
                    classSource, entityFileOrigins ?? new Dictionary<string, string>());

                var configFallbackFileName = configFileOrigins?.Values.FirstOrDefault() ?? "DbContext.cs";
                var configFiles = FileBoundarySplitter.SplitConfigSource(
                    configSource, configFileOrigins ?? new Dictionary<string, string>(), configFallbackFileName);

                foreach (var (fileName, content) in classFiles)
                {
                    WriteEntry(zip, fileName, content);
                }

                foreach (var (fileName, content) in configFiles)
                {
                    WriteEntry(zip, fileName, content);
                }
            }
            else
            {
                WriteEntry(zip, "Entities.cs", classSource);
                WriteEntry(zip, "DbContext.cs", configSource);
            }

            if (passthroughFiles is { Count: > 0 })
            {
                foreach (var (fileName, bytes) in passthroughFiles)
                {
                    WriteEntryBytes(zip, fileName, bytes);
                }
            }

            if (layout is { Count: > 0 })
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

    private static void WriteEntryBytes(ZipArchive zip, string name, byte[] bytes)
    {
        var entry = zip.CreateEntry(name);
        using var entryStream = entry.Open();
        entryStream.Write(bytes);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test EfSchemaVisualizer.slnx --filter "FullyQualifiedName~ProjectArchiveWriterTests"`
Expected: PASS.

- [ ] **Step 5: Run the full test suite**

Run: `dotnet test EfSchemaVisualizer.slnx`
Expected: PASS — in particular confirm `ProjectArchiveRoundTripTests`' existing `Write(classSource, configSource, layout)`-style positional calls still compile and pass unchanged.

- [ ] **Step 6: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Archive/ProjectArchiveWriter.cs tests/EfSchemaVisualizer.Core.Tests/Archive/ProjectArchiveWriterTests.cs
git commit -m "$(cat <<'EOF'
ProjectArchiveWriter writes multi-file zips when origins are tracked

New optional entityFileOrigins/configFileOrigins/passthroughFiles
parameters, appended after the existing layout parameter to preserve
every existing positional call site. With no origins tracked (the
plain paste-flow case), behavior is byte-for-byte identical to today:
Entities.cs + DbContext.cs.
EOF
)"
```

---

### Task 4: Round-trip tests tying reader + writer together

**Files:**
- Modify: `tests/EfSchemaVisualizer.Core.Tests/Archive/ProjectArchiveRoundTripTests.cs`

**Interfaces:**
- Consumes: `ProjectArchiveReader.Read` (Task 1), `ProjectArchiveWriter.Write` (Task 3). No new production interfaces — this task only adds test coverage proving the two sides compose correctly end-to-end.

- [ ] **Step 1: Write the failing tests**

Append to `tests/EfSchemaVisualizer.Core.Tests/Archive/ProjectArchiveRoundTripTests.cs` (inside the class):

```csharp
    [Fact]
    public void UploadThenDownload_MultipleClassFiles_PreservesOriginalFilenames()
    {
        const string blogFile = "public class Blog { public int Id { get; set; } }";
        const string postFile = "public class Post { public int Id { get; set; } }";

        using var zip = CreateZip(("Models/Blog.cs", blogFile), ("Models/Post.cs", postFile));
        var uploaded = ProjectArchiveReader.Read(zip);

        var bytes = ProjectArchiveWriter.Write(
            uploaded.ClassSource, uploaded.ConfigSource, layout: null,
            uploaded.EntityFileOrigins, uploaded.ConfigFileOrigins);

        using var downloadStream = new MemoryStream(bytes);
        using var downloadedZip = new System.IO.Compression.ZipArchive(downloadStream, System.IO.Compression.ZipArchiveMode.Read);

        Assert.NotNull(downloadedZip.GetEntry("Models/Blog.cs"));
        Assert.NotNull(downloadedZip.GetEntry("Models/Post.cs"));
    }

    [Fact]
    public void UploadThenDownload_IEntityTypeConfigurationPerFile_PreservesSeparateFiles()
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
        var uploaded = ProjectArchiveReader.Read(zip);

        var bytes = ProjectArchiveWriter.Write(
            uploaded.ClassSource, uploaded.ConfigSource, layout: null,
            uploaded.EntityFileOrigins, uploaded.ConfigFileOrigins);

        using var downloadStream = new MemoryStream(bytes);
        using var downloadedZip = new System.IO.Compression.ZipArchive(downloadStream, System.IO.Compression.ZipArchiveMode.Read);

        Assert.NotNull(downloadedZip.GetEntry("Blog.cs"));
        Assert.NotNull(downloadedZip.GetEntry("Configurations/BlogConfiguration.cs"));
    }

    [Fact]
    public void UploadThenDownload_NonCsAndUnclassifiableFiles_PassThroughByteIdentical()
    {
        const string classFile = "public class Blog { public int Id { get; set; } }";
        const string enumFile = "public enum Status { Active, Inactive }";

        using var zip = CreateZip(("Blog.cs", classFile), ("Status.cs", enumFile));
        var uploaded = ProjectArchiveReader.Read(zip);

        var bytes = ProjectArchiveWriter.Write(
            uploaded.ClassSource, uploaded.ConfigSource, layout: null,
            uploaded.EntityFileOrigins, uploaded.ConfigFileOrigins, uploaded.PassthroughFiles);

        using var downloadStream = new MemoryStream(bytes);
        using var downloadedZip = new System.IO.Compression.ZipArchive(downloadStream, System.IO.Compression.ZipArchiveMode.Read);

        var statusEntry = downloadedZip.GetEntry("Status.cs");
        Assert.NotNull(statusEntry);
        using var reader = new StreamReader(statusEntry!.Open());
        Assert.Equal(enumFile, reader.ReadToEnd());
    }

    [Fact]
    public void UploadThenDownload_EntityRemovedFromItsOnlyFile_OmitsThatFileFromOutput()
    {
        const string blogFile = "public class Blog { public int Id { get; set; } }";
        const string postFile = "public class Post { public int Id { get; set; } }";

        using var zip = CreateZip(("Models/Blog.cs", blogFile), ("Models/Post.cs", postFile));
        var uploaded = ProjectArchiveReader.Read(zip);

        // Simulate removing Post from ClassSource, as DiagramEditor.RemoveEntity would.
        var classSourceWithoutPost = uploaded.ClassSource.Replace(postFile, "").Trim();

        var bytes = ProjectArchiveWriter.Write(
            classSourceWithoutPost, uploaded.ConfigSource, layout: null,
            uploaded.EntityFileOrigins, uploaded.ConfigFileOrigins);

        using var downloadStream = new MemoryStream(bytes);
        using var downloadedZip = new System.IO.Compression.ZipArchive(downloadStream, System.IO.Compression.ZipArchiveMode.Read);

        Assert.NotNull(downloadedZip.GetEntry("Models/Blog.cs"));
        Assert.Null(downloadedZip.GetEntry("Models/Post.cs"));
    }

    [Fact]
    public void UploadThenDownload_PlainPasteFlow_StillProducesClassicTwoFileZip()
    {
        const string classSource = "public class Blog { public int Id { get; set; } }";
        const string configSource = "modelBuilder.Entity<Blog>(entity => entity.HasKey(e => e.Id));";

        // No zip was ever uploaded, so no origin maps exist — mirrors DiagramEditor's
        // default two-arg constructor path.
        var bytes = ProjectArchiveWriter.Write(classSource, configSource);

        using var downloadStream = new MemoryStream(bytes);
        using var downloadedZip = new System.IO.Compression.ZipArchive(downloadStream, System.IO.Compression.ZipArchiveMode.Read);

        Assert.Equal(2, downloadedZip.Entries.Count);
        Assert.NotNull(downloadedZip.GetEntry("Entities.cs"));
        Assert.NotNull(downloadedZip.GetEntry("DbContext.cs"));
    }
```

This requires a shared `CreateZip` test helper in this file — check whether one already exists; if not, add it near the top of the class (mirroring the one in `ProjectArchiveReaderTests.cs`):

```csharp
    private static MemoryStream CreateZip(params (string Name, string Content)[] files)
    {
        var stream = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
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
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test EfSchemaVisualizer.slnx --filter "FullyQualifiedName~ProjectArchiveRoundTripTests"`
Expected: FAIL if `CreateZip` doesn't already exist in this file (compile error), or FAIL on assertions if it does. Add `CreateZip` if missing, per Step 1.

- [ ] **Step 3: Confirm no production code changes are needed**

This task only adds tests over the code from Tasks 1–3. If any test fails for a reason other than a missing `CreateZip` helper, that indicates a real bug in Task 1/2/3's implementation — fix the relevant file there (not here) and re-run.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test EfSchemaVisualizer.slnx --filter "FullyQualifiedName~ProjectArchiveRoundTripTests"`
Expected: PASS.

- [ ] **Step 5: Run the full test suite**

Run: `dotnet test EfSchemaVisualizer.slnx`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add tests/EfSchemaVisualizer.Core.Tests/Archive/ProjectArchiveRoundTripTests.cs
git commit -m "$(cat <<'EOF'
Add end-to-end round-trip tests for zip file-boundary preservation

Covers multi-file class upload, IEntityTypeConfiguration-per-file
config, non-.cs/unclassifiable passthrough, entity removal omitting an
emptied file, and confirms the plain paste-flow (no origins tracked)
is untouched.
EOF
)"
```

---

### Task 5: `DiagramEditor` tracks and maintains file origins

**Files:**
- Modify: `src/EfSchemaVisualizer.Web/Diagram/DiagramEditor.cs`
- Test: `tests/EfSchemaVisualizer.Web.Tests/Diagram/DiagramEditorTests.cs`

**Interfaces:**
- Consumes: nothing new from Core — this task only threads two new `Dictionary<string, string>` maps through existing `DiagramEditor` logic.
- Produces: `DiagramEditor(string classSource, string configSource, IReadOnlyDictionary<string, string>? entityFileOrigins = null, IReadOnlyDictionary<string, string>? configFileOrigins = null)` constructor, plus `IReadOnlyDictionary<string, string> EntityFileOrigins` / `ConfigFileOrigins` properties, consumed by Task 6 (`Home.razor`).

- [ ] **Step 1: Write the failing tests**

Append to `tests/EfSchemaVisualizer.Web.Tests/Diagram/DiagramEditorTests.cs` (inside the class):

```csharp
    [Fact]
    public void Constructor_WithEntityFileOrigins_ExposesThemViaProperty()
    {
        var origins = new Dictionary<string, string> { ["Blog"] = "Models/Blog.cs" };

        var editor = new DiagramEditor(ClassSource, ConfigSource, origins, null);

        Assert.Equal("Models/Blog.cs", editor.EntityFileOrigins["Blog"]);
    }

    [Fact]
    public void Constructor_NoOriginsGiven_ExposesEmptyMaps()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);

        Assert.Empty(editor.EntityFileOrigins);
        Assert.Empty(editor.ConfigFileOrigins);
    }

    [Fact]
    public void RenameEntity_WithTrackedOrigin_MovesOriginToNewName()
    {
        var entityOrigins = new Dictionary<string, string> { ["Blog"] = "Models/Blog.cs" };
        var configOrigins = new Dictionary<string, string> { ["Blog"] = "Data/AppDbContext.cs" };
        var editor = new DiagramEditor(ClassSource, ConfigSource, entityOrigins, configOrigins);

        var result = editor.RenameEntity("Blog", "Article");

        Assert.True(result.Success);
        Assert.False(editor.EntityFileOrigins.ContainsKey("Blog"));
        Assert.Equal("Models/Blog.cs", editor.EntityFileOrigins["Article"]);
        Assert.Equal("Data/AppDbContext.cs", editor.ConfigFileOrigins["Article"]);
    }

    [Fact]
    public void RemoveEntity_WithTrackedOrigin_DropsItFromBothMaps()
    {
        var entityOrigins = new Dictionary<string, string> { ["Blog"] = "Models/Blog.cs" };
        var configOrigins = new Dictionary<string, string> { ["Blog"] = "Data/AppDbContext.cs" };
        const string classSourceTwoEntities = """
            public class Blog { public int Id { get; set; } }
            public class Post { public int Id { get; set; } }
            """;
        var editor = new DiagramEditor(classSourceTwoEntities, "", entityOrigins, configOrigins);

        var result = editor.RemoveEntity("Blog");

        Assert.True(result.Success);
        Assert.False(editor.EntityFileOrigins.ContainsKey("Blog"));
        Assert.False(editor.ConfigFileOrigins.ContainsKey("Blog"));
    }

    [Fact]
    public void AddEntity_NoTrackedOrigin_LeavesOriginMapsUnset()
    {
        var entityOrigins = new Dictionary<string, string> { ["Blog"] = "Models/Blog.cs" };
        var editor = new DiagramEditor(ClassSource, ConfigSource, entityOrigins, null);

        editor.AddEntity();

        var newEntityName = editor.Current.Entities.Single(e => e.Name != "Blog").Name;
        Assert.False(editor.EntityFileOrigins.ContainsKey(newEntityName));
    }

    [Fact]
    public void Undo_AfterRenameWithTrackedOrigin_RestoresOriginMapping()
    {
        var entityOrigins = new Dictionary<string, string> { ["Blog"] = "Models/Blog.cs" };
        var editor = new DiagramEditor(ClassSource, ConfigSource, entityOrigins, null);

        editor.RenameEntity("Blog", "Article");
        var undoResult = editor.Undo();

        Assert.True(undoResult.Success);
        Assert.Equal("Models/Blog.cs", editor.EntityFileOrigins["Blog"]);
        Assert.False(editor.EntityFileOrigins.ContainsKey("Article"));
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test EfSchemaVisualizer.slnx --filter "FullyQualifiedName~DiagramEditorTests"`
Expected: FAIL to compile — `DiagramEditor` has no 4-arg constructor or `EntityFileOrigins`/`ConfigFileOrigins` properties yet.

- [ ] **Step 3: Add origin-map fields, constructor parameters, and properties**

In `src/EfSchemaVisualizer.Web/Diagram/DiagramEditor.cs`, change:

```csharp
    private readonly EntityClassRewriter _classRewriter = new();
    private readonly OnModelCreatingRewriter _configRewriter = new();
    private readonly Dictionary<string, Guid> _entityIds = new();
    private readonly Stack<Snapshot> _undoStack = new();
    private readonly Stack<Snapshot> _redoStack = new();

    private sealed record Snapshot(string ClassSource, string ConfigSource, Dictionary<string, Guid> EntityIds);

    public DiagramEditor(string classSource, string configSource)
    {
        ClassSource = classSource;
        ConfigSource = configSource;
        Current = DiagramModelBuilder.Build(classSource, configSource);

        foreach (var entity in Current.Entities)
        {
            _entityIds[entity.Name] = Guid.NewGuid();
        }
    }

    public string ClassSource { get; private set; }
    public string ConfigSource { get; private set; }
    public DiagramModelResult Current { get; private set; }
    public IReadOnlyDictionary<string, Guid> EntityIds => _entityIds;
```

to:

```csharp
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

- [ ] **Step 4: Update `RenameEntity` to move origin-map entries**

In `RenameEntity`, change:

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

to:

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

        if (_entityFileOrigins.Remove(oldName, out var entityOrigin))
        {
            _entityFileOrigins[newName] = entityOrigin;
        }

        if (_configFileOrigins.Remove(oldName, out var configOrigin))
        {
            _configFileOrigins[newName] = configOrigin;
        }

        Apply(newClassSource, newConfigSource);
```

- [ ] **Step 5: Update `RemoveEntity` to drop origin-map entries**

Change:

```csharp
        var newClassSource = _classRewriter.RemoveClass(ClassSource, entityName);
        var newConfigSource = _configRewriter.RemoveEntity(ConfigSource, entityName);
        Apply(newClassSource, newConfigSource);
        _entityIds.Remove(entityName);
        return DiagramEditResult.Ok();
```

to:

```csharp
        var newClassSource = _classRewriter.RemoveClass(ClassSource, entityName);
        var newConfigSource = _configRewriter.RemoveEntity(ConfigSource, entityName);
        Apply(newClassSource, newConfigSource);
        _entityIds.Remove(entityName);
        _entityFileOrigins.Remove(entityName);
        _configFileOrigins.Remove(entityName);
        return DiagramEditResult.Ok();
```

- [ ] **Step 6: Update snapshot capture/restore and `Apply`'s reconciliation**

Change:

```csharp
    private Snapshot CurrentSnapshot() => new(ClassSource, ConfigSource, new Dictionary<string, Guid>(_entityIds));

    private void Restore(Snapshot snapshot)
    {
        ClassSource = snapshot.ClassSource;
        ConfigSource = snapshot.ConfigSource;
        _entityIds.Clear();
        foreach (var (name, id) in snapshot.EntityIds)
        {
            _entityIds[name] = id;
        }

        Current = DiagramModelBuilder.Build(ClassSource, ConfigSource);
    }
```

to:

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
        foreach (var (name, fileName) in snapshot.EntityFileOrigins)
        {
            _entityFileOrigins[name] = fileName;
        }

        _configFileOrigins.Clear();
        foreach (var (name, fileName) in snapshot.ConfigFileOrigins)
        {
            _configFileOrigins[name] = fileName;
        }

        Current = DiagramModelBuilder.Build(ClassSource, ConfigSource);
    }
```

Then, in `Apply`, change the existing reconciliation block:

```csharp
        // Hand-edited source (via SyncSource) can introduce or delete classes without
        // going through AddEntity/RemoveEntity, so _entityIds would otherwise drift out
        // of sync with Current.Entities. Reconcile here, the one place every mutation
        // (rename, add, remove, type change, hand-edit reparse) funnels through.
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
    }
```

to:

```csharp
        // Hand-edited source (via SyncSource) can introduce or delete classes without
        // going through AddEntity/RemoveEntity, so _entityIds would otherwise drift out
        // of sync with Current.Entities. Reconcile here, the one place every mutation
        // (rename, add, remove, type change, hand-edit reparse) funnels through.
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

        // Unlike _entityIds, a missing origin is a meaningful "no tracked file" state
        // (new entities fall back to a new file at download time), so only stale
        // entries are pruned here — never invented for names with no tracked origin.
        foreach (var staleName in _entityFileOrigins.Keys.Where(name => !currentNames.Contains(name)).ToList())
        {
            _entityFileOrigins.Remove(staleName);
        }

        foreach (var staleName in _configFileOrigins.Keys.Where(name => !currentNames.Contains(name)).ToList())
        {
            _configFileOrigins.Remove(staleName);
        }
    }
```

- [ ] **Step 7: Run tests to verify they pass**

Run: `dotnet test EfSchemaVisualizer.slnx --filter "FullyQualifiedName~DiagramEditorTests"`
Expected: PASS.

- [ ] **Step 8: Run the full test suite**

Run: `dotnet test EfSchemaVisualizer.slnx`
Expected: PASS — confirms every other `DiagramEditor*Tests` file (which all use the 2-arg constructor) still compiles and passes unchanged.

- [ ] **Step 9: Commit**

```bash
git add src/EfSchemaVisualizer.Web/Diagram/DiagramEditor.cs tests/EfSchemaVisualizer.Web.Tests/Diagram/DiagramEditorTests.cs
git commit -m "$(cat <<'EOF'
DiagramEditor tracks per-entity file origins alongside entity identity

New optional constructor parameters carry entity-name-to-filename maps
for classes and config, kept in sync at the same rename/add/remove/
undo/redo touch points _entityIds already is. A missing origin is left
unset (not invented) so new entities correctly fall back to a new file
at download time.
EOF
)"
```

---

### Task 6: Wire `Home.razor` to pass origins and passthrough files through upload/download

**Files:**
- Modify: `src/EfSchemaVisualizer.Web/Pages/Home.razor`

**Interfaces:**
- Consumes: `ProjectArchiveResult.EntityFileOrigins`/`ConfigFileOrigins`/`PassthroughFiles` (Task 1), `DiagramEditor`'s new constructor parameters and `EntityFileOrigins`/`ConfigFileOrigins` properties (Task 5), `ProjectArchiveWriter.Write`'s new parameters (Task 3).
- Produces: no new public interface — this is the final wiring task, not consumed by anything else in this plan.

- [ ] **Step 1: Add tracking fields**

In the `@code` block of `src/EfSchemaVisualizer.Web/Pages/Home.razor`, near the existing `_editor`/`_diagnostics` field declarations, add:

```csharp
    private Dictionary<string, string>? _entityFileOrigins;
    private Dictionary<string, string>? _configFileOrigins;
    private Dictionary<string, byte[]> _passthroughFiles = new();
```

- [ ] **Step 2: Populate the new fields in `OnZipSelected`**

Change:

```csharp
            var archiveResult = ProjectArchiveReader.Read(memory);
            _classSource = archiveResult.ClassSource;
            _configSource = archiveResult.ConfigSource;

            await RenderDiagramAsync();
```

to:

```csharp
            var archiveResult = ProjectArchiveReader.Read(memory);
            _classSource = archiveResult.ClassSource;
            _configSource = archiveResult.ConfigSource;
            _entityFileOrigins = new Dictionary<string, string>(archiveResult.EntityFileOrigins);
            _configFileOrigins = new Dictionary<string, string>(archiveResult.ConfigFileOrigins);
            _passthroughFiles = new Dictionary<string, byte[]>(archiveResult.PassthroughFiles);

            await RenderDiagramAsync();
```

- [ ] **Step 3: Pass the origin maps into the `DiagramEditor` constructor**

In `RenderDiagramAsync`, change:

```csharp
            _editor = new DiagramEditor(_classSource, _configSource);
```

to:

```csharp
            _editor = new DiagramEditor(_classSource, _configSource, _entityFileOrigins, _configFileOrigins);
```

Since `_entityFileOrigins`/`_configFileOrigins` default to `null` on a fresh page load (pure paste-flow, no zip ever uploaded), this passes `null, null` in that case — identical to calling the 2-arg constructor.

- [ ] **Step 4: Pass origins and passthrough files into `DownloadZip`**

Change:

```csharp
        var layout = _diagram is not null ? DiagramLayout.Capture(_diagram) : null;
        var bytes = ProjectArchiveWriter.Write(_editor.ClassSource, _editor.ConfigSource, layout);
```

to:

```csharp
        var layout = _diagram is not null ? DiagramLayout.Capture(_diagram) : null;
        var bytes = ProjectArchiveWriter.Write(
            _editor.ClassSource, _editor.ConfigSource, layout,
            _editor.EntityFileOrigins, _editor.ConfigFileOrigins, _passthroughFiles);
```

- [ ] **Step 5: Build the Web project**

Run: `dotnet build src/EfSchemaVisualizer.Web/EfSchemaVisualizer.Web.csproj`
Expected: Build succeeds with no new warnings (the repo has `TreatWarningsAsErrors` on, so any typo here fails the build immediately).

- [ ] **Step 6: Run the full test suite**

Run: `dotnet test EfSchemaVisualizer.slnx`
Expected: PASS — all three test projects (Core, Web, and the self-skipping smoke test) stay green.

- [ ] **Step 7: Manual verification via dev server**

Run: `dotnet run --project src/EfSchemaVisualizer.Web/EfSchemaVisualizer.Web.csproj` and, per the existing project precedent (no browser/Playwright-installable environment in this sandbox), confirm via the terminal output that the app starts and serves without error. If a browser is available in your environment, additionally:
1. Build a small test zip with two class files (e.g. `Models/Blog.cs`, `Models/Post.cs`) and a `.csproj` file, upload it via the file picker.
2. Click "Download .zip" without making any diagram edits, and confirm the downloaded zip contains `Models/Blog.cs`, `Models/Post.cs`, and the `.csproj` file, not `Entities.cs`/`DbContext.cs`.
3. Rename an entity via double-click, download again, and confirm the rename landed in the correct original file.
4. Remove an entity that was the only thing in its file, download again, and confirm that file is no longer present in the zip.

If no browser is available, note that explicitly rather than claiming interactive verification was done — matching every prior UI-feature entry in `docs/backlog.md`.

- [ ] **Step 8: Update the backlog entry**

In `docs/backlog.md`, change the line:

```
- [ ] **`[spec]` Zip round-trip loses file boundaries.** Documented trade-off
```

to `- [x]` and add an **Update:** paragraph (matching the style of every other closed item in that file) summarizing what was built: per-entity file-origin tracking in `ProjectArchiveReader`/`DiagramEditor`, the new `FileBoundarySplitter`, passthrough support for non-`.cs`/unclassifiable files, and a pointer to `docs/superpowers/specs/2026-07-23-zip-file-boundary-preservation-design.md`.

- [ ] **Step 9: Commit**

```bash
git add src/EfSchemaVisualizer.Web/Pages/Home.razor docs/backlog.md
git commit -m "$(cat <<'EOF'
Wire zip upload/download to preserve file boundaries in the app

OnZipSelected now captures per-entity file origins and passthrough
files from ProjectArchiveReader; RenderDiagramAsync threads them into
DiagramEditor; DownloadZip passes the editor's (possibly
rename/add/remove-updated) origin maps and the passthrough files back
into ProjectArchiveWriter. Plain paste-flow behavior is unchanged.

Closes the last open item in docs/backlog.md.
EOF
)"
```

---

## Self-Review Notes

- **Spec coverage:** Task 1 covers the spec's "Upload: building the origin maps" section (including the dual-style precedence rule and directory-entry exclusion). Task 2 covers "Download: splitting back into files" for both classes and config. Task 3 covers the `ProjectArchiveWriter` signature and backward-compatible parameter ordering. Task 4 covers the spec's end-to-end testing list. Task 5 covers "Wiring through DiagramEditor" including the undo/redo snapshot requirement. Task 6 covers "Wiring through Home.razor" and the backlog update. The "no using/namespace fidelity" trade-off is enforced by construction (`FileBoundarySplitter` only ever emits `typeDeclaration.ToFullString()`/scope text, never a namespace wrapper) rather than needing its own task.
- **Placeholder scan:** no TBD/TODO markers; every step has complete, runnable code.
- **Type consistency:** `ProjectArchiveResult`'s 7-arg constructor (Task 1) matches every call site added in Tasks 1/3/4/6. `FileBoundarySplitter.SplitClassSource`/`SplitConfigSource` signatures (Task 2) match their exact usage in `ProjectArchiveWriter.Write` (Task 3). `DiagramEditor`'s 4-arg constructor and `EntityFileOrigins`/`ConfigFileOrigins` properties (Task 5) match their usage in `Home.razor` (Task 6).
