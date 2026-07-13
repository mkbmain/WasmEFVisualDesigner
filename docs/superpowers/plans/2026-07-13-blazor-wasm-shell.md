# Blazor WebAssembly Shell Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stand up a minimal Blazor WebAssembly shell (`src/EfSchemaVisualizer.Web`) that references `EfSchemaVisualizer.Core` and proves, by actually calling `EntityClassParser` at runtime in a browser, that Roslyn's syntax-tree APIs work under Mono WASM.

**Architecture:** A standalone Blazor WebAssembly project (no ASP.NET Core host, matching the "fully static, no backend" design), scaffolded from the `blazorwasm` template and trimmed to a single page. That page has a textarea, a Parse button, and an output area that calls `EntityClassParser.Parse` directly — no network round-trip, no server.

**Tech Stack:** .NET 10 SDK, Blazor WebAssembly (`Microsoft.AspNetCore.Components.WebAssembly` 10.0.5), `EfSchemaVisualizer.Core` (existing project reference, itself using `Microsoft.CodeAnalysis.CSharp` 5.6.0).

## Global Constraints

- Target framework: `net10.0` (matches `EfSchemaVisualizer.Core.csproj` and the rest of the repo).
- No backend/server component — standalone WASM only, per the original design spec's "fully static, no backend" architecture.
- No file/zip upload, no ER diagram, no CI/deploy pipeline, no automated test project — all explicitly out of scope for this slice (see `docs/superpowers/specs/2026-07-13-blazor-wasm-shell-design.md`).
- Solution file is `EfSchemaVisualizer.slnx` (new `.slnx` format); use `dotnet sln EfSchemaVisualizer.slnx add <path>` to register new projects — confirmed working against this repo's solution.

---

### Task 1: Scaffold the Blazor WebAssembly project and register it in the solution

**Files:**
- Create: `src/EfSchemaVisualizer.Web/` (entire scaffolded project — see file list below)
- Modify: `EfSchemaVisualizer.slnx`

**Interfaces:**
- Produces: a buildable, runnable Blazor WASM project at `src/EfSchemaVisualizer.Web/EfSchemaVisualizer.Web.csproj`, targeting `net10.0`, registered in the solution under a `/src/` folder alongside `EfSchemaVisualizer.Core`.

- [ ] **Step 1: Scaffold the project from the `blazorwasm` template**

Run from the repo root:

```bash
dotnet new blazorwasm -n EfSchemaVisualizer.Web -o src/EfSchemaVisualizer.Web --no-https
```

Expected: `The template "Blazor WebAssembly Standalone App" was created successfully.` and a restore that succeeds. This creates (among others):
- `src/EfSchemaVisualizer.Web/EfSchemaVisualizer.Web.csproj`
- `src/EfSchemaVisualizer.Web/Program.cs`
- `src/EfSchemaVisualizer.Web/App.razor`
- `src/EfSchemaVisualizer.Web/_Imports.razor`
- `src/EfSchemaVisualizer.Web/Layout/MainLayout.razor` (+ `.css`)
- `src/EfSchemaVisualizer.Web/Layout/NavMenu.razor` (+ `.css`)
- `src/EfSchemaVisualizer.Web/Pages/Home.razor`
- `src/EfSchemaVisualizer.Web/Pages/Counter.razor`
- `src/EfSchemaVisualizer.Web/Pages/Weather.razor`
- `src/EfSchemaVisualizer.Web/Pages/NotFound.razor`
- `src/EfSchemaVisualizer.Web/Properties/launchSettings.json`
- `src/EfSchemaVisualizer.Web/wwwroot/index.html`, `wwwroot/css/app.css`, `wwwroot/favicon.png`, `wwwroot/icon-192.png`, `wwwroot/lib/bootstrap/...`, `wwwroot/sample-data/weather.json`

- [ ] **Step 2: Register the project in the solution**

```bash
dotnet sln EfSchemaVisualizer.slnx add src/EfSchemaVisualizer.Web/EfSchemaVisualizer.Web.csproj --solution-folder src
```

Expected: `Project ... added to the solution.` Confirm with:

```bash
dotnet sln EfSchemaVisualizer.slnx list
```

Expected output includes all three projects:
```
src/EfSchemaVisualizer.Core/EfSchemaVisualizer.Core.csproj
src/EfSchemaVisualizer.Web/EfSchemaVisualizer.Web.csproj
tests/EfSchemaVisualizer.Core.Tests/EfSchemaVisualizer.Core.Tests.csproj
```

