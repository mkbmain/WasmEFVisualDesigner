# EF Schema Visualizer — Design

## Problem

There's no good way to visually design or review an EF Core model that isn't
tied to Visual Studio (EF Core Power Tools) or a paid product (Devart Entity
Developer). JetBrains Rider users specifically have no visual diagramming for
EF Core at all — there's an open JetBrains feature request (RIDER-120825)
asking for it. Free alternatives that do offer a design surface (EFDesigner)
are design-first only and can't read an existing database-first project back
in.

There's also a trust gap: every existing visual tool either runs inside an
IDE or is a hosted/commercial product. Nothing lets a developer *or* a DBA
who doesn't know C# open a model, edit it visually, and get code back,
without installing an IDE extension or sending code to a server.

## Goal

A free, open-source, web-based tool that:

1. Reads existing EF Core entity classes + configuration from a C# project.
2. Renders them as an editable ER diagram.
3. Lets a user edit the diagram (entities, properties, types, relationships,
   keys, indexes) via direct manipulation — drag entities to reposition,
   draw relationships between them, similar to SQL Server Management
   Studio's table designer — not a form-based editor bolted onto a static
   render. This WYSIWYG interaction model is a stated goal, confirmed
   2026-07-13, and shapes the diagramming library choice made in
   `2026-07-13-er-diagram-render-design.md`.
4. Regenerates the modified C# files for download.

Built as a lean personal project, iterated in small working slices. Intended
to be given away for free (donations/stars), not sold directly — see
Monetization below.

## Non-goals (v1)

- No live database connection or provider-specific SQL (SQL Server / Postgres
  / MySQL). If a user is starting database-first, they run EF's own
  `dotnet ef dbcontext scaffold` first and feed the resulting C# into this
  tool. We never hold DB credentials.
- No migrations handling. `dotnet ef migrations add` remains the user's
  responsibility; this tool only edits model code.
- No accounts or server-side persistence. Fully stateless: upload, edit
  in-browser, download.
- No archive formats beyond `.zip`. `.tar`, `.tar.gz`, `.7z` are nice-to-have
  stretch goals, not required for v1.
- No support for `IEntityTypeConfiguration<T>` config classes in v1 (see
  Sequencing).

## Architecture

- **Blazor WebAssembly**, fully static, no backend. Hosted on GitHub Pages.
- Parsing/codegen logic lives in a standalone .NET class library (e.g.
  `EfSchemaVisualizer.Core`), referenced by the Blazor app. This isolates the
  engine from the UI so a future hosted service (if the optional "pro" tier
  ever happens) can reuse it without a rewrite.
- Parsing uses Roslyn's **syntax-only** APIs: `CSharpSyntaxTree.ParseText`,
  `CSharpSyntaxWalker` to read, `SyntaxFactory` / `CSharpSyntaxRewriter` to
  write. Deliberately avoids `Microsoft.CodeAnalysis.CSharp.Scripting` and
  `CSharpCompilation` — those require resolving assembly locations for
  metadata references, which breaks under Mono WASM. Pure syntax-tree
  parsing and printing has no such dependency and works on WASM today.
- GitHub Actions builds and deploys to GitHub Pages on push to `main`. The
  deployed app is always traceable to visible, unminified-in-spirit source —
  this is a deliberate trust signal: "the code you're running is the code
  you can read."

## Core workflow (v1)

1. (Outside this tool) User optionally runs `dotnet ef dbcontext scaffold`
   against their database to get initial C# entity classes.
2. User uploads a `.zip` containing their entity classes and `DbContext`
   (with its `OnModelCreating` fluent configuration).
3. Tool parses the files into an in-memory model (entities, properties,
   relationships, keys, indexes) and renders an editable ER diagram.
4. User edits the diagram: add/remove entities and properties; set CLR type,
   nullable, string length, precision; draw relationships (1:1, 1:many,
   many:many); define keys and indexes.
5. Tool regenerates the affected C# files from the edited model and offers a
   `.zip` download. No data is transmitted anywhere at any point.

## Parsing/codegen scope, sequenced

- **First working slice:** `OnModelCreating` fluent API support — all
  configuration read from and regenerated inside the one `DbContext` method.
  Chosen first because `dotnet ef dbcontext scaffold` (EF's own built-in
  reverse-engineering command) generates configuration this way by default,
  with no built-in flag to produce separate config classes instead
  ([Microsoft Learn](https://learn.microsoft.com/en-us/ef/core/managing-schemas/scaffolding/),
  [dotnet/efcore#27777](https://github.com/dotnet/efcore/issues/27777)). This
  keeps the "scaffold your DB, then feed the result straight into this tool"
  workflow working from day one, without requiring a manual refactor first.
  Regenerating just the affected entity's configuration inside a shared
  method — without disturbing unrelated code or other entities' config in
  that same method — is the harder parsing/codegen problem, so this slice is
  where the core engine gets proven.
  - Entities and properties (add/remove/rename)
  - CLR types, nullable, string length, precision
  - Relationships: 1:1, 1:many, many:many
  - Keys (including composite) and indexes (including unique)
- **Fast-follow:** `IEntityTypeConfiguration<T>` style — one isolated config
  class per entity. This is EF's own recommended pattern for larger models
  and is structurally easier to parse/regenerate safely (each entity's
  config lives in its own file), so it follows once the harder
  `OnModelCreating` case is solid.

## Monetization

The tool itself stays free and fully open source (MIT or similar permissive
license) — that openness is part of the trust story and not up for trade.
No paid gate is planned for v1. Possible future paths, decided only after
v1 has real usage:

- Donations / "buy me a coffee" and star-the-repo prompts in the README/app.
- An optional hosted "pro" tier later (accounts, saved projects, direct
  GitHub repo integration instead of zip upload/download) — would reuse the
  same `EfSchemaVisualizer.Core` library behind a thin API, not a rewrite.

## Repo & hosting

- Public GitHub repository, MIT license.
- GitHub Actions workflow builds the Blazor WASM app and deploys to GitHub
  Pages on push to `main`.
- README documents the trust model (client-side only, open source, visible
  CI build) and includes donation/star prompts.

## Open risks / things to watch in implementation

- Roslyn assemblies add real weight to the WASM payload; first-load time
  needs to be checked early rather than assumed acceptable.
- No canvas/diagramming library is chosen yet for Blazor — the ecosystem is
  less mature than React's. This is an implementation-planning decision, not
  a design blocker, but budget extra time for it.
- Safely rewriting one entity's slice of a shared `OnModelCreating` method —
  without disturbing other entities' config or unrelated code in that same
  method — is the riskiest parsing/codegen piece in v1. Worth a small
  throwaway spike early to validate the approach (e.g. round-trip a
  representative `OnModelCreating` unchanged) before committing to the full
  diagram-editing UI on top of it.
