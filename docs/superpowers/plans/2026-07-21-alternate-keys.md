# Alternate Keys (`HasAlternateKey`) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Parse `HasAlternateKey(...)` fluent calls into the model, merge them into `EntityModel`, round-trip them through the rewriter (add/remove), and expose add/toggle/remove editing in the diagram UI — closing the "alternate keys unread" backlog item.

**Architecture:** Follows the exact parse → merge → rewrite → `DiagramEditor` → UI pipeline used by every prior config kind in this codebase (most directly mirrors `HasIndex`, since an entity can have multiple alternate keys, each over one or more properties — unlike the single-valued `HasKey`).

**Tech Stack:** C#, Roslyn (`Microsoft.CodeAnalysis.CSharp`), xUnit, Blazor (`.razor`).

## Global Constraints

- Spec: `docs/superpowers/specs/2026-07-21-alternate-keys-design.md`.
- `EntityModel.AlternateKeys: IReadOnlyList<IReadOnlyList<string>>` — list-shaped like `Indexes`, not single-valued like `KeyPropertyNames`.
- `HasPrincipalKey` and relationship cross-referencing of alternate keys are out of scope.
- No name parameter — EF's `HasAlternateKey` fluent API doesn't take one, unlike `HasIndex`.
- `Directory.Build.props` sets `TreatWarningsAsErrors` — every task must build clean.
- Run `dotnet test EfSchemaVisualizer.slnx` after each task's own test run to catch regressions; the plan calls out the scoped run for speed, but do a full run before the final commit.

---

### Task 1: Model — `EntityModel.AlternateKeys` + `AlternateKeyConfig` + diagnostic code

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/Model/EntityModel.cs`
- Create: `src/EfSchemaVisualizer.Core/Merging/AlternateKeyConfig.cs`
- Modify: `src/EfSchemaVisualizer.Core/Parsing/DiagnosticCodes.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Merging/ModelMergerTests.cs` (new region, added in Task 3 alongside `ApplyAlternateKeys` — this task only touches the model shape, no new tests of its own beyond a compile check)

**Interfaces:**
- Produces: `EntityModel.AlternateKeys: IReadOnlyList<IReadOnlyList<string>>` (defaults to empty list, same pattern as `Indexes`); `EfSchemaVisualizer.Core.Merging.AlternateKeyConfig(string EntityName, IReadOnlyList<string> PropertyNames)`; `DiagnosticCodes.UnreadableHasAlternateKeyArgument`.

- [ ] **Step 1: Add `AlternateKeys` to `EntityModel`**

Edit `src/EfSchemaVisualizer.Core/Model/EntityModel.cs` to:

```csharp
using System.Collections.Generic;

namespace EfSchemaVisualizer.Core.Model;

public sealed record EntityModel(
    string Name,
    IReadOnlyList<PropertyModel> Properties,
    IReadOnlyList<string>? KeyPropertyNames = null,
    IReadOnlyList<IndexModel>? Indexes = null,
    string? TableName = null,
    string? Schema = null,
    bool IsKeyless = false,
    string? ViewName = null,
    string? SqlQuery = null,
    IReadOnlyList<IReadOnlyList<string>>? AlternateKeys = null)
{
    public IReadOnlyList<string> KeyPropertyNames { get; init; } = KeyPropertyNames ?? new List<string>();
    public IReadOnlyList<IndexModel> Indexes { get; init; } = Indexes ?? new List<IndexModel>();
    public IReadOnlyList<IReadOnlyList<string>> AlternateKeys { get; init; } = AlternateKeys ?? new List<IReadOnlyList<string>>();
}
```

(New parameter appended at the end of the primary constructor so every existing positional-argument call site in the codebase and tests keeps compiling.)

- [ ] **Step 2: Create `AlternateKeyConfig`**

Create `src/EfSchemaVisualizer.Core/Merging/AlternateKeyConfig.cs`:

```csharp
using System.Collections.Generic;

namespace EfSchemaVisualizer.Core.Merging;

public sealed record AlternateKeyConfig(string EntityName, IReadOnlyList<string> PropertyNames);
```

- [ ] **Step 3: Add the diagnostic code**

In `src/EfSchemaVisualizer.Core/Parsing/DiagnosticCodes.cs`, add a new constant alongside `UnreadableHasIndexArgument`:

```csharp
    public const string UnreadableHasAlternateKeyArgument = nameof(UnreadableHasAlternateKeyArgument);
```

- [ ] **Step 4: Build to confirm no call sites broke**

Run: `dotnet build EfSchemaVisualizer.slnx`
Expected: Build succeeds with no errors (the new `EntityModel` parameter is optional and appended last, so no existing positional-constructor call anywhere in `src/` or `tests/` needs updating).

- [ ] **Step 5: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Model/EntityModel.cs src/EfSchemaVisualizer.Core/Merging/AlternateKeyConfig.cs src/EfSchemaVisualizer.Core/Parsing/DiagnosticCodes.cs
git commit -m "Add EntityModel.AlternateKeys model shape and diagnostic code"
```

---

