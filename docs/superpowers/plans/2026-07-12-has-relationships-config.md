# Relationships (`HasOne`/`HasMany`/`WithOne`/`WithMany`) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Parse EF Core relationship configuration (`HasOne`/`HasMany`/`WithOne`/`WithMany`, `HasForeignKey`, `OnDelete`, `UsingEntity`) into a new `RelationshipModel`, following the parse → merge pattern already established for `HasKey`/`HasIndex`/etc.

**Architecture:** A new `FluentConfigParser.ParseRelationships(string sourceCode, IReadOnlyList<EntityModel> entities)` locates `HasOne`/`HasMany` calls (both nested inside an `Entity<T>(entity => {...})` lambda block and chained directly off a bare `Entity<T>()`), walks the rest of the fluent chain (`WithMany`/`WithOne`, then `HasForeignKey`/`OnDelete`/`UsingEntity` in any order), and resolves the related entity either from an explicit generic type argument or by cross-referencing the navigation property's `ClrType` against the already-parsed `EntityModel`s. `ModelMerger.ApplyRelationships` maps the resulting `RelationshipConfig` list to `RelationshipModel` 1:1 — no new schema-root aggregate type. No rewriter in this pass (see spec's "Out of scope").

**Tech Stack:** C# / .NET 10, Roslyn (`Microsoft.CodeAnalysis.CSharp`) syntax-tree parsing (no semantic model), xUnit.

## Global Constraints

- Spec: `docs/superpowers/specs/2026-07-12-has-relationships-config-design.md` — every requirement below traces back to it.
- No rewriter (`SetRelationship`/`RemoveRelationship`) in this pass — parse + merge only.
- No new schema-root aggregate type — `RelationshipModel`/`RelationshipConfig` are flat, independent lists.
- Diagnostic codes go through the `DiagnosticCodes` constants class (no bare string literals).
- All new/changed code must leave `dotnet test` fully green (207 existing tests + new ones) with 0 warnings before any commit.
- Follow existing file/namespace conventions: models in `src/EfSchemaVisualizer.Core/Model/`, parsing DTOs/parsers in `src/EfSchemaVisualizer.Core/Parsing/`, tests mirrored under `tests/EfSchemaVisualizer.Core.Tests/{Model,Parsing}/`.

---

### Task 1: Model types and diagnostic codes

**Files:**
- Create: `src/EfSchemaVisualizer.Core/Model/RelationshipKind.cs`
- Create: `src/EfSchemaVisualizer.Core/Model/RelationshipModel.cs`
- Create: `src/EfSchemaVisualizer.Core/Parsing/RelationshipConfig.cs`
- Modify: `src/EfSchemaVisualizer.Core/Parsing/DiagnosticCodes.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Model/PropertyModelTests.cs` (append)

**Interfaces:**
- Produces: `RelationshipKind` enum (`OneToOne`, `OneToMany`, `ManyToMany`); `RelationshipModel` record; `RelationshipConfig` record; `DiagnosticCodes.UnresolvableRelationshipTarget`, `DiagnosticCodes.UnreadableHasForeignKeyArgument`, `DiagnosticCodes.UnreadableOnDeleteArgument`.

- [ ] **Step 1: Write the failing test**

Append to `tests/EfSchemaVisualizer.Core.Tests/Model/PropertyModelTests.cs`:

```csharp
    [Fact]
    public void RelationshipModel_ForeignKeyProperties_DefaultsToEmpty()
    {
        var relationship = new RelationshipModel(
            "Customer", "Order", RelationshipKind.OneToMany,
            PrincipalNavigation: "Orders", DependentNavigation: "Customer");

        Assert.Empty(relationship.ForeignKeyProperties);
        Assert.Null(relationship.OnDeleteBehavior);
        Assert.Null(relationship.JoinEntityName);
    }

    [Fact]
    public void RelationshipModel_WithForeignKeyProperties_ProducesUpdatedCopy_LeavingOriginalUnchanged()
    {
        var original = new RelationshipModel(
            "Customer", "Order", RelationshipKind.OneToMany,
            PrincipalNavigation: "Orders", DependentNavigation: "Customer");

        var updated = original with { ForeignKeyProperties = new List<string> { "CustomerId" }, OnDeleteBehavior = "Cascade" };

        Assert.Empty(original.ForeignKeyProperties);
        Assert.Equal(new[] { "CustomerId" }, updated.ForeignKeyProperties);
        Assert.Equal("Cascade", updated.OnDeleteBehavior);
        Assert.Equal(original.PrincipalEntity, updated.PrincipalEntity);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~RelationshipModel"`
Expected: FAIL — compile error, `RelationshipModel`/`RelationshipKind` do not exist.

- [ ] **Step 3: Write the model types**

`src/EfSchemaVisualizer.Core/Model/RelationshipKind.cs`:
```csharp
namespace EfSchemaVisualizer.Core.Model;

public enum RelationshipKind
{
    OneToOne,
    OneToMany,
    ManyToMany,
}
```

`src/EfSchemaVisualizer.Core/Model/RelationshipModel.cs`:
```csharp
using System.Collections.Generic;

namespace EfSchemaVisualizer.Core.Model;

public sealed record RelationshipModel(
    string PrincipalEntity,
    string DependentEntity,
    RelationshipKind Kind,
    string? PrincipalNavigation,
    string? DependentNavigation,
    IReadOnlyList<string>? ForeignKeyProperties = null,
    string? OnDeleteBehavior = null,
    string? JoinEntityName = null)
{
    public IReadOnlyList<string> ForeignKeyProperties { get; init; } = ForeignKeyProperties ?? new List<string>();
}
```

`src/EfSchemaVisualizer.Core/Parsing/RelationshipConfig.cs`:
```csharp
using System.Collections.Generic;
using EfSchemaVisualizer.Core.Model;

namespace EfSchemaVisualizer.Core.Parsing;

public sealed record RelationshipConfig(
    string PrincipalEntity,
    string DependentEntity,
    RelationshipKind Kind,
    string? PrincipalNavigation,
    string? DependentNavigation,
    IReadOnlyList<string>? ForeignKeyProperties = null,
    string? OnDeleteBehavior = null,
    string? JoinEntityName = null)
{
    public IReadOnlyList<string> ForeignKeyProperties { get; init; } = ForeignKeyProperties ?? new List<string>();
}
```

Append to `src/EfSchemaVisualizer.Core/Parsing/DiagnosticCodes.cs` (inside the existing `DiagnosticCodes` class, after `UnreadableIsUniqueArgument`):
```csharp
    public const string UnresolvableRelationshipTarget = nameof(UnresolvableRelationshipTarget);
    public const string UnreadableHasForeignKeyArgument = nameof(UnreadableHasForeignKeyArgument);
    public const string UnreadableOnDeleteArgument = nameof(UnreadableOnDeleteArgument);
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~RelationshipModel"`
Expected: PASS (2 tests)

