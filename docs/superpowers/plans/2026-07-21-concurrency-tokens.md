# Concurrency Tokens Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Parse, merge, rewrite, and make editable in the diagram UI EF Core's optimistic-concurrency markers: `.IsRowVersion()`, `.IsConcurrencyToken()` (fluent), and `[Timestamp]`, `[ConcurrencyCheck]` (data annotations).

**Architecture:** Follows this codebase's established parse → merge → rewrite → editor → UI pipeline for a per-property boolean flag, closely mirroring the existing `ValueGenerated`/`IsRequiredOverride` features. Two independent `bool` fields (`IsRowVersion`, `IsConcurrencyToken`) are added to `PropertyModel` since the two fluent calls/attributes are independent at the syntax level even though `IsRowVersion` semantically implies concurrency-token behavior in EF itself.

**Tech Stack:** C#/.NET, Roslyn (`Microsoft.CodeAnalysis.CSharp`) for parsing/rewriting, Blazor WebAssembly for the diagram UI, xUnit for tests.

## Global Constraints

- Design spec: `docs/superpowers/specs/2026-07-21-concurrency-tokens-design.md` — implementation must match it exactly; if a step here needs to diverge, treat that as a signal to stop and reconcile with the spec first.
- Fluent-wins-on-conflict: when both an attribute and a fluent call target the same property, the fluent value must not be allowed to *downgrade* a `true` set by the attribute (both sources only ever assert `true`, never `false`, so this is satisfied by OR-ing rather than overwriting).
- No new diagnostic codes: none of the four constructs take arguments, so there is no "unreadable argument" case.
- Run `dotnet test EfSchemaVisualizer.slnx` after every task and confirm the full suite is green before committing.

---

### Task 1: `PropertyModel` fields, `ConcurrencyTokenConfig` DTO, and `FluentConfigParser.ParseConcurrencyTokens`

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/Model/PropertyModel.cs`
- Create: `src/EfSchemaVisualizer.Core/Merging/ConcurrencyTokenConfig.cs`
- Modify: `src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs`

**Interfaces:**
- Produces: `PropertyModel.IsRowVersion` (`bool`, default `false`), `PropertyModel.IsConcurrencyToken` (`bool`, default `false`); `ConcurrencyTokenConfig(string EntityName, string PropertyName, bool IsRowVersion, bool IsConcurrencyToken)`; `FluentConfigParser.ParseConcurrencyTokens(string sourceCode) -> ParseResult<IReadOnlyList<ConcurrencyTokenConfig>>`.
- Consumes: `FluentSyntaxHelpers.FindConfigurationScopes`, `FluentSyntaxHelpers.FindCallsNamed`, `FluentSyntaxHelpers.GetPropertyNameFor`, `DiagnosticCodes.UnresolvablePropertyName`, `ParseResult<T>` (all pre-existing).

- [ ] **Step 1: Add the two new fields to `PropertyModel`**

Edit `src/EfSchemaVisualizer.Core/Model/PropertyModel.cs` so it reads:

```csharp
namespace EfSchemaVisualizer.Core.Model;

public sealed record PropertyModel(
    string Name,
    string ClrType,
    bool IsNullable,
    int? MaxLength,
    bool? IsRequiredOverride = null,
    int? Precision = null,
    int? Scale = null,
    string? ColumnName = null,
    string? ColumnType = null,
    string? DefaultValueLiteral = null,
    string? ValueGenerated = null,
    bool IsShadow = false,
    bool IsRowVersion = false,
    bool IsConcurrencyToken = false);
```

- [ ] **Step 2: Create the `ConcurrencyTokenConfig` DTO**

Create `src/EfSchemaVisualizer.Core/Merging/ConcurrencyTokenConfig.cs`:

```csharp
namespace EfSchemaVisualizer.Core.Merging;

public sealed record ConcurrencyTokenConfig(
    string EntityName, string PropertyName, bool IsRowVersion, bool IsConcurrencyToken);
```

- [ ] **Step 3: Write the failing tests for `ParseConcurrencyTokens`**

Add to `tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs`, after the existing `ParseValueGeneration` block (search for `// ─── ParseShadowProperties` and insert immediately before it):

```csharp
    // ─── ParseConcurrencyTokens ────────────────────────────────────────────────────

    [Fact]
    public void ParseConcurrencyTokens_IsRowVersionCall_SetsIsRowVersionOnly()
    {
        const string source = """
            class Ctx : DbContext {
                protected override void OnModelCreating(ModelBuilder modelBuilder) {
                    modelBuilder.Entity<Person>(entity => {
                        entity.Property(e => e.RowVersion).IsRowVersion();
                    });
                }
            }
            """;

        var result = new FluentConfigParser().ParseConcurrencyTokens(source);

        var config = Assert.Single(result.Value);
        Assert.Equal("Person", config.EntityName);
        Assert.Equal("RowVersion", config.PropertyName);
        Assert.True(config.IsRowVersion);
        Assert.False(config.IsConcurrencyToken);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void ParseConcurrencyTokens_IsConcurrencyTokenCall_SetsIsConcurrencyTokenOnly()
    {
        const string source = """
            class Ctx : DbContext {
                protected override void OnModelCreating(ModelBuilder modelBuilder) {
                    modelBuilder.Entity<Person>(entity => {
                        entity.Property(e => e.Version).IsConcurrencyToken();
                    });
                }
            }
            """;

        var result = new FluentConfigParser().ParseConcurrencyTokens(source);

        var config = Assert.Single(result.Value);
        Assert.Equal("Person", config.EntityName);
        Assert.Equal("Version", config.PropertyName);
        Assert.False(config.IsRowVersion);
        Assert.True(config.IsConcurrencyToken);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void ParseConcurrencyTokens_BothCallsOnSameProperty_SetsBothFlags()
    {
        const string source = """
            class Ctx : DbContext {
                protected override void OnModelCreating(ModelBuilder modelBuilder) {
                    modelBuilder.Entity<Person>(entity => {
                        entity.Property(e => e.RowVersion).IsRowVersion().IsConcurrencyToken();
                    });
                }
            }
            """;

        var result = new FluentConfigParser().ParseConcurrencyTokens(source);

        var config = Assert.Single(result.Value);
        Assert.Equal("RowVersion", config.PropertyName);
        Assert.True(config.IsRowVersion);
        Assert.True(config.IsConcurrencyToken);
    }

    [Fact]
    public void ParseConcurrencyTokens_NoConcurrencyCalls_ReturnsEmpty()
    {
        const string source = """
            class Ctx : DbContext {
                protected override void OnModelCreating(ModelBuilder modelBuilder) {
                    modelBuilder.Entity<Person>(entity => {
                        entity.Property(e => e.Name).HasMaxLength(100);
                    });
                }
            }
            """;

        var result = new FluentConfigParser().ParseConcurrencyTokens(source);

        Assert.Empty(result.Value);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void ParseConcurrencyTokens_IEntityTypeConfigurationStyle_ReadsFlags()
    {
        const string source = """
            class PersonConfig : IEntityTypeConfiguration<Person> {
                public void Configure(EntityTypeBuilder<Person> builder) {
                    builder.Property(e => e.RowVersion).IsRowVersion();
                }
            }
            """;

        var result = new FluentConfigParser().ParseConcurrencyTokens(source);

        var config = Assert.Single(result.Value);
        Assert.Equal("Person", config.EntityName);
        Assert.Equal("RowVersion", config.PropertyName);
        Assert.True(config.IsRowVersion);
    }
```

