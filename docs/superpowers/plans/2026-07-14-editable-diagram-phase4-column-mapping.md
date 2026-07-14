# Editable Diagram Phase 4 (Column/Table Mapping, Precision, Default Values) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a user edit a property's column name, column type, precision/scale, and default value, plus an entity's table name/schema, directly on the ER diagram — the fourth of five phases of the editable-diagram feature described in `docs/superpowers/specs/2026-07-14-editable-diagram-design.md`.

**Architecture:** No new `EfSchemaVisualizer.Core` methods are needed — `OnModelCreatingRewriter.SetTable`/`RemoveTable`, `SetColumnName`/`RemoveColumnName`, `SetColumnType`/`RemoveColumnType`, `RewritePrecision`/`RemovePrecision`, and `SetDefaultValue`/`RemoveDefaultValue` already exist and are already unit-tested. `EntityModel.TableName`/`Schema` and `PropertyModel.ColumnName`/`ColumnType`/`Precision`/`Scale`/`DefaultValueLiteral` already flow through `DiagramModelBuilder.Build` into the Razor layer today (currently unused by any UI). This phase is Web-only: five new wrapper methods on `src/EfSchemaVisualizer.Web/Diagram/DiagramEditor.cs` (validate → call `_configRewriter.*` → `Apply(...)`, the same funnel every existing edit method uses), plus new always-visible input fields — one new row in the node header for table/schema, and five new rows inside the Phase-3 expand-on-click panel for column name/type/precision/scale/default value. Unlike Phase 3's index UI, these new fields are simple independent scalars (no "add a new one" gesture, no membership concept), so each field commits on `@onchange` exactly like Phase 3's index-name/unique-checkbox fields — no dblclick-to-edit toggle state is needed.

**Tech Stack:** C# / .NET 10, Blazor WebAssembly, Roslyn (`Microsoft.CodeAnalysis.CSharp`) for the already-existing rewriter, xUnit for `Core` tests.

## Global Constraints

- No new `EfSchemaVisualizer.Core` methods in this phase — reuse exactly as they exist today in `src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs`:
  - `SetTable(string sourceCode, string entityName, string tableName, string? schema)` / `RemoveTable(string sourceCode, string entityName)`
  - `SetColumnName(string sourceCode, string entityName, string propertyName, string columnName)` / `RemoveColumnName(string sourceCode, string entityName, string propertyName)`
  - `SetColumnType(string sourceCode, string entityName, string propertyName, string columnType)` / `RemoveColumnType(string sourceCode, string entityName, string propertyName)`
  - `RewritePrecision(string sourceCode, string entityName, string propertyName, int precision, int? scale)` / `RemovePrecision(string sourceCode, string entityName, string propertyName)`
  - `SetDefaultValue(string sourceCode, string entityName, string propertyName, string literalText)` / `RemoveDefaultValue(string sourceCode, string entityName, string propertyName)`
- `PropertyModel` (`src/EfSchemaVisualizer.Core/Model/PropertyModel.cs`) fields used this phase: `ColumnName` (`string?`), `ColumnType` (`string?`), `Precision` (`int?`), `Scale` (`int?`), `DefaultValueLiteral` (`string?`). `EntityModel` (`src/EfSchemaVisualizer.Core/Model/EntityModel.cs`) fields used: `TableName` (`string?`), `Schema` (`string?`).
- `PropertyModel.DefaultValueLiteral` is stored **with quotes already embedded for string literals** (e.g. `"active"` round-trips as the 8-character text `"active"` including the quote characters, since it comes from `LiteralExpressionSyntax.ToString()`), and `SetDefaultValue`'s `literalText` parameter expects exactly that same pre-quoted form. Treat the default-value field as an opaque raw-text round-trip — never add or strip quotes based on `ClrType`.
- Every `DiagramEditor` method: validate against `Current.Entities` first, return `DiagramEditResult.Fail(string)` on any violation, otherwise mutate via the rewriter and call the existing private `Apply(string newClassSource, string newConfigSource)` funnel — never mutate `ClassSource`/`ConfigSource` directly.
- A blank/whitespace-only input for any of these string fields means "clear the mapping" (call the `Remove*` rewriter method), not "set it to an empty string literal."
- Every interactive Razor element added inside a property row or the node header must set `@onpointerdown:stopPropagation="true"` and `@onmousedown:stopPropagation="true"` (matches every existing row/header control) so it doesn't trigger the diagram's node-drag gesture.
- No modals/popovers — inline, always-visible fields only, matching Phase 3's index-field precedent (not Phase 1's dblclick-to-edit-toggle precedent, which does not compose safely when two fields — like precision and scale — must be committed together; see Task 3's rationale).
- There is no `EfSchemaVisualizer.Web` test project. This phase's Web-side C# and Razor changes are verified by `dotnet build` (compiles, no warnings) plus a manual browser pass at the end — not by new automated tests. `Core` needs no new tests since no `Core` methods are added or changed.
- Follow the `DiagramEditResult`/`DiagramEditor` naming and error-message conventions already established (e.g. `$"Entity '{entityName}' not found."`, `$"Property '{propertyName}' not found on '{entityName}'."`).

