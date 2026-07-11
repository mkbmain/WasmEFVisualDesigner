# HasIndex Fluent Config Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add full round-trip support for `HasIndex` fluent config — parse → merge → rewrite — covering single/composite columns, `.IsUnique()`, and the named-index overload.

**Architecture:** Follows the established parse → merge → rewrite pattern. A new `IndexModel` record lives on `EntityModel.Indexes` (a list, since entities may have multiple indexes). `FluentSyntaxHelpers.TryReadIndexPropertyNames` is a shared internal helper used by both parser and rewriter for property-set identity matching. Rewrite operations (`SetIndex`/`RemoveIndex`) identify which index to target by `SequenceEqual` on column names.

**Tech Stack:** C#, Roslyn (`Microsoft.CodeAnalysis.CSharp`), xUnit

## Global Constraints

- File-scoped namespace style (`namespace Foo.Bar;`) — match existing files
- `NormalizeWhitespace()` on all insertion and mutation paths, same as every existing rewriter method
- Diagnostics use the existing `Diagnostic` record in `EfSchemaVisualizer.Core.Parsing`
- No property-existence validation; no UI wiring (Blazor is Priority 4)
- Test sources use raw string literals (`"""..."""`) matching the existing test style

---

### Task 1: IndexModel, IndexConfig, EntityModel.Indexes

**Files:**
- Create: `src/EfSchemaVisualizer.Core/Model/IndexModel.cs`
- Create: `src/EfSchemaVisualizer.Core/Parsing/IndexConfig.cs`
- Modify: `src/EfSchemaVisualizer.Core/Model/EntityModel.cs`
- Modify: `tests/EfSchemaVisualizer.Core.Tests/Model/PropertyModelTests.cs`

**Interfaces:**
- Produces:
  - `IndexModel(IReadOnlyList<string> PropertyNames, bool IsUnique, string? Name = null)` — namespace `EfSchemaVisualizer.Core.Model`
  - `IndexConfig(string EntityName, IReadOnlyList<string> PropertyNames, bool IsUnique, string? Name)` — namespace `EfSchemaVisualizer.Core.Parsing`
  - `EntityModel.Indexes` — `IReadOnlyList<IndexModel>`, defaults to `new List<IndexModel>()`

- [ ] **Step 1: Write failing tests**

Add to `tests/EfSchemaVisualizer.Core.Tests/Model/PropertyModelTests.cs` (after the last existing test):

```csharp
[Fact]
public void EntityModel_Indexes_DefaultsToEmpty()
{
    var entity = new EntityModel("Person", new List<PropertyModel>());

    Assert.Empty(entity.Indexes);
}

[Fact]
public void EntityModel_WithIndexes_ProducesUpdatedCopy_LeavingOriginalUnchanged()
{
    var original = new EntityModel("Person", new List<PropertyModel>());
    var index = new IndexModel(new List<string> { "Email" }, IsUnique: true, Name: "IX_Email");

    var updated = original with { Indexes = new List<IndexModel> { index } };

    Assert.Empty(original.Indexes);
    Assert.Single(updated.Indexes);
    Assert.Equal("IX_Email", updated.Indexes[0].Name);
    Assert.True(updated.Indexes[0].IsUnique);
}

[Fact]
public void IndexModel_Name_DefaultsToNull()
{
    var index = new IndexModel(new List<string> { "Email" }, IsUnique: false);

    Assert.Null(index.Name);
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test tests/EfSchemaVisualizer.Core.Tests/EfSchemaVisualizer.Core.Tests.csproj -v n
```

Expected: compile errors — `IndexModel` and `EntityModel.Indexes` not found.

- [ ] **Step 3: Create IndexModel.cs**

```csharp
using System.Collections.Generic;

namespace EfSchemaVisualizer.Core.Model;

public sealed record IndexModel(
    IReadOnlyList<string> PropertyNames,
    bool IsUnique,
    string? Name = null);
```

- [ ] **Step 4: Create IndexConfig.cs**

```csharp
using System.Collections.Generic;

namespace EfSchemaVisualizer.Core.Parsing;

public sealed record IndexConfig(
    string EntityName,
    IReadOnlyList<string> PropertyNames,
    bool IsUnique,
    string? Name);
```

- [ ] **Step 5: Update EntityModel.cs**

Replace the entire file content with:

```csharp
using System.Collections.Generic;

namespace EfSchemaVisualizer.Core.Model;

public sealed record EntityModel(
    string Name,
    IReadOnlyList<PropertyModel> Properties,
    IReadOnlyList<string>? KeyPropertyNames = null,
    IReadOnlyList<IndexModel>? Indexes = null)
{
    public IReadOnlyList<string> KeyPropertyNames { get; init; } = KeyPropertyNames ?? new List<string>();
    public IReadOnlyList<IndexModel> Indexes { get; init; } = Indexes ?? new List<IndexModel>();
}
```

- [ ] **Step 6: Run tests to verify they pass**

```bash
dotnet test tests/EfSchemaVisualizer.Core.Tests/EfSchemaVisualizer.Core.Tests.csproj -v n
```

Expected: all tests pass (including all previously passing tests).

- [ ] **Step 7: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Model/IndexModel.cs \
        src/EfSchemaVisualizer.Core/Parsing/IndexConfig.cs \
        src/EfSchemaVisualizer.Core/Model/EntityModel.cs \
        tests/EfSchemaVisualizer.Core.Tests/Model/PropertyModelTests.cs
