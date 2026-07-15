# `.zip` upload / download — design

> Backlog item: `docs/backlog.md` Priority 4, "`.zip` upload / download, fully
> client-side, stateless." Originally specced in
> `2026-07-07-ef-schema-visualizer-design.md` as true per-file round-tripping;
> this design deliberately scopes down to fit the app's current two-blob
> engine (see Scope below).

## Scope

The app's parsing/editing engine (`DiagramEditor`, `DiagramModelBuilder`)
operates on exactly two strings: `ClassSource` (all entity classes) and
`ConfigSource` (all fluent config). It has no notion of individual files.

True per-file round-tripping (upload N `.cs` files, edit, download those same
N files back with only the edited spans changed, other project files passed
through untouched) would require making Core's parser/rewriter file-aware —
a much larger change. This design instead:

- **Upload**: scans a `.zip` for `.cs` files, classifies each as "entity
  classes" or "config" by content, concatenates each bucket into the
  existing two textareas.
- **Download**: re-zips the two current blobs as two fixed-name files.
  Original file names/boundaries are not preserved.
- Non-`.cs` files, and `.cs` files that are neither classifiable as
  config nor as containing a type declaration (e.g. enum-only, interface-only,
  unparseable), are **ignored on upload** and **not present in the downloaded
  zip**. The tool's job is the entity/config code, not general project
  round-tripping.

This fits today's architecture, ships fast, and is explicitly a v1 scope
decision — true per-file fidelity remains a possible future iteration if the
two-blob model itself is ever generalized.

## Architecture

New `EfSchemaVisualizer.Core/Archive/` folder, two static classes. Living in
Core (not Web) keeps the logic unit-testable with plain xUnit and reusable
outside the Blazor UI, matching the existing Core/Web split rationale (a
future hosted service could reuse it without a rewrite).

### `ProjectArchiveReader`

```csharp
public static class ProjectArchiveReader
{
    public static ProjectArchiveResult Read(Stream zipStream);
}

public sealed record ProjectArchiveResult(
    string ClassSource,
    string ConfigSource,
    IReadOnlyList<Diagnostic> Diagnostics);
```

- Opens `zipStream` with `System.IO.Compression.ZipArchive` (pure managed,
  no native dependency — works under Blazor WASM).
- Iterates entries in archive order; skips directory entries and anything
  not ending in `.cs`.
- For each `.cs` entry's text, classifies it (see Classification heuristic
  below) into `Config`, `Class`, or `Ignored`.
- Concatenates all `Class`-bucket file contents (in entry order, separated
  by a blank line) into `ClassSource`; same for `Config` bucket into
  `ConfigSource`.
- If both buckets end up empty, adds a single diagnostic (new code
  `DiagnosticCodes.ArchiveNoContentFound` or similar, following the existing
  `DiagnosticCodes` constants-class pattern) with a message like "No entity
  classes or configuration found in the uploaded zip." The two source
  strings are still returned (as empty strings) rather than throwing —
  callers already have an established pattern of surfacing problems via the
  diagnostics list rather than exceptions.
- A zip that fails to open (corrupt/not-a-zip) throws
  `InvalidDataException` (what `ZipArchive` itself throws) — the caller
  (Home.razor) already has a catch-all `try/catch` around parsing that
  surfaces `ex.ToString()` via `_error`, so no new error-handling path is
  needed there.

### Classification heuristic

Per `.cs` entry, parse with `CSharpSyntaxTree.ParseText` and inspect the
root:

1. If any method declaration is named `OnModelCreating`, **or** any type
   declaration's base list contains an identifier `IEntityTypeConfiguration`
   (matches both `IEntityTypeConfiguration<Foo>` and any qualified form) →
   **Config**.
2. Else if the tree contains at least one `ClassDeclarationSyntax`,
   `RecordDeclarationSyntax`, or `StructDeclarationSyntax` → **Class**.
3. Else → **Ignored**.

This mirrors what `EntityClassParser`/`FluentConfigParser` already care
about, so it stays consistent with existing "what counts as an entity/config
file" behavior in the pasted-text flow — no new filtering rules are
introduced for what becomes an entity once a file is in the Class bucket.

