# Editable Diagram Phase 2: Add/Remove Entities and Properties — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Design doc:** `docs/superpowers/specs/2026-07-14-editable-diagram-design.md` — this plan implements Phase 2 ("Add/remove entities and properties") of that design's five-phase sequencing. Phase 1 (rename + type/nullable editing) is already merged to `main`. Read the design doc's Architecture section (source-text-is-truth, validation-before-rewrite) before starting; this plan assumes that context and follows the same conventions Phase 1 established.

**Goal:** Let a user add a new entity via a toolbar button, add a new property via a per-node "+ Add property" row, and remove an entity or property via a hover/visible "×" button — with every action rewriting the underlying C# source and refreshing the diagram, while correctly preserving dragged node positions even though entity count and order can now change (which Phase 1 explicitly did not have to handle).

**Architecture:** Same source-text-is-truth loop as Phase 1 (gesture → validate → `Core` rewriter call(s) → `Apply` reparse → `NotifyChangedAsync` → `DiagramSync.Rebuild`). The one architectural change this phase requires: `DiagramSync.Rebuild`'s existing position-preservation logic matches nodes by **ordinal index**, which Phase 1's own code comment (`DiagramSync.cs:30-32`) already flags as unsafe once entity count/order can change. This phase replaces ordinal matching with **entity-identity matching**: `DiagramEditor` assigns each entity a stable `Guid` on creation, re-keys it on rename (so renamed entities still keep their dragged position — the Phase 1 guarantee must not regress), assigns a fresh `Guid` on add, and drops it on remove. `DiagramSync.Rebuild` matches previous node positions by this `Guid`, not by array position.

**Tech Stack:** .NET 10 / C# (Roslyn `Microsoft.CodeAnalysis.CSharp`), Blazor WebAssembly, Z.Blazor.Diagrams 3.0.4.1, xUnit for `Core` tests (none needed this phase — no new `Core` methods; `AddClass`/`RemoveClass`/`AddProperty`/`RemoveProperty`/`AddEntity`/`RemoveEntity` already exist and are already tested).

## Global Constraints

- Target framework `net10.0`, `<Nullable>enable</Nullable>`, `<ImplicitUsings>enable</ImplicitUsings>` (per both `.csproj` files).
- No new `Core` rewriter methods are needed this phase — `EntityClassRewriter.AddClass/RemoveClass/AddProperty/RemoveProperty` and `OnModelCreatingRewriter.AddEntity/RemoveEntity` already exist and are already unit-tested. Do not modify their signatures.
- The Web project (`EfSchemaVisualizer.Web`) has **no automated test project** (project convention). Verify Web changes with `dotnet build` plus manual browser verification steps listed in this plan. Do not add a test project.
- Every `DiagramEditor` public method must validate before calling any rewriter, and must never let a rewriter throw past it — always return a `DiagramEditResult` (`Ok()` or `Fail(string)`), matching the contract Phase 1 established (and had to fix twice: once in-task, once in the final whole-branch review).
- Regenerated source is written back into the existing two textareas — never introduce file download/`.zip` handling (out of scope, per design doc Non-goals).
- Diagram library namespaces already in use: `Blazor.Diagrams`, `Blazor.Diagrams.Components`, `Blazor.Diagrams.Core.Geometry`, `Blazor.Diagrams.Core.Models`, `Blazor.Diagrams.Options` — reuse these.
- No comments unless the WHY is genuinely non-obvious (project-wide default).
- **Scope decision for this phase:** removing an entity or property that is still referenced by a relationship (as principal, dependent, foreign-key property, or navigation property) is *refused* with an inline error rather than silently cascaded, because Phase 2 has no relationship-removal capability yet (that's Phase 5's `RemoveRelationship`). This is a deliberate, narrower choice than the design doc's UX-conventions section, which described a confirmation dialog for cascading removal — refusing outright avoids needing new UI (a confirm dialog) for a capability (relationship removal) that doesn't exist yet in this codebase. If Phase 5 ships first in some future reordering, this guard can be relaxed then.

---

### Task 1: Entity-identity tracking (`Guid`-based position matching)

**Files:**
- Modify: `src/EfSchemaVisualizer.Web/Diagram/DiagramEditor.cs`
- Modify: `src/EfSchemaVisualizer.Web/Diagram/EntityNodeModel.cs`
- Modify: `src/EfSchemaVisualizer.Web/Diagram/DiagramSync.cs`
- Modify: `src/EfSchemaVisualizer.Web/Pages/Home.razor`

