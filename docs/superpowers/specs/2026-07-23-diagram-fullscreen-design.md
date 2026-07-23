# Diagram fills the window + fullscreen toggle

## Problem

The diagram canvas on the home page renders in a fixed `height: 600px` box below
the source editors, toolbar, and instructions. On most screens this leaves the
diagram feeling small relative to the rest of the page, and there's no way to
get more room without shrinking the browser's other chrome.

## Goals

1. By default, the diagram canvas fills the remaining vertical space down to
   the bottom of the browser window (not a fixed 600px), while the editors,
   toolbar, and instructions above it keep their natural height.
2. Add a "Fullscreen" toggle that expands the diagram to cover the entire
   browser viewport, hiding everything else on the page except a minimal
   floating toolbar, for when the user wants maximum space.

## Non-goals

- Native browser Fullscreen API (`requestFullscreen()`), which would also hide
  browser chrome (tabs/address bar). Explicitly out of scope for this change —
  a CSS overlay confined to the page is simpler and avoids the extra JS interop
  and `fullscreenchange` synchronization that the native API requires.
- Persisting fullscreen state across reloads. It's ephemeral UI state.
- Any change to diagram content, layout persistence, undo/redo, or export
  behavior — this is purely a sizing/visibility change.

## Design

### Default (non-fullscreen) sizing

`Home.razor` currently renders the diagram in a div with
`style="height: 600px; width: 100%; ..."`. Replace this with a scoped CSS class
(`diagram-panel`) defined in a new `Home.razor.css`, using flexbox so the panel
stretches to fill the space between the toolbar/instructions above it and the
bottom of the viewport, instead of a hardcoded pixel height. This adapts
automatically if the content above (e.g. diagnostics/error text) changes
height — no pixel offsets to keep in sync.

### Fullscreen toggle

- Add a `_isFullscreen` bool field and `ToggleFullscreen()` method to
  `Home.razor`.
- Add a "Fullscreen" button next to the existing "Zoom to fit" button, shown
  under the same `@if (_editContext is not null)` guard as the other
  diagram-editing buttons (so it can't be clicked before a diagram exists).
- The diagram container's CSS class switches to `diagram-panel fullscreen`
  when `_isFullscreen` is true. The `fullscreen` class makes it
  `position: fixed; inset: 0;` with an elevated `z-index`, covering the full
  browser viewport, with a background so nothing behind it shows through.
- While `_isFullscreen` is true:
  - The source editors (`<textarea>` pair), the main toolbar row, the "How to
    edit" `<details>` instructions, and the diagnostics block are all hidden
    (wrapped in `@if (!_isFullscreen)`).
  - A small floating toolbar is rendered inside the fullscreen overlay
    (absolutely positioned in a corner) with: Undo, Redo, Auto-layout, Zoom to
    fit, and Exit fullscreen. These are the only actions needed while focused
    on arranging the diagram; everything else (+Entity, file upload, Download
    .zip, Export SVG/Mermaid) is reachable after exiting.
  - If `_error` is set, it's still displayed on/near the floating toolbar —
    errors (e.g. a rejected relationship drag) must stay visible even in
    fullscreen.

### Canvas resize handling

`Z.Blazor.Diagrams`'s `DiagramCanvas` already attaches a `ResizeObserver` to
its container and calls back into the component whenever the container's
bounding rect changes (confirmed in the package's `script.js`). This means
resizing the container via CSS class changes — both the default fill-height
behavior and the fullscreen overlay — is automatically picked up by the
diagram library with no extra JS interop or manual resize calls needed.

### Escape key to exit fullscreen

`wwwroot/js/keyboardShortcuts.js` already registers one `keydown` listener on
`document` and holds a `DotNetObjectReference` for Ctrl+Z/Ctrl+Y, guarded by
`isEditableTarget` so it doesn't fire while typing in the textareas. Extend the
same listener to also check for the `Escape` key and invoke a new
`[JSInvokable] OnEscapeShortcut()` method on `Home`, reusing the existing
registration (no second listener). `OnEscapeShortcut` only acts (calls
`ToggleFullscreen()` to exit) when `_isFullscreen` is currently true; otherwise
it's a no-op, so Escape doesn't interfere with anything else on the page when
not in fullscreen.

## Edge cases

- **Toggling before a diagram is rendered**: impossible — the button only
  renders once `_editContext is not null`.
- **Reload while in fullscreen**: `_isFullscreen` is not persisted anywhere
  (not in the layout-storage key, not in localStorage), so a reload always
  starts back in the normal view.
- **Diagram state untouched**: undo/redo history, layout, entity/config
  sources, and file origins are all owned by `_editor`/`DiagramEditor` and are
  completely unaffected by this toggle.

## Testing

`EfSchemaVisualizer.Web.Tests` has no bUnit or other Razor-component test
harness today — all existing tests target the `DiagramEditor` C# logic
directly. This change is CSS/markup plus a single bool toggle, so no new
automated UI test framework is being introduced for it. Verification will be
manual, run through the app in a browser, checking:

- Default view: diagram fills down to the bottom of the window instead of
  stopping at 600px.
- Fullscreen button: diagram overlay covers the full viewport; editors,
  toolbar, and instructions are hidden; the floating toolbar (Undo/Redo/
  Auto-layout/Zoom to fit/Exit) is visible and functional.
- Exit fullscreen via both the Exit button and the Escape key returns to the
  normal view with diagram state intact.
- Dragging to create relationships and other diagram edits still work
  correctly in both states.
- Errors surfaced during fullscreen (e.g. an invalid relationship drag) are
  visible without needing to exit fullscreen.
