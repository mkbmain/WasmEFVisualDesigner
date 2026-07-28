# Owned & Complex Types (Backlog Priority 2) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close the three remaining gaps from `docs/superpowers/specs/2026-07-28-owned-and-complex-types-design.md`: parse/render EF7+ `ComplexProperty`, parse fluent config chained inside `OwnsOne`/`OwnsMany`/`ComplexProperty` builder lambdas, and support editing owned/complex-folded properties.

**Architecture:** Three independently shippable phases, each ending with a full-suite green run and a commit.
- Phase 1 replaces `PropertyModel.IsOwned` (bool) with `PropertyModel.FoldKind` (enum: `None`/`Owned`/`Complex`), adds `ComplexProperty` parsing/inference/rendering as a sibling to the existing `OwnsOne`/`OwnsMany` path.
- Phase 2 makes an `OwnsOne`/`OwnsMany`/`ComplexProperty` builder lambda a first-class configuration scope (keyed by the resolved target type name) inside `FluentSyntaxHelpers.FindConfigurationScopes`, so every existing per-attribute extractor and `ModelMerger` pass picks it up automatically.
- Phase 3 wires folded properties into the existing edit machinery: structural edits (rename/retype/remove) via `DeclaringEntityName` stamping during fold (no `DiagramEditor` changes), fluent-attribute edits via a new `OnModelCreatingRewriter.FindOrCreateOwnedConfigScope`, and nav-property rename patching the outer call's lambda parameter via a new dedicated rewriter method.

**Tech Stack:** C# / .NET, Roslyn (`Microsoft.CodeAnalysis.CSharp`) for all source parsing/rewriting, xUnit for tests, Blazor (`.razor`) for the diagram UI.

## Global Constraints

