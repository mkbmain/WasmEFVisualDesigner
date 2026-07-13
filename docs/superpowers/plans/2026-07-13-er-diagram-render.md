# Read-only ER Diagram Render Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extend the Blazor WASM shell so pasted C# source (entity classes + `OnModelCreating`/DbContext config) renders as a visual, read-only ER diagram — entity boxes with property lists, connected by relationship lines — using `Z.Blazor.Diagrams`.

**Architecture:** A new `DiagramModelBuilder` static class in `EfSchemaVisualizer.Web` orchestrates the existing `Core` parse/merge pipeline (11 separate `Parse*`/`Apply*` calls — no single orchestrator exists in `Core` by design) into a `(Entities, Relationships, Diagnostics)` tuple. `Home.razor` is rewritten to two textareas + a "Render Diagram" button that builds a `Blazor.Diagrams.BlazorDiagram` from that tuple — one custom `EntityNode` component per entity, one `LinkModel` per relationship — and renders it via `DiagramCanvas`.

**Tech Stack:** .NET 10, Blazor WebAssembly, `Z.Blazor.Diagrams` 3.0.4.1 (NuGet package name; namespace `Blazor.Diagrams`), `EfSchemaVisualizer.Core` (existing project reference).

## Global Constraints

- Target framework: `net10.0` (matches the rest of the repo).
- No drag persistence, no `.zip` upload, no auto-layout algorithm, no rewriter wiring — this slice stays read-only (per `docs/superpowers/specs/2026-07-13-er-diagram-render-design.md`).
- No automated test project for `EfSchemaVisualizer.Web` — verification is manual in a browser, matching the shell slice's precedent.
- `DiagramModelBuilder` orchestration lives in the Web project, not `Core` — `Core` keeps parse/merge as separate composed calls (established pattern from every Priority 1/2 backlog item).
- Node display is name + property name/type/nullability + key marking only — no column name/type/default value/precision surfaced in this slice (per spec).
- Relationship lines carry a text label for `RelationshipKind` (e.g. `1—*`) — no crow's-foot notation.

---

### Task 1: Add Z.Blazor.Diagrams and wire up its static assets

**Files:**
- Modify: `src/EfSchemaVisualizer.Web/EfSchemaVisualizer.Web.csproj`
- Modify: `src/EfSchemaVisualizer.Web/wwwroot/index.html`

**Interfaces:**
- Produces: `Blazor.Diagrams`, `Blazor.Diagrams.Core.Models`, `Blazor.Diagrams.Core.Geometry`, `Blazor.Diagrams.Options`, `Blazor.Diagrams.Components` namespaces resolvable from the Web project, with the library's CSS/JS loaded so rendered diagrams are styled and interactive (pan/zoom/drag).

- [ ] **Step 1: Add the package reference**

```bash
dotnet add src/EfSchemaVisualizer.Web/EfSchemaVisualizer.Web.csproj package Z.Blazor.Diagrams --version 3.0.4.1
```

Expected: `info : PackageReference for package 'Z.Blazor.Diagrams' version '3.0.4.1' added to file '...EfSchemaVisualizer.Web.csproj'.` This pulls in `Z.Blazor.Diagrams.Core` transitively.

- [ ] **Step 2: Add the library's CSS and JS to `index.html`**

Open `src/EfSchemaVisualizer.Web/wwwroot/index.html`. Add this line inside `<head>`, immediately after the existing `<link href="css/app.css" ...>` line (or after whatever the last `<link>` is):

```html
    <link href="_content/Z.Blazor.Diagrams/style.css" rel="stylesheet" />
```

Add this line inside `<body>`, immediately before the existing `<script src="_framework/blazor.webassembly.js"></script>` line:

```html
    <script src="_content/Z.Blazor.Diagrams/script.min.js"></script>
```

- [ ] **Step 3: Verify the app builds**

```bash
dotnet build src/EfSchemaVisualizer.Web/EfSchemaVisualizer.Web.csproj
```

Expected: `Build succeeded.` with 0 errors.

- [ ] **Step 4: Commit**

```bash
git add src/EfSchemaVisualizer.Web/EfSchemaVisualizer.Web.csproj src/EfSchemaVisualizer.Web/wwwroot/index.html
git commit -m "Add Z.Blazor.Diagrams package and static assets"
```

---

### Task 2: Build the DiagramModelBuilder orchestration

**Files:**
- Create: `src/EfSchemaVisualizer.Web/DiagramModelBuilder.cs`