**Interfaces:**
- Produces: `DiagramEditor.EntityIds` (`IReadOnlyDictionary<string, Guid>`, entity name → stable id, kept in sync with `Current.Entities` by every mutating method); `EntityNodeModel`'s constructor now takes a `Guid entityId` and exposes `EntityId`; `DiagramSync.Rebuild(BlazorDiagram diagram, DiagramModelResult result, IReadOnlyDictionary<string, Guid> entityIds)` (new third parameter) matches previous node positions by `EntityId` instead of ordinal index.
- Consumes (later tasks): Task 2's `AddEntity()`/`RemoveEntity(string)` and Task 3's `AddProperty(string)`/`RemoveProperty(string, string)` must each keep `EntityIds` correctly in sync (assign a new `Guid` on add, remove the entry on remove) — this task only wires up the *tracking mechanism* and updates `RenameEntity` (the only existing mutator that needs to re-key rather than add/remove); Task 2 adds the add/remove-side bookkeeping.

This task changes no user-visible behavior for Phase 1's existing scenarios — a plain rename still preserves position, exactly as before, just via a different matching mechanism. Do this task first since Tasks 2-3 depend on `EntityIds` existing.

- [ ] **Step 1: Add entity-id tracking to `DiagramEditor`**

In `src/EfSchemaVisualizer.Web/Diagram/DiagramEditor.cs`, add a field and populate it in the constructor:

```csharp
public sealed class DiagramEditor
{
    private readonly EntityClassRewriter _classRewriter = new();
    private readonly OnModelCreatingRewriter _configRewriter = new();
    private readonly Dictionary<string, Guid> _entityIds = new();

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

(Replace the existing constructor and the `ClassSource`/`ConfigSource`/`Current` property block with the above — keep every method below unchanged for now, this step only adds the field, the `EntityIds` property, and the constructor's populate-loop.)

- [ ] **Step 2: Re-key the entity id on rename**

In `RenameEntity`, after the existing `Apply(newClassSource, newConfigSource);` line and before `return DiagramEditResult.Ok();`, add:

```csharp
        var newClassSource = _classRewriter.RenameClass(ClassSource, oldName, newName);
        newClassSource = _classRewriter.RenamePropertyTypeReferences(newClassSource, oldName, newName);
        var newConfigSource = _configRewriter.RenameEntityReferences(ConfigSource, oldName, newName);
        Apply(newClassSource, newConfigSource);

        if (_entityIds.Remove(oldName, out var entityId))
        {
            _entityIds[newName] = entityId;
        }
        else
        {
            _entityIds[newName] = Guid.NewGuid();
        }

        return DiagramEditResult.Ok();
```

(This replaces the tail of the existing `RenameEntity` method — the validation checks above this point in the method are unchanged. The `else` branch is defensive: it should be unreachable in practice since `_entityIds` is always populated for every entity in `Current.Entities`, but keeps the method safe if that invariant is ever violated.)

- [ ] **Step 3: Add `EntityId` to `EntityNodeModel`**

Replace the full contents of `src/EfSchemaVisualizer.Web/Diagram/EntityNodeModel.cs` with:

```csharp
using Blazor.Diagrams.Core.Geometry;
using Blazor.Diagrams.Core.Models;
using EfSchemaVisualizer.Core.Model;

namespace EfSchemaVisualizer.Web.Diagram;

public sealed class EntityNodeModel : NodeModel
{
    public EntityNodeModel(EntityModel entity, Guid entityId, Point position) : base(position)
    {
        Entity = entity;
        EntityId = entityId;
        Title = entity.Name;
    }