git commit -m "Add IndexModel, IndexConfig and EntityModel.Indexes for HasIndex support"
```

---

### Task 2: FluentSyntaxHelpers.TryReadIndexPropertyNames + FluentConfigParser.ParseIndexes

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/Parsing/FluentSyntaxHelpers.cs`
- Modify: `src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs`
- Modify: `tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs`

**Interfaces:**
- Consumes: `IndexConfig` from Task 1
- Produces:
  - `FluentSyntaxHelpers.TryReadIndexPropertyNames(InvocationExpressionSyntax hasIndexCall)` → `(IReadOnlyList<string> PropertyNames, string? Name)?` — `internal static`, used by both parser and rewriter
  - `FluentConfigParser.ParseIndexes(string sourceCode)` → `ParseResult<IReadOnlyList<IndexConfig>>`

**How `TryReadIndexPropertyNames` handles EF's overloads:**

| First arg | Second arg | Result |
|---|---|---|
| All bare string literals (any count) | — | column names; `Name = null` |
| Lambda (`e => e.X` or `e => new { e.A, e.B }`) | none | columns from lambda; `Name = null` |
| Lambda | string literal | columns from lambda; `Name = literal` |
| `new[] { "A", "B" }` | string literal | columns from array; `Name = literal` |
| anything else | — | `null` → diagnostic |

Explicit-name anonymous members (`new { K = e.A }`) return `null` (→ diagnostic), consistent with `HasKey`.

**How `TryReadIsUnique` works:** Private to `FluentConfigParser`. Starting from `hasIndexCall.Parent`, walks up through the syntax tree (stopping at any `StatementSyntax`). When it finds a `MemberAccessExpressionSyntax` whose `Name.Identifier.Text == "IsUnique"` and whose parent is an `InvocationExpressionSyntax`, it reads that invocation's arguments. Bare `.IsUnique()` → `true`; `.IsUnique(true/false)` → the literal; non-bool-literal arg → diagnostic + `false`; no `IsUnique` found → `false`.

- [ ] **Step 1: Write failing tests**

Add to `tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs` (after the last existing test):