### `ProjectArchiveWriter`

```csharp
public static class ProjectArchiveWriter
{
    public static byte[] Write(string classSource, string configSource);
}
```

- Builds a zip in a `MemoryStream` via `ZipArchive` (`ZipArchiveMode.Create`)
  with exactly two entries: `Entities.cs` (= `classSource`) and
  `DbContext.cs` (= `configSource`). Returns the resulting bytes.
- No conditional omission of empty blobs — always both entries, even if one
  is an empty string, for predictability (the user can see at a glance that
  their diagram had no config, rather than wondering where a file went).

## Web wiring (`Home.razor`)

**Upload**: an `<InputFile>` placed next to the existing "Render Diagram" /
"+ Entity" buttons. On `OnChange`:

1. Read the selected file into a `MemoryStream` (via
   `IBrowserFile.OpenReadStream()`, size-capped the same way Blazor's
   default `InputFile` already caps `OpenReadStream` — 500 KB default limit
   raised to something generous like 20 MB, since real projects'
   entity/config code is small but the cap default is easy to hit
   accidentally on the whole-zip size).
2. Call `ProjectArchiveReader.Read`.
3. Set `_classSource`/`_configSource` from the result (this **replaces**
   whatever was in the textareas — same "clean slate" semantics as directly
   editing the boxes).
4. Set `_diagnostics` from the result's diagnostics (so an empty/unmatched
   zip surfaces the new diagnostic immediately) and call the existing
   `RenderDiagram()` so the diagram appears without an extra click.
5. Wrap the read+parse in the same `try/catch` pattern `RenderDiagram`
   already uses, setting `_error` on failure (corrupt zip, etc).

**Download**: a "Download .zip" button, disabled until `_editor is not
null` (same gating as the existing `+ Entity` button). On click:

1. Call `ProjectArchiveWriter.Write(_editor.ClassSource, _editor.ConfigSource)`.
2. Hand the resulting bytes to the browser via `IJSRuntime` using
   `DotNetStreamReference` (the standard .NET 6+ pattern for binary
   downloads from Blazor — avoids base64 inflation) and a small
   `wwwroot/js/downloadFile.js` helper (`Blob` + object URL + a
   programmatically clicked `<a download>`).
3. Fixed download filename: `ef-schema-visualizer-export.zip`.

## Testing

`ProjectArchiveReader`/`ProjectArchiveWriter` get full xUnit coverage in
`EfSchemaVisualizer.Core.Tests` (build in-memory test zips with
`ZipArchive`, assert on the resulting `ProjectArchiveResult`):

- Zip with one config-style file + one class-style file → correct bucketing.
- Zip with an `IEntityTypeConfiguration<T>` file → bucketed as Config.
- Zip with multiple class files → concatenated in entry order.
- Zip containing a non-`.cs` file (e.g. `.csproj`) → ignored.
- Zip containing an enum-only `.cs` file → ignored.
- Empty zip / zip with no matching content → diagnostic present, empty
  strings returned, no throw.
- Corrupt/non-zip stream → throws `InvalidDataException`.
- `ProjectArchiveWriter.Write` round-trips: written bytes reopen as a valid
  zip with exactly `Entities.cs` and `DbContext.cs` containing the expected
  text.

The `InputFile`/download-button UI wiring itself is not unit-testable and
joins the already-recorded gap in `docs/backlog.md` / the editable-diagram
design doc: no in-browser interactive verification has been possible in the
implementation sandbox for any UI feature so far. This remains an open
follow-up, same as prior phases.

## Out of scope

- True per-file round-trip preservation (see Scope above).
- Passthrough of non-`.cs` project files (csproj, migrations, etc) in the
  downloaded zip.
- Archive formats other than `.zip` (`.tar`, `.tar.gz`, `.7z` — already
  called out as stretch goals in the original spec, not v1).
- Any change to what counts as an "entity" once class-bucketed text reaches
  `EntityClassParser` — that's pre-existing behavior (every class in
  `ClassSource` becomes an entity), unrelated to this feature.
