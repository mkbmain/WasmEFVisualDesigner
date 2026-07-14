# Editable Diagram Phase 3 (Keys & Indexes) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a user toggle a property's primary-key membership and manage indexes (including composite, multi-property indexes) directly on the ER diagram, with every gesture immediately rewriting the pasted C# source in the two textareas — the third of five phases of the editable-diagram feature described in `docs/superpowers/specs/2026-07-14-editable-diagram-design.md`.

**Architecture:** No new `EfSchemaVisualizer.Core` methods are needed — `OnModelCreatingRewriter.SetKey`/`RemoveKey`/`SetIndex`/`RemoveIndex` already exist and are already unit-tested (see `tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs`), and `EntityModel.KeyPropertyNames`/`Indexes` already flow through `DiagramModelBuilder.Build` into the Razor layer today (currently only rendered as a read-only 🔑 badge). This phase is Web-only: new wrapper methods on `src/EfSchemaVisualizer.Web/Diagram/DiagramEditor.cs` (validate → call `_configRewriter.*` → `Apply(...)`, the same funnel every existing edit method already uses), plus a new "expand-on-click" panel on each property row in `src/EfSchemaVisualizer.Web/Diagram/EntityNode.razor` — the first use of that UX pattern in this codebase (Phases 1-2 only built flat, always-visible rows).

**Tech Stack:** C# / .NET 10, Blazor WebAssembly, Roslyn (`Microsoft.CodeAnalysis.CSharp`) for the already-existing rewriter, xUnit for `Core` tests.

## Global Constraints

- No new `EfSchemaVisualizer.Core` methods in this phase — reuse `OnModelCreatingRewriter.SetKey(string sourceCode, string entityName, IReadOnlyList<string> propertyNames)`, `RemoveKey(string sourceCode, string entityName)`, `SetIndex(string sourceCode, string entityName, IReadOnlyList<string> propertyNames, bool isUnique, string? name = null)`, and `RemoveIndex(string sourceCode, string entityName, IReadOnlyList<string> propertyNames)` exactly as they exist today in `src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs`.
- Every `DiagramEditor` method: validate against `Current.Entities`/`Current.Relationships` first, return `DiagramEditResult.Fail(string)` on any violation, otherwise mutate via the rewriter and call the existing private `Apply(string newClassSource, string newConfigSource)` funnel — never mutate `ClassSource`/`ConfigSource` directly.
- Every interactive Razor element added inside a property row must set `@onpointerdown:stopPropagation="true"` and `@onmousedown:stopPropagation="true"` (matches every existing row control) so it doesn't trigger the diagram's node-drag gesture.
- No modals/popovers — inline editing and inline expand-in-place only, per the design doc's UX conventions.
- There is no `EfSchemaVisualizer.Web` test project (confirmed: `tests/` only contains `EfSchemaVisualizer.Core.Tests`). Matching the precedent set by Phase 2 ("no new Core tests this phase... all new logic lives in the Web project's `DiagramEditor`"), this phase's Web-side C# and Razor changes are verified by `dotnet build` (compiles, no warnings) plus a manual browser pass at the end — not by new automated tests. `Core` needs no new tests since no `Core` methods are added or changed.
- Follow the `DiagramEditResult`/`DiagramEditor` naming and error-message conventions already established (e.g. `$"Entity '{entityName}' not found."`, `$"Property '{propertyName}' not found on '{entityName}'."`).

---

## File Structure

- Modify `src/EfSchemaVisualizer.Web/Diagram/DiagramEditor.cs`: add `ToggleKey`, `AddIndex`, `ToggleIndexMembership`, `SetIndexUnique`, `RenameIndex`, `RemoveIndex`.
- Modify `src/EfSchemaVisualizer.Web/Diagram/EntityNode.razor`: add an expand/collapse chevron per property row, and an expanded panel containing a "Primary key" checkbox and the index-management UI.
- Modify `docs/superpowers/specs/2026-07-14-editable-diagram-design.md`: append a Phase 3 "Update" note under Sequencing, matching the style of the existing Phase 1/2 updates.

---

### Task 1: `DiagramEditor.ToggleKey`

**Files:**
- Modify: `src/EfSchemaVisualizer.Web/Diagram/DiagramEditor.cs`