```csharp
// ─── ParseIndexes ────────────────────────────────────────────────────────────

[Fact]
public void ParseIndexes_SinglePropertyLambda_NoUniqueNoName()
{
    const string source = """
        class Ctx : DbContext {
            protected override void OnModelCreating(ModelBuilder modelBuilder) {
                modelBuilder.Entity<Person>(entity => {
                    entity.HasIndex(e => e.Email);
                });
            }
        }
        """;

    var result = new FluentConfigParser().ParseIndexes(source);

    var config = Assert.Single(result.Value);
    Assert.Equal("Person", config.EntityName);
    Assert.Equal(new[] { "Email" }, config.PropertyNames);
    Assert.False(config.IsUnique);
    Assert.Null(config.Name);
    Assert.Empty(result.Diagnostics);
}

[Fact]
public void ParseIndexes_CompositeLambda_PreservesColumnOrder()
{
    const string source = """
        class Ctx : DbContext {
            protected override void OnModelCreating(ModelBuilder modelBuilder) {
                modelBuilder.Entity<Person>(entity => {
                    entity.HasIndex(e => new { e.LastName, e.FirstName });
                });
            }
        }
        """;

    var result = new FluentConfigParser().ParseIndexes(source);

    var config = Assert.Single(result.Value);
    Assert.Equal(new[] { "LastName", "FirstName" }, config.PropertyNames);
    Assert.False(config.IsUnique);
    Assert.Null(config.Name);
    Assert.Empty(result.Diagnostics);
}

[Fact]
public void ParseIndexes_SingleStringParam_ReadsColumnName()
{
    const string source = """
        class Ctx : DbContext {
            protected override void OnModelCreating(ModelBuilder modelBuilder) {
                modelBuilder.Entity<Person>(entity => {
                    entity.HasIndex("Email");
                });
            }
        }
        """;

    var result = new FluentConfigParser().ParseIndexes(source);

    var config = Assert.Single(result.Value);
    Assert.Equal(new[] { "Email" }, config.PropertyNames);
    Assert.Null(config.Name);
    Assert.Empty(result.Diagnostics);
}

[Fact]
public void ParseIndexes_BareStringParamsComposite_ReadsAllColumnNames()
{
    const string source = """
        class Ctx : DbContext {
            protected override void OnModelCreating(ModelBuilder modelBuilder) {
                modelBuilder.Entity<Person>(entity => {
                    entity.HasIndex("LastName", "FirstName");
                });
            }
        }
        """;

    var result = new FluentConfigParser().ParseIndexes(source);

    var config = Assert.Single(result.Value);
    Assert.Equal(new[] { "LastName", "FirstName" }, config.PropertyNames);
    Assert.Null(config.Name);
    Assert.Empty(result.Diagnostics);
}

[Fact]
public void ParseIndexes_LambdaWithIndexName_ReadsNameFromSecondArg()
{
    const string source = """
        class Ctx : DbContext {
            protected override void OnModelCreating(ModelBuilder modelBuilder) {
                modelBuilder.Entity<Person>(entity => {
                    entity.HasIndex(e => e.Email, "IX_Person_Email");
                });
            }
        }
        """;

    var result = new FluentConfigParser().ParseIndexes(source);

    var config = Assert.Single(result.Value);
    Assert.Equal(new[] { "Email" }, config.PropertyNames);
    Assert.Equal("IX_Person_Email", config.Name);
    Assert.Empty(result.Diagnostics);
}

[Fact]
public void ParseIndexes_StringArrayWithIndexName_ReadsColumnsAndName()
{
    const string source = """
        class Ctx : DbContext {
            protected override void OnModelCreating(ModelBuilder modelBuilder) {
                modelBuilder.Entity<Person>(entity => {
                    entity.HasIndex(new[] { "LastName", "FirstName" }, "IX_Person_Name");
                });
            }
        }
        """;

    var result = new FluentConfigParser().ParseIndexes(source);

    var config = Assert.Single(result.Value);
    Assert.Equal(new[] { "LastName", "FirstName" }, config.PropertyNames);
    Assert.Equal("IX_Person_Name", config.Name);
    Assert.Empty(result.Diagnostics);
}

[Fact]
public void ParseIndexes_IsUnique_Bare_SetsFlagTrue()
{
    const string source = """
        class Ctx : DbContext {
            protected override void OnModelCreating(ModelBuilder modelBuilder) {
                modelBuilder.Entity<Person>(entity => {
                    entity.HasIndex(e => e.Email).IsUnique();
                });
            }
        }
        """;

    var result = new FluentConfigParser().ParseIndexes(source);

    var config = Assert.Single(result.Value);
    Assert.Equal(new[] { "Email" }, config.PropertyNames);
    Assert.True(config.IsUnique);
    Assert.Empty(result.Diagnostics);
}

[Fact]
public void ParseIndexes_IsUnique_ExplicitFalse_SetsFlagFalse()
{
    const string source = """
        class Ctx : DbContext {
            protected override void OnModelCreating(ModelBuilder modelBuilder) {
                modelBuilder.Entity<Person>(entity => {
                    entity.HasIndex(e => e.Email).IsUnique(false);
                });
            }
        }
        """;

    var result = new FluentConfigParser().ParseIndexes(source);

    var config = Assert.Single(result.Value);
    Assert.False(config.IsUnique);
    Assert.Empty(result.Diagnostics);
}

[Fact]
public void ParseIndexes_IsUnique_NonBoolLiteralArg_EmitsDiagnosticAndDefaultsFalse()
{
    const string source = """
        class Ctx : DbContext {
            protected override void OnModelCreating(ModelBuilder modelBuilder) {
                modelBuilder.Entity<Person>(entity => {
                    entity.HasIndex(e => e.Email).IsUnique(someVariable);
                });
            }
        }
        """;

    var result = new FluentConfigParser().ParseIndexes(source);

    var config = Assert.Single(result.Value);
    Assert.False(config.IsUnique);
    var diag = Assert.Single(result.Diagnostics);
    Assert.Equal("UnreadableIsUniqueArgument", diag.Code);
    Assert.Equal("Person", diag.EntityName);
    Assert.Null(diag.PropertyName);
}

[Fact]
public void ParseIndexes_ExplicitNameAnonymousMember_EmitsDiagnosticAndSkipsIndex()
{
    const string source = """
        class Ctx : DbContext {
            protected override void OnModelCreating(ModelBuilder modelBuilder) {
                modelBuilder.Entity<Person>(entity => {
                    entity.HasIndex(e => new { Key = e.Email });
                });
            }
        }
        """;

    var result = new FluentConfigParser().ParseIndexes(source);

    Assert.Empty(result.Value);
    var diag = Assert.Single(result.Diagnostics);
    Assert.Equal("UnreadableHasIndexArgument", diag.Code);
    Assert.Equal("Person", diag.EntityName);
    Assert.Null(diag.PropertyName);
}

[Fact]
public void ParseIndexes_MultipleHasIndexCalls_AllAppearInResult()
{
    const string source = """
        class Ctx : DbContext {
            protected override void OnModelCreating(ModelBuilder modelBuilder) {
                modelBuilder.Entity<Person>(entity => {
                    entity.HasIndex(e => e.Email).IsUnique();
                    entity.HasIndex(e => new { e.LastName, e.FirstName });
                });
            }
        }
        """;

    var result = new FluentConfigParser().ParseIndexes(source);

    Assert.Equal(2, result.Value.Count);
    Assert.Equal(new[] { "Email" }, result.Value[0].PropertyNames);
    Assert.True(result.Value[0].IsUnique);
    Assert.Equal(new[] { "LastName", "FirstName" }, result.Value[1].PropertyNames);
    Assert.False(result.Value[1].IsUnique);
    Assert.Empty(result.Diagnostics);
}

[Fact]
public void ParseIndexes_NoHasIndexCalls_ReturnsEmpty()
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

    var result = new FluentConfigParser().ParseIndexes(source);

    Assert.Empty(result.Value);
    Assert.Empty(result.Diagnostics);
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test tests/EfSchemaVisualizer.Core.Tests/EfSchemaVisualizer.Core.Tests.csproj -v n
```

Expected: compile errors — `ParseIndexes` method not found.

- [ ] **Step 3: Add TryReadIndexPropertyNames to FluentSyntaxHelpers.cs**

Add the following two methods inside `FluentSyntaxHelpers` (after the `GetDbSetEntityTypeArgument` method, before the closing brace):

