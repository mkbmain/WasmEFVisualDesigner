# Owned Types Rendering (Backlog W3) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stop `OwnsOne`/`OwnsMany`-owned classes from rendering as fake standalone tables — fold `OwnsOne` properties inline into the owner's card, and mark `OwnsMany` targets as "owned" (still their own card, since they really are their own table).

**Architecture:** A new `FluentConfigParser.ParseOwnedTypeCalls` extractor detects `OwnsOne`/`OwnsMany` calls per entity scope (owner name, nav property name, single-vs-many). A new `Core.Inference.OwnedTypeInference.Fold` module — structured exactly like the existing `InheritanceInference.Fold` — consumes that plus the parsed entity list, resolves each nav property to its target entity via `PropertyModel.ClrType`, and either splices the target's properties into the owner (`OwnsOne`, target entity removed from the top-level list) or marks the target `IsOwned = true` and emits an `Owned`-kind relationship (`OwnsMany`, target entity kept). It's wired into `DiagramModelBuilder.Build` before `ConventionInference.InferKey` and `InheritanceInference.Fold`. Rendering changes are additive: a new relationship-kind branch in `DiagramSync`/`RelationshipLabels`/`RelationshipLinkLabel.razor`, and a grouped read-only sub-section in `EntityNode.razor` for folded owned properties.

**Tech Stack:** C# / .NET, Roslyn (`Microsoft.CodeAnalysis.CSharp`) for parsing, xUnit for tests, Blazor (`.razor`) for the diagram UI.

## Global Constraints

- No nested-config parsing inside an `OwnsOne`/`OwnsMany` builder lambda this pass — only detect whether it has any calls in it (for the diagnostic), never read what they configure.
- Folded `OwnsOne` properties and `OwnsMany`-owned entities are **read-only** — no new rewriter/editor support for renaming, retyping, or removing them, and no removing the `OwnsOne`/`OwnsMany` relationship itself.
- All new `EntityModel`/`PropertyModel` fields must default to values that leave every existing caller/test unaffected (`IsOwned = false`, `OwnerNavigationProperty = null`).
- Follow `InheritanceInference`'s structural conventions exactly where they apply (cycle-guarded ancestor/ownership walks, `OwnedTypeFoldResult` record shape mirroring `InheritanceFoldResult`).
- Existing test suite must stay green throughout — run the full solution test suite at the end of every task, not just the new file's tests.

---

### Task 1: Model changes — `IsOwned`, `OwnerNavigationProperty`, `RelationshipKind.Owned`

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/Model/PropertyModel.cs`
- Modify: `src/EfSchemaVisualizer.Core/Model/EntityModel.cs`
- Modify: `src/EfSchemaVisualizer.Core/Model/RelationshipKind.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Model/OwnedTypeModelFieldsTests.cs` (new)

**Interfaces:**
- Produces: `PropertyModel.IsOwned` (bool, default `false`), `PropertyModel.OwnerNavigationProperty` (string?, default `null`), `EntityModel.IsOwned` (bool, default `false`), `RelationshipKind.Owned` (new enum member).

- [ ] **Step 1: Write the failing test**

```csharp
using EfSchemaVisualizer.Core.Model;
using Xunit;

namespace EfSchemaVisualizer.Core.Tests.Model;

public class OwnedTypeModelFieldsTests
{
    [Fact]
    public void PropertyModel_DefaultsLeaveOwnedFieldsUnset()
    {
        var property = new PropertyModel("Street", "string", IsNullable: false, MaxLength: null);

        Assert.False(property.IsOwned);
        Assert.Null(property.OwnerNavigationProperty);
    }

    [Fact]
    public void EntityModel_DefaultLeavesIsOwnedFalse()
    {
        var entity = new EntityModel("Address", new List<PropertyModel>());

        Assert.False(entity.IsOwned);
    }

