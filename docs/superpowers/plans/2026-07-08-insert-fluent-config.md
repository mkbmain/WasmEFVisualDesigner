# Insert New Fluent Config Where None Exists — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `OnModelCreatingRewriter.RewriteMaxLength` handle the three cases where a property currently has no (or partial) `HasMaxLength` configuration, instead of throwing — by appending, inserting, or synthesizing the missing syntax.

**Architecture:** `RewriteMaxLength` gains a four-way dispatch, checked in order of "how much syntax is missing": mutate an existing `HasMaxLength` literal (unchanged), append `.HasMaxLength(N)` onto an existing `Property()` call, insert a new `Property().HasMaxLength()` statement into an existing `Entity<T>` block, or synthesize a whole new `Entity<T>` block appended to `OnModelCreating`. Shared node-building logic (`BuildPropertyStatement`, `BuildMaxLengthCall`) is factored out so the three insertion paths don't duplicate `SyntaxFactory` calls. Two `FluentSyntaxHelpers` additions support this: `GetPropertyNameForPropertyCall` (extracted from the existing `GetPropertyNameFor`, usable on a bare `Property()` call without a chained `HasMaxLength`) and `GetPropertyLambdaParameterName` (reads a block's existing `Property(x => ...)` naming convention so new statements match).

**Tech Stack:** C# / .NET 10, Roslyn (`Microsoft.CodeAnalysis.CSharp`), xUnit.

## Global Constraints

- Design source of truth: `docs/superpowers/specs/2026-07-08-insert-fluent-config-design.md`.
- Insertion paths (cases 2–4) call `NormalizeWhitespace()` on the whole document before returning — this reformats the entire file, not just the touched subtree (explicit trade-off in the spec). Case 1 (pure mutation) is unaffected and must remain byte-identical.
- Tests for insertion paths must assert on **parsed values** via `FluentConfigParser.ParseMaxLengths`, not exact text equality, since whole-file normalization changes formatting. `Assert.Contains(result, "literal substring")` is fine for the specific new/changed call site since that fragment's internal spacing is stable.
- Two existing tests in `tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs` currently assert `Assert.Throws<InvalidOperationException>` for shapes this feature now handles by inserting. They must be updated (not left failing, not deleted) — see Task 2 and Task 3.
- `FluentSyntaxHelpers` is `internal static class`; keep new members `public` if called from `EfSchemaVisualizer.Core.CodeGen` (cross-namespace, same assembly), matching the existing `GetPropertyNameFor` / `FindCallsNamed` convention.
- Run `dotnet test` from the repo root (`/root/RiderProjects/EfSchemaVisualizer`) after every task; all tests must be green before committing.

---

## Task 1: Append `HasMaxLength` to an existing `Property()` call that has none

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/Parsing/FluentSyntaxHelpers.cs`
- Modify: `src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs`

**Interfaces:**
- Consumes: `FluentSyntaxHelpers.FindEntityConfigInvocations`, `FindCallsNamed`, `GetPropertyNameFor` (all existing, unchanged signatures).
- Produces: `FluentSyntaxHelpers.GetPropertyNameForPropertyCall(InvocationExpressionSyntax propertyInvocation) : string?` — used directly by Task 1, and by `GetPropertyNameFor` internally. `OnModelCreatingRewriter.BuildMaxLengthCall(ExpressionSyntax propertyCallExpression, int maxLength) : InvocationExpressionSyntax` — a private helper Task 2 and Task 3 will reuse.

- [ ] **Step 1: Write the failing test**

Add to `tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs`, after the existing `RewriteMaxLength_UnknownEntity_Throws` test (before the `SourceWithNestedEntityConfig` field):

```csharp
    private const string SourceWithUnconfiguredProperty = """
        public class AppDbContext : DbContext
        {
            // unrelated comment that must survive untouched
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.Property(e => e.Name).HasMaxLength(100);
                    entity.Property(e => e.Email);
                });

                modelBuilder.Entity<Address>(entity =>
                {
                    entity.Property(e => e.Line1).HasMaxLength(200);
                });
            }
        }
        """;

    [Fact]
    public void RewriteMaxLength_PropertyExistsWithoutHasMaxLength_AppendsHasMaxLengthCall()
    {
        var result = new OnModelCreatingRewriter()
            .RewriteMaxLength(SourceWithUnconfiguredProperty, entityName: "Person", propertyName: "Email", newMaxLength: 50);

        Assert.Contains("entity.Property(e => e.Email).HasMaxLength(50)", result);
        Assert.Contains("// unrelated comment that must survive untouched", result);

        var configs = new FluentConfigParser().ParseMaxLengths(result).Value;
        Assert.Contains(configs, c => c is { EntityName: "Person", PropertyName: "Name", MaxLength: 100 });
        Assert.Contains(configs, c => c is { EntityName: "Person", PropertyName: "Email", MaxLength: 50 });
        Assert.Contains(configs, c => c is { EntityName: "Address", PropertyName: "Line1", MaxLength: 200 });
    }