```csharp
internal static (IReadOnlyList<string> PropertyNames, string? Name)? TryReadIndexPropertyNames(
    InvocationExpressionSyntax hasIndexCall)
{
    var arguments = hasIndexCall.ArgumentList.Arguments;

    if (arguments.Count == 0)
        return null;

    var firstArg = arguments[0].Expression;
    var secondArg = arguments.Count >= 2 ? arguments[1].Expression : null;

    // All bare string literals → params overload (column names; no index name).
    if (arguments.All(a => a.Expression is LiteralExpressionSyntax lit
            && lit.IsKind(SyntaxKind.StringLiteralExpression)))
    {
        var names = arguments
            .Select(a => ((LiteralExpressionSyntax)a.Expression).Token.ValueText)
            .ToList();
        return (names, null);
    }

    // Lambda (+ optional string name).
    if (firstArg is SimpleLambdaExpressionSyntax { ExpressionBody: { } body })
    {
        var props = TryReadIndexPropertyNamesFromLambdaBody(body);
        if (props is null)
            return null;

        string? indexName = null;
        if (secondArg is LiteralExpressionSyntax nameLit
                && nameLit.IsKind(SyntaxKind.StringLiteralExpression))
            indexName = nameLit.Token.ValueText;
        else if (secondArg is not null)
            return null;

        return (props, indexName);
    }

    // new[] { "A", "B" } + string name.
    if (firstArg is ImplicitArrayCreationExpressionSyntax implicitArray
        && secondArg is LiteralExpressionSyntax nameArg
        && nameArg.IsKind(SyntaxKind.StringLiteralExpression))
    {
        var names = new List<string>();
        foreach (var expr in implicitArray.Initializer.Expressions)
        {
            if (expr is LiteralExpressionSyntax elemLit
                    && elemLit.IsKind(SyntaxKind.StringLiteralExpression))
                names.Add(elemLit.Token.ValueText);
            else
                return null;
        }
        return (names, nameArg.Token.ValueText);
    }

    return null;
}

private static IReadOnlyList<string>? TryReadIndexPropertyNamesFromLambdaBody(ExpressionSyntax body)
{
    if (body is MemberAccessExpressionSyntax { Name.Identifier.Text: var singleName })
        return new List<string> { singleName };

    if (body is AnonymousObjectCreationExpressionSyntax anonymousObject)
    {
        var names = new List<string>();
        foreach (var initializer in anonymousObject.Initializers)
        {
            if (initializer.NameEquals is not null
                || initializer.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: var name })
                return null;
            names.Add(name);
        }
        return names;
    }

    return null;
}
```

- [ ] **Step 4: Add ParseIndexes and TryReadIsUnique to FluentConfigParser.cs**

Add the following two methods to `FluentConfigParser` (after `ParseKeys`):

```csharp
public ParseResult<IReadOnlyList<IndexConfig>> ParseIndexes(string sourceCode)
{
    var tree = CSharpSyntaxTree.ParseText(sourceCode);
    var root = tree.GetCompilationUnitRoot();

    var results = new List<IndexConfig>();
    var diagnostics = new List<Diagnostic>();

    var entityNames = root.DescendantNodes()
        .OfType<InvocationExpressionSyntax>()
        .Select(FluentSyntaxHelpers.GetConfiguredEntityName)
        .Where(name => name is not null)
        .Distinct()!;

    foreach (var entityName in entityNames)
    {
        foreach (var entityInvocation in FluentSyntaxHelpers.FindEntityConfigInvocations(root, entityName!))
        {
            foreach (var hasIndexCall in FluentSyntaxHelpers.FindCallsNamed(entityInvocation, "HasIndex"))
            {
                var indexArgs = FluentSyntaxHelpers.TryReadIndexPropertyNames(hasIndexCall);

                if (indexArgs is null)
                {
                    diagnostics.Add(new Diagnostic(
                        "UnreadableHasIndexArgument",
                        "HasIndex argument(s) could not be read as property name(s).",
                        entityName,
                        PropertyName: null,
                        hasIndexCall.Span));
                    continue;
                }

                var (isUnique, isUniqueDiag) = TryReadIsUnique(hasIndexCall, entityName!);
                if (isUniqueDiag is not null)
                    diagnostics.Add(isUniqueDiag);

                results.Add(new IndexConfig(entityName!, indexArgs.Value.PropertyNames, isUnique, indexArgs.Value.Name));
            }
        }
    }

    return new ParseResult<IReadOnlyList<IndexConfig>>(results, diagnostics);
}

private static (bool IsUnique, Diagnostic? Diagnostic) TryReadIsUnique(
    InvocationExpressionSyntax hasIndexCall, string entityName)
{
    SyntaxNode? cursor = hasIndexCall.Parent;
    while (cursor is not null && cursor is not StatementSyntax)
    {
        if (cursor is MemberAccessExpressionSyntax { Name.Identifier.Text: "IsUnique" }
            && cursor.Parent is InvocationExpressionSyntax isUniqueInvocation)
        {
            var arg = isUniqueInvocation.ArgumentList.Arguments.FirstOrDefault();
            if (arg is null)
                return (true, null);

            if (arg.Expression is LiteralExpressionSyntax literal
                && (literal.IsKind(SyntaxKind.TrueLiteralExpression)
                    || literal.IsKind(SyntaxKind.FalseLiteralExpression)))
                return (literal.IsKind(SyntaxKind.TrueLiteralExpression), null);

            return (false, new Diagnostic(
                "UnreadableIsUniqueArgument",
                "IsUnique argument is not a boolean literal and could not be read.",
                entityName,
                PropertyName: null,
                arg.Span));
        }

        cursor = cursor.Parent;
    }

    return (false, null);
}
```