    [Fact]
    public void RelationshipKind_HasOwnedMember()
    {
        Assert.True(Enum.IsDefined(typeof(RelationshipKind), RelationshipKind.Owned));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter OwnedTypeModelFieldsTests`
Expected: FAIL to compile — `IsOwned`, `OwnerNavigationProperty`, `RelationshipKind.Owned` don't exist yet.

- [ ] **Step 3: Add the fields**

In `src/EfSchemaVisualizer.Core/Model/PropertyModel.cs`, add two new parameters at the end of the record's parameter list (after `DeclaringEntityName`):

```csharp
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
    string? DefaultValueSql = null,
    string? ValueGenerated = null,
    bool IsShadow = false,
    bool IsRowVersion = false,
    bool IsConcurrencyToken = false,
    string? Comment = null,
    bool? IsUnicode = null,
    bool? IsFixedLength = null,
    string? Collation = null,
    string? InverseProperty = null,
    string? DeclaringEntityName = null,
    bool IsOwned = false,
    string? OwnerNavigationProperty = null);
```

In `src/EfSchemaVisualizer.Core/Model/EntityModel.cs`, add one new parameter at the end (after `BaseEntityName`):

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
    bool IsOwned = false)
{
    public IReadOnlyList<string> KeyPropertyNames { get; init; } = KeyPropertyNames ?? new List<string>();
    public IReadOnlyList<IndexModel> Indexes { get; init; } = Indexes ?? new List<IndexModel>();
    public IReadOnlyList<IReadOnlyList<string>> AlternateKeys { get; init; } = AlternateKeys ?? new List<IReadOnlyList<string>>();
    public IReadOnlyList<string> SplitTables { get; init; } = SplitTables ?? new List<string>();
}
```

In `src/EfSchemaVisualizer.Core/Model/RelationshipKind.cs`, add `Owned`:

```csharp
namespace EfSchemaVisualizer.Core.Model;

public enum RelationshipKind
{
    OneToOne,
    OneToMany,
    ManyToMany,
    Inheritance,
    Owned,
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter OwnedTypeModelFieldsTests`
Expected: PASS (3 tests)

- [ ] **Step 5: Run the full solution test suite**

Run: `dotnet test`
Expected: All existing tests still PASS (additive change, no behavior touched).

- [ ] **Step 6: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Model/PropertyModel.cs src/EfSchemaVisualizer.Core/Model/EntityModel.cs src/EfSchemaVisualizer.Core/Model/RelationshipKind.cs tests/EfSchemaVisualizer.Core.Tests/Model/OwnedTypeModelFieldsTests.cs
git commit -m "Add IsOwned/OwnerNavigationProperty model fields and RelationshipKind.Owned for backlog W3"
```

---

### Task 2: Parse `OwnsOne`/`OwnsMany` calls — `OwnedTypeConfig` + `FluentConfigParser.ParseOwnedTypeCalls`

**Files:**
- Create: `src/EfSchemaVisualizer.Core/Merging/OwnedTypeConfig.cs`
- Modify: `src/EfSchemaVisualizer.Core/Parsing/DiagnosticCodes.cs`
- Modify: `src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserOwnedTypeTests.cs` (new)

**Interfaces:**
- Consumes: `FluentSyntaxHelpers.FindConfigurationScopes(CompilationUnitSyntax)`, `FluentSyntaxHelpers.FindCallsNamed(SyntaxNode, string)`, `FluentSyntaxHelpers.TryReadSinglePropertyNameArgument(InvocationExpressionSyntax)` (all `internal`, same assembly).
- Produces: `OwnedTypeConfig(string OwnerEntityName, string NavigationPropertyName, bool IsMany)`, `FluentConfigParser.ParseOwnedTypeCalls(string sourceCode) -> ParseResult<IReadOnlyList<OwnedTypeConfig>>`, `DiagnosticCodes.OwnedNestedConfigIgnored`. `OwnsOne`/`OwnsMany` added to `FluentConfigParser.RecognizedCallNames` so the outer call stops firing `UnrecognizedConfigCall`.

- [ ] **Step 1: Write the failing tests**

Create `tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserOwnedTypeTests.cs`:

```csharp
using System.Linq;
using EfSchemaVisualizer.Core.Parsing;
using Xunit;

namespace EfSchemaVisualizer.Core.Tests.Parsing;

public class FluentConfigParserOwnedTypeTests
{
    private static readonly FluentConfigParser Parser = new();

    [Fact]
    public void ParseOwnedTypeCalls_OwnsOne_ResolvesOwnerAndNavigationProperty()
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

        var result = Parser.ParseOwnedTypeCalls(source);

        var config = Assert.Single(result.Value);
        Assert.Equal("Order", config.OwnerEntityName);
        Assert.Equal("ShippingAddress", config.NavigationPropertyName);
        Assert.False(config.IsMany);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void ParseOwnedTypeCalls_OwnsMany_SetsIsManyTrue()
    {
        const string source = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Order>(entity =>
                    {
                        entity.OwnsMany(e => e.Notes);
                    });
                }
            }
            """;

        var result = Parser.ParseOwnedTypeCalls(source);

        var config = Assert.Single(result.Value);
        Assert.True(config.IsMany);
        Assert.Equal("Notes", config.NavigationPropertyName);
    }

    [Fact]
    public void ParseOwnedTypeCalls_BuilderLambdaWithNestedCalls_FiresOwnedNestedConfigIgnored()
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

        Assert.Single(result.Value);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.OwnedNestedConfigIgnored, diagnostic.Code);
    }

    [Fact]
    public void ParseOwnedTypeCalls_BuilderLambdaWithNoCalls_NoDiagnostic()
    {
        const string source = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Order>(entity =>
                    {
                        entity.OwnsOne(e => e.ShippingAddress, b => { });
                    });
                }
            }
            """;

        var result = Parser.ParseOwnedTypeCalls(source);

        Assert.Single(result.Value);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void ParseUnrecognizedCalls_OwnsOneCall_NoLongerFlagged()
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

        var diagnostics = Parser.ParseUnrecognizedCalls(source);