- Every task must leave `dotnet test EfSchemaVisualizer.slnx` fully green before moving to the next task.
- Follow existing code style exactly: positional records with `with` expressions, `ParseResult<T>(Value, Diagnostics)` wrapper pattern, `Diagnostic(Code, Message, EntityName, PropertyName, Span)` construction, `IndexByProperty`-style single-pass lookups in `ModelMerger`.
- No behavior change is permitted as a side effect of the `IsOwned`→`FoldKind` rename (Task 1.1) — every existing assertion that currently reads `IsOwned` must read the equivalent `FoldKind` value with identical pass/fail outcome.
- `EntityModel.IsOwned` (a *different* field, marking an `OwnsMany` target as a standalone-but-owned card) is explicitly **out of scope** — do not rename or touch it. Grep before editing to confirm `PropertyModel.IsOwned` vs `EntityModel.IsOwned` at every site (see research point 2 above).
- Complex type **collections** are a non-goal: detect and flag (`ComplexPropertyCollectionUnsupported`), never fold.
- `ToTable`/`WithOwner` inside any builder lambda stay flagged-not-applied (never merged onto the target `EntityModel`).
- Nested owned-in-owned / complex-in-complex / owned-in-complex (a fold-producing call inside another fold-producing call's own builder lambda) remains unsupported — do not attempt to fix the pre-existing misattribution risk documented in `FluentConfigParser.cs` lines 1067-1080; only guard against the *new* double-counting risk introduced by Phase 2 (see Task 2.1).

---

### Task 1.1: Rename `PropertyModel.IsOwned` → `PropertyModel.FoldKind`

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/Model/PropertyModel.cs:25`
- Modify: `src/EfSchemaVisualizer.Core/Inference/OwnedTypeInference.cs:73-78`
- Modify: `src/EfSchemaVisualizer.Web/Diagram/EntityNode.razor:143,148,152,153,155`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Model/OwnedTypeModelFieldsTests.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Inference/OwnedTypeInferenceTests.cs:45,160`
- Test: `tests/EfSchemaVisualizer.Web.Tests/DiagramModelBuilderOwnedTypeTests.cs:45,46,82,128,129,133`

**Interfaces:**
- Consumes: nothing new.
- Produces: `public enum FoldKind { None, Owned, Complex }` in `EfSchemaVisualizer.Core.Model`; `PropertyModel.FoldKind` (default `FoldKind.None`) replacing `PropertyModel.IsOwned` (default `false`).

- [ ] **Step 1: Write the failing tests (update existing assertions first)**
```csharp
// tests/EfSchemaVisualizer.Core.Tests/Model/OwnedTypeModelFieldsTests.cs
[Fact]
public void PropertyModel_DefaultsLeaveFoldKindNone()
{
    var property = new PropertyModel("Street", "string", IsNullable: false, MaxLength: null);

    Assert.Equal(FoldKind.None, property.FoldKind);
    Assert.Null(property.OwnerNavigationProperty);
}
```
```csharp
// tests/EfSchemaVisualizer.Core.Tests/Inference/OwnedTypeInferenceTests.cs:45
Assert.Equal(FoldKind.Owned, street.FoldKind);
// ...and line 160:
Assert.True(foldedNote.IsOwned); // unchanged: this is EntityModel.IsOwned, NOT PropertyModel — do not touch
```
```csharp
// tests/EfSchemaVisualizer.Web.Tests/DiagramModelBuilderOwnedTypeTests.cs:45-46
Assert.Contains(order.Properties, p => p.Name == "Street" && p.FoldKind == FoldKind.Owned);
Assert.Contains(order.Properties, p => p.Name == "City" && p.FoldKind == FoldKind.Owned);
// lines 82, 133 (note.IsOwned) are EntityModel.IsOwned — leave unchanged
// lines 128-129: same pattern as 45-46
```
- [ ] **Step 2: Run tests to verify they fail to compile (FoldKind doesn't exist yet)**
Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter OwnedTypeModelFieldsTests`
Expected: FAIL with a compile error `The name 'FoldKind' does not exist in the current context` / `'PropertyModel' does not contain a definition for 'FoldKind'`.
- [ ] **Step 3: Write minimal implementation**
```csharp
// src/EfSchemaVisualizer.Core/Model/PropertyModel.cs
namespace EfSchemaVisualizer.Core.Model;

public enum FoldKind
{
    None,
    Owned,
    Complex,
}

public sealed record PropertyModel(
    string Name,
    string ClrType,
    bool IsNullable,
    int? MaxLength,
    // ... unchanged fields ...
    string? DeclaringEntityName = null,
    FoldKind FoldKind = FoldKind.None,
    string? OwnerNavigationProperty = null,
    // ... unchanged remaining fields ...
    string? SequenceName = null,
    string? SequenceSchema = null);
```
```csharp
// src/EfSchemaVisualizer.Core/Inference/OwnedTypeInference.cs:73-78
.Concat(targetProperties.Select(p => p with
{
    FoldKind = FoldKind.Owned,
    OwnerNavigationProperty = call.NavigationPropertyName,
}))
```
```razor
@* src/EfSchemaVisualizer.Web/Diagram/EntityNode.razor *@
@* line 143 *@
if (property.FoldKind == FoldKind.Owned && property.OwnerNavigationProperty != _lastOwnerNav)
{
    _lastOwnerNav = property.OwnerNavigationProperty;
    <li style="padding: 4px 8px 0; font-size: 0.7em; color: #888; border-top: 1px dashed #ccc;">@_lastOwnerNav (owned)</li>
}
else if (property.FoldKind != FoldKind.Owned)
{
    _lastOwnerNav = null;
}
@* line 152 *@
<li style="padding: 2px 8px; @(isKey ? "font-weight: bold;" : "") @(property.FoldKind == FoldKind.Owned ? "opacity: 0.75;" : "")">
    @if (property.FoldKind == FoldKind.Owned)
    {
        <span title="Folded in from the owned type via @property.OwnerNavigationProperty. Read-only.">@property.Name : @property.ClrType@(property.IsNullable ? "?" : "")</span>
    }
```
Note: leave the `else if (!property.IsOwned)` at old line 148 as `else if (property.FoldKind != FoldKind.Owned)` — Task 1.6 will extend this branch to also handle `FoldKind.Complex`; don't add `Complex` handling yet in this task (keep the diff a pure rename).
- [ ] **Step 4: Run tests to verify they pass**
Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter "OwnedTypeModelFieldsTests|OwnedTypeInferenceTests"` then `dotnet test tests/EfSchemaVisualizer.Web.Tests --filter DiagramModelBuilderOwnedTypeTests`
Expected: PASS, all previously-passing assertions still pass with identical outcomes.
- [ ] **Step 5: Commit**
```bash
git add src/EfSchemaVisualizer.Core/Model/PropertyModel.cs src/EfSchemaVisualizer.Core/Inference/OwnedTypeInference.cs src/EfSchemaVisualizer.Web/Diagram/EntityNode.razor tests/EfSchemaVisualizer.Core.Tests/Model/OwnedTypeModelFieldsTests.cs tests/EfSchemaVisualizer.Core.Tests/Inference/OwnedTypeInferenceTests.cs tests/EfSchemaVisualizer.Web.Tests/DiagramModelBuilderOwnedTypeTests.cs
git commit -m "$(cat <<'EOF'
Rename PropertyModel.IsOwned to FoldKind enum

Prepares for ComplexProperty support: FoldKind (None/Owned/Complex)
replaces the owned-only bool with no behavior change. EntityModel.IsOwned
is untouched (different concept — marks an OwnsMany target's own card).

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 1.2: `ComplexProperty` parsing — `FluentConfigParser.ParseComplexPropertyCalls`

**Files:**
- Create: `src/EfSchemaVisualizer.Core/Merging/ComplexTypeConfig.cs`
- Modify: `src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs:16-26` (RecognizedCallNames), and a new method appended after `ParseOwnedTypeCalls` (after current line 1124)
- Modify: `src/EfSchemaVisualizer.Core/Parsing/DiagnosticCodes.cs:44` (insert new code near `OwnedNestedConfigIgnored`)
- Test: `tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserComplexPropertyTests.cs` (new file, mirrors `FluentConfigParserOwnedTypeTests.cs`)

**Interfaces:**
- Consumes: `FluentSyntaxHelpers.FindConfigurationScopes`, `FindCallsNamed`, `TryReadSinglePropertyNameArgument`, `TryGetElementTypeName`.
- Produces: `public sealed record ComplexTypeConfig(string OwnerEntityName, string NavigationPropertyName);`, `FluentConfigParser.ParseComplexPropertyCalls(string sourceCode, IReadOnlyList<EntityModel> entities) : ParseResult<IReadOnlyList<ComplexTypeConfig>>`, `DiagnosticCodes.ComplexPropertyCollectionUnsupported`.

- [ ] **Step 1: Write the failing tests**
```csharp
// tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserComplexPropertyTests.cs
using System.Linq;
using EfSchemaVisualizer.Core.Model;
using EfSchemaVisualizer.Core.Parsing;
using Xunit;

namespace EfSchemaVisualizer.Core.Tests.Parsing;

public class FluentConfigParserComplexPropertyTests
{
    private static readonly FluentConfigParser Parser = new();

    private static PropertyModel Property(string name, string clrType) =>
        new(name, clrType, IsNullable: false, MaxLength: null);

    [Fact]
    public void ParseComplexPropertyCalls_SingularTarget_ResolvesOwnerAndNavigationProperty()
    {
        const string source = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Order>(entity =>
                    {
                        entity.ComplexProperty(e => e.ShippingAddress);
                    });
                }
            }
            """;

        var entities = new[]
        {
            new EntityModel("Order", new[] { Property("ShippingAddress", "Address") }),
            new EntityModel("Address", new[] { Property("Street", "string") }),
        };

        var result = Parser.ParseComplexPropertyCalls(source, entities);

        var config = Assert.Single(result.Value);
        Assert.Equal("Order", config.OwnerEntityName);
        Assert.Equal("ShippingAddress", config.NavigationPropertyName);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void ParseComplexPropertyCalls_CollectionTypedTarget_FlagsCollectionUnsupportedAndDoesNotFold()
    {
        const string source = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Order>(entity =>
                    {
                        entity.ComplexProperty(e => e.Tags);
                    });
                }
            }
            """;

        var entities = new[]
        {
            new EntityModel("Order", new[] { Property("Tags", "List<Tag>") }),
            new EntityModel("Tag", new[] { Property("Value", "string") }),
        };

        var result = Parser.ParseComplexPropertyCalls(source, entities);

        Assert.Empty(result.Value);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.ComplexPropertyCollectionUnsupported, diagnostic.Code);
        Assert.Equal("Order", diagnostic.EntityName);
        Assert.Equal("Tags", diagnostic.PropertyName);
    }

    [Fact]
    public void ParseUnrecognizedCalls_ComplexPropertyCall_NoLongerFlagged()
    {
        const string source = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Order>(entity =>
                    {
                        entity.ComplexProperty(e => e.ShippingAddress);
                    });
                }
            }
            """;

        Assert.Empty(Parser.ParseUnrecognizedCalls(source));
    }
}
```
- [ ] **Step 2: Run test to verify it fails**
Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter FluentConfigParserComplexPropertyTests`
Expected: FAIL to compile — `ComplexTypeConfig` and `ParseComplexPropertyCalls` don't exist yet; `ComplexProperty` also not in `RecognizedCallNames` so the third test would otherwise fail at runtime with a non-empty diagnostics list once it compiles.
- [ ] **Step 3: Write minimal implementation**
```csharp
// src/EfSchemaVisualizer.Core/Merging/ComplexTypeConfig.cs
namespace EfSchemaVisualizer.Core.Merging;

public sealed record ComplexTypeConfig(string OwnerEntityName, string NavigationPropertyName);
```
```csharp
// src/EfSchemaVisualizer.Core/Parsing/DiagnosticCodes.cs — insert after OwnedNestedConfigIgnored (line 44)
public const string ComplexPropertyCollectionUnsupported = nameof(ComplexPropertyCollectionUnsupported);
```
```csharp
// src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs — add "ComplexProperty" to RecognizedCallNames (line 25):
"SplitToTable", "OwnsOne", "OwnsMany", "ComplexProperty", "HasConstraintName", "HasDatabaseName", "HasCheckConstraint", "UseSequence",
```
```csharp
// src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs — new method, appended after ParseOwnedTypeCalls (after line 1124)

/// Detects `ComplexProperty` calls per entity scope, mirroring `ParseOwnedTypeCalls` but without
/// the `IsMany` dimension — a complex type is always singular. If the resolved navigation
/// property's declared type is a collection shape (recognized by
/// `FluentSyntaxHelpers.TryGetElementTypeName`'s wrapper-name check), the call is NOT folded —
/// `ComplexPropertyCollectionUnsupported` is emitted instead and no `ComplexTypeConfig` is
/// produced for it, leaving the property to render as a plain scalar/class reference.
public ParseResult<IReadOnlyList<ComplexTypeConfig>> ParseComplexPropertyCalls(
    string sourceCode, IReadOnlyList<EntityModel> entities)
{
    var tree = CSharpSyntaxTree.ParseText(sourceCode);
    var root = tree.GetCompilationUnitRoot();
    var entitiesByName = entities.ToDictionary(e => e.Name);

    var results = new List<ComplexTypeConfig>();
    var diagnostics = new List<Diagnostic>();

    foreach (var (entityName, scope) in FluentSyntaxHelpers.FindConfigurationScopes(root))
    {
        foreach (var call in FluentSyntaxHelpers.FindCallsNamed(scope, "ComplexProperty"))
        {
            var navigationPropertyName = FluentSyntaxHelpers.TryReadSinglePropertyNameArgument(call);

            if (navigationPropertyName is null)
            {
                diagnostics.Add(new Diagnostic(
                    DiagnosticCodes.UnresolvablePropertyName,
                    "Could not determine which navigation property this ComplexProperty call configures.",
                    entityName,
                    PropertyName: null,
                    call.Span));
                continue;
            }

            var isCollection = entitiesByName.TryGetValue(entityName, out var owner)
                && owner.Properties.FirstOrDefault(p => p.Name == navigationPropertyName) is { } navProperty
                && IsCollectionClrType(navProperty.ClrType);

            if (isCollection)
            {
                diagnostics.Add(new Diagnostic(
                    DiagnosticCodes.ComplexPropertyCollectionUnsupported,
                    $"ComplexProperty targets a collection-typed navigation property ('{navigationPropertyName}'), which isn't supported; left unfolded.",
                    entityName,
                    navigationPropertyName,
                    call.Span));
                continue;
            }

            results.Add(new ComplexTypeConfig(entityName, navigationPropertyName));

            if (HasNestedConfigCalls(call))
            {
                diagnostics.Add(new Diagnostic(
                    DiagnosticCodes.ComplexNestedConfigIgnored,
                    "Configuration inside this ComplexProperty call's builder is not read and was ignored.",
                    entityName,
                    navigationPropertyName,
                    call.Span));
            }
        }
    }

    return new ParseResult<IReadOnlyList<ComplexTypeConfig>>(results, diagnostics);
}

/// True when `clrType` is a recognized collection-wrapper shape or an array — reuses
/// `FluentSyntaxHelpers.TryGetElementTypeName`'s wrapper-name allowlist so "collection" is
/// defined identically everywhere in this codebase (ICollection/IList/List/IEnumerable/HashSet/ISet/T[]).
private static bool IsCollectionClrType(string clrType) =>
    clrType.EndsWith("[]", System.StringComparison.Ordinal)
    || (clrType.IndexOf('<') is var openIdx && openIdx >= 0
        && FluentSyntaxHelpers.TryGetElementTypeName(clrType) is not null
        && FluentSyntaxHelpers.TryGetElementTypeName(clrType) != clrType);
```
Note: `DiagnosticCodes.ComplexNestedConfigIgnored` referenced above does not exist yet — it is added in Task 2.2 (Phase 2), since it's only meaningful once the builder lambda is genuinely being parsed. For this task, **stub it out** by adding the constant now (co-locate with `ComplexPropertyCollectionUnsupported`) even though it isn't exercised by a passing Phase-1 test yet — this avoids a forward-reference compile error. Add a `[Fact]` in this task's test file asserting the constant exists (`Assert.Equal("ComplexNestedConfigIgnored", DiagnosticCodes.ComplexNestedConfigIgnored)`) so its presence is covered by a green test before Task 2.2 gives it real behavior.
- [ ] **Step 4: Run test to verify it passes**
Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter "FluentConfigParserComplexPropertyTests|FluentConfigParserOwnedTypeTests"`
Expected: PASS. Also re-run the full `FluentConfigParserOwnedTypeTests` to confirm adding `ComplexProperty` to `RecognizedCallNames` didn't change `OwnsOne`/`OwnsMany` behavior.
- [ ] **Step 5: Commit**
```bash
git add src/EfSchemaVisualizer.Core/Merging/ComplexTypeConfig.cs src/EfSchemaVisualizer.Core/Parsing/DiagnosticCodes.cs src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserComplexPropertyTests.cs
git commit -m "$(cat <<'EOF'
Parse ComplexProperty calls, flagging collection-typed targets

Adds FluentConfigParser.ParseComplexPropertyCalls, structurally mirroring
ParseOwnedTypeCalls minus the OwnsMany dimension. A collection-typed
ComplexProperty target is detected and flagged (ComplexPropertyCollectionUnsupported)
rather than folded, since EF's relational story for those is JSON-only.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 1.3: `ComplexTypeInference.Fold` — always-fold splice, no relationship

**Files:**
- Create: `src/EfSchemaVisualizer.Core/Inference/ComplexTypeInference.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Inference/ComplexTypeInferenceTests.cs` (new, mirrors `OwnedTypeInferenceTests.cs`)

**Interfaces:**
- Consumes: `EntityModel`, `PropertyModel`, `ComplexTypeConfig`, `FoldKind`.
- Produces: `public sealed record ComplexTypeFoldResult(IReadOnlyList<EntityModel> Entities);` (no `Relationships` — a complex type is never its own table), `ComplexTypeInference.Fold(IReadOnlyList<EntityModel> entities, IReadOnlyList<ComplexTypeConfig> complexCalls) : ComplexTypeFoldResult`.

- [ ] **Step 1: Write the failing tests**
```csharp
// tests/EfSchemaVisualizer.Core.Tests/Inference/ComplexTypeInferenceTests.cs
using System.Collections.Generic;
using System.Linq;
using EfSchemaVisualizer.Core.Inference;
using EfSchemaVisualizer.Core.Merging;
using EfSchemaVisualizer.Core.Model;
using Xunit;

namespace EfSchemaVisualizer.Core.Tests.Inference;

public class ComplexTypeInferenceTests
{
    private static PropertyModel Property(string name, string clrType) =>
        new(name, clrType, IsNullable: false, MaxLength: null);

    [Fact]
    public void Fold_NoComplexCalls_ReturnsEntitiesUnchanged()
    {
        var order = new EntityModel("Order", new[] { Property("Id", "int") }, KeyPropertyNames: new[] { "Id" });

        var result = ComplexTypeInference.Fold(new[] { order }, Array.Empty<ComplexTypeConfig>());

        Assert.Same(order, Assert.Single(result.Entities));
    }

    [Fact]
    public void Fold_ComplexProperty_RemovesNavPropertyAndSplicesInComplexProperties()
    {
        var order = new EntityModel(
            "Order",
            new[] { Property("Id", "int"), Property("ShippingAddress", "Address") },
            KeyPropertyNames: new[] { "Id" });
        var address = new EntityModel("Address", new[] { Property("Street", "string"), Property("City", "string") });

        var result = ComplexTypeInference.Fold(
            new[] { order, address },
            new[] { new ComplexTypeConfig("Order", "ShippingAddress") });

        var foldedOrder = Assert.Single(result.Entities);
        Assert.Equal(new[] { "Id", "Street", "City" }, foldedOrder.Properties.Select(p => p.Name));

        var street = foldedOrder.Properties.Single(p => p.Name == "Street");
        Assert.Equal(FoldKind.Complex, street.FoldKind);
        Assert.Equal("ShippingAddress", street.OwnerNavigationProperty);
    }

    [Fact]
    public void Fold_ComplexPropertyTarget_AbsentFromResult()
    {
        var order = new EntityModel("Order", new[] { Property("ShippingAddress", "Address") });
        var address = new EntityModel("Address", new[] { Property("Street", "string") });

        var result = ComplexTypeInference.Fold(
            new[] { order, address },
            new[] { new ComplexTypeConfig("Order", "ShippingAddress") });

        Assert.DoesNotContain(result.Entities, e => e.Name == "Address");
    }

    [Fact]
    public void Fold_MultiLevelComplexChain_FoldsTransitively()
    {
        var order = new EntityModel("Order", new[] { Property("ShippingAddress", "Address") });
        var address = new EntityModel("Address", new[] { Property("Street", "string"), Property("Country", "Country") });
        var country = new EntityModel("Country", new[] { Property("Name", "string") });

        var result = ComplexTypeInference.Fold(
            new[] { order, address, country },
            new[]
            {
                new ComplexTypeConfig("Order", "ShippingAddress"),
                new ComplexTypeConfig("Address", "Country"),
            });

        var foldedOrder = Assert.Single(result.Entities);
        Assert.Equal(new[] { "Street", "Name" }, foldedOrder.Properties.Select(p => p.Name));
    }

    [Fact]
    public void Fold_MalformedComplexCycle_DoesNotThrowAndStopsAtCycle()
    {
        var a = new EntityModel("A", new[] { Property("BNav", "B") });
        var b = new EntityModel("B", new[] { Property("ANav", "A") });

        var result = ComplexTypeInference.Fold(
            new[] { a, b },
            new[]
            {
                new ComplexTypeConfig("A", "BNav"),
                new ComplexTypeConfig("B", "ANav"),
            });

        Assert.True(result.Entities.Count >= 1);
    }
}
```
- [ ] **Step 2: Run test to verify it fails**
Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter ComplexTypeInferenceTests`
Expected: FAIL to compile — `ComplexTypeInference` doesn't exist.
- [ ] **Step 3: Write minimal implementation**
```csharp
// src/EfSchemaVisualizer.Core/Inference/ComplexTypeInference.cs
using System.Collections.Generic;
using System.Linq;
using EfSchemaVisualizer.Core.Merging;
using EfSchemaVisualizer.Core.Model;

namespace EfSchemaVisualizer.Core.Inference;

public sealed record ComplexTypeFoldResult(IReadOnlyList<EntityModel> Entities);

/// Sibling to `OwnedTypeInference`, sharing the same splice/cycle-guard recursion shape but with
/// no `IsMany` dimension and no `RelationshipModel` emission: a complex type is never its own
/// table, so unlike `OwnsMany` there is nothing to draw an edge to — every `ComplexProperty` call
/// always folds (or is filtered out upstream by `ParseComplexPropertyCalls` for collection-typed
/// targets before it ever reaches here).
public static class ComplexTypeInference
{
    public static ComplexTypeFoldResult Fold(IReadOnlyList<EntityModel> entities, IReadOnlyList<ComplexTypeConfig> complexCalls)
    {
        var byName = entities.ToDictionary(e => e.Name);
        var callsByOwner = complexCalls.ToLookup(c => c.OwnerEntityName);
        var removedEntityNames = new HashSet<string>();
        var memo = new Dictionary<string, IReadOnlyList<PropertyModel>>();

        IReadOnlyList<PropertyModel>? ResolveFoldedProperties(string entityName, HashSet<string> visited)
        {
            if (memo.TryGetValue(entityName, out var cached))
            {
                return cached;
            }

            if (!byName.TryGetValue(entityName, out var entity))
            {
                return null;
            }

            if (!visited.Add(entityName))
            {
                return null;
            }

            IReadOnlyList<PropertyModel> properties = entity.Properties;

            foreach (var call in callsByOwner[entityName])
            {
                var navProperty = properties.FirstOrDefault(p => p.Name == call.NavigationPropertyName);
                if (navProperty is null || !byName.ContainsKey(navProperty.ClrType))
                {
                    continue;
                }

                var targetName = navProperty.ClrType;
                var targetProperties = ResolveFoldedProperties(targetName, visited);

                if (targetProperties is null)
                {
                    continue;
                }

                properties = properties
                    .Where(p => p.Name != call.NavigationPropertyName)
                    .Concat(targetProperties.Select(p => p with
                    {
                        FoldKind = FoldKind.Complex,
                        OwnerNavigationProperty = call.NavigationPropertyName,
                    }))
                    .ToList();

                removedEntityNames.Add(targetName);
            }

            visited.Remove(entityName);
            memo[entityName] = properties;
            return properties;
        }

        foreach (var entity in entities)
        {
            ResolveFoldedProperties(entity.Name, new HashSet<string>());
        }

        var foldedEntities = entities
            .Where(e => !removedEntityNames.Contains(e.Name))
            .Select(e =>
            {
                var properties = memo.TryGetValue(e.Name, out var p) ? p : e.Properties;
                return ReferenceEquals(properties, e.Properties) ? e : e with { Properties = properties };
            })
            .ToList();

        return new ComplexTypeFoldResult(foldedEntities);
    }
}
```
- [ ] **Step 4: Run test to verify it passes**
Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter ComplexTypeInferenceTests`
Expected: PASS.
- [ ] **Step 5: Commit**
```bash
git add src/EfSchemaVisualizer.Core/Inference/ComplexTypeInference.cs tests/EfSchemaVisualizer.Core.Tests/Inference/ComplexTypeInferenceTests.cs
git commit -m "$(cat <<'EOF'
Add ComplexTypeInference.Fold, always-folding sibling to OwnedTypeInference

Complex types (EF7+) never stay standalone and never produce a
RelationshipModel — unlike OwnsMany there's nothing to draw an edge to,
so this reuses the owned-fold's splice/cycle-guard recursion shape with
that dimension removed.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 1.4: Wire `ComplexProperty` parsing + fold into `DiagramModelBuilder.Build`

**Files:**
- Modify: `src/EfSchemaVisualizer.Web/DiagramModelBuilder.cs:55,99,144-148`
- Test: `tests/EfSchemaVisualizer.Web.Tests/DiagramModelBuilderComplexPropertyTests.cs` (new, mirrors `DiagramModelBuilderOwnedTypeTests.cs`)

**Interfaces:**
- Consumes: `FluentConfigParser.ParseComplexPropertyCalls`, `ComplexTypeInference.Fold`.
- Produces: `DiagramModelResult.Entities` with complex properties folded in.

- [ ] **Step 1: Write the failing test**
```csharp
// tests/EfSchemaVisualizer.Web.Tests/DiagramModelBuilderComplexPropertyTests.cs
using System.Linq;
using EfSchemaVisualizer.Core.Model;
using EfSchemaVisualizer.Web;
using Xunit;

namespace EfSchemaVisualizer.Web.Tests;

public class DiagramModelBuilderComplexPropertyTests
{
    [Fact]
    public void Build_ComplexProperty_AddressNotStandaloneAndOrderHasFoldedComplexProperties()
    {
        const string classSource = """
            public class Order
            {
                public int Id { get; set; }
                public Address ShippingAddress { get; set; }
            }

            public class Address
            {
                public string Street { get; set; }
                public string City { get; set; }
            }
            """;

        const string configSource = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Order>(entity =>
                    {
                        entity.ComplexProperty(e => e.ShippingAddress);
                    });
                }
            }
            """;

        var result = DiagramModelBuilder.Build(classSource, configSource);

        Assert.DoesNotContain(result.Entities, e => e.Name == "Address");
        var order = result.Entities.Single(e => e.Name == "Order");
        Assert.Contains(order.Properties, p => p.Name == "Street" && p.FoldKind == FoldKind.Complex);
        Assert.Contains(order.Properties, p => p.Name == "City" && p.FoldKind == FoldKind.Complex);
        Assert.DoesNotContain(order.Properties, p => p.Name == "ShippingAddress");
        Assert.DoesNotContain(result.Relationships, r => r.DependentEntity == "Address" || r.PrincipalEntity == "Address");
    }
}
```
- [ ] **Step 2: Run test to verify it fails**
Run: `dotnet test tests/EfSchemaVisualizer.Web.Tests --filter DiagramModelBuilderComplexPropertyTests`
Expected: FAIL — `Address` still present in `result.Entities`, `ShippingAddress` still present on `Order` (ComplexProperty isn't parsed/folded yet since `Build` never calls the new methods).
- [ ] **Step 3: Write minimal implementation**
```csharp
// src/EfSchemaVisualizer.Web/DiagramModelBuilder.cs
// after line 55 (var ownedTypeCalls = configParser.ParseOwnedTypeCalls(configSource);)
var complexPropertyCalls = configParser.ParseComplexPropertyCalls(configSource, entityResult.Value);