- [ ] **Step 3: Verify the scaffolded app builds**

```bash
dotnet build src/EfSchemaVisualizer.Web/EfSchemaVisualizer.Web.csproj
```

Expected: `Build succeeded.` with 0 errors.

- [ ] **Step 4: Commit**

```bash
git add src/EfSchemaVisualizer.Web EfSchemaVisualizer.slnx
git commit -m "Scaffold Blazor WebAssembly shell project"
```

---

### Task 2: Trim the scaffold to the minimal shell

The template ships sample pages (`Counter`, `Weather`) and nav links that don't belong in this slice — the design spec is a single demo page with no styling investment beyond what the template gives for free.

**Files:**
- Delete: `src/EfSchemaVisualizer.Web/Pages/Counter.razor`
- Delete: `src/EfSchemaVisualizer.Web/Pages/Weather.razor`
- Delete: `src/EfSchemaVisualizer.Web/wwwroot/sample-data/weather.json`
- Modify: `src/EfSchemaVisualizer.Web/Layout/NavMenu.razor`
- Modify: `src/EfSchemaVisualizer.Web/Program.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: a scaffold with only the Home page reachable, no dead sample-data references, no unused `HttpClient` DI registration (the parse demo makes no network calls).

- [ ] **Step 1: Delete the sample pages and their data file**

```bash
rm src/EfSchemaVisualizer.Web/Pages/Counter.razor
rm src/EfSchemaVisualizer.Web/Pages/Weather.razor
rm src/EfSchemaVisualizer.Web/wwwroot/sample-data/weather.json
```

- [ ] **Step 2: Remove the Counter/Weather links from the nav menu**

Replace the contents of `src/EfSchemaVisualizer.Web/Layout/NavMenu.razor` with:

```razor
<div class="top-row ps-3 navbar navbar-dark">
    <div class="container-fluid">
        <a class="navbar-brand" href="">EfSchemaVisualizer</a>
        <button title="Navigation menu" class="navbar-toggler" @onclick="ToggleNavMenu">
            <span class="navbar-toggler-icon"></span>
        </button>
    </div>
</div>

<div class="@NavMenuCssClass nav-scrollable" @onclick="ToggleNavMenu">
    <nav class="nav flex-column">
        <div class="nav-item px-3">
            <NavLink class="nav-link" href="" Match="NavLinkMatch.All">
                <span class="bi bi-house-door-fill-nav-menu" aria-hidden="true"></span> Home
            </NavLink>
        </div>
    </nav>
</div>

@code {
    private bool collapseNavMenu = true;

    private string? NavMenuCssClass => collapseNavMenu ? "collapse" : null;

    private void ToggleNavMenu()
    {
        collapseNavMenu = !collapseNavMenu;
    }
}
```

- [ ] **Step 3: Remove the unused `HttpClient` registration from `Program.cs`**

Replace the contents of `src/EfSchemaVisualizer.Web/Program.cs` with:

```csharp
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using EfSchemaVisualizer.Web;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

