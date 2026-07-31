# Runnable Project Scaffold Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add an opt-in "Generate runnable project scaffold" checkbox to the download flow that fills in whatever of `.csproj`, `AppDbContext.cs`, `AppDbContextFactory.cs`, `appsettings.json`, `Program.cs`, and `README.md` are missing, for SQL Server, PostgreSQL, or SQLite, without ever overwriting a file the user already has.

**Architecture:** Two new pure-function modules in `EfSchemaVisualizer.Core.Archive` — `ScaffoldPlanner` (detects what's missing) and `ScaffoldGenerator`/`ScaffoldTemplates` (generates only what's missing) — wired into `Home.razor`'s existing `DownloadZip` handler ahead of the existing `ProjectArchiveWriter.Write` call. No changes to `ProjectArchiveWriter` itself.

**Tech Stack:** C# / .NET 10, Roslyn (`Microsoft.CodeAnalysis.CSharp`) for the bare-statements-vs-real-class detection, xUnit for tests, Blazor WebAssembly for the UI.

## Global Constraints

- Never overwrite or modify a file that already exists in `_passthroughFiles`, and never modify a config source that's already a real `DbContext`-derived class (spec: Overwrite Policy).
- Provider dropdown appears only when no `.csproj` already exists in `_passthroughFiles` (spec: UI).
- Target framework for generated `.csproj` is `net10.0`, matching the rest of the solution.
- Three providers only: SQL Server, PostgreSQL, SQLite (spec: Non-Goals).
- `DbSet` pluralization is a simple heuristic (`+s` / `+es` after s,x,z,ch,sh / `y→ies`), not general English pluralization (spec: Non-Goals).
- Owned entities (`EntityModel.IsOwned == true`) never get their own `DbSet`.

---

### Task 1: `ScaffoldPlanner` — detect what's missing

**Files:**
- Create: `src/EfSchemaVisualizer.Core/Archive/ScaffoldPlanner.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Archive/ScaffoldPlannerTests.cs`

**Interfaces:**
- Produces: `ScaffoldProvider` enum (`SqlServer`, `PostgreSql`, `Sqlite`); `ScaffoldPlan` record (`bool NeedsCsproj, bool NeedsProgram, bool NeedsAppSettings, bool NeedsReadme, bool NeedsDbContextWrapper, ScaffoldProvider? DetectedProvider`); `ScaffoldPlanner.Plan(string configSource, IReadOnlyDictionary<string, byte[]>? passthroughFiles) : ScaffoldPlan`. Later tasks consume all of these.

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Collections.Generic;
using System.Text;
using EfSchemaVisualizer.Core.Archive;
using Xunit;

namespace EfSchemaVisualizer.Core.Tests.Archive;

public class ScaffoldPlannerTests
{
    private static IReadOnlyDictionary<string, byte[]> Files(params (string Path, string Content)[] files)
    {
        var dict = new Dictionary<string, byte[]>();
        foreach (var (path, content) in files)
        {
            dict[path] = Encoding.UTF8.GetBytes(content);
        }

        return dict;
    }

    [Fact]
    public void Plan_NoPassthroughFiles_NeedsEverything()
    {
        var plan = ScaffoldPlanner.Plan(
            "modelBuilder.Entity<Blog>(entity => entity.HasKey(e => e.Id));",
            passthroughFiles: null);

        Assert.True(plan.NeedsCsproj);
        Assert.True(plan.NeedsProgram);
        Assert.True(plan.NeedsAppSettings);
        Assert.True(plan.NeedsReadme);
        Assert.True(plan.NeedsDbContextWrapper);
        Assert.Null(plan.DetectedProvider);
    }

    [Fact]
    public void Plan_CsprojAlreadyPresent_NeedsCsprojIsFalse()
    {
        var files = Files(("MyApp.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>"));

        var plan = ScaffoldPlanner.Plan("modelBuilder.Entity<Blog>(e => e.HasKey(x => x.Id));", files);

        Assert.False(plan.NeedsCsproj);
    }

    [Fact]
    public void Plan_ProgramCsAlreadyPresentAtNestedPath_NeedsProgramIsFalse()
    {
        var files = Files(("src/Program.cs", "// existing"));

        var plan = ScaffoldPlanner.Plan("modelBuilder.Entity<Blog>(e => e.HasKey(x => x.Id));", files);

        Assert.False(plan.NeedsProgram);
    }

    [Fact]
    public void Plan_AppSettingsAlreadyPresent_NeedsAppSettingsIsFalse()
    {
        var files = Files(("appsettings.json", "{}"));

        var plan = ScaffoldPlanner.Plan("modelBuilder.Entity<Blog>(e => e.HasKey(x => x.Id));", files);

        Assert.False(plan.NeedsAppSettings);
    }

    [Fact]
    public void Plan_ReadmeAlreadyPresent_NeedsReadmeIsFalse()
    {
        var files = Files(("README.md", "# existing"));

        var plan = ScaffoldPlanner.Plan("modelBuilder.Entity<Blog>(e => e.HasKey(x => x.Id));", files);

        Assert.False(plan.NeedsReadme);
    }