// after line 99 (diagnostics.AddRange(ownedTypeCalls.Diagnostics);)
diagnostics.AddRange(complexPropertyCalls.Diagnostics);

// replace lines 144-148:
var complexTypeFold = ComplexTypeInference.Fold(mergedEntities, complexPropertyCalls.Value);
var ownedTypeFold = OwnedTypeInference.Fold(complexTypeFold.Entities, ownedTypeCalls.Value);

IReadOnlyList<EntityModel> entities = ownedTypeFold.Entities
    .Select(ConventionInference.InferKey)
    .ToList();
```
Ordering note: `ComplexTypeInference.Fold` runs first so an entity with both a `ComplexProperty` and an `OwnsOne` on *different* nav properties folds both correctly regardless of order (they don't interact); complex fold first is chosen only for determinism, not because order matters for correctness here — nested owned-in-complex or complex-in-owned remains out of scope per the spec's non-goals either way.
- [ ] **Step 4: Run test to verify it passes**
Run: `dotnet test tests/EfSchemaVisualizer.Web.Tests --filter "DiagramModelBuilderComplexPropertyTests|DiagramModelBuilderOwnedTypeTests"`
Expected: PASS.
- [ ] **Step 5: Commit**
```bash
git add src/EfSchemaVisualizer.Web/DiagramModelBuilder.cs tests/EfSchemaVisualizer.Web.Tests/DiagramModelBuilderComplexPropertyTests.cs
git commit -m "$(cat <<'EOF'
Wire ComplexProperty parsing and fold into DiagramModelBuilder.Build

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 1.5: `EntityNode.razor` — render `FoldKind.Complex` with a distinct marker