    public EntityModel Entity { get; }
    public Guid EntityId { get; }
}
```

- [ ] **Step 4: Update `DiagramSync.Rebuild` to match by `EntityId`**

Replace the full contents of `src/EfSchemaVisualizer.Web/Diagram/DiagramSync.cs` with:

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

    public static void Rebuild(BlazorDiagram diagram, DiagramModelResult result, IReadOnlyDictionary<string, Guid> entityIds)
    {
        var previousPositionsById = diagram.Nodes
            .OfType<EntityNodeModel>()
            .ToDictionary(node => node.EntityId, node => node.Position);

        diagram.Nodes.Clear();
        diagram.Links.Clear();

        var nodesByEntityName = new Dictionary<string, EntityNodeModel>();
        var newEntityIndex = 0;

        foreach (var entity in result.Entities)
        {
            var entityId = entityIds[entity.Name];

            Point position;
            if (previousPositionsById.TryGetValue(entityId, out var existingPosition))
            {
                position = existingPosition;
            }
            else
            {
                position = new Point((newEntityIndex % Columns) * XSpacing, (newEntityIndex / Columns) * YSpacing);
                newEntityIndex++;
            }

            var node = new EntityNodeModel(entity, entityId, position);
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

(Key changes from the Phase 1 version: the `entityIds` parameter; `previousPositionsById` keyed by `Guid` instead of a plain position list; `entityId = entityIds[entity.Name]` looked up per entity — this is guaranteed present because `DiagramEditor` keeps `EntityIds` in sync with `Current.Entities` for every mutation, including ones added in later tasks; `newEntityIndex` only increments for entities with no previous position, so multiple new entities grid-place sequentially instead of stacking on the same cell.)

- [ ] **Step 5: Update both `DiagramSync.Rebuild` call sites in `Home.razor`**

In `src/EfSchemaVisualizer.Web/Pages/Home.razor`, `RenderDiagram` currently has:

```csharp
            DiagramSync.Rebuild(diagram, _editor.Current);
```

Change to:

```csharp
            DiagramSync.Rebuild(diagram, _editor.Current, _editor.EntityIds);
```

And `OnDiagramEditedAsync` currently has:

```csharp
        DiagramSync.Rebuild(_diagram, _editor.Current);
```

Change to:

```csharp
        DiagramSync.Rebuild(_diagram, _editor.Current, _editor.EntityIds);
```

- [ ] **Step 6: Build to verify no compile errors**

Run: `dotnet build src/EfSchemaVisualizer.Web/EfSchemaVisualizer.Web.csproj`
Expected: `Build succeeded.`

- [ ] **Step 7: Manual smoke check (regression, not new behavior)**

Publish and serve locally (`dotnet publish src/EfSchemaVisualizer.Web/EfSchemaVisualizer.Web.csproj -c Release -o <output>`, serve `wwwroot`, open in a browser if available in this environment; if not, note that explicitly). Confirm: render the pre-filled sample, drag the "Post" node, rename "Blog" to "Weblog" — the "Post" node must still be at its dragged position and "Weblog" must be at its original position (this is the exact Phase 1 Task 8 scenario; it must still pass with the new `Guid`-based matching).

- [ ] **Step 8: Commit**

```bash
git add src/EfSchemaVisualizer.Web/Diagram/DiagramEditor.cs src/EfSchemaVisualizer.Web/Diagram/EntityNodeModel.cs src/EfSchemaVisualizer.Web/Diagram/DiagramSync.cs src/EfSchemaVisualizer.Web/Pages/Home.razor
git commit -m "Switch diagram position preservation from ordinal to entity-identity matching"
```

---

### Task 2: `DiagramEditor.AddEntity()` / `RemoveEntity(string)`

**Files:**
- Modify: `src/EfSchemaVisualizer.Web/Diagram/DiagramEditor.cs`

**Interfaces:**
- Consumes: `EntityClassRewriter.AddClass(string sourceCode, string className)` / `RemoveClass(string sourceCode, string className)` (existing); `OnModelCreatingRewriter.AddEntity(string sourceCode, string entityName, string dbSetPropertyName)` / `RemoveEntity(string sourceCode, string entityName)` (existing); `DiagramEditor.EntityIds` (Task 1).
- Produces: `public DiagramEditResult AddEntity()` — generates a unique default entity name (`"NewEntity"`, `"NewEntity2"`, ... against `Current.Entities`) and a deterministic DbSet property name (`name + "s"`, guaranteed unique since it's derived 1:1 from the already-unique entity name), adds the class and its `Entity<T>()`/`DbSet<T>` config, assigns it a fresh `Guid` in `EntityIds`, and always succeeds (no user input to reject, so it always returns `Ok()`). `public DiagramEditResult RemoveEntity(string entityName)` — fails if the entity doesn't exist, or if any relationship in `Current.Relationships` references it as principal or dependent (per the Global Constraints scope decision); otherwise removes the class and its config, drops it from `EntityIds`, returns `Ok()`.

- [ ] **Step 1: Add `AddEntity()` and its name generator**

In `src/EfSchemaVisualizer.Web/Diagram/DiagramEditor.cs`, add these methods to the `DiagramEditor` class (place after `ChangePropertyType`, before `SyncSource`):

```csharp
    public DiagramEditResult AddEntity()
    {
        var name = GenerateUniqueEntityName();
        var dbSetPropertyName = name + "s";

        var newClassSource = _classRewriter.AddClass(ClassSource, name);
        var newConfigSource = _configRewriter.AddEntity(ConfigSource, name, dbSetPropertyName);
        Apply(newClassSource, newConfigSource);
        _entityIds[name] = Guid.NewGuid();
        return DiagramEditResult.Ok();
    }

    public DiagramEditResult RemoveEntity(string entityName)
    {
        if (!Current.Entities.Any(e => e.Name == entityName))
        {
            return DiagramEditResult.Fail($"Entity '{entityName}' not found.");
        }

        var blockingRelationship = Current.Relationships.FirstOrDefault(r =>
            r.PrincipalEntity == entityName || r.DependentEntity == entityName);
        if (blockingRelationship is not null)
        {
            var otherEntity = blockingRelationship.PrincipalEntity == entityName
                ? blockingRelationship.DependentEntity
                : blockingRelationship.PrincipalEntity;
            return DiagramEditResult.Fail(
                $"Cannot remove '{entityName}': it has a relationship with '{otherEntity}'. Remove the relationship first.");
        }

        var newClassSource = _classRewriter.RemoveClass(ClassSource, entityName);
        var newConfigSource = _configRewriter.RemoveEntity(ConfigSource, entityName);
        Apply(newClassSource, newConfigSource);
        _entityIds.Remove(entityName);
        return DiagramEditResult.Ok();
    }

    private string GenerateUniqueEntityName()
    {
        if (!Current.Entities.Any(e => e.Name == "NewEntity"))
        {
            return "NewEntity";
        }

        var suffix = 2;
        while (Current.Entities.Any(e => e.Name == $"NewEntity{suffix}"))
        {
            suffix++;
        }

        return $"NewEntity{suffix}";
    }