---

## File Structure

- Modify `src/EfSchemaVisualizer.Web/Diagram/DiagramEditor.cs`: add `SetTableMapping`, `SetColumnName`, `SetColumnType`, `SetPrecision`, `SetDefaultValue`, and a private `IsValidExpressionText` helper.
- Modify `src/EfSchemaVisualizer.Web/Diagram/EntityNode.razor`: add a table/schema row to the node header, and column name/type/precision/scale/default-value rows inside the existing expand-on-click panel.
- Modify `docs/superpowers/specs/2026-07-14-editable-diagram-design.md`: append a Phase 4 "Update" note under Sequencing, matching the style of the existing Phase 1-3 updates.

---

### Task 1: `DiagramEditor.SetTableMapping`

**Files:**
- Modify: `src/EfSchemaVisualizer.Web/Diagram/DiagramEditor.cs`

**Interfaces:**
- Consumes: `Current.Entities` (`IReadOnlyList<EntityModel>`, each with `TableName`/`Schema`), `_configRewriter.SetTable(string, string, string, string?)` and `_configRewriter.RemoveTable(string, string)` (existing `Core` methods), private `Apply(string, string)`.
- Produces: `public DiagramEditResult SetTableMapping(string entityName, string? tableName, string? schema)` — Task 4's Razor UI calls this exact signature.

- [ ] **Step 1: Add the method**

Insert immediately after `RemoveIndex` (the last method added in Phase 3, currently ending at line 397 in `src/EfSchemaVisualizer.Web/Diagram/DiagramEditor.cs`), before the private `GenerateUniquePropertyName` helper:

```csharp
    public DiagramEditResult SetTableMapping(string entityName, string? tableName, string? schema)
    {
        var entity = Current.Entities.FirstOrDefault(e => e.Name == entityName);
        if (entity is null)
        {
            return DiagramEditResult.Fail($"Entity '{entityName}' not found.");
        }

        var normalizedTableName = string.IsNullOrWhiteSpace(tableName) ? null : tableName.Trim();
        var normalizedSchema = string.IsNullOrWhiteSpace(schema) ? null : schema.Trim();

        if (normalizedTableName == entity.TableName && normalizedSchema == entity.Schema)
        {
            return DiagramEditResult.Ok();
        }

        if (normalizedTableName is null)
        {
            var clearedConfigSource = _configRewriter.RemoveTable(ConfigSource, entityName);
            Apply(ClassSource, clearedConfigSource);
            return DiagramEditResult.Ok();
        }

        var newConfigSource = _configRewriter.SetTable(ConfigSource, entityName, normalizedTableName, normalizedSchema);
        Apply(ClassSource, newConfigSource);
        return DiagramEditResult.Ok();
    }
```