### Task 2: Parse — `FluentConfigParser.ParseAlternateKeys`

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs`

**Interfaces:**
- Consumes: `FluentSyntaxHelpers.FindConfigurationScopes(CompilationUnitSyntax root)` (yields `(string EntityName, SyntaxNode Scope)`); `FluentSyntaxHelpers.FindCallsNamed(SyntaxNode scope, string methodName)`; `FluentSyntaxHelpers.TryReadPropertyNameList(InvocationExpressionSyntax call) -> IReadOnlyList<string>?`; `ParseResult<T>(T Value, IReadOnlyList<Diagnostic> Diagnostics)`; `Diagnostic(string Code, string Message, string? EntityName, string? PropertyName, TextSpan Span)`.
- Produces: `FluentConfigParser.ParseAlternateKeys(string sourceCode) -> ParseResult<IReadOnlyList<AlternateKeyConfig>>`.

- [ ] **Step 1: Write the failing tests**

Add to `tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs` (place near the existing `ParseKeys_*` tests, around line 452 onward — follow that section's fixture-per-test-group style):

```csharp
    private const string SourceWithSingleAndCompositeAlternateKeys = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.HasAlternateKey(e => e.Email);
                });

                modelBuilder.Entity<OrderLine>(entity =>
                {
                    entity.HasAlternateKey(e => new { e.OrderId, e.LineNumber });
                });
            }
        }
        """;

    [Fact]
    public void ParseAlternateKeys_ReadsSingleAndCompositeLambdaKeys_AcrossMultipleEntities()
    {
        var result = new FluentConfigParser().ParseAlternateKeys(SourceWithSingleAndCompositeAlternateKeys);

        Assert.Empty(result.Diagnostics);
        Assert.Equal(2, result.Value.Count);
        Assert.Contains(result.Value, c => c.EntityName == "Person" && c.PropertyNames.SequenceEqual(new[] { "Email" }));
        Assert.Contains(result.Value, c => c.EntityName == "OrderLine" && c.PropertyNames.SequenceEqual(new[] { "OrderId", "LineNumber" }));
    }

    private const string SourceWithMultipleAlternateKeysOnOneEntity = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.HasAlternateKey(e => e.Email);
                    entity.HasAlternateKey(e => e.Ssn);
                });
            }
        }
        """;

    [Fact]
    public void ParseAlternateKeys_MultipleCallsOnOneEntity_AreAllRead()
    {
        var result = new FluentConfigParser().ParseAlternateKeys(SourceWithMultipleAlternateKeysOnOneEntity);

        Assert.Empty(result.Diagnostics);
        Assert.Equal(2, result.Value.Count);
        Assert.Contains(result.Value, c => c.EntityName == "Person" && c.PropertyNames.SequenceEqual(new[] { "Email" }));
        Assert.Contains(result.Value, c => c.EntityName == "Person" && c.PropertyNames.SequenceEqual(new[] { "Ssn" }));
    }

    private const string SourceWithStringAlternateKey = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.HasAlternateKey("Email");
                });
            }
        }
        """;

    [Fact]
    public void ParseAlternateKeys_StringOverload_IsRead()
    {
        var result = new FluentConfigParser().ParseAlternateKeys(SourceWithStringAlternateKey);

        Assert.Empty(result.Diagnostics);
        Assert.Contains(result.Value, c => c.EntityName == "Person" && c.PropertyNames.SequenceEqual(new[] { "Email" }));
    }

    private const string SourceWithExplicitNameAnonymousAlternateKeyMember = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.HasAlternateKey(e => new { Key = e.Email });
                });
            }
        }
        """;

    [Fact]
    public void ParseAlternateKeys_ExplicitNameAnonymousMember_EmitsUnreadableHasAlternateKeyArgumentDiagnostic()
    {
        var result = new FluentConfigParser().ParseAlternateKeys(SourceWithExplicitNameAnonymousAlternateKeyMember);

        Assert.Empty(result.Value);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.UnreadableHasAlternateKeyArgument, diagnostic.Code);
        Assert.Equal("Person", diagnostic.EntityName);
        Assert.Null(diagnostic.PropertyName);
    }

    private const string SourceWithAlternateKeyOnConfigurationClass = """
        public class PersonConfig : IEntityTypeConfiguration<Person>
        {
            public void Configure(EntityTypeBuilder<Person> builder)
            {
                builder.HasAlternateKey(e => e.Email);
            }
        }
        """;

    [Fact]
    public void ParseAlternateKeys_EntityTypeConfigurationStyle_IsRead()
    {
        var result = new FluentConfigParser().ParseAlternateKeys(SourceWithAlternateKeyOnConfigurationClass);

        Assert.Empty(result.Diagnostics);
        Assert.Contains(result.Value, c => c.EntityName == "Person" && c.PropertyNames.SequenceEqual(new[] { "Email" }));
    }

    [Fact]
    public void ParseUnrecognizedCalls_HasAlternateKey_IsNotFlagged()
    {
        var diagnostics = new FluentConfigParser().ParseUnrecognizedCalls(SourceWithSingleAndCompositeAlternateKeys);

        Assert.Empty(diagnostics);
    }
```

Confirm the file already has `using System.Linq;` (for `SequenceEqual`) at the top — it does, per the existing `ParseKeys_*` tests using the same helper.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test EfSchemaVisualizer.slnx --filter "FullyQualifiedName~FluentConfigParserTests"`
Expected: FAIL — `ParseAlternateKeys` does not exist on `FluentConfigParser`, and `DiagnosticCodes.UnreadableHasAlternateKeyArgument` doesn't exist yet (compile error) — Task 1 already added the diagnostic code, so only the missing method causes the failure.

- [ ] **Step 3: Implement `ParseAlternateKeys`**

In `src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs`, add a new method directly after `ParseKeys` (after line 261, before `ParseTableMappings`):

```csharp
    public ParseResult<IReadOnlyList<AlternateKeyConfig>> ParseAlternateKeys(string sourceCode)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var results = new List<AlternateKeyConfig>();
        var diagnostics = new List<Diagnostic>();

        foreach (var (entityName, scope) in FluentSyntaxHelpers.FindConfigurationScopes(root))
        {
            foreach (var hasAlternateKeyCall in FluentSyntaxHelpers.FindCallsNamed(scope, "HasAlternateKey"))
            {
                var propertyNames = FluentSyntaxHelpers.TryReadPropertyNameList(hasAlternateKeyCall);

                if (propertyNames is null)
                {
                    diagnostics.Add(new Diagnostic(
                        DiagnosticCodes.UnreadableHasAlternateKeyArgument,
                        "HasAlternateKey argument(s) could not be read as property name(s).",
                        entityName,
                        PropertyName: null,
                        hasAlternateKeyCall.Span));
                    continue;
                }

                results.Add(new AlternateKeyConfig(entityName, propertyNames));
            }
        }

        return new ParseResult<IReadOnlyList<AlternateKeyConfig>>(results, diagnostics);
    }
```