- [ ] **Step 5: Run tests to verify they pass**

```bash
dotnet test tests/EfSchemaVisualizer.Core.Tests/EfSchemaVisualizer.Core.Tests.csproj -v n
```

Expected: all tests pass.

- [ ] **Step 6: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Parsing/FluentSyntaxHelpers.cs \
        src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs \
        tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs
git commit -m "Add FluentConfigParser.ParseIndexes and FluentSyntaxHelpers.TryReadIndexPropertyNames"
```

---

### Task 3: ModelMerger.ApplyIndexes

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/Parsing/ModelMerger.cs`
- Modify: `tests/EfSchemaVisualizer.Core.Tests/Parsing/ModelMergerTests.cs`

**Interfaces:**
- Consumes: `IndexConfig` and `IndexModel` from Task 1
- Produces: `ModelMerger.ApplyIndexes(EntityModel entity, IReadOnlyList<IndexConfig> configs)` → `EntityModel`

Unlike `ApplyKeys` (which takes `FirstOrDefault`), `ApplyIndexes` collects **all** configs matching `entity.Name` — an entity may have multiple indexes.

- [ ] **Step 1: Write failing tests**

Add to `tests/EfSchemaVisualizer.Core.Tests/Parsing/ModelMergerTests.cs` (after the last existing test):

```csharp
// ─── ApplyIndexes ────────────────────────────────────────────────────────────

[Fact]
public void ApplyIndexes_PopulatesIndexesFromMatchingConfig()
{
    var entity = new EntityModel("Person", new List<PropertyModel>
    {
        new("Email", "string", IsNullable: true, MaxLength: null)
    });
    var configs = new List<IndexConfig>
    {
        new("Person", new List<string> { "Email" }, IsUnique: true, Name: "IX_Person_Email")
    };

    var result = ModelMerger.ApplyIndexes(entity, configs);

    var index = Assert.Single(result.Indexes);
    Assert.Equal(new[] { "Email" }, index.PropertyNames);
    Assert.True(index.IsUnique);
    Assert.Equal("IX_Person_Email", index.Name);
}

[Fact]
public void ApplyIndexes_CollectsAllMatchingConfigsForSameEntity()
{
    var entity = new EntityModel("Person", new List<PropertyModel>());
    var configs = new List<IndexConfig>
    {
        new("Person", new List<string> { "Email" }, IsUnique: true, Name: null),
        new("Person", new List<string> { "LastName", "FirstName" }, IsUnique: false, Name: null)
    };

    var result = ModelMerger.ApplyIndexes(entity, configs);

    Assert.Equal(2, result.Indexes.Count);
    Assert.Equal(new[] { "Email" }, result.Indexes[0].PropertyNames);
    Assert.Equal(new[] { "LastName", "FirstName" }, result.Indexes[1].PropertyNames);
}

[Fact]
public void ApplyIndexes_LeavesIndexesEmptyWhenNoMatchingConfigs()
{
    var entity = new EntityModel("Person", new List<PropertyModel>());
    var configs = new List<IndexConfig>
    {
        new("Order", new List<string> { "OrderDate" }, IsUnique: false, Name: null)
    };

    var result = ModelMerger.ApplyIndexes(entity, configs);

    Assert.Empty(result.Indexes);
}

[Fact]
public void ApplyIndexes_DoesNotAffectPropertiesOrKeyPropertyNames()
{
    var prop = new PropertyModel("Email", "string", IsNullable: false, MaxLength: null);
    var entity = new EntityModel("Person", new List<PropertyModel> { prop },
        KeyPropertyNames: new List<string> { "Id" });
    var configs = new List<IndexConfig>
    {
        new("Person", new List<string> { "Email" }, IsUnique: true, Name: null)
    };

    var result = ModelMerger.ApplyIndexes(entity, configs);

    Assert.Single(result.Properties);
    Assert.Equal("Email", result.Properties[0].Name);
    Assert.Equal(new[] { "Id" }, result.KeyPropertyNames);
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test tests/EfSchemaVisualizer.Core.Tests/EfSchemaVisualizer.Core.Tests.csproj -v n
```

Expected: compile errors — `ApplyIndexes` not found.

- [ ] **Step 3: Add ApplyIndexes to ModelMerger.cs**

Add after the `ApplyKeys` method:

```csharp
public static EntityModel ApplyIndexes(EntityModel entity, IReadOnlyList<IndexConfig> configs)
{
    var indexes = configs
        .Where(c => c.EntityName == entity.Name)
        .Select(c => new IndexModel(c.PropertyNames, c.IsUnique, c.Name))
        .ToList();

    return entity with { Indexes = indexes };
}
```

`ModelMerger.cs` already imports `EfSchemaVisualizer.Core.Model` — no new `using` directive needed.

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet test tests/EfSchemaVisualizer.Core.Tests/EfSchemaVisualizer.Core.Tests.csproj -v n
```

Expected: all tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Parsing/ModelMerger.cs \
        tests/EfSchemaVisualizer.Core.Tests/Parsing/ModelMergerTests.cs
git commit -m "Add ModelMerger.ApplyIndexes for HasIndex support"
```

---

