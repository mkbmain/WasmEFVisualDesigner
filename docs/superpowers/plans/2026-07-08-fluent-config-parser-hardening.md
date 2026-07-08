# FluentConfigParser Hardening Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix the four remaining Priority 0 backlog gaps in `FluentConfigParser`/`FluentSyntaxHelpers` — hardcoded `modelBuilder` receiver name, unread `Property` string-overload/block-bodied lambdas, silently-dropped non-literal `HasMaxLength` arguments — and wire the existing `Diagnostic`/`ParseResult<T>` channel into `FluentConfigParser` so nothing vanishes without a signal.

**Architecture:** `GetConfiguredEntityName` and `GetPropertyNameFor` in `FluentSyntaxHelpers.cs` gain additional syntax shapes they can resolve. `FluentConfigParser.ParseMaxLengths` changes its return type from `IReadOnlyList<MaxLengthConfig>` to `ParseResult<IReadOnlyList<MaxLengthConfig>>`, collecting a `Diagnostic` whenever a `HasMaxLength` call's property name or argument value can't be resolved. This mirrors the pattern `EntityClassParser.Parse` already established.

**Tech Stack:** C# / .NET 10, Roslyn (`Microsoft.CodeAnalysis.CSharp` 5.6.0), xUnit.

## Global Constraints

- Confined to `FluentSyntaxHelpers.cs` and `FluentConfigParser.cs` (plus their test files and the two callers below). No other parsing files change.
- No semantic/symbol resolution — everything stays syntax-only, matching the existing codebase's approach. Non-literal `HasMaxLength` arguments are diagnosed, never resolved to a value.
- Do not chase calls through helper methods (e.g. `ConfigurePerson(modelBuilder)`) — receiver-name matching is a shape match only (any identifier + generic `.Entity<T>(...)` member access).
- Follow existing repo conventions: `sealed record`/`sealed class`, xUnit `[Fact]` tests with C# raw string literals (`"""`) for source fixtures, matching the style in `FluentConfigParserTests.cs`.

---

