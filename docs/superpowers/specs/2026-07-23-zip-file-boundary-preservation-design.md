# Zip round-trip file-boundary preservation — design

> Backlog item: `docs/backlog.md` Priority 4 (App-level features), "Zip
> round-trip loses file boundaries." The original
> `2026-07-15-zip-upload-download-design.md` deliberately scoped down to a
> two-blob model (`Entities.cs` + `DbContext.cs`, non-`.cs` files dropped) to
> fit the app's existing engine. This design closes that gap without
> requiring the full per-file engine rewrite the original design deferred.

## Why this is tractable without a full engine rewrite

`DiagramEditor`/`DiagramModelBuilder` operate on exactly two concatenated
strings (`ClassSource`, `ConfigSource`) and have no per-file awareness. A
"true" fix would make every parser/rewriter file-aware — a much larger
change, explicitly ruled out for this iteration.

Instead, this design adds a bookkeeping layer *around* the existing engine:

- `ClassSource`/`ConfigSource` remain single concatenated strings; nothing
  about parsing, merging, or rewriting changes.
- Two new maps — entity name → original filename, for classes and for
  config — are tracked alongside the existing `Dictionary<string, Guid>`
  entity-identity map already living in `DiagramEditor` (`_entityIds`), kept
  in sync at the same three touch points that map already is (rename, add,
  remove), plus captured in the same undo/redo snapshot.