    [Fact]
    public void Plan_ConfigSourceIsRealDbContextClass_NeedsDbContextWrapperIsFalse()
    {
        const string configSource = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Blog>(e => e.HasKey(x => x.Id));
                }
            }
            """;

        var plan = ScaffoldPlanner.Plan(configSource, passthroughFiles: null);

        Assert.False(plan.NeedsDbContextWrapper);
    }

    [Fact]
    public void Plan_ConfigSourceIsBareTopLevelStatements_NeedsDbContextWrapperIsTrue()
    {
        var plan = ScaffoldPlanner.Plan(
            "modelBuilder.Entity<Blog>(e => e.HasKey(x => x.Id));",
            passthroughFiles: null);

        Assert.True(plan.NeedsDbContextWrapper);
    }

    [Theory]
    [InlineData("Microsoft.EntityFrameworkCore.SqlServer", ScaffoldProvider.SqlServer)]
    [InlineData("Npgsql.EntityFrameworkCore.PostgreSQL", ScaffoldProvider.PostgreSql)]
    [InlineData("Microsoft.EntityFrameworkCore.Sqlite", ScaffoldProvider.Sqlite)]
    public void Plan_CsprojReferencesKnownProvider_DetectsIt(string packageName, ScaffoldProvider expected)
    {
        var files = Files(("MyApp.csproj",
            $"<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><PackageReference Include=\"{packageName}\" Version=\"10.0.0\" /></ItemGroup></Project>"));

        var plan = ScaffoldPlanner.Plan("modelBuilder.Entity<Blog>(e => e.HasKey(x => x.Id));", files);

        Assert.Equal(expected, plan.DetectedProvider);
    }

    [Fact]
    public void Plan_CsprojReferencesNoKnownProvider_DetectedProviderIsNull()
    {
        var files = Files(("MyApp.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>"));

        var plan = ScaffoldPlanner.Plan("modelBuilder.Entity<Blog>(e => e.HasKey(x => x.Id));", files);

        Assert.Null(plan.DetectedProvider);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter ScaffoldPlannerTests`
Expected: FAIL with compile errors — `ScaffoldPlanner`/`ScaffoldPlan`/`ScaffoldProvider` don't exist yet.

- [ ] **Step 3: Implement `ScaffoldPlanner`**

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EfSchemaVisualizer.Core.Archive;

public enum ScaffoldProvider
{
    SqlServer,
    PostgreSql,
    Sqlite,
}

public sealed record ScaffoldPlan(
    bool NeedsCsproj,
    bool NeedsProgram,
    bool NeedsAppSettings,
    bool NeedsReadme,
    bool NeedsDbContextWrapper,
    ScaffoldProvider? DetectedProvider);

/// Determines which scaffold pieces (.csproj, Program.cs, appsettings.json, README.md, a real
/// DbContext class) are missing from a project about to be downloaded, so ScaffoldGenerator can
/// fill in only what's absent and never overwrite a file the user already has.
public static class ScaffoldPlanner
{
    public static ScaffoldPlan Plan(
        string configSource,
        IReadOnlyDictionary<string, byte[]>? passthroughFiles)
    {
        var files = passthroughFiles ?? new Dictionary<string, byte[]>();

        var csprojEntry = files.FirstOrDefault(kvp =>
            kvp.Key.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase));
        var needsCsproj = csprojEntry.Key is null;

        var needsProgram = !files.Keys.Any(path =>
            FileNameOf(path).Equals("Program.cs", StringComparison.OrdinalIgnoreCase));
        var needsAppSettings = !files.Keys.Any(path =>
            FileNameOf(path).Equals("appsettings.json", StringComparison.OrdinalIgnoreCase));
        var needsReadme = !files.Keys.Any(path =>
            FileNameOf(path).Equals("README.md", StringComparison.OrdinalIgnoreCase));

        var needsDbContextWrapper = !HasDbContextClass(configSource);

        ScaffoldProvider? detectedProvider = null;
        if (!needsCsproj)
        {
            var csprojText = Encoding.UTF8.GetString(csprojEntry.Value);
            detectedProvider = DetectProvider(csprojText);
        }

        return new ScaffoldPlan(
            needsCsproj, needsProgram, needsAppSettings, needsReadme, needsDbContextWrapper, detectedProvider);
    }

    private static string FileNameOf(string path)
    {
        var slash = path.LastIndexOfAny(new[] { '/', '\\' });
        return slash >= 0 ? path[(slash + 1)..] : path;
    }

    private static bool HasDbContextClass(string configSource)
    {
        if (string.IsNullOrWhiteSpace(configSource))
        {
            return false;
        }

        var root = CSharpSyntaxTree.ParseText(configSource).GetCompilationUnitRoot();

        return root.DescendantNodes()
            .OfType<ClassDeclarationSyntax>()
            .Any(classDeclaration => classDeclaration.BaseList?.Types.Any(baseType =>
                baseType.Type.ToString().Contains("DbContext", StringComparison.Ordinal)) ?? false);
    }

    private static ScaffoldProvider? DetectProvider(string csprojText)
    {
        if (csprojText.Contains("Microsoft.EntityFrameworkCore.SqlServer", StringComparison.OrdinalIgnoreCase))
        {
            return ScaffoldProvider.SqlServer;
        }

        if (csprojText.Contains("Npgsql.EntityFrameworkCore.PostgreSQL", StringComparison.OrdinalIgnoreCase))
        {
            return ScaffoldProvider.PostgreSql;
        }

        if (csprojText.Contains("Microsoft.EntityFrameworkCore.Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            return ScaffoldProvider.Sqlite;
        }

        return null;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter ScaffoldPlannerTests`
Expected: PASS, all 10 tests green.

- [ ] **Step 5: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Archive/ScaffoldPlanner.cs tests/EfSchemaVisualizer.Core.Tests/Archive/ScaffoldPlannerTests.cs
git commit -m "Add ScaffoldPlanner to detect missing project scaffold pieces"
```

---

### Task 2: `ScaffoldGenerator` — `DbSet` pluralization and the `AppDbContext` wrapper

**Files:**
- Create: `src/EfSchemaVisualizer.Core/Archive/ScaffoldGenerator.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Archive/ScaffoldGeneratorTests.cs`

**Interfaces:**
- Consumes: `EntityModel` (`EfSchemaVisualizer.Core.Model`) — uses `.Name` and `.IsOwned`.
- Produces: `ScaffoldGenerator.Pluralize(string name) : string`; `ScaffoldGenerator.BuildDbContextWrapper(string configSource, IReadOnlyList<EntityModel> entities, string projectName) : string`. Task 4 adds `Generate(...)` to this same class.

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Collections.Generic;
using System.Linq;
using EfSchemaVisualizer.Core.Archive;
using EfSchemaVisualizer.Core.Model;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace EfSchemaVisualizer.Core.Tests.Archive;

public class ScaffoldGeneratorTests
{
    [Theory]
    [InlineData("Blog", "Blogs")]
    [InlineData("Box", "Boxes")]
    [InlineData("Category", "Categories")]
    [InlineData("Address", "Addresses")]
    [InlineData("Bus", "Buses")]
    [InlineData("Key", "Keys")]
    public void Pluralize_AppliesHeuristicRules(string singular, string expectedPlural)
    {
        Assert.Equal(expectedPlural, ScaffoldGenerator.Pluralize(singular));
    }

    [Fact]
    public void BuildDbContextWrapper_ProducesParseableClassWithNoDiagnostics()
    {
        var entities = new List<EntityModel>
        {
            new("Blog", new List<PropertyModel>()),
            new("Post", new List<PropertyModel>()),
        };

        const string configSource = """
            modelBuilder.Entity<Blog>(entity =>
            {
                entity.HasKey(e => e.Id);
            });
            """;

        var wrapper = ScaffoldGenerator.BuildDbContextWrapper(configSource, entities, "MyApp");

        var tree = CSharpSyntaxTree.ParseText(wrapper);
        Assert.Empty(tree.GetDiagnostics());

        Assert.Contains("namespace MyApp;", wrapper);
        Assert.Contains("public class AppDbContext : DbContext", wrapper);
        Assert.Contains("public DbSet<Blog> Blogs => Set<Blog>();", wrapper);
        Assert.Contains("public DbSet<Post> Posts => Set<Post>();", wrapper);
        Assert.Contains("entity.HasKey(e => e.Id);", wrapper);
    }

    [Fact]
    public void BuildDbContextWrapper_ExcludesOwnedEntitiesFromDbSets()
    {
        var entities = new List<EntityModel>
        {
            new("Order", new List<PropertyModel>()),
            new("Address", new List<PropertyModel>()) { IsOwned = true },
        };

        var wrapper = ScaffoldGenerator.BuildDbContextWrapper(
            "modelBuilder.Entity<Order>(e => e.HasKey(x => x.Id));", entities, "MyApp");

        Assert.Contains("public DbSet<Order> Orders => Set<Order>();", wrapper);
        Assert.DoesNotContain("DbSet<Address>", wrapper);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter ScaffoldGeneratorTests`
Expected: FAIL — `ScaffoldGenerator` doesn't exist yet.

- [ ] **Step 3: Implement `ScaffoldGenerator` (partial — pluralization and wrapper only)**

```csharp
using System.Collections.Generic;
using System.Linq;
using System.Text;
using EfSchemaVisualizer.Core.Model;

namespace EfSchemaVisualizer.Core.Archive;

/// Fills in whatever scaffold pieces a ScaffoldPlan marked missing. Never regenerates or modifies
/// a file that already exists — see ScaffoldPlanner for what "missing" means.
public static class ScaffoldGenerator
{
    public static string Pluralize(string name)
    {
        if (name.Length == 0)
        {
            return name;
        }

        var lower = name.ToLowerInvariant();
        if (lower.EndsWith("s") || lower.EndsWith("x") || lower.EndsWith("z")
            || lower.EndsWith("ch") || lower.EndsWith("sh"))
        {
            return name + "es";
        }

        if (lower.EndsWith("y") && name.Length > 1 && !IsVowel(lower[^2]))
        {
            return name[..^1] + "ies";
        }

        return name + "s";
    }

    private static bool IsVowel(char c) => "aeiou".IndexOf(c) >= 0;

    public static string BuildDbContextWrapper(
        string configSource,
        IReadOnlyList<EntityModel> entities,
        string projectName)
    {
        var dbSets = new StringBuilder();
        foreach (var entity in entities.Where(e => !e.IsOwned))
        {
            dbSets.AppendLine(
                $"    public DbSet<{entity.Name}> {Pluralize(entity.Name)} => Set<{entity.Name}>();");
        }

        var indentedConfig = IndentBody(configSource, "        ");

        return $$"""
            using Microsoft.EntityFrameworkCore;

            namespace {{projectName}};

            public class AppDbContext : DbContext
            {
                public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
                {
                }

            {{dbSets.ToString().TrimEnd('\r', '\n')}}

                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
            {{indentedConfig}}
                }
            }
            """;
    }

    private static string IndentBody(string source, string indent)
    {
        var lines = source.Replace("\r\n", "\n").Split('\n');
        return string.Join("\n", lines.Select(line => line.Length == 0 ? line : indent + line));
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter ScaffoldGeneratorTests`
Expected: PASS, all 8 tests green.

- [ ] **Step 5: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Archive/ScaffoldGenerator.cs tests/EfSchemaVisualizer.Core.Tests/Archive/ScaffoldGeneratorTests.cs
git commit -m "Add ScaffoldGenerator DbSet pluralization and AppDbContext wrapper"
```

---

### Task 3: `ScaffoldTemplates` — per-provider file templates

**Files:**
- Create: `src/EfSchemaVisualizer.Core/Archive/ScaffoldTemplates.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Archive/ScaffoldTemplatesTests.cs`

**Interfaces:**
- Consumes: `ScaffoldProvider` (Task 1).
- Produces: `ScaffoldTemplates.Csproj(string projectName, ScaffoldProvider provider) : string`; `.AppSettings(string projectName, ScaffoldProvider provider) : string`; `.DbContextFactory(string projectName, ScaffoldProvider provider) : string`; `.Program(string projectName) : string`; `.Readme(string projectName) : string`. Task 4 consumes all five.

- [ ] **Step 1: Write the failing tests**

```csharp
using EfSchemaVisualizer.Core.Archive;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace EfSchemaVisualizer.Core.Tests.Archive;

public class ScaffoldTemplatesTests
{
    [Theory]
    [InlineData(ScaffoldProvider.SqlServer, "Microsoft.EntityFrameworkCore.SqlServer")]
    [InlineData(ScaffoldProvider.PostgreSql, "Npgsql.EntityFrameworkCore.PostgreSQL")]
    [InlineData(ScaffoldProvider.Sqlite, "Microsoft.EntityFrameworkCore.Sqlite")]
    public void Csproj_ReferencesCorrectProviderPackage(ScaffoldProvider provider, string expectedPackage)
    {
        var csproj = ScaffoldTemplates.Csproj("MyApp", provider);

        Assert.Contains(expectedPackage, csproj);
        Assert.Contains("Microsoft.EntityFrameworkCore.Design", csproj);
        Assert.Contains("<TargetFramework>net10.0</TargetFramework>", csproj);
        Assert.Contains("<RootNamespace>MyApp</RootNamespace>", csproj);
        Assert.Contains("appsettings.json", csproj);
    }

    [Theory]
    [InlineData(ScaffoldProvider.SqlServer, "Server=.;Database=MyApp;")]
    [InlineData(ScaffoldProvider.PostgreSql, "Host=localhost;Database=MyApp;")]
    [InlineData(ScaffoldProvider.Sqlite, "Data Source=MyApp.db")]
    public void AppSettings_UsesProviderSpecificConnectionString(ScaffoldProvider provider, string expectedFragment)
    {
        var json = ScaffoldTemplates.AppSettings("MyApp", provider);

        Assert.Contains(expectedFragment, json);
        Assert.Contains("\"ConnectionStrings\"", json);
        Assert.Contains("\"DefaultConnection\"", json);
    }

    [Theory]
    [InlineData(ScaffoldProvider.SqlServer, "UseSqlServer")]
    [InlineData(ScaffoldProvider.PostgreSql, "UseNpgsql")]
    [InlineData(ScaffoldProvider.Sqlite, "UseSqlite")]
    public void DbContextFactory_CallsProviderSpecificUseMethod(ScaffoldProvider provider, string expectedMethod)
    {
        var factory = ScaffoldTemplates.DbContextFactory("MyApp", provider);

        var tree = CSharpSyntaxTree.ParseText(factory);
        Assert.Empty(tree.GetDiagnostics());

        Assert.Contains($"optionsBuilder.{expectedMethod}(connectionString);", factory);
        Assert.Contains("IDesignTimeDbContextFactory<AppDbContext>", factory);
        Assert.Contains("namespace MyApp;", factory);
    }

    [Fact]
    public void Program_BuildsContextAndParsesCleanly()
    {
        var program = ScaffoldTemplates.Program("MyApp");

        var tree = CSharpSyntaxTree.ParseText(program);
        Assert.Empty(tree.GetDiagnostics());
        Assert.Contains("new AppDbContextFactory().CreateDbContext(args)", program);
        Assert.Contains("using MyApp;", program);
    }

    [Fact]
    public void Readme_ContainsTheThreeCommands()
    {
        var readme = ScaffoldTemplates.Readme("MyApp");

        Assert.Contains("dotnet restore", readme);
        Assert.Contains("dotnet ef migrations add Init", readme);
        Assert.Contains("dotnet ef database update", readme);
        Assert.Contains("# MyApp", readme);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter ScaffoldTemplatesTests`
Expected: FAIL — `ScaffoldTemplates` doesn't exist yet.

- [ ] **Step 3: Implement `ScaffoldTemplates`**

```csharp
using System;

namespace EfSchemaVisualizer.Core.Archive;

/// Per-provider file text for ScaffoldGenerator. Pure string templates — no Roslyn, no I/O.
public static class ScaffoldTemplates
{
    public static string Csproj(string projectName, ScaffoldProvider provider)
    {
        var packageName = ProviderPackageName(provider);
        return $$"""
            <Project Sdk="Microsoft.NET.Sdk">

              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <RootNamespace>{{projectName}}</RootNamespace>
              </PropertyGroup>

              <ItemGroup>
                <PackageReference Include="Microsoft.EntityFrameworkCore.Design" Version="10.0.0">
                  <PrivateAssets>all</PrivateAssets>
                  <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
                </PackageReference>
                <PackageReference Include="{{packageName}}" Version="10.0.0" />
              </ItemGroup>

              <ItemGroup>
                <None Update="appsettings.json">
                  <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
                </None>
              </ItemGroup>

            </Project>
            """;
    }

    public static string AppSettings(string projectName, ScaffoldProvider provider)
    {
        var connectionString = ProviderConnectionString(projectName, provider);
        return $$"""
            {
              "ConnectionStrings": {
                "DefaultConnection": "{{connectionString}}"
              }
            }
            """;
    }

    public static string DbContextFactory(string projectName, ScaffoldProvider provider)
    {
        var useMethod = ProviderUseMethod(provider);
        return $$"""
            using System;
            using System.IO;
            using System.Text.Json;
            using Microsoft.EntityFrameworkCore;
            using Microsoft.EntityFrameworkCore.Design;

            namespace {{projectName}};

            public class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
            {
                public AppDbContext CreateDbContext(string[] args)
                {
                    var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "appsettings.json"));
                    using var document = JsonDocument.Parse(json);
                    var connectionString = document.RootElement
                        .GetProperty("ConnectionStrings")
                        .GetProperty("DefaultConnection")
                        .GetString();

                    var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();
                    optionsBuilder.{{useMethod}}(connectionString);

                    return new AppDbContext(optionsBuilder.Options);
                }
            }
            """;
    }

    public static string Program(string projectName)
    {
        return $$"""
            using {{projectName}};

            var context = new AppDbContextFactory().CreateDbContext(args);
            Console.WriteLine("AppDbContext ready. Run 'dotnet ef migrations add Init' to create your first migration.");
            """;
    }

    public static string Readme(string projectName)
    {
        return $$"""
            # {{projectName}}

            Generated by EF Schema Visualizer.

            ## Getting started

            1. `dotnet restore`
            2. `dotnet ef migrations add Init`
            3. `dotnet ef database update`

            Only the files needed to make this a runnable project were generated; entity classes,
            `OnModelCreating` configuration, and anything already present in an uploaded project were
            preserved as uploaded/edited.
            """;
    }

    private static string ProviderPackageName(ScaffoldProvider provider) => provider switch
    {
        ScaffoldProvider.SqlServer => "Microsoft.EntityFrameworkCore.SqlServer",
        ScaffoldProvider.PostgreSql => "Npgsql.EntityFrameworkCore.PostgreSQL",
        ScaffoldProvider.Sqlite => "Microsoft.EntityFrameworkCore.Sqlite",
        _ => throw new ArgumentOutOfRangeException(nameof(provider)),
    };

    private static string ProviderUseMethod(ScaffoldProvider provider) => provider switch
    {
        ScaffoldProvider.SqlServer => "UseSqlServer",
        ScaffoldProvider.PostgreSql => "UseNpgsql",
        ScaffoldProvider.Sqlite => "UseSqlite",
        _ => throw new ArgumentOutOfRangeException(nameof(provider)),
    };

    private static string ProviderConnectionString(string projectName, ScaffoldProvider provider) => provider switch
    {
        ScaffoldProvider.SqlServer => $"Server=.;Database={projectName};Trusted_Connection=True;TrustServerCertificate=True",
        ScaffoldProvider.PostgreSql => $"Host=localhost;Database={projectName};Username=postgres;Password=postgres",
        ScaffoldProvider.Sqlite => $"Data Source={projectName}.db",
        _ => throw new ArgumentOutOfRangeException(nameof(provider)),
    };
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter ScaffoldTemplatesTests`
Expected: PASS, all 8 tests green.

- [ ] **Step 5: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Archive/ScaffoldTemplates.cs tests/EfSchemaVisualizer.Core.Tests/Archive/ScaffoldTemplatesTests.cs
git commit -m "Add ScaffoldTemplates for per-provider csproj/appsettings/factory/program/readme"
```

---

### Task 4: `ScaffoldGenerator.Generate` — orchestrator

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/Archive/ScaffoldGenerator.cs`
- Modify: `tests/EfSchemaVisualizer.Core.Tests/Archive/ScaffoldGeneratorTests.cs`

**Interfaces:**
- Consumes: `ScaffoldPlan` (Task 1), `ScaffoldTemplates.*` (Task 3), `ScaffoldGenerator.BuildDbContextWrapper` (Task 2).
- Produces: `ScaffoldResult` record (`string ConfigSource, IReadOnlyDictionary<string, byte[]> NewPassthroughFiles`); `ScaffoldGenerator.Generate(ScaffoldPlan plan, string configSource, IReadOnlyList<EntityModel> entities, string projectName, ScaffoldProvider provider) : ScaffoldResult`. Task 6 (Home.razor) consumes this directly.

- [ ] **Step 1: Write the failing tests (append to `ScaffoldGeneratorTests.cs`)**

```csharp
    [Fact]
    public void Generate_AllPiecesMissing_ProducesAllFilesAndWrapsConfigSource()
    {
        var plan = new ScaffoldPlan(
            NeedsCsproj: true, NeedsProgram: true, NeedsAppSettings: true,
            NeedsReadme: true, NeedsDbContextWrapper: true, DetectedProvider: null);
        var entities = new List<EntityModel> { new("Blog", new List<PropertyModel>()) };

        var result = ScaffoldGenerator.Generate(
            plan, "modelBuilder.Entity<Blog>(e => e.HasKey(x => x.Id));",
            entities, "MyApp", ScaffoldProvider.SqlServer);

        Assert.Contains("public class AppDbContext : DbContext", result.ConfigSource);
        Assert.Contains("MyApp.csproj", result.NewPassthroughFiles.Keys);
        Assert.Contains("appsettings.json", result.NewPassthroughFiles.Keys);
        Assert.Contains("Program.cs", result.NewPassthroughFiles.Keys);
        Assert.Contains("README.md", result.NewPassthroughFiles.Keys);
        Assert.Contains("AppDbContextFactory.cs", result.NewPassthroughFiles.Keys);
    }

    [Fact]
    public void Generate_NothingMissingAndRealDbContextAlreadyExists_ProducesNoNewFiles()
    {
        var plan = new ScaffoldPlan(
            NeedsCsproj: false, NeedsProgram: false, NeedsAppSettings: false,
            NeedsReadme: false, NeedsDbContextWrapper: false, DetectedProvider: ScaffoldProvider.SqlServer);
        var entities = new List<EntityModel> { new("Blog", new List<PropertyModel>()) };
        const string existingConfigSource = "public class AppDbContext : DbContext { }";

        var result = ScaffoldGenerator.Generate(
            plan, existingConfigSource, entities, "MyApp", ScaffoldProvider.SqlServer);

        Assert.Equal(existingConfigSource, result.ConfigSource);
        Assert.Empty(result.NewPassthroughFiles);
    }

    [Fact]
    public void Generate_OnlyCsprojMissing_ProducesOnlyCsprojAndFactory()
    {
        var plan = new ScaffoldPlan(
            NeedsCsproj: true, NeedsProgram: false, NeedsAppSettings: false,
            NeedsReadme: false, NeedsDbContextWrapper: false, DetectedProvider: null);
        var entities = new List<EntityModel> { new("Blog", new List<PropertyModel>()) };
        const string existingConfigSource = "public class AppDbContext : DbContext { }";

        var result = ScaffoldGenerator.Generate(
            plan, existingConfigSource, entities, "MyApp", ScaffoldProvider.Sqlite);

        Assert.Equal(existingConfigSource, result.ConfigSource);
        Assert.Equal(new[] { "MyApp.csproj", "AppDbContextFactory.cs" }.OrderBy(x => x),
            result.NewPassthroughFiles.Keys.OrderBy(x => x));
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter ScaffoldGeneratorTests`
Expected: FAIL — `ScaffoldGenerator.Generate` and `ScaffoldResult` don't exist yet.

- [ ] **Step 3: Add `ScaffoldResult` and `Generate` to `ScaffoldGenerator.cs`**

Add this record above the `ScaffoldGenerator` class, and this method inside it:

```csharp
public sealed record ScaffoldResult(
    string ConfigSource,
    IReadOnlyDictionary<string, byte[]> NewPassthroughFiles);
```

```csharp
    public static ScaffoldResult Generate(
        ScaffoldPlan plan,
        string configSource,
        IReadOnlyList<EntityModel> entities,
        string projectName,
        ScaffoldProvider provider)
    {
        var newFiles = new Dictionary<string, byte[]>();
        var resultConfigSource = configSource;

        if (plan.NeedsDbContextWrapper)
        {
            resultConfigSource = BuildDbContextWrapper(configSource, entities, projectName);
        }

        if (plan.NeedsAppSettings)
        {
            newFiles["appsettings.json"] = Encoding.UTF8.GetBytes(ScaffoldTemplates.AppSettings(projectName, provider));
        }

        if (plan.NeedsCsproj)
        {
            newFiles[$"{projectName}.csproj"] = Encoding.UTF8.GetBytes(ScaffoldTemplates.Csproj(projectName, provider));
        }

        if (plan.NeedsProgram)
        {
            newFiles["Program.cs"] = Encoding.UTF8.GetBytes(ScaffoldTemplates.Program(projectName));
        }

        if (plan.NeedsReadme)
        {
            newFiles["README.md"] = Encoding.UTF8.GetBytes(ScaffoldTemplates.Readme(projectName));
        }

        if (plan.NeedsDbContextWrapper || plan.NeedsCsproj)
        {
            newFiles["AppDbContextFactory.cs"] = Encoding.UTF8.GetBytes(ScaffoldTemplates.DbContextFactory(projectName, provider));
        }

        return new ScaffoldResult(resultConfigSource, newFiles);
    }
```

(`AppDbContextFactory.cs` is written whenever a fresh `DbContext` wrapper or a fresh `.csproj` was just generated — i.e. whenever we're establishing a runnable setup for the first time. If neither is missing there's presumably an existing hand-written `DbContext` with its own tooling wiring already, so no competing factory is added.)

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter ScaffoldGeneratorTests`
Expected: PASS, all 11 tests green (8 from Task 2 + 3 new).

- [ ] **Step 5: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Archive/ScaffoldGenerator.cs tests/EfSchemaVisualizer.Core.Tests/Archive/ScaffoldGeneratorTests.cs
git commit -m "Add ScaffoldGenerator.Generate orchestrator"
```

---

### Task 5: End-to-end round-trip tests, one per provider

**Files:**
- Create: `tests/EfSchemaVisualizer.Core.Tests/Archive/ScaffoldRoundTripTests.cs`

**Interfaces:**
- Consumes: `ScaffoldPlanner.Plan` (Task 1), `ScaffoldGenerator.Generate` (Task 4), `ProjectArchiveWriter.Write` (existing).

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using EfSchemaVisualizer.Core.Archive;
using EfSchemaVisualizer.Core.Model;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace EfSchemaVisualizer.Core.Tests.Archive;

public class ScaffoldRoundTripTests
{
    private static readonly IReadOnlyList<EntityModel> Entities = new List<EntityModel>
    {
        new("Blog", new List<PropertyModel>()),
        new("Post", new List<PropertyModel>()),
    };

    private const string ConfigSource = "modelBuilder.Entity<Blog>(e => e.HasKey(x => x.Id));";

    [Theory]
    [InlineData(ScaffoldProvider.SqlServer)]
    [InlineData(ScaffoldProvider.PostgreSql)]
    [InlineData(ScaffoldProvider.Sqlite)]
    public void FreshDownload_WithScaffoldEnabled_ProducesAllRunnableFilesParsingCleanly(ScaffoldProvider provider)
    {
        var plan = ScaffoldPlanner.Plan(ConfigSource, passthroughFiles: null);
        var result = ScaffoldGenerator.Generate(plan, ConfigSource, Entities, "MyApp", provider);

        var bytes = ProjectArchiveWriter.Write(
            classSource: "public class Blog { public int Id { get; set; } }\npublic class Post { public int Id { get; set; } }",
            configSource: result.ConfigSource,
            passthroughFiles: result.NewPassthroughFiles);

        using var stream = new MemoryStream(bytes);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);

        var expectedEntries = new[]
        {
            "MyApp.csproj", "Program.cs", "appsettings.json", "README.md", "AppDbContextFactory.cs",
        };
        foreach (var expected in expectedEntries)
        {
            Assert.NotNull(zip.GetEntry(expected));
        }

        foreach (var csFileName in new[] { "Program.cs", "AppDbContextFactory.cs" })
        {
            using var reader = new StreamReader(zip.GetEntry(csFileName)!.Open());
            var source = reader.ReadToEnd();
            var tree = CSharpSyntaxTree.ParseText(source);
            Assert.Empty(tree.GetDiagnostics());
        }

        using var dbContextReader = new StreamReader(zip.GetEntry("DbContext.cs")!.Open());
        var dbContextSource = dbContextReader.ReadToEnd();
        var dbContextTree = CSharpSyntaxTree.ParseText(dbContextSource);
        Assert.Empty(dbContextTree.GetDiagnostics());
        Assert.Contains("public class AppDbContext : DbContext", dbContextSource);
    }

    [Fact]
    public void UploadWithExistingCsproj_ScaffoldEnabled_LeavesExistingCsprojByteForByteUnchanged()
    {
        var existingCsprojBytes = Encoding.UTF8.GetBytes("<Project Sdk=\"Microsoft.NET.Sdk\"><!-- hand customized --></Project>");
        var passthrough = new Dictionary<string, byte[]> { ["MyApp.csproj"] = existingCsprojBytes };

        var plan = ScaffoldPlanner.Plan(ConfigSource, passthrough);
        Assert.False(plan.NeedsCsproj);

        var result = ScaffoldGenerator.Generate(plan, ConfigSource, Entities, "MyApp", ScaffoldProvider.SqlServer);
        Assert.DoesNotContain("MyApp.csproj", result.NewPassthroughFiles.Keys);

        var merged = new Dictionary<string, byte[]>(passthrough);
        foreach (var (path, content) in result.NewPassthroughFiles)
        {
            merged[path] = content;
        }

        var bytes = ProjectArchiveWriter.Write(
            classSource: "public class Blog { public int Id { get; set; } }",
            configSource: result.ConfigSource,
            passthroughFiles: merged);

        using var stream = new MemoryStream(bytes);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        using var reader = new StreamReader(zip.GetEntry("MyApp.csproj")!.Open());

        Assert.Equal(Encoding.UTF8.GetString(existingCsprojBytes), reader.ReadToEnd());
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter ScaffoldRoundTripTests`
Expected: FAIL initially if any wiring assumption is off (e.g. entry name casing); use the failure output to confirm it's failing for the expected reason (missing type/file), not a false start.

- [ ] **Step 3: Fix up anything the test reveals**

No new production code is expected here — this task exercises Tasks 1–4 together. If a test fails for a real reason (e.g. `ConfigSource` not routed to `DbContext.cs` by `ProjectArchiveWriter`'s default-path behavior), fix the specific mismatch in `ScaffoldGenerator`/`ScaffoldPlanner` from Tasks 1–4, not by weakening the test.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter ScaffoldRoundTripTests`
Expected: PASS, all 4 tests green (3 provider cases + 1 overwrite-policy case).

- [ ] **Step 5: Run the full Core test suite to check for regressions**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests`
Expected: PASS, no regressions.

- [ ] **Step 6: Commit**

```bash
git add tests/EfSchemaVisualizer.Core.Tests/Archive/ScaffoldRoundTripTests.cs
git commit -m "Add end-to-end scaffold round-trip tests per provider"
```

---

### Task 6: Wire the checkbox into `Home.razor`

**Files:**
- Modify: `src/EfSchemaVisualizer.Web/Pages/Home.razor:53` (toolbar `<p>` block), `:390-404` (`DownloadZip`)
- Modify: `tests/EfSchemaVisualizer.Web.Tests/Diagram/HomeMarkupTests.cs`

**Interfaces:**
- Consumes: `ScaffoldPlanner.Plan`, `ScaffoldGenerator.Generate`, `ScaffoldProvider` (all from `EfSchemaVisualizer.Core.Archive`, already `@using`'d in `Home.razor` line 6).

- [ ] **Step 1: Write the failing markup test (append to `HomeMarkupTests.cs`)**

```csharp
    [Fact]
    public void ScaffoldCheckbox_ExistsOutsideFullscreenToolbarOnly()
    {
        var markup = ReadHomeRazorSource();

        Assert.Contains("_generateScaffold", markup);

        var fullscreenBlock = ExtractFullscreenBlock(markup);
        Assert.DoesNotContain("_generateScaffold", fullscreenBlock);

        var nonFullscreenBlock = ExtractNonFullscreenBlock(markup);
        Assert.Contains("_generateScaffold", nonFullscreenBlock);
    }

    [Fact]
    public void ProviderDropdown_OnlyRendersWhenCsprojIsNeeded()
    {
        var markup = ReadHomeRazorSource();

        Assert.Contains("NeedsCsproj", markup);
        Assert.Contains("_selectedProvider", markup);
    }

    [Fact]
    public void DownloadZip_CallsScaffoldGeneratorOnlyWhenCheckboxChecked()
    {
        var markup = ReadHomeRazorSource();

        var methodIndex = markup.IndexOf("private async Task DownloadZip()", StringComparison.Ordinal);
        Assert.True(methodIndex >= 0);

        var methodBody = markup.Substring(methodIndex, 900);
        Assert.Contains("if (_generateScaffold)", methodBody);
        Assert.Contains("ScaffoldPlanner.Plan(", methodBody);
        Assert.Contains("ScaffoldGenerator.Generate(", methodBody);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/EfSchemaVisualizer.Web.Tests --filter HomeMarkupTests`
Expected: FAIL — none of the new markers exist in `Home.razor` yet.

- [ ] **Step 3: Add the toolbar controls to `Home.razor`**

Replace this line (currently at line 53):

```razor
        <button class="btn btn-secondary" disabled="@(_editor is null)" @onclick="DownloadZip">Download .zip</button>
```

with:

```razor
        <button class="btn btn-secondary" disabled="@(_editor is null)" @onclick="DownloadZip">Download .zip</button>
        <label title="Fill in any of AppDbContext, .csproj, appsettings.json, Program.cs, and README.md that are missing, without overwriting anything already present">
            <input type="checkbox" @bind="_generateScaffold" disabled="@(_editor is null)" />
            Generate runnable project scaffold
        </label>
        @if (_generateScaffold)
        {
            <input style="width: 120px;" value="@_projectName" placeholder="Project name"
                   @onchange="e => _projectName = e.Value?.ToString() ?? _projectName" />
            @if (CurrentScaffoldPlan is null || CurrentScaffoldPlan.NeedsCsproj)
            {
                <select @bind="_selectedProvider">
                    <option value="@ScaffoldProvider.SqlServer">SQL Server</option>
                    <option value="@ScaffoldProvider.PostgreSql">PostgreSQL</option>
                    <option value="@ScaffoldProvider.Sqlite">SQLite</option>
                </select>
            }
        }
```

- [ ] **Step 4: Add the backing fields and `CurrentScaffoldPlan` property to the `@code` block**

Add next to the existing `_passthroughFiles` field declaration (around line 204):

```csharp
    private bool _generateScaffold;
    private string _projectName = "MyApp";
    private ScaffoldProvider _selectedProvider = ScaffoldProvider.SqlServer;

    private ScaffoldPlan? CurrentScaffoldPlan =>
        _editor is null ? null : ScaffoldPlanner.Plan(_editor.ConfigSource, _passthroughFiles);
```

- [ ] **Step 5: Update `DownloadZip` to apply the scaffold when checked**

Replace the existing `DownloadZip` method (lines 390-404) with:

```csharp
    private async Task DownloadZip()
    {
        if (_editor is null)
        {
            return;
        }

        var layout = _diagram is not null ? DiagramLayout.Capture(_diagram) : null;
        var configSource = _editor.ConfigSource;
        var passthrough = _passthroughFiles;

        if (_generateScaffold)
        {
            var plan = ScaffoldPlanner.Plan(configSource, passthrough);
            var result = ScaffoldGenerator.Generate(
                plan, configSource, _editor.Current.Entities, _projectName, _selectedProvider);
            configSource = result.ConfigSource;
            passthrough = MergePassthroughFiles(passthrough, result.NewPassthroughFiles);
        }

        var bytes = ProjectArchiveWriter.Write(
            _editor.ClassSource, configSource, layout,
            _editor.EntityFileOrigins, _editor.ConfigFileOrigins, passthrough);
        using var stream = new MemoryStream(bytes);
        using var streamRef = new DotNetStreamReference(stream);
        await JS.InvokeVoidAsync("downloadFileFromStream", "ef-schema-visualizer-export.zip", streamRef);
    }

    private static IReadOnlyDictionary<string, byte[]> MergePassthroughFiles(
        IReadOnlyDictionary<string, byte[]>? existing,
        IReadOnlyDictionary<string, byte[]> generated)
    {
        var merged = new Dictionary<string, byte[]>(existing ?? new Dictionary<string, byte[]>());
        foreach (var (path, bytes) in generated)
        {
            merged[path] = bytes;
        }

        return merged;
    }
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test tests/EfSchemaVisualizer.Web.Tests --filter HomeMarkupTests`
Expected: PASS, all 8 tests green (5 existing + 3 new).

- [ ] **Step 7: Run the full Web test suite to check for regressions**

Run: `dotnet test tests/EfSchemaVisualizer.Web.Tests`
Expected: PASS, no regressions.

- [ ] **Step 8: Commit**

```bash
git add src/EfSchemaVisualizer.Web/Pages/Home.razor tests/EfSchemaVisualizer.Web.Tests/Diagram/HomeMarkupTests.cs
git commit -m "Wire scaffold checkbox and provider picker into Home.razor download flow"
```

---

### Task 7: Manual browser verification

**Files:** none (verification only).

- [ ] **Step 1: Start the dev server**

Run: `dotnet run --project src/EfSchemaVisualizer.Web` (or use the project's `run` skill/task if one exists), then open the served URL in a browser.

- [ ] **Step 2: Verify the from-scratch path**

With the shipped default sample still loaded, click Render Diagram, check "Generate runnable project scaffold", confirm the Project name field and provider dropdown both appear, pick SQLite, click Download .zip, and unzip the downloaded file. Confirm it contains `MyApp.csproj`, `DbContext.cs` (now a real `AppDbContext` class with `DbSet<Blog>`/`DbSet<Post>`), `AppDbContextFactory.cs`, `appsettings.json`, `Program.cs`, `README.md`.

- [ ] **Step 3: Verify `dotnet ef` actually works against the generated project**

In a scratch directory, unzip the download, run `dotnet restore`, then `dotnet ef migrations add Init`. Confirm it succeeds and produces a `Migrations/` folder — this is the actual proof the scaffold is runnable, not just that the files parse.

- [ ] **Step 4: Verify the upload path's overwrite policy**

Upload a zip containing a hand-written `MyApp.csproj` (any content) alongside entity/config files, check the scaffold checkbox, confirm the provider dropdown does NOT appear (since a `.csproj` is already present), download, and confirm the downloaded `MyApp.csproj` byte-for-byte matches what was uploaded while `appsettings.json`/`Program.cs`/`README.md` were still added.

- [ ] **Step 5: Report results**

Note any UI rough edges found (e.g. layout, missing disabled states) as follow-up items rather than silently fixing scope beyond this plan — flag them for a decision before making unplanned changes.

---

## Self-Review Notes

- **Spec coverage:** UI checkbox/inputs (Task 6), overwrite policy (Tasks 1, 4, 5's second test), detection logic (Task 1), all five generated file types + `AppDbContextFactory.cs` (Tasks 3–4), wiring into `DownloadZip` (Task 6), testing per the spec's Testing section (Tasks 1, 2, 3, 4, 5) — all covered.
- **Type consistency:** `ScaffoldPlan`, `ScaffoldProvider`, `ScaffoldResult`, and every method signature are defined once in Tasks 1–4 and reused verbatim in Tasks 5–6 with no renames.
- **Non-goals** (entity namespaces, editing an existing `.csproj`'s packages, adding `DbSet`s to a hand-written `DbContext`, non-heuristic pluralization) are respected by construction — no task attempts any of them.
