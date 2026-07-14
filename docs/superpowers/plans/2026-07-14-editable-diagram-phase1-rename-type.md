# Editable Diagram Phase 1: Rename + Type/Nullable Editing — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Design doc:** `docs/superpowers/specs/2026-07-14-editable-diagram-design.md` — this plan implements only Phase 1 ("Rename + type/nullable editing") of that design's five-phase sequencing. Read the design doc's Architecture section before starting; this plan assumes that context.

**Goal:** Let a user double-click an entity name or property name on the diagram to rename it, and double-click a property's type (or toggle a nullable checkbox) to change it — with every edit immediately rewriting the underlying C# source (shown back in the textareas) and refreshing the diagram, while preserving node drag positions.

**Architecture:** Every edit gesture validates against the currently-parsed model, calls the relevant `Core` rewriter method(s) against the in-memory source strings, re-runs `DiagramModelBuilder.Build`, and rebuilds the `BlazorDiagram`'s nodes/links — no separate diagram edit state. A new `DiagramEditor` class in the Web project owns the source strings and orchestrates validation + rewriter calls; a new `DiagramSync.Rebuild` helper rebuilds the `BlazorDiagram` node/link collections from a fresh `DiagramModelResult` while preserving existing node positions by ordinal index (safe for Phase 1 since rename/type-change never changes entity count or order — Phase 2's add/remove will need to revisit this).

**Tech Stack:** .NET 10 / C# (Roslyn `Microsoft.CodeAnalysis.CSharp` for rewriting), Blazor WebAssembly, Z.Blazor.Diagrams 3.0.4.1, xUnit for `Core` tests.

## Global Constraints

- Target framework `net10.0`, `<Nullable>enable</Nullable>`, `<ImplicitUsings>enable</ImplicitUsings>` (per both `.csproj` files) — write nullable-aware C#.
- Every `Core` rewriter method has the shape `public string MethodName(string sourceCode, ...)` and returns the full new source text; follow this exactly for new methods.
- `Core` changes require full xUnit test coverage in `tests/EfSchemaVisualizer.Core.Tests/`, following the existing test file's `[Fact]`-per-scenario, `SourceXxx` constant-per-fixture conventions.
- The Web project (`EfSchemaVisualizer.Web`) has **no automated test project** (project convention — Web is a thin caller into already-tested `Core` logic). Verify Web changes with `dotnet build` (compile-time correctness) plus manual browser verification steps listed in this plan. Do not add a test project for Web.
- Regenerated source is written back into the existing two textareas (`_classSource`/`_configSource` in `Home.razor`) — never introduce file download/`.zip` handling; that's explicitly out of scope (design doc Non-goals).
- Diagram library namespaces already in use: `Blazor.Diagrams`, `Blazor.Diagrams.Components`, `Blazor.Diagrams.Core.Geometry`, `Blazor.Diagrams.Core.Models`, `Blazor.Diagrams.Options` — reuse these, don't introduce a different diagramming library.
- No comments unless the WHY is genuinely non-obvious (project-wide default, not specific to this plan).

---

### Task 1: `EntityClassRewriter.ChangePropertyType`

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/CodeGen/EntityClassRewriter.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/CodeGen/EntityClassRewriterTests.cs`

**Interfaces:**
- Produces: `public string ChangePropertyType(string sourceCode, string className, string propertyName, string newClrType, bool newIsNullable)` on `EntityClassRewriter` — locates the property by name on the given top-level type (class/record/struct, via the existing private `FindTopLevelType` helper) and replaces its type node. Throws `InvalidOperationException` if the property isn't found (matching `RenameProperty`'s existing behavior for consistency).

- [ ] **Step 1: Write the failing tests**

Add to `tests/EfSchemaVisualizer.Core.Tests/CodeGen/EntityClassRewriterTests.cs` (add near the existing `RenameProperty_*` tests):

```csharp
private const string SourceForTypeChange = """
    public class Person
    {
        // unrelated comment that must survive
        public int Id { get; set; }
        public string Name { get; set; }
    }
    """;

[Fact]
public void ChangePropertyType_NonNullableToNonNullable_ChangesTypeOnly()
{
    var result = new EntityClassRewriter().ChangePropertyType(
        SourceForTypeChange, className: "Person", propertyName: "Id",
        newClrType: "long", newIsNullable: false);

    Assert.Contains("public long Id { get; set; }", result);
    Assert.Contains("public string Name { get; set; }", result);
    Assert.Contains("// unrelated comment that must survive", result);
}

[Fact]
public void ChangePropertyType_NonNullableToNullable_AddsQuestionMarkSuffix()
{
    var result = new EntityClassRewriter().ChangePropertyType(
        SourceForTypeChange, className: "Person", propertyName: "Name",
        newClrType: "string", newIsNullable: true);

    Assert.Contains("public string? Name { get; set; }", result);
}

[Fact]
public void ChangePropertyType_NullableToNonNullable_RemovesQuestionMarkSuffix()
{
    const string sourceWithNullableProperty = """
        public class Person
        {
            public string? MiddleName { get; set; }
        }
        """;

    var result = new EntityClassRewriter().ChangePropertyType(
        sourceWithNullableProperty, className: "Person", propertyName: "MiddleName",
        newClrType: "string", newIsNullable: false);

    Assert.Contains("public string MiddleName { get; set; }", result);
    Assert.DoesNotContain("string?", result);
}