**Interfaces:**
- Consumes: `Current.Entities` (`IReadOnlyList<EntityModel>`, each with `KeyPropertyNames`/`Properties`), `_configRewriter.SetKey(string, string, IReadOnlyList<string>)` (existing `Core` method), private `Apply(string, string)`.
- Produces: `public DiagramEditResult ToggleKey(string entityName, string propertyName, bool isKey)` — later tasks (the Razor UI in Task 3) call this exact signature.

- [ ] **Step 1: Add the method**

Insert immediately after `RemoveProperty` (after line 231, before the `GenerateUniquePropertyName` private helper) in `src/EfSchemaVisualizer.Web/Diagram/DiagramEditor.cs`:

```csharp
    public DiagramEditResult ToggleKey(string entityName, string propertyName, bool isKey)
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

        var alreadyKey = entity.KeyPropertyNames.Contains(propertyName);
        if (isKey == alreadyKey)
        {
            return DiagramEditResult.Ok();
        }

        var newKeyPropertyNames = isKey
            ? entity.KeyPropertyNames.Append(propertyName).ToList()
            : entity.KeyPropertyNames.Where(name => name != propertyName).ToList();

        if (newKeyPropertyNames.Count == 0)
        {
            return DiagramEditResult.Fail($"'{entityName}' must have at least one key property.");
        }

        var newConfigSource = _configRewriter.SetKey(ConfigSource, entityName, newKeyPropertyNames);
        Apply(ClassSource, newConfigSource);
        return DiagramEditResult.Ok();
    }
```

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build src/EfSchemaVisualizer.Web/EfSchemaVisualizer.Web.csproj`
Expected: `Build succeeded.` with 0 warnings, 0 errors.

- [ ] **Step 3: Commit**

```bash
git add src/EfSchemaVisualizer.Web/Diagram/DiagramEditor.cs
git commit -m "Add DiagramEditor.ToggleKey for diagram-driven primary-key editing"
```

---

### Task 2: `DiagramEditor` index methods

**Files:**
- Modify: `src/EfSchemaVisualizer.Web/Diagram/DiagramEditor.cs`

**Interfaces:**
- Consumes: `Current.Entities[].Indexes` (`IReadOnlyList<IndexModel>`, `IndexModel(IReadOnlyList<string> PropertyNames, bool IsUnique, string? Name = null)` from `EfSchemaVisualizer.Core.Model`), `_configRewriter.SetIndex(string, string, IReadOnlyList<string>, bool, string?)` and `_configRewriter.RemoveIndex(string, string, IReadOnlyList<string>)` (existing `Core` methods).
- Produces: `public DiagramEditResult AddIndex(string entityName, string propertyName)`, `public DiagramEditResult ToggleIndexMembership(string entityName, IReadOnlyList<string> indexPropertyNames, string propertyName, bool include)`, `public DiagramEditResult SetIndexUnique(string entityName, IReadOnlyList<string> propertyNames, bool isUnique)`, `public DiagramEditResult RenameIndex(string entityName, IReadOnlyList<string> propertyNames, string? newName)`, `public DiagramEditResult RemoveIndex(string entityName, IReadOnlyList<string> propertyNames)` — later tasks (Task 4's Razor UI) call these exact signatures.

- [ ] **Step 1: Add the five methods**

Insert immediately after `ToggleKey` (added in Task 1) in `src/EfSchemaVisualizer.Web/Diagram/DiagramEditor.cs`:

```csharp
    public DiagramEditResult AddIndex(string entityName, string propertyName)
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

        if (entity.Indexes.Any(i => i.PropertyNames.SequenceEqual(new[] { propertyName })))
        {
            return DiagramEditResult.Fail($"'{entityName}' already has an index on '{propertyName}'.");
        }

        var newConfigSource = _configRewriter.SetIndex(ConfigSource, entityName, new List<string> { propertyName }, isUnique: false);
        Apply(ClassSource, newConfigSource);
        return DiagramEditResult.Ok();
    }

    public DiagramEditResult ToggleIndexMembership(string entityName, IReadOnlyList<string> indexPropertyNames, string propertyName, bool include)
    {
        var entity = Current.Entities.FirstOrDefault(e => e.Name == entityName);
        if (entity is null)
        {
            return DiagramEditResult.Fail($"Entity '{entityName}' not found.");
        }

        var index = entity.Indexes.FirstOrDefault(i => i.PropertyNames.SequenceEqual(indexPropertyNames));
        if (index is null)
        {
            return DiagramEditResult.Fail($"Index not found on '{entityName}'.");
        }

        var alreadyIncluded = index.PropertyNames.Contains(propertyName);
        if (include == alreadyIncluded)
        {
            return DiagramEditResult.Ok();
        }

        var newPropertyNames = include
            ? index.PropertyNames.Append(propertyName).ToList()
            : index.PropertyNames.Where(name => name != propertyName).ToList();

        if (newPropertyNames.Count == 0)
        {
            var configAfterRemove = _configRewriter.RemoveIndex(ConfigSource, entityName, index.PropertyNames);
            Apply(ClassSource, configAfterRemove);
            return DiagramEditResult.Ok();
        }

        if (entity.Indexes.Any(i => !ReferenceEquals(i, index) && i.PropertyNames.SequenceEqual(newPropertyNames)))
        {
            return DiagramEditResult.Fail($"'{entityName}' already has an index on [{string.Join(", ", newPropertyNames)}].");
        }

        var withoutOldIndex = _configRewriter.RemoveIndex(ConfigSource, entityName, index.PropertyNames);
        var withNewIndex = _configRewriter.SetIndex(withoutOldIndex, entityName, newPropertyNames, index.IsUnique, index.Name);
        Apply(ClassSource, withNewIndex);
        return DiagramEditResult.Ok();
    }

    public DiagramEditResult SetIndexUnique(string entityName, IReadOnlyList<string> propertyNames, bool isUnique)
    {
        var entity = Current.Entities.FirstOrDefault(e => e.Name == entityName);
        if (entity is null)
        {
            return DiagramEditResult.Fail($"Entity '{entityName}' not found.");
        }

        var index = entity.Indexes.FirstOrDefault(i => i.PropertyNames.SequenceEqual(propertyNames));
        if (index is null)
        {
            return DiagramEditResult.Fail($"Index not found on '{entityName}'.");
        }

        if (index.IsUnique == isUnique)
        {
            return DiagramEditResult.Ok();
        }

        var newConfigSource = _configRewriter.SetIndex(ConfigSource, entityName, propertyNames, isUnique, index.Name);
        Apply(ClassSource, newConfigSource);
        return DiagramEditResult.Ok();
    }

    public DiagramEditResult RenameIndex(string entityName, IReadOnlyList<string> propertyNames, string? newName)
    {
        var entity = Current.Entities.FirstOrDefault(e => e.Name == entityName);
        if (entity is null)
        {
            return DiagramEditResult.Fail($"Entity '{entityName}' not found.");
        }

        var index = entity.Indexes.FirstOrDefault(i => i.PropertyNames.SequenceEqual(propertyNames));
        if (index is null)
        {
            return DiagramEditResult.Fail($"Index not found on '{entityName}'.");
        }

        var normalizedName = string.IsNullOrWhiteSpace(newName) ? null : newName.Trim();
        if (normalizedName == index.Name)
        {
            return DiagramEditResult.Ok();
        }

        var newConfigSource = _configRewriter.SetIndex(ConfigSource, entityName, propertyNames, index.IsUnique, normalizedName);
        Apply(ClassSource, newConfigSource);
        return DiagramEditResult.Ok();
    }

    public DiagramEditResult RemoveIndex(string entityName, IReadOnlyList<string> propertyNames)
    {
        var entity = Current.Entities.FirstOrDefault(e => e.Name == entityName);
        if (entity is null)
        {
            return DiagramEditResult.Fail($"Entity '{entityName}' not found.");
        }

        var index = entity.Indexes.FirstOrDefault(i => i.PropertyNames.SequenceEqual(propertyNames));
        if (index is null)
        {
            return DiagramEditResult.Fail($"Index not found on '{entityName}'.");
        }

        var newConfigSource = _configRewriter.RemoveIndex(ConfigSource, entityName, propertyNames);
        Apply(ClassSource, newConfigSource);
        return DiagramEditResult.Ok();
    }