**Interfaces:**
- Consumes: `EfSchemaVisualizer.Core.Parsing.EntityClassParser.Parse(string) : ParseResult<IReadOnlyList<EntityModel>>`; `EfSchemaVisualizer.Core.Parsing.FluentConfigParser` — all ten `Parse*` methods (`ParseMaxLengths`, `ParsePrecisions`, `ParseIsRequired`, `ParseKeys`, `ParseTableMappings`, `ParseColumnNames`, `ParseColumnTypes`, `ParseDefaultValues`, `ParseIndexes`, `ParseRelationships(string, IReadOnlyList<EntityModel>)`); `EfSchemaVisualizer.Core.Parsing.ModelMerger` — all nine `Apply*` methods (`ApplyMaxLengths`, `ApplyIsRequired`, `ApplyPrecisions`, `ApplyKeys`, `ApplyIndexes`, `ApplyTableMapping`, `ApplyColumnNames`, `ApplyColumnTypes`, `ApplyDefaultValues`) plus `ApplyRelationships(IReadOnlyList<RelationshipConfig>) : IReadOnlyList<RelationshipModel>`.
- Produces: `DiagramModelBuilder.Build(string classSource, string configSource) : DiagramModelResult`, where `DiagramModelResult` is a new `sealed record DiagramModelResult(IReadOnlyList<EntityModel> Entities, IReadOnlyList<RelationshipModel> Relationships, IReadOnlyList<Diagnostic> Diagnostics)`. This is what Task 5's page code calls to get everything needed to build the diagram.

- [ ] **Step 1: Write `DiagramModelBuilder.cs`**

```csharp
using EfSchemaVisualizer.Core.Model;
using EfSchemaVisualizer.Core.Parsing;

namespace EfSchemaVisualizer.Web;

public sealed record DiagramModelResult(
    IReadOnlyList<EntityModel> Entities,
    IReadOnlyList<RelationshipModel> Relationships,
    IReadOnlyList<Diagnostic> Diagnostics);

public static class DiagramModelBuilder
{
    public static DiagramModelResult Build(string classSource, string configSource)
    {
        var entityParser = new EntityClassParser();
        var configParser = new FluentConfigParser();

        var entityResult = entityParser.Parse(classSource);
        var diagnostics = new List<Diagnostic>(entityResult.Diagnostics);

        var maxLengths = configParser.ParseMaxLengths(configSource);
        var precisions = configParser.ParsePrecisions(configSource);
        var isRequired = configParser.ParseIsRequired(configSource);
        var keys = configParser.ParseKeys(configSource);
        var tables = configParser.ParseTableMappings(configSource);
        var columnNames = configParser.ParseColumnNames(configSource);
        var columnTypes = configParser.ParseColumnTypes(configSource);
        var defaultValues = configParser.ParseDefaultValues(configSource);
        var indexes = configParser.ParseIndexes(configSource);
        var relationships = configParser.ParseRelationships(configSource, entityResult.Value);

        diagnostics.AddRange(maxLengths.Diagnostics);
        diagnostics.AddRange(precisions.Diagnostics);
        diagnostics.AddRange(isRequired.Diagnostics);
        diagnostics.AddRange(keys.Diagnostics);
        diagnostics.AddRange(tables.Diagnostics);
        diagnostics.AddRange(columnNames.Diagnostics);
        diagnostics.AddRange(columnTypes.Diagnostics);
        diagnostics.AddRange(defaultValues.Diagnostics);
        diagnostics.AddRange(indexes.Diagnostics);
        diagnostics.AddRange(relationships.Diagnostics);

        var entities = entityResult.Value
            .Select(entity => ModelMerger.ApplyMaxLengths(entity, maxLengths.Value))
            .Select(entity => ModelMerger.ApplyPrecisions(entity, precisions.Value))
            .Select(entity => ModelMerger.ApplyIsRequired(entity, isRequired.Value))
            .Select(entity => ModelMerger.ApplyKeys(entity, keys.Value))
            .Select(entity => ModelMerger.ApplyTableMapping(entity, tables.Value))
            .Select(entity => ModelMerger.ApplyColumnNames(entity, columnNames.Value))
            .Select(entity => ModelMerger.ApplyColumnTypes(entity, columnTypes.Value))
            .Select(entity => ModelMerger.ApplyDefaultValues(entity, defaultValues.Value))
            .Select(entity => ModelMerger.ApplyIndexes(entity, indexes.Value))
            .ToList();

        var relationshipModels = ModelMerger.ApplyRelationships(relationships.Value);

        return new DiagramModelResult(entities, relationshipModels, diagnostics);
    }
}
```