**Files:**
- Modify: `src/EfSchemaVisualizer.Web/Diagram/EntityNode.razor:143-156`
- Test: manual/browser verification only (Blazor `.razor` files aren't unit-testable in this repo's current test setup — confirmed no `EntityNode` component tests exist under `tests/`); add a `DiagramModelBuilder`-level assertion instead that a `FoldKind.Complex` property round-trips as expected (already covered by Task 1.4's test) — this task is presentation-only.

**Interfaces:**
- Consumes: `PropertyModel.FoldKind`.
- Produces: visually distinct sub-header + marker for `FoldKind.Complex` rows, parallel to the existing `FoldKind.Owned` "◆" styling but visually distinct (e.g. "●" or a different color) so a user can tell at a glance which EF concept produced a group.

- [ ] **Step 1: Write the failing test**
No new automated test (presentation-only, no existing `.razor` component test harness in this repo). Instead, write a short manual verification checklist as a code comment placeholder is not appropriate — skip Step 1/2 automated-test steps for this task and proceed directly to implementation, per the note above. (If a bUnit-style component test harness is later added to the repo, this task should gain one; none exists today, confirmed via `find tests -iname "*.razor*"` returning nothing.)
- [ ] **Step 2: N/A** (see Step 1 note)
- [ ] **Step 3: Write minimal implementation**
```razor
@* src/EfSchemaVisualizer.Web/Diagram/EntityNode.razor — replace lines 138-156 *@
<ul style="list-style: none; margin: 0; padding: 0;">
    @{ string? _lastOwnerNav = null; }
    @foreach (var property in Node.Entity.Properties)
    {
        var isKey = Node.Entity.KeyPropertyNames.Contains(property.Name);
        if (property.FoldKind != FoldKind.None && property.OwnerNavigationProperty != _lastOwnerNav)
        {
            _lastOwnerNav = property.OwnerNavigationProperty;
            var kindLabel = property.FoldKind == FoldKind.Complex ? "complex" : "owned";
            <li style="padding: 4px 8px 0; font-size: 0.7em; color: #888; border-top: 1px dashed #ccc;">@_lastOwnerNav (@kindLabel)</li>
        }
        else if (property.FoldKind == FoldKind.None)
        {
            _lastOwnerNav = null;
        }
        <li style="padding: 2px 8px; @(isKey ? "font-weight: bold;" : "") @(property.FoldKind != FoldKind.None ? "opacity: 0.75;" : "")">
            @if (property.FoldKind == FoldKind.Owned)
            {
                <span title="Folded in from the owned type via @property.OwnerNavigationProperty.">
                    <span style="opacity: 0.6; margin-right: 2px;">◆</span>@property.Name : @property.ClrType@(property.IsNullable ? "?" : "")
                </span>
            }
            else if (property.FoldKind == FoldKind.Complex)
            {
                <span title="Folded in from the complex type via @property.OwnerNavigationProperty.">
                    <span style="opacity: 0.6; margin-right: 2px; color: #4a8a6a;">●</span>@property.Name : @property.ClrType@(property.IsNullable ? "?" : "")
                </span>
            }
```
Note: Task 3.x (Phase 3) will revisit this block again to make folded properties editable instead of a plain `<span>` — this task only adds the `Complex` visual branch alongside the existing read-only `Owned` one, keeping the diff minimal and matching Phase 1's "no editing yet" scope.
- [ ] **Step 4: Verify manually**
Run: `dotnet run --project src/EfSchemaVisualizer.Web` (or the repo's existing `run` workflow), load a sample with a `ComplexProperty` call, confirm the "●" marker and "(complex)" sub-header render distinctly from "◆"/"(owned)".
- [ ] **Step 5: Commit**
```bash
git add src/EfSchemaVisualizer.Web/Diagram/EntityNode.razor
git commit -m "$(cat <<'EOF'
Render FoldKind.Complex properties with a distinct marker from owned

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 1.6: Full Phase 1 regression run

**Files:** none (verification only).

- [ ] **Step 1: Run the full suite**
Run: `dotnet test EfSchemaVisualizer.slnx`
Expected: 100% pass, including every pre-existing test (no `IsOwned`/`FoldKind` fixture needed changing beyond Task 1.1's rename sites).
- [ ] **Step 2: Commit** (only if Step 1 required any fix-up changes; otherwise skip — no empty commits)

---

### Task 2.1: `FluentSyntaxHelpers` — nested builder-lambda scope discovery + opaque-boundary fix

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/Parsing/FluentSyntaxHelpers.cs:18-51` (`FindAllCalls`), `446-495` (`FindConfigurationScopes`)
- Modify: `src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs` — add a constructor, replace ~20 call sites of `FluentSyntaxHelpers.FindConfigurationScopes(root)` with `FluentSyntaxHelpers.FindConfigurationScopes(root, _entities)`
- Modify: `src/EfSchemaVisualizer.Web/DiagramModelBuilder.cs:21` (`new FluentConfigParser()` → `new FluentConfigParser(entityResult.Value)`)
- Test: `tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentSyntaxHelpersNestedScopeTests.cs` (new — this exercises `FluentSyntaxHelpers` directly; it's `internal`, so this test file must live in `EfSchemaVisualizer.Core.Tests` which already has `InternalsVisibleTo` access per line 8 of `FluentSyntaxHelpers.cs`)

**Interfaces:**
- Consumes: `EntityModel` list (for target-type resolution).
- Produces: `FluentSyntaxHelpers.FindConfigurationScopes(CompilationUnitSyntax root, IReadOnlyList<EntityModel>? entities = null)` yielding additional `(TargetTypeName, BuilderLambdaScope)` pairs for `OwnsOne`/`OwnsMany`/`ComplexProperty` calls whose builder lambda has a block body; `FindAllCalls`'s opaque boundary extended so a builder-lambda call's node is still visited but its builder-lambda subtree is not double-walked.

- [ ] **Step 1: Write the failing tests**
```csharp
// tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentSyntaxHelpersNestedScopeTests.cs
using System.Linq;
using EfSchemaVisualizer.Core.Model;
using EfSchemaVisualizer.Core.Parsing;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace EfSchemaVisualizer.Core.Tests.Parsing;

public class FluentSyntaxHelpersNestedScopeTests
{
    private static PropertyModel Property(string name, string clrType) =>
        new(name, clrType, IsNullable: false, MaxLength: null);

    [Fact]
    public void FindConfigurationScopes_WithEntities_YieldsOwnsOneBuilderLambdaKeyedByTargetTypeName()
    {
        const string source = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Order>(entity =>
                    {
                        entity.OwnsOne(e => e.ShippingAddress, b =>
                        {
                            b.Property(a => a.Street).HasMaxLength(100);
                        });
                    });
                }
            }
            """;

        var root = CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot();
        var entities = new[]
        {
            new EntityModel("Order", new[] { Property("ShippingAddress", "Address") }),
            new EntityModel("Address", new[] { Property("Street", "string") }),
        };

        var scopes = FluentSyntaxHelpers.FindConfigurationScopes(root, entities).ToList();

        Assert.Contains(scopes, s => s.EntityName == "Order");
        Assert.Contains(scopes, s => s.EntityName == "Address");

        var addressScope = scopes.Single(s => s.EntityName == "Address").Scope;
        var maxLengthCall = Assert.Single(FluentSyntaxHelpers.FindCallsNamed(addressScope, "HasMaxLength"));
        Assert.Equal("Street", FluentSyntaxHelpers.GetPropertyNameFor(maxLengthCall));
    }

    [Fact]
    public void FindConfigurationScopes_WithoutEntities_BehavesExactlyAsBefore()
    {
        const string source = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Order>(entity => { entity.OwnsOne(e => e.ShippingAddress, b => { b.Property(a => a.Street).HasMaxLength(100); }); });
                }
            }
            """;

        var root = CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot();

        var scopes = FluentSyntaxHelpers.FindConfigurationScopes(root).ToList();

        Assert.Single(scopes);
        Assert.Equal("Order", scopes[0].EntityName);
    }

    [Fact]
    public void FindCallsNamed_OuterEntityScope_DoesNotDescendIntoOwnsOneBuilderLambdaButStillFindsTheOwnsOneCallItself()
    {
        const string source = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Order>(entity =>
                    {
                        entity.OwnsOne(e => e.ShippingAddress, b =>
                        {
                            b.Property(a => a.Street).HasMaxLength(100);
                        });
                    });
                }
            }
            """;

        var root = CSharpSyntaxTree.ParseText(source).GetCompilationUnitRoot();
        var entities = new[]
        {
            new EntityModel("Order", new[] { Property("ShippingAddress", "Address") }),
            new EntityModel("Address", new[] { Property("Street", "string") }),
        };

        var orderScope = FluentSyntaxHelpers.FindConfigurationScopes(root, entities)
            .Single(s => s.EntityName == "Order").Scope;

        // The OwnsOne call itself is still found directly on the outer (Order) scope...
        Assert.Single(FluentSyntaxHelpers.FindCallsNamed(orderScope, "OwnsOne"));
        // ...but HasMaxLength inside its builder lambda is NOT double-counted against Order's scope,
        // since it now belongs to the separately-yielded Address scope.
        Assert.Empty(FluentSyntaxHelpers.FindCallsNamed(orderScope, "HasMaxLength"));
    }
}
```
- [ ] **Step 2: Run test to verify it fails**
Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter FluentSyntaxHelpersNestedScopeTests`
Expected: FAIL — `FindConfigurationScopes(root, entities)` overload doesn't exist yet (compile error), and once stubbed in, the third test would fail because `FindCallsNamed(orderScope, "HasMaxLength")` currently returns the nested call (pre-existing opaque-boundary gap documented in `ParseOwnedTypeCalls`'s doc comment).
- [ ] **Step 3: Write minimal implementation**
```csharp
// src/EfSchemaVisualizer.Core/Parsing/FluentSyntaxHelpers.cs

// Replace FindAllCalls's Walk (lines 32-50):
void Walk(SyntaxNode node)
{
    foreach (var child in node.ChildNodes())
    {
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

            if (TryGetFoldingBuilderLambda(invocation) is { } builderLambda)
            {
                // Opaque boundary for the builder-lambda subtree only: the OwnsOne/OwnsMany/
                // ComplexProperty invocation itself IS added to results above (so
                // FindCallsNamed(scope, "OwnsOne") still matches it directly), but its builder
                // lambda's body is skipped here — once FindConfigurationScopes(root, entities)
                // yields that builder lambda as its own scope (see below), walking into it here
                // too would double-count every call inside it against the outer entity's scope.
                foreach (var otherChild in child.ChildNodes().Where(c => c != builderLambda))
                {
                    Walk(otherChild);
                }

                continue;
            }
        }

        Walk(child);
    }
}

/// If `invocation` is an `OwnsOne`/`OwnsMany`/`ComplexProperty` call with a second (builder)
/// lambda argument, returns that lambda node; otherwise null. The first lambda argument is
/// always the navigation-property selector, never the builder (same convention as
/// `ParseOwnedTypeCalls`'s `HasNestedConfigCalls`).
private static AnonymousFunctionExpressionSyntax? TryGetFoldingBuilderLambda(InvocationExpressionSyntax invocation)
{
    if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: var name }
        || (name != "OwnsOne" && name != "OwnsMany" && name != "ComplexProperty"))
    {
        return null;
    }

    return invocation.ArgumentList.Arguments
        .Select(a => a.Expression)
        .OfType<AnonymousFunctionExpressionSyntax>()
        .Skip(1)
        .FirstOrDefault();
}
```
```csharp
// Extend FindConfigurationScopes (existing signature at line 446) with an optional entities param:
internal static IEnumerable<(string EntityName, SyntaxNode Scope)> FindConfigurationScopes(
    CompilationUnitSyntax root, IReadOnlyList<EntityModel>? entities = null)
{
    // ... existing body unchanged, yielding Entity<T>() and IEntityTypeConfiguration<T> scopes ...

    if (entities is not null)
    {
        foreach (var nested in FindOwnedAndComplexNestedScopes(root, entities))
        {
            yield return nested;
        }
    }
}

/// For every OwnsOne/OwnsMany/ComplexProperty call found in an already-discovered entity scope,
/// resolves the target type via `entities` (the pre-fold class-parsed model — the only place that
/// knows what CLR type a navigation property like `ShippingAddress` points to; config-source syntax
/// alone never spells out a type) and, if the call has a block-bodied builder lambda, yields that
/// lambda's block as a scope keyed by the target type's name — so every existing per-attribute
/// Parse* method picks up config chained inside it via FindCallsNamed(scope, ...) with zero
/// extractor changes.
private static IEnumerable<(string EntityName, SyntaxNode Scope)> FindOwnedAndComplexNestedScopes(
    CompilationUnitSyntax root, IReadOnlyList<EntityModel> entities)
{
    var byName = entities.ToDictionary(e => e.Name);

    foreach (var (ownerEntityName, scope) in FindConfigurationScopes(root))
    {
        if (!byName.TryGetValue(ownerEntityName, out var owner))
        {
            continue;
        }

        foreach (var callName in new[] { "OwnsOne", "OwnsMany", "ComplexProperty" })
        {
            foreach (var call in FindCallsNamed(scope, callName))
            {
                var navPropertyName = TryReadSinglePropertyNameArgument(call);
                var navProperty = navPropertyName is null
                    ? null
                    : owner.Properties.FirstOrDefault(p => p.Name == navPropertyName);

                if (navProperty is null)
                {
                    continue;
                }

                var targetTypeName = callName == "OwnsMany"
                    ? TryGetElementTypeName(navProperty.ClrType)
                    : navProperty.ClrType;

                if (targetTypeName is null || !byName.ContainsKey(targetTypeName))
                {
                    continue;
                }

                var builderLambda = TryGetFoldingBuilderLambda(call);
                if (builderLambda?.Block is { } block)
                {
                    yield return (targetTypeName, block);
                }
            }
        }
    }
}
```
```csharp
// src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs — add near the top of the class (after RecognizedCallNames/ContextSensitiveCallNames):
private readonly IReadOnlyList<EntityModel> _entities;

public FluentConfigParser(IReadOnlyList<EntityModel>? entities = null)
{
    _entities = entities ?? Array.Empty<EntityModel>();
}
```
Then mechanically replace every occurrence of `FluentSyntaxHelpers.FindConfigurationScopes(root)` inside `FluentConfigParser.cs` (there are ~20 — every `Parse*` method that iterates `foreach (var (entityName, scope) in FluentSyntaxHelpers.FindConfigurationScopes(root))`, e.g. `ParseUnrecognizedCalls` line 48, `ParseColumnNames` line 1147, and every sibling `Parse*` method for `HasMaxLength`, `HasPrecision`, `IsRequired`, `IsUnicode`, `IsFixedLength`, `HasKey`, `HasAlternateKey`, `HasColumnType`, `HasDefaultValue`, `HasDefaultValueSql`, `HasComputedColumnSql`, `HasIndex`, `HasComment` (property variant), value-generation, concurrency tokens, shadow properties, `UseSequence`, `HasCheckConstraint`) with `FluentSyntaxHelpers.FindConfigurationScopes(root, _entities)`. Leave `ParseOwnedTypeCalls` and `ParseComplexPropertyCalls` untouched here — they don't need the nested scopes themselves (they're what *produces* the nav-property-to-target mapping the nested-scope resolution depends on; including them would be self-referential and harmless but pointless).
```csharp
// src/EfSchemaVisualizer.Web/DiagramModelBuilder.cs:21
var configParser = new FluentConfigParser(entityResult.Value);
```
- [ ] **Step 4: Run test to verify it passes**
Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter "FluentSyntaxHelpersNestedScopeTests|FluentConfigParserOwnedTypeTests|FluentConfigParserComplexPropertyTests"`
Expected: PASS. Also run the full Core test project to catch any of the ~20 mechanically-edited call sites that were missed or mis-edited: `dotnet test tests/EfSchemaVisualizer.Core.Tests`
- [ ] **Step 5: Commit**
```bash
git add src/EfSchemaVisualizer.Core/Parsing/FluentSyntaxHelpers.cs src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs src/EfSchemaVisualizer.Web/DiagramModelBuilder.cs tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentSyntaxHelpersNestedScopeTests.cs
git commit -m "$(cat <<'EOF'
Treat OwnsOne/OwnsMany/ComplexProperty builder lambdas as configuration scopes

FindConfigurationScopes now optionally accepts the class-parsed entity
list (the only place a nav property's target CLR type is resolvable) and
yields each builder lambda's block as an additional scope keyed by the
target type's name, so every existing per-attribute Parse* method and
ModelMerger pass picks up nested config for free. FindAllCalls gains a
second opaque-boundary case so builder-lambda calls aren't double-counted
against the outer entity's scope now that they have their own.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 2.2: Narrow `OwnedNestedConfigIgnored` / activate `ComplexNestedConfigIgnored` to `ToTable`/`WithOwner` only

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs:1058-1137` (`ParseOwnedTypeCalls`, `HasNestedConfigCalls`) and the `ParseComplexPropertyCalls` method from Task 1.2
- Modify (existing tests that must change per research point 14): `tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserOwnedTypeTests.cs:60-83`, `tests/EfSchemaVisualizer.Web.Tests/DiagramModelBuilderOwnedTypeTests.cs:135-166`
- Test: extend `tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserComplexPropertyTests.cs` with the `ComplexNestedConfigIgnored` cases

