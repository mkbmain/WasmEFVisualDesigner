# Diagram Fill-Height + Fullscreen Toggle Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the diagram canvas on the home page fill the remaining window height by default, and add a fullscreen toggle that expands it to cover the whole viewport with a minimal floating toolbar.

**Architecture:** Replace the diagram container's fixed `height: 600px` inline style with a scoped `Home.razor.css` class (`diagram-panel`) that flexes to fill available vertical space. Add an `_isFullscreen` bool to `Home.razor` that toggles a `fullscreen` CSS class (a `position: fixed` viewport overlay) and swaps the visible controls: normal mode shows the full page (editors/toolbar/instructions), fullscreen mode hides all of that behind an `@if` and shows only a small floating toolbar (Undo/Redo/Auto-layout/Zoom to fit/Exit) plus any error text. Escape exits fullscreen via the existing `keyboardShortcuts.js` keydown listener.

**Tech Stack:** Blazor WebAssembly (.NET 10), `Z.Blazor.Diagrams` 3.0.4.1 (its `DiagramCanvas` already uses a `ResizeObserver` internally, so CSS-driven container resizes are picked up automatically — no extra JS interop needed for the diagram itself).

## Global Constraints

- No bUnit/Razor-component test harness exists in this repo (`EfSchemaVisualizer.Web.Tests` only tests `DiagramEditor` C# logic). Do not introduce one for this change — verify manually by running the app.
- Follow the existing `641px` responsive breakpoint already used in `Layout/MainLayout.razor.css` when adding new media queries, for consistency with the rest of the app.
- `_isFullscreen` is ephemeral UI state: never persist it to `layoutStorage`/localStorage.
- Run the app to verify each task with: `dotnet run --project src/EfSchemaVisualizer.Web` (from repo root), then open the printed `http://localhost:...` URL in a browser.

---

### Task 1: Default diagram sizing fills to the bottom of the window

**Files:**
- Modify: `src/EfSchemaVisualizer.Web/Pages/Home.razor:81`
- Create: `src/EfSchemaVisualizer.Web/Pages/Home.razor.css`

**Interfaces:**
- Produces: CSS classes `page-fill` (wraps the whole page body) and `diagram-panel` (the diagram container) that later tasks (Task 2) extend with a `.fullscreen` variant.

- [ ] **Step 1: Wrap the page body in a `page-fill` div and replace the diagram container's inline style with the `diagram-panel` class**

In `src/EfSchemaVisualizer.Web/Pages/Home.razor`, find:

```razor
<PageTitle>EF Schema Visualizer</PageTitle>

<h1>EF Schema Visualizer — ER Diagram</h1>
```

Replace with:

```razor
<PageTitle>EF Schema Visualizer</PageTitle>

<div class="page-fill">

<h1>EF Schema Visualizer — ER Diagram</h1>
```

Then find the closing of the file's markup section, immediately before the `@code` block:

```razor
    <div style="height: 600px; width: 100%; border: 1px solid #ccc; position: relative;">
        <CascadingValue Value="_diagram">
            <CascadingValue Value="_editContext">
                <DiagramCanvas>
                    <Widgets>
                        <NavigatorWidget Width="180" Height="130" />
                    </Widgets>
                </DiagramCanvas>
            </CascadingValue>
        </CascadingValue>
    </div>
}

@code {
```

Replace with:

```razor
    <div class="diagram-panel">
        <CascadingValue Value="_diagram">
            <CascadingValue Value="_editContext">
                <DiagramCanvas>
                    <Widgets>
                        <NavigatorWidget Width="180" Height="130" />
                    </Widgets>
                </DiagramCanvas>
            </CascadingValue>
        </CascadingValue>
    </div>
}

</div>

@code {
```

- [ ] **Step 2: Create `Home.razor.css`**

Create `src/EfSchemaVisualizer.Web/Pages/Home.razor.css`:

```css
@media (min-width: 641px) {
    .page-fill {
        display: flex;
        flex-direction: column;
        min-height: calc(100vh - 3.5rem);
    }

    .diagram-panel {
        flex: 1 1 auto;
        min-height: 400px;
    }
}

@media (max-width: 640.98px) {
    .diagram-panel {
        min-height: 70vh;
    }
}

.diagram-panel {
    width: 100%;
    border: 1px solid #ccc;
    position: relative;
    overflow: hidden;
}
```

`calc(100vh - 3.5rem)` matches the sticky top-row's `height: 3.5rem` set in `Layout/MainLayout.razor.css` at the same `641px` breakpoint, so the page fills exactly to the bottom of the window on desktop widths. Below that breakpoint the top-row isn't sticky (per the existing layout), so `diagram-panel` just gets a comfortable `min-height` instead and the page scrolls normally.

- [ ] **Step 3: Build to confirm no compile errors**

Run: `dotnet build src/EfSchemaVisualizer.Web`
Expected: `Build succeeded.`

- [ ] **Step 4: Manually verify in the browser**

Run: `dotnet run --project src/EfSchemaVisualizer.Web`, open the printed URL.

- Click "Render Diagram" with the default sample source.
- Confirm the diagram canvas now extends down to the bottom of the browser window instead of stopping at a fixed ~600px box.
- Resize the browser window vertically and confirm the diagram panel grows/shrinks to keep filling to the bottom.
- Resize below 641px width and confirm the layout still looks reasonable (diagram panel roughly 70% of viewport height, page scrolls).

- [ ] **Step 5: Commit**

```bash
git add src/EfSchemaVisualizer.Web/Pages/Home.razor src/EfSchemaVisualizer.Web/Pages/Home.razor.css
git commit -m "Home: diagram panel fills remaining window height instead of a fixed 600px box"
```

---

### Task 2: Fullscreen toggle

**Files:**
- Modify: `src/EfSchemaVisualizer.Web/Pages/Home.razor`
- Modify: `src/EfSchemaVisualizer.Web/Pages/Home.razor.css`

**Interfaces:**
- Consumes: `page-fill` / `diagram-panel` classes from Task 1.
- Produces: `_isFullscreen` field, `ToggleFullscreen()` method on `Home` — consumed by Task 3's `OnEscapeShortcut()`.

- [ ] **Step 1: Add the `_isFullscreen` field and `ToggleFullscreen()` method**

In `src/EfSchemaVisualizer.Web/Pages/Home.razor`, find:

```csharp
    private DiagramEditor? _editor;
    private DiagramEditContext? _editContext;
    private BlazorDiagram? _diagram;
    private IReadOnlyList<Diagnostic>? _diagnostics;
    private string? _error;
    private DotNetObjectReference<Home>? _shortcutsRef;
```

Replace with:

```csharp
    private DiagramEditor? _editor;
    private DiagramEditContext? _editContext;
    private BlazorDiagram? _diagram;
    private IReadOnlyList<Diagnostic>? _diagnostics;
    private string? _error;
    private DotNetObjectReference<Home>? _shortcutsRef;
    private bool _isFullscreen;
```

Then find:

```csharp
    private void ZoomToFit()
    {
        _diagram?.ZoomToFit(50);
    }
```

Replace with:

```csharp
    private void ZoomToFit()
    {
        _diagram?.ZoomToFit(50);
    }

    private void ToggleFullscreen()
    {
        _isFullscreen = !_isFullscreen;
    }
```

- [ ] **Step 2: Gate the editors/toolbar/instructions/diagnostics behind `!_isFullscreen`, add the Fullscreen button, and rework the diagram panel to show a floating toolbar in fullscreen**

Find this whole block (the page body from the title through the diagram panel, as left by Task 1):

```razor
<h1>EF Schema Visualizer — ER Diagram</h1>

<p>Paste entity classes and matching <code>OnModelCreating</code> fluent config below, then click Render Diagram.</p>

<div style="display: flex; gap: 16px;">
    <div style="flex: 1;">
        <label for="class-source">Entity classes</label>
        <textarea id="class-source" @bind="_classSource" @bind:event="oninput" @bind:after="SyncEditorSource" rows="14" style="width: 100%; font-family: monospace;"></textarea>
    </div>
    <div style="flex: 1;">
        <label for="config-source">DbContext / OnModelCreating</label>
        <textarea id="config-source" @bind="_configSource" @bind:event="oninput" @bind:after="SyncEditorSource" rows="14" style="width: 100%; font-family: monospace;"></textarea>
    </div>
</div>

<p>
    <button id="render-diagram" class="btn btn-primary" @onclick="() => RenderDiagramAsync()">Render Diagram</button>
    @if (_editContext is not null)
    {
        <button class="btn btn-secondary" @onclick="AddEntity">+ Entity</button>
        <button class="btn btn-secondary" title="Undo last diagram edit (Ctrl+Z)" disabled="@(_editor is null || !_editor.CanUndo)" @onclick="UndoAsync">Undo</button>
        <button class="btn btn-secondary" title="Redo last undone diagram edit (Ctrl+Y)" disabled="@(_editor is null || !_editor.CanRedo)" @onclick="RedoAsync">Redo</button>
        <button class="btn btn-secondary" title="Arrange entities into layers by relationship (principals before dependents)" @onclick="AutoLayout">Auto-layout</button>
        <button class="btn btn-secondary" title="Zoom/pan so every entity is visible" @onclick="ZoomToFit">Zoom to fit</button>
    }
    <InputFile OnChange="OnZipSelected" accept=".zip" />
    <button class="btn btn-secondary" disabled="@(_editor is null)" @onclick="DownloadZip">Download .zip</button>
    <button class="btn btn-secondary" title="Export the diagram as a standalone SVG image" disabled="@(_editor is null || _diagram is null)" @onclick="ExportSvgAsync">Export SVG</button>
    <button class="btn btn-secondary" title="Export the diagram as Mermaid erDiagram text" disabled="@(_editor is null)" @onclick="ExportMermaidAsync">Export Mermaid</button>
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

@if (_diagram is not null && _editContext is not null)
{
    <details style="margin: 8px 0; font-size: 0.85em;">
        <summary style="cursor: pointer;">How to edit the diagram</summary>
        <ul style="margin: 4px 0;">
            <li>Double-click an entity name, property name, or property type to rename/retype it in place.</li>
            <li>Drag from a port on the child (many) entity to the parent (one) entity to draw a relationship.</li>
            <li>Click the ▸ next to a property to expand its options: primary key toggle, column name/type, precision/scale, default value, and indexes.</li>
            <li>Use the "nullable" checkbox next to a property's type, and the × buttons to remove properties/entities.</li>
            <li>Use the Undo/Redo buttons above (or Ctrl+Z / Ctrl+Y anywhere outside the textareas) to step back through diagram edits (each gesture is one step).</li>
            <li>Use the Auto-layout button to arrange entities into layers by relationship, and Zoom to fit to bring every entity back into view; the small overview in the canvas corner shows where you are when zoomed in.</li>
            <li>Use Export SVG for a standalone image of the current layout, or Export Mermaid for <code>erDiagram</code> text you can paste into docs/Markdown.</li>
        </ul>
    </details>
    <div class="diagram-panel">
        <CascadingValue Value="_diagram">
            <CascadingValue Value="_editContext">
                <DiagramCanvas>
                    <Widgets>
                        <NavigatorWidget Width="180" Height="130" />
                    </Widgets>
                </DiagramCanvas>
            </CascadingValue>
        </CascadingValue>
    </div>
}
```

Replace with:

```razor
@if (!_isFullscreen)
{
    <h1>EF Schema Visualizer — ER Diagram</h1>

    <p>Paste entity classes and matching <code>OnModelCreating</code> fluent config below, then click Render Diagram.</p>

    <div style="display: flex; gap: 16px;">
        <div style="flex: 1;">
            <label for="class-source">Entity classes</label>
            <textarea id="class-source" @bind="_classSource" @bind:event="oninput" @bind:after="SyncEditorSource" rows="14" style="width: 100%; font-family: monospace;"></textarea>
        </div>
        <div style="flex: 1;">
            <label for="config-source">DbContext / OnModelCreating</label>
            <textarea id="config-source" @bind="_configSource" @bind:event="oninput" @bind:after="SyncEditorSource" rows="14" style="width: 100%; font-family: monospace;"></textarea>
        </div>
    </div>

    <p>
        <button id="render-diagram" class="btn btn-primary" @onclick="() => RenderDiagramAsync()">Render Diagram</button>
        @if (_editContext is not null)
        {
            <button class="btn btn-secondary" @onclick="AddEntity">+ Entity</button>
            <button class="btn btn-secondary" title="Undo last diagram edit (Ctrl+Z)" disabled="@(_editor is null || !_editor.CanUndo)" @onclick="UndoAsync">Undo</button>
            <button class="btn btn-secondary" title="Redo last undone diagram edit (Ctrl+Y)" disabled="@(_editor is null || !_editor.CanRedo)" @onclick="RedoAsync">Redo</button>
            <button class="btn btn-secondary" title="Arrange entities into layers by relationship (principals before dependents)" @onclick="AutoLayout">Auto-layout</button>
            <button class="btn btn-secondary" title="Zoom/pan so every entity is visible" @onclick="ZoomToFit">Zoom to fit</button>
            <button class="btn btn-secondary" title="Expand the diagram to fill the whole window" @onclick="ToggleFullscreen">Fullscreen</button>
        }
        <InputFile OnChange="OnZipSelected" accept=".zip" />
        <button class="btn btn-secondary" disabled="@(_editor is null)" @onclick="DownloadZip">Download .zip</button>
        <button class="btn btn-secondary" title="Export the diagram as a standalone SVG image" disabled="@(_editor is null || _diagram is null)" @onclick="ExportSvgAsync">Export SVG</button>
        <button class="btn btn-secondary" title="Export the diagram as Mermaid erDiagram text" disabled="@(_editor is null)" @onclick="ExportMermaidAsync">Export Mermaid</button>
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

    @if (_diagram is not null && _editContext is not null)
    {
        <details style="margin: 8px 0; font-size: 0.85em;">
            <summary style="cursor: pointer;">How to edit the diagram</summary>
            <ul style="margin: 4px 0;">
                <li>Double-click an entity name, property name, or property type to rename/retype it in place.</li>
                <li>Drag from a port on the child (many) entity to the parent (one) entity to draw a relationship.</li>
                <li>Click the ▸ next to a property to expand its options: primary key toggle, column name/type, precision/scale, default value, and indexes.</li>
                <li>Use the "nullable" checkbox next to a property's type, and the × buttons to remove properties/entities.</li>
                <li>Use the Undo/Redo buttons above (or Ctrl+Z / Ctrl+Y anywhere outside the textareas) to step back through diagram edits (each gesture is one step).</li>
                <li>Use the Auto-layout button to arrange entities into layers by relationship, and Zoom to fit to bring every entity back into view; the small overview in the canvas corner shows where you are when zoomed in.</li>
                <li>Use Export SVG for a standalone image of the current layout, or Export Mermaid for <code>erDiagram</code> text you can paste into docs/Markdown. Use Fullscreen for more room to work.</li>
            </ul>
        </details>
    }
}

@if (_diagram is not null && _editContext is not null)
{
    <div class="diagram-panel @(_isFullscreen ? "fullscreen" : "")">
        @if (_isFullscreen)
        {
            <div class="fullscreen-toolbar">
                <button class="btn btn-secondary btn-sm" title="Undo last diagram edit (Ctrl+Z)" disabled="@(_editor is null || !_editor.CanUndo)" @onclick="UndoAsync">Undo</button>
                <button class="btn btn-secondary btn-sm" title="Redo last undone diagram edit (Ctrl+Y)" disabled="@(_editor is null || !_editor.CanRedo)" @onclick="RedoAsync">Redo</button>
                <button class="btn btn-secondary btn-sm" title="Arrange entities into layers by relationship (principals before dependents)" @onclick="AutoLayout">Auto-layout</button>
                <button class="btn btn-secondary btn-sm" title="Zoom/pan so every entity is visible" @onclick="ZoomToFit">Zoom to fit</button>
                <button class="btn btn-secondary btn-sm" title="Exit fullscreen (Esc)" @onclick="ToggleFullscreen">Exit fullscreen</button>
            </div>
            @if (_error is not null)
            {
                <pre class="fullscreen-error">@_error</pre>
            }
        }
        <CascadingValue Value="_diagram">
            <CascadingValue Value="_editContext">
                <DiagramCanvas>
                    <Widgets>
                        <NavigatorWidget Width="180" Height="130" />
                    </Widgets>
                </DiagramCanvas>
            </CascadingValue>
        </CascadingValue>
    </div>
}
```

- [ ] **Step 3: Add `.fullscreen`, `.fullscreen-toolbar`, and `.fullscreen-error` styles**

In `src/EfSchemaVisualizer.Web/Pages/Home.razor.css`, find:

```css
.diagram-panel {
    width: 100%;
    border: 1px solid #ccc;
    position: relative;
    overflow: hidden;
}
```

Replace with:

```css
.diagram-panel {
    width: 100%;
    border: 1px solid #ccc;
    position: relative;
    overflow: hidden;
}

.diagram-panel.fullscreen {
    position: fixed;
    inset: 0;
    z-index: 1000;
    border: none;
    background: #fff;
    min-height: 0;
    flex: none;
}

.fullscreen-toolbar {
    position: absolute;
    top: 12px;
    right: 12px;
    z-index: 1001;
    display: flex;
    gap: 8px;
    background: rgba(255, 255, 255, 0.95);
    padding: 8px;
    border-radius: 6px;
    box-shadow: 0 1px 4px rgba(0, 0, 0, 0.2);
}

.fullscreen-error {
    position: absolute;
    top: 12px;
    left: 12px;
    z-index: 1001;
    max-width: 50%;
    margin: 0;
    padding: 8px;
    background: rgba(255, 255, 255, 0.95);
    border-radius: 6px;
    color: red;
}
```

`position: fixed; inset: 0;` makes the overlay cover the full viewport regardless of where `diagram-panel` sits in the normal document flow — no changes to `MainLayout` are needed.

- [ ] **Step 4: Build to confirm no compile errors**

Run: `dotnet build src/EfSchemaVisualizer.Web`
Expected: `Build succeeded.`

- [ ] **Step 5: Manually verify in the browser**

Run: `dotnet run --project src/EfSchemaVisualizer.Web`, open the printed URL.

- Render the default sample diagram, click "Fullscreen".
- Confirm: editors, main toolbar, instructions, and diagnostics disappear; the diagram covers the full browser viewport; a small floating toolbar (Undo/Redo/Auto-layout/Zoom to fit/Exit fullscreen) appears in the top-right corner.
- Drag a relationship between two entities while in fullscreen — confirm it still works.
- Try to create an invalid relationship (e.g. drag onto the same entity so it's rejected) and confirm the resulting error text appears near the top-left of the fullscreen overlay.
- Click "Exit fullscreen" and confirm the page returns to the normal view with all content restored and the diagram's edits/layout intact.

- [ ] **Step 6: Commit**

```bash
git add src/EfSchemaVisualizer.Web/Pages/Home.razor src/EfSchemaVisualizer.Web/Pages/Home.razor.css
git commit -m "Home: add fullscreen toggle for the diagram with a minimal floating toolbar"
```

---

### Task 3: Exit fullscreen with the Escape key

**Files:**
- Modify: `src/EfSchemaVisualizer.Web/Pages/Home.razor`
- Modify: `src/EfSchemaVisualizer.Web/wwwroot/js/keyboardShortcuts.js`

**Interfaces:**
- Consumes: `_isFullscreen` field and `ToggleFullscreen()` from Task 2; the existing `_shortcutsRef` `DotNetObjectReference<Home>` registered in `OnAfterRenderAsync` (`Pages/Home.razor:142-144`) and torn down in `DisposeAsync` (`Pages/Home.razor:153-159`) — reused as-is, no new registration.
- Produces: `[JSInvokable] OnEscapeShortcut()` on `Home`, invoked from `keyboardShortcuts.js`.

- [ ] **Step 1: Add the `OnEscapeShortcut` JSInvokable method**

In `src/EfSchemaVisualizer.Web/Pages/Home.razor`, find:

```csharp
    [JSInvokable]
    public async Task OnRedoShortcut() => await RedoAsync();
```

Replace with:

```csharp
    [JSInvokable]
    public async Task OnRedoShortcut() => await RedoAsync();

    [JSInvokable]
    public void OnEscapeShortcut()
    {
        if (!_isFullscreen)
        {
            return;
        }

        _isFullscreen = false;
        StateHasChanged();
    }
```

`StateHasChanged()` is required here (unlike the button-driven `ToggleFullscreen()`) because this method is invoked directly from JS via `DotNetObjectReference`, outside Blazor's normal event-dispatch pipeline that triggers re-renders automatically — the same reason `OnUndoShortcut`/`OnRedoShortcut` route through `OnDiagramEditedAsync()`, which itself calls `StateHasChanged()`.

- [ ] **Step 2: Handle the Escape key in `keyboardShortcuts.js`**

In `src/EfSchemaVisualizer.Web/wwwroot/js/keyboardShortcuts.js`, find:

```javascript
function handleUndoRedoKeydown(event) {
    if (isEditableTarget(event.target) || (!event.ctrlKey && !event.metaKey)) {
        return;
    }
```

Replace with:

```javascript
function handleUndoRedoKeydown(event) {
    if (event.key === 'Escape') {
        _undoRedoDotNetRef?.invokeMethodAsync('OnEscapeShortcut');
        return;
    }

    if (isEditableTarget(event.target) || (!event.ctrlKey && !event.metaKey)) {
        return;
    }
```

The editors are hidden while `_isFullscreen` is true (Task 2), so there's no case where focus is in a textarea during fullscreen — no need to guard this branch with `isEditableTarget`. `OnEscapeShortcut` itself is a no-op outside fullscreen, so Escape presses elsewhere on the page are unaffected.

- [ ] **Step 3: Build to confirm no compile errors**

Run: `dotnet build src/EfSchemaVisualizer.Web`
Expected: `Build succeeded.`

- [ ] **Step 4: Manually verify in the browser**

Run: `dotnet run --project src/EfSchemaVisualizer.Web`, open the printed URL.

- Render the diagram, enter fullscreen, press Escape — confirm it exits fullscreen back to the normal view.
- While NOT in fullscreen, press Escape — confirm nothing changes (no errors in the browser console).
- Enter fullscreen again, use Ctrl+Z / Ctrl+Y from the floating toolbar's Undo/Redo buttons and confirm they still work alongside the Escape handling (i.e. the shared keydown listener still dispatches both correctly).

- [ ] **Step 5: Commit**

```bash
git add src/EfSchemaVisualizer.Web/Pages/Home.razor src/EfSchemaVisualizer.Web/wwwroot/js/keyboardShortcuts.js
git commit -m "Home: exit fullscreen with the Escape key"
```