- [ ] **Step 4: Run the tests to verify they fail to compile (method doesn't exist yet)**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter "FullyQualifiedName~ParseConcurrencyTokens"`
Expected: build error — `'FluentConfigParser' does not contain a definition for 'ParseConcurrencyTokens'`.

- [ ] **Step 5: Implement `ParseConcurrencyTokens`**

In `src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs`, add `"IsRowVersion"` and `"IsConcurrencyToken"` to `RecognizedCallNames` (line 16-23):

```csharp
    private static readonly HashSet<string> RecognizedCallNames = new()
    {
        "Property", "HasMaxLength", "HasPrecision", "IsRequired", "HasKey", "ToTable",
        "HasColumnName", "HasColumnType", "HasDefaultValue", "HasIndex", "IsUnique",
        "HasOne", "HasMany", "WithOne", "WithMany", "HasForeignKey", "OnDelete", "UsingEntity",
        "Ignore", "ValueGeneratedOnAdd", "ValueGeneratedOnUpdate", "ValueGeneratedOnAddOrUpdate",
        "ValueGeneratedNever", "UseIdentityColumn", "ToView", "ToSqlQuery", "HasNoKey",
        "IsRowVersion", "IsConcurrencyToken",
    };
```

Then add the new method, placed after `ParseValueGeneration` (after the closing brace at line 672, before `public ParseResult<IReadOnlyList<ShadowPropertyConfig>> ParseShadowProperties`):

```csharp
    public ParseResult<IReadOnlyList<ConcurrencyTokenConfig>> ParseConcurrencyTokens(string sourceCode)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var diagnostics = new List<Diagnostic>();
        var flagsByProperty = new Dictionary<(string EntityName, string PropertyName), (bool IsRowVersion, bool IsConcurrencyToken)>();

        foreach (var (entityName, scope) in FluentSyntaxHelpers.FindConfigurationScopes(root))
        {
            foreach (var (callName, marksRowVersion) in new[] { ("IsRowVersion", true), ("IsConcurrencyToken", false) })
            {
                foreach (var call in FluentSyntaxHelpers.FindCallsNamed(scope, callName))
                {
                    var propertyName = FluentSyntaxHelpers.GetPropertyNameFor(call);

                    if (propertyName is null)
                    {
                        diagnostics.Add(new Diagnostic(
                            DiagnosticCodes.UnresolvablePropertyName,
                            $"Could not resolve the property configured by '{callName}'.",
                            entityName,
                            PropertyName: null,
                            call.Span));
                        continue;
                    }

                    var key = (entityName, propertyName);
                    var existing = flagsByProperty.GetValueOrDefault(key);
                    flagsByProperty[key] = marksRowVersion
                        ? (true, existing.IsConcurrencyToken)
                        : (existing.IsRowVersion, true);
                }
            }
        }

        var results = flagsByProperty
            .Select(kvp => new ConcurrencyTokenConfig(kvp.Key.EntityName, kvp.Key.PropertyName, kvp.Value.IsRowVersion, kvp.Value.IsConcurrencyToken))
            .ToList();

        return new ParseResult<IReadOnlyList<ConcurrencyTokenConfig>>(results, diagnostics);
    }
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter "FullyQualifiedName~ParseConcurrencyTokens"`
Expected: PASS (5 tests).

- [ ] **Step 7: Run the full Core test suite to confirm no regressions**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests`
Expected: all tests PASS (no count regression from before this task).

- [ ] **Step 8: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Model/PropertyModel.cs \
        src/EfSchemaVisualizer.Core/Merging/ConcurrencyTokenConfig.cs \
        src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs \
        tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs
git commit -m "Add ConcurrencyTokenConfig and FluentConfigParser.ParseConcurrencyTokens"
```

---

### Task 2: `EntityClassParser` attribute parsing for `[Timestamp]`/`[ConcurrencyCheck]`

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/Parsing/EntityClassParser.cs:219-267` (`ParseProperty`)
- Test: `tests/EfSchemaVisualizer.Core.Tests/Parsing/EntityClassParserTests.cs`

**Interfaces:**
- Consumes: `PropertyModel.IsRowVersion`/`IsConcurrencyToken` (Task 1); existing private `FindAttribute(SyntaxList<AttributeListSyntax>, string)` helper (`EntityClassParser.cs:297`).
- Produces: `ParseProperty` now sets `IsRowVersion`/`IsConcurrencyToken` on the returned `PropertyModel` from `[Timestamp]`/`[ConcurrencyCheck]` attributes.

- [ ] **Step 1: Write the failing tests**

Add to `tests/EfSchemaVisualizer.Core.Tests/Parsing/EntityClassParserTests.cs`, directly after the existing `Parse_RequiredAttribute_SetsIsRequiredOverride` test (around line 380):

```csharp
    [Fact]
    public void Parse_TimestampAttribute_SetsIsRowVersion()
    {
        const string source = """
            public class Person
            {
                public int Id { get; set; }
                [Timestamp]
                public byte[] RowVersion { get; set; } = System.Array.Empty<byte>();
            }
            """;

        var result = new EntityClassParser().Parse(source);

        var property = result.Value.Single().Properties.Single(p => p.Name == "RowVersion");
        Assert.True(property.IsRowVersion);
        Assert.False(property.IsConcurrencyToken);
    }

    [Fact]
    public void Parse_ConcurrencyCheckAttribute_SetsIsConcurrencyToken()
    {
        const string source = """
            public class Person
            {
                public int Id { get; set; }
                [ConcurrencyCheck]
                public int Version { get; set; }
            }
            """;

        var result = new EntityClassParser().Parse(source);

        var property = result.Value.Single().Properties.Single(p => p.Name == "Version");
        Assert.False(property.IsRowVersion);
        Assert.True(property.IsConcurrencyToken);
    }

    [Fact]
    public void Parse_NoConcurrencyAttributes_LeavesBothFlagsFalse()
    {
        const string source = """
            public class Person
            {
                public int Id { get; set; }
                public string Name { get; set; } = "";
            }
            """;

        var result = new EntityClassParser().Parse(source);

        var property = result.Value.Single().Properties.Single(p => p.Name == "Name");
        Assert.False(property.IsRowVersion);
        Assert.False(property.IsConcurrencyToken);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter "FullyQualifiedName~Parse_TimestampAttribute|FullyQualifiedName~Parse_ConcurrencyCheckAttribute"`
Expected: FAIL — both new assertions fail (`Assert.True` on a `false` default).

- [ ] **Step 3: Implement the attribute reads**

In `src/EfSchemaVisualizer.Core/Parsing/EntityClassParser.cs`, edit `ParseProperty` (currently lines 219-267). Add the two new attribute reads after the `precision`/`scale` block (after line 255, before the `return new PropertyModel(` at line 257):

```csharp
        bool isRowVersion = FindAttribute(attributeLists, "Timestamp") is not null;
        bool isConcurrencyToken = FindAttribute(attributeLists, "ConcurrencyCheck") is not null;

        return new PropertyModel(
            property.Identifier.Text,
            clrType,
            isNullable,
            maxLength,
            isRequiredOverride,
            precision,
            scale,
            columnName,
            columnType,
            IsRowVersion: isRowVersion,
            IsConcurrencyToken: isConcurrencyToken);
```

(This replaces the existing `return new PropertyModel(...)` call, which previously ended at `columnType`.)

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter "FullyQualifiedName~Parse_TimestampAttribute|FullyQualifiedName~Parse_ConcurrencyCheckAttribute|FullyQualifiedName~Parse_NoConcurrencyAttributes"`
Expected: PASS (3 tests).

- [ ] **Step 5: Run the full Core test suite**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests`
Expected: all tests PASS.

- [ ] **Step 6: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Parsing/EntityClassParser.cs \
        tests/EfSchemaVisualizer.Core.Tests/Parsing/EntityClassParserTests.cs
git commit -m "Parse [Timestamp]/[ConcurrencyCheck] attributes into IsRowVersion/IsConcurrencyToken"
```

---

### Task 3: `ModelMerger.ApplyConcurrencyTokens`

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/Merging/ModelMerger.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Merging/ModelMergerTests.cs`

**Interfaces:**
- Consumes: `ConcurrencyTokenConfig` (Task 1), `PropertyModel.IsRowVersion`/`IsConcurrencyToken` (Task 1), private `IndexByProperty` helper (`ModelMerger.cs:178`).
- Produces: `ModelMerger.ApplyConcurrencyTokens(EntityModel entity, IReadOnlyList<ConcurrencyTokenConfig> configs) -> EntityModel`.

- [ ] **Step 1: Write the failing tests**

Add to `tests/EfSchemaVisualizer.Core.Tests/Merging/ModelMergerTests.cs`, after the existing `// ─── ApplyValueGeneration` block (around line 434, before `// ─── ApplyShadowProperties`):

```csharp
    // ─── ApplyConcurrencyTokens ────────────────────────────────────────────────────

    [Fact]
    public void ApplyConcurrencyTokens_SetsFlagsOnMatchingProperty_LeavesOthersUntouched()
    {
        var entity = new EntityModel("Person", new List<PropertyModel>
        {
            new("Id", "int", IsNullable: false, MaxLength: null),
            new("RowVersion", "byte[]", IsNullable: false, MaxLength: null),
            new("Name", "string", IsNullable: true, MaxLength: null),
        });

        var configs = new List<ConcurrencyTokenConfig>
        {
            new("Person", "RowVersion", IsRowVersion: true, IsConcurrencyToken: false),
            new("Address", "Line1", IsRowVersion: true, IsConcurrencyToken: false), // different entity, must not affect Person
        };

        var merged = ModelMerger.ApplyConcurrencyTokens(entity, configs);

        var rowVersion = merged.Properties.Single(p => p.Name == "RowVersion");
        Assert.True(rowVersion.IsRowVersion);
        Assert.False(rowVersion.IsConcurrencyToken);
        Assert.False(merged.Properties.Single(p => p.Name == "Name").IsRowVersion);
    }

    [Fact]
    public void ApplyConcurrencyTokens_BothFlagsOnSameConfig_SetsBoth()
    {
        var entity = new EntityModel("Person", new List<PropertyModel>
        {
            new("Id", "int", IsNullable: false, MaxLength: null),
            new("RowVersion", "byte[]", IsNullable: false, MaxLength: null),
        });

        var configs = new List<ConcurrencyTokenConfig>
        {
            new("Person", "RowVersion", IsRowVersion: true, IsConcurrencyToken: true),
        };

        var merged = ModelMerger.ApplyConcurrencyTokens(entity, configs);

        var rowVersion = merged.Properties.Single(p => p.Name == "RowVersion");
        Assert.True(rowVersion.IsRowVersion);
        Assert.True(rowVersion.IsConcurrencyToken);
    }

    [Fact]
    public void ApplyConcurrencyTokens_AttributeSeededTrue_NotDowngradedByAbsentFluentConfig()
    {
        var entity = new EntityModel("Person", new List<PropertyModel>
        {
            new("Id", "int", IsNullable: false, MaxLength: null),
            new("RowVersion", "byte[]", IsNullable: false, MaxLength: null, IsRowVersion: true),
        });

        var merged = ModelMerger.ApplyConcurrencyTokens(entity, new List<ConcurrencyTokenConfig>());

        Assert.True(merged.Properties.Single(p => p.Name == "RowVersion").IsRowVersion);
    }
```

- [ ] **Step 2: Run the tests to verify they fail to compile**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter "FullyQualifiedName~ApplyConcurrencyTokens"`
Expected: build error — `'ModelMerger' does not contain a definition for 'ApplyConcurrencyTokens'`.

- [ ] **Step 3: Implement `ApplyConcurrencyTokens`**

In `src/EfSchemaVisualizer.Core/Merging/ModelMerger.cs`, add after `ApplyValueGeneration` (after line 156, before `ApplyShadowProperties`):

```csharp
    public static EntityModel ApplyConcurrencyTokens(EntityModel entity, IReadOnlyList<ConcurrencyTokenConfig> configs)
    {
        var byProperty = IndexByProperty(entity.Name, configs, c => c.EntityName, c => c.PropertyName);

        var updatedProperties = entity.Properties
            .Select(property => byProperty.TryGetValue(property.Name, out var config)
                ? property with
                {
                    IsRowVersion = property.IsRowVersion || config.IsRowVersion,
                    IsConcurrencyToken = property.IsConcurrencyToken || config.IsConcurrencyToken,
                }
                : property)
            .ToList();

        return entity with { Properties = updatedProperties };
    }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter "FullyQualifiedName~ApplyConcurrencyTokens"`
Expected: PASS (3 tests).

- [ ] **Step 5: Run the full Core test suite**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests`
Expected: all tests PASS.

- [ ] **Step 6: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Merging/ModelMerger.cs \
        tests/EfSchemaVisualizer.Core.Tests/Merging/ModelMergerTests.cs
git commit -m "Add ModelMerger.ApplyConcurrencyTokens"
```

---

### Task 4: Wire into `DiagramModelBuilder.Build`

**Files:**
- Modify: `src/EfSchemaVisualizer.Web/DiagramModelBuilder.cs`
- Test: `tests/EfSchemaVisualizer.Web.Tests/DiagramModelBuilderTests.cs`

**Interfaces:**
- Consumes: `FluentConfigParser.ParseConcurrencyTokens` (Task 1), `ModelMerger.ApplyConcurrencyTokens` (Task 3).

- [ ] **Step 1: Write the failing tests**

Add to `tests/EfSchemaVisualizer.Web.Tests/DiagramModelBuilderTests.cs`, after the existing `Build_UseIdentityColumn_SetsValueGeneratedOnProperty` test (around line 352, before `Build_ShadowProperty_AppearsAsIsShadowProperty`):

```csharp
    [Fact]
    public void Build_IsRowVersionCall_SetsIsRowVersionOnProperty()
    {
        const string classSource = """
            public class Person
            {
                public int Id { get; set; }
                public byte[] RowVersion { get; set; } = System.Array.Empty<byte>();
            }
            """;

        const string configSource = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Person>(entity =>
                    {
                        entity.Property(e => e.RowVersion).IsRowVersion();
                    });
                }
            }
            """;

        var result = DiagramModelBuilder.Build(classSource, configSource);

        var rowVersion = result.Entities.Single().Properties.Single(p => p.Name == "RowVersion");
        Assert.True(rowVersion.IsRowVersion);
        Assert.False(rowVersion.IsConcurrencyToken);
    }

    [Fact]
    public void Build_TimestampAttribute_SetsIsRowVersionOnProperty()
    {
        const string classSource = """
            public class Person
            {
                public int Id { get; set; }
                [Timestamp]
                public byte[] RowVersion { get; set; } = System.Array.Empty<byte>();
            }
            """;

        const string configSource = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                }
            }
            """;

        var result = DiagramModelBuilder.Build(classSource, configSource);

        Assert.True(result.Entities.Single().Properties.Single(p => p.Name == "RowVersion").IsRowVersion);
    }

    [Fact]
    public void Build_IsRowVersionCallNotFlaggedAsUnrecognized()
    {
        const string classSource = """
            public class Person
            {
                public int Id { get; set; }
                public byte[] RowVersion { get; set; } = System.Array.Empty<byte>();
            }
            """;

        const string configSource = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Person>(entity =>
                    {
                        entity.Property(e => e.RowVersion).IsRowVersion();
                    });
                }
            }
            """;

        var result = DiagramModelBuilder.Build(classSource, configSource);

        Assert.DoesNotContain(result.Diagnostics, d => d.Code == DiagnosticCodes.UnrecognizedConfigCall);
    }
```

Add `using EfSchemaVisualizer.Core.Parsing;` at the top of the file if not already present (check first — `DiagnosticCodes` lives in that namespace).

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/EfSchemaVisualizer.Web.Tests --filter "FullyQualifiedName~Build_IsRowVersionCall|FullyQualifiedName~Build_TimestampAttribute"`
Expected: FAIL — `IsRowVersion` is `false` on both (parser output isn't wired into `Build` yet).

- [ ] **Step 3: Wire the new parser/merger into `Build`**

In `src/EfSchemaVisualizer.Web/DiagramModelBuilder.cs`, add after the `valueGeneration` line (line 37):

```csharp
        var concurrencyTokens = configParser.ParseConcurrencyTokens(configSource);
```

Add after the `diagnostics.AddRange(valueGeneration.Diagnostics);` line (line 57):

```csharp
        diagnostics.AddRange(concurrencyTokens.Diagnostics);
```

Add to the `entities` pipeline, after `.Select(entity => ModelMerger.ApplyValueGeneration(entity, valueGeneration.Value))` (line 85):

```csharp
            .Select(entity => ModelMerger.ApplyConcurrencyTokens(entity, concurrencyTokens.Value))
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/EfSchemaVisualizer.Web.Tests --filter "FullyQualifiedName~Build_IsRowVersionCall|FullyQualifiedName~Build_TimestampAttribute"`
Expected: PASS (3 tests).

- [ ] **Step 5: Run the full Web test suite**

Run: `dotnet test tests/EfSchemaVisualizer.Web.Tests`
Expected: all tests PASS.

- [ ] **Step 6: Commit**

```bash
git add src/EfSchemaVisualizer.Web/DiagramModelBuilder.cs \
        tests/EfSchemaVisualizer.Web.Tests/DiagramModelBuilderTests.cs
git commit -m "Wire ParseConcurrencyTokens/ApplyConcurrencyTokens into DiagramModelBuilder"
```

---

### Task 5: `OnModelCreatingRewriter` Set/Remove pairs

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs`

**Interfaces:**
- Consumes: `FluentSyntaxHelpers.FindCallsNamed`, `FluentSyntaxHelpers.GetPropertyNameFor`, `FluentSyntaxHelpers.GetPropertyNameForPropertyCall`, `FluentSyntaxHelpers.GetPropertyLambdaParameterName`, private `FindConfigScopes`, `GetScopeBlockAndReceiver`, `FindOnModelCreatingMethod`, `BuildEntityInvocationStatement` (all pre-existing on this class).
- Produces: `SetRowVersion(string sourceCode, string entityName, string propertyName) -> string`, `RemoveRowVersion(string sourceCode, string entityName, string propertyName) -> string`, `SetConcurrencyToken(string sourceCode, string entityName, string propertyName) -> string`, `RemoveConcurrencyToken(string sourceCode, string entityName, string propertyName) -> string`.

- [ ] **Step 1: Write the failing tests**

Add to `tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs`, after the `RemoveIsRequired_EntityHasNoConfigAtAll_ReturnsSourceUnchanged` test (around line 396, before `private const string SourceWithSingleKey`):

```csharp
    private const string SourceWithUnconfiguredRowVersionProperty = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.Property(e => e.Name).HasMaxLength(100);
                    entity.Property(e => e.RowVersion);
                });
            }
        }
        """;

    [Fact]
    public void SetRowVersion_PropertyExistsWithoutCall_AppendsBareIsRowVersionCall()
    {
        var result = new OnModelCreatingRewriter()
            .SetRowVersion(SourceWithUnconfiguredRowVersionProperty, entityName: "Person", propertyName: "RowVersion");

        Assert.Contains("entity.Property(e => e.RowVersion).IsRowVersion()", result);
        Assert.Contains("entity.Property(e => e.Name).HasMaxLength(100)", result);
    }

    [Fact]
    public void SetRowVersion_CallAlreadyPresent_IsIdempotentNoOp()
    {
        var once = new OnModelCreatingRewriter()
            .SetRowVersion(SourceWithUnconfiguredRowVersionProperty, entityName: "Person", propertyName: "RowVersion");

        var twice = new OnModelCreatingRewriter()
            .SetRowVersion(once, entityName: "Person", propertyName: "RowVersion");

        Assert.Equal(once, twice);
    }

    [Fact]
    public void SetRowVersion_PropertyNeverMentioned_InsertsNewStatementAtEndOfBlock()
    {
        const string source = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Person>(entity =>
                    {
                        entity.Property(e => e.Name).HasMaxLength(100);
                    });
                }
            }
            """;

        var result = new OnModelCreatingRewriter()
            .SetRowVersion(source, entityName: "Person", propertyName: "RowVersion");

        Assert.Contains("entity.Property(e => e.RowVersion).IsRowVersion()", result);
        Assert.Contains("entity.Property(e => e.Name).HasMaxLength(100)", result);
    }

    [Fact]
    public void SetRowVersion_UnknownEntity_InsertsNewEntityBlock()
    {
        var result = new OnModelCreatingRewriter()
            .SetRowVersion(SourceWithUnconfiguredRowVersionProperty, entityName: "Vehicle", propertyName: "RowVersion");

        Assert.Contains("modelBuilder.Entity<Vehicle>(entity =>", result);
        Assert.Contains("entity.Property(e => e.RowVersion).IsRowVersion()", result);
        Assert.Contains("entity.Property(e => e.Name).HasMaxLength(100)", result); // Person untouched
    }

    [Fact]
    public void RemoveRowVersion_ExistingCall_StripsCallLeavesBarePropertyCall()
    {
        var withCall = new OnModelCreatingRewriter()
            .SetRowVersion(SourceWithUnconfiguredRowVersionProperty, entityName: "Person", propertyName: "RowVersion");

        var result = new OnModelCreatingRewriter()
            .RemoveRowVersion(withCall, entityName: "Person", propertyName: "RowVersion");

        Assert.Contains("entity.Property(e => e.RowVersion);", result);
        Assert.DoesNotContain("IsRowVersion", result);
    }

    [Fact]
    public void RemoveRowVersion_NoMatchingCall_ReturnsSourceUnchanged()
    {
        var result = new OnModelCreatingRewriter()
            .RemoveRowVersion(SourceWithUnconfiguredRowVersionProperty, entityName: "Person", propertyName: "RowVersion");

        Assert.Equal(SourceWithUnconfiguredRowVersionProperty, result);
    }

    private const string SourceWithUnconfiguredConcurrencyTokenProperty = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.Property(e => e.Version);
                });
            }
        }
        """;

    [Fact]
    public void SetConcurrencyToken_PropertyExistsWithoutCall_AppendsBareIsConcurrencyTokenCall()
    {
        var result = new OnModelCreatingRewriter()
            .SetConcurrencyToken(SourceWithUnconfiguredConcurrencyTokenProperty, entityName: "Person", propertyName: "Version");

        Assert.Contains("entity.Property(e => e.Version).IsConcurrencyToken()", result);
    }

    [Fact]
    public void RemoveConcurrencyToken_ExistingCall_StripsCallLeavesBarePropertyCall()
    {
        var withCall = new OnModelCreatingRewriter()
            .SetConcurrencyToken(SourceWithUnconfiguredConcurrencyTokenProperty, entityName: "Person", propertyName: "Version");

        var result = new OnModelCreatingRewriter()
            .RemoveConcurrencyToken(withCall, entityName: "Person", propertyName: "Version");

        Assert.Contains("entity.Property(e => e.Version);", result);
        Assert.DoesNotContain("IsConcurrencyToken", result);
    }

    [Fact]
    public void SetRowVersionAndSetConcurrencyToken_OnSameProperty_AreIndependent()
    {
        var withRowVersion = new OnModelCreatingRewriter()
            .SetRowVersion(SourceWithUnconfiguredRowVersionProperty, entityName: "Person", propertyName: "RowVersion");

        var withBoth = new OnModelCreatingRewriter()
            .SetConcurrencyToken(withRowVersion, entityName: "Person", propertyName: "RowVersion");

        Assert.Contains("IsRowVersion()", withBoth);
        Assert.Contains("IsConcurrencyToken()", withBoth);

        var withoutRowVersion = new OnModelCreatingRewriter()
            .RemoveRowVersion(withBoth, entityName: "Person", propertyName: "RowVersion");

        Assert.DoesNotContain("IsRowVersion", withoutRowVersion);
        Assert.Contains("IsConcurrencyToken()", withoutRowVersion);
    }