```

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build src/EfSchemaVisualizer.Web/EfSchemaVisualizer.Web.csproj`
Expected: `Build succeeded.` with 0 warnings, 0 errors.

- [ ] **Step 3: Commit**

```bash
git add src/EfSchemaVisualizer.Web/Diagram/DiagramEditor.cs
git commit -m "Add DiagramEditor index management methods (add, toggle membership, unique, rename, remove)"
```

---

### Task 3: Expand/collapse chevron + primary-key checkbox in `EntityNode.razor`

**Files:**
- Modify: `src/EfSchemaVisualizer.Web/Diagram/EntityNode.razor`

**Interfaces:**
- Consumes: `EditContext.Editor.ToggleKey(string, string, bool)` (Task 1).
- Produces: `_expandedPropertyName` field and `ToggleExpand(string propertyName)` method — Task 4 renders its index UI conditionally on `_expandedPropertyName == property.Name`, inside the same expanded block this task creates.

- [ ] **Step 1: Add the chevron button to each property row**

In `src/EfSchemaVisualizer.Web/Diagram/EntityNode.razor`, replace the remove-button line (currently lines 76-79):

```razor
                <button type="button" title="Remove property" style="border: none; background: transparent; cursor: pointer;"
                        @onclick="() => RemoveProperty(property.Name)"
                        @onpointerdown:stopPropagation="true"
                        @onmousedown:stopPropagation="true">×</button>
```