- [ ] **Step 2: Verify the app builds**

```bash
dotnet build src/EfSchemaVisualizer.Web/EfSchemaVisualizer.Web.csproj
```

Expected: `Build succeeded.` with 0 errors.

- [ ] **Step 3: Commit**

```bash
git add src/EfSchemaVisualizer.Web/DiagramModelBuilder.cs
git commit -m "Add DiagramModelBuilder orchestration for entity+relationship parsing"
```

---

### Task 3: Build the custom EntityNode component

**Files:**
- Create: `src/EfSchemaVisualizer.Web/Diagram/EntityNodeModel.cs`
- Create: `src/EfSchemaVisualizer.Web/Diagram/EntityNode.razor`

**Interfaces:**
- Consumes: `EfSchemaVisualizer.Core.Model.EntityModel` (via constructor), `Blazor.Diagrams.Core.Models.NodeModel` (base class), `Blazor.Diagrams.Core.Geometry.Point`.
- Produces: `EntityNodeModel` (a `NodeModel` subclass carrying an `EntityModel Entity` property) and `EntityNode.razor` (the Blazor component that renders it — used later by `Task 5`'s `diagram.RegisterComponent<EntityNodeModel, EntityNode>()`).

- [ ] **Step 1: Write `EntityNodeModel.cs`**

```csharp
using Blazor.Diagrams.Core.Geometry;
using Blazor.Diagrams.Core.Models;
using EfSchemaVisualizer.Core.Model;

namespace EfSchemaVisualizer.Web.Diagram;

public sealed class EntityNodeModel : NodeModel
{
    public EntityNodeModel(EntityModel entity, Point position) : base(position)
    {
        Entity = entity;
        Title = entity.Name;
    }

    public EntityModel Entity { get; }
}
```

- [ ] **Step 2: Write `EntityNode.razor`**

```razor
@namespace EfSchemaVisualizer.Web.Diagram
@using Microsoft.AspNetCore.Components
@using System.Linq

<div class="card" style="width: 260px; border: 1px solid #444;">
    <div class="card-header" style="font-weight: bold; padding: 4px 8px; background: #eee;">
        @Node.Entity.Name
    </div>
    <ul style="list-style: none; margin: 0; padding: 0;">
        @foreach (var property in Node.Entity.Properties)
        {
            var isKey = Node.Entity.KeyPropertyNames.Contains(property.Name);
            <li style="padding: 2px 8px; @(isKey ? "font-weight: bold;" : "")">
                @(isKey ? "🔑 " : "")@property.Name: @property.ClrType@(property.IsNullable ? "?" : "")
            </li>
        }
    </ul>
</div>

@code {
    [Parameter]
    public EntityNodeModel Node { get; set; } = null!;
}
```

- [ ] **Step 3: Verify the app builds**

```bash
dotnet build src/EfSchemaVisualizer.Web/EfSchemaVisualizer.Web.csproj
```

Expected: `Build succeeded.` with 0 errors.

- [ ] **Step 4: Commit**

```bash
git add src/EfSchemaVisualizer.Web/Diagram/EntityNodeModel.cs src/EfSchemaVisualizer.Web/Diagram/EntityNode.razor
git commit -m "Add EntityNode diagram component"
```

---

### Task 4: Add the RelationshipKind label helper

**Files:**
- Create: `src/EfSchemaVisualizer.Web/Diagram/RelationshipLabels.cs`

**Interfaces:**
- Consumes: `EfSchemaVisualizer.Core.Model.RelationshipKind` (enum: `OneToOne`, `OneToMany`, `ManyToMany`).
- Produces: `RelationshipLabels.For(RelationshipKind kind) : string`, used by Task 5 to label each `LinkModel`.

- [ ] **Step 1: Write `RelationshipLabels.cs`**

```csharp
using EfSchemaVisualizer.Core.Model;

namespace EfSchemaVisualizer.Web.Diagram;

public static class RelationshipLabels
{
    public static string For(RelationshipKind kind) => kind switch
    {
        RelationshipKind.OneToOne => "1—1",
        RelationshipKind.OneToMany => "1—*",
        RelationshipKind.ManyToMany => "*—*",
        _ => "?",
    };
}
```

- [ ] **Step 2: Verify the app builds**

```bash
dotnet build src/EfSchemaVisualizer.Web/EfSchemaVisualizer.Web.csproj
```

Expected: `Build succeeded.` with 0 errors.

- [ ] **Step 3: Commit**

```bash
git add src/EfSchemaVisualizer.Web/Diagram/RelationshipLabels.cs
git commit -m "Add relationship kind label helper"
```

---

### Task 5: Wire up the two-textarea page and render the diagram

**Files:**
- Modify: `src/EfSchemaVisualizer.Web/Pages/Home.razor`

**Interfaces:**
- Consumes: `DiagramModelBuilder.Build(string, string) : DiagramModelResult` (Task 2); `EntityNodeModel` (Task 3); `RelationshipLabels.For(RelationshipKind) : string` (Task 4); `Blazor.Diagrams.BlazorDiagram`, `Blazor.Diagrams.Options.BlazorDiagramOptions`, `Blazor.Diagrams.Core.Models.LinkModel`, `Blazor.Diagrams.Core.Geometry.Point`, `Blazor.Diagrams.Components.DiagramCanvas`.
- Produces: the working end-to-end diagram render this whole slice exists to prove.

- [ ] **Step 1: Replace `Home.razor`**

```razor
@page "/"
@using EfSchemaVisualizer.Core.Model
@using EfSchemaVisualizer.Core.Parsing
@using EfSchemaVisualizer.Web.Diagram
@using Blazor.Diagrams
@using Blazor.Diagrams.Components
@using Blazor.Diagrams.Core.Geometry
@using Blazor.Diagrams.Core.Models
@using Blazor.Diagrams.Options

<PageTitle>EF Schema Visualizer</PageTitle>

<h1>EF Schema Visualizer — ER Diagram</h1>

<p>Paste entity classes and matching <code>OnModelCreating</code> fluent config below, then click Render Diagram.</p>

<div style="display: flex; gap: 16px;">
    <div style="flex: 1;">
        <label>Entity classes</label>
        <textarea @bind="_classSource" @bind:event="oninput" rows="14" style="width: 100%; font-family: monospace;"></textarea>
    </div>
    <div style="flex: 1;">
        <label>DbContext / OnModelCreating</label>
        <textarea @bind="_configSource" @bind:event="oninput" rows="14" style="width: 100%; font-family: monospace;"></textarea>
    </div>
</div>

<p>
    <button class="btn btn-primary" @onclick="RenderDiagram">Render Diagram</button>
</p>

@if (_error is not null)
{
    <pre style="color: red;">@_error</pre>
}

@if (_diagnostics is { Count: > 0 })
{
    <pre style="color: darkorange;">Diagnostics:
@foreach (var diagnostic in _diagnostics)
{
    @($"  [{diagnostic.Code}] {diagnostic.Message}{Environment.NewLine}")
}</pre>
}

@if (_diagram is not null)
{
    <div style="height: 600px; width: 100%; border: 1px solid #ccc;">
        <CascadingValue Value="_diagram">
            <DiagramCanvas />
        </CascadingValue>
    </div>
}

@code {
    private string _classSource = """
        public class Blog
        {
            public int Id { get; set; }
            public string Title { get; set; } = "";
        }

        public class Post
        {
            public int Id { get; set; }
            public string Title { get; set; } = "";
            public int BlogId { get; set; }
            public Blog Blog { get; set; } = null!;
        }
        """;

    private string _configSource = """
        modelBuilder.Entity<Blog>(entity =>
        {
            entity.HasKey(e => e.Id);
        });

        modelBuilder.Entity<Post>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasOne(e => e.Blog).WithMany().HasForeignKey(e => e.BlogId);
        });
        """;

    private BlazorDiagram? _diagram;
    private IReadOnlyList<Diagnostic>? _diagnostics;
    private string? _error;

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

            var nodesByEntityName = new Dictionary<string, EntityNodeModel>();
            const int columns = 4;
            const double xSpacing = 320;
            const double ySpacing = 260;

            for (var i = 0; i < result.Entities.Count; i++)
            {
                var entity = result.Entities[i];
                var column = i % columns;
                var row = i / columns;
                var node = new EntityNodeModel(entity, new Point(column * xSpacing, row * ySpacing));
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

            _diagram = diagram;
        }
        catch (Exception ex)
        {
            _error = ex.ToString();
        }
    }
}
```

- [ ] **Step 2: Verify the app builds**

```bash
dotnet build src/EfSchemaVisualizer.Web/EfSchemaVisualizer.Web.csproj
```

Expected: `Build succeeded.` with 0 errors.

- [ ] **Step 3: Commit**

```bash
git add src/EfSchemaVisualizer.Web/Pages/Home.razor
git commit -m "Render parsed entities and relationships as an ER diagram"
```

---

### Task 6: Manual browser verification and design-doc writeup

**Files:**
- Modify: `docs/superpowers/specs/2026-07-13-er-diagram-render-design.md` (fill in the payload size result)

**Interfaces:**
- Consumes: the running app produced by Tasks 1-5.
- Produces: a filled-in verification record in the design doc; no code changes.

- [ ] **Step 1: Publish the app**

```bash
dotnet publish src/EfSchemaVisualizer.Web/EfSchemaVisualizer.Web.csproj -c Release -o /tmp/claude-0/-root-RiderProjects-WasmEFVisualDesigner/8828caca-86de-4f35-b132-a1f86ca63acc/scratchpad/web-publish
```

Expected: `Build succeeded.` and a `wwwroot` directory under the publish output containing `_framework/` and `_content/Z.Blazor.Diagrams/`.

- [ ] **Step 2: Measure the published payload size**

```bash
du -sh /tmp/claude-0/-root-RiderProjects-WasmEFVisualDesigner/8828caca-86de-4f35-b132-a1f86ca63acc/scratchpad/web-publish/wwwroot/_framework
```

Note the reported size for comparison against the shell slice's 46M baseline — this goes into the design doc in Step 5.

- [ ] **Step 3: Serve the published output locally**

```bash
cd /tmp/claude-0/-root-RiderProjects-WasmEFVisualDesigner/8828caca-86de-4f35-b132-a1f86ca63acc/scratchpad/web-publish/wwwroot && python3 -m http.server 8080
```

(Leave running in the background; a WASM app requires a real HTTP server, not `file://`, because of MIME-type requirements for `.wasm`/`.dll` assets.)

- [ ] **Step 4: Open in a browser and confirm the diagram works**

Open `http://localhost:8080` in a browser. Confirm, noting the rough time from navigation to interactive:
1. The page loads and shows the pre-filled `Blog`/`Post` sample in both textareas.
2. Clicking "Render Diagram" without editing the textareas shows two entity node boxes (`Blog`, `Post`), each listing its properties with CLR types, connected by a labeled relationship line (`1—*`).
3. The `Id` property on each node is visually marked as a key (bold + 🔑 prefix), since both entities configure `HasKey(e => e.Id)`.
4. Dragging a node moves it on the canvas without any error or page crash (this is the library's default behavior — nothing in this slice persists the new position, but it must not break rendering).
5. Editing the config textarea to remove the `HasOne`/`WithMany` call and clicking Render Diagram again shows the two nodes with no connecting line.
6. Replacing the class textarea with clearly invalid C# (e.g. `this is not c#`) and clicking Render Diagram does not crash the page — it should render an empty or near-empty diagram plus diagnostics, not hit the `catch` block's exception display, consistent with Roslyn's tolerant parsing (per the shell slice's prior finding).

- [ ] **Step 5: Record the result in the design doc**

Edit `docs/superpowers/specs/2026-07-13-er-diagram-render-design.md`, replacing the line:

```
6. Note the published payload size delta versus the shell slice's 46M
   baseline, recorded here after implementation.
```

with the actual measured values, e.g.:

```
6. Result (recorded YYYY-MM-DD): published `_framework` payload was
   <SIZE> (delta of <DELTA> versus the shell slice's 46M baseline);
   first load to interactive on localhost was approximately <TIME>.
   Z.Blazor.Diagrams rendered both entity nodes with correct property
   lists, key marking, and a labeled relationship line; dragging nodes,
   panning, and zooming all worked without errors under Mono WASM.
```

(Adjust the wording if any case in Step 4 behaved unexpectedly — this must reflect what was actually observed, not the expected outcome.)

- [ ] **Step 6: Stop the local server and clean up the publish output**

Stop the `python3 -m http.server` process (Ctrl-C, or `kill` the background job), then:

```bash
rm -rf /tmp/claude-0/-root-RiderProjects-WasmEFVisualDesigner/8828caca-86de-4f35-b132-a1f86ca63acc/scratchpad/web-publish
```

- [ ] **Step 7: Commit**

```bash
git add docs/superpowers/specs/2026-07-13-er-diagram-render-design.md
git commit -m "Record ER diagram render verification results"
```