**Interfaces:**
- Consumes: nothing new.
- Produces: `HasNestedConfigCalls` replaced by a narrower `HasIgnoredNestedConfigCalls` that only matches `ToTable`/`WithOwner` by name (not "any call").

- [ ] **Step 1: Update the existing tests to the new trigger, and add new ones**
```csharp
// tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserOwnedTypeTests.cs:60-83 — replace body's trigger call
[Fact]
public void ParseOwnedTypeCalls_BuilderLambdaWithToTable_FiresOwnedNestedConfigIgnored()
{
    const string source = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Order>(entity =>
                {
                    entity.OwnsOne(e => e.ShippingAddress, b =>
                    {
                        b.ToTable("Addresses");
                    });
                });
            }
        }
        """;

    var result = Parser.ParseOwnedTypeCalls(source);

    Assert.Single(result.Value);
    var diagnostic = Assert.Single(result.Diagnostics);
    Assert.Equal(DiagnosticCodes.OwnedNestedConfigIgnored, diagnostic.Code);
}

[Fact]
public void ParseOwnedTypeCalls_BuilderLambdaWithHasMaxLength_NoLongerFiresOwnedNestedConfigIgnored()
{
    const string source = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Order>(entity =>
                {
                    entity.OwnsOne(e => e.ShippingAddress, b =>
                    {
                        b.Property(a => a.Street).HasMaxLength(100);
                    });
                });
            }
        }
        """;

    var result = Parser.ParseOwnedTypeCalls(source);

    Assert.Empty(result.Diagnostics);
}
```
```csharp
// tests/EfSchemaVisualizer.Web.Tests/DiagramModelBuilderOwnedTypeTests.cs:135-166 — same trigger swap:
// change `b.Property(a => a.Street).HasMaxLength(100);` to `b.ToTable("Addresses");`
// and rename the test to Build_OwnsOneWithToTableInBuilder_SurfacesOwnedNestedConfigIgnoredDiagnostic
```
```csharp
// tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserComplexPropertyTests.cs — add:
[Fact]
public void ParseComplexPropertyCalls_BuilderLambdaWithWithOwner_FiresComplexNestedConfigIgnored()
{
    const string source = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Order>(entity =>
                {
                    entity.ComplexProperty(e => e.ShippingAddress, b =>
                    {
                        b.WithOwner();
                    });
                });
            }
        }
        """;

    var entities = new[]
    {
        new EntityModel("Order", new[] { new PropertyModel("ShippingAddress", "Address", IsNullable: false, MaxLength: null) }),
        new EntityModel("Address", new[] { new PropertyModel("Street", "string", IsNullable: false, MaxLength: null) }),
    };

    var result = Parser.ParseComplexPropertyCalls(source, entities);

    var diagnostic = Assert.Single(result.Diagnostics);
    Assert.Equal(DiagnosticCodes.ComplexNestedConfigIgnored, diagnostic.Code);
}

[Fact]
public void ParseComplexPropertyCalls_BuilderLambdaWithHasColumnName_NoLongerFiresComplexNestedConfigIgnored()
{
    const string source = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Order>(entity =>
                {
                    entity.ComplexProperty(e => e.ShippingAddress, b =>
                    {
                        b.Property(a => a.Street).HasColumnName("street_name");
                    });
                });
            }
        }
        """;

    var entities = new[]
    {
        new EntityModel("Order", new[] { new PropertyModel("ShippingAddress", "Address", IsNullable: false, MaxLength: null) }),
        new EntityModel("Address", new[] { new PropertyModel("Street", "string", IsNullable: false, MaxLength: null) }),
    };

    var result = Parser.ParseComplexPropertyCalls(source, entities);

    Assert.Empty(result.Diagnostics);
}
```
- [ ] **Step 2: Run tests to verify they fail**
Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter "FluentConfigParserOwnedTypeTests|FluentConfigParserComplexPropertyTests"`
Expected: FAIL — `HasMaxLength`/`HasColumnName` still trigger the ignored diagnostic (old "any call" behavior), and `ToTable`/`WithOwner` cases aren't specifically asserted yet either way (they'd currently pass since "any call" also matches them, but the "no longer fires" tests fail).
- [ ] **Step 3: Write minimal implementation**
```csharp
// src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs — replace HasNestedConfigCalls (lines 1126-1137)
private static readonly string[] IgnoredNestedBuilderCallNames = { "ToTable", "WithOwner" };

/// True if `call`'s second lambda argument (the builder) contains any `ToTable`/`WithOwner`
/// call — the two builder-lambda calls this pass still doesn't apply (table splitting and owner
/// customization are explicit non-goals). Everything else inside the builder is now genuinely
/// parsed via the nested scope FluentSyntaxHelpers.FindConfigurationScopes yields for it, so it no
/// longer needs a "something was ignored" diagnostic.
private static bool HasIgnoredNestedConfigCalls(InvocationExpressionSyntax call)
{
    var builderLambda = call.ArgumentList.Arguments
        .Select(a => a.Expression)
        .OfType<AnonymousFunctionExpressionSyntax>()
        .Skip(1)
        .FirstOrDefault();

    if (builderLambda is null)
    {
        return false;
    }

    return builderLambda.DescendantNodes()
        .OfType<InvocationExpressionSyntax>()
        .Any(nested => nested.Expression is MemberAccessExpressionSyntax { Name.Identifier.Text: var name }
            && IgnoredNestedBuilderCallNames.Contains(name));
}
```
Update both call sites (in `ParseOwnedTypeCalls`, line ~1110, and `ParseComplexPropertyCalls` from Task 1.2) from `HasNestedConfigCalls(call)` to `HasIgnoredNestedConfigCalls(call)`.
- [ ] **Step 4: Run tests to verify they pass**
Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter "FluentConfigParserOwnedTypeTests|FluentConfigParserComplexPropertyTests"` then `dotnet test tests/EfSchemaVisualizer.Web.Tests --filter DiagramModelBuilderOwnedTypeTests`
Expected: PASS.
- [ ] **Step 5: Commit**
```bash
git add src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserOwnedTypeTests.cs tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserComplexPropertyTests.cs tests/EfSchemaVisualizer.Web.Tests/DiagramModelBuilderOwnedTypeTests.cs
git commit -m "$(cat <<'EOF'
Narrow OwnedNestedConfigIgnored/ComplexNestedConfigIgnored to ToTable/WithOwner

Now that builder-lambda config is genuinely parsed via its own scope,
these diagnostics should only fire for the two calls this pass still
doesn't apply (table splitting, owner customization) rather than for any
call at all.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 2.3: End-to-end test — `HasMaxLength`/`HasColumnName` inside a builder lambda shows up on the folded property

**Files:**
- Test: `tests/EfSchemaVisualizer.Web.Tests/DiagramModelBuilderOwnedTypeTests.cs` (add), `tests/EfSchemaVisualizer.Web.Tests/DiagramModelBuilderComplexPropertyTests.cs` (add)

**Interfaces:**
- Consumes: the full `DiagramModelBuilder.Build` pipeline (this is a pure integration test, no new production code expected).

- [ ] **Step 1: Write the failing test**
```csharp
// tests/EfSchemaVisualizer.Web.Tests/DiagramModelBuilderOwnedTypeTests.cs — add
[Fact]
public void Build_OwnsOneBuilderLambdaHasMaxLengthAndHasColumnName_AppliesToFoldedProperty()
{
    const string classSource = """
        public class Order
        {
            public int Id { get; set; }
            public Address ShippingAddress { get; set; }
        }

        public class Address
        {
            public string Street { get; set; }
        }
        """;

    const string configSource = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Order>(entity =>
                {
                    entity.OwnsOne(e => e.ShippingAddress, b =>
                    {
                        b.Property(a => a.Street).HasMaxLength(100);
                        b.HasColumnName("shipping_street");
                    });
                });
            }
        }
        """;

    var result = DiagramModelBuilder.Build(classSource, configSource);

    var order = result.Entities.Single(e => e.Name == "Order");
    var street = order.Properties.Single(p => p.Name == "Street");
    Assert.Equal(100, street.MaxLength);
}
```
```csharp
// tests/EfSchemaVisualizer.Web.Tests/DiagramModelBuilderComplexPropertyTests.cs — add
[Fact]
public void Build_ComplexPropertyBuilderLambdaHasColumnName_AppliesToFoldedProperty()
{
    const string classSource = """
        public class Order
        {
            public int Id { get; set; }
            public Address ShippingAddress { get; set; }
        }

        public class Address
        {
            public string Street { get; set; }
        }
        """;

    const string configSource = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Order>(entity =>
                {
                    entity.ComplexProperty(e => e.ShippingAddress, b =>
                    {
                        b.Property(a => a.Street).HasColumnName("shipping_street");
                    });
                });
            }
        }
        """;

    var result = DiagramModelBuilder.Build(classSource, configSource);

    var order = result.Entities.Single(e => e.Name == "Order");
    var street = order.Properties.Single(p => p.Name == "Street");
    Assert.Equal("shipping_street", street.ColumnName);
}
```
- [ ] **Step 2: Run test to verify it fails or passes**
Run: `dotnet test tests/EfSchemaVisualizer.Web.Tests --filter "DiagramModelBuilderOwnedTypeTests|DiagramModelBuilderComplexPropertyTests"`
Expected: These should already PASS if Task 2.1/2.2 were implemented correctly (this is the "no new extractor code" claim's proof). If either FAILs, it means the nested-scope wiring from Task 2.1 has a bug — treat this as the regression signal and fix Task 2.1's implementation, not as new production code to write.
- [ ] **Step 3: N/A** (no new production code expected — see Step 2)
- [ ] **Step 4: Confirm passing**
Run the same filter again after any fix-up.
- [ ] **Step 5: Commit**
```bash
git add tests/EfSchemaVisualizer.Web.Tests/DiagramModelBuilderOwnedTypeTests.cs tests/EfSchemaVisualizer.Web.Tests/DiagramModelBuilderComplexPropertyTests.cs
git commit -m "$(cat <<'EOF'
Add end-to-end coverage: builder-lambda config applies to folded properties

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 2.4: Full Phase 2 regression run

**Files:** none (verification only).

- [ ] **Step 1: Run the full suite**
Run: `dotnet test EfSchemaVisualizer.slnx`
Expected: 100% pass.
- [ ] **Step 2: Commit** (only if fix-ups were needed)

---

### Task 3.1: Stamp `DeclaringEntityName` during owned/complex fold

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/Inference/OwnedTypeInference.cs:73-78`
- Modify: `src/EfSchemaVisualizer.Core/Inference/ComplexTypeInference.cs` (splice `with` expression from Task 1.3)
- Test: extend `tests/EfSchemaVisualizer.Core.Tests/Inference/OwnedTypeInferenceTests.cs` and `ComplexTypeInferenceTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: `PropertyModel.DeclaringEntityName` set to the folded-in type's own name on first splice, preserved (not overwritten) on subsequent re-splice in a multi-level chain.