with:

```razor
                <button type="button" title="More options" style="border: none; background: transparent; cursor: pointer;"
                        @onclick="() => ToggleExpand(property.Name)"
                        @onpointerdown:stopPropagation="true"
                        @onmousedown:stopPropagation="true">@(_expandedPropertyName == property.Name ? "▾" : "▸")</button>
                <button type="button" title="Remove property" style="border: none; background: transparent; cursor: pointer;"
                        @onclick="() => RemoveProperty(property.Name)"
                        @onpointerdown:stopPropagation="true"
                        @onmousedown:stopPropagation="true">×</button>
```

- [ ] **Step 2: Add the expanded panel with the primary-key checkbox**

Immediately after the (still-unmodified) property-error block and before the closing `</li>` (currently lines 80-84):

```razor
                @if (_propertyErrors.TryGetValue(property.Name, out var propertyError))
                {
                    <div style="color: red; font-size: 0.8em;">@propertyError</div>
                }
                @if (_expandedPropertyName == property.Name)
                {
                    <div style="margin: 4px 0 4px 16px; padding: 4px 8px; background: #f7f7f7; border-left: 2px solid #ccc;">
                        <label style="font-size: 0.8em; display: block;">
                            <input type="checkbox" checked="@Node.Entity.KeyPropertyNames.Contains(property.Name)"
                                   @onchange="e => ToggleKey(property, (bool)(e.Value ?? false))"
                                   @onpointerdown:stopPropagation="true"
                                   @onmousedown:stopPropagation="true" />
                            primary key
                        </label>
                    </div>
                }
            </li>
```

This replaces the file's existing lines 80-84 (the property-error `@if` plus the bare `</li>`) — keep the property-error block exactly as it was, just add the new `@if (_expandedPropertyName == ...)` block and move `</li>` to follow it. (Task 4 will insert the index UI inside this same `<div>`, after the "primary key" `<label>`.)

- [ ] **Step 3: Add the backing field and handlers in `@code`**

In the `@code` block, immediately after the existing `private readonly Dictionary<string, string> _propertyErrors = new();` line (currently line 168):

