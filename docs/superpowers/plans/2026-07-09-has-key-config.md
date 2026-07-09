# `HasKey` Fluent Config Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add full round-trip support (model, parse, merge, rewrite) for EF Core's `HasKey(...)` fluent config, covering both single-column and composite keys.

**Architecture:** Follows the existing `HasMaxLength`/`IsRequired` parse → merge → rewrite pattern. `EntityModel` gains an ordered `KeyPropertyNames` list (entity-level, not per-property). `FluentConfigParser.ParseKeys` reads four call shapes into a new `KeyConfig` DTO. `ModelMerger.ApplyKeys` folds it into `EntityModel`. `OnModelCreatingRewriter.SetKey`/`RemoveKey` write it back, always emitting a canonical lambda form.

**Tech Stack:** C# / .NET 10, Roslyn (`Microsoft.CodeAnalysis.CSharp`), xUnit.

## Global Constraints

- Spec: `docs/superpowers/specs/2026-07-09-has-key-config-design.md` — read it before starting; this plan implements it verbatim.
- `TargetFramework` is `net10.0`, `Nullable` is enabled, on both `src/EfSchemaVisualizer.Core` and `tests/EfSchemaVisualizer.Core.Tests` — match existing nullable-annotation style in the files you touch.
- Never silently drop config the parser can't read — unreadable shapes must emit a `Diagnostic` (Priority 0 project rule; see spec's "New diagnostic code" section).
- New public methods are separate composed calls, not orchestrated into existing ones (e.g. `ApplyKeys` is called alongside `ApplyMaxLengths`/`ApplyIsRequired` by whatever composes them, not folded into either).
- Run tests with `dotnet test --filter "FullyQualifiedName~<TestClassName>"` from the repo root (`/root/RiderProjects/EfSchemaVisualizer`); this project has no solution-wide script beyond `dotnet test`.
- Commit after each task, not each step.

---