```

- [ ] **Step 2: Run the tests to verify they fail to compile**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter "FullyQualifiedName~RowVersion|FullyQualifiedName~ConcurrencyToken" `
Expected: build error — `SetRowVersion`/`RemoveRowVersion`/`SetConcurrencyToken`/`RemoveConcurrencyToken` don't exist yet.

- [ ] **Step 3: Implement the four methods via shared private helpers**

In `src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs`, add the following block after `RemoveIsRequired` (after line 1691, before `public string RenameEntityReferences`):

```csharp
    public string SetRowVersion(string sourceCode, string entityName, string propertyName) =>
        SetBareMarkerCall(sourceCode, entityName, propertyName, "IsRowVersion");

    public string RemoveRowVersion(string sourceCode, string entityName, string propertyName) =>
        RemoveBareMarkerCall(sourceCode, entityName, propertyName, "IsRowVersion");

    public string SetConcurrencyToken(string sourceCode, string entityName, string propertyName) =>
        SetBareMarkerCall(sourceCode, entityName, propertyName, "IsConcurrencyToken");

    public string RemoveConcurrencyToken(string sourceCode, string entityName, string propertyName) =>
        RemoveBareMarkerCall(sourceCode, entityName, propertyName, "IsConcurrencyToken");

    /// Idempotently ensures a bare, no-argument fluent call (e.g. `.IsRowVersion()`) is chained onto
    /// the given property's `.Property(...)` call. Shared by SetRowVersion/SetConcurrencyToken since
    /// both are structurally identical bare property-scoped markers.
    private static string SetBareMarkerCall(string sourceCode, string entityName, string propertyName, string callName)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var scopes = FindConfigScopes(root, entityName);

        var existingCall = scopes
            .SelectMany(scope => FluentSyntaxHelpers.FindCallsNamed(scope, callName))
            .FirstOrDefault(call => FluentSyntaxHelpers.GetPropertyNameFor(call) == propertyName);

        if (existingCall is not null)
        {
            return sourceCode;
        }

        var existingPropertyCall = scopes
            .SelectMany(scope => FluentSyntaxHelpers.FindCallsNamed(scope, "Property"))
            .FirstOrDefault(call => FluentSyntaxHelpers.GetPropertyNameForPropertyCall(call) == propertyName);

        if (existingPropertyCall is not null)
        {
            var markerCall = BuildBareMarkerCall(existingPropertyCall, callName);
            var newRoot = root.ReplaceNode(existingPropertyCall, markerCall);
            return newRoot.NormalizeWhitespace().ToFullString();
        }

        var existingScope = scopes.FirstOrDefault();

        if (existingScope is not null)
        {
            var (block, blockReceiverName) = GetScopeBlockAndReceiver(existingScope);
            var propertyLambdaParam = FluentSyntaxHelpers.GetPropertyLambdaParameterName(existingScope);

            var newStatement = BuildBareMarkerPropertyStatement(blockReceiverName, propertyLambdaParam, propertyName, callName);
            var newBlock = block.AddStatements(newStatement);

            var newRoot = root.ReplaceNode(block, newBlock);
            return newRoot.NormalizeWhitespace().ToFullString();
        }

        var method = FindOnModelCreatingMethod(root);
        var methodBody = method.Body
            ?? throw new InvalidOperationException("OnModelCreating has no method body.");
        var modelBuilderParamName = method.ParameterList.Parameters.Single().Identifier.Text;

        var propertyStatement = BuildBareMarkerPropertyStatement("entity", "e", propertyName, callName);
        var entityBlockStatement = BuildEntityInvocationStatement(modelBuilderParamName, entityName, SyntaxFactory.Block(propertyStatement));

        var newMethodBody = methodBody.AddStatements(entityBlockStatement);
        var finalRoot = root.ReplaceNode(methodBody, newMethodBody);
        return finalRoot.NormalizeWhitespace().ToFullString();
    }

    private static InvocationExpressionSyntax BuildBareMarkerCall(ExpressionSyntax propertyCallExpression, string callName)
    {
        return SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                propertyCallExpression,
                SyntaxFactory.IdentifierName(callName)),
            SyntaxFactory.ArgumentList());
    }

    private static ExpressionStatementSyntax BuildBareMarkerPropertyStatement(
        string blockReceiverName, string propertyLambdaParam, string propertyName, string callName)
    {
        var propertyCall = SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                SyntaxFactory.IdentifierName(blockReceiverName),
                SyntaxFactory.IdentifierName("Property")),
            SyntaxFactory.ArgumentList(
                SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.Argument(
                        SyntaxFactory.SimpleLambdaExpression(
                            SyntaxFactory.Parameter(SyntaxFactory.Identifier(propertyLambdaParam)),
                            SyntaxFactory.MemberAccessExpression(
                                SyntaxKind.SimpleMemberAccessExpression,
                                SyntaxFactory.IdentifierName(propertyLambdaParam),
                                SyntaxFactory.IdentifierName(propertyName)))))));

        return SyntaxFactory.ExpressionStatement(BuildBareMarkerCall(propertyCall, callName));
    }

    /// Removes a bare, no-argument fluent call (e.g. `.IsRowVersion()`) chained onto a property's
    /// `.Property(...)` call, unwrapping back to the bare property call. No-ops if absent.
    private static string RemoveBareMarkerCall(string sourceCode, string entityName, string propertyName, string callName)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var scopes = FindConfigScopes(root, entityName);

        var existingCall = scopes
            .SelectMany(scope => FluentSyntaxHelpers.FindCallsNamed(scope, callName))
            .FirstOrDefault(call => FluentSyntaxHelpers.GetPropertyNameFor(call) == propertyName);

        if (existingCall is null)
        {
            return sourceCode;
        }

        var propertyCallExpression = ((MemberAccessExpressionSyntax)existingCall.Expression).Expression;

        var newRoot = root.ReplaceNode(existingCall, propertyCallExpression);
        return newRoot.NormalizeWhitespace().ToFullString();
    }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter "FullyQualifiedName~RowVersion|FullyQualifiedName~ConcurrencyToken"`