### Task 4: OnModelCreatingRewriter.SetIndex + RemoveIndex

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs`
- Modify: `tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs`
- Modify: `docs/backlog.md`

**Interfaces:**
- Consumes:
  - `FluentSyntaxHelpers.TryReadIndexPropertyNames` (Task 2) — for property-set identity matching
  - Existing private helpers: `FindOnModelCreatingMethod`, `BuildEntityInvocationStatement` — do **not** duplicate these
- Produces:
  - `OnModelCreatingRewriter.SetIndex(string sourceCode, string entityName, IReadOnlyList<string> propertyNames, bool isUnique, string? name = null)` → `string`
  - `OnModelCreatingRewriter.RemoveIndex(string sourceCode, string entityName, IReadOnlyList<string> propertyNames)` → `string`

**SetIndex three-case dispatch (mirrors SetKey):**
1. **Mutate** — existing `HasIndex` with matching property-set found: extract receiver name from `((MemberAccessExpressionSyntax)hasIndexCall.Expression).Expression.ToString()`, find the enclosing `ExpressionStatementSyntax` via `.Ancestors().OfType<ExpressionStatementSyntax>().First()`, replace it with a freshly-built canonical statement. (Rebuilding the whole statement cleanly handles toggling `.IsUnique()` and the name without token surgery.)
2. **Insert statement** — entity block exists, no matching index: append new `HasIndex` statement to the lambda `Block`.
3. **Synthesize block** — entity has no config at all: mint a new `Entity<T>(entity => { ... })` block via the existing `BuildEntityInvocationStatement`.

**RemoveIndex:** find the `ExpressionStatementSyntax` ancestor of the matching `HasIndex` call (this includes any chained `.IsUnique()`) and remove it entirely. No-op if no match.

**Canonical write form (always):**
- Single column → `e => e.X`; composite → `e => new { e.A, e.B }`
- `name != null` → second argument `"IX_..."` in the argument list
- `isUnique == true` → chain `.IsUnique()` (bare form); `false` → omit entirely

- [ ] **Step 1: Write failing tests**

Add to `tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs` (after the last existing test):

```csharp
// ─── SetIndex / RemoveIndex ──────────────────────────────────────────────────

[Fact]
public void SetIndex_MutatesExistingHasIndex_ToAddIsUnique()
{
    const string source = """
        class Ctx : DbContext {
            protected override void OnModelCreating(ModelBuilder modelBuilder) {
                modelBuilder.Entity<Person>(entity => {
                    entity.HasIndex(e => e.Email);
                });
            }
        }
        """;

    var result = new OnModelCreatingRewriter().SetIndex(source, "Person", new[] { "Email" }, isUnique: true);

    var configs = new FluentConfigParser().ParseIndexes(result);
    var config = Assert.Single(configs.Value);
    Assert.Equal(new[] { "Email" }, config.PropertyNames);
    Assert.True(config.IsUnique);
    Assert.Null(config.Name);
}

[Fact]
public void SetIndex_MutatesExistingHasIndex_ToRemoveUniqueness()
{
    const string source = """
        class Ctx : DbContext {
            protected override void OnModelCreating(ModelBuilder modelBuilder) {
                modelBuilder.Entity<Person>(entity => {
                    entity.HasIndex(e => e.Email).IsUnique();
                });
            }
        }
        """;

    var result = new OnModelCreatingRewriter().SetIndex(source, "Person", new[] { "Email" }, isUnique: false);

    Assert.DoesNotContain("IsUnique", result);
    var configs = new FluentConfigParser().ParseIndexes(result);
    var config = Assert.Single(configs.Value);
    Assert.False(config.IsUnique);
}

[Fact]
public void SetIndex_MutatesExistingHasIndex_ToChangeName()
{
    const string source = """
        class Ctx : DbContext {
            protected override void OnModelCreating(ModelBuilder modelBuilder) {
                modelBuilder.Entity<Person>(entity => {
                    entity.HasIndex(e => e.Email, "IX_Old");
                });
            }
        }
        """;

    var result = new OnModelCreatingRewriter().SetIndex(source, "Person", new[] { "Email" }, isUnique: false, name: "IX_New");

    var configs = new FluentConfigParser().ParseIndexes(result);
    var config = Assert.Single(configs.Value);
    Assert.Equal("IX_New", config.Name);
    Assert.False(config.IsUnique);
}

[Fact]
public void SetIndex_InsertsStatementIntoExistingEntityBlock()
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

    var result = new OnModelCreatingRewriter().SetIndex(source, "Person", new[] { "Email" }, isUnique: true);

    var configs = new FluentConfigParser().ParseIndexes(result);
    var config = Assert.Single(configs.Value);
    Assert.Equal(new[] { "Email" }, config.PropertyNames);
    Assert.True(config.IsUnique);
    Assert.Contains("HasMaxLength", result);
}

[Fact]
public void SetIndex_SynthesizesNewEntityBlock_WhenEntityNotConfigured()
{
    const string source = """
        class Ctx : DbContext {
            protected override void OnModelCreating(ModelBuilder modelBuilder) {
            }
        }
        """;

    var result = new OnModelCreatingRewriter().SetIndex(source, "Person", new[] { "Email" }, isUnique: false, name: "IX_Person_Email");

    var configs = new FluentConfigParser().ParseIndexes(result);
    var config = Assert.Single(configs.Value);
    Assert.Equal("Person", config.EntityName);
    Assert.Equal(new[] { "Email" }, config.PropertyNames);
    Assert.Equal("IX_Person_Email", config.Name);
    Assert.False(config.IsUnique);
}