- [ ] **Step 1: Write the failing tests**
```csharp
// tests/EfSchemaVisualizer.Core.Tests/Inference/OwnedTypeInferenceTests.cs — add
[Fact]
public void Fold_OwnsOne_StampsDeclaringEntityNameToTargetType()
{
    var order = new EntityModel("Order", new[] { Property("Id", "int"), Property("ShippingAddress", "Address") });
    var address = new EntityModel("Address", new[] { Property("Street", "string") });

    var result = OwnedTypeInference.Fold(
        new[] { order, address },
        new[] { new OwnedTypeConfig("Order", "ShippingAddress", IsMany: false) });

    var street = result.Entities.Single(e => e.Name == "Order").Properties.Single(p => p.Name == "Street");
    Assert.Equal("Address", street.DeclaringEntityName);
}

[Fact]
public void Fold_MultiLevelOwnedChain_PreservesInnerDeclaringEntityNameOnOuterReSplice()
{
    var order = new EntityModel("Order", new[] { Property("ShippingAddress", "Address") });
    var address = new EntityModel("Address", new[] { Property("Street", "string"), Property("Country", "Country") });
    var country = new EntityModel("Country", new[] { Property("Name", "string") });

    var result = OwnedTypeInference.Fold(
        new[] { order, address, country },
        new[]
        {
            new OwnedTypeConfig("Order", "ShippingAddress", IsMany: false),
            new OwnedTypeConfig("Address", "Country", IsMany: false),
        });

    var foldedOrder = Assert.Single(result.Entities);
    Assert.Equal("Address", foldedOrder.Properties.Single(p => p.Name == "Street").DeclaringEntityName);
    Assert.Equal("Country", foldedOrder.Properties.Single(p => p.Name == "Name").DeclaringEntityName);
}
```
```csharp
// tests/EfSchemaVisualizer.Core.Tests/Inference/ComplexTypeInferenceTests.cs — add
[Fact]
public void Fold_ComplexProperty_StampsDeclaringEntityNameToTargetType()
{
    var order = new EntityModel("Order", new[] { Property("ShippingAddress", "Address") });
    var address = new EntityModel("Address", new[] { Property("Street", "string") });

    var result = ComplexTypeInference.Fold(
        new[] { order, address },
        new[] { new ComplexTypeConfig("Order", "ShippingAddress") });

    var street = result.Entities.Single().Properties.Single(p => p.Name == "Street");
    Assert.Equal("Address", street.DeclaringEntityName);
}
```
- [ ] **Step 2: Run tests to verify they fail**
Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter "OwnedTypeInferenceTests|ComplexTypeInferenceTests"`
Expected: FAIL — `DeclaringEntityName` is `null` on folded properties today.
- [ ] **Step 3: Write minimal implementation**
```csharp
// src/EfSchemaVisualizer.Core/Inference/OwnedTypeInference.cs:73-78
.Concat(targetProperties.Select(p => p with
{
    FoldKind = FoldKind.Owned,
    OwnerNavigationProperty = call.NavigationPropertyName,
    DeclaringEntityName = p.DeclaringEntityName ?? targetName,
}))
```
```csharp
// src/EfSchemaVisualizer.Core/Inference/ComplexTypeInference.cs — same pattern
.Concat(targetProperties.Select(p => p with
{
    FoldKind = FoldKind.Complex,
    OwnerNavigationProperty = call.NavigationPropertyName,
    DeclaringEntityName = p.DeclaringEntityName ?? targetName,
}))
```
- [ ] **Step 4: Run tests to verify they pass**
Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter "OwnedTypeInferenceTests|ComplexTypeInferenceTests"`
Expected: PASS.
- [ ] **Step 5: Commit**
```bash
git add src/EfSchemaVisualizer.Core/Inference/OwnedTypeInference.cs src/EfSchemaVisualizer.Core/Inference/ComplexTypeInference.cs tests/EfSchemaVisualizer.Core.Tests/Inference/OwnedTypeInferenceTests.cs tests/EfSchemaVisualizer.Core.Tests/Inference/ComplexTypeInferenceTests.cs
git commit -m "$(cat <<'EOF'
Stamp DeclaringEntityName during owned/complex fold

Reuses the DeclaringEntityName-based routing DiagramEditor already built
for inherited properties (W2) — no DiagramEditor changes needed, since
ResolveDeclaringEntity already reads whatever DeclaringEntityName says.
Only stamped when unset, so a multi-level chain (Order->Address->Country)
keeps Country's own properties routed to Country's file, not re-stamped
to Address on the outer re-splice.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 3.2: Integration test — structural edits on a folded owned/complex property round-trip to the right file

**Files:**
- Test: `tests/EfSchemaVisualizer.Web.Tests/DiagramEditorOwnedTypeTests.cs` (new — check for an existing `DiagramEditorTests.cs` to match conventions/fixture helpers first)

**Interfaces:**
- Consumes: `DiagramEditor.RenameProperty`, `ChangePropertyType`, `RemoveProperty`.

- [ ] **Step 1: Write the failing test**
```csharp
// tests/EfSchemaVisualizer.Web.Tests/DiagramEditorOwnedTypeTests.cs
using System.Linq;
using EfSchemaVisualizer.Web.Diagram;
using Xunit;

namespace EfSchemaVisualizer.Web.Tests;

public class DiagramEditorOwnedTypeTests
{
    private const string ClassSource = """
        public class Order
        {
            public int Id { get; set; }
            public Address ShippingAddress { get; set; }
        }

        public class Address
        {
            public string Street { get; set; }
        }
        """;

