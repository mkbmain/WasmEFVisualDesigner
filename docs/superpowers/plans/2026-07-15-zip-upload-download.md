# .zip Upload/Download Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the user upload a `.zip` of `.cs` files (bucketed into entity-class vs config text) into the two existing textareas, and download the current diagram's edited source back out as a `.zip`.

**Architecture:** Two new pure-C# static classes in `EfSchemaVisualizer.Core/Archive/` (`ProjectArchiveReader`, `ProjectArchiveWriter`) do the zip classification/concatenation and zip building, fully unit-tested with xUnit. `Home.razor` in the Web project wires an `<InputFile>` to the reader and a "Download .zip" button to the writer plus a small JS interop helper for triggering the browser download.

**Tech Stack:** `System.IO.Compression.ZipArchive` (BCL, no new package), Roslyn (`Microsoft.CodeAnalysis.CSharp`, already referenced by Core), Blazor `InputFile` + `IJSRuntime`/`DotNetStreamReference` (already available via existing `_Imports.razor`).

## Global Constraints

- Target framework for all touched projects is `net10.0`; do not introduce new package references — everything needed (`System.IO.Compression`, Roslyn, Blazor Forms/JSInterop) is already available.
- Follow the existing `DiagnosticCodes` constants-class pattern for any new diagnostic code — no bare string literals (per `docs/backlog.md` Priority 2 "Diagnostic codes are bare string literals" fix already in place).
- Non-`.cs` files and unclassifiable `.cs` files (no `OnModelCreating` method, no `IEntityTypeConfiguration` base, no class/record/struct declaration) are silently ignored on upload — never added to either bucket, never surfaced as a per-file diagnostic.
- Downloaded zip always contains exactly two entries, `Entities.cs` and `DbContext.cs`, even if one blob is empty — no conditional omission.
- True per-file round-tripping is explicitly out of scope (see `docs/superpowers/specs/2026-07-15-zip-upload-download-design.md` Scope section) — do not build file-boundary tracking.

---

### Task 1: `ProjectArchiveReader` — zip classification and concatenation

**Files:**
- Create: `src/EfSchemaVisualizer.Core/Archive/ProjectArchiveReader.cs`
- Modify: `src/EfSchemaVisualizer.Core/Parsing/DiagnosticCodes.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Archive/ProjectArchiveReaderTests.cs`

**Interfaces:**
- Consumes: `EfSchemaVisualizer.Core.Parsing.Diagnostic` (record: `Code, Message, EntityName, PropertyName, Span` — `Span` is `Microsoft.CodeAnalysis.Text.TextSpan`), `EfSchemaVisualizer.Core.Parsing.DiagnosticCodes` (constants class).
- Produces: `EfSchemaVisualizer.Core.Archive.ProjectArchiveResult` record (`string ClassSource, string ConfigSource, IReadOnlyList<Diagnostic> Diagnostics`) and `EfSchemaVisualizer.Core.Archive.ProjectArchiveReader.Read(Stream zipStream) -> ProjectArchiveResult` (static method) — both consumed by Task 3 (Web wiring).

- [ ] **Step 1: Add the new diagnostic code**

Open `src/EfSchemaVisualizer.Core/Parsing/DiagnosticCodes.cs` and add one line inside the class (anywhere among the existing constants, e.g. at the end before the closing brace):

```csharp
    public const string ArchiveNoContentFound = nameof(ArchiveNoContentFound);
```

- [ ] **Step 2: Write the failing tests**

Create `tests/EfSchemaVisualizer.Core.Tests/Archive/ProjectArchiveReaderTests.cs`:

```csharp
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
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter "FullyQualifiedName~ProjectArchiveReaderTests"`
Expected: build FAILS (`ProjectArchiveReader`/`ProjectArchiveResult` don't exist yet).

- [ ] **Step 4: Implement `ProjectArchiveReader`**

Create `src/EfSchemaVisualizer.Core/Archive/ProjectArchiveReader.cs`:

```csharp
using System.IO.Compression;
using EfSchemaVisualizer.Core.Parsing;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace EfSchemaVisualizer.Core.Archive;

public sealed record ProjectArchiveResult(
    string ClassSource,
    string ConfigSource,
    IReadOnlyList<Diagnostic> Diagnostics);

public static class ProjectArchiveReader
{
    public static ProjectArchiveResult Read(Stream zipStream)
    {
        using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);

        var classFiles = new List<string>();
        var configFiles = new List<string>();

        foreach (var entry in zip.Entries)
        {
            if (!entry.FullName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            using var reader = new StreamReader(entry.Open());
            var text = reader.ReadToEnd();

            switch (Classify(text))
            {
                case FileKind.Config:
                    configFiles.Add(text);
                    break;
                case FileKind.Class:
                    classFiles.Add(text);
                    break;
                case FileKind.Ignored:
                    break;
            }
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

        return new ProjectArchiveResult(classSource, configSource, diagnostics);
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

        var hasOnModelCreating = root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Any(m => m.Identifier.Text == "OnModelCreating");

        var hasEntityTypeConfigurationBase = root.DescendantNodes()
            .OfType<BaseListSyntax>()
            .Any(baseList => baseList.Types.Any(t => t.Type.ToString().Contains("IEntityTypeConfiguration")));

        if (hasOnModelCreating || hasEntityTypeConfigurationBase)
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

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter "FullyQualifiedName~ProjectArchiveReaderTests"`
Expected: PASS (7 tests).

- [ ] **Step 6: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Archive/ProjectArchiveReader.cs src/EfSchemaVisualizer.Core/Parsing/DiagnosticCodes.cs tests/EfSchemaVisualizer.Core.Tests/Archive/ProjectArchiveReaderTests.cs
git commit -m "Add ProjectArchiveReader: classify and bucket zip .cs files into class/config source"
```

---

### Task 2: `ProjectArchiveWriter` — build a zip from the two source blobs

**Files:**
- Create: `src/EfSchemaVisualizer.Core/Archive/ProjectArchiveWriter.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Archive/ProjectArchiveWriterTests.cs`

**Interfaces:**
- Consumes: nothing from Task 1 (independent).
- Produces: `EfSchemaVisualizer.Core.Archive.ProjectArchiveWriter.Write(string classSource, string configSource) -> byte[]` — consumed by Task 3 (Web wiring).

- [ ] **Step 1: Write the failing tests**

Create `tests/EfSchemaVisualizer.Core.Tests/Archive/ProjectArchiveWriterTests.cs`:

```csharp
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
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter "FullyQualifiedName~ProjectArchiveWriterTests"`
Expected: build FAILS (`ProjectArchiveWriter` doesn't exist yet).

- [ ] **Step 3: Implement `ProjectArchiveWriter`**

Create `src/EfSchemaVisualizer.Core/Archive/ProjectArchiveWriter.cs`:

```csharp
using System.IO.Compression;

namespace EfSchemaVisualizer.Core.Archive;

public static class ProjectArchiveWriter
{
    public static byte[] Write(string classSource, string configSource)
    {
        using var stream = new MemoryStream();

        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(zip, "Entities.cs", classSource);
            WriteEntry(zip, "DbContext.cs", configSource);
        }

        return stream.ToArray();
    }

    private static void WriteEntry(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter "FullyQualifiedName~ProjectArchiveWriterTests"`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Archive/ProjectArchiveWriter.cs tests/EfSchemaVisualizer.Core.Tests/Archive/ProjectArchiveWriterTests.cs
git commit -m "Add ProjectArchiveWriter: build a two-entry zip from class/config source"
```

---

### Task 3: Wire upload/download into `Home.razor`

**Files:**
- Create: `src/EfSchemaVisualizer.Web/wwwroot/js/downloadFile.js`
- Modify: `src/EfSchemaVisualizer.Web/wwwroot/index.html`
- Modify: `src/EfSchemaVisualizer.Web/Pages/Home.razor`

**Interfaces:**
- Consumes: `EfSchemaVisualizer.Core.Archive.ProjectArchiveReader.Read(Stream) -> ProjectArchiveResult` and `ProjectArchiveWriter.Write(string, string) -> byte[]` from Tasks 1–2; `Diagnostic` from `EfSchemaVisualizer.Core.Parsing` (already imported in `Home.razor`); existing `_classSource`, `_configSource`, `_diagnostics`, `_error`, `_editor`, `RenderDiagram()` fields/method already in `Home.razor`.
- Produces: nothing consumed by later tasks — this is the last task.

This task has no automated test (browser-only UI wiring, consistent with the rest of the app's UI layer — see the design doc's Testing section and the pre-existing "no in-browser verification possible in this sandbox" gap recorded in `docs/backlog.md`). Steps are implement-and-build-verify instead of TDD.

- [ ] **Step 1: Add the JS download helper**

Create `src/EfSchemaVisualizer.Web/wwwroot/js/downloadFile.js`:

```javascript
async function downloadFileFromStream(fileName, contentStreamReference) {
    const arrayBuffer = await contentStreamReference.arrayBuffer();
    const blob = new Blob([arrayBuffer]);
    const url = URL.createObjectURL(blob);
    const anchor = document.createElement('a');
    anchor.href = url;
    anchor.download = fileName ?? '';
    anchor.click();
    anchor.remove();
    URL.revokeObjectURL(url);
}
```

- [ ] **Step 2: Reference the script from `index.html`**

In `src/EfSchemaVisualizer.Web/wwwroot/index.html`, add a `<script>` tag right before the closing `</body>` tag's existing `_framework/blazor.webassembly` script line (i.e. immediately after the `Z.Blazor.Diagrams/script.min.js` line and before the `blazor.webassembly` line):

```html
    <script src="js/downloadFile.js"></script>
```

So that section of the file reads:

```html
    <script src="_content/Z.Blazor.Diagrams/script.min.js"></script>
    <script src="js/downloadFile.js"></script>
    <script src="_framework/blazor.webassembly#[.{fingerprint}].js"></script>
```

- [ ] **Step 3: Add `@using` for the Archive namespace**

In `src/EfSchemaVisualizer.Web/Pages/Home.razor`, add a new `@using` line alongside the existing ones at the top of the file (after `@using EfSchemaVisualizer.Core.Model`):

```razor
@using EfSchemaVisualizer.Core.Archive
```

- [ ] **Step 4: Inject `IJSRuntime`**

In `src/EfSchemaVisualizer.Web/Pages/Home.razor`, add this directive right after the `@page "/"` line:

```razor
@inject IJSRuntime JS
```

(`Microsoft.JSInterop` is already globally imported via `_Imports.razor`, so `IJSRuntime` resolves without a new `@using`.)

- [ ] **Step 5: Add the upload input and download button to the markup**

In `src/EfSchemaVisualizer.Web/Pages/Home.razor`, replace this block:

```razor
<p>
    <button class="btn btn-primary" @onclick="RenderDiagram">Render Diagram</button>
    @if (_editContext is not null)
    {
        <button class="btn btn-secondary" @onclick="AddEntity">+ Entity</button>
    }
</p>
```

with:

```razor
<p>
    <button class="btn btn-primary" @onclick="RenderDiagram">Render Diagram</button>
    @if (_editContext is not null)
    {
        <button class="btn btn-secondary" @onclick="AddEntity">+ Entity</button>
    }
    <InputFile OnChange="OnZipSelected" accept=".zip" />
    <button class="btn btn-secondary" disabled="@(_editor is null)" @onclick="DownloadZip">Download .zip</button>
</p>
```

- [ ] **Step 6: Add the `OnZipSelected` and `DownloadZip` handlers**

In `src/EfSchemaVisualizer.Web/Pages/Home.razor`, add these two methods inside the `@code { }` block, right after the existing `SyncEditorSource` method:

```csharp
    private async Task OnZipSelected(InputFileChangeEventArgs e)
    {
        try
        {
            using var memory = new MemoryStream();
            await using (var stream = e.File.OpenReadStream(maxAllowedSize: 20 * 1024 * 1024))
            {
                await stream.CopyToAsync(memory);
            }

            memory.Position = 0;

            var archiveResult = ProjectArchiveReader.Read(memory);
            _classSource = archiveResult.ClassSource;
            _configSource = archiveResult.ConfigSource;

            RenderDiagram();

            if (archiveResult.Diagnostics.Count > 0)
            {
                _diagnostics = (_diagnostics ?? new List<Diagnostic>())
                    .Concat(archiveResult.Diagnostics)
                    .ToList();
            }
        }
        catch (Exception ex)
        {
            _error = ex.ToString();
        }
    }

    private async Task DownloadZip()
    {
        if (_editor is null)
        {
            return;
        }

        var bytes = ProjectArchiveWriter.Write(_editor.ClassSource, _editor.ConfigSource);
        using var stream = new MemoryStream(bytes);
        using var streamRef = new DotNetStreamReference(stream);
        await JS.InvokeVoidAsync("downloadFileFromStream", "ef-schema-visualizer-export.zip", streamRef);
    }
```

- [ ] **Step 7: Build the Web project to verify it compiles**

Run: `dotnet build src/EfSchemaVisualizer.Web/EfSchemaVisualizer.Web.csproj`
Expected: `Build succeeded.` with 0 errors.

- [ ] **Step 8: Run the full test suite as a regression check**

Run: `dotnet test`
Expected: all existing tests plus the new `ProjectArchiveReaderTests`/`ProjectArchiveWriterTests` PASS, 0 failures.

- [ ] **Step 9: Commit**

```bash
git add src/EfSchemaVisualizer.Web/wwwroot/js/downloadFile.js src/EfSchemaVisualizer.Web/wwwroot/index.html src/EfSchemaVisualizer.Web/Pages/Home.razor
git commit -m "Wire zip upload/download into Home.razor via ProjectArchiveReader/Writer"
```

---

## Follow-up (not part of this plan)

In-browser interactive verification (actually selecting a `.zip` file and clicking Download in a real browser) has not been possible in past implementation sandboxes for this project (recorded in `docs/backlog.md` and `2026-07-14-editable-diagram-design.md`). If a browser is available when this plan is executed, manually verify: upload a zip with a class file + `OnModelCreating` config file renders the diagram correctly; upload a zip with no matching content shows the new diagnostic; download produces a valid zip openable by a standard zip tool with the two expected files.