- [ ] **Step 5: Run the full suite to confirm no regressions**

Run: `dotnet test`
Expected: all tests pass (209 total), 0 warnings.

- [ ] **Step 6: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Model/RelationshipKind.cs src/EfSchemaVisualizer.Core/Model/RelationshipModel.cs src/EfSchemaVisualizer.Core/Parsing/RelationshipConfig.cs src/EfSchemaVisualizer.Core/Parsing/DiagnosticCodes.cs tests/EfSchemaVisualizer.Core.Tests/Model/PropertyModelTests.cs
git commit -m "Add RelationshipModel/RelationshipConfig types and relationship diagnostic codes"
```

---

### Task 2: Promote `TryReadKeyPropertyNames` to a shared `FluentSyntaxHelpers.TryReadPropertyNameList`

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/Parsing/FluentSyntaxHelpers.cs`
- Modify: `src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs:249-299` (the `TryReadKeyPropertyNames`/`TryReadKeyPropertyNamesFromLambdaBody` private methods and their one call site in `ParseKeys`)
- Test: `tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentSyntaxHelpersTests.cs` (new file)

**Interfaces:**
- Produces: `internal static IReadOnlyList<string>? FluentSyntaxHelpers.TryReadPropertyNameList(InvocationExpressionSyntax call)` — same argument-shape resolution `HasKey`/`HasForeignKey` both need (single lambda member access, composite anonymous-object lambda, bare string literal params).
- Consumed by: `FluentConfigParser.ParseKeys` (Task 2, updated in place) and `FluentConfigParser.ParseRelationships` (Task 4).

This is a behavior-preserving refactor: the existing `ParseKeys` test suite (`FluentConfigParserTests.ParseKeys_*`, 6 tests) must still pass unchanged afterward — that's the regression check for this task, run alongside the new direct tests below.

- [ ] **Step 1: Write the failing test**

Create `tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentSyntaxHelpersTests.cs`:

```csharp
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace EfSchemaVisualizer.Core.Tests.Parsing;

public class FluentSyntaxHelpersTests
{
    private static InvocationExpressionSyntax ParseSingleInvocation(string callExpression)
    {
        var wrapped = $$"""
            public class C
            {
                void M()
                {
                    {{callExpression}};
                }
            }
            """;
        var tree = CSharpSyntaxTree.ParseText(wrapped);
        var root = tree.GetCompilationUnitRoot();
        return root.DescendantNodes().OfType<InvocationExpressionSyntax>().Last();
    }

    [Fact]
    public void TryReadPropertyNameList_SingleLambda_ReturnsSingleName()
    {
        var invocation = ParseSingleInvocation("entity.HasKey(e => e.Id)");

        var names = EfSchemaVisualizer.Core.Parsing.FluentSyntaxHelpers.TryReadPropertyNameList(invocation);

        Assert.Equal(new[] { "Id" }, names);
    }

    [Fact]
    public void TryReadPropertyNameList_CompositeLambda_ReturnsNamesInOrder()
    {
        var invocation = ParseSingleInvocation("entity.HasKey(e => new { e.A, e.B })");

        var names = EfSchemaVisualizer.Core.Parsing.FluentSyntaxHelpers.TryReadPropertyNameList(invocation);

        Assert.Equal(new[] { "A", "B" }, names);
    }

    [Fact]
    public void TryReadPropertyNameList_BareStringParams_ReturnsNamesInOrder()
    {
        var invocation = ParseSingleInvocation("""entity.HasKey("A", "B")""");

        var names = EfSchemaVisualizer.Core.Parsing.FluentSyntaxHelpers.TryReadPropertyNameList(invocation);

        Assert.Equal(new[] { "A", "B" }, names);
    }

    [Fact]
    public void TryReadPropertyNameList_ExplicitNameAnonymousMember_ReturnsNull()
    {
        var invocation = ParseSingleInvocation("entity.HasKey(e => new { K = e.A })");

        var names = EfSchemaVisualizer.Core.Parsing.FluentSyntaxHelpers.TryReadPropertyNameList(invocation);

        Assert.Null(names);
    }

    [Fact]
    public void TryReadPropertyNameList_NoArguments_ReturnsNull()
    {
        var invocation = ParseSingleInvocation("entity.HasKey()");

        var names = EfSchemaVisualizer.Core.Parsing.FluentSyntaxHelpers.TryReadPropertyNameList(invocation);

        Assert.Null(names);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~FluentSyntaxHelpersTests"`
Expected: FAIL — compile error, `TryReadPropertyNameList` does not exist on `FluentSyntaxHelpers`.

- [ ] **Step 3: Add the shared helper to `FluentSyntaxHelpers.cs`**

Add to `src/EfSchemaVisualizer.Core/Parsing/FluentSyntaxHelpers.cs`, after `GetPropertyLambdaParameterName` (before `GetConfiguredEntityName`):

```csharp
    /// Reads a property name list from an invocation's arguments: a single lambda member access
    /// (`e => e.Id`), a composite anonymous-object lambda (`e => new { e.A, e.B }`), or bare string
    /// literal params (single or composite), e.g. for `HasKey(...)` or `HasForeignKey(...)`.
    /// Returns null when the argument shape isn't recognized.
    internal static IReadOnlyList<string>? TryReadPropertyNameList(InvocationExpressionSyntax call)
    {
        var arguments = call.ArgumentList.Arguments;

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
            return TryReadPropertyNameListFromLambdaBody(body);
        }

        return null;
    }

    private static IReadOnlyList<string>? TryReadPropertyNameListFromLambdaBody(ExpressionSyntax body)
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

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~FluentSyntaxHelpersTests"`
Expected: PASS (5 tests)

- [ ] **Step 5: Update `ParseKeys` to use the shared helper and delete the now-duplicated private methods**

In `src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs`, change line 228:
```csharp
                    var propertyNames = TryReadKeyPropertyNames(hasKeyCall);
```
to:
```csharp
                    var propertyNames = FluentSyntaxHelpers.TryReadPropertyNameList(hasKeyCall);
```

Then delete the two now-unused private methods (originally lines 249-299): `TryReadKeyPropertyNames` and `TryReadKeyPropertyNamesFromLambdaBody`.

- [ ] **Step 6: Run the full suite to confirm the refactor is behavior-preserving**

Run: `dotnet test`
Expected: all tests pass (214 total: 209 from Task 1 + 5 new), 0 warnings. In particular, all 6 `ParseKeys_*` tests in `FluentConfigParserTests` must still pass unchanged — this is the regression check that the promotion didn't alter `HasKey` behavior.

- [ ] **Step 7: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Parsing/FluentSyntaxHelpers.cs src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentSyntaxHelpersTests.cs
git commit -m "Promote HasKey's property-name-list parsing into a shared FluentSyntaxHelpers helper"
```

---

### Task 3: Add `FindChainedCall` and `TryGetElementTypeName` primitives

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/Parsing/FluentSyntaxHelpers.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentSyntaxHelpersTests.cs` (append)