    private const string ConfigSource = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Order>(entity =>
                {
                    entity.OwnsOne(e => e.ShippingAddress);
                });
            }
        }
        """;

    [Fact]
    public void RenameProperty_FoldedOwnedProperty_RenamesOnAddressClassNotOrder()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);

        var result = editor.RenameProperty("Order", "Street", "StreetLine1");

        Assert.True(result.Success);
        Assert.Contains("public string StreetLine1 { get; set; }", editor.ClassSource);
        Assert.DoesNotContain("public string Street {", editor.ClassSource);

        var order = editor.Current.Entities.Single(e => e.Name == "Order");
        Assert.Contains(order.Properties, p => p.Name == "StreetLine1");
    }

    [Fact]
    public void RemoveProperty_FoldedOwnedProperty_RemovesFromAddressClass()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);

        var result = editor.RemoveProperty("Order", "Street");

        Assert.True(result.Success);
        Assert.DoesNotContain("Street", editor.ClassSource.Split('\n').FirstOrDefault(l => l.Contains("class Address")) is null
            ? editor.ClassSource
            : editor.ClassSource);
        var order = editor.Current.Entities.Single(e => e.Name == "Order");
        Assert.DoesNotContain(order.Properties, p => p.Name == "Street");
    }
}
```
(Before finalizing this test file, check `tests/EfSchemaVisualizer.Web.Tests/` for an existing `DiagramEditorTests.cs` or similar and match its exact fixture-construction style, e.g. whether `DiagramEditor` is constructed with named args or a helper method — adjust constructor call accordingly.)
- [ ] **Step 2: Run test to verify it fails**
Run: `dotnet test tests/EfSchemaVisualizer.Web.Tests --filter DiagramEditorOwnedTypeTests`
Expected: Given Task 3.1 already stamps `DeclaringEntityName`, and `DiagramEditor.ResolveDeclaringEntity`/`EntityClassRewriter` already route generically by that field, this SHOULD already pass. If it fails, it's a genuine gap in `ResolveDeclaringEntity` or `EntityClassRewriter` to diagnose and fix (not a "new feature" — treat as a bug per `superpowers:systematic-debugging`).
- [ ] **Step 3: Fix if needed** (expected to be a no-op per the research above — do not write speculative code; only patch what the failing assertion reveals)
- [ ] **Step 4: Run test to verify it passes**
Run: `dotnet test tests/EfSchemaVisualizer.Web.Tests --filter DiagramEditorOwnedTypeTests`
- [ ] **Step 5: Commit**
```bash
git add tests/EfSchemaVisualizer.Web.Tests/DiagramEditorOwnedTypeTests.cs
git commit -m "$(cat <<'EOF'
Add coverage: structural edits on folded owned properties route to the
owning type's class file via DeclaringEntityName

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 3.3: `OnModelCreatingRewriter.FindOrCreateOwnedConfigScope`

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs` (new private method near `FindConfigScopes`, ~line 1891)
- Test: `tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterOwnedConfigScopeTests.cs` (new — check `OnModelCreatingRewriterTests.cs` first for existing helper/fixture conventions)

**Interfaces:**
- Consumes: `CompilationUnitSyntax root`, `ownerEntityName`, `navPropertyName`.
- Produces: `private static SyntaxNode FindOrCreateOwnedConfigScope(CompilationUnitSyntax root, string ownerEntityName, string navPropertyName, out CompilationUnitSyntax updatedRoot)` — mirrors `FindConfigScopes`/`InsertEntityBlock`'s "find, else synthesize" shape, but for the `OwnsOne(e => e.Foo)` → `OwnsOne(e => e.Foo, b => { })` bare-to-block-lambda transformation instead of a bogus `Entity<T>()` block.

- [ ] **Step 1: Write the failing test**
```csharp
// tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterOwnedConfigScopeTests.cs
using System.Linq;
using EfSchemaVisualizer.Core.CodeGen;
using EfSchemaVisualizer.Core.Parsing;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace EfSchemaVisualizer.Core.Tests.CodeGen;

public class OnModelCreatingRewriterOwnedConfigScopeTests
{
    private readonly OnModelCreatingRewriter _rewriter = new();

    [Fact]
    public void SetColumnName_OnFoldedOwnedProperty_SynthesizesBuilderLambdaOnBareOwnsOneCall()
    {
        const string source = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Order>(entity =>
                    {
                        entity.OwnsOne(e => e.ShippingAddress);
                    });
                }
            }
            """;

        var newSource = _rewriter.SetColumnNameOnOwnedProperty(source, "Order", "ShippingAddress", "Street", "shipping_street");

        var root = CSharpSyntaxTree.ParseText(newSource).GetCompilationUnitRoot();
        var addressScope = FluentSyntaxHelpers.FindConfigurationScopes(root).ToList();
        // Re-parse via the standard entity-name-keyed lookup won't find "Address" without the
        // entities list, so assert on raw text instead — the important thing is the shape:
        Assert.Contains("OwnsOne(e => e.ShippingAddress, b =>", newSource);
        Assert.Contains("HasColumnName(\"shipping_street\")", newSource);
    }

    [Fact]
    public void SetColumnName_OnFoldedOwnedProperty_ExistingBuilderLambda_AppendsToIt()
    {
        const string source = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Order>(entity =>
                    {
                        entity.OwnsOne(e => e.ShippingAddress, b =>
                        {
                            b.Property(a => a.Street).HasMaxLength(100);
                        });
                    });
                }
            }
            """;

        var newSource = _rewriter.SetColumnNameOnOwnedProperty(source, "Order", "ShippingAddress", "Street", "shipping_street");

        Assert.Contains("HasMaxLength(100)", newSource);
        Assert.Contains("HasColumnName(\"shipping_street\")", newSource);
    }
}
```
- [ ] **Step 2: Run test to verify it fails**
Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter OnModelCreatingRewriterOwnedConfigScopeTests`
Expected: FAIL — `SetColumnNameOnOwnedProperty` doesn't exist yet.
- [ ] **Step 3: Write minimal implementation**
```csharp
// src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs — new method, near FindConfigScopes (~line 1891)

/// Locates the builder-lambda block of an existing `OwnsOne(nav, builder)`/`OwnsMany(nav, builder)`/
/// `ComplexProperty(nav, builder)` call for `navPropertyName` within `ownerEntityName`'s own
/// Entity<T>()/Configure scope(s), or synthesizes one by adding a second lambda argument to a
/// currently-bare `OwnsOne(nav)`-shaped call if the call exists but has no builder lambda yet.
/// Mirrors FindConfigScopes/InsertEntityBlock's "find, else synthesize" shape for Entity<T>(), but
/// targets the call's own builder lambda instead of a top-level Entity<T>() block — a plain
/// InsertEntityBlock-style `modelBuilder.Entity<Address>(...)` would be wrong here, since Address
/// isn't a real top-level Entity<T>() target once it's owned/complex-folded.
/// Returns null (with `newRoot` unchanged) if no OwnsOne/OwnsMany/ComplexProperty call targeting
/// `navPropertyName` exists anywhere in `ownerEntityName`'s scope(s) — callers should surface this
/// as an edit failure rather than silently no-op'ing.
private static (SyntaxNode Scope, CompilationUnitSyntax NewRoot)? FindOrCreateOwnedConfigScope(
    CompilationUnitSyntax root, string ownerEntityName, string navPropertyName)
{
    var ownerScopes = FindConfigScopes(root, ownerEntityName);

    foreach (var callName in new[] { "OwnsOne", "OwnsMany", "ComplexProperty" })
    {
        var call = ownerScopes
            .SelectMany(scope => FluentSyntaxHelpers.FindCallsNamed(scope, callName))
            .FirstOrDefault(c => FluentSyntaxHelpers.TryReadSinglePropertyNameArgument(c) == navPropertyName);

        if (call is null)
        {
            continue;
        }

        var builderLambda = call.ArgumentList.Arguments
            .Select(a => a.Expression)
            .OfType<AnonymousFunctionExpressionSyntax>()
            .Skip(1)
            .FirstOrDefault();

        if (builderLambda?.Block is { } existingBlock)
        {
            return (existingBlock, root);
        }

        // Bare OwnsOne(e => e.Foo) — synthesize the second (builder) lambda argument:
        // OwnsOne(e => e.Foo, b => { }).
        var navLambdaParam = call.ArgumentList.Arguments[0].Expression is SimpleLambdaExpressionSyntax navLambda
            ? navLambda.Parameter.Identifier.Text
            : "e";
        var builderParamName = navLambdaParam == "b" ? "builder" : "b";

        var newBlock = SyntaxFactory.Block();
        var newBuilderArgument = SyntaxFactory.Argument(
            SyntaxFactory.SimpleLambdaExpression(
                SyntaxFactory.Parameter(SyntaxFactory.Identifier(builderParamName)),
                newBlock));

        var newCall = call.WithArgumentList(
            call.ArgumentList.WithArguments(call.ArgumentList.Arguments.Add(newBuilderArgument)));

        var newRoot = (CompilationUnitSyntax)root.ReplaceNode(call, newCall);

        // Re-locate the block in the new tree (the old `newBlock` node instance isn't part of
        // `newRoot` — ReplaceNode produces fresh nodes) by re-running the same lookup against it.
        var relocatedCall = FindConfigScopes(newRoot, ownerEntityName)
            .SelectMany(scope => FluentSyntaxHelpers.FindCallsNamed(scope, callName))
            .First(c => FluentSyntaxHelpers.TryReadSinglePropertyNameArgument(c) == navPropertyName);
        var relocatedBlock = ((SimpleLambdaExpressionSyntax)relocatedCall.ArgumentList.Arguments[1].Expression).Block!;

        return (relocatedBlock, newRoot);
    }

    return null;
}
```
```csharp
// Representative public entry point exercising the new scope resolver — this is the minimal
// slice needed to prove FindOrCreateOwnedConfigScope works; Task 3.4 wires DiagramEditor's real
// attribute-edit call sites (SetColumnName, RewriteMaxLength, etc.) through the same
// find-scope-or-owned-scope branch instead of duplicating this per method.
public string SetColumnNameOnOwnedProperty(
    string sourceCode, string ownerEntityName, string navPropertyName, string propertyName, string columnName)
{
    var tree = CSharpSyntaxTree.ParseText(sourceCode);
    var root = tree.GetCompilationUnitRoot();

    var resolved = FindOrCreateOwnedConfigScope(root, ownerEntityName, navPropertyName)
        ?? throw new InvalidOperationException(
            $"No OwnsOne/OwnsMany/ComplexProperty call for '{navPropertyName}' found on '{ownerEntityName}'.");

    var (scope, newRoot) = resolved;

    var existingColumnNameCall = FluentSyntaxHelpers.FindCallsNamed(scope, "HasColumnName")
        .FirstOrDefault(call => FluentSyntaxHelpers.GetPropertyNameFor(call) == propertyName);

    if (existingColumnNameCall is not null)
    {
        var newArgument = SyntaxFactory.Argument(
            SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(columnName)));
        var newCall = existingColumnNameCall.WithArgumentList(
            existingColumnNameCall.ArgumentList.WithArguments(SyntaxFactory.SingletonSeparatedList(newArgument)));
        return newRoot.ReplaceNode(existingColumnNameCall, newCall).NormalizeWhitespace().ToFullString();
    }

    var existingPropertyCall = FluentSyntaxHelpers.FindCallsNamed(scope, "Property")
        .FirstOrDefault(call => FluentSyntaxHelpers.GetPropertyNameForPropertyCall(call) == propertyName);

    var propertyLambdaParam = FluentSyntaxHelpers.GetPropertyLambdaParameterName(scope);
    var (block, blockReceiverName) = GetScopeBlockAndReceiver(scope);

    ExpressionSyntax propertyCallExpression = existingPropertyCall
        ?? SyntaxFactory.InvocationExpression(
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

    var columnNameCall = SyntaxFactory.InvocationExpression(
        SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression, propertyCallExpression, SyntaxFactory.IdentifierName("HasColumnName")),
        SyntaxFactory.ArgumentList(
            SyntaxFactory.SingletonSeparatedList(
                SyntaxFactory.Argument(
                    SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(columnName))))));

    var newStatement = SyntaxFactory.ExpressionStatement(columnNameCall);

    if (existingPropertyCall is not null)
    {
        return newRoot.ReplaceNode(existingPropertyCall, columnNameCall).NormalizeWhitespace().ToFullString();
    }

    var newBlock = block.AddStatements(newStatement);
    return newRoot.ReplaceNode(block, newBlock).NormalizeWhitespace().ToFullString();
}
```
- [ ] **Step 4: Run test to verify it passes**
Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter OnModelCreatingRewriterOwnedConfigScopeTests`
Expected: PASS.
- [ ] **Step 5: Commit**
```bash
git add src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterOwnedConfigScopeTests.cs
git commit -m "$(cat <<'EOF'
Add OnModelCreatingRewriter.FindOrCreateOwnedConfigScope

Locates an existing OwnsOne/OwnsMany/ComplexProperty builder lambda for
a folded property's owner+nav, or synthesizes one on a currently-bare
call — mirrors FindConfigScopes/InsertEntityBlock's find-or-create shape
without the wrong fallback (a bogus Entity<T>() block) that the existing
per-attribute mutators' InsertEntityBlock-style fallback would otherwise
produce for a type that isn't really top-level.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 3.4: `DiagramEditor` — route fluent-attribute edits on folded properties through the owned config scope

**Files:**
- Modify: `src/EfSchemaVisualizer.Web/Diagram/DiagramEditor.cs` (every call site that currently does `var owningEntityName = ResolveDeclaringEntity(entityName, propertyName); var newConfigSource = _configRewriter.SetXxx(ConfigSource, owningEntityName, ...);` for a fluent-attribute — lines 767, 795, 822, 860, 893, 932, 966, 1032, 1060, 1088, 1477 per the earlier grep)
- Modify: `src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs` — generalize the `SetColumnNameOnOwnedProperty` prototype from Task 3.3 into a reusable pattern for the other attribute mutators (`RewriteMaxLength`, `RewritePrecision`, `SetColumnType`, `SetDefaultValue`, `SetDefaultValueSql`, `SetComputedColumnSql`, `SetUseSequence`, `SetRowVersion`/`SetConcurrencyToken`, `RemoveColumnName`/`RemoveMaxLength`/etc. — same generalization)
- Test: extend `tests/EfSchemaVisualizer.Web.Tests/DiagramEditorOwnedTypeTests.cs`

**Interfaces:**
- Consumes: `PropertyModel.FoldKind`, `PropertyModel.OwnerNavigationProperty`, `OnModelCreatingRewriter.FindOrCreateOwnedConfigScope`.
- Produces: `DiagramEditor.CommitMaxLength`/`CommitColumnName`/etc. correctly editing a folded property's config inside its owner's `OwnsOne`/`ComplexProperty` builder lambda instead of a nonexistent `Entity<Address>()` block.

- [ ] **Step 1: Write the failing test**
```csharp
// tests/EfSchemaVisualizer.Web.Tests/DiagramEditorOwnedTypeTests.cs — add
[Fact]
public void CommitColumnName_FoldedOwnedProperty_WritesIntoOwnsOneBuilderLambdaNotABogusEntityBlock()
{
    var editor = new DiagramEditor(ClassSource, ConfigSource);

    var street = editor.Current.Entities.Single(e => e.Name == "Order").Properties.Single(p => p.Name == "Street");
    var result = editor.SetColumnName("Order", street.Name, "shipping_street");

    Assert.True(result.Success);
    Assert.DoesNotContain("Entity<Address>", editor.ConfigSource);
    Assert.Contains("OwnsOne(e => e.ShippingAddress, b =>", editor.ConfigSource);
    Assert.Contains("HasColumnName(\"shipping_street\")", editor.ConfigSource);

    var street2 = editor.Current.Entities.Single(e => e.Name == "Order").Properties.Single(p => p.Name == "Street");
    Assert.Equal("shipping_street", street2.ColumnName);
}
```
Confirmed via grep of `DiagramEditor.cs`: the public method is `SetColumnName(string entityName, string propertyName, string? columnName)` (line 747), one of ~15 sibling `SetXxx(string entityName, string propertyName, ...)` methods (`SetColumnType`, `SetMaxLength`, `SetRequiredOverride`, `SetRowVersion`, `SetConcurrencyToken`, `SetPrecision`, `SetDefaultValue`, `SetDefaultValueSql`, `SetComputedColumnSql`, `SetUseSequence`) that Task 3.4's per-call-site change below applies to.
- [ ] **Step 2: Run test to verify it fails**
Run: `dotnet test tests/EfSchemaVisualizer.Web.Tests --filter DiagramEditorOwnedTypeTests`
Expected: FAIL — today this either throws (`FindOnModelCreatingMethod`/`InsertEntityBlock` producing a bogus `Entity<Address>(...)` block) or silently writes nonsense config that never round-trips back onto `Street` (since `Address` isn't in `Current.Entities` as a real entity post-fold).
- [ ] **Step 3: Write minimal implementation**
For each of the ~11 call sites in `DiagramEditor.cs` following this shape:
```csharp
var owningEntityName = ResolveDeclaringEntity(entityName, propertyName);
var newConfigSource = _configRewriter.SetColumnName(ConfigSource, owningEntityName, propertyName, columnName);
```
change to:
```csharp
var property = Current.Entities.First(e => e.Name == entityName).Properties.First(p => p.Name == propertyName);
var newConfigSource = property.FoldKind != FoldKind.None && property.OwnerNavigationProperty is { } navName
    ? _configRewriter.SetColumnNameOnOwnedProperty(ConfigSource, entityName, navName, propertyName, columnName)
    : _configRewriter.SetColumnName(ConfigSource, ResolveDeclaringEntity(entityName, propertyName), propertyName, columnName);
```
Generalize `OnModelCreatingRewriter.SetColumnNameOnOwnedProperty` from Task 3.3 into the repeated pattern for the other attribute setters (`SetMaxLengthOnOwnedProperty`, `SetPrecisionOnOwnedProperty`, `SetColumnTypeOnOwnedProperty`, `SetDefaultValueOnOwnedProperty`, `SetDefaultValueSqlOnOwnedProperty`, `SetComputedColumnSqlOnOwnedProperty`, `SetUseSequenceOnOwnedProperty`, `SetRowVersionOnOwnedProperty`, `SetConcurrencyTokenOnOwnedProperty`) — each following the exact same `FindOrCreateOwnedConfigScope` → existing-call-mutate-or-append-new-statement shape as `SetColumnNameOnOwnedProperty`, differing only in which fluent call name (`HasMaxLength`, `HasPrecision`, `HasColumnType`, `HasDefaultValue`, `HasDefaultValueSql`, `HasComputedColumnSql`, `UseSequence`, `IsRowVersion`, `IsConcurrencyToken`) and argument shape they build — copy `SetColumnNameOnOwnedProperty`'s structure per attribute rather than trying to build one fully generic method, matching this codebase's existing per-attribute method style (no shared generic mutator abstraction exists today either — `RewriteMaxLength`/`SetColumnName`/etc. are all hand-written siblings).
- [ ] **Step 4: Run test to verify it passes**
Run: `dotnet test tests/EfSchemaVisualizer.Web.Tests --filter DiagramEditorOwnedTypeTests`
Expected: PASS.
- [ ] **Step 5: Commit**
```bash
git add src/EfSchemaVisualizer.Web/Diagram/DiagramEditor.cs src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs tests/EfSchemaVisualizer.Web.Tests/DiagramEditorOwnedTypeTests.cs
git commit -m "$(cat <<'EOF'
Route fluent-attribute edits on folded properties through the owned config scope

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 3.5: Nav-property rename patches the outer `OwnsOne`/`OwnsMany`/`ComplexProperty` call's lambda parameter

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs` (new method near `RenamePropertyReferences`, ~line 2231)
- Modify: `src/EfSchemaVisualizer.Web/Diagram/DiagramEditor.cs:160-164` (`RenameProperty`)
- Test: `tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs` (extend, or new file if the existing one doesn't cover owned types), `tests/EfSchemaVisualizer.Web.Tests/DiagramEditorOwnedTypeTests.cs` (extend)

**Interfaces:**
- Consumes: `FluentSyntaxHelpers.TryReadSinglePropertyNameArgument`, `FindConfigScopes`.
- Produces: `OnModelCreatingRewriter.RenameOwnedNavigationReference(string sourceCode, string ownerEntityName, string oldNavName, string newNavName) : string` — rewrites the first (nav-selector) lambda argument of any `OwnsOne`/`OwnsMany`/`ComplexProperty` call on `ownerEntityName` targeting `oldNavName`; no-op (returns source unchanged) if no such call exists.

- [ ] **Step 1: Write the failing test**
```csharp
// tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs — add (or new file)
[Fact]
public void RenameOwnedNavigationReference_OwnsOneCall_RenamesLambdaParameterReference()
{
    const string source = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Order>(entity =>
                {
                    entity.OwnsOne(e => e.ShippingAddress, b =>
                    {
                        b.Property(a => a.Street).HasMaxLength(100);
                    });
                });
            }
        }
        """;

    var rewriter = new OnModelCreatingRewriter();
    var newSource = rewriter.RenameOwnedNavigationReference(source, "Order", "ShippingAddress", "DeliveryAddress");

    Assert.Contains("OwnsOne(e => e.DeliveryAddress, b =>", newSource);
    Assert.DoesNotContain("ShippingAddress", newSource);
}