Then add `"HasAlternateKey"` to `RecognizedCallNames` (near the top of the class, currently listing `"HasKey"` among others):

```csharp
        "Property", "HasMaxLength", "HasPrecision", "IsRequired", "HasKey", "HasAlternateKey", "ToTable",
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test EfSchemaVisualizer.slnx --filter "FullyQualifiedName~FluentConfigParserTests"`
Expected: PASS, all tests in the file green.

- [ ] **Step 5: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs
git commit -m "Parse HasAlternateKey fluent calls into AlternateKeyConfig"
```

---

### Task 3: Merge — `ModelMerger.ApplyAlternateKeys` + wire into `DiagramModelBuilder`

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/Merging/ModelMerger.cs`
- Modify: `src/EfSchemaVisualizer.Web/DiagramModelBuilder.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Merging/ModelMergerTests.cs`

**Interfaces:**
- Consumes: `EntityModel` (Task 1), `AlternateKeyConfig` (Task 1), `FluentConfigParser.ParseAlternateKeys` (Task 2).
- Produces: `ModelMerger.ApplyAlternateKeys(EntityModel entity, IReadOnlyList<AlternateKeyConfig> configs) -> EntityModel`.

- [ ] **Step 1: Write the failing tests**

Add to `tests/EfSchemaVisualizer.Core.Tests/Merging/ModelMergerTests.cs`, in a new region placed after the existing `ApplyIndexes` region (after line 168-ish, matching that region's `// ─── ApplyIndexes ─── ...` banner style):

```csharp
    // ─── ApplyAlternateKeys ─────────────────────────────────────────────────

    [Fact]
    public void ApplyAlternateKeys_PopulatesAlternateKeysFromMatchingConfig()
    {
        var entity = new EntityModel("Person", new List<PropertyModel>
        {
            new("Email", "string", IsNullable: true, MaxLength: null)
        });
        var configs = new List<AlternateKeyConfig>
        {
            new("Person", new List<string> { "Email" })
        };

        var result = ModelMerger.ApplyAlternateKeys(entity, configs);

        var alternateKey = Assert.Single(result.AlternateKeys);
        Assert.Equal(new[] { "Email" }, alternateKey);
    }

    [Fact]
    public void ApplyAlternateKeys_CollectsAllMatchingConfigsForSameEntity()
    {
        var entity = new EntityModel("Person", new List<PropertyModel>());
        var configs = new List<AlternateKeyConfig>
        {
            new("Person", new List<string> { "Email" }),
            new("Person", new List<string> { "TenantId", "Ssn" })
        };

        var result = ModelMerger.ApplyAlternateKeys(entity, configs);

        Assert.Equal(2, result.AlternateKeys.Count);
        Assert.Equal(new[] { "Email" }, result.AlternateKeys[0]);
        Assert.Equal(new[] { "TenantId", "Ssn" }, result.AlternateKeys[1]);
    }

    [Fact]
    public void ApplyAlternateKeys_NoMatchingConfig_LeavesAlternateKeysEmpty()
    {
        var entity = new EntityModel("Person", new List<PropertyModel>());

        var result = ModelMerger.ApplyAlternateKeys(entity, new List<AlternateKeyConfig>());

        Assert.Empty(result.AlternateKeys);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test EfSchemaVisualizer.slnx --filter "FullyQualifiedName~ModelMergerTests"`
Expected: FAIL — `ModelMerger.ApplyAlternateKeys` does not exist.

- [ ] **Step 3: Implement `ApplyAlternateKeys`**

In `src/EfSchemaVisualizer.Core/Merging/ModelMerger.cs`, add directly after `ApplyIndexes` (after line 64):

```csharp
    public static EntityModel ApplyAlternateKeys(EntityModel entity, IReadOnlyList<AlternateKeyConfig> configs)
    {
        var alternateKeys = configs
            .Where(c => c.EntityName == entity.Name)
            .Select(c => c.PropertyNames)
            .ToList();

        return entity with { AlternateKeys = alternateKeys };
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test EfSchemaVisualizer.slnx --filter "FullyQualifiedName~ModelMergerTests"`
Expected: PASS.

- [ ] **Step 5: Wire into `DiagramModelBuilder.Build`**

In `src/EfSchemaVisualizer.Web/DiagramModelBuilder.cs`:

Add a parse call alongside the other `configParser.Parse*` calls (after line 26, `var keys = configParser.ParseKeys(configSource);`):

```csharp
        var alternateKeys = configParser.ParseAlternateKeys(configSource);
```

Add its diagnostics alongside the other `diagnostics.AddRange(...)` calls (after line 48, `diagnostics.AddRange(keys.Diagnostics);`):

```csharp
        diagnostics.AddRange(alternateKeys.Diagnostics);
```

Add the merge step into the `entities` pipeline, directly after `ModelMerger.ApplyKeys` (after line 76):

```csharp
            .Select(entity => ModelMerger.ApplyAlternateKeys(entity, alternateKeys.Value))
```

- [ ] **Step 6: Add a `DiagramModelBuilderTests` coverage check**

Read `tests/EfSchemaVisualizer.Web.Tests/DiagramModelBuilderTests.cs` first to find an existing test asserting `Indexes` end-to-end (search for `Indexes` in that file) and copy its shape for a new test:

```csharp
    [Fact]
    public void Build_ParsesAlternateKey_IntoEntityModel()
    {
        const string classSource = """
            public class Person
            {
                public int Id { get; set; }
                public string Email { get; set; } = "";
            }
            """;
        const string configSource = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Person>(entity =>
                    {
                        entity.HasKey(e => e.Id);
                        entity.HasAlternateKey(e => e.Email);
                    });
                }
            }
            """;

        var result = DiagramModelBuilder.Build(classSource, configSource);

        var person = result.Entities.Single(e => e.Name == "Person");
        var alternateKey = Assert.Single(person.AlternateKeys);
        Assert.Equal(new[] { "Email" }, alternateKey);
    }
```

Add it to `tests/EfSchemaVisualizer.Web.Tests/DiagramModelBuilderTests.cs`, matching the file's existing `using`s and test-method placement conventions (append near other `Build_Parses*` tests).

- [ ] **Step 7: Run the full test suite**

Run: `dotnet test EfSchemaVisualizer.slnx`
Expected: PASS, all projects green.

- [ ] **Step 8: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Merging/ModelMerger.cs src/EfSchemaVisualizer.Web/DiagramModelBuilder.cs tests/EfSchemaVisualizer.Core.Tests/Merging/ModelMergerTests.cs tests/EfSchemaVisualizer.Web.Tests/DiagramModelBuilderTests.cs
git commit -m "Merge alternate-key configs into EntityModel.AlternateKeys"
```

---

### Task 4: Rewrite — `OnModelCreatingRewriter.AddAlternateKey` / `RemoveAlternateKey`

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs`

**Interfaces:**
- Consumes: `FluentSyntaxHelpers.FindCallsNamed`, `FluentSyntaxHelpers.TryReadPropertyNameList`, private helpers already in the class: `FindConfigScopes(CompilationUnitSyntax root, string entityName) -> List<SyntaxNode>`, `GetScopeBlockAndReceiver(SyntaxNode scope) -> (BlockSyntax Block, string ReceiverName)`, `BuildEntityInvocationStatement(string modelBuilderParamName, string entityName, BlockSyntax block) -> ExpressionStatementSyntax`, `FindOnModelCreatingMethod(CompilationUnitSyntax root)`, and the existing private `BuildHasKeyArgumentList(IReadOnlyList<string> propertyNames) -> ArgumentListSyntax` (reused as-is — `HasAlternateKey`'s argument shape is identical to `HasKey`'s).
- Produces: `OnModelCreatingRewriter.AddAlternateKey(string sourceCode, string entityName, IReadOnlyList<string> propertyNames) -> string`; `OnModelCreatingRewriter.RemoveAlternateKey(string sourceCode, string entityName, IReadOnlyList<string> propertyNames) -> string`. Both are no-op-safe: `AddAlternateKey` returns `sourceCode` unchanged if that exact property set is already configured as an alternate key; `RemoveAlternateKey` returns `sourceCode` unchanged if no matching alternate key exists (matching `RemoveIndex`'s precedent — never throws).

- [ ] **Step 1: Write the failing tests**

Add to `tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs`, placed after the existing `RemoveKey_EntityHasNoConfigAtAll_ReturnsSourceUnchanged` test (after line 645):

```csharp
    private const string SourceWithSingleAlternateKey = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.HasKey(e => e.Id);
                    entity.HasAlternateKey(e => e.Email);
                });
            }
        }
        """;

    [Fact]
    public void AddAlternateKey_EntityWithoutOne_InsertsStatementAtEndOfBlock()
    {
        var result = new OnModelCreatingRewriter()
            .AddAlternateKey(SourceWithSingleKey, entityName: "Person", propertyNames: new List<string> { "Guid" });

        Assert.Contains("entity.HasKey(e => e.Id);", result);
        Assert.Contains("entity.HasAlternateKey(e => e.Guid)", result);

        var configs = new FluentConfigParser().ParseAlternateKeys(result).Value;
        Assert.Contains(configs, c => c.EntityName == "Person" && c.PropertyNames.SequenceEqual(new[] { "Guid" }));
    }

    [Fact]
    public void AddAlternateKey_SecondAlternateKeyOnSameEntity_AddsBothWithoutRemovingFirst()
    {
        var result = new OnModelCreatingRewriter()
            .AddAlternateKey(SourceWithSingleAlternateKey, entityName: "Person", propertyNames: new List<string> { "Ssn" });

        var configs = new FluentConfigParser().ParseAlternateKeys(result).Value;
        Assert.Equal(2, configs.Count);
        Assert.Contains(configs, c => c.PropertyNames.SequenceEqual(new[] { "Email" }));
        Assert.Contains(configs, c => c.PropertyNames.SequenceEqual(new[] { "Ssn" }));
    }

    [Fact]
    public void AddAlternateKey_CompositePropertySet_WritesAnonymousObjectLambda()
    {
        var result = new OnModelCreatingRewriter()
            .AddAlternateKey(SourceWithSingleKey, entityName: "Person", propertyNames: new List<string> { "TenantId", "Guid" });

        Assert.Contains("entity.HasAlternateKey(e => new { e.TenantId, e.Guid })", result);
    }

    [Fact]
    public void AddAlternateKey_AlreadyConfigured_ReturnsSourceUnchanged()
    {
        var result = new OnModelCreatingRewriter()
            .AddAlternateKey(SourceWithSingleAlternateKey, entityName: "Person", propertyNames: new List<string> { "Email" });

        Assert.Equal(SourceWithSingleAlternateKey, result);
    }

    [Fact]
    public void AddAlternateKey_UnknownEntity_InsertsNewEntityBlock()
    {
        var result = new OnModelCreatingRewriter()
            .AddAlternateKey(SourceWithSingleKey, entityName: "Vehicle", propertyNames: new List<string> { "Vin" });

        Assert.Contains("modelBuilder.Entity<Vehicle>", result);
        Assert.Contains("entity.HasAlternateKey(e => e.Vin)", result);
    }

    [Fact]
    public void RemoveAlternateKey_ExistingCall_RemovesStatementEntirely()
    {
        var result = new OnModelCreatingRewriter()
            .RemoveAlternateKey(SourceWithSingleAlternateKey, entityName: "Person", propertyNames: new List<string> { "Email" });

        Assert.DoesNotContain("HasAlternateKey", result);
        Assert.Contains("entity.HasKey(e => e.Id);", result);
    }

    [Fact]
    public void RemoveAlternateKey_NoMatchingPropertySet_ReturnsSourceUnchanged()
    {
        var result = new OnModelCreatingRewriter()
            .RemoveAlternateKey(SourceWithSingleAlternateKey, entityName: "Person", propertyNames: new List<string> { "Ssn" });

        Assert.Equal(SourceWithSingleAlternateKey, result);
    }

    [Fact]
    public void RemoveAlternateKey_EntityHasNoConfigAtAll_ReturnsSourceUnchanged()
    {
        var result = new OnModelCreatingRewriter()
            .RemoveAlternateKey(SourceWithSingleKey, entityName: "Vehicle", propertyNames: new List<string> { "Vin" });

        Assert.Equal(SourceWithSingleKey, result);
    }
```

(`SourceWithSingleKey` is the existing fixture at line 544 of the same file, already in scope.)

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test EfSchemaVisualizer.slnx --filter "FullyQualifiedName~OnModelCreatingRewriterTests"`
Expected: FAIL — `AddAlternateKey`/`RemoveAlternateKey` don't exist on `OnModelCreatingRewriter`.

- [ ] **Step 3: Implement `AddAlternateKey` and `RemoveAlternateKey`**

In `src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs`, add directly after `RemoveKey` (after line 475, before `public string SetKeyless`):

```csharp
    public string AddAlternateKey(string sourceCode, string entityName, IReadOnlyList<string> propertyNames)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var scopes = FindConfigScopes(root, entityName);

        var alreadyExists = scopes
            .SelectMany(scope => FluentSyntaxHelpers.FindCallsNamed(scope, "HasAlternateKey"))
            .Any(call => FluentSyntaxHelpers.TryReadPropertyNameList(call) is { } existing
                && existing.SequenceEqual(propertyNames));

        if (alreadyExists)
        {
            return sourceCode;
        }

        var existingScope = scopes.FirstOrDefault();

        if (existingScope is not null)
        {
            return InsertAlternateKeyStatement(root, existingScope, propertyNames);
        }

        return InsertAlternateKeyEntityBlock(root, entityName, propertyNames);
    }

    private static string InsertAlternateKeyStatement(CompilationUnitSyntax root, SyntaxNode scope, IReadOnlyList<string> propertyNames)
    {
        var (block, blockReceiverName) = GetScopeBlockAndReceiver(scope);

        var newStatement = BuildHasAlternateKeyStatement(blockReceiverName, propertyNames);
        var newBlock = block.AddStatements(newStatement);

        var newRoot = root.ReplaceNode(block, newBlock);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    private static string InsertAlternateKeyEntityBlock(CompilationUnitSyntax root, string entityName, IReadOnlyList<string> propertyNames)
    {
        var method = FindOnModelCreatingMethod(root);

        var methodBody = method.Body
            ?? throw new InvalidOperationException("OnModelCreating has no method body.");

        var modelBuilderParamName = method.ParameterList.Parameters.Single().Identifier.Text;

        var alternateKeyStatement = BuildHasAlternateKeyStatement("entity", propertyNames);
        var entityBlockStatement = BuildEntityInvocationStatement(modelBuilderParamName, entityName, SyntaxFactory.Block(alternateKeyStatement));

        var newMethodBody = methodBody.AddStatements(entityBlockStatement);
        var newRoot = root.ReplaceNode(methodBody, newMethodBody);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    private static ExpressionStatementSyntax BuildHasAlternateKeyStatement(string blockReceiverName, IReadOnlyList<string> propertyNames)
    {
        return SyntaxFactory.ExpressionStatement(
            SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.IdentifierName(blockReceiverName),
                    SyntaxFactory.IdentifierName("HasAlternateKey")),
                BuildHasKeyArgumentList(propertyNames)));
    }

    public string RemoveAlternateKey(string sourceCode, string entityName, IReadOnlyList<string> propertyNames)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var scopes = FindConfigScopes(root, entityName);

        var existingCall = scopes
            .SelectMany(scope => FluentSyntaxHelpers.FindCallsNamed(scope, "HasAlternateKey"))
            .FirstOrDefault(call => FluentSyntaxHelpers.TryReadPropertyNameList(call) is { } existing
                && existing.SequenceEqual(propertyNames));

        if (existingCall is null || existingCall.Parent is not ExpressionStatementSyntax statement)
        {
            return sourceCode;
        }

        var newRoot = root.RemoveNode(statement, SyntaxRemoveOptions.KeepNoTrivia)!;
        return newRoot.NormalizeWhitespace().ToFullString();
    }