[Fact]
public void SetIndex_CompositeColumns_WrittenInOrder()
{
    const string source = """
        class Ctx : DbContext {
            protected override void OnModelCreating(ModelBuilder modelBuilder) {
            }
        }
        """;

    var result = new OnModelCreatingRewriter().SetIndex(source, "Person", new[] { "LastName", "FirstName" }, isUnique: false);

    var configs = new FluentConfigParser().ParseIndexes(result);
    var config = Assert.Single(configs.Value);
    Assert.Equal(new[] { "LastName", "FirstName" }, config.PropertyNames);
}

[Fact]
public void RemoveIndex_RemovesStatementIncludingIsUniqueChain()
{
    const string source = """
        class Ctx : DbContext {
            protected override void OnModelCreating(ModelBuilder modelBuilder) {
                modelBuilder.Entity<Person>(entity => {
                    entity.HasIndex(e => e.Email).IsUnique();
                });
            }
        }
        """;

    var result = new OnModelCreatingRewriter().RemoveIndex(source, "Person", new[] { "Email" });

    Assert.DoesNotContain("HasIndex", result);
    Assert.DoesNotContain("IsUnique", result);
}

[Fact]
public void RemoveIndex_LeavesOtherIndexesUntouched()
{
    const string source = """
        class Ctx : DbContext {
            protected override void OnModelCreating(ModelBuilder modelBuilder) {
                modelBuilder.Entity<Person>(entity => {
                    entity.HasIndex(e => e.Email).IsUnique();
                    entity.HasIndex(e => new { e.LastName, e.FirstName });
                });
            }
        }
        """;

    var result = new OnModelCreatingRewriter().RemoveIndex(source, "Person", new[] { "Email" });

    var configs = new FluentConfigParser().ParseIndexes(result);
    var config = Assert.Single(configs.Value);
    Assert.Equal(new[] { "LastName", "FirstName" }, config.PropertyNames);
}

[Fact]
public void RemoveIndex_IsNoop_WhenNoMatchingIndex()
{
    const string source = """
        class Ctx : DbContext {
            protected override void OnModelCreating(ModelBuilder modelBuilder) {
                modelBuilder.Entity<Person>(entity => {
                    entity.HasIndex(e => e.Email);
                });
            }
        }
        """;

    var result = new OnModelCreatingRewriter().RemoveIndex(source, "Person", new[] { "PhoneNumber" });

    Assert.Equal(source, result);
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test tests/EfSchemaVisualizer.Core.Tests/EfSchemaVisualizer.Core.Tests.csproj -v n
```

Expected: compile errors — `SetIndex`, `RemoveIndex` not found.

- [ ] **Step 3: Add SetIndex, RemoveIndex, and their private helpers to OnModelCreatingRewriter.cs**

Add the following methods to `OnModelCreatingRewriter` (before the closing brace of the class). Do **not** duplicate the existing `FindOnModelCreatingMethod` or `BuildEntityInvocationStatement` — call them directly.

```csharp
public string SetIndex(string sourceCode, string entityName, IReadOnlyList<string> propertyNames, bool isUnique, string? name = null)
{
    var tree = CSharpSyntaxTree.ParseText(sourceCode);
    var root = tree.GetCompilationUnitRoot();

    var entityInvocations = FluentSyntaxHelpers.FindEntityConfigInvocations(root, entityName).ToList();

    var existingHasIndexCall = entityInvocations
        .SelectMany(inv => FluentSyntaxHelpers.FindCallsNamed(inv, "HasIndex"))
        .FirstOrDefault(call =>
        {
            var args = FluentSyntaxHelpers.TryReadIndexPropertyNames(call);
            return args is not null && args.Value.PropertyNames.SequenceEqual(propertyNames);
        });

    if (existingHasIndexCall is not null)
    {
        return MutateExistingIndex(root, existingHasIndexCall, propertyNames, isUnique, name);
    }

    var existingEntityInvocation = entityInvocations.FirstOrDefault();

    if (existingEntityInvocation is not null)
    {
        return InsertIndexStatement(root, existingEntityInvocation, propertyNames, isUnique, name);
    }

    return InsertIndexEntityBlock(root, entityName, propertyNames, isUnique, name);
}

private static string MutateExistingIndex(
    CompilationUnitSyntax root,
    InvocationExpressionSyntax hasIndexCall,
    IReadOnlyList<string> propertyNames,
    bool isUnique,
    string? name)
{
    var blockReceiverName = ((MemberAccessExpressionSyntax)hasIndexCall.Expression).Expression.ToString();
    var existingStatement = hasIndexCall.Ancestors().OfType<ExpressionStatementSyntax>().First();
    var newStatement = BuildHasIndexStatement(blockReceiverName, propertyNames, isUnique, name);

    var newRoot = root.ReplaceNode(existingStatement, newStatement);
    return newRoot.NormalizeWhitespace().ToFullString();
}

private static string InsertIndexStatement(
    CompilationUnitSyntax root,
    InvocationExpressionSyntax entityInvocation,
    IReadOnlyList<string> propertyNames,
    bool isUnique,
    string? name)
{
    var lambda = (SimpleLambdaExpressionSyntax)entityInvocation.ArgumentList.Arguments.Single().Expression;
    var block = lambda.Block!;
    var blockReceiverName = lambda.Parameter.Identifier.Text;

    var newStatement = BuildHasIndexStatement(blockReceiverName, propertyNames, isUnique, name);
    var newBlock = block.AddStatements(newStatement);

    var newRoot = root.ReplaceNode(block, newBlock);
    return newRoot.NormalizeWhitespace().ToFullString();
}

private static string InsertIndexEntityBlock(
    CompilationUnitSyntax root,
    string entityName,
    IReadOnlyList<string> propertyNames,
    bool isUnique,
    string? name)
{
    var method = FindOnModelCreatingMethod(root);

    var methodBody = method.Body
        ?? throw new InvalidOperationException("OnModelCreating has no method body.");

    var modelBuilderParamName = method.ParameterList.Parameters.Single().Identifier.Text;

    var indexStatement = BuildHasIndexStatement("entity", propertyNames, isUnique, name);
    var entityBlockStatement = BuildEntityInvocationStatement(
        modelBuilderParamName, entityName, SyntaxFactory.Block(indexStatement));

    var newMethodBody = methodBody.AddStatements(entityBlockStatement);
    var newRoot = root.ReplaceNode(methodBody, newMethodBody);
    return newRoot.NormalizeWhitespace().ToFullString();
}

private static ExpressionStatementSyntax BuildHasIndexStatement(
    string blockReceiverName,
    IReadOnlyList<string> propertyNames,
    bool isUnique,
    string? name)
{
    ExpressionSyntax expression = SyntaxFactory.InvocationExpression(
        SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression,
            SyntaxFactory.IdentifierName(blockReceiverName),
            SyntaxFactory.IdentifierName("HasIndex")),
        BuildHasIndexArgumentList(propertyNames, name));

    if (isUnique)
    {
        expression = SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                expression,
                SyntaxFactory.IdentifierName("IsUnique")),
            SyntaxFactory.ArgumentList());
    }

    return SyntaxFactory.ExpressionStatement(expression);
}