```

- [ ] **Step 2: Build to verify no compile errors**

Run: `dotnet build src/EfSchemaVisualizer.Web/EfSchemaVisualizer.Web.csproj`
Expected: `Build succeeded.` (Nothing calls these methods yet — this only checks they compile.)

- [ ] **Step 3: Manual trace verification (no automated Web tests exist)**

Since this project has no Web test project, manually trace through the code and record in your report:
1. `AddEntity()` called twice in a row on the default pre-filled sample (`Blog`, `Post`): first call should produce `"NewEntity"`/`"NewEntitys"`; second call should produce `"NewEntity2"`/`"NewEntity2s"` (since `"NewEntity"` now exists in `Current.Entities` after the first `Apply`).
2. `RemoveEntity("Blog")` on the default pre-filled sample (which has `Post.Blog` → `Blog` via `HasOne`/`WithMany`/`HasForeignKey`) should return `Fail` mentioning the relationship with `Post`, and `ClassSource`/`ConfigSource`/`Current` must be unchanged (no `Apply` call reached).
3. `RemoveEntity("NoSuchEntity")` should return `Fail("Entity 'NoSuchEntity' not found.")`.

- [ ] **Step 4: Commit**

```bash
git add src/EfSchemaVisualizer.Web/Diagram/DiagramEditor.cs
git commit -m "Add DiagramEditor.AddEntity and RemoveEntity"
```

---

### Task 3: `DiagramEditor.AddProperty(string)` / `RemoveProperty(string, string)`

**Files:**
- Modify: `src/EfSchemaVisualizer.Web/Diagram/DiagramEditor.cs`

**Interfaces:**
- Consumes: `EntityClassRewriter.AddProperty(string sourceCode, string className, PropertyModel property)` / `RemoveProperty(string sourceCode, string className, string propertyName)` (existing).
- Produces: `public DiagramEditResult AddProperty(string entityName)` — fails if the entity doesn't exist; otherwise generates a unique default property name (`"NewProperty"`, `"NewProperty2"`, ... scoped to that entity's own properties) of type `string`, non-nullable, adds it, returns `Ok()`. `public DiagramEditResult RemoveProperty(string entityName, string propertyName)` — fails if the entity or property doesn't exist, or if the property is part of the entity's key, an index, or a relationship (foreign-key property or navigation property, on either side) — same "refuse rather than silently break" posture as `RemoveEntity`; otherwise removes it, returns `Ok()`.

- [ ] **Step 1: Add `AddProperty` and `RemoveProperty`**

In `src/EfSchemaVisualizer.Web/Diagram/DiagramEditor.cs`, add these methods to the `DiagramEditor` class (place after `RemoveEntity` / `GenerateUniqueEntityName` from Task 2):

```csharp
    public DiagramEditResult AddProperty(string entityName)
    {
        var entity = Current.Entities.FirstOrDefault(e => e.Name == entityName);
        if (entity is null)
        {
            return DiagramEditResult.Fail($"Entity '{entityName}' not found.");
        }

        var propertyName = GenerateUniquePropertyName(entity);
        var newClassSource = _classRewriter.AddProperty(
            ClassSource, entityName, new PropertyModel(propertyName, "string", IsNullable: false, MaxLength: null));
        Apply(newClassSource, ConfigSource);
        return DiagramEditResult.Ok();
    }

    public DiagramEditResult RemoveProperty(string entityName, string propertyName)
    {
        var entity = Current.Entities.FirstOrDefault(e => e.Name == entityName);
        if (entity is null)
        {
            return DiagramEditResult.Fail($"Entity '{entityName}' not found.");
        }

        if (!entity.Properties.Any(p => p.Name == propertyName))
        {
            return DiagramEditResult.Fail($"Property '{propertyName}' not found on '{entityName}'.");
        }

        if (entity.KeyPropertyNames.Contains(propertyName))
        {
            return DiagramEditResult.Fail($"Cannot remove '{propertyName}': it is part of '{entityName}''s key.");
        }

        if (entity.Indexes.Any(i => i.PropertyNames.Contains(propertyName)))
        {
            return DiagramEditResult.Fail($"Cannot remove '{propertyName}': it is used in an index.");
        }

        var blockingRelationship = Current.Relationships.FirstOrDefault(r =>
            (r.DependentEntity == entityName && (r.ForeignKeyProperties.Contains(propertyName) || r.DependentNavigation == propertyName)) ||
            (r.PrincipalEntity == entityName && r.PrincipalNavigation == propertyName));
        if (blockingRelationship is not null)
        {
            return DiagramEditResult.Fail($"Cannot remove '{propertyName}': it is used by a relationship.");
        }

        var newClassSource = _classRewriter.RemoveProperty(ClassSource, entityName, propertyName);
        Apply(newClassSource, ConfigSource);
        return DiagramEditResult.Ok();
    }

    private static string GenerateUniquePropertyName(EntityModel entity)
    {
        if (!entity.Properties.Any(p => p.Name == "NewProperty"))
        {
            return "NewProperty";
        }

        var suffix = 2;
        while (entity.Properties.Any(p => p.Name == $"NewProperty{suffix}"))
        {
            suffix++;
        }

        return $"NewProperty{suffix}";
    }