### Task 1: `EntityModel.KeyPropertyNames`

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/Model/EntityModel.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Model/PropertyModelTests.cs`

**Interfaces:**
- Produces: `EntityModel.KeyPropertyNames` — `IReadOnlyList<string>`, defaults to `[]` so all two-arg call sites (`EntityClassParser`, existing tests) keep compiling unchanged.

- [ ] **Step 1: Write the failing test**

Add to `tests/EfSchemaVisualizer.Core.Tests/Model/PropertyModelTests.cs`, inside the `PropertyModelTests` class, after `EntityModel_ExposesNameAndProperties`:

```csharp
    [Fact]
    public void EntityModel_KeyPropertyNames_DefaultsToEmpty()
    {
        var entity = new EntityModel("Person", new List<PropertyModel>());

        Assert.Empty(entity.KeyPropertyNames);
    }

    [Fact]
    public void EntityModel_WithKeyPropertyNames_ProducesUpdatedCopy_LeavingOriginalUnchanged()
    {
        var original = new EntityModel("Person", new List<PropertyModel>());

        var updated = original with { KeyPropertyNames = new List<string> { "Id" } };

        Assert.Empty(original.KeyPropertyNames);
        Assert.Equal(new[] { "Id" }, updated.KeyPropertyNames);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~PropertyModelTests"`
Expected: build error — `EntityModel` has no member `KeyPropertyNames`.

- [ ] **Step 3: Implement**

Read the current file first (`Read src/EfSchemaVisualizer.Core/Model/EntityModel.cs`), then replace its contents with:

```csharp
namespace EfSchemaVisualizer.Core.Model;

public sealed record EntityModel(
    string Name,
    IReadOnlyList<PropertyModel> Properties,
    IReadOnlyList<string>? KeyPropertyNames = null)
{
    public IReadOnlyList<string> KeyPropertyNames { get; init; } = KeyPropertyNames ?? new List<string>();
}
```

The primary constructor parameter is nullable with a `null` default so existing two-argument call sites keep compiling unchanged. The explicit `init` property shadows the auto-generated positional property, coalescing `null` to an empty list and exposing a non-nullable `IReadOnlyList<string>` to callers. `with { KeyPropertyNames = ... }` still works normally since it targets this `init` property directly. This pattern (nullable positional parameter + shadowing non-nullable `init` property with a coalescing initializer) was verified to compile and behave correctly under `net10.0` before writing this step.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~PropertyModelTests"`
Expected: PASS, all tests in the file (including the two new ones and the pre-existing `EntityModel_ExposesNameAndProperties`) green.

- [ ] **Step 5: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Model/EntityModel.cs tests/EfSchemaVisualizer.Core.Tests/Model/PropertyModelTests.cs
git commit -m "Add KeyPropertyNames to EntityModel"
```

---

### Task 2: `KeyConfig` DTO and `FluentConfigParser.ParseKeys`

**Files:**
- Create: `src/EfSchemaVisualizer.Core/Parsing/KeyConfig.cs`
- Modify: `src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs`

**Interfaces:**
- Consumes: `FluentSyntaxHelpers.FindEntityConfigInvocations(CompilationUnitSyntax, string)`, `FluentSyntaxHelpers.FindCallsNamed(SyntaxNode, string)`, `FluentSyntaxHelpers.GetConfiguredEntityName(InvocationExpressionSyntax)` (all `internal static`, same namespace) — no changes needed to any of them.
- Produces: `KeyConfig(string EntityName, IReadOnlyList<string> PropertyNames)`; `FluentConfigParser.ParseKeys(string sourceCode) : ParseResult<IReadOnlyList<KeyConfig>>`; new diagnostic code `"UnreadableHasKeyArgument"`.

- [ ] **Step 1: Write the failing tests**

Add to `tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs`, inside the `FluentConfigParserTests` class, after the last existing test (`ParseIsRequired_IsRequiredFollowedByHasMaxLength_StillResolvesPropertyName`):

```csharp
    private const string SourceWithSingleAndCompositeKeys = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.HasKey(e => e.Id);
                });

                modelBuilder.Entity<OrderLine>(entity =>
                {
                    entity.HasKey(e => new { e.OrderId, e.LineNumber });
                });
            }
        }
        """;

    [Fact]
    public void ParseKeys_ReadsSingleAndCompositeLambdaKeys_AcrossMultipleEntities()
    {
        var result = new FluentConfigParser().ParseKeys(SourceWithSingleAndCompositeKeys);

        Assert.Empty(result.Diagnostics);
        Assert.Equal(2, result.Value.Count);
        Assert.Contains(result.Value, c => c.EntityName == "Person" && c.PropertyNames.SequenceEqual(new[] { "Id" }));
        Assert.Contains(result.Value, c => c.EntityName == "OrderLine" && c.PropertyNames.SequenceEqual(new[] { "OrderId", "LineNumber" }));
    }

    private const string SourceWithStringKey = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.HasKey("Id");
                });
            }
        }
        """;

    [Fact]
    public void ParseKeys_StringOverload_IsRead()
    {
        var result = new FluentConfigParser().ParseKeys(SourceWithStringKey);

        Assert.Empty(result.Diagnostics);
        Assert.Contains(result.Value, c => c.EntityName == "Person" && c.PropertyNames.SequenceEqual(new[] { "Id" }));
    }

    private const string SourceWithStringArrayKey = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<OrderLine>(entity =>
                {
                    entity.HasKey("OrderId", "LineNumber");
                });
            }
        }
        """;

    [Fact]
    public void ParseKeys_StringParamsOverload_IsReadInOrder()
    {
        var result = new FluentConfigParser().ParseKeys(SourceWithStringArrayKey);

        Assert.Empty(result.Diagnostics);
        Assert.Contains(result.Value, c => c.EntityName == "OrderLine" && c.PropertyNames.SequenceEqual(new[] { "OrderId", "LineNumber" }));
    }

    private const string SourceWithExplicitNameAnonymousMember = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.HasKey(e => new { Key = e.Id });
                });
            }
        }
        """;

    [Fact]
    public void ParseKeys_ExplicitNameAnonymousMember_EmitsUnreadableHasKeyArgumentDiagnostic()
    {
        var result = new FluentConfigParser().ParseKeys(SourceWithExplicitNameAnonymousMember);

        Assert.Empty(result.Value);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("UnreadableHasKeyArgument", diagnostic.Code);
        Assert.Equal("Person", diagnostic.EntityName);
        Assert.Null(diagnostic.PropertyName);
    }

    private const string SourceWithMethodCallKeyArgument = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.HasKey(GetKeySelector());
                });
            }
        }
        """;

    [Fact]
    public void ParseKeys_MethodCallArgument_EmitsUnreadableHasKeyArgumentDiagnostic()
    {
        var result = new FluentConfigParser().ParseKeys(SourceWithMethodCallKeyArgument);

        Assert.Empty(result.Value);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("UnreadableHasKeyArgument", diagnostic.Code);
        Assert.Equal("Person", diagnostic.EntityName);
        Assert.Null(diagnostic.PropertyName);
    }

    private const string SourceWithNoHasKeyCall = """
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

    [Fact]
    public void ParseKeys_NoCallPresent_ReturnsEmpty()
    {
        var result = new FluentConfigParser().ParseKeys(SourceWithNoHasKeyCall);

        Assert.Empty(result.Diagnostics);
        Assert.Empty(result.Value);
    }