```

`BuildHasKeyArgumentList` is the existing private method at line 432 of this file — reused verbatim since `HasAlternateKey`'s argument shape (`e => e.X` / `e => new { e.A, e.B }` / string params) is identical to `HasKey`'s.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test EfSchemaVisualizer.slnx --filter "FullyQualifiedName~OnModelCreatingRewriterTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs
git commit -m "Add OnModelCreatingRewriter.AddAlternateKey/RemoveAlternateKey"
```

---

### Task 5: `DiagramEditor` — add / toggle-membership / remove

**Files:**
- Modify: `src/EfSchemaVisualizer.Web/Diagram/DiagramEditor.cs`
- Test: `tests/EfSchemaVisualizer.Web.Tests/Diagram/DiagramEditorPropertyPanelTests.cs`

**Interfaces:**
- Consumes: `Current.Entities: IReadOnlyList<EntityModel>` (via `EntityModel.AlternateKeys`), `_configRewriter.AddAlternateKey`/`RemoveAlternateKey` (Task 4), `Apply(string newClassSource, string newConfigSource)` (existing private method — the single funnel every mutation goes through, pushes undo state), `DiagramEditResult.Ok()` / `DiagramEditResult.Fail(string)`.
- Produces: `DiagramEditor.AddAlternateKey(string entityName, string propertyName) -> DiagramEditResult`; `DiagramEditor.ToggleAlternateKeyMembership(string entityName, IReadOnlyList<string> alternateKeyPropertyNames, string propertyName, bool include) -> DiagramEditResult`; `DiagramEditor.RemoveAlternateKey(string entityName, IReadOnlyList<string> propertyNames) -> DiagramEditResult`.