        Assert.Empty(diagnostics);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter FluentConfigParserOwnedTypeTests`
Expected: FAIL to compile — `ParseOwnedTypeCalls` and `OwnedTypeConfig` don't exist; `DiagnosticCodes.OwnedNestedConfigIgnored` doesn't exist.

- [ ] **Step 3: Create `OwnedTypeConfig`**

Create `src/EfSchemaVisualizer.Core/Merging/OwnedTypeConfig.cs`:

```csharp
namespace EfSchemaVisualizer.Core.Merging;

public sealed record OwnedTypeConfig(string OwnerEntityName, string NavigationPropertyName, bool IsMany);
```

- [ ] **Step 4: Add the diagnostic code**

In `src/EfSchemaVisualizer.Core/Parsing/DiagnosticCodes.cs`, add after `ArchiveBuildArtifactSkipped`:

```csharp
    public const string OwnedNestedConfigIgnored = nameof(OwnedNestedConfigIgnored);
```

- [ ] **Step 5: Add `OwnsOne`/`OwnsMany` to `RecognizedCallNames` and implement `ParseOwnedTypeCalls`**

In `src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs`, add to the `RecognizedCallNames` set (line 25, after `"SplitToTable",`):

```csharp
        "SplitToTable", "OwnsOne", "OwnsMany",
```

Add `using EfSchemaVisualizer.Core.Merging;` if not already present (it already is, line 4). Add this method (placed after `ParseSplitTables`, before `ParseColumnNames`, to keep it near the other entity-scoped `Parse*` methods):

```csharp
    /// Detects `OwnsOne`/`OwnsMany` calls per entity scope, recording which navigation property
    /// each targets so `OwnedTypeInference` can resolve the owned type and fold (`OwnsOne`) or
    /// link (`OwnsMany`) it. The builder (second) lambda's body is intentionally not walked for
    /// column-level configuration — only whether it contains any call at all is checked, to flag
    /// `OwnedNestedConfigIgnored` rather than silently dropping it. This only recognizes the
    /// two-lambda-argument shape (`OwnsOne(nav, builder)`); the single-argument fluently-chained
    /// overload (`OwnsOne(nav).Property(...)`) is not specifically detected for this diagnostic —
    /// same scope cut as `ToTable`/`SplitToTable`'s documented builder-lambda-only reads.
    public ParseResult<IReadOnlyList<OwnedTypeConfig>> ParseOwnedTypeCalls(string sourceCode)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var results = new List<OwnedTypeConfig>();
        var diagnostics = new List<Diagnostic>();

        foreach (var (entityName, scope) in FluentSyntaxHelpers.FindConfigurationScopes(root))
        {
            foreach (var (callName, isMany) in new[] { ("OwnsOne", false), ("OwnsMany", true) })
            {
                foreach (var call in FluentSyntaxHelpers.FindCallsNamed(scope, callName))
                {
                    var navigationPropertyName = FluentSyntaxHelpers.TryReadSinglePropertyNameArgument(call);

                    if (navigationPropertyName is null)
                    {
                        diagnostics.Add(new Diagnostic(
                            DiagnosticCodes.UnresolvablePropertyName,
                            $"Could not determine which navigation property this {callName} call configures.",
                            entityName,
                            PropertyName: null,
                            call.Span));
                        continue;
                    }

                    results.Add(new OwnedTypeConfig(entityName, navigationPropertyName, isMany));

                    if (HasNestedConfigCalls(call))
                    {
                        diagnostics.Add(new Diagnostic(
                            DiagnosticCodes.OwnedNestedConfigIgnored,
                            $"Configuration inside this {callName} call's builder is not read and was ignored.",
                            entityName,
                            navigationPropertyName,
                            call.Span));
                    }
                }
            }
        }

        return new ParseResult<IReadOnlyList<OwnedTypeConfig>>(results, diagnostics);
    }

    /// True if `call`'s second lambda argument (the builder) has any invocation inside it.
    /// The first lambda argument is always the navigation-property selector, never the builder.
    private static bool HasNestedConfigCalls(InvocationExpressionSyntax call)
    {
        var builderLambda = call.ArgumentList.Arguments
            .Select(a => a.Expression)
            .OfType<AnonymousFunctionExpressionSyntax>()
            .Skip(1)
            .FirstOrDefault();

        return builderLambda is not null && builderLambda.DescendantNodes().OfType<InvocationExpressionSyntax>().Any();
    }
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter "FluentConfigParserOwnedTypeTests|FluentConfigParserTests"`
Expected: PASS — new tests green, and existing `FluentConfigParserTests` (including any `ParseUnrecognizedCalls` coverage) still green.

- [ ] **Step 7: Run the full solution test suite**

Run: `dotnet test`
Expected: All PASS.

- [ ] **Step 8: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Merging/OwnedTypeConfig.cs src/EfSchemaVisualizer.Core/Parsing/DiagnosticCodes.cs src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserOwnedTypeTests.cs
git commit -m "Parse OwnsOne/OwnsMany calls, flag ignored nested builder config"
```

---

### Task 3: `Core.Inference.OwnedTypeInference` — fold `OwnsOne`, mark `OwnsMany`

**Files:**
- Create: `src/EfSchemaVisualizer.Core/Inference/OwnedTypeInference.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Inference/OwnedTypeInferenceTests.cs` (new)

**Interfaces:**
- Consumes: `EntityModel`, `PropertyModel`, `OwnedTypeConfig` (from Task 1/2), `FluentSyntaxHelpers.TryGetElementTypeName(string)` (internal, same assembly).
- Produces: `OwnedTypeFoldResult(IReadOnlyList<EntityModel> Entities, IReadOnlyList<RelationshipModel> Relationships)`, `OwnedTypeInference.Fold(IReadOnlyList<EntityModel> entities, IReadOnlyList<OwnedTypeConfig> ownedCalls) -> OwnedTypeFoldResult`.

- [ ] **Step 1: Write the failing tests**

Create `tests/EfSchemaVisualizer.Core.Tests/Inference/OwnedTypeInferenceTests.cs`:

```csharp
using System.Collections.Generic;
using System.Linq;
using EfSchemaVisualizer.Core.Inference;
using EfSchemaVisualizer.Core.Merging;
using EfSchemaVisualizer.Core.Model;
using Xunit;

namespace EfSchemaVisualizer.Core.Tests.Inference;

public class OwnedTypeInferenceTests
{
    private static PropertyModel Property(string name, string clrType) =>
        new(name, clrType, IsNullable: false, MaxLength: null);

    [Fact]
    public void Fold_NoOwnedCalls_ReturnsEntitiesUnchangedAndNoRelationships()
    {
        var order = new EntityModel("Order", new[] { Property("Id", "int") }, KeyPropertyNames: new[] { "Id" });

        var result = OwnedTypeInference.Fold(new[] { order }, Array.Empty<OwnedTypeConfig>());

        Assert.Same(order, Assert.Single(result.Entities));
        Assert.Empty(result.Relationships);
    }

    [Fact]
    public void Fold_OwnsOne_RemovesNavPropertyAndSplicesInOwnedProperties()
    {
        var order = new EntityModel(
            "Order",
            new[] { Property("Id", "int"), Property("ShippingAddress", "Address") },
            KeyPropertyNames: new[] { "Id" });
        var address = new EntityModel("Address", new[] { Property("Street", "string"), Property("City", "string") });

        var result = OwnedTypeInference.Fold(
            new[] { order, address },
            new[] { new OwnedTypeConfig("Order", "ShippingAddress", IsMany: false) });

        var foldedOrder = Assert.Single(result.Entities);
        Assert.Equal("Order", foldedOrder.Name);
        Assert.Equal(new[] { "Id", "Street", "City" }, foldedOrder.Properties.Select(p => p.Name));
        Assert.DoesNotContain(foldedOrder.Properties, p => p.Name == "ShippingAddress");

        var street = foldedOrder.Properties.Single(p => p.Name == "Street");
        Assert.True(street.IsOwned);
        Assert.Equal("ShippingAddress", street.OwnerNavigationProperty);
        Assert.Empty(result.Relationships);
    }

    [Fact]
    public void Fold_OwnsOne_TargetEntityAbsentFromResult()
    {
        var order = new EntityModel("Order", new[] { Property("ShippingAddress", "Address") });
        var address = new EntityModel("Address", new[] { Property("Street", "string") });

        var result = OwnedTypeInference.Fold(
            new[] { order, address },
            new[] { new OwnedTypeConfig("Order", "ShippingAddress", IsMany: false) });

        Assert.DoesNotContain(result.Entities, e => e.Name == "Address");
    }

    [Fact]
    public void Fold_TwoOwnsOneNavsOfSameTargetType_BothGroupsFoldedIndependently()
    {
        var order = new EntityModel(
            "Order",
            new[] { Property("ShippingAddress", "Address"), Property("BillingAddress", "Address") });
        var address = new EntityModel("Address", new[] { Property("Street", "string") });

        var result = OwnedTypeInference.Fold(
            new[] { order, address },
            new[]
            {
                new OwnedTypeConfig("Order", "ShippingAddress", IsMany: false),
                new OwnedTypeConfig("Order", "BillingAddress", IsMany: false),
            });

        var foldedOrder = result.Entities.Single(e => e.Name == "Order");
        var streets = foldedOrder.Properties.Where(p => p.Name == "Street").ToList();
        Assert.Equal(2, streets.Count);
        Assert.Contains(streets, p => p.OwnerNavigationProperty == "ShippingAddress");
        Assert.Contains(streets, p => p.OwnerNavigationProperty == "BillingAddress");
    }

    [Fact]
    public void Fold_MultiLevelOwnedChain_FoldsTransitively()
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
        Assert.Equal(new[] { "Street", "Name" }, foldedOrder.Properties.Select(p => p.Name));
    }

    [Fact]
    public void Fold_MalformedOwnershipCycle_DoesNotThrowAndStopsAtCycle()
    {
        var a = new EntityModel("A", new[] { Property("BNav", "B") });
        var b = new EntityModel("B", new[] { Property("ANav", "A") });

        var result = OwnedTypeInference.Fold(
            new[] { a, b },
            new[]
            {
                new OwnedTypeConfig("A", "BNav", IsMany: false),
                new OwnedTypeConfig("B", "ANav", IsMany: false),
            });

        Assert.True(result.Entities.Count >= 1);
    }

    [Fact]
    public void Fold_NavigationPropertyNotFoundOnOwner_LeavesEntitiesUnchanged()
    {
        var order = new EntityModel("Order", new[] { Property("Id", "int") });
        var address = new EntityModel("Address", new[] { Property("Street", "string") });

        var result = OwnedTypeInference.Fold(
            new[] { order, address },
            new[] { new OwnedTypeConfig("Order", "ShippingAddress", IsMany: false) });

        Assert.Equal(2, result.Entities.Count);
    }

    [Fact]
    public void Fold_TargetEntityTypeNotResolvable_LeavesEntitiesUnchanged()
    {
        var order = new EntityModel("Order", new[] { Property("ShippingAddress", "Address") });

        var result = OwnedTypeInference.Fold(
            new[] { order },
            new[] { new OwnedTypeConfig("Order", "ShippingAddress", IsMany: false) });

        var unchanged = Assert.Single(result.Entities);
        Assert.Contains(unchanged.Properties, p => p.Name == "ShippingAddress");
    }

    [Fact]
    public void Fold_OwnsMany_KeepsTargetStandaloneMarkedOwnedAndEmitsOwnedRelationship()
    {
        var order = new EntityModel("Order", new[] { Property("Notes", "ICollection<OrderNote>") });
        var note = new EntityModel("OrderNote", new[] { Property("Text", "string") });

        var result = OwnedTypeInference.Fold(
            new[] { order, note },
            new[] { new OwnedTypeConfig("Order", "Notes", IsMany: true) });

        Assert.Equal(2, result.Entities.Count);
        var foldedNote = result.Entities.Single(e => e.Name == "OrderNote");
        Assert.True(foldedNote.IsOwned);

        var relationship = Assert.Single(result.Relationships);
        Assert.Equal("Order", relationship.PrincipalEntity);
        Assert.Equal("OrderNote", relationship.DependentEntity);
        Assert.Equal(RelationshipKind.Owned, relationship.Kind);
        Assert.Equal("Notes", relationship.PrincipalNavigation);
        Assert.False(relationship.IsInferred);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter OwnedTypeInferenceTests`