```

This file already has `using System.Linq;` at the top, so `.SequenceEqual` in the test bodies above resolves without a new `using`.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~FluentConfigParserTests"`
Expected: build error — `FluentConfigParser` has no method `ParseKeys`.

- [ ] **Step 3: Create the `KeyConfig` DTO**

Create `src/EfSchemaVisualizer.Core/Parsing/KeyConfig.cs`:

```csharp
using System.Collections.Generic;

namespace EfSchemaVisualizer.Core.Parsing;

public sealed record KeyConfig(string EntityName, IReadOnlyList<string> PropertyNames);
```

- [ ] **Step 4: Implement `ParseKeys`**

Read `src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs`, then add the following as a new public method on `FluentConfigParser`, placed after `ParseIsRequired` and before the class's closing brace, plus two new private helpers below it:

```csharp
    public ParseResult<IReadOnlyList<KeyConfig>> ParseKeys(string sourceCode)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var results = new List<KeyConfig>();
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
                foreach (var hasKeyCall in FluentSyntaxHelpers.FindCallsNamed(entityInvocation, "HasKey"))
                {
                    var propertyNames = TryReadKeyPropertyNames(hasKeyCall);

                    if (propertyNames is null)
                    {
                        diagnostics.Add(new Diagnostic(
                            "UnreadableHasKeyArgument",
                            "HasKey argument(s) could not be read as property name(s).",
                            entityName,
                            PropertyName: null,
                            hasKeyCall.Span));
                        continue;
                    }

                    results.Add(new KeyConfig(entityName!, propertyNames));
                }
            }
        }

        return new ParseResult<IReadOnlyList<KeyConfig>>(results, diagnostics);
    }

    private static IReadOnlyList<string>? TryReadKeyPropertyNames(InvocationExpressionSyntax hasKeyCall)
    {
        var arguments = hasKeyCall.ArgumentList.Arguments;

        if (arguments.Count == 0)
        {
            return null;
        }

        if (arguments.All(a => a.Expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression)))
        {
            return arguments
                .Select(a => ((LiteralExpressionSyntax)a.Expression).Token.ValueText)
                .ToList();
        }

        if (arguments.Count == 1 && arguments[0].Expression is SimpleLambdaExpressionSyntax { ExpressionBody: { } body })
        {
            return TryReadKeyPropertyNamesFromLambdaBody(body);
        }

        return null;
    }

    private static IReadOnlyList<string>? TryReadKeyPropertyNamesFromLambdaBody(ExpressionSyntax body)
    {
        if (body is MemberAccessExpressionSyntax { Name.Identifier.Text: var singleName })
        {
            return new List<string> { singleName };
        }

        if (body is AnonymousObjectCreationExpressionSyntax anonymousObject)
        {
            var names = new List<string>();

            foreach (var initializer in anonymousObject.Initializers)
            {
                if (initializer.NameEquals is not null
                    || initializer.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: var name })
                {
                    return null;
                }

                names.Add(name);
            }

            return names;
        }

        return null;
    }
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~FluentConfigParserTests"`
Expected: PASS, all tests in the file green (existing `HasMaxLength`/`IsRequired` tests plus the new `ParseKeys` tests).

- [ ] **Step 6: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Parsing/KeyConfig.cs src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs
git commit -m "Add FluentConfigParser.ParseKeys for HasKey fluent config"
```

---

### Task 3: `ModelMerger.ApplyKeys`

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/Parsing/ModelMerger.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Parsing/ModelMergerTests.cs`