```

This requires `using EfSchemaVisualizer.Core.Model;` at the top of the file for `EntityModel`/`PropertyModel` — check whether it's already present (it should be, since `DiagramModelResult`/`Current` already uses these types via `DiagramModelBuilder.cs`'s own using); add it if the file doesn't compile without it.

- [ ] **Step 2: Build to verify no compile errors**

Run: `dotnet build src/EfSchemaVisualizer.Web/EfSchemaVisualizer.Web.csproj`
Expected: `Build succeeded.`

- [ ] **Step 3: Manual trace verification**

Record in your report:
1. `AddProperty("Blog")` on the default sample should add a `public string NewProperty { get; set; }` to the `Blog` class.
2. `RemoveProperty("Post", "Id")` on the default sample — `Id` is `Post`'s key (`HasKey(e => e.Id)`) — should return `Fail` mentioning the key.
3. `RemoveProperty("Post", "BlogId")` on the default sample — `BlogId` is the foreign-key property in `HasForeignKey(e => e.BlogId)` — should return `Fail` mentioning the relationship.
4. `RemoveProperty("Post", "Blog")` (the navigation property) should also return `Fail` mentioning the relationship (via `DependentNavigation == "Blog"`).
5. `RemoveProperty("Post", "Title")` (an ordinary, unconfigured property) should succeed.

- [ ] **Step 4: Commit**

```bash
git add src/EfSchemaVisualizer.Web/Diagram/DiagramEditor.cs
git commit -m "Add DiagramEditor.AddProperty and RemoveProperty"
```

---

### Task 4: "+ Entity" toolbar button

**Files:**
- Modify: `src/EfSchemaVisualizer.Web/Pages/Home.razor`

**Interfaces:**
- Consumes: `DiagramEditor.AddEntity()` (Task 2), `OnDiagramEditedAsync` (existing, from Phase 1).

- [ ] **Step 1: Add the toolbar button and its handler**

In `src/EfSchemaVisualizer.Web/Pages/Home.razor`, change:

```razor
<p>
    <button class="btn btn-primary" @onclick="RenderDiagram">Render Diagram</button>