await builder.Build().RunAsync();
```

- [ ] **Step 4: Verify the app still builds**

```bash
dotnet build src/EfSchemaVisualizer.Web/EfSchemaVisualizer.Web.csproj
```

Expected: `Build succeeded.` with 0 errors. (A build warning is not expected here — if `Home.razor` or `App.razor` reference `Counter`/`Weather` routes anywhere, fix those references now; the template does not link to them from `App.razor` by default, only from `NavMenu.razor`, which was just edited.)

- [ ] **Step 5: Commit**

```bash
git add src/EfSchemaVisualizer.Web
git commit -m "Trim Blazor shell scaffold to a single page"
```

---

### Task 3: Reference EfSchemaVisualizer.Core from the Web project

**Files:**
- Modify: `src/EfSchemaVisualizer.Web/EfSchemaVisualizer.Web.csproj`

**Interfaces:**
- Consumes: `EfSchemaVisualizer.Core` public API — specifically `EfSchemaVisualizer.Core.Parsing.EntityClassParser`, `EfSchemaVisualizer.Core.Parsing.ParseResult<T>`, `EfSchemaVisualizer.Core.Parsing.Diagnostic`, `EfSchemaVisualizer.Core.Model.EntityModel`, `EfSchemaVisualizer.Core.Model.PropertyModel` (all already defined in `src/EfSchemaVisualizer.Core`).
- Produces: `EfSchemaVisualizer.Core` types resolvable from `EfSchemaVisualizer.Web` code, including under WASM compilation — this is the change Task 5's manual verification checks actually runs correctly in a browser.

- [ ] **Step 1: Add the project reference**

```bash
dotnet add src/EfSchemaVisualizer.Web/EfSchemaVisualizer.Web.csproj reference src/EfSchemaVisualizer.Core/EfSchemaVisualizer.Core.csproj
```

Expected: `Reference ... added to the project.`

- [ ] **Step 2: Verify the reference resolves under a WASM build**

```bash
dotnet build src/EfSchemaVisualizer.Web/EfSchemaVisualizer.Web.csproj
```

Expected: `Build succeeded.` with 0 errors. This confirms `Microsoft.CodeAnalysis.CSharp` (pulled in transitively via `EfSchemaVisualizer.Core`) at least compiles for the `browser-wasm` RID — it does not yet prove correct *runtime* behavior, which Task 5 checks in an actual browser.

- [ ] **Step 3: Commit**

```bash
git add src/EfSchemaVisualizer.Web/EfSchemaVisualizer.Web.csproj
git commit -m "Reference EfSchemaVisualizer.Core from the Web project"
```

---

### Task 4: Build the parse demo page

**Files:**
- Modify: `src/EfSchemaVisualizer.Web/Pages/Home.razor`

**Interfaces:**
- Consumes: `EntityClassParser.Parse(string sourceCode) : ParseResult<IReadOnlyList<EntityModel>>` (`src/EfSchemaVisualizer.Core/Parsing/EntityClassParser.cs:12`); `ParseResult<T>` has `Value` and `Diagnostics` (`src/EfSchemaVisualizer.Core/Parsing/ParseResult.cs:5`); `EntityModel` has `Name` and `Properties` (`src/EfSchemaVisualizer.Core/Model/EntityModel.cs:5-11`); `PropertyModel` has `Name`, `ClrType`, `IsNullable`, `MaxLength` (`src/EfSchemaVisualizer.Core/Model/PropertyModel.cs:3-13`); `Diagnostic` has `Code` and `Message` (`src/EfSchemaVisualizer.Core/Parsing/Diagnostic.cs:5-10`).
- Produces: the working end-to-end demo this whole slice exists to prove.

- [ ] **Step 1: Replace `Home.razor` with the parse demo**

```razor
@page "/"
@using EfSchemaVisualizer.Core.Parsing

<PageTitle>EF Schema Visualizer</PageTitle>

<h1>EF Schema Visualizer — Parser Demo</h1>

<p>Paste a C# entity class below and click Parse to run it through <code>EntityClassParser</code> directly in the browser.</p>

<textarea @bind="_sourceCode" @bind:event="oninput" rows="12" style="width: 100%; font-family: monospace;"></textarea>

<p>
    <button class="btn btn-primary" @onclick="Parse">Parse</button>
</p>

@if (_error is not null)
{
    <pre style="color: red;">@_error</pre>
}
else if (_result is not null)
{
    <pre>@FormatResult(_result)</pre>
}