```csharp
    private string? _expandedPropertyName;

    private void ToggleExpand(string propertyName)
    {
        _expandedPropertyName = _expandedPropertyName == propertyName ? null : propertyName;
    }

    private async Task ToggleKey(PropertyModel property, bool isKey)
    {
        var result = EditContext.Editor.ToggleKey(Node.Entity.Name, property.Name, isKey);
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

- [ ] **Step 4: Build to verify it compiles**

Run: `dotnet build src/EfSchemaVisualizer.Web/EfSchemaVisualizer.Web.csproj`
Expected: `Build succeeded.` with 0 warnings, 0 errors.

- [ ] **Step 5: Commit**

```bash
git add src/EfSchemaVisualizer.Web/Diagram/EntityNode.razor
git commit -m "Add expand-on-click panel with primary-key toggle to diagram property rows"
```

---

### Task 4: Index management UI inside the expanded panel

**Files:**
- Modify: `src/EfSchemaVisualizer.Web/Diagram/EntityNode.razor`

**Interfaces:**
- Consumes: `Node.Entity.Indexes` (`IReadOnlyList<IndexModel>`), `EditContext.Editor.AddIndex(string, string)`, `.ToggleIndexMembership(string, IReadOnlyList<string>, string, bool)`, `.SetIndexUnique(string, IReadOnlyList<string>, bool)`, `.RenameIndex(string, IReadOnlyList<string>, string?)`, `.RemoveIndex(string, IReadOnlyList<string>)` (all from Task 2).
- Produces: nothing consumed by later phases (this closes out Phase 3).

- [ ] **Step 1: Add the index section inside the expanded panel**

Extend the expanded `<div>` added in Task 3, Step 2 — insert immediately after the "primary key" `<label>` and before the panel's closing `</div>`:

```razor
                        <div style="font-size: 0.8em; margin-top: 4px;">
                            <div>Indexes:</div>
                            @foreach (var index in Node.Entity.Indexes)
                            {
                                <div style="display: flex; align-items: center; gap: 4px; margin: 2px 0;">
                                    <input type="checkbox" checked="@index.PropertyNames.Contains(property.Name)"
                                           @onchange="e => ToggleIndexMembership(property, index, (bool)(e.Value ?? false))"
                                           @onpointerdown:stopPropagation="true"
                                           @onmousedown:stopPropagation="true" />
                                    <span>[@string.Join(", ", index.PropertyNames)]</span>
                                    <label>
                                        <input type="checkbox" checked="@index.IsUnique"
                                               @onchange="e => SetIndexUnique(index, (bool)(e.Value ?? false))"
                                               @onpointerdown:stopPropagation="true"
                                               @onmousedown:stopPropagation="true" />
                                        unique
                                    </label>
                                    <input value="@index.Name" placeholder="(auto name)" style="width: 90px;"
                                           @onchange="e => RenameIndex(index, e.Value?.ToString())"
                                           @onpointerdown:stopPropagation="true"
                                           @onmousedown:stopPropagation="true" />
                                    <button type="button" title="Remove index" style="border: none; background: transparent; cursor: pointer;"
                                            @onclick="() => RemoveIndex(index)"
                                            @onpointerdown:stopPropagation="true"
                                            @onmousedown:stopPropagation="true">×</button>
                                </div>
                            }
                            <button type="button" @onclick="() => AddIndex(property)"
                                    @onpointerdown:stopPropagation="true"
                                    @onmousedown:stopPropagation="true">+ New index on this property</button>
                            @if (_indexError is not null)
                            {
                                <div style="color: red;">@_indexError</div>
                            }
                        </div>
```

- [ ] **Step 2: Add `@using` for `IndexModel`, the backing field, and handlers**

`IndexModel` lives in `EfSchemaVisualizer.Core.Model`, already imported via the file's existing `@using EfSchemaVisualizer.Core.Model` (line 5) — no new `@using` needed.

In the `@code` block, immediately after the `ToggleKey` method added in Task 3, Step 3:

```csharp
    private string? _indexError;

    private async Task AddIndex(PropertyModel property)
    {
        var result = EditContext.Editor.AddIndex(Node.Entity.Name, property.Name);
        if (result.Success)
        {
            _indexError = null;
            await EditContext.NotifyChangedAsync();
        }
        else
        {
            _indexError = result.Error;
        }
    }

    private async Task ToggleIndexMembership(PropertyModel property, IndexModel index, bool include)
    {
        var result = EditContext.Editor.ToggleIndexMembership(Node.Entity.Name, index.PropertyNames, property.Name, include);
        if (result.Success)
        {
            _indexError = null;
            await EditContext.NotifyChangedAsync();
        }
        else
        {
            _indexError = result.Error;
        }
    }

    private async Task SetIndexUnique(IndexModel index, bool isUnique)
    {
        var result = EditContext.Editor.SetIndexUnique(Node.Entity.Name, index.PropertyNames, isUnique);
        if (result.Success)
        {
            _indexError = null;
            await EditContext.NotifyChangedAsync();
        }
        else
        {
            _indexError = result.Error;
        }
    }

    private async Task RenameIndex(IndexModel index, string? newName)
    {
        var result = EditContext.Editor.RenameIndex(Node.Entity.Name, index.PropertyNames, newName);
        if (result.Success)
        {
            _indexError = null;
            await EditContext.NotifyChangedAsync();
        }
        else
        {
            _indexError = result.Error;
        }
    }

    private async Task RemoveIndex(IndexModel index)
    {
        var result = EditContext.Editor.RemoveIndex(Node.Entity.Name, index.PropertyNames);
        if (result.Success)
        {
            _indexError = null;
            await EditContext.NotifyChangedAsync();
        }
        else
        {
            _indexError = result.Error;
        }
    }