**Interfaces:**
- Produces: `internal static InvocationExpressionSyntax? FluentSyntaxHelpers.FindChainedCall(InvocationExpressionSyntax invocation, string methodName)`; `internal static string? FluentSyntaxHelpers.TryGetElementTypeName(string clrType)`.
- Consumed by: `FluentConfigParser.ParseRelationships` (Task 4).

- [ ] **Step 1: Write the failing tests**

Append to `tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentSyntaxHelpersTests.cs`:

```csharp
    [Fact]
    public void FindChainedCall_ImmediateNextCallMatches_ReturnsIt()
    {
        var wrapped = """
            public class C
            {
                void M()
                {
                    entity.HasOne(d => d.Customer).WithMany(p => p.Orders);
                }
            }
            """;
        var tree = CSharpSyntaxTree.ParseText(wrapped);
        var root = tree.GetCompilationUnitRoot();
        var hasOneCall = root.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(i => i.Expression is MemberAccessExpressionSyntax { Name.Identifier.Text: "HasOne" });

        var chained = EfSchemaVisualizer.Core.Parsing.FluentSyntaxHelpers.FindChainedCall(hasOneCall, "WithMany");

        Assert.NotNull(chained);
        Assert.Equal("WithMany", ((MemberAccessExpressionSyntax)chained!.Expression).Name.Identifier.Text);
    }

    [Fact]
    public void FindChainedCall_NextCallHasDifferentName_ReturnsNull()
    {
        var wrapped = """
            public class C
            {
                void M()
                {
                    entity.HasOne(d => d.Customer).WithOne(p => p.Person);
                }
            }
            """;
        var tree = CSharpSyntaxTree.ParseText(wrapped);
        var root = tree.GetCompilationUnitRoot();
        var hasOneCall = root.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(i => i.Expression is MemberAccessExpressionSyntax { Name.Identifier.Text: "HasOne" });

        var chained = EfSchemaVisualizer.Core.Parsing.FluentSyntaxHelpers.FindChainedCall(hasOneCall, "WithMany");

        Assert.Null(chained);
    }

    [Fact]
    public void FindChainedCall_NothingChained_ReturnsNull()
    {
        var invocation = ParseSingleInvocation("entity.HasOne(d => d.Customer)");

        var chained = EfSchemaVisualizer.Core.Parsing.FluentSyntaxHelpers.FindChainedCall(invocation, "WithMany");

        Assert.Null(chained);
    }

    [Theory]
    [InlineData("ICollection<Order>", "Order")]
    [InlineData("IList<Order>", "Order")]
    [InlineData("List<Order>", "Order")]
    [InlineData("IEnumerable<Order>", "Order")]
    [InlineData("HashSet<Order>", "Order")]
    [InlineData("ISet<Order>", "Order")]
    [InlineData("Order[]", "Order")]
    [InlineData("Order", "Order")]
    public void TryGetElementTypeName_RecognizedShapes_ReturnsElementType(string clrType, string expected)
    {
        var result = EfSchemaVisualizer.Core.Parsing.FluentSyntaxHelpers.TryGetElementTypeName(clrType);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void TryGetElementTypeName_UnrecognizedGenericWrapper_ReturnsNull()
    {
        var result = EfSchemaVisualizer.Core.Parsing.FluentSyntaxHelpers.TryGetElementTypeName("IQueryable<Order>");

        Assert.Null(result);
    }
```

(`System.Linq` is already imported in this file from Task 2, covering the `.Single(...)` calls above.)

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~FluentSyntaxHelpersTests"`
Expected: FAIL — compile error, `FindChainedCall`/`TryGetElementTypeName` do not exist.

- [ ] **Step 3: Implement both primitives**

Add to `src/EfSchemaVisualizer.Core/Parsing/FluentSyntaxHelpers.cs`, after `TryReadPropertyNameListFromLambdaBody`:

```csharp
    /// Finds the invocation immediately chained onto `invocation` via `.methodName(...)`, e.g. given
    /// the `HasOne(...)` invocation, `FindChainedCall(hasOneCall, "WithMany")` finds the
    /// `.WithMany(...)` invocation wrapping it. Returns null if nothing is chained onto `invocation`,
    /// or if what's chained isn't named `methodName`.
    internal static InvocationExpressionSyntax? FindChainedCall(InvocationExpressionSyntax invocation, string methodName)
    {
        return invocation.Parent is MemberAccessExpressionSyntax { Name.Identifier.Text: var name } memberAccess
            && memberAccess.Expression == invocation
            && name == methodName
            && memberAccess.Parent is InvocationExpressionSyntax chained
                ? chained
                : null;
    }

    private static readonly string[] CollectionWrapperNames =
    {
        "ICollection", "IList", "List", "IEnumerable", "HashSet", "ISet",
    };

    /// Given a property's ClrType text (e.g. "ICollection&lt;Order&gt;", "Order[]", or bare "Order"),
    /// returns the element type name for recognized collection wrapper shapes, or the type text
    /// unchanged if it isn't a generic/array shape at all. Returns null for a generic wrapper shape
    /// that isn't recognized (e.g. "IQueryable&lt;Order&gt;").
    internal static string? TryGetElementTypeName(string clrType)
    {
        if (clrType.EndsWith("[]", StringComparison.Ordinal))
        {
            return clrType[..^2];
        }

        var genericOpen = clrType.IndexOf('<');
        if (genericOpen < 0)
        {
            return clrType;
        }

        var wrapperName = clrType[..genericOpen];
        if (!CollectionWrapperNames.Contains(wrapperName))
        {
            return null;
        }

        var genericClose = clrType.LastIndexOf('>');
        return genericClose > genericOpen
            ? clrType[(genericOpen + 1)..genericClose]
            : null;
    }
```

Add `using System;` to the top of `FluentSyntaxHelpers.cs` (needed for `StringComparison`).

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~FluentSyntaxHelpersTests"`
Expected: PASS (17 tests: 5 from Task 2 + 12 new — the `TryGetElementTypeName_RecognizedShapes_ReturnsElementType` `[Theory]` counts as 8 individual test cases, one per `[InlineData]` row)

- [ ] **Step 5: Run the full suite**

Run: `dotnet test`
Expected: all tests pass (226 total: 214 from Task 2 + 12 new), 0 warnings.

- [ ] **Step 6: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Parsing/FluentSyntaxHelpers.cs tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentSyntaxHelpersTests.cs
git commit -m "Add FindChainedCall and TryGetElementTypeName syntax-helper primitives"
```

---

### Task 4: `FluentConfigParser.ParseRelationships` — OneToMany (both directions)

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs` (append)

