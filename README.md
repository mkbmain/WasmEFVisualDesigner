# EF Schema Visualizer

A free, open-source, web-based visual designer for Entity Framework Core
models. Upload your existing entity classes and `OnModelCreating` (or
`IEntityTypeConfiguration<T>`) configuration, edit the model as an ER
diagram — drag entities, draw relationships, toggle keys/indexes, edit
properties — and download regenerated C# source. No IDE extension, no
server, no account, nothing ever leaves your browser.

**Live app:** https://mkbmain.github.io/WasmEFVisualDesigner/

## Why

There's no good way to visually design or review an EF Core model that
isn't tied to Visual Studio (EF Core Power Tools) or a paid product
(Devart Entity Developer). Rider users have no visual diagramming for EF
Core at all. Free design-first tools (EFDesigner) can't read an existing
database-first project back in, and every hosted alternative asks you to
trust a server with your code. This tool reads, edits, and regenerates
your model entirely client-side.

## How it works

1. Paste or `.zip`-upload your entity classes and EF configuration.
2. The app parses them with Roslyn's syntax-only APIs (no compilation, no
   assembly resolution — this is what makes it work under Blazor
   WebAssembly) into an in-memory model.
3. The model renders as an editable ER diagram — drag to reposition
   entities, drag between ports to draw relationships, click a property
   to expand its key/index/column/precision/default-value options.
4. Every edit is applied back to your original C# via a Roslyn syntax
   rewriter and immediately reparsed, so the diagram always reflects
   exactly what will be downloaded.
5. Download the regenerated `.zip`, or copy the C# straight out of the
   textareas.

## Non-goals

- No live database connection or provider-specific SQL. If you're
  starting database-first, run `dotnet ef dbcontext scaffold` first and
  feed the resulting C# in here.
- No migrations handling — `dotnet ef migrations add` remains your job.
- No accounts or server-side persistence. Fully stateless: upload, edit,
  download.

## Project layout

- `src/EfSchemaVisualizer.Core` — parsing, model merging, and syntax
  rewriting engine (Roslyn, no UI dependency).
- `src/EfSchemaVisualizer.Web` — Blazor WebAssembly app: diagram
  rendering (`Z.Blazor.Diagrams`), editing UI, zip upload/download.
- `tests/EfSchemaVisualizer.Core.Tests` — unit tests for the parsing/
  rewriting engine.
- `docs/backlog.md` — what's built and what's left.
- `docs/superpowers/specs/` — design docs for each feature slice.

## Getting started

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
# run the app locally
dotnet run --project src/EfSchemaVisualizer.Web

# run the test suite
dotnet test EfSchemaVisualizer.slnx
```

The app is fully static once published — `dotnet publish
src/EfSchemaVisualizer.Web` produces a `wwwroot` you can host anywhere.
A GitHub Actions workflow (`.github/workflows/deploy.yml`) builds and
deploys `main` to GitHub Pages automatically.

## Contributing

Issues and PRs welcome. Check `docs/backlog.md` for known gaps and
`docs/superpowers/specs/` for the design rationale behind existing
features before proposing changes to them.

## License

MIT — see [LICENSE](LICENSE).
