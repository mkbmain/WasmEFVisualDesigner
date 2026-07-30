# `UsingEntity` Nested Join-Entity Configuration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Read, model, render, and round-trip-preserve every `UsingEntity(...)` overload shape (generic or string-named join entity; bare, single join-config lambda, two per-side FK lambdas, or three-lambda combination), and stop `SetRelationshipShape` from silently destroying hand-written `UsingEntity` config on unrelated relationship edits.

**Architecture:** Reuse the existing "treat a builder lambda as its own configuration scope" mechanism (already used for `OwnsOne`/`OwnsMany`/`ComplexProperty`) so every existing per-property parser picks up join-entity-wide config (`HasKey`, `HasColumnName`, `Property<T>(...)`, ...) with zero new extractor code. Add one small dedicated extractor for the two per-side FK lambdas. Add a new `EntityModel.IsSharedType` flag and synthesize a bare `EntityModel` for a string-named join entity by seeding it into the entity list before the existing merge pipeline runs, so every existing merger applies to it for free. Fix the rewriter's "destroy and rebuild" edit path to capture and re-attach the existing `UsingEntity(...)` call's arguments verbatim.

**Tech Stack:** C# / Roslyn (`Microsoft.CodeAnalysis.CSharp`), xUnit.

## Global Constraints

- Spec: `docs/superpowers/specs/2026-07-30-using-entity-design.md` — read it before starting; every task below implements one piece of it.
- Follow existing code style exactly: `record` types with `init`-backed defaulted `IReadOnlyList` properties, `ParseResult<T>` wrapper for parser methods that report diagnostics, diagnostics added via `DiagnosticCodes` constants (never inline strings).
- Every new/changed public method must have an XML-doc-free but clear one-line `///` comment only where the *why* isn't obvious from the name — match the surrounding file's comment density, don't over-comment.
- No new `RelationshipKind` — many-to-many with a shared-type join entity is still `RelationshipKind.ManyToMany`.
- Run `dotnet test` from the repo root (`/root/RiderProjects/WasmEFVisualDesigner`) after every task; all pre-existing tests must stay green in addition to the new ones.

---

### Task 1: Model fields — `EntityModel.IsSharedType`, relationship join-entity fields, diagnostic code

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/Model/EntityModel.cs`
- Modify: `src/EfSchemaVisualizer.Core/Model/RelationshipModel.cs`
- Modify: `src/EfSchemaVisualizer.Core/Merging/RelationshipConfig.cs`
- Modify: `src/EfSchemaVisualizer.Core/Parsing/DiagnosticCodes.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Model/EntityModelTests.cs` (create if it doesn't already exist — check first with `find tests -iname "EntityModelTests.cs"`; if absent, add the assertions to `tests/EfSchemaVisualizer.Core.Tests/Merging/ModelMergerTests.cs` instead, in a new `[Fact]`, since that file already constructs `EntityModel`/`RelationshipModel` instances directly)

**Interfaces:**
- Produces: `EntityModel.IsSharedType` (`bool`, default `false`); `RelationshipModel.JoinEntityIsSharedType` (`bool`, default `false`), `RelationshipModel.JoinEntityRightForeignKey` / `JoinEntityLeftForeignKey` (`IReadOnlyList<string>`, default empty, same `init`-backed-property pattern as `ForeignKeyProperties`); identical three fields on `RelationshipConfig`; `DiagnosticCodes.UnreadableUsingEntityForeignKeyArgument`.

- [ ] **Step 1: Write the failing test**

```csharp
// In tests/EfSchemaVisualizer.Core.Tests/Merging/ModelMergerTests.cs
[Fact]
public void RelationshipModel_JoinEntityFields_DefaultToFalseAndEmpty()
{
    var relationship = new RelationshipModel("Post", "Tag", RelationshipKind.ManyToMany, null, null);

    Assert.False(relationship.JoinEntityIsSharedType);
    Assert.Empty(relationship.JoinEntityRightForeignKey);
    Assert.Empty(relationship.JoinEntityLeftForeignKey);

    var withValues = relationship with
    {
        JoinEntityIsSharedType = true,
        JoinEntityRightForeignKey = new List<string> { "TagId" },
        JoinEntityLeftForeignKey = new List<string> { "PostId" },
    };

    Assert.True(withValues.JoinEntityIsSharedType);
    Assert.Equal(new List<string> { "TagId" }, withValues.JoinEntityRightForeignKey);
    Assert.Equal(new List<string> { "PostId" }, withValues.JoinEntityLeftForeignKey);
}