**Interfaces:**
- Consumes: `EntityModel.KeyPropertyNames` (Task 1), `KeyConfig` (Task 2).
- Produces: `ModelMerger.ApplyKeys(EntityModel entity, IReadOnlyList<KeyConfig> configs) : EntityModel`.

- [ ] **Step 1: Write the failing test**

Add to `tests/EfSchemaVisualizer.Core.Tests/Parsing/ModelMergerTests.cs`, inside the `ModelMergerTests` class, after `ApplyIsRequired_SetsIsRequiredOverrideOnMatchingProperty_LeavesOthersUntouched`:

```csharp
    [Fact]
    public void ApplyKeys_SetsKeyPropertyNamesOnMatchingEntity_LeavesOthersUntouched()
    {
        var entity = new EntityModel("Person", new List<PropertyModel>
        {
            new("Id", "int", IsNullable: false, MaxLength: null),
            new("Name", "string", IsNullable: true, MaxLength: null),
        });

        var configs = new List<KeyConfig>
        {
            new("Person", new List<string> { "Id" }),
            new("Address", new List<string> { "Id" }), // different entity, must not affect Person
        };

        var merged = ModelMerger.ApplyKeys(entity, configs);

        Assert.Equal(new[] { "Id" }, merged.KeyPropertyNames);
        // Properties themselves are untouched by the merge.
        Assert.Equal(2, merged.Properties.Count);
    }

    [Fact]
    public void ApplyKeys_NoMatchingConfig_LeavesKeyPropertyNamesEmpty()
    {
        var entity = new EntityModel("Person", new List<PropertyModel>
        {
            new("Id", "int", IsNullable: false, MaxLength: null),
        });

        var configs = new List<KeyConfig>
        {
            new("Address", new List<string> { "Id" }),
        };

        var merged = ModelMerger.ApplyKeys(entity, configs);

        Assert.Empty(merged.KeyPropertyNames);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~ModelMergerTests"`
Expected: build error — `ModelMerger` has no method `ApplyKeys`.

- [ ] **Step 3: Implement**

Read `src/EfSchemaVisualizer.Core/Parsing/ModelMerger.cs`, then add this new public static method after `ApplyIsRequired`, before the class's closing brace:

```csharp
    public static EntityModel ApplyKeys(EntityModel entity, IReadOnlyList<KeyConfig> configs)
    {
        var config = configs.FirstOrDefault(c => c.EntityName == entity.Name);

        return config is null ? entity : entity with { KeyPropertyNames = config.PropertyNames };
    }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~ModelMergerTests"`
Expected: PASS, all tests in the file green.

- [ ] **Step 5: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Parsing/ModelMerger.cs tests/EfSchemaVisualizer.Core.Tests/Parsing/ModelMergerTests.cs
git commit -m "Add ModelMerger.ApplyKeys for HasKey fluent config"
```

---

### Task 4: `OnModelCreatingRewriter.SetKey`

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs`