Expected: PASS (all new tests).

- [ ] **Step 5: Run the full Core test suite**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests`
Expected: all tests PASS.

- [ ] **Step 6: Commit**

```bash
git add src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs \
        tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs
git commit -m "Add OnModelCreatingRewriter Set/RemoveRowVersion and Set/RemoveConcurrencyToken"
```

---

### Task 6: `DiagramEditor.SetRowVersion`/`SetConcurrencyToken`

**Files:**
- Modify: `src/EfSchemaVisualizer.Web/Diagram/DiagramEditor.cs`
- Test: `tests/EfSchemaVisualizer.Web.Tests/Diagram/DiagramEditorPropertyPanelTests.cs`

**Interfaces:**
- Consumes: `OnModelCreatingRewriter.SetRowVersion`/`RemoveRowVersion`/`SetConcurrencyToken`/`RemoveConcurrencyToken` (Task 5), `PropertyModel.IsRowVersion`/`IsConcurrencyToken` (Task 1), existing `DiagramEditResult`, `Current`, `Apply` members.
- Produces: `DiagramEditor.SetRowVersion(string entityName, string propertyName, bool isRowVersion) -> DiagramEditResult`, `DiagramEditor.SetConcurrencyToken(string entityName, string propertyName, bool isConcurrencyToken) -> DiagramEditResult`.

- [ ] **Step 1: Write the failing tests**

Add to `tests/EfSchemaVisualizer.Web.Tests/Diagram/DiagramEditorPropertyPanelTests.cs`, after the `SetRequiredOverride_ClearingExistingOverride_RemovesIsRequired` test (around line 81):

```csharp
    [Fact]
    public void SetRowVersion_SetToTrue_InsertsIsRowVersion()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);

        var result = editor.SetRowVersion("Person", "Name", true);

        Assert.True(result.Success);
        Assert.True(editor.Current.Entities.Single().Properties.Single(p => p.Name == "Name").IsRowVersion);
        Assert.Contains("IsRowVersion()", editor.ConfigSource);
    }

    [Fact]
    public void SetRowVersion_SetToFalse_WhenAlreadyFalse_IsNoOp()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);

        var result = editor.SetRowVersion("Person", "Name", false);

        Assert.True(result.Success);
        Assert.DoesNotContain("IsRowVersion", editor.ConfigSource);
    }

    [Fact]
    public void SetRowVersion_ClearingExistingFlag_RemovesIsRowVersion()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);
        editor.SetRowVersion("Person", "Name", true);

        var result = editor.SetRowVersion("Person", "Name", false);

        Assert.True(result.Success);
        Assert.False(editor.Current.Entities.Single().Properties.Single(p => p.Name == "Name").IsRowVersion);
        Assert.DoesNotContain("IsRowVersion", editor.ConfigSource);
    }

    [Fact]
    public void SetRowVersion_UnknownEntity_Fails()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);

        var result = editor.SetRowVersion("DoesNotExist", "Name", true);

        Assert.False(result.Success);
    }

    [Fact]
    public void SetConcurrencyToken_SetToTrue_InsertsIsConcurrencyToken()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);

        var result = editor.SetConcurrencyToken("Person", "Name", true);

        Assert.True(result.Success);
        Assert.True(editor.Current.Entities.Single().Properties.Single(p => p.Name == "Name").IsConcurrencyToken);
        Assert.Contains("IsConcurrencyToken()", editor.ConfigSource);
    }

    [Fact]
    public void SetConcurrencyToken_ClearingExistingFlag_RemovesIsConcurrencyToken()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);
        editor.SetConcurrencyToken("Person", "Name", true);

        var result = editor.SetConcurrencyToken("Person", "Name", false);

        Assert.True(result.Success);
        Assert.False(editor.Current.Entities.Single().Properties.Single(p => p.Name == "Name").IsConcurrencyToken);
        Assert.DoesNotContain("IsConcurrencyToken", editor.ConfigSource);
    }

    [Fact]
    public void SetConcurrencyToken_UnknownProperty_Fails()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);

        var result = editor.SetConcurrencyToken("Person", "DoesNotExist", true);

        Assert.False(result.Success);
    }