```

- [ ] **Step 3: Build to verify it compiles**

Run: `dotnet build src/EfSchemaVisualizer.Web/EfSchemaVisualizer.Web.csproj`
Expected: `Build succeeded.` with 0 warnings, 0 errors.

- [ ] **Step 4: Commit**

```bash
git add src/EfSchemaVisualizer.Web/Diagram/EntityNode.razor
git commit -m "Add composite index management UI to diagram property rows"
```

---

### Task 5: Full-solution verification and design-doc update

**Files:**
- Modify: `docs/superpowers/specs/2026-07-14-editable-diagram-design.md`

**Interfaces:**
- Consumes: nothing new.
- Produces: nothing (final task).

- [ ] **Step 1: Run the full `Core` test suite**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests`
Expected: all tests pass, 0 failed (no new `Core` tests were added this phase, per the Global Constraints — this confirms nothing regressed).

- [ ] **Step 2: Build the whole solution**

Run: `dotnet build`
Expected: `Build succeeded.` with 0 warnings, 0 errors for both `EfSchemaVisualizer.Core` and `EfSchemaVisualizer.Web`.

- [ ] **Step 3: Publish the Web project**

Run: `dotnet publish src/EfSchemaVisualizer.Web/EfSchemaVisualizer.Web.csproj -c Release`
Expected: publish succeeds, producing a `wwwroot` output.

- [ ] **Step 4: Attempt manual browser verification**

Serve the published `wwwroot` locally and open it in a real browser. If a browser is available in this environment, walk through:
1. Render a sample with a single-property key; click the chevron on that property row, confirm the "primary key" checkbox is checked, uncheck it and confirm an inline error appears (last key property can't be removed) and the checkbox reverts.
2. Add a second property, expand it, check "primary key" — confirm the entity now has a composite key (`HasKey(e => new { e.X, e.Y })` in the regenerated source) and both checkboxes are now checked.
3. Expand a property and click "+ New index on this property" — confirm a single-property `HasIndex` call appears in the regenerated source and a new row appears in every property's expanded index list.
4. Expand a second property and check that same index's membership checkbox — confirm the index becomes composite in the regenerated source (`HasIndex(e => new { e.A, e.B })`), then uncheck it back to confirm it reverts to single-property.
5. Toggle an index's "unique" checkbox and edit its name field — confirm both changes appear in the regenerated source (`.IsUnique()` and the name argument).
6. Remove an index via its "×" button — confirm the `HasIndex` statement is fully gone from the regenerated source.

If this sandbox has no browser available (consistent with every prior phase — `which chromium chromium-browser google-chrome firefox node npx playwright` all reporting not found), record that explicitly rather than claiming verification occurred.

- [ ] **Step 5: Record the phase update in the design doc**

In `docs/superpowers/specs/2026-07-14-editable-diagram-design.md`, under the Sequencing section's numbered list, replace item 3 (currently just `3. **Keys and indexes.** Key-toggle on a property row; index add/remove in the row's expand-on-click area.`) with an entry following the same "**Update:**" style as items 1 and 2, summarizing what was built (`DiagramEditor.ToggleKey`/`AddIndex`/`ToggleIndexMembership`/`SetIndexUnique`/`RenameIndex`/`RemoveIndex`, the new expand-on-click panel in `EntityNode.razor`), the verification commands run and their results, and whether interactive browser verification was possible (matching the phrasing precedent of Phase 1/2's entries).

- [ ] **Step 6: Commit**

```bash
git add docs/superpowers/specs/2026-07-14-editable-diagram-design.md
git commit -m "Record Phase 3 (keys and indexes) verification results"
```

---

## Self-Review

**Spec coverage:** Design doc's Phase 3 line — "Key-toggle on a property row; index add/remove in the row's expand-on-click area" — is covered: Task 3 builds the expand-on-click mechanism (new to this codebase) plus the key toggle; Task 4 builds index add/remove/membership/unique/rename inside that same panel, supporting full composite-index authoring (per the user's explicit choice to build the expand panel now rather than defer to single-property-only).

**Placeholder scan:** No TBD/TODO markers; every step shows complete, concrete code.

**Type consistency:** `ToggleKey(string entityName, string propertyName, bool isKey)` (Task 1) matches its Task 3 call site exactly. `AddIndex(string, string)`, `ToggleIndexMembership(string, IReadOnlyList<string>, string, bool)`, `SetIndexUnique(string, IReadOnlyList<string>, bool)`, `RenameIndex(string, IReadOnlyList<string>, string?)`, `RemoveIndex(string, IReadOnlyList<string>)` (Task 2) match their Task 4 call sites exactly, all passing `index.PropertyNames` (an `IReadOnlyList<string>` per `IndexModel`'s definition in `src/EfSchemaVisualizer.Core/Model/IndexModel.cs`).