**Interfaces:**
- Consumes: `FluentSyntaxHelpers.FindEntityConfigInvocations`, `FluentSyntaxHelpers.FindCallsNamed`, `FluentConfigParser.ParseKeys` (Task 2, used in tests to verify round-trip), `KeyConfig` (Task 2).
- Produces: `OnModelCreatingRewriter.SetKey(string sourceCode, string entityName, IReadOnlyList<string> propertyNames) : string`. Always emits `e => e.X` for a single property or `e => new { e.A, e.B }` for two or more, with lambda parameter always named `e` (this is a canonical rewrite, not a style-preserving mutation — unlike `RewriteMaxLength`'s byte-identical mutate path, `SetKey` always calls `NormalizeWhitespace()`).

- [ ] **Step 1: Write the failing tests**

Add to `tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs`, inside the `OnModelCreatingRewriterTests` class, after the last `IsRequired`-related test (`RemoveIsRequired_EntityHasNoConfigAtAll_ReturnsSourceUnchanged`) and before the `RemoveMaxLength_...` tests that follow it (insert as a new block right after that test's closing brace):

```csharp
    private const string SourceWithSingleKey = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.HasKey(e => e.Id);
                    entity.Property(e => e.Name).HasMaxLength(100);
                });
            }
        }
        """;

    [Fact]
    public void SetKey_ExistingSingleKey_MutatesToComposite()
    {
        var result = new OnModelCreatingRewriter()
            .SetKey(SourceWithSingleKey, entityName: "Person", propertyNames: new List<string> { "TenantId", "Id" });

        Assert.Contains("entity.HasKey(e => new { e.TenantId, e.Id })", result);
        Assert.DoesNotContain("entity.HasKey(e => e.Id)", result);
        Assert.Contains("entity.Property(e => e.Name).HasMaxLength(100)", result);
    }

    [Fact]
    public void SetKey_ExistingSingleKey_MutatesToDifferentSingleProperty()
    {
        var result = new OnModelCreatingRewriter()
            .SetKey(SourceWithSingleKey, entityName: "Person", propertyNames: new List<string> { "Guid" });

        Assert.Contains("entity.HasKey(e => e.Guid)", result);
        Assert.DoesNotContain("entity.HasKey(e => e.Id)", result);
    }

    private const string SourceWithEntityConfiguredNoKey = """
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

    [Fact]
    public void SetKey_EntityConfiguredWithoutHasKey_InsertsStatementAtEndOfBlock()
    {
        var result = new OnModelCreatingRewriter()
            .SetKey(SourceWithEntityConfiguredNoKey, entityName: "Person", propertyNames: new List<string> { "Id" });

        Assert.Contains("entity.HasKey(e => e.Id)", result);
        Assert.Contains("entity.Property(e => e.Name).HasMaxLength(100)", result);

        var configs = new FluentConfigParser().ParseKeys(result).Value;
        Assert.Contains(configs, c => c.EntityName == "Person" && c.PropertyNames.SequenceEqual(new[] { "Id" }));
    }

    [Fact]
    public void SetKey_UnknownEntity_InsertsNewEntityBlock()
    {
        var result = new OnModelCreatingRewriter()
            .SetKey(SourceWithSingleKey, entityName: "Vehicle", propertyNames: new List<string> { "Vin" });

        Assert.Contains("modelBuilder.Entity<Vehicle>", result);
        Assert.Contains("entity.HasKey(e => e.Vin)", result);

        var configs = new FluentConfigParser().ParseKeys(result).Value;
        Assert.Contains(configs, c => c.EntityName == "Vehicle" && c.PropertyNames.SequenceEqual(new[] { "Vin" }));
        Assert.Contains(configs, c => c.EntityName == "Person" && c.PropertyNames.SequenceEqual(new[] { "Id" }));
    }
```

`.SequenceEqual` needs `System.Linq`, already imported at the top of this test file (used by other tests in the same class). Confirm with a quick check before running:

Run: `grep -n "^using" tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs`
Expected: includes `using System.Linq;` (if not present, add it as the first `using` line).

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~OnModelCreatingRewriterTests"`
Expected: build error — `OnModelCreatingRewriter` has no method `SetKey`.

- [ ] **Step 3: Implement**

Read `src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs`, then add `SetKey` as a new public method placed after `RemoveIsRequired` and before `RenameEntityReferences`, plus its private helpers immediately after it:

```csharp
    public string SetKey(string sourceCode, string entityName, IReadOnlyList<string> propertyNames)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var entityInvocations = FluentSyntaxHelpers.FindEntityConfigInvocations(root, entityName).ToList();

        var existingHasKeyCall = entityInvocations
            .SelectMany(entityInvocation => FluentSyntaxHelpers.FindCallsNamed(entityInvocation, "HasKey"))
            .FirstOrDefault();

        if (existingHasKeyCall is not null)
        {
            return MutateExistingKey(root, existingHasKeyCall, propertyNames);
        }

        var existingEntityInvocation = entityInvocations.FirstOrDefault();

        if (existingEntityInvocation is not null)
        {
            return InsertKeyStatement(root, existingEntityInvocation, propertyNames);
        }

        return InsertKeyEntityBlock(root, entityName, propertyNames);
    }

    private static string MutateExistingKey(CompilationUnitSyntax root, InvocationExpressionSyntax targetCall, IReadOnlyList<string> propertyNames)
    {
        var newCall = targetCall.WithArgumentList(BuildHasKeyArgumentList(propertyNames));

        var newRoot = root.ReplaceNode(targetCall, newCall);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    private static string InsertKeyStatement(CompilationUnitSyntax root, InvocationExpressionSyntax entityInvocation, IReadOnlyList<string> propertyNames)
    {
        var lambda = (SimpleLambdaExpressionSyntax)entityInvocation.ArgumentList.Arguments.Single().Expression;
        var block = lambda.Block!;
        var blockReceiverName = lambda.Parameter.Identifier.Text;

        var newStatement = BuildHasKeyStatement(blockReceiverName, propertyNames);
        var newBlock = block.AddStatements(newStatement);

        var newRoot = root.ReplaceNode(block, newBlock);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    private static string InsertKeyEntityBlock(CompilationUnitSyntax root, string entityName, IReadOnlyList<string> propertyNames)
    {
        var method = FindOnModelCreatingMethod(root);

        var methodBody = method.Body
            ?? throw new InvalidOperationException("OnModelCreating has no method body.");

        var modelBuilderParamName = method.ParameterList.Parameters.Single().Identifier.Text;

        var keyStatement = BuildHasKeyStatement("entity", propertyNames);
        var entityBlockStatement = BuildEntityInvocationStatement(modelBuilderParamName, entityName, SyntaxFactory.Block(keyStatement));

        var newMethodBody = methodBody.AddStatements(entityBlockStatement);
        var newRoot = root.ReplaceNode(methodBody, newMethodBody);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    private static ExpressionStatementSyntax BuildHasKeyStatement(string blockReceiverName, IReadOnlyList<string> propertyNames)
    {
        return SyntaxFactory.ExpressionStatement(
            SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.IdentifierName(blockReceiverName),
                    SyntaxFactory.IdentifierName("HasKey")),
                BuildHasKeyArgumentList(propertyNames)));
    }

    private static ArgumentListSyntax BuildHasKeyArgumentList(IReadOnlyList<string> propertyNames)
    {
        const string lambdaParam = "e";

        ExpressionSyntax body = propertyNames.Count == 1
            ? SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                SyntaxFactory.IdentifierName(lambdaParam),
                SyntaxFactory.IdentifierName(propertyNames[0]))
            : SyntaxFactory.AnonymousObjectCreationExpression(
                SyntaxFactory.SeparatedList(
                    propertyNames.Select(name => SyntaxFactory.AnonymousObjectMemberDeclarator(
                        SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            SyntaxFactory.IdentifierName(lambdaParam),
                            SyntaxFactory.IdentifierName(name))))));

        return SyntaxFactory.ArgumentList(
            SyntaxFactory.SingletonSeparatedList(
                SyntaxFactory.Argument(
                    SyntaxFactory.SimpleLambdaExpression(
                        SyntaxFactory.Parameter(SyntaxFactory.Identifier(lambdaParam)),
                        body))));
    }
```

`propertyNames.Count == 1` requires `IReadOnlyList<string>`, which already exposes `.Count` and index access — no extra `using` needed. This method must be placed so it (and the other new private helpers) can see `FindOnModelCreatingMethod` and `BuildEntityInvocationStatement`, which are private methods already defined later in the same class — that's fine, C# doesn't require declaration order within a class.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~OnModelCreatingRewriterTests"`
Expected: PASS, all tests in the file green.

- [ ] **Step 5: Commit**

```bash
git add src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs
git commit -m "Add OnModelCreatingRewriter.SetKey for HasKey fluent config"
```

---

### Task 5: `OnModelCreatingRewriter.RemoveKey`

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs`

**Interfaces:**
- Consumes: same `FluentSyntaxHelpers` methods as Task 4.
- Produces: `OnModelCreatingRewriter.RemoveKey(string sourceCode, string entityName) : string`. Removes the whole `entity.HasKey(...);` statement (no bare receiver to fall back to, unlike `RemoveMaxLength`/`RemoveIsRequired`). No-op (returns `sourceCode` unchanged) if no `HasKey` call exists for the entity.

- [ ] **Step 1: Write the failing tests**

Add to `tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs`, inside the `OnModelCreatingRewriterTests` class, directly after the `SetKey_UnknownEntity_InsertsNewEntityBlock` test added in Task 4:

```csharp
    [Fact]
    public void RemoveKey_ExistingCall_RemovesHasKeyStatementEntirely()
    {
        var result = new OnModelCreatingRewriter()
            .RemoveKey(SourceWithSingleKey, entityName: "Person");

        Assert.DoesNotContain("HasKey", result);
        Assert.Contains("entity.Property(e => e.Name).HasMaxLength(100)", result);
    }

    [Fact]
    public void RemoveKey_NoMatchingCall_ReturnsSourceUnchanged()
    {
        var result = new OnModelCreatingRewriter()
            .RemoveKey(SourceWithEntityConfiguredNoKey, entityName: "Person");

        Assert.Equal(SourceWithEntityConfiguredNoKey, result);
    }

    [Fact]
    public void RemoveKey_EntityHasNoConfigAtAll_ReturnsSourceUnchanged()
    {
        var result = new OnModelCreatingRewriter()
            .RemoveKey(SourceWithSingleKey, entityName: "Vehicle");

        Assert.Equal(SourceWithSingleKey, result);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~OnModelCreatingRewriterTests"`
Expected: build error — `OnModelCreatingRewriter` has no method `RemoveKey`.

- [ ] **Step 3: Implement**

Read `src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs`, then add `RemoveKey` as a new public method placed directly after `SetKey`'s block of helpers added in Task 4 (i.e., after `BuildHasKeyArgumentList`, before `RenameEntityReferences`):

```csharp
    public string RemoveKey(string sourceCode, string entityName)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var entityInvocations = FluentSyntaxHelpers.FindEntityConfigInvocations(root, entityName).ToList();

        var existingHasKeyCall = entityInvocations
            .SelectMany(entityInvocation => FluentSyntaxHelpers.FindCallsNamed(entityInvocation, "HasKey"))
            .FirstOrDefault();

        if (existingHasKeyCall is null || existingHasKeyCall.Parent is not ExpressionStatementSyntax statement)
        {
            return sourceCode;
        }

        var newRoot = root.RemoveNode(statement, SyntaxRemoveOptions.KeepNoTrivia)!;
        return newRoot.NormalizeWhitespace().ToFullString();
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~OnModelCreatingRewriterTests"`
Expected: PASS, all tests in the file green.

- [ ] **Step 5: Run the full test suite**

Run: `dotnet test`
Expected: PASS, all tests across the whole solution green (this confirms nothing in Tasks 1-5 broke `EntityClassParserTests`, `RoundTripTests`, or `EntityClassRewriterTests`, none of which this plan touches directly but all of which construct `EntityModel`/use the parser pipeline).

- [ ] **Step 6: Commit**

```bash
git add src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs
git commit -m "Add OnModelCreatingRewriter.RemoveKey for HasKey fluent config"
```

---

## Task 6: Update the backlog

**Files:**
- Modify: `docs/backlog.md`

- [ ] **Step 1: Check off the Keys item**

In `docs/backlog.md`, find this line in the Priority 2 section:

```
- [ ] **`[spec]` Keys** — `HasKey`, including composite keys.
```

Replace it with:

```
- [x] **`[spec]` Keys** — `HasKey`, including composite keys.
      **Update:** `FluentConfigParser.ParseKeys` reads `HasKey(e => e.Id)`,
      `HasKey(e => new { e.A, e.B })`, `HasKey("Id")`, and
      `HasKey("A", "B")` into `KeyConfig`; `ModelMerger.ApplyKeys` folds
      that into `EntityModel.KeyPropertyNames` (entity-level, not a
      per-property field, since composite key order matters);
      `OnModelCreatingRewriter.SetKey`/`RemoveKey` write it back, always
      emitting the canonical lambda form (see
      `2026-07-09-has-key-config-design.md`).
```

- [ ] **Step 2: Commit**

```bash
git add docs/backlog.md
git commit -m "Check off HasKey backlog item"
```

---

## Self-Review Notes

- **Spec coverage:** all four parse shapes (lambda single, lambda composite, string single, string params array), the `UnreadableHasKeyArgument` diagnostic, `EntityModel.KeyPropertyNames`, `ModelMerger.ApplyKeys`, and `OnModelCreatingRewriter.SetKey`/`RemoveKey`'s three-case dispatch are each covered by a task and at least one test. The spec's "Out of scope" items (no `[Key]` attribute support, no duplicate/existence validation, no UI) are correctly not implemented.
- **Placeholder scan:** no TBD/TODO; every step shows complete code.
- **Type consistency:** `KeyConfig.PropertyNames`, `EntityModel.KeyPropertyNames`, `SetKey`'s `propertyNames` parameter, and `ApplyKeys`'s `configs` parameter are all `IReadOnlyList<string>` end-to-end, matching the spec.