</p>
```

to:

```razor
<p>
    <button class="btn btn-primary" @onclick="RenderDiagram">Render Diagram</button>
    @if (_editContext is not null)
    {
        <button class="btn btn-secondary" @onclick="AddEntity">+ Entity</button>
    }
</p>
```

Add this method to the `@code` block (place it after `SyncEditorSource`):

```csharp
    private async Task AddEntity()
    {
        if (_editor is null)
        {
            return;
        }

        _editor.AddEntity();
        await OnDiagramEditedAsync();
    }
```

- [ ] **Step 2: Build to verify no compile errors**

Run: `dotnet build src/EfSchemaVisualizer.Web/EfSchemaVisualizer.Web.csproj`
Expected: `Build succeeded.`

- [ ] **Step 3: Manual browser verification**

Publish and serve locally. In the browser (or note explicitly if unavailable in this environment):
1. Click "Render Diagram" with the pre-filled sample. Confirm the "+ Entity" button appears next to "Render Diagram".
2. Click "+ Entity". Confirm a new node titled "NewEntity" appears on the diagram (grid-placed, not overlapping "Blog"/"Post"), and the "Entity classes" textarea now contains `public class NewEntity` with an empty body, and the "DbContext / OnModelCreating" textarea now has a `DbSet<NewEntity> NewEntitys` property and an empty `modelBuilder.Entity<NewEntity>(entity => { });` block.
3. Click "+ Entity" again. Confirm a second new node titled "NewEntity2" appears, grid-placed at a different position from "NewEntity" (not stacked on top of it).
4. Drag "Blog" to a new position, then click "+ Entity" once more. Confirm "Blog" stays at its dragged position (position-preservation still works after Task 1's rework).

- [ ] **Step 4: Commit**

```bash
git add src/EfSchemaVisualizer.Web/Pages/Home.razor
git commit -m "Add + Entity toolbar button to the diagram"
```

---

### Task 5: "+ Add property" row and per-property remove "×"

**Files:**
- Modify: `src/EfSchemaVisualizer.Web/Diagram/EntityNode.razor`

**Interfaces:**
- Consumes: `DiagramEditor.AddProperty(string)` / `RemoveProperty(string, string)` (Task 3).

- [ ] **Step 1: Add the "+ Add property" row inside the `<ul>`**

In `src/EfSchemaVisualizer.Web/Diagram/EntityNode.razor`, the `<ul>` currently reads (this is the exact current content — Step 2 below adds one more button inside this same `<li>`, so make both edits together or in either order, they don't conflict):

```razor
    <ul style="list-style: none; margin: 0; padding: 0;">
        @foreach (var property in Node.Entity.Properties)
        {
            var isKey = Node.Entity.KeyPropertyNames.Contains(property.Name);
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
                @if (_propertyErrors.TryGetValue(property.Name, out var propertyError))
                {
                    <div style="color: red; font-size: 0.8em;">@propertyError</div>
                }
            </li>
        }
    </ul>
```

Add a new `<li>` immediately after the `@foreach` block's closing `}` and before the `</ul>`, so the end of the `<ul>` reads:

```razor
                @if (_propertyErrors.TryGetValue(property.Name, out var propertyError))
                {
                    <div style="color: red; font-size: 0.8em;">@propertyError</div>
                }
            </li>
        }
        <li style="padding: 2px 8px;">
            <button type="button" style="font-size: 0.8em;"
                    @onclick="AddProperty"
                    @onpointerdown:stopPropagation="true"
                    @onmousedown:stopPropagation="true">+ Add property</button>
        </li>
    </ul>