```

- [ ] **Step 2: Run the tests to verify they fail to compile**

Run: `dotnet test tests/EfSchemaVisualizer.Web.Tests --filter "FullyQualifiedName~SetRowVersion|FullyQualifiedName~SetConcurrencyToken"`
Expected: build error — `DiagramEditor` has no `SetRowVersion`/`SetConcurrencyToken` methods.

- [ ] **Step 3: Implement the two `DiagramEditor` methods**

In `src/EfSchemaVisualizer.Web/Diagram/DiagramEditor.cs`, add after `SetRequiredOverride` (after line 625, before `public DiagramEditResult SetPrecision`):

```csharp
    public DiagramEditResult SetRowVersion(string entityName, string propertyName, bool isRowVersion)
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

        if (isRowVersion == property.IsRowVersion)
        {
            return DiagramEditResult.Ok();
        }

        var newConfigSource = isRowVersion
            ? _configRewriter.SetRowVersion(ConfigSource, entityName, propertyName)
            : _configRewriter.RemoveRowVersion(ConfigSource, entityName, propertyName);
        Apply(ClassSource, newConfigSource);
        return DiagramEditResult.Ok();
    }

    public DiagramEditResult SetConcurrencyToken(string entityName, string propertyName, bool isConcurrencyToken)
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

        if (isConcurrencyToken == property.IsConcurrencyToken)
        {
            return DiagramEditResult.Ok();
        }

        var newConfigSource = isConcurrencyToken
            ? _configRewriter.SetConcurrencyToken(ConfigSource, entityName, propertyName)
            : _configRewriter.RemoveConcurrencyToken(ConfigSource, entityName, propertyName);
        Apply(ClassSource, newConfigSource);
        return DiagramEditResult.Ok();
    }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/EfSchemaVisualizer.Web.Tests --filter "FullyQualifiedName~SetRowVersion|FullyQualifiedName~SetConcurrencyToken"`
Expected: PASS (all new tests).

- [ ] **Step 5: Run the full Web test suite**

Run: `dotnet test tests/EfSchemaVisualizer.Web.Tests`
Expected: all tests PASS.

- [ ] **Step 6: Commit**

```bash
git add src/EfSchemaVisualizer.Web/Diagram/DiagramEditor.cs \
        tests/EfSchemaVisualizer.Web.Tests/Diagram/DiagramEditorPropertyPanelTests.cs