Expected: FAIL to compile — `OwnedTypeInference` doesn't exist.

- [ ] **Step 3: Implement `OwnedTypeInference`**

Create `src/EfSchemaVisualizer.Core/Inference/OwnedTypeInference.cs`:

```csharp
using System.Collections.Generic;
using System.Linq;
using EfSchemaVisualizer.Core.Merging;
using EfSchemaVisualizer.Core.Model;
using EfSchemaVisualizer.Core.Parsing;

namespace EfSchemaVisualizer.Core.Inference;

public sealed record OwnedTypeFoldResult(
    IReadOnlyList<EntityModel> Entities,
    IReadOnlyList<RelationshipModel> Relationships);

public static class OwnedTypeInference
{
    public static OwnedTypeFoldResult Fold(IReadOnlyList<EntityModel> entities, IReadOnlyList<OwnedTypeConfig> ownedCalls)
    {
        var byName = entities.ToDictionary(e => e.Name);
        var ownsOneByOwner = ownedCalls.Where(c => !c.IsMany).ToLookup(c => c.OwnerEntityName);
        var removedEntityNames = new HashSet<string>();
        var memo = new Dictionary<string, IReadOnlyList<PropertyModel>>();

        // Folds `entityName`'s own OwnsOne targets into it, recursively (root of the recursion is
        // whichever entity a caller starts from; a multi-level owned chain like Order->Address->
        // Country resolves Country and Address fully before Order splices Address's already-folded
        // properties in). `visited` guards one recursion chain against a cycle (A owns B owns A):
        // hitting an entity already on the current path returns null instead of recursing forever,
        // and the caller that receives null leaves that particular nav property un-folded rather
        // than guessing. Memoized so a target reachable from two different owners (or visited twice
        // via the outer per-entity loop below) is only resolved once.
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

            foreach (var call in ownsOneByOwner[entityName])
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
                        IsOwned = true,
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

        var relationships = new List<RelationshipModel>();

        foreach (var call in ownedCalls.Where(c => c.IsMany))
        {
            if (!byName.TryGetValue(call.OwnerEntityName, out var owner))
            {
                continue;
            }

            var navProperty = owner.Properties.FirstOrDefault(p => p.Name == call.NavigationPropertyName);
            var targetName = navProperty is null ? null : FluentSyntaxHelpers.TryGetElementTypeName(navProperty.ClrType);

            if (targetName is null || !byName.TryGetValue(targetName, out var target))
            {
                continue;
            }

            byName[targetName] = target with { IsOwned = true };
            relationships.Add(new RelationshipModel(
                call.OwnerEntityName,
                targetName,
                RelationshipKind.Owned,
                PrincipalNavigation: call.NavigationPropertyName,
                DependentNavigation: null,
                ForeignKeyProperties: new List<string>(),
                IsInferred: false));
        }

        var foldedEntities = entities
            .Where(e => !removedEntityNames.Contains(e.Name))
            .Select(e =>
            {
                var marked = byName[e.Name]; // same reference as `e` unless the OwnsMany pass mutated it
                var properties = memo.TryGetValue(e.Name, out var p) ? p : e.Properties;

                return ReferenceEquals(properties, e.Properties) && ReferenceEquals(marked, e)
                    ? e
                    : e with { Properties = properties, IsOwned = marked.IsOwned };
            })
            .ToList();

        return new OwnedTypeFoldResult(foldedEntities, relationships);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter OwnedTypeInferenceTests`
Expected: PASS (10 tests) — this includes the reference-equality no-op case (`Fold_NoOwnedCalls_ReturnsEntitiesUnchangedAndNoRelationships` asserts `Assert.Same`), the two-navs-of-the-same-target-type case, and the multi-level chain case (`Order.ShippingAddress -> Address.Country -> Country` resolves `Country` and `Address` bottom-up before `Order` splices in, so `Order` ends up with `Street`/`Name` and both `Address` and `Country` are absent from the result).

- [ ] **Step 5: Run the full solution test suite**

Run: `dotnet test`
Expected: All PASS.

- [ ] **Step 6: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Inference/OwnedTypeInference.cs tests/EfSchemaVisualizer.Core.Tests/Inference/OwnedTypeInferenceTests.cs
git commit -m "Add OwnedTypeInference: fold OwnsOne inline, mark OwnsMany targets as owned"
```

---

### Task 4: Wire `OwnedTypeInference` into `DiagramModelBuilder.Build`

**Files:**
- Modify: `src/EfSchemaVisualizer.Web/DiagramModelBuilder.cs`
- Test: `tests/EfSchemaVisualizer.Web.Tests/DiagramModelBuilderOwnedTypeTests.cs` (new)

**Interfaces:**
- Consumes: `FluentConfigParser.ParseOwnedTypeCalls` (Task 2), `OwnedTypeInference.Fold` (Task 3).
- Produces: `DiagramModelBuilder.Build` now folds `OwnsOne` and marks `OwnsMany` before key inference/inheritance folding; `DiagramModelResult.Entities`/`Relationships` reflect it.

- [ ] **Step 1: Write the failing test**

Create `tests/EfSchemaVisualizer.Web.Tests/DiagramModelBuilderOwnedTypeTests.cs`:

```csharp
using System.Linq;
using EfSchemaVisualizer.Core.Model;
using EfSchemaVisualizer.Web;
using Xunit;