private static ArgumentListSyntax BuildHasIndexArgumentList(IReadOnlyList<string> propertyNames, string? name)
{
    const string lambdaParam = "e";

    ExpressionSyntax body = propertyNames.Count == 1
        ? SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression,
            SyntaxFactory.IdentifierName(lambdaParam),
            SyntaxFactory.IdentifierName(propertyNames[0]))
        : SyntaxFactory.AnonymousObjectCreationExpression(
            SyntaxFactory.SeparatedList(
                propertyNames.Select(n => SyntaxFactory.AnonymousObjectMemberDeclarator(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName(lambdaParam),
                        SyntaxFactory.IdentifierName(n))))));

    var lambdaArg = SyntaxFactory.Argument(
        SyntaxFactory.SimpleLambdaExpression(
            SyntaxFactory.Parameter(SyntaxFactory.Identifier(lambdaParam)),
            body));

    if (name is not null)
    {
        return SyntaxFactory.ArgumentList(
            SyntaxFactory.SeparatedList(new[]
            {
                lambdaArg,
                SyntaxFactory.Argument(
                    SyntaxFactory.LiteralExpression(
                        SyntaxKind.StringLiteralExpression,
                        SyntaxFactory.Literal(name)))
            }));
    }

    return SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(lambdaArg));
}

public string RemoveIndex(string sourceCode, string entityName, IReadOnlyList<string> propertyNames)
{
    var tree = CSharpSyntaxTree.ParseText(sourceCode);
    var root = tree.GetCompilationUnitRoot();

    var entityInvocations = FluentSyntaxHelpers.FindEntityConfigInvocations(root, entityName).ToList();

    var existingHasIndexCall = entityInvocations
        .SelectMany(inv => FluentSyntaxHelpers.FindCallsNamed(inv, "HasIndex"))
        .FirstOrDefault(call =>
        {
            var args = FluentSyntaxHelpers.TryReadIndexPropertyNames(call);
            return args is not null && args.Value.PropertyNames.SequenceEqual(propertyNames);
        });

    if (existingHasIndexCall is null)
        return sourceCode;

    var statement = existingHasIndexCall.Ancestors().OfType<ExpressionStatementSyntax>().First();
    var newRoot = root.RemoveNode(statement, SyntaxRemoveOptions.KeepNoTrivia)!;
    return newRoot.NormalizeWhitespace().ToFullString();
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet test tests/EfSchemaVisualizer.Core.Tests/EfSchemaVisualizer.Core.Tests.csproj -v n
```

Expected: all tests pass.

- [ ] **Step 5: Check off the backlog item**

In `docs/backlog.md`, find:

```
- [ ] **`[spec]` Indexes** — `HasIndex`, including unique.
```

Replace with:

```
- [x] **`[spec]` Indexes** — `HasIndex`, including unique.
      **Update:** `FluentConfigParser.ParseIndexes` reads single/composite lambda,
      bare string params, lambda+name, and string-array+name overloads into `IndexConfig`;
      `ModelMerger.ApplyIndexes` folds all matching configs into `EntityModel.Indexes`
      (a list — entities may have multiple indexes); `OnModelCreatingRewriter.SetIndex`/
      `RemoveIndex` write it back using property-set identity (`SequenceEqual`), always
      emitting the canonical lambda form with optional `.IsUnique()` chain and inline
      name arg (see `2026-07-11-has-index-config-design.md`).
```

- [ ] **Step 6: Commit**

```bash
git add src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs \
        tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs \
        docs/backlog.md
git commit -m "Add OnModelCreatingRewriter.SetIndex and RemoveIndex for HasIndex support"
```