- On download, the current (possibly edited) `ClassSource`/`ConfigSource`
  are split back into per-entity chunks (a top-level type declaration for
  classes; an entity's `FindConfigurationScopes` scope for config) and
  regrouped by filename using the origin maps.
- Files that were never classified as class or config content (non-`.cs`
  files, and `.cs` files with no recognizable type/config) are stored as raw
  bytes and passed through byte-for-byte, entirely independent of
  `DiagramEditor` — they never touch `ClassSource`/`ConfigSource`.

## Trade-off: no `using`/namespace fidelity

Preserving each entity's original `using` directives and namespace wrapper
on write-back would require splicing edits into a per-file template with
holes — substantially more machinery, closer in size to the full per-file
rewrite this design avoids. This design deliberately drops that fidelity:
each entity is written back as a bare class/record/struct block, the same
shape the paste-flow textareas already use. File *boundaries* (which entity
lands in which file) are preserved; the `using`/`namespace` wrapper around
them is not.

## Data model

### `ProjectArchiveResult` (extended)

```csharp
public sealed record ProjectArchiveResult(
    string ClassSource,
    string ConfigSource,
    IReadOnlyList<Diagnostic> Diagnostics,
    IReadOnlyDictionary<string, EntityPosition> Layout,
    IReadOnlyDictionary<string, string> EntityFileOrigins,
    IReadOnlyDictionary<string, string> ConfigFileOrigins,
    IReadOnlyDictionary<string, byte[]> PassthroughFiles);
```

- `EntityFileOrigins`: entity/record/struct name → the zip entry name it was
  read from (e.g. `"Blog" → "Models/Blog.cs"`).
- `ConfigFileOrigins`: entity name → the zip entry name its config was read
  from. Multiple entities map to the same filename when their config lives
  in one shared `OnModelCreating` method (only one file can physically hold
  that method body); each maps to its own filename under the
  `IEntityTypeConfiguration<T>`-per-file pattern.
- `PassthroughFiles`: every other zip entry (excluding the `diagram-layout.json`
  sidecar, which keeps its existing dedicated handling), keyed by entry name,
  raw bytes.

### `ProjectArchiveWriter.Write` (extended)

```csharp
public static byte[] Write(
    string classSource,
    string configSource,
    IReadOnlyDictionary<string, string>? entityFileOrigins = null,
    IReadOnlyDictionary<string, string>? configFileOrigins = null,
    IReadOnlyDictionary<string, byte[]>? passthroughFiles = null,
    IReadOnlyDictionary<string, EntityPosition>? layout = null);
```

All new parameters default to null/empty, so a caller passing only
`classSource`/`configSource` (the paste-flow path) gets exactly today's
two-file zip — this is the backward-compatibility story, not a special case
to code around.

## Upload: building the origin maps

`ProjectArchiveReader.Read` keeps its existing per-entry classification
(Class / Config / Ignored) unchanged. It adds one more pass per entry,
using traversals the parser already performs elsewhere:

- **Class files**: walk top-level `ClassDeclarationSyntax` /
  `RecordDeclarationSyntax` / `StructDeclarationSyntax` nodes and record
  `EntityFileOrigins[typeName] = entry.FullName` for each.
- **Config files**:
  - Any top-level class whose base list contains `IEntityTypeConfiguration<T>`
    → `ConfigFileOrigins[entityName] = entry.FullName` for that one entity.
  - Everything else (a `DbContext`-shaped file with `OnModelCreating`, or
    bare top-level fluent statements) → every entity name found via
    `FluentSyntaxHelpers.GetConfiguredEntityName` in that file maps to the
    same `entry.FullName`. Since an `OnModelCreating` method body can only
    live in one file, there is at most one such file per project.
  - If an entity is configured both ways at once (an `Entity<T>()` block in
    the shared `OnModelCreating` file *and* its own
    `IEntityTypeConfiguration<T>` file — an existing, already-handled dual-
    style case), `ConfigFileOrigins` records the `OnModelCreating` file's
    origin, matching the existing precedent that the `Entity<T>()` block is
    authoritative for that entity today (`OnModelCreatingRewriter` already
    prefers it on edit).
- **Everything else** (non-`.cs` entries, and `.cs` entries with no
  recognizable type declaration or config scope) → raw bytes captured into
  `PassthroughFiles[entry.FullName]`.
- The existing `diagram-layout.json` sidecar entry is recognized and
  consumed before this new logic runs (unchanged from today), so it is
  never treated as passthrough.

Classification itself — what counts as Class vs. Config vs. Ignored — does
not change. This only records which file each already-classified thing came
from.

## Download: splitting back into files

Both class and config reconstruction follow the same shape: parse the
current source, extract per-entity chunks, group by filename via the origin
map, join same-filename chunks with a blank line (matching today's join
convention), one zip entry per resulting filename. A filename with zero
chunks after edits (every entity that pointed to it was removed) is omitted
from the output — no empty stub file is written.

**Classes**: parse `classSource`; for each top-level type declaration, look
up its name in `EntityFileOrigins`.
- Found → its own text (`.ToFullString()`, bare declaration only — no
  `using`/namespace, per the trade-off above) is appended to that filename's
  buffer.
- Not found (a new entity added via the diagram, never in the original
  upload) → written to a new file named `<EntityName>.cs`.

**Config**: for each entity in the current model, resolve its config scope
the same way `OnModelCreatingRewriter` already does today (the
`IEntityTypeConfiguration<T>` class as a whole, or the `Entity<T>(...)`
scope/statements) and look up the entity's name in `ConfigFileOrigins`.
- Found → appended, in original relative order, to that filename's buffer.
  Entities sharing a filename (the `OnModelCreating` case) end up
  concatenated together in one file, reproducing today's single-file
  behavior under whatever name it actually had.
- Not found (new entity) → falls back to whichever shared
  `OnModelCreating`-style filename already exists in `ConfigFileOrigins`'
  values, or a new `DbContext.cs` if none did. This matches the existing
  "new entities always synthesize into `OnModelCreating`" behavior.

**Passthrough files**: written verbatim, unconditionally, independent of any
diagram edit.

## Wiring through `DiagramEditor`

`DiagramEditor` gains two new fields, `_entityFileOrigins` and
`_configFileOrigins` (`Dictionary<string, string>`), managed at exactly the
points `_entityIds` already is:

- New constructor overload:
  `DiagramEditor(classSource, configSource, entityFileOrigins = null, configFileOrigins = null)`.
  Null/omitted defaults to empty — the existing two-arg constructor keeps
  its current behavior unchanged (paste-flow callers are unaffected).
- `RenameEntity`: when an entry moves from `_entityIds[oldName]` to
  `_entityIds[newName]`, the same key rename happens in
  `_entityFileOrigins`/`_configFileOrigins` if a mapping exists for
  `oldName`.
- `AddEntity`: no origin recorded — falls through to the "new file" /
  "shared fallback file" rules above.
- `RemoveEntity`: the entry is dropped from both origin maps, mirroring the
  existing `_entityIds` removal.
- `Snapshot` (the undo/redo record) gains both origin maps alongside
  `ClassSource`/`ConfigSource`/`EntityIds`, so undo/redo restores origin
  tracking consistently with everything else the snapshot already restores.
- New public read-only properties `EntityFileOrigins` / `ConfigFileOrigins`,
  mirroring the existing `EntityIds` property, for `Home.razor` to read at
  download time.

## Wiring through `Home.razor`

- New field `_passthroughFiles` (`Dictionary<string, byte[]>`), populated
  from `ProjectArchiveResult.PassthroughFiles` in `OnZipSelected`.
- `OnZipSelected`: constructs `DiagramEditor` with the new origin-map
  constructor overload instead of the two-arg one.
- The plain "Render Diagram" button (pure paste-flow, no zip involved)
  keeps using the two-arg `DiagramEditor` constructor — no code path change,
  so `_passthroughFiles` stays empty and download still produces the
  classic two-file zip. This is the natural backward-compatibility
  behavior, not a special case.
- `DownloadZip`: passes `_editor.EntityFileOrigins`, `_editor.ConfigFileOrigins`,
  and `_passthroughFiles` into `ProjectArchiveWriter.Write`, alongside the
  existing `ClassSource`/`ConfigSource`/layout arguments.

## Testing

Extends the existing `Archive` test suite
(`ProjectArchiveReaderTests`/`ProjectArchiveWriterTests`/`ProjectArchiveRoundTripTests`)
plus `DiagramEditorTests`:

- Multi-file class upload (`Blog.cs` + `Post.cs`) → origins recorded
  correctly; round-trips to the same two filenames on download.
- `IEntityTypeConfiguration<T>`-per-file upload (one config class per file)
  → each entity's config origin tracked independently; round-trips to
  separate files.
- Shared `OnModelCreating` file configuring multiple entities → all map to
  one filename; reassembles into one file containing all entities' config,
  in original order.
- A non-`.cs` file (e.g. `.csproj`) and an unclassifiable `.cs` file
  (enum-only) → both appear byte-identical in the output zip.
- New entity added post-upload → class lands in `<EntityName>.cs`; its
  config lands in the pre-existing shared config file, or a new
  `DbContext.cs` if the project only had `IEntityTypeConfiguration` files.
- Entity removed, leaving its original file with no remaining entities →
  that filename is absent from the output zip.
- Rename after upload → entity's origin mapping follows the rename; file
  content still lands under the *original* filename.
- Undo after a rename/add/remove → origin maps restore correctly alongside
  `ClassSource`/`ConfigSource`/`EntityIds`.
- Plain paste-flow (no zip ever uploaded) → download still produces exactly
  `Entities.cs` + `DbContext.cs`, byte-identical to today's behavior.

## Out of scope

- Preserving original `using` directives / namespace wrappers on write-back
  (see Trade-off section above).
- Preserving any shared structure within a file beyond flat per-entity
  grouping — e.g. if a namespace originally wrapped several entities,
  renaming/removing individual ones does not attempt to keep any remaining
  siblings' formatting or ordering beyond the blank-line-joined concatenation
  already used elsewhere in this codebase.
- Archive formats other than `.zip` (unchanged from the original zip design).
- Any additional whitespace/formatting fidelity beyond the existing
  whole-blob `NormalizeWhitespace()` behavior already used on synthesis
  paths — unchanged, pre-existing trade-off from the add/insert rewriter
  work.
- Passthrough files are never parsed or validated — a passthrough file
  referencing a renamed/removed entity (e.g. a `.csproj` item or a
  migration) is not updated or flagged; it passes through unchanged, exactly
  as uploaded.