```

Add `using EfSchemaVisualizer.Core.Parsing;` to the top of the test file (needed for `FluentConfigParser`).

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~RewriteMaxLength_PropertyExistsWithoutHasMaxLength_AppendsHasMaxLengthCall"`
Expected: FAIL with `System.InvalidOperationException: No HasMaxLength call found for Person.Email`

- [ ] **Step 3: Extract `GetPropertyNameForPropertyCall` in `FluentSyntaxHelpers.cs`**

Replace the existing `GetPropertyNameFor` method (and the block immediately after it) with:

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

        return GetPropertyNameForPropertyCall(propertyInvocation);
    }

    /// Given a bare `entity.Property(e => e.Name)` invocation itself (string overload and
    /// block-bodied lambda also resolved), returns "Name" without requiring a `.HasMaxLength(...)`
    /// (or any other) call chained onto it.
    public static string? GetPropertyNameForPropertyCall(InvocationExpressionSyntax propertyInvocation)
    {
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

(This is a pure refactor — same logic, split into two methods. No behavior change.)

- [ ] **Step 4: Restructure `OnModelCreatingRewriter.RewriteMaxLength` to dispatch, and add the append case**

Replace the entire contents of `src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs` with:

```csharp
using System;
using System.Linq;
using EfSchemaVisualizer.Core.Parsing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EfSchemaVisualizer.Core.CodeGen;

public sealed class OnModelCreatingRewriter
{
    public string RewriteMaxLength(string sourceCode, string entityName, string propertyName, int newMaxLength)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var entityInvocations = FluentSyntaxHelpers.FindEntityConfigInvocations(root, entityName).ToList();

        var existingMaxLengthCall = entityInvocations
            .SelectMany(entityInvocation => FluentSyntaxHelpers.FindCallsNamed(entityInvocation, "HasMaxLength"))
            .FirstOrDefault(call => FluentSyntaxHelpers.GetPropertyNameFor(call) == propertyName);

        if (existingMaxLengthCall is not null)
        {
            return MutateExistingMaxLength(root, existingMaxLengthCall, newMaxLength);
        }

        var existingPropertyCall = entityInvocations
            .SelectMany(entityInvocation => FluentSyntaxHelpers.FindCallsNamed(entityInvocation, "Property"))
            .FirstOrDefault(call => FluentSyntaxHelpers.GetPropertyNameForPropertyCall(call) == propertyName);

        if (existingPropertyCall is not null)
        {
            return AppendMaxLengthToPropertyCall(root, existingPropertyCall, newMaxLength);
        }

        throw new InvalidOperationException(
            $"No HasMaxLength call found for {entityName}.{propertyName}");
    }

    private static string MutateExistingMaxLength(CompilationUnitSyntax root, InvocationExpressionSyntax targetCall, int newMaxLength)
    {
        var newArgument = SyntaxFactory.Argument(
            SyntaxFactory.LiteralExpression(
                SyntaxKind.NumericLiteralExpression,
                SyntaxFactory.Literal(newMaxLength)));

        var newCall = targetCall.WithArgumentList(
            targetCall.ArgumentList.WithArguments(
                SyntaxFactory.SingletonSeparatedList(newArgument)));

        var newRoot = root.ReplaceNode(targetCall, newCall);
        return newRoot.ToFullString();
    }

    private static string AppendMaxLengthToPropertyCall(CompilationUnitSyntax root, InvocationExpressionSyntax propertyCall, int newMaxLength)
    {
        var maxLengthCall = BuildMaxLengthCall(propertyCall, newMaxLength);

        var newRoot = root.ReplaceNode(propertyCall, maxLengthCall);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    private static InvocationExpressionSyntax BuildMaxLengthCall(ExpressionSyntax propertyCallExpression, int maxLength)
    {
        return SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                propertyCallExpression,
                SyntaxFactory.IdentifierName("HasMaxLength")),
            SyntaxFactory.ArgumentList(
                SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.Argument(
                        SyntaxFactory.LiteralExpression(
                            SyntaxKind.NumericLiteralExpression,
                            SyntaxFactory.Literal(maxLength))))));
    }
}
```

Note: the final `throw` (for "no `Entity<T>` block found for this property at all yet") is intentionally still here — Task 2 and Task 3 will replace it with the insertion cases, one at a time.

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~RewriteMaxLength_PropertyExistsWithoutHasMaxLength_AppendsHasMaxLengthCall"`
Expected: PASS

- [ ] **Step 6: Run the full suite to confirm no regressions**

Run: `dotnet test`
Expected: all tests pass (31 existing + 1 new = 32)

- [ ] **Step 7: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Parsing/FluentSyntaxHelpers.cs src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs
git commit -m "Append HasMaxLength to an existing Property() call that has none"
```

---

## Task 2: Insert a new `Property().HasMaxLength()` statement when the entity's block exists but the property is never mentioned

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/Parsing/FluentSyntaxHelpers.cs`
- Modify: `src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs`

**Interfaces:**
- Consumes: `FluentSyntaxHelpers.FindCallsNamed`, `GetPropertyNameForPropertyCall` (from Task 1). `OnModelCreatingRewriter.BuildMaxLengthCall` (private, from Task 1).
- Produces: `FluentSyntaxHelpers.GetPropertyLambdaParameterName(InvocationExpressionSyntax entityInvocation) : string`. `OnModelCreatingRewriter.BuildPropertyStatement(string blockReceiverName, string propertyLambdaParam, string propertyName, int maxLength) : ExpressionStatementSyntax` — a private helper Task 3 will also reuse.

- [ ] **Step 1: Write the failing tests**

First, update the existing `RewriteMaxLength_NestedEntityConfig_DoesNotLeakIntoOuterEntitysScope` test — it currently asserts a throw for exactly the shape this task now handles by inserting. Replace it entirely with:

```csharp
    [Fact]
    public void RewriteMaxLength_PropertyOnlyPresentInNestedConfig_InsertsNewStatementIntoOuterScope()
    {
        var rewriter = new OnModelCreatingRewriter();

        // Person has no Line1 property in this shape - Line1 belongs to the nested Address config.
        // RewriteMaxLength is purely syntactic (it doesn't cross-check property names against a
        // parsed EntityModel), so it inserts a new statement into Person's own scope rather than
        // reaching into the nested Address block.
        var result = rewriter.RewriteMaxLength(
            SourceWithNestedEntityConfig, entityName: "Person", propertyName: "Line1", newMaxLength: 999);

        Assert.Contains("entity.Property(e => e.Line1).HasMaxLength(999)", result);

        var configs = new FluentConfigParser().ParseMaxLengths(result).Value;
        Assert.Contains(configs, c => c is { EntityName: "Person", PropertyName: "Line1", MaxLength: 999 });
        Assert.Contains(configs, c => c is { EntityName: "Address", PropertyName: "Line1", MaxLength: 200 });
    }
```

Then add two more tests after it:

```csharp
    private const string SourceWithMissingPropertyMention = """
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
    public void RewriteMaxLength_PropertyNeverMentioned_InsertsNewStatementAtEndOfBlock()
    {
        var result = new OnModelCreatingRewriter()
            .RewriteMaxLength(SourceWithMissingPropertyMention, entityName: "Person", propertyName: "Email", newMaxLength: 75);

        Assert.Contains("entity.Property(e => e.Email).HasMaxLength(75)", result);

        var configs = new FluentConfigParser().ParseMaxLengths(result).Value;
        Assert.Contains(configs, c => c is { EntityName: "Person", PropertyName: "Name", MaxLength: 100 });
        Assert.Contains(configs, c => c is { EntityName: "Person", PropertyName: "Email", MaxLength: 75 });
    }

    private const string SourceWithNonDefaultLambdaParam = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.Property(x => x.Name).HasMaxLength(100);
                });
            }
        }
        """;

    [Fact]
    public void RewriteMaxLength_PropertyNeverMentioned_MatchesSiblingLambdaParameterName()
    {
        var result = new OnModelCreatingRewriter()
            .RewriteMaxLength(SourceWithNonDefaultLambdaParam, entityName: "Person", propertyName: "Email", newMaxLength: 75);

        Assert.Contains("entity.Property(x => x.Email).HasMaxLength(75)", result);
    }

    private const string SourceWithEmptyEntityBlock = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                });
            }
        }
        """;

    [Fact]
    public void RewriteMaxLength_EmptyEntityBlock_FallsBackToDefaultLambdaParameterName()
    {
        var result = new OnModelCreatingRewriter()
            .RewriteMaxLength(SourceWithEmptyEntityBlock, entityName: "Person", propertyName: "Name", newMaxLength: 40);

        Assert.Contains("entity.Property(e => e.Name).HasMaxLength(40)", result);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~OnModelCreatingRewriterTests"`
Expected: `RewriteMaxLength_PropertyOnlyPresentInNestedConfig_InsertsNewStatementIntoOuterScope`, `RewriteMaxLength_PropertyNeverMentioned_InsertsNewStatementAtEndOfBlock`, `RewriteMaxLength_PropertyNeverMentioned_MatchesSiblingLambdaParameterName`, and `RewriteMaxLength_EmptyEntityBlock_FallsBackToDefaultLambdaParameterName` all FAIL with `System.InvalidOperationException: No HasMaxLength call found for ...`. Other tests in the file still PASS.

- [ ] **Step 3: Add `GetPropertyLambdaParameterName` to `FluentSyntaxHelpers.cs`**

Add this method after `GetPropertyNameForPropertyCall` (before `GetConfiguredEntityName`):

```csharp
    /// Returns the lambda parameter name used by an existing `entity.Property(<param> => ...)` call
    /// within the given `Entity&lt;T&gt;(...)` invocation's scope, so a newly synthesized `Property()`
    /// call can match the block's existing style. Falls back to "e" if the block has no such call yet.
    public static string GetPropertyLambdaParameterName(InvocationExpressionSyntax entityInvocation)
    {
        foreach (var propertyCall in FindCallsNamed(entityInvocation, "Property"))
        {
            if (propertyCall.ArgumentList.Arguments.Select(a => a.Expression).FirstOrDefault() is SimpleLambdaExpressionSyntax lambda)
            {
                return lambda.Parameter.Identifier.Text;
            }
        }

        return "e";
    }
```

- [ ] **Step 4: Add the insert-statement case to `OnModelCreatingRewriter.cs`**

In `RewriteMaxLength`, replace:

```csharp
        if (existingPropertyCall is not null)
        {
            return AppendMaxLengthToPropertyCall(root, existingPropertyCall, newMaxLength);
        }

        throw new InvalidOperationException(
            $"No HasMaxLength call found for {entityName}.{propertyName}");
```

with:

```csharp
        if (existingPropertyCall is not null)
        {
            return AppendMaxLengthToPropertyCall(root, existingPropertyCall, newMaxLength);
        }

        var existingEntityInvocation = entityInvocations.FirstOrDefault();

        if (existingEntityInvocation is not null)
        {
            return InsertPropertyStatement(root, existingEntityInvocation, propertyName, newMaxLength);
        }

        throw new InvalidOperationException(
            $"No HasMaxLength call found for {entityName}.{propertyName}");
```

Then add these two methods after `AppendMaxLengthToPropertyCall`:

```csharp
    private static string InsertPropertyStatement(CompilationUnitSyntax root, InvocationExpressionSyntax entityInvocation, string propertyName, int newMaxLength)
    {
        var lambda = (SimpleLambdaExpressionSyntax)entityInvocation.ArgumentList.Arguments.Single().Expression;
        var block = lambda.Block!;
        var blockReceiverName = lambda.Parameter.Identifier.Text;
        var propertyLambdaParam = FluentSyntaxHelpers.GetPropertyLambdaParameterName(entityInvocation);

        var newStatement = BuildPropertyStatement(blockReceiverName, propertyLambdaParam, propertyName, newMaxLength);
        var newBlock = block.AddStatements(newStatement);

        var newRoot = root.ReplaceNode(block, newBlock);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    private static ExpressionStatementSyntax BuildPropertyStatement(string blockReceiverName, string propertyLambdaParam, string propertyName, int maxLength)
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

        return SyntaxFactory.ExpressionStatement(BuildMaxLengthCall(propertyCall, maxLength));
    }
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~OnModelCreatingRewriterTests"`
Expected: all pass.

- [ ] **Step 6: Run the full suite to confirm no regressions**

Run: `dotnet test`
Expected: all tests pass (32 existing + 3 new = 35; the nested-config test was replaced, not added, so net +3).

- [ ] **Step 7: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Parsing/FluentSyntaxHelpers.cs src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs
git commit -m "Insert a new Property().HasMaxLength() statement when the property is never mentioned"
```

---

## Task 3: Synthesize a whole new `Entity<T>` block when the entity has no config at all

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs`

**Interfaces:**
- Consumes: `OnModelCreatingRewriter.BuildPropertyStatement` (from Task 2).
- Produces: none further — this is the last of the four `RewriteMaxLength` cases.

- [ ] **Step 1: Write the failing tests**

First, update the existing `RewriteMaxLength_UnknownEntity_Throws` test — it currently asserts a throw for exactly the shape this task now handles by inserting. Replace it entirely with:

```csharp
    [Fact]
    public void RewriteMaxLength_UnknownEntity_InsertsNewEntityBlock()
    {
        var result = new OnModelCreatingRewriter()
            .RewriteMaxLength(Source, entityName: "Vehicle", propertyName: "Name", newMaxLength: 10);

        Assert.Contains("modelBuilder.Entity<Vehicle>(entity =>", result);
        Assert.Contains("entity.Property(e => e.Name).HasMaxLength(10)", result);

        var configs = new FluentConfigParser().ParseMaxLengths(result).Value;
        Assert.Contains(configs, c => c is { EntityName: "Vehicle", PropertyName: "Name", MaxLength: 10 });
        Assert.Contains(configs, c => c is { EntityName: "Person", PropertyName: "Name", MaxLength: 100 });
        Assert.Contains(configs, c => c is { EntityName: "Address", PropertyName: "Line1", MaxLength: 200 });
    }
```

Then add these tests after it:

```csharp
    private const string SourceWithRenamedBuilderAndNoConfig = """
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
    public void RewriteMaxLength_UnknownEntity_RenamedModelBuilderParameter_UsesSameReceiverName()
    {
        var result = new OnModelCreatingRewriter()
            .RewriteMaxLength(SourceWithRenamedBuilderAndNoConfig, entityName: "Vehicle", propertyName: "Name", newMaxLength: 10);

        Assert.Contains("builder.Entity<Vehicle>(entity =>", result);
        Assert.DoesNotContain("modelBuilder.Entity<Vehicle>", result);
    }

    private const string SourceWithoutOnModelCreating = """
        public class AppDbContext : DbContext
        {
        }
        """;

    [Fact]
    public void RewriteMaxLength_OnModelCreatingMissing_Throws()
    {
        var rewriter = new OnModelCreatingRewriter();

        Assert.Throws<InvalidOperationException>(() =>
            rewriter.RewriteMaxLength(SourceWithoutOnModelCreating, entityName: "Vehicle", propertyName: "Name", newMaxLength: 10));
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~OnModelCreatingRewriterTests"`
Expected: `RewriteMaxLength_UnknownEntity_InsertsNewEntityBlock` and `RewriteMaxLength_UnknownEntity_RenamedModelBuilderParameter_UsesSameReceiverName` FAIL with `System.InvalidOperationException: No HasMaxLength call found for Vehicle.Name`. `RewriteMaxLength_OnModelCreatingMissing_Throws` PASSES already (empty class throws `InvalidOperationException` today via `FindEntityConfigInvocations` finding nothing and falling through to the existing final throw) — that's fine, it's a regression guard for Step 4, not a new behavior.

- [ ] **Step 3: Add the synthesize-whole-block case to `OnModelCreatingRewriter.cs`**

Replace the final line of `RewriteMaxLength`:

```csharp
        throw new InvalidOperationException(
            $"No HasMaxLength call found for {entityName}.{propertyName}");
```

with:

```csharp
        return InsertEntityBlock(root, entityName, propertyName, newMaxLength);
```

Then add this method after `InsertPropertyStatement`:

```csharp
    private static string InsertEntityBlock(CompilationUnitSyntax root, string entityName, string propertyName, int newMaxLength)
    {
        var method = root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(m => m.Identifier.Text == "OnModelCreating")
            ?? throw new InvalidOperationException("No OnModelCreating method found in source.");

        var methodBody = method.Body
            ?? throw new InvalidOperationException("OnModelCreating has no method body.");

        var modelBuilderParamName = method.ParameterList.Parameters.Single().Identifier.Text;

        var propertyStatement = BuildPropertyStatement("entity", "e", propertyName, newMaxLength);

        var entityBlockStatement = SyntaxFactory.ExpressionStatement(
            SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.IdentifierName(modelBuilderParamName),
                    SyntaxFactory.GenericName(SyntaxFactory.Identifier("Entity"))
                        .WithTypeArgumentList(
                            SyntaxFactory.TypeArgumentList(
                                SyntaxFactory.SingletonSeparatedList<TypeSyntax>(
                                    SyntaxFactory.IdentifierName(entityName))))),
                SyntaxFactory.ArgumentList(
                    SyntaxFactory.SingletonSeparatedList(
                        SyntaxFactory.Argument(
                            SyntaxFactory.SimpleLambdaExpression(
                                SyntaxFactory.Parameter(SyntaxFactory.Identifier("entity")),
                                SyntaxFactory.Block(propertyStatement)))))));

        var newMethodBody = methodBody.AddStatements(entityBlockStatement);
        var newRoot = root.ReplaceNode(methodBody, newMethodBody);
        return newRoot.NormalizeWhitespace().ToFullString();
    }
```

This requires the method-lookup path to be reachable — but note `InsertEntityBlock` is only called once `entityInvocations` is empty (no `Entity<T>` block found anywhere for this entity), so the `OnModelCreating` lookup here is independent of `FindEntityConfigInvocations`. `SourceWithoutOnModelCreating` (an empty class) has no `OnModelCreating` method at all, so `FirstOrDefault(...)` returns `null` and the `?? throw` fires — this is what keeps `RewriteMaxLength_OnModelCreatingMissing_Throws` passing.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~OnModelCreatingRewriterTests"`
Expected: all pass, including `RewriteMaxLength_OnModelCreatingMissing_Throws`.

- [ ] **Step 5: Run the full suite to confirm no regressions**

Run: `dotnet test`
Expected: all tests pass (35 + 2 new = 37; `RewriteMaxLength_UnknownEntity_Throws` was replaced, not added, so net +2).

- [ ] **Step 6: Commit**

```bash
git add src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs
git commit -m "Synthesize a whole new Entity<T> block when the entity has no config at all"
```

---

## Task 4: Update the backlog

**Files:**
- Modify: `docs/backlog.md`

**Interfaces:**
- Consumes: none.
- Produces: none — documentation only.

- [ ] **Step 1: Check off the completed item**

In `docs/backlog.md`, under `## Priority 1`, change:

```markdown
- [ ] **`[found]` Insert new fluent config where none exists.**
      `OnModelCreatingRewriter` can only replace an existing `HasMaxLength` arg.
      It cannot add a `HasMaxLength` (or any call) to a property that has none.
      Requires generating a new statement into a lambda body while preserving
      surrounding trivia/indentation. This is the trivia problem the spike's
      byte-identical test did *not* exercise (it only swaps one token).
```

to:

```markdown
- [x] **`[found]` Insert new fluent config where none exists.**
      `OnModelCreatingRewriter` can only replace an existing `HasMaxLength` arg.
      It cannot add a `HasMaxLength` (or any call) to a property that has none.
      Requires generating a new statement into a lambda body while preserving
      surrounding trivia/indentation. This is the trivia problem the spike's
      byte-identical test did *not* exercise (it only swaps one token).
      **Update:** `RewriteMaxLength` now handles all four cases (mutate,
      append, insert statement, synthesize whole block) — see
      `2026-07-08-insert-fluent-config-design.md`. Trivia is *not* preserved
      on insertion paths (whole-file `NormalizeWhitespace()` is used
      instead, a deliberate trade-off documented in the spec); only the
      pure-mutation path remains byte-identical.
```

- [ ] **Step 2: Commit**

```bash
git add docs/backlog.md
git commit -m "Check off 'insert new fluent config where none exists' in backlog"
```