**Interfaces:**
- Consumes: `EntityModel`/`PropertyModel` (from `EfSchemaVisualizer.Core.Model`); `FluentSyntaxHelpers.FindEntityConfigInvocations`, `.FindCallsNamed`, `.FindChainedCall`, `.TryReadPropertyNameList`, `.TryGetElementTypeName`, `.GetConfiguredEntityName` (all existing/Task-3 helpers).
- Produces: `public ParseResult<IReadOnlyList<RelationshipConfig>> FluentConfigParser.ParseRelationships(string sourceCode, IReadOnlyList<EntityModel> entities)`. Consumed by `ModelMerger.ApplyRelationships` (Task 6).

This task builds the full `ParseRelationships` method and its private helpers, but only exercises the two `OneToMany` shapes (`HasOne...WithMany` and `HasMany...WithOne`) plus the entity-resolution and chain-discovery machinery every shape depends on. Tasks 5 and 6 extend the same method for `OneToOne`/`ManyToMany` without re-deriving this machinery.

- [ ] **Step 1: Write the first failing test — block-nested `HasOne...WithMany`**

Append to `tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs` (new section at the end of the class, before the final closing brace):

```csharp

    // ─── ParseRelationships ─────────────────────────────────────────────────────

    private static readonly IReadOnlyList<EntityModel> OrderCustomerEntities = new List<EntityModel>
    {
        new("Customer", new List<PropertyModel>
        {
            new("Id", "int", IsNullable: false, MaxLength: null),
            new("Orders", "ICollection<Order>", IsNullable: false, MaxLength: null),
        }),
        new("Order", new List<PropertyModel>
        {
            new("Id", "int", IsNullable: false, MaxLength: null),
            new("CustomerId", "int", IsNullable: false, MaxLength: null),
            new("Customer", "Customer", IsNullable: false, MaxLength: null),
        }),
    };

    private const string SourceWithHasOneWithManyBlockNested = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Order>(entity =>
                {
                    entity.HasOne(d => d.Customer)
                          .WithMany(p => p.Orders)
                          .HasForeignKey(d => d.CustomerId);
                });
            }
        }
        """;

    [Fact]
    public void ParseRelationships_HasOneWithMany_BlockNested_ResolvesOneToMany()
    {
        var result = new FluentConfigParser().ParseRelationships(SourceWithHasOneWithManyBlockNested, OrderCustomerEntities);

        Assert.Empty(result.Diagnostics);
        var relationship = Assert.Single(result.Value);
        Assert.Equal(RelationshipKind.OneToMany, relationship.Kind);
        Assert.Equal("Customer", relationship.PrincipalEntity);
        Assert.Equal("Order", relationship.DependentEntity);
        Assert.Equal("Orders", relationship.PrincipalNavigation);
        Assert.Equal("Customer", relationship.DependentNavigation);
        Assert.Equal(new[] { "CustomerId" }, relationship.ForeignKeyProperties);
    }
```

Add `using EfSchemaVisualizer.Core.Model;` to the top of `FluentConfigParserTests.cs` if not already present.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~ParseRelationships_HasOneWithMany_BlockNested"`
Expected: FAIL — compile error, `ParseRelationships` does not exist on `FluentConfigParser`.

- [ ] **Step 3: Implement `ParseRelationships` and its helpers**

Add to `src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs`, as a new public method (after `ParseIndexes` and before the private `TryReadIsUnique` method — keeping it near the other `Parse*` public methods):