git commit -m "Add DiagramEditor.SetRowVersion/SetConcurrencyToken"
```

---

### Task 7: `EntityNode.razor` UI checkboxes

**Files:**
- Modify: `src/EfSchemaVisualizer.Web/Diagram/EntityNode.razor`
- Test: `tests/EfSchemaVisualizer.Web.Tests/Diagram/EntityNodeAccessibilityTests.cs`

**Interfaces:**
- Consumes: `DiagramEditor.SetRowVersion`/`SetConcurrencyToken` (Task 6), `PropertyModel.IsRowVersion`/`IsConcurrencyToken` (Task 1), existing `EditContext`, `_propertyErrors`, `EditContext.NotifyChangedAsync()`.

- [ ] **Step 1: Write the failing markup test**

Add to `tests/EfSchemaVisualizer.Web.Tests/Diagram/EntityNodeAccessibilityTests.cs`, as a new test method inside the existing class (after `EveryTitledButton_HasAMatchingAriaLabel`, before `ReadEntityNodeRazorSource`):

```csharp
    [Fact]
    public void PropertyExpandPanel_HasRowVersionAndConcurrencyTokenCheckboxes()
    {
        var markup = ReadEntityNodeRazorSource();

        Assert.Contains("CommitRowVersion", markup);
        Assert.Contains("CommitConcurrencyToken", markup);
        Assert.Contains("Row version", markup);
        Assert.Contains("Concurrency token", markup);
    }
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/EfSchemaVisualizer.Web.Tests --filter "FullyQualifiedName~PropertyExpandPanel_HasRowVersionAndConcurrencyTokenCheckboxes"`
Expected: FAIL — markup doesn't contain `CommitRowVersion`/`CommitConcurrencyToken` yet.

- [ ] **Step 3: Add the two checkboxes to the property expand panel**

In `src/EfSchemaVisualizer.Web/Diagram/EntityNode.razor`, insert two new `<label>` blocks after the "Required override" `<label>` (after line 188, before the "Column name" `<label>` at line 189):

```razor
                                <label style="display: block;">
                                    <input type="checkbox" checked="@property.IsRowVersion"
                                           @onchange="e => CommitRowVersion(property, (bool)(e.Value ?? false))"
                                           @onpointerdown:stopPropagation="true"
                                           @onmousedown:stopPropagation="true" />
                                    Row version
                                </label>
                                <label style="display: block;">
                                    <input type="checkbox" checked="@property.IsConcurrencyToken"
                                           @onchange="e => CommitConcurrencyToken(property, (bool)(e.Value ?? false))"
                                           @onpointerdown:stopPropagation="true"
                                           @onmousedown:stopPropagation="true" />
                                    Concurrency token
                                </label>
