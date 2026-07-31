# Runnable Project Scaffold on Download

> Backlog item: Priority 3 — "Runnable project scaffold on download"
> (`docs/backlog.md`).

## Problem

Even with every Priority 0–2 bug fixed, journey 1 ("DBA designs a schema from
scratch and ends up with a database they can run") dead-ends at download.
Starting from the shipped sample, adding an entity, and downloading produces
`Entities.cs` (valid C#, no namespace) and `DbContext.cs` containing bare
`modelBuilder.Entity<Blog>(...)` statements — not a `DbContext`. No class, no
`DbSet`s, no `using Microsoft.EntityFrameworkCore;`, no `.csproj`, no provider
package, no connection string, no `Program.cs`, no migration. `modelBuilder`
is an undefined identifier. Someone with no C# experience cannot turn that
into a database.

This also matters for uploaded projects: an upload may be missing pieces
(no `Program.cs`, no `appsettings.json`) even though its class/config files
round-trip correctly via the existing passthrough mechanism (F2/F3).

## Goal

Add an opt-in "Generate runnable project scaffold" checkbox next to the
existing Download .zip button. When checked, the download fills in whatever
scaffold pieces are missing — a real `AppDbContext`, `.csproj`, connection
string, entry point, and instructions — without ever overwriting a file the
user already has.

## UI

A checkbox, "Generate runnable project scaffold", appears next to the
Download .zip button whenever `_editor is not null` (both freehand-paste and
uploaded-zip sessions).

When checked:
- A "Project name" text input appears, defaulting to `"MyApp"`, or — if an
  existing `.csproj` is present in `_passthroughFiles` — to that file's name
  (minus extension), purely as a starting point for the input; it doesn't
  imply we'll rename anything.
- A provider dropdown (SQL Server / PostgreSQL / SQLite) appears **only when
  no `.csproj` is present** in `_passthroughFiles`. If a `.csproj` already
  exists we never touch it (see Overwrite Policy), so a provider choice would
  have nothing to apply to.

`DownloadZip` reads these two inputs and threads them into the new scaffold
step described below, before the existing `ProjectArchiveWriter.Write` call.

## Overwrite Policy

Scaffold generation is strictly additive: a file that already exists in
`_passthroughFiles` (or, for the config file, config source that's already a
real `DbContext`-derived class) is never regenerated or modified. Checking
the box on an upload that already has a `.csproj`/`Program.cs`/etc. fills in
only what's absent — e.g. adds a missing `appsettings.json` but leaves an
existing one alone.

## Detection — `ScaffoldPlanner` (new, `EfSchemaVisualizer.Core.Archive`)

```csharp
public static class ScaffoldPlanner
{
    public static ScaffoldPlan Plan(
        string configSource,
        IReadOnlyDictionary<string, byte[]>? passthroughFiles);
}

public sealed record ScaffoldPlan(
    bool NeedsCsproj,
    bool NeedsProgram,
    bool NeedsAppSettings,
    bool NeedsReadme,
    bool NeedsDbContextWrapper,
    ScaffoldProvider? DetectedProvider);

public enum ScaffoldProvider { SqlServer, PostgreSql, Sqlite }
```

- `NeedsCsproj` / `NeedsProgram` / `NeedsAppSettings` / `NeedsReadme`: true
  when no passthrough entry's path ends with `.csproj` / equals `Program.cs`
  / equals `appsettings.json` / equals `README.md` (case-insensitive
  filename match, ignoring directory).
- `NeedsDbContextWrapper`: parse `configSource` with Roslyn; true when the
  compilation unit's members are bare `GlobalStatementSyntax` (today's bare
  fluent-call shape) rather than a `ClassDeclarationSyntax` whose base list
  includes `DbContext`. If a real `DbContext` class already exists, it is
  left completely alone — no attempt to add missing `DbSet`s to hand-written
  code (documented non-goal).
- `DetectedProvider`: only computed when `NeedsCsproj` is false; scans the
  existing `.csproj`'s text for one of the three known EF provider package
  names. Informational only (e.g. could be shown in the UI as "detected:
  SQL Server") — never drives generation, since we don't touch an existing
  `.csproj`.

## Generation — `ScaffoldGenerator` (new)

```csharp
public static class ScaffoldGenerator
{
    public static ScaffoldResult Generate(
        ScaffoldPlan plan,
        string configSource,
        IReadOnlyList<EntityModel> entities,
        string projectName,
        ScaffoldProvider provider);
}

public sealed record ScaffoldResult(
    string ConfigSource,               // possibly rewritten (DbContext wrapper added)
    IReadOnlyDictionary<string, byte[]> NewPassthroughFiles);
```

Produces only the pieces `plan` marked missing:

- **`AppDbContext.cs`** (only if `NeedsDbContextWrapper`): wraps the existing
  fluent-config body verbatim inside `OnModelCreating(ModelBuilder
  modelBuilder)`, adds one `DbSet<T>` property per entity in `entities`
  (simple heuristic pluralization: `+s` by default, `+es` after
  s/x/ch/sh, `y → ies`; documented as not a general English pluralizer —
  a generated name a user dislikes is a one-line hand edit), a
  `public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)`
  constructor, `using Microsoft.EntityFrameworkCore;`, and
  `namespace {ProjectName};`. This becomes the new `ConfigSource` returned
  from `Generate` (written to `DbContext.cs`, or the config file's existing
  origin path if one exists — unchanged from today's writer behavior).
- **`AppDbContextFactory.cs`**: implements
  `IDesignTimeDbContextFactory<AppDbContext>`; reads
  `ConnectionStrings:DefaultConnection` out of `appsettings.json` by hand
  (`System.Text.Json`, no `Microsoft.Extensions.Configuration` package
  needed) so `dotnet ef` tooling can construct the context without a
  `Program.cs`/generic host.
- **`appsettings.json`** (only if `NeedsAppSettings`): a
  `ConnectionStrings.DefaultConnection` placeholder appropriate to
  `provider` (e.g.
  `Server=.;Database={ProjectName};Trusted_Connection=True;TrustServerCertificate=True`
  for SQL Server; `Host=localhost;Database={ProjectName};Username=postgres;Password=postgres`
  for PostgreSQL; `Data Source={ProjectName}.db` for SQLite).
- **`{ProjectName}.csproj`** (only if `NeedsCsproj`): SDK-style, `net10.0`,
  references `Microsoft.EntityFrameworkCore.Design` plus the one provider
  package matching `provider`.
- **`Program.cs`** (only if `NeedsProgram`): minimal top-level statement that
  builds the context via `AppDbContextFactory` and writes a ready message.
  Not required for `dotnet ef` tooling to work — a starting point, not load
  bearing.
- **`README.md`** (only if `NeedsReadme`): the three commands
  (`dotnet restore`, `dotnet ef migrations add Init`,
  `dotnet ef database update`) plus one line noting what was generated vs.
  preserved from the original upload.

`AppDbContextFactory.cs` is written whenever `NeedsDbContextWrapper` or
`NeedsCsproj` is true (i.e. whenever we're establishing a fresh runnable
setup) — if both are false there's an existing hand-written `DbContext` and
presumably existing tooling wiring, so we don't add a competing factory.

## Wiring into `Home.razor`

`DownloadZip` becomes:

```csharp
private async Task DownloadZip()
{
    if (_editor is null) return;

    var layout = _diagram is not null ? DiagramLayout.Capture(_diagram) : null;
    var configSource = _editor.ConfigSource;
    var passthrough = _passthroughFiles;

    if (_generateScaffold)
    {
        var plan = ScaffoldPlanner.Plan(configSource, passthrough);
        var result = ScaffoldGenerator.Generate(
            plan, configSource, _editor.Current.Entities, _projectName, _selectedProvider);
        configSource = result.ConfigSource;
        passthrough = MergePassthrough(passthrough, result.NewPassthroughFiles);
    }

    var bytes = ProjectArchiveWriter.Write(
        _editor.ClassSource, configSource, layout,
        _editor.EntityFileOrigins, _editor.ConfigFileOrigins, passthrough);
    // ... unchanged download-trigger code
}
```

No changes to `ProjectArchiveWriter` itself — it already accepts arbitrary
passthrough entries and a config source string.

## Testing

- `ScaffoldPlannerTests`: one case per missing/present combination of the
  four boolean flags, plus `NeedsDbContextWrapper` true/false against a
  bare-statement vs. a real-class config source, plus provider detection
  against a `.csproj` fixture per provider and against one with no provider
  reference.
- `ScaffoldGeneratorTests`: generated `AppDbContext.cs` / `.csproj` /
  `Program.cs` each parse with zero Roslyn diagnostics; `DbSet` naming
  pluralization cases (`Blog→Blogs`, `Box→Boxes`, `Category→Categories`);
  one case per provider verifying the correct package reference and
  connection-string shape.
- One end-to-end case per provider in the existing
  `ProjectArchiveRoundTripTests` style: render the shipped sample, check the
  scaffold box, download, assert every generated file is present and parses,
  and that no passthrough file already present in a synthetic "uploaded"
  fixture was altered.

## Non-Goals

- Entity-class namespaces (separate backlog item: "Namespace and `DbSet`
  name are unreachable").
- Adding missing `DbSet`s to an already hand-written `DbContext` class.
- Editing an existing `.csproj`'s package references (e.g. to add a missing
  provider to an upload that has a `.csproj` but no provider package).
- Any provider beyond SQL Server, PostgreSQL, SQLite.
- General-purpose English pluralization for `DbSet` names.