```csharp
    public ParseResult<IReadOnlyList<RelationshipConfig>> ParseRelationships(
        string sourceCode, IReadOnlyList<EntityModel> entities)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var results = new List<RelationshipConfig>();
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
                var calls = FluentSyntaxHelpers.FindCallsNamed(entityInvocation, "HasOne")
                    .Concat(FluentSyntaxHelpers.FindCallsNamed(entityInvocation, "HasMany"))
                    .ToList();

                if (FluentSyntaxHelpers.FindChainedCall(entityInvocation, "HasOne") is { } chainedHasOne)
                {
                    calls.Add(chainedHasOne);
                }

                if (FluentSyntaxHelpers.FindChainedCall(entityInvocation, "HasMany") is { } chainedHasMany)
                {
                    calls.Add(chainedHasMany);
                }

                foreach (var call in calls)
                {
                    ParseRelationshipChain(call, entityName!, entities, results, diagnostics);
                }
            }
        }

        return new ParseResult<IReadOnlyList<RelationshipConfig>>(results, diagnostics);
    }

    private static void ParseRelationshipChain(
        InvocationExpressionSyntax call,
        string configuringEntityName,
        IReadOnlyList<EntityModel> entities,
        List<RelationshipConfig> results,
        List<Diagnostic> diagnostics)
    {
        var isHasMany = GetInvokedMethodName(call) == "HasMany";

        var withCall = FluentSyntaxHelpers.FindChainedCall(call, "WithMany")
            ?? FluentSyntaxHelpers.FindChainedCall(call, "WithOne");

        if (withCall is null)
        {
            return;
        }

        var isWithMany = GetInvokedMethodName(withCall) == "WithMany";

        var (targetEntityName, targetResolved) = ResolveRelatedEntity(call, configuringEntityName, entities);

        if (!targetResolved)
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticCodes.UnresolvableRelationshipTarget,
                $"Could not determine the related entity for this {(isHasMany ? "HasMany" : "HasOne")} call.",
                configuringEntityName,
                PropertyName: null,
                call.Span));
            return;
        }

        var kind = (isHasMany, isWithMany) switch
        {
            (false, true) => RelationshipKind.OneToMany,  // HasOne...WithMany
            (true, false) => RelationshipKind.OneToMany,  // HasMany...WithOne
            (true, true) => RelationshipKind.ManyToMany,  // HasMany...WithMany
            (false, false) => RelationshipKind.OneToOne,  // HasOne...WithOne
        };

        InvocationExpressionSyntax? hasForeignKeyCall = null;
        InvocationExpressionSyntax? onDeleteCall = null;
        InvocationExpressionSyntax? usingEntityCall = null;

        WalkRelationshipTailChain(withCall, invocation =>
        {
            switch (GetInvokedMethodName(invocation))
            {
                case "HasForeignKey": hasForeignKeyCall = invocation; break;
                case "OnDelete": onDeleteCall = invocation; break;
                case "UsingEntity": usingEntityCall = invocation; break;
            }
        });

        string principalEntity;
        string dependentEntity;

        if (kind == RelationshipKind.OneToOne)
        {
            var explicitDependent = TryGetGenericTypeArgument(hasForeignKeyCall);
            dependentEntity = explicitDependent ?? configuringEntityName;
            principalEntity = dependentEntity == configuringEntityName ? targetEntityName! : configuringEntityName;
        }
        else if (kind == RelationshipKind.ManyToMany)
        {
            principalEntity = configuringEntityName;
            dependentEntity = targetEntityName!;
        }
        else if (!isHasMany) // HasOne...WithMany
        {
            principalEntity = targetEntityName!;
            dependentEntity = configuringEntityName;
        }
        else // HasMany...WithOne
        {
            principalEntity = configuringEntityName;
            dependentEntity = targetEntityName!;
        }

        var configuringNav = TryReadNavigationName(call);
        var targetNav = TryReadNavigationName(withCall);

        string? principalNavigation;
        string? dependentNavigation;

        if (kind == RelationshipKind.OneToOne)
        {
            if (dependentEntity == configuringEntityName)
            {
                dependentNavigation = configuringNav;
                principalNavigation = targetNav;
            }
            else
            {
                dependentNavigation = targetNav;
                principalNavigation = configuringNav;
            }
        }
        else if (kind == RelationshipKind.ManyToMany)
        {
            principalNavigation = configuringNav;
            dependentNavigation = targetNav;
        }
        else if (!isHasMany) // HasOne...WithMany
        {
            dependentNavigation = configuringNav;
            principalNavigation = targetNav;
        }
        else // HasMany...WithOne
        {
            principalNavigation = configuringNav;
            dependentNavigation = targetNav;
        }

        IReadOnlyList<string> foreignKeyProperties = Array.Empty<string>();
        if (hasForeignKeyCall is not null)
        {
            var props = FluentSyntaxHelpers.TryReadPropertyNameList(hasForeignKeyCall);

            if (props is null)
            {
                diagnostics.Add(new Diagnostic(
                    DiagnosticCodes.UnreadableHasForeignKeyArgument,
                    "HasForeignKey argument(s) could not be read as property name(s).",
                    dependentEntity,
                    PropertyName: null,
                    hasForeignKeyCall.Span));
            }
            else
            {
                foreignKeyProperties = props;
            }
        }

        string? onDeleteBehavior = null;
        if (onDeleteCall is not null)
        {
            var arg = onDeleteCall.ArgumentList.Arguments.FirstOrDefault();

            if (arg?.Expression is MemberAccessExpressionSyntax { Name.Identifier.Text: var behaviorName })
            {
                onDeleteBehavior = behaviorName;
            }
            else
            {
                diagnostics.Add(new Diagnostic(
                    DiagnosticCodes.UnreadableOnDeleteArgument,
                    "OnDelete argument is not a DeleteBehavior member access and could not be read.",
                    dependentEntity,
                    PropertyName: null,
                    arg?.Span ?? onDeleteCall.Span));
            }
        }

        var joinEntityName = kind == RelationshipKind.ManyToMany ? TryGetGenericTypeArgument(usingEntityCall) : null;

        results.Add(new RelationshipConfig(
            principalEntity,
            dependentEntity,
            kind,
            principalNavigation,
            dependentNavigation,
            foreignKeyProperties,
            onDeleteBehavior,
            joinEntityName));
    }

    private static (string? EntityName, bool Resolved) ResolveRelatedEntity(
        InvocationExpressionSyntax call, string configuringEntityName, IReadOnlyList<EntityModel> entities)
    {
        var explicitTarget = TryGetGenericTypeArgument(call);
        if (explicitTarget is not null)
        {
            return (explicitTarget, true);
        }

        var navigationName = TryReadNavigationName(call);
        if (navigationName is null)
        {
            return (null, false);
        }

        var configuringEntity = entities.FirstOrDefault(e => e.Name == configuringEntityName);
        var property = configuringEntity?.Properties.FirstOrDefault(p => p.Name == navigationName);

        if (property is null)
        {
            return (null, false);
        }

        var elementTypeName = FluentSyntaxHelpers.TryGetElementTypeName(property.ClrType);

        return elementTypeName is null ? (null, false) : (elementTypeName, true);
    }

    private static string? TryReadNavigationName(InvocationExpressionSyntax call)
    {
        var argumentExpression = call.ArgumentList.Arguments
            .Select(a => a.Expression)
            .FirstOrDefault();

        return argumentExpression switch
        {
            SimpleLambdaExpressionSyntax { ExpressionBody: MemberAccessExpressionSyntax { Name.Identifier.Text: var name } } => name,
            ParenthesizedLambdaExpressionSyntax { ExpressionBody: MemberAccessExpressionSyntax { Name.Identifier.Text: var name } } => name,
            _ => null,
        };
    }

    private static string? GetInvokedMethodName(InvocationExpressionSyntax invocation)
    {
        return invocation.Expression is MemberAccessExpressionSyntax { Name: SimpleNameSyntax simpleName }
            ? simpleName.Identifier.Text
            : null;
    }

    private static string? TryGetGenericTypeArgument(InvocationExpressionSyntax? invocation)
    {
        return invocation?.Expression is MemberAccessExpressionSyntax { Name: GenericNameSyntax { TypeArgumentList.Arguments: [var typeArg] } }
            ? typeArg.ToString()
            : null;
    }

    private static void WalkRelationshipTailChain(InvocationExpressionSyntax withCall, Action<InvocationExpressionSyntax> visit)
    {
        SyntaxNode? cursor = withCall.Parent;

        while (cursor is not null && cursor is not StatementSyntax)
        {
            if (cursor is MemberAccessExpressionSyntax && cursor.Parent is InvocationExpressionSyntax invocation)
            {
                visit(invocation);
            }

            cursor = cursor.Parent;
        }
    }
```

Add `using System;` and `using EfSchemaVisualizer.Core.Model;` to the top of `FluentConfigParser.cs` if not already present.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~ParseRelationships_HasOneWithMany_BlockNested"`
Expected: PASS

- [ ] **Step 5: Write the next failing test — chained (no-lambda) style, same shape**

Append to `FluentConfigParserTests.cs`:

```csharp

    private const string SourceWithHasOneWithManyChained = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Order>()
                    .HasOne(d => d.Customer)
                    .WithMany(p => p.Orders)
                    .HasForeignKey(d => d.CustomerId);
            }
        }
        """;

    [Fact]
    public void ParseRelationships_HasOneWithMany_ChainedOffBareEntity_ResolvesOneToMany()
    {
        var result = new FluentConfigParser().ParseRelationships(SourceWithHasOneWithManyChained, OrderCustomerEntities);

        Assert.Empty(result.Diagnostics);
        var relationship = Assert.Single(result.Value);
        Assert.Equal(RelationshipKind.OneToMany, relationship.Kind);
        Assert.Equal("Customer", relationship.PrincipalEntity);
        Assert.Equal("Order", relationship.DependentEntity);
        Assert.Equal(new[] { "CustomerId" }, relationship.ForeignKeyProperties);
    }

    [Fact]
    public void ParseRelationships_HasManyWithOne_ResolvesOneToMany_PrincipalIsConfiguringEntity()
    {
        const string source = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Customer>(entity =>
                    {
                        entity.HasMany(p => p.Orders)
                              .WithOne(d => d.Customer)
                              .HasForeignKey(d => d.CustomerId);
                    });
                }
            }
            """;

        var result = new FluentConfigParser().ParseRelationships(source, OrderCustomerEntities);

        Assert.Empty(result.Diagnostics);
        var relationship = Assert.Single(result.Value);
        Assert.Equal(RelationshipKind.OneToMany, relationship.Kind);
        Assert.Equal("Customer", relationship.PrincipalEntity);
        Assert.Equal("Order", relationship.DependentEntity);
        Assert.Equal("Orders", relationship.PrincipalNavigation);
        Assert.Equal("Customer", relationship.DependentNavigation);
        Assert.Equal(new[] { "CustomerId" }, relationship.ForeignKeyProperties);
    }

    [Fact]
    public void ParseRelationships_BareWithMany_NoInverseNavigation_PrincipalNavigationIsNull()
    {
        const string source = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Order>(entity =>
                    {
                        entity.HasOne(d => d.Customer).WithMany();
                    });
                }
            }
            """;

        var result = new FluentConfigParser().ParseRelationships(source, OrderCustomerEntities);

        Assert.Empty(result.Diagnostics);
        var relationship = Assert.Single(result.Value);
        Assert.Null(relationship.PrincipalNavigation);
        Assert.Equal("Customer", relationship.DependentNavigation);
        Assert.Empty(relationship.ForeignKeyProperties);
    }

    [Fact]
    public void ParseRelationships_NoHasForeignKeyCall_ForeignKeyPropertiesEmpty_NoDiagnostic()
    {
        const string source = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Order>(entity =>
                    {
                        entity.HasOne(d => d.Customer).WithMany(p => p.Orders);
                    });
                }
            }
            """;

        var result = new FluentConfigParser().ParseRelationships(source, OrderCustomerEntities);

        Assert.Empty(result.Diagnostics);
        var relationship = Assert.Single(result.Value);
        Assert.Empty(relationship.ForeignKeyProperties);
    }

    [Fact]
    public void ParseRelationships_ExplicitGenericTarget_NoNavLambda_ResolvesEntity()
    {
        const string source = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Order>(entity =>
                    {
                        entity.HasOne<Customer>().WithMany();
                    });
                }
            }
            """;

        var result = new FluentConfigParser().ParseRelationships(source, OrderCustomerEntities);

        Assert.Empty(result.Diagnostics);
        var relationship = Assert.Single(result.Value);
        Assert.Equal("Customer", relationship.PrincipalEntity);
        Assert.Equal("Order", relationship.DependentEntity);
    }

    [Fact]
    public void ParseRelationships_MalformedChain_NoWithCall_SkippedSilently()
    {
        const string source = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Order>(entity =>
                    {
                        entity.HasOne(d => d.Customer);
                    });
                }
            }
            """;

        var result = new FluentConfigParser().ParseRelationships(source, OrderCustomerEntities);

        Assert.Empty(result.Diagnostics);
        Assert.Empty(result.Value);
    }

    [Fact]
    public void ParseRelationships_UnresolvableNavigation_EmitsUnresolvableRelationshipTargetDiagnostic()
    {
        const string source = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Order>(entity =>
                    {
                        entity.HasOne(d => d.NoSuchProperty).WithMany(p => p.Orders);
                    });
                }
            }
            """;

        var result = new FluentConfigParser().ParseRelationships(source, OrderCustomerEntities);

        Assert.Empty(result.Value);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.UnresolvableRelationshipTarget, diagnostic.Code);
        Assert.Equal("Order", diagnostic.EntityName);
    }

    [Fact]
    public void ParseRelationships_UnrecognizedCollectionWrapper_EmitsUnresolvableRelationshipTargetDiagnostic()
    {
        var entities = new List<EntityModel>
        {
            new("Customer", new List<PropertyModel>
            {
                new("Orders", "IQueryable<Order>", IsNullable: false, MaxLength: null),
            }),
            new("Order", new List<PropertyModel>()),
        };
        const string source = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Customer>(entity =>
                    {
                        entity.HasMany(p => p.Orders).WithOne();
                    });
                }
            }
            """;

        var result = new FluentConfigParser().ParseRelationships(source, entities);

        Assert.Empty(result.Value);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.UnresolvableRelationshipTarget, diagnostic.Code);
    }

    [Fact]
    public void ParseRelationships_UnreadableHasForeignKeyArgument_EmitsDiagnostic_RelationshipStillRecorded()
    {
        const string source = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Order>(entity =>
                    {
                        entity.HasOne(d => d.Customer).WithMany(p => p.Orders).HasForeignKey(GetFkExpression());
                    });
                }
            }
            """;

        var result = new FluentConfigParser().ParseRelationships(source, OrderCustomerEntities);

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.UnreadableHasForeignKeyArgument, diagnostic.Code);
        Assert.Equal("Order", diagnostic.EntityName);
        var relationship = Assert.Single(result.Value);
        Assert.Empty(relationship.ForeignKeyProperties);
    }

    [Fact]
    public void ParseRelationships_OnDelete_Present_IsRead()
    {
        const string source = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Order>(entity =>
                    {
                        entity.HasOne(d => d.Customer)
                              .WithMany(p => p.Orders)
                              .HasForeignKey(d => d.CustomerId)
                              .OnDelete(DeleteBehavior.Cascade);
                    });
                }
            }
            """;

        var result = new FluentConfigParser().ParseRelationships(source, OrderCustomerEntities);

        Assert.Empty(result.Diagnostics);
        var relationship = Assert.Single(result.Value);
        Assert.Equal("Cascade", relationship.OnDeleteBehavior);
    }

    [Fact]
    public void ParseRelationships_OnDelete_UnreadableArgument_EmitsDiagnostic()
    {
        const string source = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Order>(entity =>
                    {
                        entity.HasOne(d => d.Customer)
                              .WithMany(p => p.Orders)
                              .OnDelete(GetBehavior());
                    });
                }
            }
            """;

        var result = new FluentConfigParser().ParseRelationships(source, OrderCustomerEntities);

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.UnreadableOnDeleteArgument, diagnostic.Code);
        var relationship = Assert.Single(result.Value);
        Assert.Null(relationship.OnDeleteBehavior);
    }

    [Fact]
    public void ParseRelationships_HasForeignKeyAndOnDelete_OrderReversed_BothStillRead()
    {
        const string source = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Order>(entity =>
                    {
                        entity.HasOne(d => d.Customer)
                              .WithMany(p => p.Orders)
                              .OnDelete(DeleteBehavior.Restrict)
                              .HasForeignKey(d => d.CustomerId);
                    });
                }
            }
            """;

        var result = new FluentConfigParser().ParseRelationships(source, OrderCustomerEntities);

        Assert.Empty(result.Diagnostics);
        var relationship = Assert.Single(result.Value);
        Assert.Equal(new[] { "CustomerId" }, relationship.ForeignKeyProperties);
        Assert.Equal("Restrict", relationship.OnDeleteBehavior);
    }
```

- [ ] **Step 6: Run test to verify these fail for the expected reason**