namespace EfSchemaVisualizer.Web.Tests;

public class DiagramModelBuilderOwnedTypeTests
{
    [Fact]
    public void Build_OwnsOne_AddressNotStandaloneAndOrderHasFoldedProperties()
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
                        entity.OwnsOne(e => e.ShippingAddress);
                    });
                }
            }
            """;

        var result = DiagramModelBuilder.Build(classSource, configSource);

        Assert.DoesNotContain(result.Entities, e => e.Name == "Address");
        var order = result.Entities.Single(e => e.Name == "Order");
        Assert.Contains(order.Properties, p => p.Name == "Street" && p.IsOwned);
        Assert.Contains(order.Properties, p => p.Name == "City" && p.IsOwned);
        Assert.DoesNotContain(order.Properties, p => p.Name == "ShippingAddress");
    }

    [Fact]
    public void Build_OwnsMany_TargetKeptStandaloneMarkedOwnedWithOwnedRelationship()
    {
        const string classSource = """
            public class Order
            {
                public int Id { get; set; }
                public ICollection<OrderNote> Notes { get; set; }
            }

            public class OrderNote
            {
                public string Text { get; set; }
            }
            """;

        const string configSource = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Order>(entity =>
                    {
                        entity.OwnsMany(e => e.Notes);
                    });
                }
            }
            """;

        var result = DiagramModelBuilder.Build(classSource, configSource);

        var note = result.Entities.Single(e => e.Name == "OrderNote");
        Assert.True(note.IsOwned);
        Assert.Contains(result.Relationships, r => r.Kind == RelationshipKind.Owned
            && r.PrincipalEntity == "Order" && r.DependentEntity == "OrderNote");
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/EfSchemaVisualizer.Web.Tests --filter DiagramModelBuilderOwnedTypeTests`
Expected: FAIL — `Address` still appears standalone (not wired in yet).

- [ ] **Step 3: Wire it into `DiagramModelBuilder.Build`**

In `src/EfSchemaVisualizer.Web/DiagramModelBuilder.cs`, add near the other `configParser.Parse*` calls (after line 50, `var ignoredEntityNames = ...`):

```csharp
        var ownedTypeCalls = configParser.ParseOwnedTypeCalls(configSource);
```

Add its diagnostics alongside the others (after line 83, `diagnostics.AddRange(unrecognizedCalls);`):

```csharp
        diagnostics.AddRange(ownedTypeCalls.Diagnostics);
```

Change the end of the entities pipeline (lines 91-126) from:

```csharp
        IReadOnlyList<EntityModel> entities = entityResult.Value
            .Where(entity => !ignoredEntityNames.Contains(entity.Name))
            .Select(entity => ModelMerger.ApplyMaxLengths(entity, maxLengths.Value))
            // ... (unchanged middle of the chain) ...
            .Select(entity => ModelMerger.ApplyShadowProperties(entity, shadowProperties.Value))
            .Select(ConventionInference.InferKey)
            .ToList();

        var inheritanceFold = InheritanceInference.Fold(entities);
        entities = inheritanceFold.Entities;
```

to:

```csharp
        IReadOnlyList<EntityModel> mergedEntities = entityResult.Value
            .Where(entity => !ignoredEntityNames.Contains(entity.Name))
            .Select(entity => ModelMerger.ApplyMaxLengths(entity, maxLengths.Value))
            // ... (unchanged middle of the chain) ...
            .Select(entity => ModelMerger.ApplyShadowProperties(entity, shadowProperties.Value))
            .ToList();

        var ownedTypeFold = OwnedTypeInference.Fold(mergedEntities, ownedTypeCalls.Value);

        IReadOnlyList<EntityModel> entities = ownedTypeFold.Entities
            .Select(ConventionInference.InferKey)
            .ToList();

        var inheritanceFold = InheritanceInference.Fold(entities);
        entities = inheritanceFold.Entities;
```

(Keep every line in the "unchanged middle of the chain" exactly as it is today — only the variable name at the top (`entities` → `mergedEntities`), the removal of `.Select(ConventionInference.InferKey)` from that chain, and the new lines after it change.)

Finally, change the `allRelationships` concatenation (lines 153-156) from:

```csharp
        var allRelationships = relationshipModels
            .Concat(inferredRelationships)
            .Concat(inheritanceFold.Relationships)
            .ToList();
```

to:

```csharp
        var allRelationships = relationshipModels
            .Concat(inferredRelationships)
            .Concat(inheritanceFold.Relationships)
            .Concat(ownedTypeFold.Relationships)
            .ToList();
```

Add `using EfSchemaVisualizer.Core.Merging;` to the top of the file if `OwnedTypeConfig`/`ownedTypeCalls.Value`'s type isn't otherwise resolvable (check first — `EfSchemaVisualizer.Core.Merging` may already be in scope via existing usings; if not, add it next to the existing `using EfSchemaVisualizer.Core.Inference;` line).

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/EfSchemaVisualizer.Web.Tests --filter DiagramModelBuilderOwnedTypeTests`
Expected: PASS (2 tests).

- [ ] **Step 5: Run the full solution test suite**

Run: `dotnet test`
Expected: All PASS — this reorders when `ConventionInference.InferKey` runs relative to the owned-type fold, so pay particular attention to any existing `DiagramModelBuilderTests` covering convention-inferred keys; they should be unaffected since owned-type folding never touches an entity with no `OwnsOne`/`OwnsMany` call referencing it.

- [ ] **Step 6: Commit**