Note: a blank table name always clears the whole mapping (including schema), since `ToTable` requires a table name argument — there is no rewriter method that sets a schema without a table name. This mirrors how `SetTable`'s underlying `ToTable(...)` call is structured in `Core`.

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build src/EfSchemaVisualizer.Web/EfSchemaVisualizer.Web.csproj`
Expected: `Build succeeded.` with 0 warnings, 0 errors.

- [ ] **Step 3: Commit**

```bash
git add src/EfSchemaVisualizer.Web/Diagram/DiagramEditor.cs
git commit -m "Add DiagramEditor.SetTableMapping for diagram-driven table/schema editing"
```

---

### Task 2: `DiagramEditor.SetColumnName` and `SetColumnType`

**Files:**
- Modify: `src/EfSchemaVisualizer.Web/Diagram/DiagramEditor.cs`

**Interfaces:**
- Consumes: `Current.Entities[].Properties` (`IReadOnlyList<PropertyModel>`, each with `ColumnName`/`ColumnType`), `_configRewriter.SetColumnName`/`RemoveColumnName`/`SetColumnType`/`RemoveColumnType` (existing `Core` methods).
- Produces: `public DiagramEditResult SetColumnName(string entityName, string propertyName, string? columnName)`, `public DiagramEditResult SetColumnType(string entityName, string propertyName, string? columnType)` — Task 5's Razor UI calls these exact signatures.

- [ ] **Step 1: Add the two methods**

Insert immediately after `SetTableMapping` (added in Task 1):

```csharp
    public DiagramEditResult SetColumnName(string entityName, string propertyName, string? columnName)
    {
        var entity = Current.Entities.FirstOrDefault(e => e.Name == entityName);
        if (entity is null)
        {
            return DiagramEditResult.Fail($"Entity '{entityName}' not found.");
        }

        var property = entity.Properties.FirstOrDefault(p => p.Name == propertyName);
        if (property is null)
        {
            return DiagramEditResult.Fail($"Property '{propertyName}' not found on '{entityName}'.");
        }

        var normalizedColumnName = string.IsNullOrWhiteSpace(columnName) ? null : columnName.Trim();
        if (normalizedColumnName == property.ColumnName)
        {
            return DiagramEditResult.Ok();
        }

        var newConfigSource = normalizedColumnName is null
            ? _configRewriter.RemoveColumnName(ConfigSource, entityName, propertyName)
            : _configRewriter.SetColumnName(ConfigSource, entityName, propertyName, normalizedColumnName);
        Apply(ClassSource, newConfigSource);
        return DiagramEditResult.Ok();
    }

    public DiagramEditResult SetColumnType(string entityName, string propertyName, string? columnType)
    {
        var entity = Current.Entities.FirstOrDefault(e => e.Name == entityName);
        if (entity is null)
        {
            return DiagramEditResult.Fail($"Entity '{entityName}' not found.");
        }

        var property = entity.Properties.FirstOrDefault(p => p.Name == propertyName);
        if (property is null)
        {
            return DiagramEditResult.Fail($"Property '{propertyName}' not found on '{entityName}'.");
        }

        var normalizedColumnType = string.IsNullOrWhiteSpace(columnType) ? null : columnType.Trim();
        if (normalizedColumnType == property.ColumnType)
        {
            return DiagramEditResult.Ok();
        }

        var newConfigSource = normalizedColumnType is null
            ? _configRewriter.RemoveColumnType(ConfigSource, entityName, propertyName)
            : _configRewriter.SetColumnType(ConfigSource, entityName, propertyName, normalizedColumnType);
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
git commit -m "Add DiagramEditor.SetColumnName and SetColumnType for diagram-driven column mapping"
```

---

### Task 3: `DiagramEditor.SetPrecision` and `SetDefaultValue`

**Files:**
- Modify: `src/EfSchemaVisualizer.Web/Diagram/DiagramEditor.cs`

**Interfaces:**
- Consumes: `Current.Entities[].Properties` (`Precision`/`Scale`/`DefaultValueLiteral`), `_configRewriter.RewritePrecision`/`RemovePrecision`/`SetDefaultValue`/`RemoveDefaultValue` (existing `Core` methods), `Microsoft.CodeAnalysis.CSharp.SyntaxFactory` (already imported in this file for `IsValidTypeToken`).
- Produces: `public DiagramEditResult SetPrecision(string entityName, string propertyName, int? precision, int? scale)`, `public DiagramEditResult SetDefaultValue(string entityName, string propertyName, string? literalText)` — Task 5's Razor UI calls these exact signatures.

- [ ] **Step 1: Add the two methods and the validation helper**

Insert immediately after `SetColumnType` (added in Task 2):

```csharp
    public DiagramEditResult SetPrecision(string entityName, string propertyName, int? precision, int? scale)
    {
        var entity = Current.Entities.FirstOrDefault(e => e.Name == entityName);
        if (entity is null)
        {
            return DiagramEditResult.Fail($"Entity '{entityName}' not found.");
        }

        var property = entity.Properties.FirstOrDefault(p => p.Name == propertyName);
        if (property is null)
        {
            return DiagramEditResult.Fail($"Property '{propertyName}' not found on '{entityName}'.");
        }

        if (precision is null && scale is not null)
        {
            return DiagramEditResult.Fail("Scale cannot be set without precision.");
        }

        if (precision is not null && precision <= 0)
        {
            return DiagramEditResult.Fail("Precision must be a positive number.");
        }

        if (scale is not null && scale < 0)
        {
            return DiagramEditResult.Fail("Scale cannot be negative.");
        }

        if (precision == property.Precision && scale == property.Scale)
        {
            return DiagramEditResult.Ok();
        }

        var newConfigSource = precision is null
            ? _configRewriter.RemovePrecision(ConfigSource, entityName, propertyName)
            : _configRewriter.RewritePrecision(ConfigSource, entityName, propertyName, precision.Value, scale);
        Apply(ClassSource, newConfigSource);
        return DiagramEditResult.Ok();
    }

    public DiagramEditResult SetDefaultValue(string entityName, string propertyName, string? literalText)
    {
        var entity = Current.Entities.FirstOrDefault(e => e.Name == entityName);
        if (entity is null)
        {
            return DiagramEditResult.Fail($"Entity '{entityName}' not found.");
        }

        var property = entity.Properties.FirstOrDefault(p => p.Name == propertyName);
        if (property is null)
        {
            return DiagramEditResult.Fail($"Property '{propertyName}' not found on '{entityName}'.");
        }

        var normalizedLiteral = string.IsNullOrWhiteSpace(literalText) ? null : literalText.Trim();
        if (normalizedLiteral == property.DefaultValueLiteral)
        {
            return DiagramEditResult.Ok();
        }

        if (normalizedLiteral is not null && !IsValidExpressionText(normalizedLiteral))
        {
            return DiagramEditResult.Fail($"'{normalizedLiteral}' is not a valid default value expression.");
        }

        var newConfigSource = normalizedLiteral is null
            ? _configRewriter.RemoveDefaultValue(ConfigSource, entityName, propertyName)
            : _configRewriter.SetDefaultValue(ConfigSource, entityName, propertyName, normalizedLiteral);
        Apply(ClassSource, newConfigSource);
        return DiagramEditResult.Ok();
    }
```

Then add the validation helper immediately after the existing private `IsValidTypeToken` method (the last method in the file):

```csharp
    private static bool IsValidExpressionText(string text)
    {
        var expression = SyntaxFactory.ParseExpression(text);
        return expression.ToFullString() == text && !expression.ContainsDiagnostics;
    }
```

This mirrors `IsValidTypeToken`'s existing shape exactly (parse, round-trip-compare, check diagnostics) — `SetDefaultValue`'s underlying `Core` method (`OnModelCreatingRewriter.SetDefaultValue`) parses `literalText` via `SyntaxFactory.ParseExpression` with no validation of its own, so this check must happen in `DiagramEditor` before calling it, the same way `ChangePropertyType` validates the type text before calling into `Core`.

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build src/EfSchemaVisualizer.Web/EfSchemaVisualizer.Web.csproj`
Expected: `Build succeeded.` with 0 warnings, 0 errors.

- [ ] **Step 3: Commit**

```bash
git add src/EfSchemaVisualizer.Web/Diagram/DiagramEditor.cs
git commit -m "Add DiagramEditor.SetPrecision and SetDefaultValue for diagram-driven column detail editing"
```

---

### Task 4: Table/schema fields in the node header

**Files:**
- Modify: `src/EfSchemaVisualizer.Web/Diagram/EntityNode.razor`

**Interfaces:**
- Consumes: `EditContext.Editor.SetTableMapping(string, string?, string?)` (Task 1).
- Produces: `_tableError` field, `CommitTableName`/`CommitSchema` methods — no later task depends on these (this closes out the entity-level part of Phase 4).

- [ ] **Step 1: Add the table/schema row to the markup**

In `src/EfSchemaVisualizer.Web/Diagram/EntityNode.razor`, insert a new `<div>` immediately after the closing `</div>` of `card-header` (currently line 28) and before the `@if (_nameError is not null)` block (currently line 29):

```razor
    <div style="padding: 2px 8px; font-size: 0.75em; color: #555; display: flex; align-items: center; gap: 4px;">
        <span>Table:</span>
        <input style="width: 80px;" value="@Node.Entity.TableName" placeholder="(default)"
               @onchange="e => CommitTableName(e.Value?.ToString())"
               @onpointerdown:stopPropagation="true"
               @onmousedown:stopPropagation="true" />
        <span>Schema:</span>
        <input style="width: 60px;" value="@Node.Entity.Schema" placeholder="(none)"
               @onchange="e => CommitSchema(e.Value?.ToString())"
               @onpointerdown:stopPropagation="true"
               @onmousedown:stopPropagation="true" />
    </div>
    @if (_tableError is not null)
    {
        <div style="color: red; font-size: 0.8em; padding: 0 8px;">@_tableError</div>
    }
```

This follows Phase 3's index-field convention (always-visible input, commits via `@onchange`) rather than Phase 1's dblclick-to-edit-toggle convention used for the entity name — table name and schema are edited as two independent fields that each commit on their own, always pairing with the other field's current value from `Node.Entity` directly, so there is no risk of one field's blur prematurely committing a stale value for the other (the bug a shared "edit mode" toggle would introduce for two simultaneously-open inputs).

- [ ] **Step 2: Add the backing field and handlers in `@code`**

In the `@code` block, immediately after the existing `RemoveEntity` method (currently ending at line 215), before the `_editingPropertyName` field:

```csharp
    private string? _tableError;

    private async Task CommitTableName(string? newTableName)
    {
        var result = EditContext.Editor.SetTableMapping(Node.Entity.Name, newTableName, Node.Entity.Schema);
        if (result.Success)
        {
            _tableError = null;
            await EditContext.NotifyChangedAsync();
        }
        else
        {
            _tableError = result.Error;
        }
    }

    private async Task CommitSchema(string? newSchema)
    {
        var result = EditContext.Editor.SetTableMapping(Node.Entity.Name, Node.Entity.TableName, newSchema);
        if (result.Success)
        {
            _tableError = null;
            await EditContext.NotifyChangedAsync();
        }
        else
        {
            _tableError = result.Error;
        }
    }
```

- [ ] **Step 3: Build to verify it compiles**

Run: `dotnet build src/EfSchemaVisualizer.Web/EfSchemaVisualizer.Web.csproj`
Expected: `Build succeeded.` with 0 warnings, 0 errors.

- [ ] **Step 4: Commit**

```bash
git add src/EfSchemaVisualizer.Web/Diagram/EntityNode.razor
git commit -m "Add table/schema editing row to diagram entity node header"
```

---

### Task 5: Column name/type/precision/scale/default-value fields in the expand panel

**Files:**
- Modify: `src/EfSchemaVisualizer.Web/Diagram/EntityNode.razor`

**Interfaces:**
- Consumes: `Node.Entity.Properties[].ColumnName`/`ColumnType`/`Precision`/`Scale`/`DefaultValueLiteral`, `EditContext.Editor.SetColumnName(string, string, string?)`, `.SetColumnType(string, string, string?)`, `.SetPrecision(string, string, int?, int?)`, `.SetDefaultValue(string, string, string?)` (all from Tasks 2-3).
- Produces: nothing consumed by later phases (this closes out Phase 4).

- [ ] **Step 1: Add the new fields inside the expand panel**

In `src/EfSchemaVisualizer.Web/Diagram/EntityNode.razor`, insert a new `<div>` inside the Phase-3 expand panel, immediately after the "primary key" `<label>` closes (currently line 97) and before the "Indexes:" `<div>` starts (currently line 98):

```razor
                        <div style="font-size: 0.8em; margin-top: 4px;">
                            <label style="display: block;">
                                Column name:
                                <input style="width: 100px;" value="@property.ColumnName" placeholder="(same as property)"
                                       @onchange="e => CommitColumnName(property, e.Value?.ToString())"
                                       @onpointerdown:stopPropagation="true"
                                       @onmousedown:stopPropagation="true" />
                            </label>
                            <label style="display: block;">
                                Column type:
                                <input style="width: 100px;" value="@property.ColumnType" placeholder="(default)"
                                       @onchange="e => CommitColumnType(property, e.Value?.ToString())"
                                       @onpointerdown:stopPropagation="true"
                                       @onmousedown:stopPropagation="true" />
                            </label>
                            <label style="display: block;">
                                Precision:
                                <input type="number" style="width: 50px;" value="@property.Precision"
                                       @onchange="e => CommitPrecision(property, ParseNullableInt(e.Value?.ToString()))"
                                       @onpointerdown:stopPropagation="true"
                                       @onmousedown:stopPropagation="true" />
                                Scale:
                                <input type="number" style="width: 50px;" value="@property.Scale"
                                       @onchange="e => CommitScale(property, ParseNullableInt(e.Value?.ToString()))"
                                       @onpointerdown:stopPropagation="true"
                                       @onmousedown:stopPropagation="true" />
                            </label>
                            <label style="display: block;">
                                Default value:
                                <input style="width: 100px;" value="@property.DefaultValueLiteral" placeholder="(none)"
                                       @onchange="e => CommitDefaultValue(property, e.Value?.ToString())"
                                       @onpointerdown:stopPropagation="true"
                                       @onmousedown:stopPropagation="true" />
                            </label>
                        </div>
```

Precision and scale each commit independently via their own `@onchange`, always pairing with the *other* field's current committed value read from `property.Precision`/`property.Scale` — so there is no shared edit-buffer and no risk of one field's commit clobbering the other's in-progress edit, the same reasoning as Task 4's table/schema fields.

Errors from these five new handlers reuse the existing `_propertyErrors` dictionary (already rendered at lines 84-87, just above this panel in the same `<li>`) rather than introducing a new error-display slot — matching how Phase 3's `ToggleKey` reused the same dictionary.

- [ ] **Step 2: Add the handlers and the int-parsing helper in `@code`**

In the `@code` block, immediately after the existing `ToggleNullable` method (currently ending at line 419), before `AddProperty`:

```csharp
    private async Task CommitColumnName(PropertyModel property, string? newColumnName)
    {
        var result = EditContext.Editor.SetColumnName(Node.Entity.Name, property.Name, newColumnName);
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

    private async Task CommitColumnType(PropertyModel property, string? newColumnType)
    {
        var result = EditContext.Editor.SetColumnType(Node.Entity.Name, property.Name, newColumnType);
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

    private async Task CommitPrecision(PropertyModel property, int? newPrecision)
    {
        var result = EditContext.Editor.SetPrecision(Node.Entity.Name, property.Name, newPrecision, property.Scale);
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

    private async Task CommitScale(PropertyModel property, int? newScale)
    {
        var result = EditContext.Editor.SetPrecision(Node.Entity.Name, property.Name, property.Precision, newScale);
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

    private async Task CommitDefaultValue(PropertyModel property, string? newLiteralText)
    {
        var result = EditContext.Editor.SetDefaultValue(Node.Entity.Name, property.Name, newLiteralText);
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

    private static int? ParseNullableInt(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return int.TryParse(text, out var value) ? value : null;
    }
```

`ParseNullableInt` treats an unparseable value the same as a blank one (clears the field) rather than surfacing a separate parse error — the `<input type="number">` browser control already constrains entry to numeric text or empty, so this fallback path is a defensive no-op in practice, not a user-facing gap.

- [ ] **Step 3: Build to verify it compiles**

Run: `dotnet build src/EfSchemaVisualizer.Web/EfSchemaVisualizer.Web.csproj`
Expected: `Build succeeded.` with 0 warnings, 0 errors.

- [ ] **Step 4: Commit**

```bash
git add src/EfSchemaVisualizer.Web/Diagram/EntityNode.razor
git commit -m "Add column name/type/precision/scale/default-value editing to diagram property rows"
```

---

### Task 6: Full-solution verification and design-doc update

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
1. Render a sample entity; set its table name and schema via the new header fields, confirm `.ToTable("Name", "schema")` appears in the regenerated source; clear the table name field and confirm the whole `.ToTable(...)` call disappears (schema clears with it).
2. Expand a property; set its column name and column type, confirm `.HasColumnName(...)` / `.HasColumnType(...)` appear; clear each field back to blank and confirm the calls disappear.
3. Set precision only (no scale) on a decimal property, confirm `.HasPrecision(N)` appears with one argument; then set a scale too, confirm it becomes `.HasPrecision(N, M)`; clear precision and confirm the whole call disappears (including any scale).
4. Set a default value on a string property using a quoted literal (e.g. `"active"` typed including the quote characters) and confirm `.HasDefaultValue("active")` appears in the regenerated source unchanged; set a numeric default (e.g. `5`) on an int property and confirm `.HasDefaultValue(5)` appears; try an invalid expression (e.g. unbalanced parentheses) and confirm an inline error appears instead of corrupting the source.

If this sandbox has no browser available (consistent with every prior phase — `which chromium chromium-browser google-chrome firefox node npx playwright` all reporting not found), record that explicitly rather than claiming verification occurred.

- [ ] **Step 5: Record the phase update in the design doc**

In `docs/superpowers/specs/2026-07-14-editable-diagram-design.md`, under the Sequencing section's numbered list, replace item 4 (currently just `4. **Column/table mapping, precision, default values.** Remaining expand-on-click fields, plus entity-level table/schema in the node header.`) with an entry following the same "**Update:**" style as items 1-3, summarizing what was built (`DiagramEditor.SetTableMapping`/`SetColumnName`/`SetColumnType`/`SetPrecision`/`SetDefaultValue`, the new header row and expand-panel fields in `EntityNode.razor`), the verification commands run and their results, and whether interactive browser verification was possible (matching the phrasing precedent of Phases 1-3's entries).