[Fact]
public void EntityModel_IsSharedType_DefaultsToFalse()
{
    var entity = new EntityModel("PostTag", new List<PropertyModel>());

    Assert.False(entity.IsSharedType);

    var shared = entity with { IsSharedType = true };
    Assert.True(shared.IsSharedType);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~RelationshipModel_JoinEntityFields_DefaultToFalseAndEmpty|FullyQualifiedName~EntityModel_IsSharedType_DefaultsToFalse"`
Expected: FAIL with a compile error (`'JoinEntityIsSharedType' does not exist`, `'IsSharedType' does not exist`, etc.)

- [ ] **Step 3: Add the fields**

In `src/EfSchemaVisualizer.Core/Model/EntityModel.cs`, add `bool IsSharedType = false` as the last parameter of the `EntityModel` record's primary constructor (after `string? DiscriminatorValue = null`):

```csharp
public sealed record EntityModel(
    string Name,
    IReadOnlyList<PropertyModel> Properties,
    IReadOnlyList<string>? KeyPropertyNames = null,
    IReadOnlyList<IndexModel>? Indexes = null,
    string? TableName = null,
    string? Schema = null,
    bool IsKeyless = false,
    bool IsKeyInferred = false,
    string? ViewName = null,
    string? SqlQuery = null,
    IReadOnlyList<IReadOnlyList<string>>? AlternateKeys = null,
    bool HasQueryFilter = false,
    string? Comment = null,
    bool IsJson = false,
    string? JsonColumnName = null,
    bool IsTemporal = false,
    IReadOnlyList<string>? SplitTables = null,
    string? BaseEntityName = null,
    bool IsOwned = false,
    string? KeyName = null,
    IReadOnlyList<CheckConstraintModel>? CheckConstraints = null,
    MappingStrategy MappingStrategy = MappingStrategy.Tph,
    string? DiscriminatorPropertyName = null,
    string? DiscriminatorClrType = null,
    string? DiscriminatorValue = null,
    bool IsSharedType = false)
```

In `src/EfSchemaVisualizer.Core/Model/RelationshipModel.cs`, add the three new fields after `PrincipalKeyProperties`, and back the two lists with the same `init`-backed-default pattern `ForeignKeyProperties` already uses:

```csharp
public sealed record RelationshipModel(
    string PrincipalEntity,
    string DependentEntity,
    RelationshipKind Kind,
    string? PrincipalNavigation,
    string? DependentNavigation,
    IReadOnlyList<string>? ForeignKeyProperties = null,
    string? OnDeleteBehavior = null,
    string? JoinEntityName = null,
    bool IsInferred = false,
    string? ConstraintName = null,
    IReadOnlyList<string>? PrincipalKeyProperties = null,
    bool JoinEntityIsSharedType = false,
    IReadOnlyList<string>? JoinEntityRightForeignKey = null,
    IReadOnlyList<string>? JoinEntityLeftForeignKey = null)
{
    public IReadOnlyList<string> ForeignKeyProperties { get; init; } = ForeignKeyProperties ?? new List<string>();
    public IReadOnlyList<string> PrincipalKeyProperties { get; init; } = PrincipalKeyProperties ?? new List<string>();
    public IReadOnlyList<string> JoinEntityRightForeignKey { get; init; } = JoinEntityRightForeignKey ?? new List<string>();
    public IReadOnlyList<string> JoinEntityLeftForeignKey { get; init; } = JoinEntityLeftForeignKey ?? new List<string>();
}
```

In `src/EfSchemaVisualizer.Core/Merging/RelationshipConfig.cs`, make the identical addition:

```csharp
public sealed record RelationshipConfig(
    string PrincipalEntity,
    string DependentEntity,
    RelationshipKind Kind,
    string? PrincipalNavigation,
    string? DependentNavigation,
    IReadOnlyList<string>? ForeignKeyProperties = null,
    string? OnDeleteBehavior = null,
    string? JoinEntityName = null,
    string? ConstraintName = null,
    IReadOnlyList<string>? PrincipalKeyProperties = null,
    bool JoinEntityIsSharedType = false,
    IReadOnlyList<string>? JoinEntityRightForeignKey = null,
    IReadOnlyList<string>? JoinEntityLeftForeignKey = null)
{
    public IReadOnlyList<string> ForeignKeyProperties { get; init; } = ForeignKeyProperties ?? new List<string>();
    public IReadOnlyList<string> PrincipalKeyProperties { get; init; } = PrincipalKeyProperties ?? new List<string>();
    public IReadOnlyList<string> JoinEntityRightForeignKey { get; init; } = JoinEntityRightForeignKey ?? new List<string>();
    public IReadOnlyList<string> JoinEntityLeftForeignKey { get; init; } = JoinEntityLeftForeignKey ?? new List<string>();
}
```

In `src/EfSchemaVisualizer.Core/Parsing/DiagnosticCodes.cs`, add one new constant, next to `UnreadableHasPrincipalKeyArgument`:

```csharp
public const string UnreadableUsingEntityForeignKeyArgument = nameof(UnreadableUsingEntityForeignKeyArgument);
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~RelationshipModel_JoinEntityFields_DefaultToFalseAndEmpty|FullyQualifiedName~EntityModel_IsSharedType_DefaultsToFalse"`
Expected: PASS

- [ ] **Step 5: Run the full test suite to confirm no positional-constructor breakage**

Run: `dotnet test`
Expected: PASS (all existing tests green — every existing `RelationshipModel`/`RelationshipConfig`/`EntityModel` construction in the codebase uses named arguments for anything past the first 5 positional parameters, so appending new trailing optional parameters is source-compatible)

- [ ] **Step 6: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Model/EntityModel.cs src/EfSchemaVisualizer.Core/Model/RelationshipModel.cs src/EfSchemaVisualizer.Core/Merging/RelationshipConfig.cs src/EfSchemaVisualizer.Core/Parsing/DiagnosticCodes.cs tests/EfSchemaVisualizer.Core.Tests/Merging/ModelMergerTests.cs
git commit -m "Add IsSharedType/join-entity-FK model fields for UsingEntity support"
```

---

### Task 2: `FluentSyntaxHelpers` — join-entity identity, lambda extraction, nested scopes, opaque-boundary fix

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/Parsing/FluentSyntaxHelpers.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentSyntaxHelpersTests.cs` (check with `find tests -iname "FluentSyntaxHelpersTests.cs"`; if it doesn't exist, add these as `[Fact]`s directly in `tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs`, since `FluentSyntaxHelpers` is `internal` and that test assembly already has `InternalsVisibleTo` access — confirm via the `[assembly: InternalsVisibleTo(...)]` line at the top of `FluentSyntaxHelpers.cs`)

**Interfaces:**
- Consumes: nothing new from Task 1 directly (this task is pure Roslyn-syntax plumbing).
- Produces: `FluentSyntaxHelpers.TryGetGenericTypeArgument(InvocationExpressionSyntax? invocation) -> string?` (moved here from `FluentConfigParser`, now `internal static` instead of `private static`); `FluentSyntaxHelpers.TryReadUsingEntityStringName(InvocationExpressionSyntax? usingEntityCall) -> string?`; `FluentSyntaxHelpers.GetUsingEntityLambdaArguments(InvocationExpressionSyntax usingEntityCall) -> IReadOnlyList<AnonymousFunctionExpressionSyntax>`; `FluentSyntaxHelpers.FindUsingEntityNestedScopes(CompilationUnitSyntax root) -> IEnumerable<(string EntityName, SyntaxNode Scope)>`; `FindConfigurationScopes` now also yields these. `FindAllCalls`'s internal `Walk` treats all of a `UsingEntity` call's lambda arguments as an opaque boundary (not just the OwnsOne/OwnsMany/ComplexProperty builder lambda), so a nested `HasOne(...).WithMany(...)` inside a per-side FK lambda is never misread as a new top-level relationship on the outer entity.

This task fixes a latent bug: today, `FindAllCalls`'s walk descends into *any* lambda argument except the two cases it explicitly special-cases (a nested `Entity<T>()` call, and an `OwnsOne`/`OwnsMany`/`ComplexProperty` builder lambda). A `UsingEntity<T>(right => right.HasOne<Foo>().WithMany()..., left => ...)` call's FK lambdas are walked into today with no boundary at all, so `ParseRelationships`'s outer `FindCallsNamed(scope, "HasOne").Concat(FindCallsNamed(scope, "HasMany"))` scan already picks up the nested `HasOne<Foo>().WithMany()` chain and misreads it as a second, spurious relationship on the *outer* entity. Step 1 below reproduces this as a failing test before any other change.

- [ ] **Step 1: Write the failing test proving the pre-existing phantom-relationship bug**

```csharp
// In tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs
private const string SourceUsingEntityTwoLambdaForeignKeys = """
    public class AppDbContext : DbContext
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Post>(entity =>
            {
                entity.HasMany(p => p.Tags).WithMany(t => t.Posts).UsingEntity<PostTag>(
                    right => right.HasOne<Tag>().WithMany().HasForeignKey("TagId"),
                    left => left.HasOne<Post>().WithMany().HasForeignKey("PostId"));
            });
        }
    }
    """;

[Fact]
public void ParseRelationships_UsingEntityTwoLambdaForeignKeys_DoesNotProduceAPhantomRelationship()
{
    var result = new FluentConfigParser().ParseRelationships(SourceUsingEntityTwoLambdaForeignKeys, PostTagEntities);

    // Exactly one relationship: Post<->Tag many-to-many. The HasOne<Tag>()/HasOne<Post>() calls
    // nested inside UsingEntity's per-side FK lambdas must NOT be read as separate relationships.
    var relationship = Assert.Single(result.Value);
    Assert.Equal(RelationshipKind.ManyToMany, relationship.Kind);
    Assert.Equal("Post", relationship.PrincipalEntity);
    Assert.Equal("Tag", relationship.DependentEntity);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~ParseRelationships_UsingEntityTwoLambdaForeignKeys_DoesNotProduceAPhantomRelationship"`
Expected: FAIL — `Assert.Single` throws because `result.Value` contains 2 relationships (the real Post/Tag many-to-many, plus a phantom one synthesized from the nested `HasOne<Tag>().WithMany()` inside the right-side lambda).

- [ ] **Step 3: Move `TryGetGenericTypeArgument` into `FluentSyntaxHelpers`**

In `src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs`, delete the private method (currently at the bottom of the file, right after `ResolveRelatedEntity`):

```csharp
private static string? TryGetGenericTypeArgument(InvocationExpressionSyntax? invocation)
{
    return invocation?.Expression is MemberAccessExpressionSyntax { Name: GenericNameSyntax { TypeArgumentList.Arguments: [var typeArg] } }
        ? typeArg.ToString()
        : null;
}
```

Update its two call sites in the same file (inside `ParseRelationshipChain`) to call `FluentSyntaxHelpers.TryGetGenericTypeArgument(...)` instead:

```csharp
// was: var explicitDependent = TryGetGenericTypeArgument(hasForeignKeyCall);
var explicitDependent = FluentSyntaxHelpers.TryGetGenericTypeArgument(hasForeignKeyCall);
```

(The second call site, involving `usingEntityCall`, is rewritten entirely in Task 3 — no need to touch it here.)

In `src/EfSchemaVisualizer.Core/Parsing/FluentSyntaxHelpers.cs`, add the method (near `TryGetElementTypeName`, or any other small standalone syntax helper):

```csharp
internal static string? TryGetGenericTypeArgument(InvocationExpressionSyntax? invocation)
{
    return invocation?.Expression is MemberAccessExpressionSyntax { Name: GenericNameSyntax { TypeArgumentList.Arguments: [var typeArg] } }
        ? typeArg.ToString()
        : null;
}
```

- [ ] **Step 4: Add `TryReadUsingEntityStringName` and `GetUsingEntityLambdaArguments`**

In `src/EfSchemaVisualizer.Core/Parsing/FluentSyntaxHelpers.cs`, add right after `TryGetGenericTypeArgument`:

```csharp
/// Reads the string-literal join-entity name from a shared-type `UsingEntity("Name", ...)` call's
/// first argument. Returns null for the generic `UsingEntity<T>(...)` form (whose identity is a
/// type argument on the method name, not a call argument at all) or when no `UsingEntity` call is
/// present.
internal static string? TryReadUsingEntityStringName(InvocationExpressionSyntax? usingEntityCall)
{
    return usingEntityCall?.ArgumentList.Arguments.FirstOrDefault()?.Expression is LiteralExpressionSyntax
        { RawKind: (int)SyntaxKind.StringLiteralExpression } literal
        ? literal.Token.ValueText
        : null;
}

/// Returns every lambda-typed argument of a `UsingEntity(...)` call, in argument order — 0 (bare),
/// 1 (join-entity-wide config), 2 (per-side FK), or 3 (per-side FK + join-entity-wide config).
/// `OfType&lt;AnonymousFunctionExpressionSyntax&gt;` already skips a leading string-literal
/// join-entity-name argument (the shared-type overloads), so no separate "skip argument 0" logic is
/// needed here — unlike `TryGetFoldingBuilderLambda`, whose nav-selector argument can itself be a
/// lambda and must be skipped by position instead.
internal static IReadOnlyList<AnonymousFunctionExpressionSyntax> GetUsingEntityLambdaArguments(InvocationExpressionSyntax usingEntityCall)
{
    return usingEntityCall.ArgumentList.Arguments
        .Select(a => a.Expression)
        .OfType<AnonymousFunctionExpressionSyntax>()
        .ToList();
}
```

- [ ] **Step 5: Write failing tests for the two new helpers directly**

```csharp
// In tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs

private static InvocationExpressionSyntax GetUsingEntityCall(string source)
{
    var root = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot();
    return root.DescendantNodes()
        .OfType<InvocationExpressionSyntax>()
        .Single(i => i.Expression is MemberAccessExpressionSyntax { Name.Identifier.Text: "UsingEntity" });
}

[Fact]
public void TryReadUsingEntityStringName_GenericForm_ReturnsNull()
{
    var call = GetUsingEntityCall("""
        class C { void M() { x.HasMany(y).WithMany(z).UsingEntity<PostTag>(); } }
        """);

    Assert.Null(FluentSyntaxHelpers.TryReadUsingEntityStringName(call));
}

[Fact]
public void TryReadUsingEntityStringName_StringForm_ReturnsLiteral()
{
    var call = GetUsingEntityCall("""
        class C { void M() { x.HasMany(y).WithMany(z).UsingEntity("PostTags"); } }
        """);

    Assert.Equal("PostTags", FluentSyntaxHelpers.TryReadUsingEntityStringName(call));
}

[Fact]
public void GetUsingEntityLambdaArguments_ThreeLambdaForm_ReturnsAllThreeInOrder()
{
    var call = GetUsingEntityCall("""
        class C
        {
            void M()
            {
                x.HasMany(y).WithMany(z).UsingEntity<PostTag>(
                    right => right.HasOne<Tag>(),
                    left => left.HasOne<Post>(),
                    j => j.HasKey("A", "B"));
            }
        }
        """);

    var lambdas = FluentSyntaxHelpers.GetUsingEntityLambdaArguments(call);

    Assert.Equal(3, lambdas.Count);
}

[Fact]
public void GetUsingEntityLambdaArguments_StringNameAndOneLambda_SkipsTheStringLiteral()
{
    var call = GetUsingEntityCall("""
        class C
        {
            void M()
            {
                x.HasMany(y).WithMany(z).UsingEntity("PostTags", j => j.HasKey("A", "B"));
            }
        }
        """);

    var lambdas = FluentSyntaxHelpers.GetUsingEntityLambdaArguments(call);

    Assert.Single(lambdas);
}
```

Note: `FluentSyntaxHelpers` is `internal`, and `[assembly: InternalsVisibleTo("EfSchemaVisualizer.Core.Tests")]` at the top of the file already grants the test assembly access — no extra project reference needed.

- [ ] **Step 6: Run tests to verify they fail then pass**

Run: `dotnet test --filter "FullyQualifiedName~TryReadUsingEntityStringName|FullyQualifiedName~GetUsingEntityLambdaArguments"`
Expected: FAIL first (methods referenced don't exist yet in this exact form until Step 4 lands — if Step 4 was already applied, these should already PASS; if so, re-run after confirming Step 4's code is saved). After Step 4's code is in place: PASS.

- [ ] **Step 7: Add `FindUsingEntityNestedScopes` and wire it into `FindConfigurationScopes`**

In `src/EfSchemaVisualizer.Core/Parsing/FluentSyntaxHelpers.cs`, add near `FindOwnedAndComplexNestedScopes`:

```csharp
/// For every `UsingEntity(...)` call found anywhere in the file, resolves its join entity's name
/// (generic type argument, or shared-type string literal) and — when the call has exactly one
/// lambda argument (join-entity-wide config) or exactly three (per-side FK + join-entity-wide
/// config, where the third is the join-entity one) — yields that lambda itself as a configuration
/// scope keyed by the join entity's name. Every existing per-property `Parse*` method then reads
/// from it via `FindCallsNamed(scope, ...)` with zero extractor changes, the same reuse this
/// project already uses for `OwnsOne`/`OwnsMany`/`ComplexProperty` builder lambdas.
///
/// Unlike that owned/complex-type precedent, the lambda is yielded whether it's block-bodied
/// (`j => { ... }`) or expression-bodied (`j => j.HasKey(...)`) — `FindCallsNamed`'s underlying
/// walk only needs some `SyntaxNode` to recurse into, and a single-call expression body is the
/// common real-world shape for this particular call.
internal static IEnumerable<(string EntityName, SyntaxNode Scope)> FindUsingEntityNestedScopes(CompilationUnitSyntax root)
{
    foreach (var call in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
    {
        if (call.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "UsingEntity" })
        {
            continue;
        }

        var joinEntityName = TryGetGenericTypeArgument(call) ?? TryReadUsingEntityStringName(call);
        if (joinEntityName is null)
        {
            continue;
        }

        var lambdas = GetUsingEntityLambdaArguments(call);
        AnonymousFunctionExpressionSyntax? joinConfigLambda = lambdas.Count switch
        {
            1 => lambdas[0],
            3 => lambdas[2],
            _ => null,
        };

        if (joinConfigLambda is not null)
        {
            yield return (joinEntityName, joinConfigLambda);
        }
    }
}
```

Update `FindConfigurationScopes` (a few lines above `FindOwnedAndComplexNestedScopes`) to also yield these, unconditionally (no `entities` dependency, unlike the owned/complex case):

```csharp
internal static IEnumerable<(string EntityName, SyntaxNode Scope)> FindConfigurationScopes(
    CompilationUnitSyntax root, IReadOnlyList<EntityModel>? entities = null)
{
    foreach (var scope in FindConfigurationScopesCore(root))
    {
        yield return scope;
    }

    foreach (var nested in FindUsingEntityNestedScopes(root))
    {
        yield return nested;
    }

    if (entities is not null)
    {
        foreach (var nested in FindOwnedAndComplexNestedScopes(root, entities))
        {
            yield return nested;
        }
    }
}
```

- [ ] **Step 8: Write a failing test proving the join-config lambda is now readable as a scope**

```csharp
// In tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs
private const string SourceUsingEntitySingleConfigLambda = """
    public class AppDbContext : DbContext
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Post>(entity =>
            {
                entity.HasMany(p => p.Tags).WithMany(t => t.Posts).UsingEntity<PostTag>(
                    j => j.HasKey("PostId", "TagId"));
            });
        }
    }
    """;

[Fact]
public void ParseKeys_UsingEntitySingleConfigLambda_ReadsJoinEntityHasKey()
{
    var result = new FluentConfigParser().ParseKeys(SourceUsingEntitySingleConfigLambda);

    Assert.Empty(result.Diagnostics);
    var key = Assert.Single(result.Value, k => k.EntityName == "PostTag");
    Assert.Equal(new List<string> { "PostId", "TagId" }, key.PropertyNames);
}
```

- [ ] **Step 9: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~ParseKeys_UsingEntitySingleConfigLambda_ReadsJoinEntityHasKey"`
Expected: FAIL — `ParseKeys` finds no `HasKey` for `PostTag` because `FindConfigurationScopes` doesn't yield the `UsingEntity` lambda as a scope yet (this step's code from Step 7 hasn't landed until you save it — if already saved, this should already pass; run it now to confirm).

- [ ] **Step 10: Run test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~ParseKeys_UsingEntitySingleConfigLambda_ReadsJoinEntityHasKey"`
Expected: PASS

- [ ] **Step 11: Generalize the opaque-boundary logic in `FindAllCalls`'s `Walk`**

This is the fix for the phantom-relationship bug from Step 1. In `src/EfSchemaVisualizer.Core/Parsing/FluentSyntaxHelpers.cs`, replace the private `FindAllCalls` method entirely:

```csharp
private static IEnumerable<InvocationExpressionSyntax> FindAllCalls(SyntaxNode scope)
{
    var results = new List<InvocationExpressionSyntax>();
    Walk(scope);
    return results;

    void Walk(SyntaxNode node, IReadOnlyList<SyntaxNode>? excludeSubtrees = null)
    {
        foreach (var child in node.ChildNodes())
        {
            if (excludeSubtrees is not null && excludeSubtrees.Contains(child))
            {
                // Opaque boundary for an owned/complex builder lambda or a UsingEntity lambda
                // argument: `excludeSubtrees` entries may be nested several levels below `node`
                // (inside the invocation's ArgumentList), not a direct child, so this check must
                // apply at every depth of the recursive walk below, not just immediate children.
                continue;
            }

            if (child is InvocationExpressionSyntax nestedEntityInvocation
                && GetConfiguredEntityName(nestedEntityInvocation) is not null)
            {
                // Opaque boundary: don't descend into a nested Entity<> configuration's subtree,
                // and the invocation node itself is never needed by any caller of FindAllCalls.
                continue;
            }

            if (child is InvocationExpressionSyntax invocation)
            {
                results.Add(invocation);

                var opaqueLambdas = GetOpaqueLambdaArguments(invocation);
                if (opaqueLambdas.Count > 0)
                {
                    // Opaque boundary for the lambda argument subtree(s) only: the invocation
                    // itself IS added to results above (so e.g. FindCallsNamed(scope, "OwnsOne")
                    // or FindCallsNamed(scope, "UsingEntity") still matches it directly), but the
                    // lambda bodies are skipped here — either because FindConfigurationScopes
                    // yields them as their own scope elsewhere (double-counting risk), or because
                    // (UsingEntity's per-side FK lambdas specifically) their nested
                    // HasOne(...).WithMany(...) chain must never be misattributed to the outer
                    // entity's own relationship scan.
                    Walk(child, opaqueLambdas);
                    continue;
                }
            }

            Walk(child, excludeSubtrees);
        }
    }
}

/// Returns the lambda argument(s) of an invocation whose bodies must not be walked as part of the
/// enclosing scope: the single builder lambda for `OwnsOne`/`OwnsMany`/`ComplexProperty`, or all of
/// a `UsingEntity(...)` call's lambda arguments (however many — 0 to 3). Empty for everything else.
private static IReadOnlyList<AnonymousFunctionExpressionSyntax> GetOpaqueLambdaArguments(InvocationExpressionSyntax invocation)
{
    if (TryGetFoldingBuilderLambda(invocation) is { } foldingLambda)
    {
        return new[] { foldingLambda };
    }

    if (invocation.Expression is MemberAccessExpressionSyntax { Name.Identifier.Text: "UsingEntity" })
    {
        return GetUsingEntityLambdaArguments(invocation);
    }

    return Array.Empty<AnonymousFunctionExpressionSyntax>();
}
```

(`IReadOnlyList<AnonymousFunctionExpressionSyntax>` is covariant and assignable directly to the `IReadOnlyList<SyntaxNode>? excludeSubtrees` parameter — no cast needed.)

- [ ] **Step 12: Run the phantom-relationship test from Step 1 again**

Run: `dotnet test --filter "FullyQualifiedName~ParseRelationships_UsingEntityTwoLambdaForeignKeys_DoesNotProduceAPhantomRelationship"`
Expected: PASS

- [ ] **Step 13: Run the full test suite**

Run: `dotnet test`
Expected: PASS (in particular, re-verify every existing `OwnsOne`/`OwnsMany`/`ComplexProperty` test still passes, since `FindAllCalls`'s core logic changed — the behavior for those three is unchanged, only generalized from "one excluded node" to "a list of excluded nodes")

- [ ] **Step 14: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Parsing/FluentSyntaxHelpers.cs src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs
git commit -m "Add UsingEntity scope discovery and fix nested-FK-lambda opaque boundary"
```

---

### Task 3: `FluentConfigParser` — join-entity identity + per-side FK extraction

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs`

**Interfaces:**
- Consumes: `FluentSyntaxHelpers.TryGetGenericTypeArgument`, `TryReadUsingEntityStringName`, `GetUsingEntityLambdaArguments` (Task 2); `RelationshipConfig.JoinEntityIsSharedType`/`JoinEntityRightForeignKey`/`JoinEntityLeftForeignKey` (Task 1).
- Produces: `ParseRelationships` now populates the three new `RelationshipConfig` fields for every `UsingEntity` shape.

- [ ] **Step 1: Write failing tests for every remaining shape**

```csharp
// In tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs

[Fact]
public void ParseRelationships_UsingEntityStringName_SetsJoinEntityNameAndIsSharedType()
{
    const string source = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Post>(entity =>
                {
                    entity.HasMany(p => p.Tags).WithMany(t => t.Posts).UsingEntity("PostTags");
                });
            }
        }
        """;

    var result = new FluentConfigParser().ParseRelationships(source, PostTagEntities);

    var relationship = Assert.Single(result.Value);
    Assert.Equal("PostTags", relationship.JoinEntityName);
    Assert.True(relationship.JoinEntityIsSharedType);
}

[Fact]
public void ParseRelationships_UsingEntityGenericSingleConfigLambda_JoinEntityIsSharedTypeIsFalse()
{
    const string source = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Post>(entity =>
                {
                    entity.HasMany(p => p.Tags).WithMany(t => t.Posts).UsingEntity<PostTag>(
                        j => j.HasKey("PostId", "TagId"));
                });
            }
        }
        """;

    var result = new FluentConfigParser().ParseRelationships(source, PostTagEntities);

    var relationship = Assert.Single(result.Value);
    Assert.Equal("PostTag", relationship.JoinEntityName);
    Assert.False(relationship.JoinEntityIsSharedType);
}

[Fact]
public void ParseRelationships_UsingEntityTwoLambdaForeignKeys_ReadsRightAndLeftForeignKeys()
{
    const string source = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Post>(entity =>
                {
                    entity.HasMany(p => p.Tags).WithMany(t => t.Posts).UsingEntity<PostTag>(
                        right => right.HasOne<Tag>().WithMany().HasForeignKey("TagId"),
                        left => left.HasOne<Post>().WithMany().HasForeignKey("PostId"));
                });
            }
        }
        """;

    var result = new FluentConfigParser().ParseRelationships(source, PostTagEntities);

    Assert.Empty(result.Diagnostics);
    var relationship = Assert.Single(result.Value);
    Assert.Equal(new List<string> { "TagId" }, relationship.JoinEntityRightForeignKey);
    Assert.Equal(new List<string> { "PostId" }, relationship.JoinEntityLeftForeignKey);
}

[Fact]
public void ParseRelationships_UsingEntityThreeLambdaForm_ReadsForeignKeysAndJoinConfig()
{
    const string source = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Post>(entity =>
                {
                    entity.HasMany(p => p.Tags).WithMany(t => t.Posts).UsingEntity<PostTag>(
                        right => right.HasOne<Tag>().WithMany().HasForeignKey("TagId"),
                        left => left.HasOne<Post>().WithMany().HasForeignKey("PostId"),
                        j => j.HasKey("PostId", "TagId"));
                });
            }
        }
        """;

    var relationships = new FluentConfigParser().ParseRelationships(source, PostTagEntities);
    var relationship = Assert.Single(relationships.Value);
    Assert.Equal(new List<string> { "TagId" }, relationship.JoinEntityRightForeignKey);
    Assert.Equal(new List<string> { "PostId" }, relationship.JoinEntityLeftForeignKey);

    var keys = new FluentConfigParser().ParseKeys(source);
    var key = Assert.Single(keys.Value, k => k.EntityName == "PostTag");
    Assert.Equal(new List<string> { "PostId", "TagId" }, key.PropertyNames);
}

[Fact]
public void ParseRelationships_UsingEntityStringNameThreeLambdaForm_ReadsEverything()
{
    const string source = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Post>(entity =>
                {
                    entity.HasMany(p => p.Tags).WithMany(t => t.Posts).UsingEntity(
                        "PostTags",
                        right => right.HasOne<Tag>().WithMany().HasForeignKey("TagId"),
                        left => left.HasOne<Post>().WithMany().HasForeignKey("PostId"),
                        j => j.HasKey("PostId", "TagId"));
                });
            }
        }
        """;

    var relationships = new FluentConfigParser().ParseRelationships(source, PostTagEntities);
    var relationship = Assert.Single(relationships.Value);
    Assert.Equal("PostTags", relationship.JoinEntityName);
    Assert.True(relationship.JoinEntityIsSharedType);
    Assert.Equal(new List<string> { "TagId" }, relationship.JoinEntityRightForeignKey);
    Assert.Equal(new List<string> { "PostId" }, relationship.JoinEntityLeftForeignKey);
}

[Fact]
public void ParseRelationships_UsingEntityUnreadableForeignKeyArgument_EmitsDiagnostic()
{
    const string source = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Post>(entity =>
                {
                    entity.HasMany(p => p.Tags).WithMany(t => t.Posts).UsingEntity<PostTag>(
                        right => right.HasOne<Tag>().WithMany().HasForeignKey(SomeHelper()),
                        left => left.HasOne<Post>().WithMany().HasForeignKey("PostId"));
                });
            }
        }
        """;

    var result = new FluentConfigParser().ParseRelationships(source, PostTagEntities);

    Assert.Contains(result.Diagnostics, d => d.Code == DiagnosticCodes.UnreadableUsingEntityForeignKeyArgument);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~ParseRelationships_UsingEntity"`
Expected: FAIL — `JoinEntityIsSharedType` stays `false` for the string-name case, `JoinEntityRightForeignKey`/`JoinEntityLeftForeignKey` stay empty for every shape, no diagnostic fires for the unreadable case.

- [ ] **Step 3: Implement**

In `src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs`, replace this block (near the end of `ParseRelationshipChain`):

```csharp
var joinEntityName = kind == RelationshipKind.ManyToMany ? TryGetGenericTypeArgument(usingEntityCall) : null;

results.Add(new RelationshipConfig(
    principalEntity,
    dependentEntity,
    kind,
    principalNavigation,
    dependentNavigation,
    foreignKeyProperties,
    onDeleteBehavior,
    joinEntityName,
    constraintName,
    principalKeyProperties));
```

with:

```csharp
string? joinEntityName = null;
var joinEntityIsSharedType = false;
IReadOnlyList<string> joinEntityRightForeignKey = Array.Empty<string>();
IReadOnlyList<string> joinEntityLeftForeignKey = Array.Empty<string>();

if (kind == RelationshipKind.ManyToMany && usingEntityCall is not null)
{
    var genericName = FluentSyntaxHelpers.TryGetGenericTypeArgument(usingEntityCall);
    joinEntityName = genericName ?? FluentSyntaxHelpers.TryReadUsingEntityStringName(usingEntityCall);
    joinEntityIsSharedType = genericName is null && joinEntityName is not null;

    var lambdas = FluentSyntaxHelpers.GetUsingEntityLambdaArguments(usingEntityCall);
    if (lambdas.Count is 2 or 3)
    {
        var (rightForeignKey, rightDiagnostic) = ReadUsingEntitySideForeignKey(lambdas[0], dependentEntity);
        var (leftForeignKey, leftDiagnostic) = ReadUsingEntitySideForeignKey(lambdas[1], principalEntity);
        joinEntityRightForeignKey = rightForeignKey;
        joinEntityLeftForeignKey = leftForeignKey;

        if (rightDiagnostic is not null)
        {
            diagnostics.Add(rightDiagnostic);
        }

        if (leftDiagnostic is not null)
        {
            diagnostics.Add(leftDiagnostic);
        }
    }
}

results.Add(new RelationshipConfig(
    principalEntity,
    dependentEntity,
    kind,
    principalNavigation,
    dependentNavigation,
    foreignKeyProperties,
    onDeleteBehavior,
    joinEntityName,
    constraintName,
    principalKeyProperties,
    joinEntityIsSharedType,
    joinEntityRightForeignKey,
    joinEntityLeftForeignKey));
```

Add a new private helper right after `ParseRelationshipChain`:

```csharp
/// Reads a `UsingEntity`'s per-side FK lambda (`right => right.HasOne<T>().WithMany().HasForeignKey(...)`)
/// for the property name(s) backing that side's foreign key. Returns an empty list (no diagnostic)
/// when the lambda has no `HasForeignKey` call at all — EF defaults to a shadow FK named by
/// convention in that case, which this tool doesn't need to compute since nothing was explicitly
/// configured. Returns an empty list plus a diagnostic when `HasForeignKey` is present but its
/// argument(s) can't be read as property names.
private static (IReadOnlyList<string> ForeignKey, Diagnostic? Diagnostic) ReadUsingEntitySideForeignKey(
    AnonymousFunctionExpressionSyntax lambda, string entityName)
{
    var hasForeignKeyCall = FluentSyntaxHelpers.FindCallsNamed(lambda, "HasForeignKey").FirstOrDefault();
    if (hasForeignKeyCall is null)
    {
        return (Array.Empty<string>(), null);
    }

    var propertyNames = FluentSyntaxHelpers.TryReadPropertyNameList(hasForeignKeyCall);
    if (propertyNames is not null)
    {
        return (propertyNames, null);
    }

    return (Array.Empty<string>(), new Diagnostic(
        DiagnosticCodes.UnreadableUsingEntityForeignKeyArgument,
        "UsingEntity's per-side HasForeignKey argument(s) could not be read as property name(s).",
        entityName,
        PropertyName: null,
        hasForeignKeyCall.Span));
}
```

Update the other, unrelated call site of `TryGetGenericTypeArgument` earlier in the same method (already done in Task 2 Step 3 — confirm it reads `FluentSyntaxHelpers.TryGetGenericTypeArgument(hasForeignKeyCall)`, not a dangling reference to the now-deleted private method).

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~ParseRelationships_UsingEntity"`
Expected: PASS

- [ ] **Step 5: Run the full test suite**

Run: `dotnet test`
Expected: PASS

- [ ] **Step 6: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs
git commit -m "Parse UsingEntity's shared-type identity and per-side FK lambdas"
```

---

### Task 4: `ModelMerger` — passthrough + join-entity shadow-property synthesis

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/Merging/ModelMerger.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Merging/ModelMergerTests.cs`

**Interfaces:**
- Consumes: `RelationshipConfig`/`RelationshipModel` new fields (Task 1); `EntityModel.IsSharedType` (Task 1).
- Produces: `ModelMerger.ApplyRelationships` now passes through the three new fields; new `ModelMerger.ApplyJoinEntityForeignKeyShadowProperties(IReadOnlyList<EntityModel> entities, IReadOnlyList<RelationshipModel> relationships) -> IReadOnlyList<EntityModel>`.

- [ ] **Step 1: Write failing tests**

```csharp
// In tests/EfSchemaVisualizer.Core.Tests/Merging/ModelMergerTests.cs

[Fact]
public void ApplyRelationships_PassesThroughJoinEntityFields()
{
    var config = new RelationshipConfig(
        "Post", "Tag", RelationshipKind.ManyToMany, "Tags", "Posts",
        JoinEntityName: "PostTags",
        JoinEntityIsSharedType: true,
        JoinEntityRightForeignKey: new List<string> { "TagId" },
        JoinEntityLeftForeignKey: new List<string> { "PostId" });

    var relationship = Assert.Single(ModelMerger.ApplyRelationships(new List<RelationshipConfig> { config }));

    Assert.True(relationship.JoinEntityIsSharedType);
    Assert.Equal(new List<string> { "TagId" }, relationship.JoinEntityRightForeignKey);
    Assert.Equal(new List<string> { "PostId" }, relationship.JoinEntityLeftForeignKey);
}

[Fact]
public void ApplyJoinEntityForeignKeyShadowProperties_AddsMissingObjectTypedShadowProperties()
{
    var joinEntity = new EntityModel("PostTags", new List<PropertyModel>(), IsSharedType: true);
    var relationship = new RelationshipModel(
        "Post", "Tag", RelationshipKind.ManyToMany, "Tags", "Posts",
        JoinEntityName: "PostTags",
        JoinEntityIsSharedType: true,
        JoinEntityRightForeignKey: new List<string> { "TagId" },
        JoinEntityLeftForeignKey: new List<string> { "PostId" });

    var result = ModelMerger.ApplyJoinEntityForeignKeyShadowProperties(
        new List<EntityModel> { joinEntity }, new List<RelationshipModel> { relationship });

    var updated = Assert.Single(result);
    Assert.Contains(updated.Properties, p => p is { Name: "TagId", ClrType: "object", IsShadow: true });
    Assert.Contains(updated.Properties, p => p is { Name: "PostId", ClrType: "object", IsShadow: true });
}

[Fact]
public void ApplyJoinEntityForeignKeyShadowProperties_DoesNotDuplicateAnAlreadyPresentProperty()
{
    var joinEntity = new EntityModel(
        "PostTags",
        new List<PropertyModel> { new("TagId", "int", IsNullable: false, MaxLength: null, IsShadow: true) },
        IsSharedType: true);
    var relationship = new RelationshipModel(
        "Post", "Tag", RelationshipKind.ManyToMany, "Tags", "Posts",
        JoinEntityName: "PostTags",
        JoinEntityIsSharedType: true,
        JoinEntityRightForeignKey: new List<string> { "TagId" },
        JoinEntityLeftForeignKey: new List<string> { "PostId" });

    var result = ModelMerger.ApplyJoinEntityForeignKeyShadowProperties(
        new List<EntityModel> { joinEntity }, new List<RelationshipModel> { relationship });

    var updated = Assert.Single(result);
    Assert.Single(updated.Properties, p => p.Name == "TagId");
    Assert.Equal("int", updated.Properties.Single(p => p.Name == "TagId").ClrType);
    Assert.Contains(updated.Properties, p => p is { Name: "PostId", ClrType: "object", IsShadow: true });
}

[Fact]
public void ApplyJoinEntityForeignKeyShadowProperties_NonSharedTypeEntity_IsUnaffected()
{
    var entity = new EntityModel("Post", new List<PropertyModel>());

    var result = ModelMerger.ApplyJoinEntityForeignKeyShadowProperties(
        new List<EntityModel> { entity }, new List<RelationshipModel>());

    Assert.Same(entity, Assert.Single(result));
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~ApplyRelationships_PassesThroughJoinEntityFields|FullyQualifiedName~ApplyJoinEntityForeignKeyShadowProperties"`
Expected: FAIL with a compile error (`ApplyJoinEntityForeignKeyShadowProperties` doesn't exist) and/or assertion failures (new fields not passed through).

- [ ] **Step 3: Implement**

In `src/EfSchemaVisualizer.Core/Merging/ModelMerger.cs`, update `ApplyRelationships`:

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
            c.JoinEntityName,
            ConstraintName: c.ConstraintName,
            PrincipalKeyProperties: c.PrincipalKeyProperties,
            JoinEntityIsSharedType: c.JoinEntityIsSharedType,
            JoinEntityRightForeignKey: c.JoinEntityRightForeignKey,
            JoinEntityLeftForeignKey: c.JoinEntityLeftForeignKey))
        .ToList();
}
```

Add a new public method, near `ApplyShadowProperties`:

```csharp
/// A UsingEntity per-side FK lambda (`right => right.HasOne&lt;T&gt;().WithMany().HasForeignKey("Name")`)
/// names a property by string, but unlike a `Property&lt;T&gt;("Name")` call (see
/// `ApplyShadowProperties`), it never states that property's CLR type — so any name it references
/// that isn't already present on the join entity (from an explicit `Property&lt;T&gt;()` call, or
/// from being a real class property for a non-shared-type join entity) gets a same-named,
/// `object`-typed shadow property synthesized here, purely so it has *something* to render/rename.
/// Only applies to shared-type join entities — a class-backed join entity's FK-referenced
/// properties are always already present (they're real class properties).
public static IReadOnlyList<EntityModel> ApplyJoinEntityForeignKeyShadowProperties(
    IReadOnlyList<EntityModel> entities, IReadOnlyList<RelationshipModel> relationships)
{
    return entities
        .Select(entity =>
        {
            if (!entity.IsSharedType)
            {
                return entity;
            }

            var relationship = relationships.FirstOrDefault(r => r.JoinEntityName == entity.Name);
            if (relationship is null)
            {
                return entity;
            }

            var existingNames = entity.Properties.Select(p => p.Name).ToHashSet();
            var missingNames = relationship.JoinEntityRightForeignKey
                .Concat(relationship.JoinEntityLeftForeignKey)
                .Where(name => !existingNames.Contains(name))
                .Distinct()
                .ToList();

            if (missingNames.Count == 0)
            {
                return entity;
            }

            var newProperties = missingNames
                .Select(name => new PropertyModel(name, "object", IsNullable: true, MaxLength: null, IsShadow: true))
                .ToList();

            return entity with { Properties = entity.Properties.Concat(newProperties).ToList() };
        })
        .ToList();
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~ApplyRelationships_PassesThroughJoinEntityFields|FullyQualifiedName~ApplyJoinEntityForeignKeyShadowProperties"`
Expected: PASS

- [ ] **Step 5: Run the full test suite**

Run: `dotnet test`
Expected: PASS

- [ ] **Step 6: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Merging/ModelMerger.cs tests/EfSchemaVisualizer.Core.Tests/Merging/ModelMergerTests.cs
git commit -m "Add ModelMerger support for shared-type join-entity FK properties"
```

---

### Task 5: `DiagramModelBuilder` — synthesize shared-type join entities end-to-end

**Files:**
- Modify: `src/EfSchemaVisualizer.Web/DiagramModelBuilder.cs`
- Test: `find tests -iname "DiagramModelBuilderTests.cs"` (use whatever file already covers `DiagramModelBuilder.Build` end-to-end tests; if none exists, create `tests/EfSchemaVisualizer.Web.Tests/DiagramModelBuilderTests.cs` following the namespace/using pattern of a neighboring test file in that test project)

**Interfaces:**
- Consumes: `ModelMerger.ApplyJoinEntityForeignKeyShadowProperties` (Task 4); `FluentConfigParser.ParseRelationships` (Task 3); `EntityModel.IsSharedType` (Task 1).
- Produces: `DiagramModelBuilder.Build` now includes a synthesized `EntityModel` (with `IsSharedType = true`) for every string-named `UsingEntity(...)` relationship whose join entity has no matching class, fully populated by the existing merge pipeline (key, columns, shadow properties) plus the new FK-shadow-property step.

- [ ] **Step 1: Write the failing end-to-end test**

```csharp
// In the DiagramModelBuilderTests file
using System.Linq;
using EfSchemaVisualizer.Core.Model;
using EfSchemaVisualizer.Core.Parsing;
using Xunit;

namespace EfSchemaVisualizer.Web.Tests; // match whatever namespace neighboring tests in this project use

public class UsingEntitySharedTypeTests
{
    private const string ClassSource = """
        public class Post
        {
            public int Id { get; set; }
            public ICollection<Tag> Tags { get; set; }
        }

        public class Tag
        {
            public int Id { get; set; }
            public ICollection<Post> Posts { get; set; }
        }
        """;

    private const string ConfigSource = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Post>(entity =>
                {
                    entity.HasMany(p => p.Tags).WithMany(t => t.Posts).UsingEntity(
                        "PostTags",
                        right => right.HasOne<Tag>().WithMany().HasForeignKey("TagId"),
                        left => left.HasOne<Post>().WithMany().HasForeignKey("PostId"),
                        j => j.HasKey("PostId", "TagId"));
                });
            }
        }
        """;

    [Fact]
    public void Build_SharedTypeJoinEntity_IsSynthesizedWithKeyAndForeignKeyProperties()
    {
        var result = DiagramModelBuilder.Build(ClassSource, ConfigSource);

        var joinEntity = Assert.Single(result.Entities, e => e.Name == "PostTags");
        Assert.True(joinEntity.IsSharedType);
        Assert.Equal(new List<string> { "PostId", "TagId" }, joinEntity.KeyPropertyNames);
        Assert.Contains(joinEntity.Properties, p => p.Name == "PostId");
        Assert.Contains(joinEntity.Properties, p => p.Name == "TagId");

        Assert.DoesNotContain(result.Diagnostics, d => d.Code == DiagnosticCodes.EntityHasNoKey && d.EntityName == "PostTags");
    }

    [Fact]
    public void Build_SharedTypeJoinEntityWithNoExplicitKey_FiresEntityHasNoKey()
    {
        const string configWithoutKey = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Post>(entity =>
                    {
                        entity.HasMany(p => p.Tags).WithMany(t => t.Posts).UsingEntity("PostTags");
                    });
                }
            }
            """;

        var result = DiagramModelBuilder.Build(ClassSource, configWithoutKey);

        Assert.Contains(result.Diagnostics, d => d.Code == DiagnosticCodes.EntityHasNoKey && d.EntityName == "PostTags");
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~UsingEntitySharedTypeTests"`
Expected: FAIL — `result.Entities` contains no entity named `"PostTags"` at all yet.

- [ ] **Step 3: Implement**

In `src/EfSchemaVisualizer.Web/DiagramModelBuilder.cs`, find this line (just before the big `mergedEntities` `Select` pipeline begins):

```csharp
IReadOnlyList<EntityModel> mergedEntities = entityResult.Value
    .Where(entity => !ignoredEntityNames.Contains(entity.Name))
```

Insert immediately above it:

```csharp
var sharedTypeJoinEntityNames = fluentRelationships.Value
    .Where(c => c.JoinEntityIsSharedType && c.JoinEntityName is not null)
    .Select(c => c.JoinEntityName!)
    .Distinct()
    .Where(name => entityResult.Value.All(e => e.Name != name))
    .ToList();

var baseEntities = entityResult.Value
    .Concat(sharedTypeJoinEntityNames.Select(name => new EntityModel(name, new List<PropertyModel>(), IsSharedType: true)))
    .ToList();
```

Then change the pipeline's starting point from `entityResult.Value` to `baseEntities`:

```csharp
IReadOnlyList<EntityModel> mergedEntities = baseEntities
    .Where(entity => !ignoredEntityNames.Contains(entity.Name))
    // ... rest of the .Select(...) chain is unchanged
```

Then find this line (after `relationshipModels` is computed):

```csharp
var relationshipModels = ModelMerger.ApplyRelationships(mergedRelationshipConfigs);
```

Insert immediately after it:

```csharp
entities = ModelMerger.ApplyJoinEntityForeignKeyShadowProperties(entities, relationshipModels);
```

(`entities` is already a reassignable local of type `IReadOnlyList<EntityModel>` at this point in the method — the same variable `EnumStorageInference.Fold` reassigns a few lines earlier.)

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~UsingEntitySharedTypeTests"`
Expected: PASS

- [ ] **Step 5: Run the full test suite**

Run: `dotnet test`
Expected: PASS

- [ ] **Step 6: Commit**

```bash
git add src/EfSchemaVisualizer.Web/DiagramModelBuilder.cs tests/EfSchemaVisualizer.Web.Tests/
git commit -m "Synthesize shared-type join entities in DiagramModelBuilder"
```

---

### Task 6: `OnModelCreatingRewriter` — preserve nested config across unrelated edits; write shared-type form

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs`

**Interfaces:**
- Consumes: `RelationshipModel.JoinEntityIsSharedType` (Task 1).
- Produces: `OnModelCreatingRewriter.TryCaptureUsingEntityArguments(string sourceCode, RelationshipModel relationship) -> ArgumentListSyntax?` (new public method); `SetRelationship(string sourceCode, RelationshipModel relationship, ArgumentListSyntax? preservedUsingEntityArguments = null)` (new optional third parameter, source-compatible with existing 2-arg call sites); `BuildUsingEntityCall` now emits `UsingEntity("Name")` for a shared-type relationship with no preserved arguments, or re-attaches preserved arguments verbatim when present.

- [ ] **Step 1: Write failing tests**

```csharp
// In tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs

private const string SourceWithUsingEntityConfigLambda = """
    public class AppDbContext : DbContext
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Blog>(entity =>
            {
                entity.HasMany<Post>().WithMany().UsingEntity<BlogPost>(j => j.HasKey("BlogId", "PostId"));
            });
        }
    }
    """;

[Fact]
public void TryCaptureUsingEntityArguments_ManyToManyWithConfigLambda_CapturesArgumentListVerbatim()
{
    var relationship = new RelationshipModel(
        "Blog", "Post", RelationshipKind.ManyToMany, null, null,
        JoinEntityName: "BlogPost");

    var captured = new OnModelCreatingRewriter()
        .TryCaptureUsingEntityArguments(SourceWithUsingEntityConfigLambda, relationship);

    Assert.NotNull(captured);
    Assert.Equal("(j => j.HasKey(\"BlogId\", \"PostId\"))", captured!.ToString());
}

[Fact]
public void TryCaptureUsingEntityArguments_BareUsingEntity_ReturnsNull()
{
    var relationship = new RelationshipModel(
        "Blog", "Post", RelationshipKind.ManyToMany, null, null,
        JoinEntityName: "BlogPost");

    var captured = new OnModelCreatingRewriter()
        .TryCaptureUsingEntityArguments(SourceWithManyToManyRelationship, relationship);

    Assert.Null(captured);
}

[Fact]
public void TryCaptureUsingEntityArguments_NotManyToMany_ReturnsNull()
{
    var relationship = new RelationshipModel("Blog", "Post", RelationshipKind.OneToMany, null, null);

    var captured = new OnModelCreatingRewriter()
        .TryCaptureUsingEntityArguments(SourceWithNoRelationshipConfig, relationship);

    Assert.Null(captured);
}

[Fact]
public void SetRelationship_ManyToMany_WithPreservedUsingEntityArguments_ReattachesThemVerbatim()
{
    var relationship = new RelationshipModel(
        "Blog", "Post", RelationshipKind.ManyToMany, null, null,
        JoinEntityName: "BlogPost", OnDeleteBehavior: null);

    var captured = new OnModelCreatingRewriter()
        .TryCaptureUsingEntityArguments(SourceWithUsingEntityConfigLambda, relationship);

    var withoutOld = new OnModelCreatingRewriter().RemoveRelationship(SourceWithUsingEntityConfigLambda, relationship);
    var result = new OnModelCreatingRewriter().SetRelationship(withoutOld, relationship, captured);

    Assert.Contains("UsingEntity<BlogPost>(j => j.HasKey(\"BlogId\", \"PostId\"))", result);
}

[Fact]
public void SetRelationship_SharedTypeJoinEntity_EmitsStringNamedUsingEntity()
{
    var relationship = new RelationshipModel(
        "Blog", "Post", RelationshipKind.ManyToMany, null, null,
        JoinEntityName: "BlogPosts", JoinEntityIsSharedType: true);

    var result = new OnModelCreatingRewriter()
        .SetRelationship(SourceWithNoRelationshipConfig, relationship);

    Assert.Contains("entity.HasMany<Post>().WithMany().UsingEntity(\"BlogPosts\")", result);
}
```

(`SourceWithManyToManyRelationship`, `SourceWithNoRelationshipConfig` already exist in this test file — reuse them as-is.)

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~TryCaptureUsingEntityArguments|FullyQualifiedName~SetRelationship_ManyToMany_WithPreservedUsingEntityArguments|FullyQualifiedName~SetRelationship_SharedTypeJoinEntity"`
Expected: FAIL with a compile error (`TryCaptureUsingEntityArguments` doesn't exist; `SetRelationship` doesn't accept a third argument yet).

- [ ] **Step 3: Extract the shared call-finding helper and add `TryCaptureUsingEntityArguments`**

In `src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs`, replace `RemoveRelationship`:

```csharp
public string RemoveRelationship(string sourceCode, RelationshipModel relationship)
{
    var tree = CSharpSyntaxTree.ParseText(sourceCode);
    var root = tree.GetCompilationUnitRoot();

    var matchingCall = FindRelationshipConfiguringCall(root, relationship);

    if (matchingCall is null
        || matchingCall.Ancestors().OfType<ExpressionStatementSyntax>().FirstOrDefault() is not { } statement)
    {
        return sourceCode;
    }

    var newRoot = root.RemoveNode(statement, SyntaxRemoveOptions.KeepNoTrivia)!;
    return newRoot.NormalizeWhitespace().ToFullString();
}

/// Captures a many-to-many relationship's existing `UsingEntity(...)` call arguments (its
/// lambdas/string name, verbatim syntax) before an edit removes and rebuilds the whole
/// `HasMany().WithMany().UsingEntity(...)` statement, so `SetRelationship` can re-attach them
/// unchanged instead of silently discarding hand-written join-entity configuration. Returns null
/// when the relationship isn't many-to-many, its statement can't be found, or it has no
/// `UsingEntity(...)` call at all (bare `HasMany().WithMany()`, or a brand-new relationship).
public ArgumentListSyntax? TryCaptureUsingEntityArguments(string sourceCode, RelationshipModel relationship)
{
    if (relationship.Kind != RelationshipKind.ManyToMany)
    {
        return null;
    }

    var tree = CSharpSyntaxTree.ParseText(sourceCode);
    var root = tree.GetCompilationUnitRoot();

    var matchingCall = FindRelationshipConfiguringCall(root, relationship);
    if (matchingCall is null)
    {
        return null;
    }

    InvocationExpressionSyntax? usingEntityCall = null;
    FluentSyntaxHelpers.WalkChainedTail(matchingCall, invocation =>
    {
        if (invocation.Expression is MemberAccessExpressionSyntax { Name.Identifier.Text: "UsingEntity" })
        {
            usingEntityCall = invocation;
        }
    });

    return usingEntityCall?.ArgumentList;
}

private static InvocationExpressionSyntax? FindRelationshipConfiguringCall(CompilationUnitSyntax root, RelationshipModel relationship)
{
    var scopeEntityName = relationship.Kind == RelationshipKind.ManyToMany
        ? relationship.PrincipalEntity
        : relationship.DependentEntity;
    var otherEntityName = relationship.Kind == RelationshipKind.ManyToMany
        ? relationship.DependentEntity
        : relationship.PrincipalEntity;
    var methodName = relationship.Kind == RelationshipKind.ManyToMany ? "HasMany" : "HasOne";
    var expectedNavigation = relationship.Kind == RelationshipKind.ManyToMany
        ? relationship.PrincipalNavigation
        : relationship.DependentNavigation;

    var scopes = FindConfigScopes(root, scopeEntityName);

    return scopes
        .SelectMany(scope => FluentSyntaxHelpers.FindCallsNamed(scope, methodName))
        .FirstOrDefault(call =>
            HasGenericTypeArgument(call, otherEntityName)
            || (expectedNavigation is not null && TryGetNavigationPropertyName(call) == expectedNavigation));
}
```

(This is a behavior-preserving refactor of `RemoveRelationship`'s existing logic into the shared `FindRelationshipConfiguringCall` helper, plus the new `TryCaptureUsingEntityArguments` method built on it.)

- [ ] **Step 4: Thread `preservedUsingEntityArguments` through `SetRelationship` and `BuildUsingEntityCall`**

Update `SetRelationship`:

```csharp
public string SetRelationship(string sourceCode, RelationshipModel relationship, ArgumentListSyntax? preservedUsingEntityArguments = null)
{
    var tree = CSharpSyntaxTree.ParseText(sourceCode);
    var root = tree.GetCompilationUnitRoot();

    var scopeEntityName = relationship.Kind == RelationshipKind.ManyToMany
        ? relationship.PrincipalEntity
        : relationship.DependentEntity;

    var scopes = FindConfigScopes(root, scopeEntityName);
    var existingScope = scopes.FirstOrDefault();

    if (existingScope is not null)
    {
        return InsertRelationshipStatement(root, existingScope, relationship, preservedUsingEntityArguments);
    }

    return InsertRelationshipEntityBlock(root, scopeEntityName, relationship, preservedUsingEntityArguments);
}

private static string InsertRelationshipStatement(
    CompilationUnitSyntax root, SyntaxNode scope, RelationshipModel relationship, ArgumentListSyntax? preservedUsingEntityArguments)
{
    var (block, blockReceiverName) = GetScopeBlockAndReceiver(scope);

    var newStatement = BuildRelationshipStatement(blockReceiverName, relationship, preservedUsingEntityArguments);
    var newBlock = block.AddStatements(newStatement);

    var newRoot = root.ReplaceNode(block, newBlock);
    return newRoot.NormalizeWhitespace().ToFullString();
}

private static string InsertRelationshipEntityBlock(
    CompilationUnitSyntax root, string scopeEntityName, RelationshipModel relationship, ArgumentListSyntax? preservedUsingEntityArguments)
{
    var method = FindOnModelCreatingMethod(root);
    var methodBody = method.Body
        ?? throw new InvalidOperationException("OnModelCreating has no method body.");
    var modelBuilderParamName = method.ParameterList.Parameters.Single().Identifier.Text;

    var statement = BuildRelationshipStatement("entity", relationship, preservedUsingEntityArguments);
    var entityBlockStatement = BuildEntityInvocationStatement(modelBuilderParamName, scopeEntityName, SyntaxFactory.Block(statement));

    var newMethodBody = methodBody.AddStatements(entityBlockStatement);
    var newRoot = root.ReplaceNode(methodBody, newMethodBody);
    return newRoot.NormalizeWhitespace().ToFullString();
}

private static ExpressionStatementSyntax BuildRelationshipStatement(
    string blockReceiverName, RelationshipModel relationship, ArgumentListSyntax? preservedUsingEntityArguments = null)
{
    ExpressionSyntax chain = SyntaxFactory.IdentifierName(blockReceiverName);

    if (relationship.Kind == RelationshipKind.ManyToMany)
    {
        chain = BuildRelationshipCall(chain, "HasMany", relationship.DependentEntity, relationship.PrincipalNavigation);
        chain = BuildRelationshipCall(chain, "WithMany", targetEntityName: null, relationship.DependentNavigation);

        if (relationship.JoinEntityName is not null)
        {
            chain = BuildUsingEntityCall(chain, relationship, preservedUsingEntityArguments);
        }

        return SyntaxFactory.ExpressionStatement(chain);
    }

    if (relationship.Kind == RelationshipKind.OneToOne)
    {
        chain = BuildRelationshipCall(chain, "HasOne", relationship.PrincipalEntity, relationship.DependentNavigation);
        chain = BuildRelationshipCall(chain, "WithOne", targetEntityName: null, relationship.PrincipalNavigation);
        chain = AppendHasForeignKey(chain, relationship.ForeignKeyProperties, relationship.DependentEntity);
        chain = AppendHasPrincipalKey(chain, relationship.PrincipalKeyProperties, relationship.PrincipalEntity);
        chain = AppendOnDelete(chain, relationship.OnDeleteBehavior);
        chain = AppendHasConstraintName(chain, relationship.ConstraintName);
        return SyntaxFactory.ExpressionStatement(chain);
    }

    // OneToMany
    chain = BuildRelationshipCall(chain, "HasOne", relationship.PrincipalEntity, relationship.DependentNavigation);
    chain = BuildRelationshipCall(chain, "WithMany", targetEntityName: null, relationship.PrincipalNavigation);
    chain = AppendHasForeignKey(chain, relationship.ForeignKeyProperties, dependentGeneric: null);
    chain = AppendHasPrincipalKey(chain, relationship.PrincipalKeyProperties, principalGeneric: null);
    chain = AppendOnDelete(chain, relationship.OnDeleteBehavior);
    chain = AppendHasConstraintName(chain, relationship.ConstraintName);
    return SyntaxFactory.ExpressionStatement(chain);
}
```

Replace `BuildUsingEntityCall`:

```csharp
private static ExpressionSyntax BuildUsingEntityCall(
    ExpressionSyntax chain, RelationshipModel relationship, ArgumentListSyntax? preservedArguments)
{
    SimpleNameSyntax methodIdentifier = relationship.JoinEntityIsSharedType
        ? SyntaxFactory.IdentifierName("UsingEntity")
        : SyntaxFactory.GenericName(SyntaxFactory.Identifier("UsingEntity"))
            .WithTypeArgumentList(SyntaxFactory.TypeArgumentList(
                SyntaxFactory.SingletonSeparatedList<TypeSyntax>(SyntaxFactory.IdentifierName(relationship.JoinEntityName!))));

    var argumentList = preservedArguments
        ?? (relationship.JoinEntityIsSharedType
            ? SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(
                SyntaxFactory.Argument(SyntaxFactory.LiteralExpression(
                    SyntaxKind.StringLiteralExpression,
                    SyntaxFactory.Literal(relationship.JoinEntityName!)))))
            : SyntaxFactory.ArgumentList());

    return SyntaxFactory.InvocationExpression(
        SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, chain, methodIdentifier),
        argumentList);
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~TryCaptureUsingEntityArguments|FullyQualifiedName~SetRelationship_ManyToMany_WithPreservedUsingEntityArguments|FullyQualifiedName~SetRelationship_SharedTypeJoinEntity"`
Expected: PASS

- [ ] **Step 6: Run the full test suite**

Run: `dotnet test`
Expected: PASS (in particular, the two pre-existing `SetRelationship_ManyToMany_InsertsIntoPrincipalScope` / `SetRelationship_ManyToMany_WithJoinEntity_EmitsUsingEntity` tests must still pass unchanged, since they call the 2-arg `SetRelationship` overload)

- [ ] **Step 7: Commit**

```bash
git add src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs
git commit -m "Preserve UsingEntity nested config across relationship edits; write shared-type form"
```

---

### Task 7: `DiagramEditor.SetRelationshipShape` — thread the preservation through the editor

**Files:**
- Modify: `src/EfSchemaVisualizer.Web/Diagram/DiagramEditor.cs`
- Test: `find tests -iname "DiagramEditorTests.cs"`

**Interfaces:**
- Consumes: `OnModelCreatingRewriter.TryCaptureUsingEntityArguments` (Task 6).
- Produces: `SetRelationshipShape`'s public signature is unchanged; only its internal rewrite call changes.

- [ ] **Step 1: Write the failing test**

```csharp
// In the DiagramEditorTests file, following whatever fixture/setup pattern
// surrounding SetRelationshipShape tests already use in that file.
[Fact]
public void SetRelationshipShape_ManyToManyWithUsingEntityConfig_UnrelatedEditPreservesNestedConfig()
{
    const string classSource = """
        public class Blog
        {
            public int Id { get; set; }
            public ICollection<Post> Posts { get; set; }
        }

        public class Post
        {
            public int Id { get; set; }
            public ICollection<Blog> Blogs { get; set; }
        }
        """;

    const string configSource = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Blog>(entity =>
                {
                    entity.HasMany(b => b.Posts).WithMany(p => p.Blogs).UsingEntity<BlogPost>(j => j.HasKey("BlogId", "PostId"));
                });
            }
        }
        """;

    var editor = new DiagramEditor(classSource, configSource);
    var relationship = editor.Current.Relationships.Single(r => r.Kind == RelationshipKind.ManyToMany);

    var result = editor.SetRelationshipShape(
        relationship,
        RelationshipKind.ManyToMany,
        newForeignKeyProperties: Array.Empty<string>(),
        newOnDeleteBehavior: null,
        newConstraintName: "SomeUnrelatedNameChangeThatDoesNothingForManyToMany");

    Assert.True(result.Success);
    Assert.Contains("UsingEntity<BlogPost>(j => j.HasKey(\"BlogId\", \"PostId\"))", editor.ConfigSource);
}
```

(`DiagramEditor(classSource, configSource)`, `editor.Current`, `editor.ConfigSource`, and `DiagramEditResult.Success` are all confirmed public members as of this plan's writing — `src/EfSchemaVisualizer.Web/Diagram/DiagramEditor.cs:42-61`.)

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~SetRelationshipShape_ManyToManyWithUsingEntityConfig_UnrelatedEditPreservesNestedConfig"`
Expected: FAIL — the `UsingEntity<BlogPost>(j => j.HasKey(...))` lambda is gone from the rewritten source, replaced by a bare `UsingEntity<BlogPost>()`.

- [ ] **Step 3: Implement**

In `src/EfSchemaVisualizer.Web/Diagram/DiagramEditor.cs`, in `SetRelationshipShape`, replace:

```csharp
var withoutOld = relationship.IsInferred
    ? ConfigSource
    : _configRewriter.RemoveRelationship(ConfigSource, relationship);

if (!relationship.IsInferred && withoutOld == ConfigSource)
{
    return DiagramEditResult.Fail("Could not locate this relationship's existing configuration to update.");
}

var withNew = _configRewriter.SetRelationship(withoutOld, updated);
Apply(ClassSource, withNew);
return DiagramEditResult.Ok();
```

with:

```csharp
var preservedUsingEntityArguments = relationship.IsInferred
    ? null
    : _configRewriter.TryCaptureUsingEntityArguments(ConfigSource, relationship);

var withoutOld = relationship.IsInferred
    ? ConfigSource
    : _configRewriter.RemoveRelationship(ConfigSource, relationship);

if (!relationship.IsInferred && withoutOld == ConfigSource)
{
    return DiagramEditResult.Fail("Could not locate this relationship's existing configuration to update.");
}

var withNew = _configRewriter.SetRelationship(withoutOld, updated, preservedUsingEntityArguments);
Apply(ClassSource, withNew);
return DiagramEditResult.Ok();
```

(Capturing before `RemoveRelationship` matters: it must read the *original* `ConfigSource`, not the post-removal `withoutOld`, since by definition the `UsingEntity(...)` call no longer exists in `withoutOld`.)

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~SetRelationshipShape_ManyToManyWithUsingEntityConfig_UnrelatedEditPreservesNestedConfig"`
Expected: PASS

- [ ] **Step 5: Run the full test suite**

Run: `dotnet test`
Expected: PASS

- [ ] **Step 6: Commit**

```bash
git add src/EfSchemaVisualizer.Web/Diagram/DiagramEditor.cs tests/
git commit -m "Preserve UsingEntity config in DiagramEditor.SetRelationshipShape edits"
```

---

### Task 8: UI rendering — shared-type marker and read-only per-side FK lines

**Files:**
- Modify: `src/EfSchemaVisualizer.Web/Diagram/EntityNode.razor`
- Modify: `src/EfSchemaVisualizer.Web/Diagram/RelationshipLinkLabel.razor`

**Interfaces:**
- Consumes: `EntityModel.IsSharedType`, `RelationshipModel.JoinEntityRightForeignKey`/`JoinEntityLeftForeignKey` (Task 1).
- Produces: no new C# interfaces — Razor markup only. This task has no automated test (no test infrastructure in this repo renders Razor components); verify manually per Step 3.

- [ ] **Step 1: Add the shared-type marker to `EntityNode.razor`**

In `src/EfSchemaVisualizer.Web/Diagram/EntityNode.razor`, immediately after the existing `IsOwned` marker block (around line 34-37):

```razor
@if (Node.Entity.IsOwned)
{
    <span title="Owned type (OwnsMany): this is its own table, but it cannot exist independently of its owner." style="opacity: 0.6; margin-right: 2px;">◆</span>
}
@if (Node.Entity.IsSharedType)
{
    <span title="Shared-type entity: configured via UsingEntity(...) with no backing C# class." style="opacity: 0.6; font-size: 0.8em; margin-right: 2px;">(shared type)</span>
}
```

- [ ] **Step 2: Add read-only per-side FK lines to `RelationshipLinkLabel.razor`**

In `src/EfSchemaVisualizer.Web/Diagram/RelationshipLinkLabel.razor`, replace:

```razor
else if (Label.Relationship.JoinEntityName is not null)
{
    <div style="display: block;">Join entity: @Label.Relationship.JoinEntityName</div>
}
```

with:

```razor
else if (Label.Relationship.JoinEntityName is not null)
{
    <div style="display: block;">Join entity: @Label.Relationship.JoinEntityName</div>
    @if (Label.Relationship.JoinEntityRightForeignKey.Any())
    {
        <div style="display: block; color: #888;" title="Read-only: configured via UsingEntity's per-side FK lambda in source.">
            Join FK to @Label.Relationship.DependentEntity: @string.Join(", ", Label.Relationship.JoinEntityRightForeignKey)
        </div>
    }
    @if (Label.Relationship.JoinEntityLeftForeignKey.Any())
    {
        <div style="display: block; color: #888;" title="Read-only: configured via UsingEntity's per-side FK lambda in source.">
            Join FK to @Label.Relationship.PrincipalEntity: @string.Join(", ", Label.Relationship.JoinEntityLeftForeignKey)
        </div>
    }
}
```

- [ ] **Step 3: Build and manually verify in the browser**

Run: `dotnet build`
Expected: builds cleanly (Razor markup compiles as part of the Web project).

Then use the project's `run` skill (or `dotnet run --project src/EfSchemaVisualizer.Web`) to launch the app, paste a class + config source using one of the shared-type `UsingEntity` shapes from the spec into the app's input, and confirm: the join entity's card shows "(shared type)"; the relationship's expanded label shows the two "Join FK to ..." lines; nothing else regresses (existing owned/inferred markers still render correctly).

- [ ] **Step 4: Commit**

```bash
git add src/EfSchemaVisualizer.Web/Diagram/EntityNode.razor src/EfSchemaVisualizer.Web/Diagram/RelationshipLinkLabel.razor
git commit -m "Render shared-type join entities and read-only per-side FK config in the diagram"
```

---

### Task 9: Full-stack round-trip regression test

**Files:**
- Modify: `tests/EfSchemaVisualizer.Core.Tests/RoundTripFuzzTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 1-7.
- Produces: no new production interfaces — this is a pure regression test exercising the full `DiagramModelBuilder.Build` → `DiagramEditor` → rewrite pipeline together, distinct from the unit-level tests in Tasks 3/6 which each test one layer in isolation.

- [ ] **Step 1: Write the failing test**

Add this as a new, self-contained `[Fact]` in `tests/EfSchemaVisualizer.Core.Tests/RoundTripFuzzTests.cs` (own private consts, not woven into the shared `EntitySource`/`ConfigSource` fixture at the top of the file, to avoid risk to the existing corpus):

```csharp
private const string ManyToManyWithUsingEntityClassSource = """
    public class Post
    {
        public int Id { get; set; }
        public string Title { get; set; }
        public ICollection<Tag> Tags { get; set; }
    }

    public class Tag
    {
        public int Id { get; set; }
        public ICollection<Post> Posts { get; set; }
    }
    """;

private const string ManyToManyWithUsingEntityConfigSource = """
    public class AppDbContext : DbContext
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Post>(entity =>
            {
                entity.Property(e => e.Title).HasMaxLength(200);
                entity.HasMany(p => p.Tags).WithMany(t => t.Posts).UsingEntity<PostTag>(
                    j => j.HasKey("PostId", "TagId"));
            });
        }
    }
    """;

[Fact]
public void EndToEnd_UnrelatedPropertyEdit_PreservesUsingEntityNestedConfig()
{
    var editor = new EfSchemaVisualizer.Web.Diagram.DiagramEditor(
        ManyToManyWithUsingEntityClassSource, ManyToManyWithUsingEntityConfigSource);

    var titleResult = editor.SetMaxLength("Post", "Title", 250);
    Assert.True(titleResult.Success);

    Assert.Contains("UsingEntity<PostTag>(j => j.HasKey(\"PostId\", \"TagId\"))", editor.ConfigSource);

    var rebuilt = EfSchemaVisualizer.Web.DiagramModelBuilder.Build(editor.ClassSource, editor.ConfigSource);
    var joinEntity = Assert.Single(rebuilt.Entities, e => e.Name == "PostTag");
    Assert.Equal(new List<string> { "PostId", "TagId" }, joinEntity.KeyPropertyNames);
}
```

(`DiagramEditor.SetMaxLength(entityName, propertyName, maxLength)`, `editor.ClassSource`, and `editor.ConfigSource` are all confirmed public members as of this plan's writing — `src/EfSchemaVisualizer.Web/Diagram/DiagramEditor.cs:60-61` and `:877`.)

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~EndToEnd_UnrelatedPropertyEdit_PreservesUsingEntityNestedConfig"`
Expected: PASS already, if Tasks 1-7 are all correctly implemented (this test exercises no new production code — it's pure verification). If it fails, that means one of the earlier tasks has a gap; treat a failure here as a signal to go back and fix the relevant earlier task, not as something to patch locally.

- [ ] **Step 3: Run the full test suite**

Run: `dotnet test`
Expected: PASS

- [ ] **Step 4: Commit**

```bash
git add tests/EfSchemaVisualizer.Core.Tests/RoundTripFuzzTests.cs
git commit -m "Add full-stack round-trip regression test for UsingEntity nested config"
```

---

### Task 10: Docs — README and backlog

**Files:**
- Modify: `README.md`
- Modify: `docs/backlog.md`

**Interfaces:** none — documentation only.

- [ ] **Step 1: Update README**

In `README.md`, remove this bullet from the "Unsupported EF Core features" list (currently lines 91-93):

```markdown
- `UsingEntity`'s nested join-entity configuration (the join entity itself is
  read/written, but calls chained inside its `UsingEntity(j => ...)` builder
  are not).
```

- [ ] **Step 2: Update backlog**

In `docs/backlog.md`, find the line:

```markdown
- [ ] **`[found]` `UsingEntity`'s nested join-entity configuration.** The join
      entity is read/written; calls chained inside `UsingEntity(j => ...)` are not.
```

Replace it with (flip to `[x]`, append an `**Update:**` paragraph in the file's established style — see the `HasPrincipalKey` entry immediately above it for the exact tone/format to match):

```markdown
- [x] **`[found]` `UsingEntity`'s nested join-entity configuration.** The join
      entity is read/written; calls chained inside `UsingEntity(j => ...)` are not.
      — Fixed 2026-07-30. See
      `docs/superpowers/specs/2026-07-30-using-entity-design.md`.
      All eight `UsingEntity` overload shapes are now parsed: generic `<T>` or
      shared-type string-named join entity identity; bare, single join-entity-
      config lambda, two per-side FK lambdas, or three-lambda combinations. The
      join-entity-config lambda is now treated as its own configuration scope
      (new `FluentSyntaxHelpers.FindUsingEntityNestedScopes`), so every existing
      per-property parser reads `HasKey`/`HasColumnName`/`Property<T>(...)`/etc.
      from it with no new extractor code — the same reuse this project already
      used for `OwnsOne`/`OwnsMany`/`ComplexProperty`. New
      `RelationshipModel.JoinEntityIsSharedType`/`JoinEntityRightForeignKey`/
      `JoinEntityLeftForeignKey` model the per-side FK lambdas; new
      `EntityModel.IsSharedType` marks a synthesized, class-less join entity for
      the string-named form, populated by the existing merge pipeline plus a new
      `ModelMerger.ApplyJoinEntityForeignKeyShadowProperties` for FK-referenced
      property names with no explicit `Property<T>()` declaration. Also fixed a
      pre-existing latent bug found while building this: `UsingEntity`'s per-side
      FK lambdas were walked into by the generic relationship scanner with no
      opaque boundary, so a nested `HasOne(...).WithMany(...)` inside one could
      be misread as a second, phantom top-level relationship — `FindAllCalls`'s
      walk now treats every `UsingEntity` lambda argument as an opaque boundary,
      generalizing the single-lambda exclusion previously used only for
      `OwnsOne`/`OwnsMany`/`ComplexProperty` builder lambdas.

      Also fixed the write-path data-loss gap this item implied: editing a
      many-to-many relationship's shape used to delete and fully rebuild the
      `HasMany().WithMany().UsingEntity(...)` statement from scratch on *any*
      edit, silently destroying hand-written join-entity config even when the
      edit had nothing to do with it. New
      `OnModelCreatingRewriter.TryCaptureUsingEntityArguments` captures the
      existing `UsingEntity(...)` call's arguments verbatim before the old
      statement is removed; `SetRelationship`/`DiagramEditor.SetRelationshipShape`
      now re-attach them unchanged on every edit this pass ships (none of them
      change FK/key config *inside* the lambdas, so preservation is always
      exact, not partial regeneration).

      **Documented non-goals (out of scope):** editing the join entity's
      per-side FK properties or its shared-type-ness via the UI — read, modeled,
      rendered, and round-trip-preserved, but changing them stays a hand-edit-
      the-source operation for now; nested config beyond
      `HasOne/WithMany/HasForeignKey` inside a per-side FK lambda (e.g. a
      `.HasConstraintName(...)`/`.OnDelete(...)` chained there) isn't parsed
      into a model field, though genuinely unrecognized calls there still fire
      the standard diagnostic; a shared-type join entity's `ToTable(...)`
      mapping works via the existing parser (no new code) but isn't specifically
      tested by this pass.
```

- [ ] **Step 3: Commit**

```bash
git add README.md docs/backlog.md
git commit -m "Update docs for UsingEntity nested join-entity configuration support"
```

---

## Final verification

- [ ] Run `dotnet test` one more time from the repo root and confirm the full suite is green.
- [ ] Re-read `docs/superpowers/specs/2026-07-30-using-entity-design.md` section by section and confirm each piece (Model, Parse, Merge, Validity check, Rewrite, Edit, UI, Out of scope) has a corresponding task above.