```bash
git add src/EfSchemaVisualizer.Web/DiagramModelBuilder.cs tests/EfSchemaVisualizer.Web.Tests/DiagramModelBuilderOwnedTypeTests.cs
git commit -m "Wire OwnedTypeInference into DiagramModelBuilder.Build"
```

---

### Task 5: Rendering — owned relationship kind, folded-property grouping, owned-entity indicator

**Files:**
- Modify: `src/EfSchemaVisualizer.Web/Diagram/RelationshipLabels.cs`
- Modify: `src/EfSchemaVisualizer.Web/Diagram/DiagramSync.cs`
- Modify: `src/EfSchemaVisualizer.Web/Diagram/RelationshipLinkLabel.razor`
- Modify: `src/EfSchemaVisualizer.Web/Diagram/EntityNode.razor`
- Test: `tests/EfSchemaVisualizer.Web.Tests/RelationshipLabelsTests.cs` (modify if it exists, else create)

**Interfaces:**
- Consumes: `RelationshipKind.Owned` (Task 1), `PropertyModel.IsOwned`/`OwnerNavigationProperty` (Task 1), `EntityModel.IsOwned` (Task 1).
- Produces: no new public interfaces — this task only changes rendering behavior consumed by the running app.

- [ ] **Step 1: Check for an existing `RelationshipLabelsTests` file**

Run: `find tests -iname "RelationshipLabelsTests.cs"`

If it exists, read it and add a case there in Step 2 below instead of creating a new file. If it doesn't exist, create `tests/EfSchemaVisualizer.Web.Tests/RelationshipLabelsTests.cs`.

- [ ] **Step 2: Write the failing test**

```csharp
using EfSchemaVisualizer.Core.Model;
using EfSchemaVisualizer.Web.Diagram;
using Xunit;

namespace EfSchemaVisualizer.Web.Tests;

public class RelationshipLabelsTests
{
    [Fact]
    public void For_Owned_ReturnsDistinctGlyph()
    {
        var label = RelationshipLabels.For(RelationshipKind.Owned);

        Assert.Equal("◆", label);
    }
}
```

(If the file already existed with other cases, just add this one `[Fact]` method to the existing class instead of the whole file above.)

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test tests/EfSchemaVisualizer.Web.Tests --filter RelationshipLabelsTests`
Expected: FAIL — `RelationshipLabels.For(RelationshipKind.Owned)` currently returns `"?"` (the switch's default arm).

- [ ] **Step 4: Add the `Owned` glyph**

In `src/EfSchemaVisualizer.Web/Diagram/RelationshipLabels.cs`, add a case:

```csharp
    public static string For(RelationshipKind kind) => kind switch
    {
        RelationshipKind.OneToOne => "1—1",
        RelationshipKind.OneToMany => "1—*",
        RelationshipKind.ManyToMany => "*—*",
        RelationshipKind.Inheritance => "▷",
        RelationshipKind.Owned => "◆",
        _ => "?",
    };
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/EfSchemaVisualizer.Web.Tests --filter RelationshipLabelsTests`
Expected: PASS.

- [ ] **Step 6: Add the `Owned` edge color in `DiagramSync.cs`**

In `src/EfSchemaVisualizer.Web/Diagram/DiagramSync.cs`, change:

```csharp
            var link = new LinkModel(dependentNode, principalNode);
            if (relationship.Kind == RelationshipKind.Inheritance)
            {
                link.Color = "#4a5a8a";
            }
            else if (relationship.IsInferred)
            {
                link.Color = "#aaaaaa";
            }
```

to:

```csharp
            var link = new LinkModel(dependentNode, principalNode);
            if (relationship.Kind == RelationshipKind.Inheritance)
            {
                link.Color = "#4a5a8a";
            }
            else if (relationship.Kind == RelationshipKind.Owned)
            {
                link.Color = "#8a6a4a";
            }
            else if (relationship.IsInferred)
            {
                link.Color = "#aaaaaa";
            }