- [ ] **Step 6: Commit**

```bash
git add docs/superpowers/specs/2026-07-14-editable-diagram-design.md
git commit -m "Record Phase 4 (column/table mapping, precision, default values) verification results"
```

---

## Self-Review

**Spec coverage:** Design doc's Phase 4 line — "Remaining expand-on-click fields, plus entity-level table/schema in the node header" — is covered: Task 4 builds the entity-level table/schema header row; Task 5 builds the remaining expand-on-click fields (column name, column type, precision, scale, default value), the last fields the design doc's Architecture section lists ("reveal key toggle, column name/type, precision/scale, default value, and index membership" — key toggle and index membership were Phase 3; this phase completes the rest of that list).

**Placeholder scan:** No TBD/TODO markers; every step shows complete, concrete code.

**Type consistency:** `SetTableMapping(string entityName, string? tableName, string? schema)` (Task 1) matches its Task 4 call sites (`CommitTableName`/`CommitSchema`) exactly. `SetColumnName(string, string, string?)`/`SetColumnType(string, string, string?)` (Task 2) and `SetPrecision(string, string, int?, int?)`/`SetDefaultValue(string, string, string?)` (Task 3) all match their Task 5 call sites exactly, including `ParseNullableInt`'s `int?` return type flowing straight into `SetPrecision`'s `int?` parameters.