- [ ] **Step 1: Write the failing tests**

Add to `tests/EfSchemaVisualizer.Web.Tests/Diagram/DiagramEditorPropertyPanelTests.cs` (append after the last test in the file — check the file's tail first to match brace/spacing style):

```csharp
    [Fact]
    public void AddAlternateKey_NewProperty_InsertsHasAlternateKey()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);

        var result = editor.AddAlternateKey("Person", "Name");

        Assert.True(result.Success);
        var alternateKey = Assert.Single(editor.Current.Entities.Single().AlternateKeys);
        Assert.Equal(new[] { "Name" }, alternateKey);
        Assert.Contains("HasAlternateKey(e => e.Name)", editor.ConfigSource);
    }

    [Fact]
    public void AddAlternateKey_AlreadyExists_Fails()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);
        editor.AddAlternateKey("Person", "Name");

        var result = editor.AddAlternateKey("Person", "Name");

        Assert.False(result.Success);
    }

    [Fact]
    public void AddAlternateKey_UnknownEntity_Fails()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);

        var result = editor.AddAlternateKey("Unknown", "Name");

        Assert.False(result.Success);
    }

    [Fact]
    public void AddAlternateKey_UnknownProperty_Fails()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);

        var result = editor.AddAlternateKey("Person", "Unknown");

        Assert.False(result.Success);
    }

    [Fact]
    public void ToggleAlternateKeyMembership_AddSecondPropertyToExistingKey_MakesItComposite()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);
        editor.AddAlternateKey("Person", "Name");

        var result = editor.ToggleAlternateKeyMembership("Person", new[] { "Name" }, "Id", include: true);

        Assert.True(result.Success);
        var alternateKey = Assert.Single(editor.Current.Entities.Single().AlternateKeys);
        Assert.Equal(new[] { "Name", "Id" }, alternateKey);
    }

    [Fact]
    public void ToggleAlternateKeyMembership_RemoveOnlyMemberProperty_RemovesTheAlternateKeyEntirely()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);
        editor.AddAlternateKey("Person", "Name");

        var result = editor.ToggleAlternateKeyMembership("Person", new[] { "Name" }, "Name", include: false);

        Assert.True(result.Success);
        Assert.Empty(editor.Current.Entities.Single().AlternateKeys);
    }

    [Fact]
    public void RemoveAlternateKey_Existing_RemovesIt()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);
        editor.AddAlternateKey("Person", "Name");

        var result = editor.RemoveAlternateKey("Person", new[] { "Name" });

        Assert.True(result.Success);
        Assert.Empty(editor.Current.Entities.Single().AlternateKeys);
    }

    [Fact]
    public void RemoveAlternateKey_NotFound_Fails()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);

        var result = editor.RemoveAlternateKey("Person", new[] { "Name" });

        Assert.False(result.Success);
    }
```

Note: the duplicate-collision branch inside `ToggleAlternateKeyMembership` (toggling a property in would produce a property set matching a *different* existing alternate key) has no dedicated test here — the equivalent branch in `ToggleIndexMembership` isn't separately unit-tested in this codebase either, so this matches existing precedent rather than being a new gap.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test EfSchemaVisualizer.slnx --filter "FullyQualifiedName~DiagramEditorPropertyPanelTests"`
Expected: FAIL — `AddAlternateKey`/`ToggleAlternateKeyMembership`/`RemoveAlternateKey` don't exist on `DiagramEditor`.

- [ ] **Step 3: Implement the three `DiagramEditor` methods**

In `src/EfSchemaVisualizer.Web/Diagram/DiagramEditor.cs`, add directly after `RemoveIndex` (after line 401, before `public DiagramEditResult SetTableMapping`):

```csharp
    public DiagramEditResult AddAlternateKey(string entityName, string propertyName)
    {
        var entity = Current.Entities.FirstOrDefault(e => e.Name == entityName);
        if (entity is null)
        {
            return DiagramEditResult.Fail($"Entity '{entityName}' not found.");
        }

        if (!entity.Properties.Any(p => p.Name == propertyName))
        {
            return DiagramEditResult.Fail($"Property '{propertyName}' not found on '{entityName}'.");
        }

        if (entity.AlternateKeys.Any(k => k.SequenceEqual(new[] { propertyName })))
        {
            return DiagramEditResult.Fail($"'{entityName}' already has an alternate key on '{propertyName}'.");
        }

        var newConfigSource = _configRewriter.AddAlternateKey(ConfigSource, entityName, new List<string> { propertyName });
        Apply(ClassSource, newConfigSource);
        return DiagramEditResult.Ok();
    }

    public DiagramEditResult ToggleAlternateKeyMembership(string entityName, IReadOnlyList<string> alternateKeyPropertyNames, string propertyName, bool include)
    {
        var entity = Current.Entities.FirstOrDefault(e => e.Name == entityName);
        if (entity is null)
        {
            return DiagramEditResult.Fail($"Entity '{entityName}' not found.");
        }

        var alternateKey = entity.AlternateKeys.FirstOrDefault(k => k.SequenceEqual(alternateKeyPropertyNames));
        if (alternateKey is null)
        {
            return DiagramEditResult.Fail($"Alternate key not found on '{entityName}'.");
        }

        var alreadyIncluded = alternateKey.Contains(propertyName);
        if (include == alreadyIncluded)
        {
            return DiagramEditResult.Ok();
        }

        var newPropertyNames = include
            ? alternateKey.Append(propertyName).ToList()
            : alternateKey.Where(name => name != propertyName).ToList();

        if (newPropertyNames.Count == 0)
        {
            var configAfterRemove = _configRewriter.RemoveAlternateKey(ConfigSource, entityName, alternateKey);
            Apply(ClassSource, configAfterRemove);
            return DiagramEditResult.Ok();
        }

        if (entity.AlternateKeys.Any(k => !ReferenceEquals(k, alternateKey) && k.SequenceEqual(newPropertyNames)))
        {
            return DiagramEditResult.Fail($"'{entityName}' already has an alternate key on [{string.Join(", ", newPropertyNames)}].");
        }

        var withoutOldAlternateKey = _configRewriter.RemoveAlternateKey(ConfigSource, entityName, alternateKey);
        var withNewAlternateKey = _configRewriter.AddAlternateKey(withoutOldAlternateKey, entityName, newPropertyNames);
        Apply(ClassSource, withNewAlternateKey);
        return DiagramEditResult.Ok();
    }

    public DiagramEditResult RemoveAlternateKey(string entityName, IReadOnlyList<string> propertyNames)
    {
        var entity = Current.Entities.FirstOrDefault(e => e.Name == entityName);
        if (entity is null)
        {
            return DiagramEditResult.Fail($"Entity '{entityName}' not found.");
        }

        var alternateKey = entity.AlternateKeys.FirstOrDefault(k => k.SequenceEqual(propertyNames));
        if (alternateKey is null)
        {
            return DiagramEditResult.Fail($"Alternate key not found on '{entityName}'.");
        }

        var newConfigSource = _configRewriter.RemoveAlternateKey(ConfigSource, entityName, propertyNames);
        Apply(ClassSource, newConfigSource);
        return DiagramEditResult.Ok();
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test EfSchemaVisualizer.slnx --filter "FullyQualifiedName~DiagramEditorPropertyPanelTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/EfSchemaVisualizer.Web/Diagram/DiagramEditor.cs tests/EfSchemaVisualizer.Web.Tests/Diagram/DiagramEditorPropertyPanelTests.cs
git commit -m "Add DiagramEditor.AddAlternateKey/ToggleAlternateKeyMembership/RemoveAlternateKey"
```

---

### Task 6: UI — `EntityNode.razor` "Alternate keys" panel

**Files:**
- Modify: `src/EfSchemaVisualizer.Web/Diagram/EntityNode.razor`

**Interfaces:**
- Consumes: `Node.Entity.AlternateKeys: IReadOnlyList<IReadOnlyList<string>>` (Task 1), `EditContext.Editor.AddAlternateKey`/`ToggleAlternateKeyMembership`/`RemoveAlternateKey` (Task 5), `EditContext.NotifyChangedAsync()` (existing).
- Produces: A rendered "Alternate keys:" section in the property expand panel; no new public interface (leaf UI component).

- [ ] **Step 1: Add the markup block**

In `src/EfSchemaVisualizer.Web/Diagram/EntityNode.razor`, insert a new block directly after the closing `</div>` of the "Indexes:" block (after line 271, before the closing `</div>` of the expand panel at line 272):

```razor
                            <div style="font-size: 0.8em; margin-top: 4px;">
                                <div>Alternate keys:</div>
                                @foreach (var alternateKey in Node.Entity.AlternateKeys)
                                {
                                    <div style="display: flex; align-items: center; gap: 4px; margin: 2px 0;">
                                        <input type="checkbox" checked="@alternateKey.Contains(property.Name)"
                                               @onchange="e => ToggleAlternateKeyMembership(property, alternateKey, (bool)(e.Value ?? false))"
                                               @onpointerdown:stopPropagation="true"
                                               @onmousedown:stopPropagation="true" />
                                        <span>[@string.Join(", ", alternateKey)]</span>
                                        <button type="button" title="Remove alternate key" aria-label="Remove alternate key" style="border: none; background: transparent; cursor: pointer;"
                                                @onclick="() => RemoveAlternateKey(alternateKey)"
                                                @onpointerdown:stopPropagation="true"
                                                @onmousedown:stopPropagation="true">×</button>
                                    </div>
                                }
                                <button type="button" @onclick="() => AddAlternateKey(property)"
                                        @onpointerdown:stopPropagation="true"
                                        @onmousedown:stopPropagation="true">+ New alternate key on this property</button>
                                @if (_alternateKeyError is not null)
                                {
                                    <div style="color: red;">@_alternateKeyError</div>
                                }
                            </div>
```

This sits inside the existing `@if (_expandedPropertyName == property.Name)` block, so `property` is in scope, matching the "Indexes:" block immediately above it.

- [ ] **Step 2: Add the code-behind handlers**

In the same file's `@code` block, add directly after `RemoveIndex` (after line 538, before `private void BeginPropertyRename`):

```csharp
    private string? _alternateKeyError;

    private async Task AddAlternateKey(PropertyModel property)
    {
        var result = EditContext.Editor.AddAlternateKey(Node.Entity.Name, property.Name);
        if (result.Success)
        {
            _alternateKeyError = null;
            await EditContext.NotifyChangedAsync();
        }
        else
        {
            _alternateKeyError = result.Error;
        }
    }

    private async Task ToggleAlternateKeyMembership(PropertyModel property, IReadOnlyList<string> alternateKey, bool include)
    {
        var result = EditContext.Editor.ToggleAlternateKeyMembership(Node.Entity.Name, alternateKey, property.Name, include);
        if (result.Success)
        {
            _alternateKeyError = null;
            await EditContext.NotifyChangedAsync();
        }
        else
        {
            _alternateKeyError = result.Error;
        }
    }

    private async Task RemoveAlternateKey(IReadOnlyList<string> alternateKey)
    {
        var result = EditContext.Editor.RemoveAlternateKey(Node.Entity.Name, alternateKey);
        if (result.Success)
        {
            _alternateKeyError = null;
            await EditContext.NotifyChangedAsync();
        }
        else
        {
            _alternateKeyError = result.Error;
        }
    }
```

- [ ] **Step 3: Build the Web project**

Run: `dotnet build src/EfSchemaVisualizer.Web/EfSchemaVisualizer.Web.csproj`
Expected: Build succeeds — Razor compiler resolves `Node.Entity.AlternateKeys`, `EditContext.Editor.AddAlternateKey`/etc. with no errors, and `TreatWarningsAsErrors` doesn't trip (no unused variables, all `@onchange` handlers correctly typed).

- [ ] **Step 4: Run the full test suite**

Run: `dotnet test EfSchemaVisualizer.slnx`
Expected: PASS, all projects green (this task adds no new automated UI tests — per the existing `EntityNode.razor` precedent, `PortRenderer` children aren't renderable under bUnit, so index/alternate-key panel interactions are covered at the `DiagramEditor` layer in Task 5, consistent with how `AddIndex`/`ToggleIndexMembership` are — or rather, aren't — separately covered; Task 5's tests exceed that existing bar).

- [ ] **Step 5: Commit**

```bash
git add src/EfSchemaVisualizer.Web/Diagram/EntityNode.razor
git commit -m "Surface alternate keys in the diagram's property expand panel"
```

---

### Task 7: Extend the round-trip fuzz corpus

**Files:**
- Modify: `tests/EfSchemaVisualizer.Core.Tests/RoundTripFuzzTests.cs`

**Interfaces:**
- Consumes: `FluentConfigParser.ParseAlternateKeys` (Task 2), `OnModelCreatingRewriter.AddAlternateKey` (Task 4).
- Produces: no new public interface — extends existing fixture/test coverage.

- [ ] **Step 1: Add a `HasAlternateKey` line to the corpus fixture**

In `tests/EfSchemaVisualizer.Core.Tests/RoundTripFuzzTests.cs`, add one line to the `Blog` entity block in `ConfigSource` (after line 53, `entity.HasIndex(e => e.Url).IsUnique();`):

```csharp
                    entity.HasAlternateKey(e => e.Url);
```

So the `Blog` block reads:

```csharp
                modelBuilder.Entity<Blog>(entity =>
                {
                    entity.HasKey(e => e.BlogId);
                    entity.ToTable("Blogs");
                    entity.Property(e => e.Url).HasMaxLength(500).IsRequired();
                    entity.Property(e => e.Rating).HasColumnName("BlogRating").HasColumnType("decimal(5,2)");
                    // Server-generated timestamp; HasDefaultValueSql is not modeled by the parser.
                    entity.Property(e => e.CreatedAt).HasDefaultValueSql("GETUTCDATE()");
                    entity.HasIndex(e => e.Url).IsUnique();
                    entity.HasAlternateKey(e => e.Url);
                });
```

- [ ] **Step 2: Add the no-op round-trip assertion**

In `NoOpEdits_AreByteIdenticalAcrossEveryConfigKindInTheCorpus` (around line 106-107, directly after the `index` assertion block), add:

```csharp
        var alternateKey = parser.ParseAlternateKeys(ConfigSource).Value.Single(c => c.EntityName == "Blog");
        Assert.Equal(ConfigSource, rewriter.AddAlternateKey(ConfigSource, "Blog", alternateKey.PropertyNames));
```

Unlike `SetKey`/`SetIndex` (which always reparse and reformat via `NormalizeWhitespace()`, even when the value being set matches what's already there — because "mutate" is their only path for an existing call), `AddAlternateKey`'s Task 4 implementation short-circuits on `alreadyExists` and returns the original `sourceCode` string directly with no parsing at all. So this no-op is byte-identical, not merely line-ending-identical — use `Assert.Equal`, matching the `maxLength`/`isRequired`/`columnName`/`columnType` assertions earlier in this same test (which hit an equivalent early-return-unchanged path), not `AssertOnlyLineEndingsDiffer`.

- [ ] **Step 3: Add the preservation assertion**

In `EditingOnePropertyPreservesEverythingElseVerbatim_IncludingUnsupportedConstructs`, add to the block of `Blog`-block assertions (near line 139, after `Assert.Contains("HasDefaultValueSql(\"GETUTCDATE()\")", blogBlockAfter);`):

```csharp
        Assert.Contains("HasAlternateKey(e => e.Url)", blogBlockAfter);
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test EfSchemaVisualizer.slnx --filter "FullyQualifiedName~RoundTripFuzzTests"`
Expected: PASS.

- [ ] **Step 5: Run the full suite one final time**

Run: `dotnet test EfSchemaVisualizer.slnx`
Expected: PASS across all three test projects (`EfSchemaVisualizer.Core.Tests`, `EfSchemaVisualizer.Web.Tests`, `EfSchemaVisualizer.SmokeTests`).

- [ ] **Step 6: Commit**

```bash
git add tests/EfSchemaVisualizer.Core.Tests/RoundTripFuzzTests.cs
git commit -m "Cover HasAlternateKey in the round-trip fuzz corpus"
```

---

### Task 8: Update the backlog

**Files:**
- Modify: `docs/backlog.md`

- [ ] **Step 1: Mark the item done**

Change the line at `docs/backlog.md:934` from:

```
- [ ] **`[found]` Alternate keys unread.** `HasAlternateKey(...)` — also a
      valid `HasForeignKey` principal target, so its absence can make parsed
      relationships subtly wrong.
```

to `- [x]` and append an `**Update:**` paragraph summarizing what shipped, following the exact style of every other completed item in the file (e.g. the "Keyless/view entities unread" item immediately above it) — name the new model field, parser method, rewriter methods, `DiagramEditor` methods, and UI location, and note that `HasPrincipalKey`/relationship cross-referencing remains explicitly out of scope per the design spec.

- [ ] **Step 2: Commit**

```bash
git add docs/backlog.md
git commit -m "Mark alternate-keys backlog item done"
```

---

## Self-Review Notes

- **Spec coverage:** Model shape (Task 1), parse (Task 2), merge + `DiagramModelBuilder` wiring (Task 3), rewrite add/remove (Task 4), `DiagramEditor` add/toggle/remove (Task 5), UI panel (Task 6), fuzz-corpus coverage (Task 7) all map directly to the spec's sections. `HasPrincipalKey` is explicitly left untouched, per spec.
- **Type consistency:** `AlternateKeyConfig.PropertyNames`, `EntityModel.AlternateKeys` (`IReadOnlyList<IReadOnlyList<string>>`), and every method signature in Tasks 4–6 use `IReadOnlyList<string>` for a single key's property names consistently throughout.
- **Placeholder scan:** none found — the earlier draft of Task 7's no-op assertion (byte-identical vs. line-ending-only) has been resolved by tracing `AddAlternateKey`'s `alreadyExists` branch (Task 4) back to its unmodified `return sourceCode;`, so Task 7 now specifies the correct `Assert.Equal` directly instead of leaving it open.