@code {
    private string _sourceCode = """
        public class Person
        {
            public int Id { get; set; }
            public string Name { get; set; } = "";
            public int? Age { get; set; }
        }
        """;

    private ParseResult<IReadOnlyList<EfSchemaVisualizer.Core.Model.EntityModel>>? _result;
    private string? _error;

    private void Parse()
    {
        _error = null;
        _result = null;

        try
        {
            var parser = new EntityClassParser();
            _result = parser.Parse(_sourceCode);
        }
        catch (Exception ex)
        {
            _error = ex.ToString();
        }
    }

    private static string FormatResult(ParseResult<IReadOnlyList<EfSchemaVisualizer.Core.Model.EntityModel>> result)
    {
        var lines = new List<string>();

        foreach (var entity in result.Value)
        {
            lines.Add($"Entity: {entity.Name}");
            foreach (var property in entity.Properties)
            {
                var nullable = property.IsNullable ? "?" : "";
                lines.Add($"  {property.Name}: {property.ClrType}{nullable}");
            }
        }

        if (result.Diagnostics.Count > 0)
        {
            lines.Add("");
            lines.Add("Diagnostics:");
            foreach (var diagnostic in result.Diagnostics)
            {
                lines.Add($"  [{diagnostic.Code}] {diagnostic.Message}");
            }
        }

        return string.Join(Environment.NewLine, lines);
    }
}
```

- [ ] **Step 2: Verify the app builds**

```bash
dotnet build src/EfSchemaVisualizer.Web/EfSchemaVisualizer.Web.csproj
```

Expected: `Build succeeded.` with 0 errors.

- [ ] **Step 3: Commit**

```bash
git add src/EfSchemaVisualizer.Web/Pages/Home.razor
git commit -m "Add parser demo page to the Blazor shell"
```

---

### Task 5: Manual browser verification and risk-check writeup

This task exercises the actual goal of the slice: confirming Roslyn's syntax-tree APIs work under Mono WASM in a real browser, and recording a first data point on payload size / load time.

**Files:**
- Modify: `docs/superpowers/specs/2026-07-13-blazor-wasm-shell-design.md` (fill in the payload size / load time result)

**Interfaces:**
- Consumes: the running app produced by Tasks 1-4.
- Produces: a filled-in verification record in the design doc; no code changes.

- [ ] **Step 1: Publish the app**

```bash
dotnet publish src/EfSchemaVisualizer.Web/EfSchemaVisualizer.Web.csproj -c Release -o /tmp/claude-0/-root-RiderProjects-EfSchemaVisualizer/a9e4845b-ed79-4c0c-863e-e7a571d56977/scratchpad/web-publish
```

Expected: `Build succeeded.` and a `wwwroot` directory under the publish output containing `_framework/` (the WASM runtime + assemblies).

- [ ] **Step 2: Measure the published payload size**

```bash
du -sh /tmp/claude-0/-root-RiderProjects-EfSchemaVisualizer/a9e4845b-ed79-4c0c-863e-e7a571d56977/scratchpad/web-publish/wwwroot/_framework
```

Note the reported size — this goes into the design doc in Step 5.

- [ ] **Step 3: Serve the published output locally**

```bash
cd /tmp/claude-0/-root-RiderProjects-EfSchemaVisualizer/a9e4845b-ed79-4c0c-863e-e7a571d56977/scratchpad/web-publish/wwwroot && python3 -m http.server 8080
```

(Leave running in the background; a WASM app requires a real HTTP server, not `file://`, because of MIME-type requirements for `.wasm`/`.dll` assets.)

- [ ] **Step 4: Open in a browser and confirm the demo works**

Open `http://localhost:8080` in a browser. Confirm, noting the rough time from navigation to interactive:
1. The page loads and shows the pre-filled `Person` class in the textarea.
2. Clicking "Parse" without editing the textarea shows output listing `Entity: Person` with its three properties (`Id: int`, `Name: string`, `Age: int?`).
3. Replacing the textarea contents with `public interface IFoo {}` and clicking Parse shows a `Diagnostics:` section containing the `NoEntityDeclarations` code (confirms the no-entity-declarations path from `EntityClassParser.cs:23-35` works at runtime).
4. Replacing the textarea contents with clearly invalid C# (e.g. `this is not c#`) and clicking Parse does not crash the page — Roslyn's `ParseText` tolerates malformed input and produces a syntax tree with errors rather than throwing, so this should still render a result (likely no entities found) rather than hitting the `catch` block. Confirm the page stays responsive either way.

- [ ] **Step 5: Record the result in the design doc**

Edit `docs/superpowers/specs/2026-07-13-blazor-wasm-shell-design.md`, replacing the line:

```
5. Record the published payload size and a rough first-load time in this
   design doc as the result of the Roslyn-in-WASM risk check (filled in
   during implementation).
```

with the actual measured values, e.g.:

```
5. Result (recorded YYYY-MM-DD): published `_framework` payload was
   <SIZE>; first load to interactive on localhost was approximately
   <TIME>. Roslyn's syntax-tree APIs (`CSharpSyntaxTree.ParseText`,
   `EntityClassParser.Parse`) executed correctly under Mono WASM with no
   runtime errors across the valid-entity, no-entity, and malformed-input
   cases.
```

(Adjust the wording if any case in Step 4 behaved unexpectedly — this must reflect what was actually observed, not the expected outcome.)

- [ ] **Step 6: Stop the local server and clean up the publish output**

Stop the `python3 -m http.server` process (Ctrl-C, or `kill` the background job), then:

```bash
rm -rf /tmp/claude-0/-root-RiderProjects-EfSchemaVisualizer/a9e4845b-ed79-4c0c-863e-e7a571d56977/scratchpad/web-publish
```

- [ ] **Step 7: Commit**

```bash
git add docs/superpowers/specs/2026-07-13-blazor-wasm-shell-design.md
git commit -m "Record Roslyn-in-WASM verification results"
```