```

- [ ] **Step 2: Add a remove "×" button to each property row**

Inside the existing `@foreach` loop's `<li>`, immediately before the existing `@if (_propertyErrors.TryGetValue(...))` block at the end of the `<li>`, add:

```razor
                <button type="button" title="Remove property" style="border: none; background: transparent; cursor: pointer;"
                        @onclick="() => RemoveProperty(property.Name)"
                        @onpointerdown:stopPropagation="true"
                        @onmousedown:stopPropagation="true">×</button>
```

So the `<li>` body's tail (after the nullable `<label>`) reads:

```razor
                <label style="font-size: 0.8em; margin-left: 4px;">
                    <input type="checkbox" checked="@property.IsNullable"
                           @onchange="e => ToggleNullable(property, (bool)(e.Value ?? false))"
                           @onpointerdown:stopPropagation="true"
                           @onmousedown:stopPropagation="true" />
                    nullable
                </label>
                <button type="button" title="Remove property" style="border: none; background: transparent; cursor: pointer;"
                        @onclick="() => RemoveProperty(property.Name)"
                        @onpointerdown:stopPropagation="true"
                        @onmousedown:stopPropagation="true">×</button>
                @if (_propertyErrors.TryGetValue(property.Name, out var propertyError))
                {
                    <div style="color: red; font-size: 0.8em;">@propertyError</div>
                }
```

- [ ] **Step 3: Add the `AddProperty`/`RemoveProperty` handlers**

In the `@code` block, add these methods (place them after `ToggleNullable`, at the end of the block):

```csharp
    private async Task AddProperty()
    {
        var result = EditContext.Editor.AddProperty(Node.Entity.Name);
        if (result.Success)
        {
            await EditContext.NotifyChangedAsync();
        }
    }

    private async Task RemoveProperty(string propertyName)
    {
        var result = EditContext.Editor.RemoveProperty(Node.Entity.Name, propertyName);
        if (result.Success)
        {
            _propertyErrors.Remove(propertyName);
            await EditContext.NotifyChangedAsync();
        }
        else
        {
            _propertyErrors[propertyName] = result.Error!;
        }
    }
```

(`AddProperty` deliberately does not surface `result.Error` anywhere — per Task 2's design, `DiagramEditor.AddProperty(entityName)` can only fail if the entity itself doesn't exist, which cannot happen from this button since it's only rendered inside a node bound to a real `Node.Entity`. If that assumption is ever wrong, the click silently no-ops rather than crashing — acceptable for a case that shouldn't be reachable.)

- [ ] **Step 4: Build to verify no compile errors**

Run: `dotnet build src/EfSchemaVisualizer.Web/EfSchemaVisualizer.Web.csproj`
Expected: `Build succeeded.`

- [ ] **Step 5: Manual browser verification**

Publish and serve locally. In the browser (or note explicitly if unavailable):
1. Render the pre-filled sample. Confirm each node's property list ends with a "+ Add property" row, and each existing property row has a small "×" button.
2. Click "+ Add property" on "Blog". Confirm a new "NewProperty: string" row appears; the "Entity classes" textarea shows the new property added to the `Blog` class.
3. Click "×" next to "Title" on "Blog" (an unconfigured property). Confirm the row disappears and the textarea no longer has that property.
4. Click "×" next to "Id" on "Post" (the key property). Confirm an inline error appears on that row (mentioning the key) and nothing is removed.
5. Click "×" next to "BlogId" on "Post" (the foreign-key property). Confirm an inline error appears (mentioning the relationship) and nothing is removed.

- [ ] **Step 6: Commit**

```bash
git add src/EfSchemaVisualizer.Web/Diagram/EntityNode.razor
git commit -m "Add property add/remove UX to the diagram"
```

---

### Task 6: Node-level remove "×" (entity deletion)

**Files:**
- Modify: `src/EfSchemaVisualizer.Web/Diagram/EntityNode.razor`

**Interfaces:**
- Consumes: `DiagramEditor.RemoveEntity(string)` (Task 2).

- [ ] **Step 1: Restructure the title bar to hold both the name and a remove button**

In `src/EfSchemaVisualizer.Web/Diagram/EntityNode.razor`, change:

```razor
<div class="card" style="width: 260px; border: 1px solid #444;">
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
```

to:

```razor
<div class="card" style="width: 260px; border: 1px solid #444;">
    <div class="card-header" style="font-weight: bold; padding: 4px 8px; background: #eee; display: flex; justify-content: space-between; align-items: center;">
        <span style="flex: 1;">
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
        </span>
        <button type="button" title="Remove entity" style="border: none; background: transparent; cursor: pointer; font-weight: bold;"
                @onclick="RemoveEntity"
                @onpointerdown:stopPropagation="true"
                @onmousedown:stopPropagation="true">×</button>
    </div>