```

- [ ] **Step 7: Add the read-only expanded view for `Owned` in `RelationshipLinkLabel.razor`**

In `src/EfSchemaVisualizer.Web/Diagram/RelationshipLinkLabel.razor`, change:

```razor
        @if (Label.Relationship.Kind == RelationshipKind.Inheritance)
        {
            <div style="display: block;">@Label.Relationship.DependentEntity extends @Label.Relationship.PrincipalEntity</div>
        }
        else
        {
```

to:

```razor
        @if (Label.Relationship.Kind == RelationshipKind.Inheritance)
        {
            <div style="display: block;">@Label.Relationship.DependentEntity extends @Label.Relationship.PrincipalEntity</div>
        }
        else if (Label.Relationship.Kind == RelationshipKind.Owned)
        {
            <div style="display: block;">@Label.Relationship.PrincipalEntity owns @Label.Relationship.DependentEntity via @Label.Relationship.PrincipalNavigation</div>
        }
        else
        {
```

- [ ] **Step 8: Group folded owned properties and add the owned-entity indicator in `EntityNode.razor`**

In `src/EfSchemaVisualizer.Web/Diagram/EntityNode.razor`, change the card header (around line 17-38) to show an "owned" indicator next to the title when `Node.Entity.IsOwned` is true — change:

```razor
    <div class="card-header" style="font-weight: bold; padding: 4px 8px; background: #eee; display: flex; justify-content: space-between; align-items: center;">
        <span style="flex: 1;">
            @if (_isEditingName)
```

to:

```razor
    <div class="card-header" style="font-weight: bold; padding: 4px 8px; background: #eee; display: flex; justify-content: space-between; align-items: center;">
        <span style="flex: 1;">
            @if (Node.Entity.IsOwned)
            {
                <span title="Owned type (OwnsMany): this is its own table, but it cannot exist independently of its owner." style="opacity: 0.6; margin-right: 2px;">◆</span>
            }
            @if (_isEditingName)
```

Then change the property list loop (line 91-92) to render a grouping sub-header whenever `OwnerNavigationProperty` changes, and skip all edit controls for an owned property. Replace:

```razor
    <ul style="list-style: none; margin: 0; padding: 0;">
        @foreach (var property in Node.Entity.Properties)
        {
            var isKey = Node.Entity.KeyPropertyNames.Contains(property.Name);
            <li style="padding: 2px 8px; @(isKey ? "font-weight: bold;" : "")">
                @if (property.IsShadow)
                {
```

with:

```razor
    <ul style="list-style: none; margin: 0; padding: 0;">
        @{ string? _lastOwnerNav = null; }
        @foreach (var property in Node.Entity.Properties)
        {
            var isKey = Node.Entity.KeyPropertyNames.Contains(property.Name);
            if (property.IsOwned && property.OwnerNavigationProperty != _lastOwnerNav)
            {
                _lastOwnerNav = property.OwnerNavigationProperty;
                <li style="padding: 4px 8px 0; font-size: 0.7em; color: #888; border-top: 1px dashed #ccc;">@_lastOwnerNav (owned)</li>
            }
            else if (!property.IsOwned)
            {
                _lastOwnerNav = null;
            }
            <li style="padding: 2px 8px; @(isKey ? "font-weight: bold;" : "") @(property.IsOwned ? "opacity: 0.75;" : "")">
                @if (property.IsOwned)
                {
                    <span title="Folded in from the owned type via @property.OwnerNavigationProperty. Read-only.">@property.Name : @property.ClrType@(property.IsNullable ? "?" : "")</span>
                }
                else if (property.IsShadow)
                {
```

This turns the original two-branch `@if (property.IsShadow) { ... } else { ... }` into a three-branch chain. The `else` in front of the original `@if (property.IsShadow)` (which the edit above already added) is the only change needed to that line itself — do not touch the shadow-property markup inside it, nor the final `else` block (the full editable-property markup, originally lines 101-307) at all; both keep their exact existing content. After the edit, the chain reads: `@if (property.IsOwned) { <span>read-only</span> } else if (property.IsShadow) { <existing shadow markup> } else { <existing full editable markup> }`. Read the file back after making the edit to confirm the brace/`<li>`/`</li>` nesting is still balanced (the new `if`/`else if` block you inserted before the `<li style="...">` line, plus the three-way `@if`/`else if`/`else` inside it, must each close exactly once) — Razor will fail to build otherwise, which the next step catches.

- [ ] **Step 9: Manually verify the app still builds**

Run: `dotnet build`
Expected: Build succeeds with no Razor compilation errors.

- [ ] **Step 10: Run the full solution test suite**

Run: `dotnet test`
Expected: All PASS.

- [ ] **Step 11: Manual smoke test in the browser**

Use the `run` skill (or `dotnet run --project src/EfSchemaVisualizer.Web`) to launch the app. Paste a class source with `public class Order { public int Id {get;set;} public Address ShippingAddress {get;set;} }` / `public class Address { public string Street {get;set;} public string City {get;set;} }` and a config source with `entity.OwnsOne(e => e.ShippingAddress);` inside `Entity<Order>`. Confirm:
- `Address` does not appear as its own card.
- `Order`'s card shows `Street`/`City` grouped under a "ShippingAddress (owned)" sub-header, with no rename/type-edit/remove controls on those rows.
- Repeat with `OwnsMany` and confirm the target keeps its own card, shows the ◆ indicator, and the edge between owner and target is the new distinct color with the ◆ label; expanding it shows the read-only "X owns Y via Z" line with no Kind dropdown/Remove button.

- [ ] **Step 12: Commit**

```bash
git add src/EfSchemaVisualizer.Web/Diagram/RelationshipLabels.cs src/EfSchemaVisualizer.Web/Diagram/DiagramSync.cs src/EfSchemaVisualizer.Web/Diagram/RelationshipLinkLabel.razor src/EfSchemaVisualizer.Web/Diagram/EntityNode.razor tests/EfSchemaVisualizer.Web.Tests/RelationshipLabelsTests.cs
git commit -m "Render owned types: distinct edge/glyph, grouped read-only folded properties, owned-entity indicator"
```

---

### Task 6: Update `docs/backlog.md`

**Files:**
- Modify: `docs/backlog.md`

**Interfaces:**
- None — documentation only.

- [ ] **Step 1: Mark W3 done**

In `docs/backlog.md`, change the W3 bullet's checkbox from `- [ ]` to `- [x]` and append a "Fixed" note in the same style as W1/W2 (see the existing W1/W2 entries directly above it for the exact tone/format), summarizing: `OwnsOne` folds inline via `OwnedTypeInference` (new `Core/Inference/OwnedTypeInference.cs`), `OwnsMany` keeps its target standalone with `IsOwned=true` and a new `RelationshipKind.Owned` edge, nested builder-lambda config is flagged via the new `OwnedNestedConfigIgnored` diagnostic instead of silently dropped, and both are read-only this pass (verified against the `Order`/`ShippingAddress`/`Address` repro from the original finding).

- [ ] **Step 2: Commit**

```bash
git add docs/backlog.md
git commit -m "Mark backlog W3 (owned types render as fake tables) done"
```