[Fact]
public void RenameOwnedNavigationReference_NoMatchingCall_ReturnsSourceUnchanged()
{
    const string source = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Order>(entity => { });
            }
        }
        """;

    var rewriter = new OnModelCreatingRewriter();
    var newSource = rewriter.RenameOwnedNavigationReference(source, "Order", "ShippingAddress", "DeliveryAddress");

    Assert.Equal(source, newSource);
}
```
```csharp
// tests/EfSchemaVisualizer.Web.Tests/DiagramEditorOwnedTypeTests.cs — add
[Fact]
public void RenameProperty_OwnerNavigationProperty_PatchesOuterOwnsOneCallAndPropertyDeclaration()
{
    var editor = new DiagramEditor(ClassSource, ConfigSource);

    var result = editor.RenameProperty("Order", "ShippingAddress", "DeliveryAddress");

    Assert.True(result.Success);
    Assert.Contains("public Address DeliveryAddress { get; set; }", editor.ClassSource);
    Assert.Contains("OwnsOne(e => e.DeliveryAddress", editor.ConfigSource);
    Assert.DoesNotContain("ShippingAddress", editor.ClassSource);
    Assert.DoesNotContain("ShippingAddress", editor.ConfigSource);

    var order = editor.Current.Entities.Single(e => e.Name == "Order");
    Assert.Contains(order.Properties, p => p.Name == "Street" && p.OwnerNavigationProperty == "DeliveryAddress");
}
```
- [ ] **Step 2: Run tests to verify they fail**
Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter OnModelCreatingRewriterTests` and `dotnet test tests/EfSchemaVisualizer.Web.Tests --filter DiagramEditorOwnedTypeTests`
Expected: FAIL — `RenameOwnedNavigationReference` doesn't exist; the `DiagramEditor` test fails because `ConfigSource` still says `ShippingAddress` after rename (only `RenamePropertyReferences`'s narrow `Property(e => e.X)` rewriting runs today, which doesn't match `OwnsOne`'s first argument).
- [ ] **Step 3: Write minimal implementation**
```csharp
// src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs — new method near RenamePropertyReferences (~line 2231)

/// Renaming an owner's navigation property (e.g. `Order.ShippingAddress` -> `Order.DeliveryAddress`)
/// must also patch the outer `OwnsOne(e => e.ShippingAddress, ...)` call's lambda parameter, not
/// just the property declaration on Order's class — RenamePropertyReferences only rewrites
/// `Property(e => e.X)`-shaped calls and doesn't know to look here. No-ops (returns `sourceCode`
/// unchanged) if no OwnsOne/OwnsMany/ComplexProperty call targets `oldNavName` on `ownerEntityName`.
public string RenameOwnedNavigationReference(
    string sourceCode, string ownerEntityName, string oldNavName, string newNavName)
{
    var tree = CSharpSyntaxTree.ParseText(sourceCode);
    var root = tree.GetCompilationUnitRoot();

    var scopes = FindConfigScopes(root, ownerEntityName);

    foreach (var callName in new[] { "OwnsOne", "OwnsMany", "ComplexProperty" })
    {
        var call = scopes
            .SelectMany(scope => FluentSyntaxHelpers.FindCallsNamed(scope, callName))
            .FirstOrDefault(c => FluentSyntaxHelpers.TryReadSinglePropertyNameArgument(c) == oldNavName);

        if (call is null)
        {
            continue;
        }

        var navArgument = call.ArgumentList.Arguments[0];

        if (navArgument.Expression is not SimpleLambdaExpressionSyntax { ExpressionBody: MemberAccessExpressionSyntax access } lambda)
        {
            continue;
        }

        var newLambda = lambda.WithExpressionBody(access.WithName(SyntaxFactory.IdentifierName(newNavName)));
        var newCall = call.WithArgumentList(
            call.ArgumentList.WithArguments(
                call.ArgumentList.Arguments.Replace(navArgument, navArgument.WithExpression(newLambda))));

        var newRoot = root.ReplaceNode(call, newCall);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    return sourceCode;
}
```
```csharp
// src/EfSchemaVisualizer.Web/Diagram/DiagramEditor.cs:160-164 — RenameProperty, add the patch call
var owningEntityName = ResolveDeclaringEntity(entityName, oldPropertyName);
var newClassSource = _classRewriter.RenameProperty(ClassSource, owningEntityName, oldPropertyName, newPropertyName);
var newConfigSource = _configRewriter.RenamePropertyReferences(ConfigSource, owningEntityName, oldPropertyName, newPropertyName);

// If oldPropertyName is itself an owner-side nav property with an OwnsOne/OwnsMany/ComplexProperty
// call (owningEntityName == entityName in that case, since a nav property's own DeclaringEntityName
// is never stamped by the fold — only the properties folded IN from the target get stamped), also
// patch the outer call's lambda parameter.
newConfigSource = _configRewriter.RenameOwnedNavigationReference(newConfigSource, entityName, oldPropertyName, newPropertyName);

Apply(newClassSource, newConfigSource);
return DiagramEditResult.Ok();
```
- [ ] **Step 4: Run tests to verify they pass**
Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter OnModelCreatingRewriterTests` and `dotnet test tests/EfSchemaVisualizer.Web.Tests --filter DiagramEditorOwnedTypeTests`
Expected: PASS.
- [ ] **Step 5: Commit**
```bash
git add src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs src/EfSchemaVisualizer.Web/Diagram/DiagramEditor.cs tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs tests/EfSchemaVisualizer.Web.Tests/DiagramEditorOwnedTypeTests.cs
git commit -m "$(cat <<'EOF'
Patch outer OwnsOne/OwnsMany/ComplexProperty lambda parameter on nav rename

Renaming an owner's navigation property now also rewrites the fluent
call's nav-selector lambda, not just the property declaration —
RenamePropertyReferences only ever handled Property(e => e.X)-shaped
calls and had no notion of this second reference.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 3.6: `EntityNode.razor` — make folded properties editable

**Files:**
- Modify: `src/EfSchemaVisualizer.Web/Diagram/EntityNode.razor:152-198` (the `@if (property.FoldKind == FoldKind.Owned) { readonly span } else if (Complex) { readonly span } else { full edit UI }` branch from Task 1.5)

**Interfaces:**
- Consumes: `DiagramEditor.RenameProperty`/`ChangePropertyType`/`RemoveProperty`/attribute setters (all now folded-property-aware per Tasks 3.1-3.5).
- Produces: folded properties get the same rename/retype/remove/expand UI as normal properties, with only a small marker distinguishing their origin (no longer a fully separate read-only branch).

- [ ] **Step 1: N/A** (presentation-only, no automated `.razor` component test harness — see Task 1.5's note; verify manually)
- [ ] **Step 2: N/A**
- [ ] **Step 3: Write minimal implementation**
```razor
@* src/EfSchemaVisualizer.Web/Diagram/EntityNode.razor — replace lines 152-198's three-way branch *@
<li style="padding: 2px 8px; @(isKey ? "font-weight: bold;" : "") @(property.FoldKind != FoldKind.None ? "opacity: 0.9;" : "")">
    @if (property.FoldKind == FoldKind.Owned)
    {
        <span style="opacity: 0.6; margin-right: 2px;" title="Folded in from the owned type via @property.OwnerNavigationProperty.">◆</span>
    }
    else if (property.FoldKind == FoldKind.Complex)
    {
        <span style="opacity: 0.6; margin-right: 2px; color: #4a8a6a;" title="Folded in from the complex type via @property.OwnerNavigationProperty.">●</span>
    }
    @if (property.IsShadow)
    {
        <span class="shadow-property" title="Shadow property: configured in code but has no matching CLR member. Read-only here."
              style="font-style: italic; color: #888;">@property.Name : @property.ClrType@(property.IsNullable ? "?" : "") (shadow)</span>
    }
    else
    {
        @* ... existing full edit UI body (rename/retype/nullable/expand/remove), unchanged from
               today's `else` branch at old lines 164-198 — now reached for FoldKind.Owned and
               FoldKind.Complex properties too, not just FoldKind.None ones. *@
    }
```
- [ ] **Step 4: Verify manually**
Run: `dotnet run --project src/EfSchemaVisualizer.Web`, load a sample with an `OwnsOne`/`ComplexProperty` fold, confirm double-click rename/retype and the "▸ more options" panel now work on folded rows, and edits persist into the correct source file/config scope per Tasks 3.1-3.5.
- [ ] **Step 5: Commit**
```bash
git add src/EfSchemaVisualizer.Web/Diagram/EntityNode.razor
git commit -m "$(cat <<'EOF'
Make owned/complex folded properties editable in the diagram UI

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 3.7: Full Phase 3 regression run

**Files:** none (verification only).

- [ ] **Step 1: Run the full suite**
Run: `dotnet test EfSchemaVisualizer.slnx`
Expected: 100% pass — every pre-existing test plus every test added across Phases 1-3.
- [ ] **Step 2: Manual smoke test**
Run: `dotnet run --project src/EfSchemaVisualizer.Web`, exercise: a `ComplexProperty` fold renders with the "●" marker; a builder-lambda `HasMaxLength` shows up on the folded property; renaming a folded property patches the `Address` class file; renaming the owner's nav property patches the outer `OwnsOne(...)` call; a fluent-attribute edit on a previously-bare `OwnsOne(e => e.Foo)` synthesizes a builder lambda.
- [ ] **Step 3: Commit** (only if Step 1/2 required fix-ups)