```

(Everything else in the file — the `_nameError` display, the property `<ul>`, and all `@code` members from Task 5 and Phase 1 — is unchanged by this step.)

- [ ] **Step 2: Add the `RemoveEntity` handler**

In the `@code` block, add this method (place it after `OnNameKeyDown`, so it sits near the other title-bar-related handlers):

```csharp
    private async Task RemoveEntity()
    {
        var result = EditContext.Editor.RemoveEntity(Node.Entity.Name);
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
```

- [ ] **Step 3: Build to verify no compile errors**

Run: `dotnet build src/EfSchemaVisualizer.Web/EfSchemaVisualizer.Web.csproj`
Expected: `Build succeeded.`

- [ ] **Step 4: Manual browser verification**

Publish and serve locally. In the browser (or note explicitly if unavailable):
1. Render the pre-filled sample. Confirm both "Blog" and "Post" node title bars show a "×" button next to the name.
2. Click "×" on "Blog". Since `Post.Blog`/`HasForeignKey(e => e.BlogId)` references it, confirm an inline error appears under "Blog"'s title (mentioning the relationship with "Post") and the node is NOT removed.
3. Click "+ Entity" to add "NewEntity" (no relationships), then click its "×". Confirm the node disappears, the "Entity classes" textarea no longer has `class NewEntity`, and the "DbContext / OnModelCreating" textarea no longer has its `DbSet<NewEntity>`/`Entity<NewEntity>(...)` block.
4. Confirm "Blog" and "Post" (and their relationship line/label) are unaffected by removing "NewEntity", and any dragged positions on them are preserved.

- [ ] **Step 5: Commit**

```bash
git add src/EfSchemaVisualizer.Web/Diagram/EntityNode.razor
git commit -m "Add entity remove UX to the diagram"
```

---

### Task 7: Full Phase 2 regression pass

**Files:** none (verification-only task), plus a doc update.

- [ ] **Step 1: Run the full Core test suite**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests`
Expected: PASS, all 277 existing tests (no new `Core` tests this phase — no new `Core` methods were added).

- [ ] **Step 2: Build the whole solution**

Run: `dotnet build`
Expected: `Build succeeded.` for both `EfSchemaVisualizer.Core` and `EfSchemaVisualizer.Web`.

- [ ] **Step 3: End-to-end manual verification**

Publish, serve `wwwroot` locally, and in a real browser if available in this environment (state plainly if not):
1. Render the pre-filled sample, drag "Post" to a new position.
2. Add two new entities via "+ Entity". Confirm they grid-place without overlapping each other or "Blog"/"Post", and "Post" stays at its dragged position throughout both additions.
3. Rename one of the new entities (Phase 1 flow) — confirm it keeps its position after the rename (the Task 1 identity-matching fix).
4. Add a property to one of the new entities, then remove it. Confirm both actions update the textareas correctly.
5. Attempt to remove "Blog" (blocked by relationship) and "Post"'s "Id"/"BlogId"/"Blog" (blocked by key/relationship) — confirm all four are refused with inline errors and nothing is silently corrupted.
6. Remove a new entity with no relationships — confirm it, its `DbSet`, and its `Entity<T>()` block are all gone from both textareas, and the rest of the diagram is unaffected.

Record the outcome (pass/fail per scenario), same convention as Phase 1 Task 8 — state plainly if browser verification isn't possible in this environment rather than asserting success.

- [ ] **Step 4: Update the design doc's Sequencing section**

In `docs/superpowers/specs/2026-07-14-editable-diagram-design.md`, under `## Sequencing`, find item "2. **Add/remove entities and properties.**" and add an "**Update:**" paragraph directly after it (same style as item 1's existing Update note from Phase 1) recording: what was built (list the new `DiagramEditor` methods, the identity-matching rework, the UI additions), the test/build results from Steps 1-2, and the honest state of manual browser verification from Step 3.

- [ ] **Step 5: Commit**

```bash
git add docs/superpowers/specs/2026-07-14-editable-diagram-design.md
git commit -m "Record Phase 2 (add/remove entities and properties) verification results"
```