[Fact]
public void ChangePropertyType_PropertyNotFound_Throws()
{
    var rewriter = new EntityClassRewriter();

    Assert.Throws<InvalidOperationException>(() =>
        rewriter.ChangePropertyType(
            SourceForTypeChange, className: "Person", propertyName: "DoesNotExist",
            newClrType: "int", newIsNullable: false));
}

[Fact]
public void ChangePropertyType_MultipleProperties_OnlyTargetChanges()
{
    var result = new EntityClassRewriter().ChangePropertyType(
        SourceForTypeChange, className: "Person", propertyName: "Id",
        newClrType: "Guid", newIsNullable: false);

    Assert.Contains("public Guid Id { get; set; }", result);
    Assert.Contains("public string Name { get; set; }", result);
}

[Fact]
public void ChangePropertyType_RecordBodyProperty_ChangesType()
{
    const string recordSource = """
        public record Person
        {
            public int Id { get; set; }
        }
        """;

    var result = new EntityClassRewriter().ChangePropertyType(
        recordSource, className: "Person", propertyName: "Id",
        newClrType: "long", newIsNullable: false);

    Assert.Contains("public long Id { get; set; }", result);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter "FullyQualifiedName~ChangePropertyType"`
Expected: FAIL — `ChangePropertyType` does not exist on `EntityClassRewriter` (compile error).

- [ ] **Step 3: Implement `ChangePropertyType`**

Add to `src/EfSchemaVisualizer.Core/CodeGen/EntityClassRewriter.cs`, alongside `RenameProperty` (same class, same file):

```csharp
public string ChangePropertyType(string sourceCode, string className, string propertyName, string newClrType, bool newIsNullable)
{
    var tree = CSharpSyntaxTree.ParseText(sourceCode);
    var root = tree.GetCompilationUnitRoot();

    var targetType = FindTopLevelType(root, className);

    var targetProperty = targetType.Members
        .OfType<PropertyDeclarationSyntax>()
        .FirstOrDefault(p => p.Identifier.Text == propertyName)
        ?? throw new InvalidOperationException($"No property named '{propertyName}' found on type '{className}'.");

    TypeSyntax newTypeSyntax = SyntaxFactory.ParseTypeName(newClrType);
    if (newIsNullable)
    {
        newTypeSyntax = SyntaxFactory.NullableType(newTypeSyntax);
    }

    var newProperty = targetProperty.WithType(newTypeSyntax);

    var newRoot = root.ReplaceNode(targetProperty, newProperty);
    return newRoot.NormalizeWhitespace().ToFullString();
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter "FullyQualifiedName~ChangePropertyType"`
Expected: PASS (6 tests).

- [ ] **Step 5: Run the full Core test suite to check for regressions**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests`
Expected: PASS, all tests (existing + 6 new).

- [ ] **Step 6: Commit**

```bash
git add src/EfSchemaVisualizer.Core/CodeGen/EntityClassRewriter.cs tests/EfSchemaVisualizer.Core.Tests/CodeGen/EntityClassRewriterTests.cs
git commit -m "Add EntityClassRewriter.ChangePropertyType for editable-diagram Phase 1"
```

---

### Task 2: `DiagramSync.Rebuild` helper (extract from `Home.razor`)

**Files:**
- Create: `src/EfSchemaVisualizer.Web/Diagram/DiagramSync.cs`
- Modify: `src/EfSchemaVisualizer.Web/Pages/Home.razor:89-140` (the `RenderDiagram` method's node/link construction block)

**Interfaces:**
- Consumes: `EfSchemaVisualizer.Web.DiagramModelResult` (existing, from `DiagramModelBuilder.cs`), `EfSchemaVisualizer.Web.Diagram.EntityNodeModel` (existing), `EfSchemaVisualizer.Web.Diagram.RelationshipLabels.For(RelationshipKind)` (existing).
- Produces: `public static class DiagramSync` with `public static void Rebuild(BlazorDiagram diagram, DiagramModelResult result)` — clears and repopulates `diagram.Nodes`/`diagram.Links` from `result`, preserving existing node positions by ordinal index (the *i*-th entity in `result.Entities` reuses the *i*-th existing node's position if one exists, else falls back to grid placement). This is the helper every later edit gesture (Task 5 onward) calls after a successful edit.

This task is a pure refactor — it changes *how* `Home.razor` builds the diagram, not *what* gets built. No new user-visible behavior yet. Do this refactor before wiring editing so Task 5+ can call one well-tested helper instead of duplicating the node/link construction loop inline in `Home.razor`.

- [ ] **Step 1: Create `DiagramSync.cs`**

```csharp
using Blazor.Diagrams;
using Blazor.Diagrams.Core.Geometry;
using Blazor.Diagrams.Core.Models;
using EfSchemaVisualizer.Core.Model;

namespace EfSchemaVisualizer.Web.Diagram;

public static class DiagramSync
{
    private const int Columns = 4;
    private const double XSpacing = 320;
    private const double YSpacing = 260;

    public static void Rebuild(BlazorDiagram diagram, DiagramModelResult result)
    {
        var previousPositions = diagram.Nodes
            .OfType<EntityNodeModel>()
            .Select(node => node.Position)
            .ToList();

        diagram.Nodes.Clear();
        diagram.Links.Clear();

        var nodesByEntityName = new Dictionary<string, EntityNodeModel>();

        for (var i = 0; i < result.Entities.Count; i++)
        {
            var entity = result.Entities[i];

            // Ordinal matching: safe while entity count/order can't change (Phase 1 is
            // rename/type-change only). Phase 2 (add/remove) will need identity-based
            // matching once the count and order can diverge from the previous render.
            var position = i < previousPositions.Count
                ? previousPositions[i]
                : new Point((i % Columns) * XSpacing, (i / Columns) * YSpacing);

            var node = new EntityNodeModel(entity, position);
            diagram.Nodes.Add(node);
            nodesByEntityName[entity.Name] = node;
        }

        foreach (var relationship in result.Relationships)
        {
            if (!nodesByEntityName.TryGetValue(relationship.PrincipalEntity, out var principalNode) ||
                !nodesByEntityName.TryGetValue(relationship.DependentEntity, out var dependentNode))
            {
                continue;
            }

            var link = new LinkModel(dependentNode, principalNode);
            link.AddLabel(RelationshipLabels.For(relationship.Kind));
            diagram.Links.Add(link);
        }
    }
}
```

- [ ] **Step 2: Replace the inline construction block in `Home.razor`**

In `src/EfSchemaVisualizer.Web/Pages/Home.razor`, the `RenderDiagram` method currently (lines ~100-134) builds nodes/links inline. Replace that block so it reads:

```csharp
private void RenderDiagram()
{
    _error = null;
    _diagnostics = null;
    _diagram = null;

    try
    {
        var result = DiagramModelBuilder.Build(_classSource, _configSource);
        _diagnostics = result.Diagnostics;

        var diagram = new BlazorDiagram(new BlazorDiagramOptions
        {
            AllowMultiSelection = true,
        });
        diagram.RegisterComponent<EntityNodeModel, EntityNode>();

        DiagramSync.Rebuild(diagram, result);

        _diagram = diagram;
    }
    catch (Exception ex)
    {
        _error = ex.ToString();
    }
}
```

This removes the `nodesByEntityName`, `columns`/`xSpacing`/`ySpacing` locals and the two `for`/`foreach` loops that previously lived directly in `RenderDiagram` — `DiagramSync.Rebuild` now owns that logic.

- [ ] **Step 3: Build to verify no compile errors**

Run: `dotnet build src/EfSchemaVisualizer.Web/EfSchemaVisualizer.Web.csproj`
Expected: `Build succeeded.`

- [ ] **Step 4: Manual smoke check**

Run: `dotnet publish src/EfSchemaVisualizer.Web/EfSchemaVisualizer.Web.csproj -c Release -o /tmp/claude-0/-root-RiderProjects-WasmEFVisualDesigner/3000b5a1-9d4b-4fee-ab10-d7b903502583/scratchpad/publish-task2`
Then serve `wwwroot` from that output folder (e.g. `python3 -m http.server 8080` from inside it) and open in a browser: confirm clicking "Render Diagram" with the pre-filled sample still shows the two-entity diagram exactly as before this refactor (no behavior change expected). If no browser is available in the current environment, note that explicitly as a follow-up rather than skipping silently.

- [ ] **Step 5: Commit**

```bash
git add src/EfSchemaVisualizer.Web/Diagram/DiagramSync.cs src/EfSchemaVisualizer.Web/Pages/Home.razor
git commit -m "Extract DiagramSync.Rebuild helper from Home.razor's RenderDiagram"
```

---

### Task 3: `DiagramEditor` (edit orchestration + validation)

**Files:**
- Create: `src/EfSchemaVisualizer.Web/Diagram/DiagramEditor.cs`
- Create: `src/EfSchemaVisualizer.Web/Diagram/DiagramEditContext.cs`

**Interfaces:**
- Consumes: `EfSchemaVisualizer.Web.DiagramModelBuilder.Build(string, string)` → `DiagramModelResult` (existing); `EfSchemaVisualizer.Core.CodeGen.EntityClassRewriter.RenameClass/RenameProperty/ChangePropertyType` (existing + Task 1); `EfSchemaVisualizer.Core.CodeGen.OnModelCreatingRewriter.RenameEntityReferences/RenamePropertyReferences` (existing).
- Produces:
  - `public sealed class DiagramEditResult` with `bool Success`, `string? Error`, static factories `DiagramEditResult.Ok()` and `DiagramEditResult.Fail(string error)`.
  - `public sealed class DiagramEditor` with constructor `DiagramEditor(string classSource, string configSource)`, properties `string ClassSource { get; }`, `string ConfigSource { get; }`, `DiagramModelResult Current { get; }`, and methods `DiagramEditResult RenameEntity(string oldName, string newName)`, `DiagramEditResult RenameProperty(string entityName, string oldPropertyName, string newPropertyName)`, `DiagramEditResult ChangePropertyType(string entityName, string propertyName, string newClrType, bool newIsNullable)`. Every method validates first (no rewrite on failure) and updates `ClassSource`/`ConfigSource`/`Current` together on success.
  - `public sealed class DiagramEditContext` with `required DiagramEditor Editor { get; init; }` and `required Func<Task> NotifyChangedAsync { get; init; }` — the cascading value `EntityNode.razor` (Task 5+) will consume to call back into `Home.razor` after a successful edit.

- [ ] **Step 1: Create `DiagramEditResult` and `DiagramEditor`**

`src/EfSchemaVisualizer.Web/Diagram/DiagramEditor.cs`:

```csharp
using EfSchemaVisualizer.Core.CodeGen;
using Microsoft.CodeAnalysis.CSharp;

namespace EfSchemaVisualizer.Web.Diagram;

public sealed class DiagramEditResult
{
    private DiagramEditResult(bool success, string? error)
    {
        Success = success;
        Error = error;
    }

    public bool Success { get; }
    public string? Error { get; }

    public static DiagramEditResult Ok() => new(true, null);
    public static DiagramEditResult Fail(string error) => new(false, error);
}

public sealed class DiagramEditor
{
    private readonly EntityClassRewriter _classRewriter = new();
    private readonly OnModelCreatingRewriter _configRewriter = new();

    public DiagramEditor(string classSource, string configSource)
    {
        ClassSource = classSource;
        ConfigSource = configSource;
        Current = DiagramModelBuilder.Build(classSource, configSource);
    }

    public string ClassSource { get; private set; }
    public string ConfigSource { get; private set; }
    public DiagramModelResult Current { get; private set; }

    public DiagramEditResult RenameEntity(string oldName, string newName)
    {
        if (!SyntaxFacts.IsValidIdentifier(newName))
        {
            return DiagramEditResult.Fail($"'{newName}' is not a valid entity name.");
        }

        if (Current.Entities.Any(e => e.Name == newName))
        {
            return DiagramEditResult.Fail($"An entity named '{newName}' already exists.");
        }

        var newClassSource = _classRewriter.RenameClass(ClassSource, oldName, newName);
        var newConfigSource = _configRewriter.RenameEntityReferences(ConfigSource, oldName, newName);
        Apply(newClassSource, newConfigSource);
        return DiagramEditResult.Ok();
    }

    public DiagramEditResult RenameProperty(string entityName, string oldPropertyName, string newPropertyName)
    {
        if (!SyntaxFacts.IsValidIdentifier(newPropertyName))
        {
            return DiagramEditResult.Fail($"'{newPropertyName}' is not a valid property name.");
        }

        var entity = Current.Entities.FirstOrDefault(e => e.Name == entityName);
        if (entity is null)
        {
            return DiagramEditResult.Fail($"Entity '{entityName}' not found.");
        }

        if (entity.Properties.Any(p => p.Name == newPropertyName))
        {
            return DiagramEditResult.Fail($"'{entityName}' already has a property named '{newPropertyName}'.");
        }

        var newClassSource = _classRewriter.RenameProperty(ClassSource, entityName, oldPropertyName, newPropertyName);
        var newConfigSource = _configRewriter.RenamePropertyReferences(ConfigSource, entityName, oldPropertyName, newPropertyName);
        Apply(newClassSource, newConfigSource);
        return DiagramEditResult.Ok();
    }

    public DiagramEditResult ChangePropertyType(string entityName, string propertyName, string newClrType, bool newIsNullable)
    {
        if (!IsValidTypeToken(newClrType))
        {
            return DiagramEditResult.Fail($"'{newClrType}' is not a valid type.");
        }

        var newClassSource = _classRewriter.ChangePropertyType(ClassSource, entityName, propertyName, newClrType, newIsNullable);
        Apply(newClassSource, ConfigSource);
        return DiagramEditResult.Ok();
    }

    private void Apply(string newClassSource, string newConfigSource)
    {
        ClassSource = newClassSource;
        ConfigSource = newConfigSource;
        Current = DiagramModelBuilder.Build(ClassSource, ConfigSource);
    }

    private static bool IsValidTypeToken(string typeText)
    {
        var trimmed = typeText.Trim();
        if (trimmed.Length == 0)
        {
            return false;
        }

        var typeSyntax = SyntaxFactory.ParseTypeName(trimmed);
        return typeSyntax.ToFullString() == trimmed && !typeSyntax.ContainsDiagnostics;
    }
}
```

- [ ] **Step 2: Create `DiagramEditContext`**

`src/EfSchemaVisualizer.Web/Diagram/DiagramEditContext.cs`:

```csharp
namespace EfSchemaVisualizer.Web.Diagram;

public sealed class DiagramEditContext
{
    public required DiagramEditor Editor { get; init; }
    public required Func<Task> NotifyChangedAsync { get; init; }
}
```

- [ ] **Step 3: Build to verify no compile errors**

Run: `dotnet build src/EfSchemaVisualizer.Web/EfSchemaVisualizer.Web.csproj`
Expected: `Build succeeded.` (Nothing references these two new classes yet, so this only checks they compile standalone.)

- [ ] **Step 4: Commit**

```bash
git add src/EfSchemaVisualizer.Web/Diagram/DiagramEditor.cs src/EfSchemaVisualizer.Web/Diagram/DiagramEditContext.cs
git commit -m "Add DiagramEditor and DiagramEditContext for editable-diagram Phase 1"
```

---

### Task 4: Wire `DiagramEditor`/`DiagramEditContext` into `Home.razor`

**Files:**
- Modify: `src/EfSchemaVisualizer.Web/Pages/Home.razor`

**Interfaces:**
- Consumes: `DiagramEditor` and `DiagramEditContext` (Task 3), `DiagramSync.Rebuild` (Task 2).
- Produces: `Home.razor` now holds a `DiagramEditor? _editor` field and passes a `DiagramEditContext` as a nested `<CascadingValue>` inside the existing `<CascadingValue Value="_diagram">` block, so `EntityNode.razor` (Task 5+) can receive it via `[CascadingParameter]`. `Home.razor` also gains `private async Task OnDiagramEditedAsync()`, the callback every future edit gesture triggers after a successful rewrite — it re-reads `_editor.ClassSource`/`ConfigSource` back into the textarea-bound fields, updates `_diagnostics`, and calls `DiagramSync.Rebuild`.

This task still produces no new *visible* editing UI (that starts in Task 5) — it only wires the plumbing so `RenderDiagram` constructs a `DiagramEditor` instead of calling `DiagramModelBuilder.Build` directly, and exposes `_editContext` for the diagram markup.

- [ ] **Step 1: Update the `@code` block**

Replace the `RenderDiagram` method and its surrounding fields in `Home.razor` with:

```csharp
private DiagramEditor? _editor;
private DiagramEditContext? _editContext;
private BlazorDiagram? _diagram;
private IReadOnlyList<Diagnostic>? _diagnostics;
private string? _error;

private void RenderDiagram()
{
    _error = null;
    _diagnostics = null;
    _diagram = null;
    _editContext = null;

    try
    {
        _editor = new DiagramEditor(_classSource, _configSource);
        _diagnostics = _editor.Current.Diagnostics;

        var diagram = new BlazorDiagram(new BlazorDiagramOptions
        {
            AllowMultiSelection = true,
        });
        diagram.RegisterComponent<EntityNodeModel, EntityNode>();

        DiagramSync.Rebuild(diagram, _editor.Current);

        _diagram = diagram;
        _editContext = new DiagramEditContext
        {
            Editor = _editor,
            NotifyChangedAsync = OnDiagramEditedAsync,
        };
    }
    catch (Exception ex)
    {
        _error = ex.ToString();
    }
}

private Task OnDiagramEditedAsync()
{
    if (_diagram is null || _editor is null)
    {
        return Task.CompletedTask;
    }

    _classSource = _editor.ClassSource;
    _configSource = _editor.ConfigSource;
    _diagnostics = _editor.Current.Diagnostics;
    DiagramSync.Rebuild(_diagram, _editor.Current);
    StateHasChanged();

    return Task.CompletedTask;
}
```

(This replaces the version of `RenderDiagram` written in Task 2 Step 2 — `_editor`/`_editContext` are new fields alongside the existing `_diagram`/`_diagnostics`/`_error`.)

- [ ] **Step 2: Update the markup to nest the edit-context `<CascadingValue>`**

Change:

```razor
@if (_diagram is not null)
{
    <div style="height: 600px; width: 100%; border: 1px solid #ccc;">
        <CascadingValue Value="_diagram">
            <DiagramCanvas />
        </CascadingValue>
    </div>
}
```

to:

```razor
@if (_diagram is not null && _editContext is not null)
{
    <div style="height: 600px; width: 100%; border: 1px solid #ccc;">
        <CascadingValue Value="_diagram">
            <CascadingValue Value="_editContext">
                <DiagramCanvas />
            </CascadingValue>
        </CascadingValue>
    </div>
}
```

- [ ] **Step 3: Build to verify no compile errors**

Run: `dotnet build src/EfSchemaVisualizer.Web/EfSchemaVisualizer.Web.csproj`
Expected: `Build succeeded.`

- [ ] **Step 4: Manual smoke check**

Publish and serve locally (same approach as Task 2 Step 4). Confirm clicking "Render Diagram" with the pre-filled sample still renders identically to before — this task changes internals only, `EntityNode.razor` hasn't changed yet so there's still no visible edit affordance.

- [ ] **Step 5: Commit**

```bash
git add src/EfSchemaVisualizer.Web/Pages/Home.razor
git commit -m "Wire DiagramEditor/DiagramEditContext into Home.razor"
```

---

### Task 5: Inline entity rename

**Files:**
- Modify: `src/EfSchemaVisualizer.Web/Diagram/EntityNode.razor`

**Interfaces:**
- Consumes: `DiagramEditContext` (Task 3/4) via `[CascadingParameter]`; `DiagramEditor.RenameEntity(string, string)` (Task 3).
- Produces: Double-clicking an entity's title bar turns it into an editable text input; Enter or blur commits (calling `RenameEntity` then `EditContext.NotifyChangedAsync()` on success, or showing an inline error on failure); Escape cancels without committing.

- [ ] **Step 1: Add `using` directives and cascading parameter**

At the top of `EntityNode.razor`, add:

```razor
@using Microsoft.AspNetCore.Components.Web
```

(it already has `@using Microsoft.AspNetCore.Components` and `@using System.Linq`.)

In the `@code` block, add above the existing `Node` parameter:

```csharp
[CascadingParameter]
public DiagramEditContext EditContext { get; set; } = null!;
```

- [ ] **Step 2: Add rename state fields and handlers**

In the `@code` block:

```csharp
private bool _isEditingName;
private string _editingName = string.Empty;
private string? _nameError;

private void BeginNameEdit()
{
    _isEditingName = true;
    _editingName = Node.Entity.Name;
    _nameError = null;
}

private async Task CommitNameEdit()
{
    if (!_isEditingName)
    {
        return;
    }

    _isEditingName = false;

    if (_editingName == Node.Entity.Name)
    {
        return;
    }

    var result = EditContext.Editor.RenameEntity(Node.Entity.Name, _editingName);
    if (result.Success)
    {
        _nameError = null;
        await EditContext.NotifyChangedAsync();
    }
    else
    {
        _nameError = result.Error;
    }
}

private async Task OnNameKeyDown(KeyboardEventArgs e)
{
    if (e.Key == "Enter")
    {
        await CommitNameEdit();
    }
    else if (e.Key == "Escape")
    {
        _isEditingName = false;
        _nameError = null;
    }
}
```

- [ ] **Step 3: Replace the title-bar markup**

Change:

```razor
<div class="card-header" style="font-weight: bold; padding: 4px 8px; background: #eee;">
    @Node.Entity.Name
</div>
```

to:

```razor
<div class="card-header" style="font-weight: bold; padding: 4px 8px; background: #eee;">
    @if (_isEditingName)
    {
        <input value="@_editingName"
               @oninput="e => _editingName = e.Value?.ToString() ?? string.Empty"
               @onblur="CommitNameEdit"
               @onkeydown="OnNameKeyDown"
               @onpointerdown:stopPropagation="true"
               @onmousedown:stopPropagation="true" />
    }
    else
    {
        <span @ondblclick="BeginNameEdit">@Node.Entity.Name</span>
    }
</div>
@if (_nameError is not null)
{
    <div style="color: red; font-size: 0.8em; padding: 0 8px;">@_nameError</div>
}
```

(The `stopPropagation` attributes prevent Z.Blazor.Diagrams' node-drag pointer handler from engaging while the user is clicking/typing inside the rename input.)

- [ ] **Step 4: Build to verify no compile errors**

Run: `dotnet build src/EfSchemaVisualizer.Web/EfSchemaVisualizer.Web.csproj`
Expected: `Build succeeded.`

- [ ] **Step 5: Manual browser verification**

Publish and serve locally (same approach as Task 2 Step 4). In the browser:
1. Click "Render Diagram" with the pre-filled sample.
2. Double-click the "Blog" node's title. Confirm it becomes an editable text box pre-filled with "Blog".
3. Change it to "Weblog", press Enter. Confirm: the node's title now reads "Weblog"; the "Entity classes" textarea now shows `public class Weblog` instead of `public class Blog`; the "DbContext / OnModelCreating" textarea shows `modelBuilder.Entity<Weblog>(...)` instead of `Blog`; the "Post" node's relationship link is unaffected; the node did not jump position.
4. Double-click "Weblog" again, change it to "Post" (colliding with the existing "Post" entity), press Enter. Confirm an inline red error appears (e.g. "An entity named 'Post' already exists.") and the title reverts to displaying "Weblog" — the textareas are unchanged.
5. Double-click a title, press Escape. Confirm it cancels without changing anything.

If no browser is available in the current environment, record that explicitly as an unverified follow-up rather than claiming success.

- [ ] **Step 6: Commit**

```bash
git add src/EfSchemaVisualizer.Web/Diagram/EntityNode.razor
git commit -m "Add inline entity rename to the diagram"
```

---

### Task 6: Inline property rename

**Files:**
- Modify: `src/EfSchemaVisualizer.Web/Diagram/EntityNode.razor`

**Interfaces:**
- Consumes: `DiagramEditor.RenameProperty(string, string, string)` (Task 3).
- Produces: Double-clicking a property's name (within its row) turns it into an editable input, same commit/cancel/error pattern as Task 5, scoped per-property via a `Dictionary<string, string>` of per-property errors (since multiple rows can each show their own error independently).

- [ ] **Step 1: Add property-rename state fields and handlers**

In the `@code` block, add:

```csharp
private string? _editingPropertyName;
private string _editingPropertyValue = string.Empty;
private readonly Dictionary<string, string> _propertyErrors = new();

private void BeginPropertyRename(string propertyName)
{
    _editingPropertyName = propertyName;
    _editingPropertyValue = propertyName;
    _propertyErrors.Remove(propertyName);
}

private async Task CommitPropertyRename(string originalPropertyName)
{
    if (_editingPropertyName != originalPropertyName)
    {
        return;
    }

    _editingPropertyName = null;

    if (_editingPropertyValue == originalPropertyName)
    {
        return;
    }

    var result = EditContext.Editor.RenameProperty(Node.Entity.Name, originalPropertyName, _editingPropertyValue);
    if (result.Success)
    {
        _propertyErrors.Remove(originalPropertyName);
        await EditContext.NotifyChangedAsync();
    }
    else
    {
        _propertyErrors[originalPropertyName] = result.Error!;
    }
}

private async Task OnPropertyRenameKeyDown(KeyboardEventArgs e, string propertyName)
{
    if (e.Key == "Enter")
    {
        await CommitPropertyRename(propertyName);
    }
    else if (e.Key == "Escape")
    {
        _editingPropertyName = null;
    }
}
```

- [ ] **Step 2: Replace the property-name portion of the row markup**

Change the `<li>` body (currently a single line rendering `@(isKey ? "🔑 " : "")@property.Name: @property.ClrType@(...)`) to:

```razor
<li style="padding: 2px 8px; @(isKey ? "font-weight: bold;" : "")">
    @if (isKey)
    {
        <text>🔑 </text>
    }
    @if (_editingPropertyName == property.Name)
    {
        <input value="@_editingPropertyValue"
               @oninput="e => _editingPropertyValue = e.Value?.ToString() ?? string.Empty"
               @onblur="() => CommitPropertyRename(property.Name)"
               @onkeydown="e => OnPropertyRenameKeyDown(e, property.Name)"
               @onpointerdown:stopPropagation="true"
               @onmousedown:stopPropagation="true" />
    }
    else
    {
        <span @ondblclick="() => BeginPropertyRename(property.Name)">@property.Name</span>
    }
    : @property.ClrType@(property.IsNullable ? "?" : "")
    @if (_propertyErrors.TryGetValue(property.Name, out var propertyError))
    {
        <div style="color: red; font-size: 0.8em;">@propertyError</div>
    }
</li>
```

- [ ] **Step 3: Build to verify no compile errors**

Run: `dotnet build src/EfSchemaVisualizer.Web/EfSchemaVisualizer.Web.csproj`
Expected: `Build succeeded.`

- [ ] **Step 4: Manual browser verification**

Publish and serve locally. In the browser:
1. Render the pre-filled sample.
2. Double-click "Title" on the "Blog" node. Confirm it becomes editable.
3. Rename to "Headline", press Enter. Confirm the row now reads "Headline: string"; the "Entity classes" textarea shows `public string Headline` in the `Blog` class; if any fluent config referenced `Title` for `Blog` it would be updated too (the pre-filled sample has none for `Blog.Title`, so just confirm the class-side rename).
4. Try renaming "Id" to "BlogId" — wait, "Post" has its own separate "Id"; within the same entity, try renaming "Id" to an existing sibling name like "Headline" (post-step-3) to trigger the duplicate-name inline error; confirm it appears on that specific row only and the textarea is unchanged.

If no browser is available, record that explicitly as an unverified follow-up.

- [ ] **Step 5: Commit**

```bash
git add src/EfSchemaVisualizer.Web/Diagram/EntityNode.razor
git commit -m "Add inline property rename to the diagram"
```

---

### Task 7: Property type + nullable editing

**Files:**
- Modify: `src/EfSchemaVisualizer.Web/Diagram/EntityNode.razor`

**Interfaces:**
- Consumes: `DiagramEditor.ChangePropertyType(string, string, string, bool)` (Task 3).
- Produces: Double-clicking a property's type text turns it into an editable input (committing via `ChangePropertyType` with the property's current nullable flag); a checkbox next to the type toggles nullability directly (calling `ChangePropertyType` with the property's current type and the toggled flag). Both reuse the same `_propertyErrors` dictionary from Task 6 for inline error display.

- [ ] **Step 1: Add type-edit state fields and handlers**

In the `@code` block, add (needs `using EfSchemaVisualizer.Core.Model;` at the top of the file for `PropertyModel`):

```razor
@using EfSchemaVisualizer.Core.Model
```

```csharp
private string? _editingPropertyType;
private string _editingTypeValue = string.Empty;

private void BeginTypeEdit(PropertyModel property)
{
    _editingPropertyType = property.Name;
    _editingTypeValue = property.ClrType;
    _propertyErrors.Remove(property.Name);
}

private async Task CommitTypeEdit(PropertyModel property)
{
    if (_editingPropertyType != property.Name)
    {
        return;
    }

    _editingPropertyType = null;

    if (_editingTypeValue == property.ClrType)
    {
        return;
    }

    var result = EditContext.Editor.ChangePropertyType(Node.Entity.Name, property.Name, _editingTypeValue, property.IsNullable);
    if (result.Success)
    {
        _propertyErrors.Remove(property.Name);
        await EditContext.NotifyChangedAsync();
    }
    else
    {
        _propertyErrors[property.Name] = result.Error!;
    }
}

private async Task OnTypeKeyDown(KeyboardEventArgs e, PropertyModel property)
{
    if (e.Key == "Enter")
    {
        await CommitTypeEdit(property);
    }
    else if (e.Key == "Escape")
    {
        _editingPropertyType = null;
    }
}

private async Task ToggleNullable(PropertyModel property, bool isNullable)
{
    var result = EditContext.Editor.ChangePropertyType(Node.Entity.Name, property.Name, property.ClrType, isNullable);
    if (result.Success)
    {
        _propertyErrors.Remove(property.Name);
        await EditContext.NotifyChangedAsync();
    }
    else
    {
        _propertyErrors[property.Name] = result.Error!;
    }
}
```

- [ ] **Step 2: Replace the type portion of the row markup**

Change the line `: @property.ClrType@(property.IsNullable ? "?" : "")` (added in Task 6) to:

```razor
:
@if (_editingPropertyType == property.Name)
{
    <input style="width: 90px;" value="@_editingTypeValue"
           @oninput="e => _editingTypeValue = e.Value?.ToString() ?? string.Empty"
           @onblur="() => CommitTypeEdit(property)"
           @onkeydown="e => OnTypeKeyDown(e, property)"
           @onpointerdown:stopPropagation="true"
           @onmousedown:stopPropagation="true" />
}
else
{
    <span @ondblclick="() => BeginTypeEdit(property)">@property.ClrType@(property.IsNullable ? "?" : "")</span>
}
<label style="font-size: 0.8em; margin-left: 4px;">
    <input type="checkbox" checked="@property.IsNullable"
           @onchange="e => ToggleNullable(property, (bool)(e.Value ?? false))"
           @onpointerdown:stopPropagation="true"
           @onmousedown:stopPropagation="true" />
    nullable
</label>
```

- [ ] **Step 3: Build to verify no compile errors**

Run: `dotnet build src/EfSchemaVisualizer.Web/EfSchemaVisualizer.Web.csproj`
Expected: `Build succeeded.`

- [ ] **Step 4: Manual browser verification**

Publish and serve locally. In the browser:
1. Render the pre-filled sample.
2. Double-click "int" next to "Id" on "Post". Change it to "long", press Enter. Confirm the row now reads "Id: long"; the "Entity classes" textarea shows `public long Id { get; set; }` under `Post`.
3. Check the "nullable" checkbox next to "Title" on "Blog". Confirm the row updates to "string?" and the textarea shows `public string? Title { get; set; } = "";` — note any pre-existing initializer; if the rewriter's `NormalizeWhitespace`/type-replace interacts oddly with the `= ""` initializer on a now-nullable property, record that as an observation (not necessarily a blocker for this phase, but note it for Phase 2+ awareness).
4. Double-click a type field, type garbage like "int int", press Enter. Confirm an inline error appears (e.g. "'int int' is not a valid type.") and the field reverts — textarea unchanged.
5. Confirm dragging a node by its title bar (when not in edit mode) still works and doesn't conflict with the new inline-edit inputs elsewhere on the node.

If no browser is available, record that explicitly as an unverified follow-up.

- [ ] **Step 5: Commit**

```bash
git add src/EfSchemaVisualizer.Web/Diagram/EntityNode.razor
git commit -m "Add inline property type and nullable editing to the diagram"
```

---

### Task 8: Full Phase 1 regression pass

**Files:** none (verification-only task).

- [ ] **Step 1: Run the full Core test suite**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests`
Expected: PASS, all tests including the 6 from Task 1.

- [ ] **Step 2: Build the whole solution**

Run: `dotnet build`
Expected: `Build succeeded.` for both `EfSchemaVisualizer.Core` and `EfSchemaVisualizer.Web`.

- [ ] **Step 3: End-to-end manual verification**

Publish (`dotnet publish src/EfSchemaVisualizer.Web/EfSchemaVisualizer.Web.csproj -c Release -o <output>`), serve `wwwroot` locally, and in a real browser:
1. Render the pre-filled sample.
2. Drag the "Post" node to a new position.
3. Rename "Post" to "Article" (Task 5's flow). Confirm the node stays at the dragged position (ordinal-position preservation working) and the relationship link to "Blog" still renders correctly with its label.
4. Rename a property, change a type, toggle nullable — confirm each updates both textareas and the diagram without losing the other node's dragged position.
5. Trigger each of the three inline-error cases (duplicate entity name, duplicate property name, invalid type text) and confirm none of them touch the textareas.

Record the outcome (pass/fail per scenario) the same way `2026-07-13-er-diagram-render-design.md`'s Verification section did — if no browser is available in this environment, state that plainly rather than asserting success.

- [ ] **Step 4: Update the design doc's Sequencing section**

In `docs/superpowers/specs/2026-07-14-editable-diagram-design.md`, under "## Sequencing", add a short "**Update:**" note under item 1 recording what was verified (or not) per Step 3 above, following the same update-note convention used throughout the other spec files in this repo (e.g. `2026-07-13-er-diagram-render-design.md`'s Verification section, or the `**Update:**` notes in `docs/backlog.md`).

- [ ] **Step 5: Commit**

```bash
git add docs/superpowers/specs/2026-07-14-editable-diagram-design.md
git commit -m "Record Phase 1 (rename + type/nullable editing) verification results"
```