### Task 1: Receiver-name shape match

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/Parsing/FluentSyntaxHelpers.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs`

**Interfaces:**
- Produces: `FluentSyntaxHelpers.GetConfiguredEntityName(InvocationExpressionSyntax)` now matches `<anyIdentifier>.Entity<T>(...)` instead of requiring the identifier `modelBuilder`.

- [ ] **Step 1: Write the failing test**

Add to `FluentConfigParserTests.cs`:

```csharp
    private const string SourceWithRenamedBuilder = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder builder)
            {
                builder.Entity<Person>(entity =>
                {
                    entity.Property(e => e.Name).HasMaxLength(100);
                });
            }
        }
        """;

    [Fact]
    public void ParseMaxLengths_RenamedBuilderParameter_StillResolvesEntity()
    {
        var result = new FluentConfigParser().ParseMaxLengths(SourceWithRenamedBuilder);

        Assert.Empty(result.Diagnostics);
        Assert.Contains(result.Value, c => c is { EntityName: "Person", PropertyName: "Name", MaxLength: 100 });
    }
```

- [ ] **Step 2: Run test to verify it fails to compile**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests/EfSchemaVisualizer.Core.Tests.csproj`
Expected: Build error — `ParseMaxLengths` still returns `IReadOnlyList<MaxLengthConfig>`, which has no `.Value`/`.Diagnostics` members. (This will keep failing to compile until Task 4 changes the signature — that's expected; leave the test in place, it becomes a real pass/fail once Task 4 lands.)

- [ ] **Step 3: Fix `GetConfiguredEntityName`**

In `FluentSyntaxHelpers.cs`, change:

```csharp
    internal static string? GetConfiguredEntityName(InvocationExpressionSyntax invocation)
    {
        return invocation.Expression is MemberAccessExpressionSyntax
        {
            Expression: IdentifierNameSyntax { Identifier.Text: "modelBuilder" },
            Name: GenericNameSyntax { Identifier.Text: "Entity" } generic
        }
            ? generic.TypeArgumentList.Arguments.FirstOrDefault()?.ToString()
            : null;
    }
```

to:

```csharp
    internal static string? GetConfiguredEntityName(InvocationExpressionSyntax invocation)
    {
        return invocation.Expression is MemberAccessExpressionSyntax
        {
            Expression: IdentifierNameSyntax,
            Name: GenericNameSyntax { Identifier.Text: "Entity" } generic
        }
            ? generic.TypeArgumentList.Arguments.FirstOrDefault()?.ToString()
            : null;
    }
```

- [ ] **Step 4: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Parsing/FluentSyntaxHelpers.cs tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs
git commit -m "Match Entity<T> configuration by shape, not receiver identifier name"
```

---

### Task 2: `Property` string-overload and block-bodied lambda support

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/Parsing/FluentSyntaxHelpers.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: `FluentSyntaxHelpers.GetPropertyNameFor(InvocationExpressionSyntax)` additionally resolves `entity.Property("Name")` (string literal argument) and `entity.Property(e => { return e.Name; })` (block-bodied lambda, single return statement of a member access). Falls through to `null` for anything else, same as today.

- [ ] **Step 1: Write the failing tests**

Add to `FluentConfigParserTests.cs`:

```csharp
    private const string SourceWithStringPropertyOverload = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.Property("Name").HasMaxLength(100);
                });
            }
        }
        """;

    [Fact]
    public void ParseMaxLengths_PropertyStringOverload_IsRead()
    {
        var result = new FluentConfigParser().ParseMaxLengths(SourceWithStringPropertyOverload);

        Assert.Empty(result.Diagnostics);
        Assert.Contains(result.Value, c => c is { EntityName: "Person", PropertyName: "Name", MaxLength: 100 });
    }

    private const string SourceWithBlockBodiedLambda = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.Property(e => { return e.Name; }).HasMaxLength(100);
                });
            }
        }
        """;

    [Fact]
    public void ParseMaxLengths_BlockBodiedLambdaWithSingleReturn_IsRead()
    {
        var result = new FluentConfigParser().ParseMaxLengths(SourceWithBlockBodiedLambda);

        Assert.Empty(result.Diagnostics);
        Assert.Contains(result.Value, c => c is { EntityName: "Person", PropertyName: "Name", MaxLength: 100 });
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests/EfSchemaVisualizer.Core.Tests.csproj`
Expected: Still a build error from Task 1's pending signature change (Task 4) — that's fine, these tests will start reporting real results once Task 4 lands. Note this and proceed.

- [ ] **Step 3: Extend `GetPropertyNameFor`**

In `FluentSyntaxHelpers.cs`, replace:

```csharp
    /// Given a fluent call like `entity.Property(e => e.Name).HasMaxLength(100)`, returns "Name".
    public static string? GetPropertyNameFor(InvocationExpressionSyntax fluentCall)
    {
        if (fluentCall.Expression is not MemberAccessExpressionSyntax
            {
                Expression: InvocationExpressionSyntax propertyInvocation
            })
        {
            return null;
        }

        if (propertyInvocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Property" })
        {
            return null;
        }

        var lambdaArg = propertyInvocation.ArgumentList.Arguments
            .Select(a => a.Expression)
            .OfType<SimpleLambdaExpressionSyntax>()
            .FirstOrDefault();

        return lambdaArg?.ExpressionBody is MemberAccessExpressionSyntax { Name.Identifier.Text: var propertyName }
            ? propertyName
            : null;
    }
```

with:

```csharp
    /// Given a fluent call like `entity.Property(e => e.Name).HasMaxLength(100)`, returns "Name".
    /// Also resolves the string overload `entity.Property("Name")` and a block-bodied lambda with
    /// a single `return e.Name;` statement.
    public static string? GetPropertyNameFor(InvocationExpressionSyntax fluentCall)
    {
        if (fluentCall.Expression is not MemberAccessExpressionSyntax
            {
                Expression: InvocationExpressionSyntax propertyInvocation
            })
        {
            return null;
        }

        if (propertyInvocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Property" })
        {
            return null;
        }

        var argumentExpression = propertyInvocation.ArgumentList.Arguments
            .Select(a => a.Expression)
            .FirstOrDefault();

        return argumentExpression switch
        {
            SimpleLambdaExpressionSyntax { ExpressionBody: MemberAccessExpressionSyntax { Name.Identifier.Text: var name } } => name,
            SimpleLambdaExpressionSyntax { Block: { Statements: [ReturnStatementSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: var name } }] } } => name,
            LiteralExpressionSyntax { Token.ValueText: var text } literal when literal.IsKind(SyntaxKind.StringLiteralExpression) => text,
            _ => null,
        };
    }
```

- [ ] **Step 4: Add `using Microsoft.CodeAnalysis.CSharp;` if missing**

Check the top of `FluentSyntaxHelpers.cs` — `SyntaxKind.StringLiteralExpression` requires `using Microsoft.CodeAnalysis.CSharp;`. The file currently only has `using Microsoft.CodeAnalysis;` and `using Microsoft.CodeAnalysis.CSharp.Syntax;`. Add:

```csharp
using Microsoft.CodeAnalysis.CSharp;
```

alongside the existing usings at the top of the file.

- [ ] **Step 5: Run tests to verify they pass (compile check only — full pass depends on Task 4)**

Run: `dotnet build src/EfSchemaVisualizer.Core/EfSchemaVisualizer.Core.csproj`
Expected: Builds cleanly with no errors.

- [ ] **Step 6: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Parsing/FluentSyntaxHelpers.cs tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs
git commit -m "Read Property string-overload and single-return block-bodied lambdas"
```

---

### Task 3: Diagnostics channel — `ParseResult<T>` wrapper + diagnostic emission

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs`
- Modify: `tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs`
- Modify: `tests/EfSchemaVisualizer.Core.Tests/RoundTripTests.cs`

**Interfaces:**
- Consumes: `Diagnostic`, `ParseResult<T>` (already exist in `src/EfSchemaVisualizer.Core/Parsing/Diagnostic.cs` and `ParseResult.cs`); `FluentSyntaxHelpers.GetPropertyNameFor`/`GetConfiguredEntityName`/`FindEntityConfigInvocations`/`FindCallsNamed` from Tasks 1–2.
- Produces: `FluentConfigParser.ParseMaxLengths(string sourceCode) : ParseResult<IReadOnlyList<MaxLengthConfig>>`. Emits `Diagnostic` with `Code == "UnresolvablePropertyName"` when a `HasMaxLength` call's property name can't be resolved, and `Code == "UnreadableMaxLengthArgument"` when the property name resolves but the argument isn't an integer literal.

This task makes all the tests written in Tasks 1–2 (which reference `.Value`/`.Diagnostics`) actually compile and pass, and updates the two pre-existing tests plus `RoundTripTests.cs`.

- [ ] **Step 1: Update the two pre-existing `FluentConfigParserTests` to unwrap `ParseResult`**

In `FluentConfigParserTests.cs`, change:

```csharp
    [Fact]
    public void ParseMaxLengths_ReadsEveryConfiguredProperty_AcrossMultipleEntities()
    {
        var configs = new FluentConfigParser().ParseMaxLengths(Source);

        Assert.Equal(3, configs.Count);
        Assert.Contains(configs, c => c is { EntityName: "Person", PropertyName: "Name", MaxLength: 100 });
        Assert.Contains(configs, c => c is { EntityName: "Person", PropertyName: "Email", MaxLength: 255 });
        Assert.Contains(configs, c => c is { EntityName: "Address", PropertyName: "Line1", MaxLength: 200 });
    }
```

to:

```csharp
    [Fact]
    public void ParseMaxLengths_ReadsEveryConfiguredProperty_AcrossMultipleEntities()
    {
        var result = new FluentConfigParser().ParseMaxLengths(Source);

        Assert.Empty(result.Diagnostics);
        Assert.Equal(3, result.Value.Count);
        Assert.Contains(result.Value, c => c is { EntityName: "Person", PropertyName: "Name", MaxLength: 100 });
        Assert.Contains(result.Value, c => c is { EntityName: "Person", PropertyName: "Email", MaxLength: 255 });
        Assert.Contains(result.Value, c => c is { EntityName: "Address", PropertyName: "Line1", MaxLength: 200 });
    }
```

And change:

```csharp
    [Fact]
    public void ParseMaxLengths_NestedEntityConfig_DoesNotAttributeNestedCallsToOuterEntity()
    {
        var configs = new FluentConfigParser().ParseMaxLengths(SourceWithNestedEntityConfig);

        Assert.Equal(2, configs.Count);
        Assert.Contains(configs, c => c is { EntityName: "Person", PropertyName: "Name", MaxLength: 100 });
        Assert.Contains(configs, c => c is { EntityName: "Address", PropertyName: "Line1", MaxLength: 200 });
        Assert.DoesNotContain(configs, c => c.EntityName == "Person" && c.PropertyName == "Line1");
    }
```

to:

```csharp
    [Fact]
    public void ParseMaxLengths_NestedEntityConfig_DoesNotAttributeNestedCallsToOuterEntity()
    {
        var result = new FluentConfigParser().ParseMaxLengths(SourceWithNestedEntityConfig);

        Assert.Empty(result.Diagnostics);
        Assert.Equal(2, result.Value.Count);
        Assert.Contains(result.Value, c => c is { EntityName: "Person", PropertyName: "Name", MaxLength: 100 });
        Assert.Contains(result.Value, c => c is { EntityName: "Address", PropertyName: "Line1", MaxLength: 200 });
        Assert.DoesNotContain(result.Value, c => c.EntityName == "Person" && c.PropertyName == "Line1");
    }
```

- [ ] **Step 2: Write the failing diagnostics tests**

Add to `FluentConfigParserTests.cs`:

```csharp
    private const string SourceWithConstArgument = """
        public class AppDbContext : DbContext
        {
            private const int MaxNameLength = 100;

            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.Property(e => e.Name).HasMaxLength(MaxNameLength);
                });
            }
        }
        """;

    [Fact]
    public void ParseMaxLengths_ConstIdentifierArgument_EmitsUnreadableMaxLengthArgumentDiagnostic()
    {
        var result = new FluentConfigParser().ParseMaxLengths(SourceWithConstArgument);

        Assert.Empty(result.Value);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("UnreadableMaxLengthArgument", diagnostic.Code);
        Assert.Equal("Person", diagnostic.EntityName);
        Assert.Equal("Name", diagnostic.PropertyName);
    }

    private const string SourceWithArithmeticArgument = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.Property(e => e.Name).HasMaxLength(50 * 2);
                });
            }
        }
        """;

    [Fact]
    public void ParseMaxLengths_ArithmeticArgument_EmitsUnreadableMaxLengthArgumentDiagnostic()
    {
        var result = new FluentConfigParser().ParseMaxLengths(SourceWithArithmeticArgument);

        Assert.Empty(result.Value);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("UnreadableMaxLengthArgument", diagnostic.Code);
        Assert.Equal("Person", diagnostic.EntityName);
        Assert.Equal("Name", diagnostic.PropertyName);
    }

    private const string SourceWithUnresolvablePropertyLambda = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.Property(e =>
                    {
                        var name = e.Name;
                        return name;
                    }).HasMaxLength(100);
                });
            }
        }
        """;

    [Fact]
    public void ParseMaxLengths_UnresolvablePropertyLambda_EmitsUnresolvablePropertyNameDiagnostic()
    {
        var result = new FluentConfigParser().ParseMaxLengths(SourceWithUnresolvablePropertyLambda);

        Assert.Empty(result.Value);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("UnresolvablePropertyName", diagnostic.Code);
        Assert.Equal("Person", diagnostic.EntityName);
        Assert.Null(diagnostic.PropertyName);
    }
```

- [ ] **Step 3: Run tests to verify they fail to compile**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests/EfSchemaVisualizer.Core.Tests.csproj`
Expected: Build errors — `ParseMaxLengths` still returns `IReadOnlyList<MaxLengthConfig>` with no `.Value`/`.Diagnostics`.

- [ ] **Step 4: Rewrite `FluentConfigParser.ParseMaxLengths`**

Replace the whole file `FluentConfigParser.cs`:

```csharp
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EfSchemaVisualizer.Core.Parsing;

public sealed class FluentConfigParser
{
    public ParseResult<IReadOnlyList<MaxLengthConfig>> ParseMaxLengths(string sourceCode)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var results = new List<MaxLengthConfig>();
        var diagnostics = new List<Diagnostic>();

        // Distinct entity names configured anywhere in the file.
        var entityNames = root.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Select(FluentSyntaxHelpers.GetConfiguredEntityName)
            .Where(name => name is not null)
            .Distinct()!;

        foreach (var entityName in entityNames)
        {
            foreach (var entityInvocation in FluentSyntaxHelpers.FindEntityConfigInvocations(root, entityName!))
            {
                foreach (var maxLengthCall in FluentSyntaxHelpers.FindCallsNamed(entityInvocation, "HasMaxLength"))
                {
                    var propertyName = FluentSyntaxHelpers.GetPropertyNameFor(maxLengthCall);
                    var arg = maxLengthCall.ArgumentList.Arguments.FirstOrDefault();

                    if (arg is null)
                    {
                        continue;
                    }

                    if (propertyName is null)
                    {
                        diagnostics.Add(new Diagnostic(
                            "UnresolvablePropertyName",
                            "Could not determine which property this HasMaxLength call configures.",
                            entityName,
                            PropertyName: null,
                            maxLengthCall.Span));
                        continue;
                    }

                    if (int.TryParse(arg.Expression.ToString(), out var maxLength))
                    {
                        results.Add(new MaxLengthConfig(entityName!, propertyName, maxLength));
                    }
                    else
                    {
                        diagnostics.Add(new Diagnostic(
                            "UnreadableMaxLengthArgument",
                            "HasMaxLength argument is not an integer literal and could not be read.",
                            entityName,
                            propertyName,
                            arg.Span));
                    }
                }
            }
        }

        return new ParseResult<IReadOnlyList<MaxLengthConfig>>(results, diagnostics);
    }
}
```

- [ ] **Step 5: Update `RoundTripTests.cs` call sites**

In `tests/EfSchemaVisualizer.Core.Tests/RoundTripTests.cs`, there are three call sites. Change:

```csharp
        var configs = new FluentConfigParser().ParseMaxLengths(ContextSource);
        var merged = ModelMerger.ApplyMaxLengths(baseEntity, configs);
```

(in `Parse_Merge_NoEdit_RegeneratesConfigIdenticalToOriginal`) to:

```csharp
        var configs = new FluentConfigParser().ParseMaxLengths(ContextSource).Value;
        var merged = ModelMerger.ApplyMaxLengths(baseEntity, configs);
```

Change (in `Parse_Edit_Regenerate_ChangesOnlyTheEditedProperty`):

```csharp
        var configs = new FluentConfigParser().ParseMaxLengths(ContextSource);
        var addressLine1 = configs.Single(c => c is { EntityName: "Address", PropertyName: "Line1" });
```

to:

```csharp
        var configs = new FluentConfigParser().ParseMaxLengths(ContextSource).Value;
        var addressLine1 = configs.Single(c => c is { EntityName: "Address", PropertyName: "Line1" });
```

And change:

```csharp
        var configsAfter = new FluentConfigParser().ParseMaxLengths(regenerated);
        var addressLine1After = configsAfter.Single(c => c is { EntityName: "Address", PropertyName: "Line1" });
```

to:

```csharp
        var configsAfter = new FluentConfigParser().ParseMaxLengths(regenerated).Value;
        var addressLine1After = configsAfter.Single(c => c is { EntityName: "Address", PropertyName: "Line1" });
```

- [ ] **Step 6: Run full test suite to verify everything passes**

Run: `dotnet test`
Expected: All tests PASS, including every test added in Tasks 1, 2, and 3.

- [ ] **Step 7: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs tests/EfSchemaVisualizer.Core.Tests/RoundTripTests.cs
git commit -m "Wire diagnostics channel into FluentConfigParser for unresolvable property names and non-literal HasMaxLength arguments"
```

---

### Task 4: Update the backlog

**Files:**
- Modify: `docs/backlog.md`

- [ ] **Step 1: Check off the four completed Priority 0 items**

In `docs/backlog.md`, change these from `- [ ]` to `- [x]`:
- "Hardcoded `modelBuilder` receiver name."
- "`Property` string-overload and block-bodied lambdas not read."
- "Non-literal `HasMaxLength` arguments dropped silently."

For the diagnostics-channel item, replace the existing partial-completion note with a fully-checked entry:

```markdown
- [x] **`[found]` Parser silently drops config it doesn't understand — add a diagnostics / "unsupported" channel.**
      Today anything the parser can't model is discarded with no signal. A user
      would see an incomplete model and not know it. Introduce an
      `UnsupportedConfig` / diagnostics list on the parse result so the UI can
      warn "N fluent calls could not be read" rather than silently hiding them.
      This is the single most important trust fix.
      **Update:** `Diagnostic`/`ParseResult<T>` now populated by both
      `EntityClassParser` and `FluentConfigParser` (see
      `2026-07-08-fluent-config-parser-hardening-design.md`).
```

- [ ] **Step 2: Run the full test suite once more as a final sanity check**

Run: `dotnet test`
Expected: All tests PASS.

- [ ] **Step 3: Commit**

```bash
git add docs/backlog.md
git commit -m "Check off remaining P0 backlog items completed by the FluentConfigParser hardening pass"
```