Run: `dotnet test --filter "FullyQualifiedName~ParseRelationships"`
Expected: FAIL — the tests from Step 5 fail (implementation from Step 3 already covers most of this shape's logic, so check the failure messages are genuine assertion mismatches, not compile errors; if `GetFkExpression`/`GetBehavior` cause issues, note they're just placeholder method-call syntax inside the *EF-code-under-test string*, not real C# compiled by the test project — they never need to resolve, since `CSharpSyntaxTree.ParseText` only parses syntax, never binds symbols).

- [ ] **Step 7: Confirm all pass**

Run: `dotnet test --filter "FullyQualifiedName~ParseRelationships"`
Expected: PASS (12 tests)

- [ ] **Step 8: Run the full suite**

Run: `dotnet test`
Expected: all tests pass (239 total: 226 from Task 3 + 13 new [1 from step 1 + 12 from step 5]), 0 warnings.

- [ ] **Step 9: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs
git commit -m "Add FluentConfigParser.ParseRelationships: OneToMany (both configuring sides)"
```

---

### Task 5: Extend `ParseRelationships` for `OneToOne`

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs` (no changes expected — this task should be pure test coverage if Task 4's implementation is correct; see Step 3)
- Test: `tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs` (append)

**Interfaces:**
- Consumes: same `ParseRelationships`/`ParseRelationshipChain` from Task 4 — the `OneToOne` branches were written in Task 4's implementation but not yet exercised by a test.

- [ ] **Step 1: Write the failing tests**

Append to `FluentConfigParserTests.cs`:

```csharp

    private static readonly IReadOnlyList<EntityModel> PersonAddressEntities = new List<EntityModel>
    {
        new("Person", new List<PropertyModel>
        {
            new("Id", "int", IsNullable: false, MaxLength: null),
            new("Address", "Address", IsNullable: true, MaxLength: null),
        }),
        new("Address", new List<PropertyModel>
        {
            new("Id", "int", IsNullable: false, MaxLength: null),
            new("PersonId", "int", IsNullable: false, MaxLength: null),
            new("Person", "Person", IsNullable: false, MaxLength: null),
        }),
    };

    [Fact]
    public void ParseRelationships_HasOneWithOne_ExplicitForeignKeyGeneric_ResolvesDependent()
    {
        const string source = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Person>(entity =>
                    {
                        entity.HasOne(p => p.Address)
                              .WithOne(a => a.Person)
                              .HasForeignKey<Address>(a => a.PersonId);
                    });
                }
            }
            """;

        var result = new FluentConfigParser().ParseRelationships(source, PersonAddressEntities);

        Assert.Empty(result.Diagnostics);
        var relationship = Assert.Single(result.Value);
        Assert.Equal(RelationshipKind.OneToOne, relationship.Kind);
        Assert.Equal("Person", relationship.PrincipalEntity);
        Assert.Equal("Address", relationship.DependentEntity);
        Assert.Equal("Address", relationship.PrincipalNavigation);
        Assert.Equal("Person", relationship.DependentNavigation);
        Assert.Equal(new[] { "PersonId" }, relationship.ForeignKeyProperties);
    }

    [Fact]
    public void ParseRelationships_HasOneWithOne_NoExplicitGeneric_DefaultsDependentToConfiguringEntity()
    {
        const string source = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Address>(entity =>
                    {
                        entity.HasOne(a => a.Person)
                              .WithOne(p => p.Address)
                              .HasForeignKey(a => a.PersonId);
                    });
                }
            }
            """;

        var result = new FluentConfigParser().ParseRelationships(source, PersonAddressEntities);

        Assert.Empty(result.Diagnostics);
        var relationship = Assert.Single(result.Value);
        Assert.Equal(RelationshipKind.OneToOne, relationship.Kind);
        Assert.Equal("Address", relationship.DependentEntity);
        Assert.Equal("Person", relationship.PrincipalEntity);
        Assert.Equal("Person", relationship.DependentNavigation);
        Assert.Equal("Address", relationship.PrincipalNavigation);
    }
```

- [ ] **Step 2: Run test to verify current behavior**

Run: `dotnet test --filter "FullyQualifiedName~ParseRelationships_HasOneWithOne"`
Expected: PASS immediately, since Task 4 already implemented the `OneToOne` branches (the `kind` computation, dependent-resolution, and nav-assignment `if (kind == RelationshipKind.OneToOne)` branches in `ParseRelationshipChain`) even though no test exercised them yet.

If either test FAILs, treat it as a real bug in Task 4's `OneToOne` branches: re-read `ParseRelationshipChain`'s `OneToOne` handling (dependent = `TryGetGenericTypeArgument(hasForeignKeyCall) ?? configuringEntityName`; principal = whichever of {configuringEntityName, targetEntityName} isn't the dependent) and fix before proceeding — do not edit the test to match broken behavior.

- [ ] **Step 3: Run the full suite**

Run: `dotnet test`
Expected: all tests pass (241 total), 0 warnings.

- [ ] **Step 4: Commit**

```bash
git add tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs
git commit -m "Add test coverage for ParseRelationships OneToOne shape"
```

---

### Task 6: Extend `ParseRelationships` for `ManyToMany` and `UsingEntity`

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs` (no changes expected — see Step 3, same rationale as Task 5)
- Test: `tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs` (append)

- [ ] **Step 1: Write the failing tests**

Append to `FluentConfigParserTests.cs`:

```csharp

    private static readonly IReadOnlyList<EntityModel> PostTagEntities = new List<EntityModel>
    {
        new("Post", new List<PropertyModel>
        {
            new("Id", "int", IsNullable: false, MaxLength: null),
            new("Tags", "ICollection<Tag>", IsNullable: false, MaxLength: null),
        }),
        new("Tag", new List<PropertyModel>
        {
            new("Id", "int", IsNullable: false, MaxLength: null),
            new("Posts", "ICollection<Post>", IsNullable: false, MaxLength: null),
        }),
    };

    [Fact]
    public void ParseRelationships_HasManyWithMany_ResolvesManyToMany()
    {
        const string source = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Post>(entity =>
                    {
                        entity.HasMany(p => p.Tags).WithMany(t => t.Posts);
                    });
                }
            }
            """;

        var result = new FluentConfigParser().ParseRelationships(source, PostTagEntities);

        Assert.Empty(result.Diagnostics);
        var relationship = Assert.Single(result.Value);
        Assert.Equal(RelationshipKind.ManyToMany, relationship.Kind);
        Assert.Equal("Post", relationship.PrincipalEntity);
        Assert.Equal("Tag", relationship.DependentEntity);
        Assert.Equal("Tags", relationship.PrincipalNavigation);
        Assert.Equal("Posts", relationship.DependentNavigation);
        Assert.Empty(relationship.ForeignKeyProperties);
        Assert.Null(relationship.OnDeleteBehavior);
        Assert.Null(relationship.JoinEntityName);
    }

    [Fact]
    public void ParseRelationships_HasManyWithMany_ExplicitUsingEntityGeneric_SetsJoinEntityName()
    {
        const string source = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Post>(entity =>
                    {
                        entity.HasMany(p => p.Tags).WithMany(t => t.Posts).UsingEntity<PostTag>();
                    });
                }
            }
            """;

        var result = new FluentConfigParser().ParseRelationships(source, PostTagEntities);

        Assert.Empty(result.Diagnostics);
        var relationship = Assert.Single(result.Value);
        Assert.Equal("PostTag", relationship.JoinEntityName);
    }

    [Fact]
    public void ParseRelationships_HasManyWithMany_BareUsingEntity_JoinEntityNameNull_NoDiagnostic()
    {
        const string source = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Post>(entity =>
                    {
                        entity.HasMany(p => p.Tags).WithMany(t => t.Posts).UsingEntity(j => j.ToTable("PostTags"));
                    });
                }
            }
            """;

        var result = new FluentConfigParser().ParseRelationships(source, PostTagEntities);

        Assert.Empty(result.Diagnostics);
        var relationship = Assert.Single(result.Value);
        Assert.Null(relationship.JoinEntityName);
    }
```

- [ ] **Step 2: Run test to verify current behavior**

Run: `dotnet test --filter "FullyQualifiedName~ParseRelationships_HasManyWithMany"`
Expected: PASS immediately — Task 4's implementation already contains the `ManyToMany`/`UsingEntity` handling (`kind == RelationshipKind.ManyToMany` branches, and `joinEntityName = kind == RelationshipKind.ManyToMany ? TryGetGenericTypeArgument(usingEntityCall) : null`).

If either test FAILs, treat it as a real bug — fix `ParseRelationshipChain`'s `ManyToMany`/`usingEntityCall` handling, don't adjust the test.

- [ ] **Step 3: Run the full suite**

Run: `dotnet test`
Expected: all tests pass (244 total), 0 warnings.

- [ ] **Step 4: Commit**

```bash
git add tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs
git commit -m "Add test coverage for ParseRelationships ManyToMany shape and UsingEntity"
```

---

### Task 7: `ModelMerger.ApplyRelationships`

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/Parsing/ModelMerger.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Parsing/ModelMergerTests.cs` (append)

**Interfaces:**
- Consumes: `RelationshipConfig` (Task 1).
- Produces: `public static IReadOnlyList<RelationshipModel> ModelMerger.ApplyRelationships(IReadOnlyList<RelationshipConfig> configs)`.

- [ ] **Step 1: Write the failing tests**

Append to `tests/EfSchemaVisualizer.Core.Tests/Parsing/ModelMergerTests.cs`:

```csharp

    // ─── ApplyRelationships ─────────────────────────────────────────────────────

    [Fact]
    public void ApplyRelationships_MapsConfigsToModels_FieldForField()
    {
        var configs = new List<RelationshipConfig>
        {
            new("Customer", "Order", RelationshipKind.OneToMany,
                PrincipalNavigation: "Orders", DependentNavigation: "Customer",
                ForeignKeyProperties: new List<string> { "CustomerId" },
                OnDeleteBehavior: "Cascade"),
        };

        var result = ModelMerger.ApplyRelationships(configs);

        var relationship = Assert.Single(result);
        Assert.Equal("Customer", relationship.PrincipalEntity);
        Assert.Equal("Order", relationship.DependentEntity);
        Assert.Equal(RelationshipKind.OneToMany, relationship.Kind);
        Assert.Equal("Orders", relationship.PrincipalNavigation);
        Assert.Equal("Customer", relationship.DependentNavigation);
        Assert.Equal(new[] { "CustomerId" }, relationship.ForeignKeyProperties);
        Assert.Equal("Cascade", relationship.OnDeleteBehavior);
    }

    [Fact]
    public void ApplyRelationships_EmptyInput_ReturnsEmpty()
    {
        var result = ModelMerger.ApplyRelationships(new List<RelationshipConfig>());

        Assert.Empty(result);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~ApplyRelationships"`
Expected: FAIL — compile error, `ModelMerger.ApplyRelationships` does not exist.

- [ ] **Step 3: Implement `ApplyRelationships`**

Append to `src/EfSchemaVisualizer.Core/Parsing/ModelMerger.cs`, inside the `ModelMerger` class, after `ApplyDefaultValues`:

```csharp
    public static IReadOnlyList<RelationshipModel> ApplyRelationships(IReadOnlyList<RelationshipConfig> configs)
    {
        return configs
            .Select(c => new RelationshipModel(
                c.PrincipalEntity,
                c.DependentEntity,
                c.Kind,
                c.PrincipalNavigation,
                c.DependentNavigation,
                c.ForeignKeyProperties,
                c.OnDeleteBehavior,
                c.JoinEntityName))
            .ToList();
    }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~ApplyRelationships"`
Expected: PASS (2 tests)

- [ ] **Step 5: Run the full suite**

Run: `dotnet test`
Expected: all tests pass (246 total), 0 warnings.

- [ ] **Step 6: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Parsing/ModelMerger.cs tests/EfSchemaVisualizer.Core.Tests/Parsing/ModelMergerTests.cs
git commit -m "Add ModelMerger.ApplyRelationships"
```

---

### Task 8: Update backlog

**Files:**
- Modify: `docs/backlog.md`

- [ ] **Step 1: Check off the Relationships item**

In `docs/backlog.md`, replace:
```markdown
- [ ] **`[spec]` Relationships** — 1:1, 1:many, many:many (`HasOne`/`WithMany`/`HasForeignKey` etc.). Largest single item; likely its own plan.
```
with:
```markdown
- [x] **`[spec]` Relationships** — 1:1, 1:many, many:many (`HasOne`/`WithMany`/`HasForeignKey` etc.).
      **Update:** `FluentConfigParser.ParseRelationships` reads all four shapes
      (`HasOne`/`WithMany`, `HasMany`/`WithOne`, `HasOne`/`WithOne`,
      `HasMany`/`WithMany`), in both the block-nested and bare-`Entity<T>()`-chained
      styles, resolving the related entity via explicit generic type arguments or by
      cross-referencing navigation properties against the already-parsed
      `EntityModel`s; `ModelMerger.ApplyRelationships` maps the results into
      `RelationshipModel` (see
      `2026-07-12-has-relationships-config-design.md`). Parse + merge only — no
      rewriter yet (`SetRelationship`/`RemoveRelationship` deferred to a follow-up
      spec, since there's no diagram consumer yet to validate the write-back shape
      against). `UsingEntity`'s nested join-config, `HasPrincipalKey`, data-annotation
      attributes, and redundant both-sides configuration remain out of scope.
```

- [ ] **Step 2: Run the full suite one last time**

Run: `dotnet test`
Expected: all tests pass (246 total), 0 warnings.

- [ ] **Step 3: Commit**

```bash
git add docs/backlog.md
git commit -m "Check off Priority 2 relationships backlog item (parse+merge)"
```