```

- [ ] **Step 4: Add the two commit handlers**

In the `@code` block, add after `CommitRequiredOverride` (after line 674, before `CommitColumnName`):

```csharp
    private async Task CommitRowVersion(PropertyModel property, bool newIsRowVersion)
    {
        var result = EditContext.Editor.SetRowVersion(Node.Entity.Name, property.Name, newIsRowVersion);
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

    private async Task CommitConcurrencyToken(PropertyModel property, bool newIsConcurrencyToken)
    {
        var result = EditContext.Editor.SetConcurrencyToken(Node.Entity.Name, property.Name, newIsConcurrencyToken);
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
```

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test tests/EfSchemaVisualizer.Web.Tests --filter "FullyQualifiedName~PropertyExpandPanel_HasRowVersionAndConcurrencyTokenCheckboxes"`
Expected: PASS.

- [ ] **Step 6: Build the Web project to confirm the Razor markup compiles**

Run: `dotnet build src/EfSchemaVisualizer.Web/EfSchemaVisualizer.Web.csproj`
Expected: Build succeeds with no errors or new warnings (the repo's `Directory.Build.props` treats warnings as errors, so this also catches accessibility/markup mistakes).

- [ ] **Step 7: Run the full Web test suite**

Run: `dotnet test tests/EfSchemaVisualizer.Web.Tests`
Expected: all tests PASS.

- [ ] **Step 8: Commit**

```bash
git add src/EfSchemaVisualizer.Web/Diagram/EntityNode.razor \
        tests/EfSchemaVisualizer.Web.Tests/Diagram/EntityNodeAccessibilityTests.cs
git commit -m "Add Row version/Concurrency token checkboxes to the property expand panel"
```

---

### Task 8: Full-suite verification and backlog update

**Files:**
- Modify: `docs/backlog.md`

**Interfaces:**
- None — this task only verifies the completed feature and updates project tracking.

- [ ] **Step 1: Run the entire solution's test suite**

Run: `dotnet test EfSchemaVisualizer.slnx`
Expected: all tests across `EfSchemaVisualizer.Core.Tests`, `EfSchemaVisualizer.Web.Tests`, and `EfSchemaVisualizer.SmokeTests` PASS (smoke test self-skips unless `SMOKE_TEST_PUBLISH_DIR` is set, per existing project convention).

- [ ] **Step 2: Run `dotnet format` to confirm no style drift**

Run: `dotnet format EfSchemaVisualizer.slnx --verify-no-changes`
Expected: exits 0 (no formatting diffs) — this mirrors the CI `Format check` step in `deploy.yml`.

- [ ] **Step 3: Update `docs/backlog.md`**

Edit `docs/backlog.md`. Find the line (in the Round 3 review, Priority 3 section):

```
- [ ] **`[found]` Concurrency tokens unread.** `IsRowVersion()`,
      `IsConcurrencyToken()`, `[Timestamp]`, `[ConcurrencyCheck]`.
```

Replace it with:

```
- [x] **`[found]` Concurrency tokens unread.** `IsRowVersion()`,
      `IsConcurrencyToken()`, `[Timestamp]`, `[ConcurrencyCheck]`.
      **Update:** `FluentConfigParser.ParseConcurrencyTokens` reads both fluent
      calls into a new `ConcurrencyTokenConfig` (two independent bools, since
      the calls can co-occur on one property); `EntityClassParser.ParseProperty`
      reads `[Timestamp]`/`[ConcurrencyCheck]` the same way `[Required]` is
      read; `ModelMerger.ApplyConcurrencyTokens` ORs attribute- and
      fluent-derived flags together (fluent only ever raises, never lowers).
      Unlike the previous `ValueGenerated` badge-only pass, this one includes
      full write-back: `OnModelCreatingRewriter.SetRowVersion`/
      `RemoveRowVersion`/`SetConcurrencyToken`/`RemoveConcurrencyToken` (two
      independent bare-marker-call Set/Remove pairs sharing private
      `SetBareMarkerCall`/`RemoveBareMarkerCall` helpers), `DiagramEditor.
      SetRowVersion`/`SetConcurrencyToken`, and two checkboxes ("Row version",
      "Concurrency token") in `EntityNode.razor`'s property expand panel — see
      `2026-07-21-concurrency-tokens-design.md`.
```

- [ ] **Step 4: Commit**

```bash
git add docs/backlog.md
git commit -m "Mark concurrency-tokens backlog item done"
```
