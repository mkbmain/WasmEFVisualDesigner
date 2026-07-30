# HasPrincipalKey Support Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Parse, model, merge, validate, rewrite, and edit `HasPrincipalKey(...)` on relationships, end to end, so it round-trips and is editable in the diagram UI — closing the last unread relationship-config gap.

**Architecture:** Mirrors the existing `ForeignKeyProperties`/`HasForeignKey` vertical slice exactly, at every layer (model record field → parser capture → merge passthrough → rewriter emission → `DiagramEditor` edit method → Blazor UI). One new model-validity check (`CheckPrincipalKeyReferencesMissingProperty`) catches the one genuine EF-Core-breaking shape: a `HasPrincipalKey` property that no longer exists on the principal entity (stale after a rename/removal) — deliberately *not* validating that the properties form a declared key, because EF implicitly creates the alternate key itself when they don't.

**Tech Stack:** C#, Roslyn (Microsoft.CodeAnalysis.CSharp), xUnit, Blazor (.razor).

## Global Constraints

- Every new diagnostic code follows the existing `nameof(...)` const pattern in `DiagnosticCodes.cs`, grouped with its category (parse-time "unreadable" vs. post-merge "model-validity").
- New record fields default to an empty list via the same `init`-backed-property pattern already used for `ForeignKeyProperties` on both `RelationshipConfig` and `RelationshipModel` — never `null` in practice.
- `SetRelationshipShape`'s new parameter must be added as a trailing optional parameter (after the existing optional `newConstraintName`) so every existing call site compiles unchanged.
- Follow the file's existing code style: no comments explaining *what* code does, only non-obvious *why* (matching every file touched in this plan).

---

### Task 1: Model fields and diagnostic codes

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/Merging/RelationshipConfig.cs`
- Modify: `src/EfSchemaVisualizer.Core/Model/RelationshipModel.cs`
- Modify: `src/EfSchemaVisualizer.Core/Parsing/DiagnosticCodes.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Merging/ModelMergerTests.cs` (extended in Task 3, not this task — this task only touches records/constants, which have no independent test surface)

**Interfaces:**
- Produces: `RelationshipConfig.PrincipalKeyProperties: IReadOnlyList<string>` (init, defaults to empty list), `RelationshipModel.PrincipalKeyProperties: IReadOnlyList<string>` (same), `DiagnosticCodes.UnreadableHasPrincipalKeyArgument`, `DiagnosticCodes.PrincipalKeyReferencesMissingProperty`.

- [ ] **Step 1: Add `PrincipalKeyProperties` to `RelationshipConfig`**

Edit `src/EfSchemaVisualizer.Core/Merging/RelationshipConfig.cs` to:

```csharp
using System.Collections.Generic;
using EfSchemaVisualizer.Core.Model;

namespace EfSchemaVisualizer.Core.Merging;

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
    IReadOnlyList<string>? PrincipalKeyProperties = null)
{
    public IReadOnlyList<string> ForeignKeyProperties { get; init; } = ForeignKeyProperties ?? new List<string>();
    public IReadOnlyList<string> PrincipalKeyProperties { get; init; } = PrincipalKeyProperties ?? new List<string>();
}
```

- [ ] **Step 2: Add `PrincipalKeyProperties` to `RelationshipModel`**

Edit `src/EfSchemaVisualizer.Core/Model/RelationshipModel.cs` to:

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
    string? JoinEntityName = null,
    bool IsInferred = false,
    string? ConstraintName = null,
    IReadOnlyList<string>? PrincipalKeyProperties = null)
{
    public IReadOnlyList<string> ForeignKeyProperties { get; init; } = ForeignKeyProperties ?? new List<string>();
    public IReadOnlyList<string> PrincipalKeyProperties { get; init; } = PrincipalKeyProperties ?? new List<string>();
}
```

- [ ] **Step 3: Add the two new diagnostic codes**

Edit `src/EfSchemaVisualizer.Core/Parsing/DiagnosticCodes.cs`. Add after `UnreadableHasForeignKeyArgument` (line 36):

```csharp
    public const string UnreadableHasPrincipalKeyArgument = nameof(UnreadableHasPrincipalKeyArgument);
```

Add after `ForeignKeyTargetsKeylessPrincipal` (line 61, in the model-validity group):

```csharp
    public const string PrincipalKeyReferencesMissingProperty = nameof(PrincipalKeyReferencesMissingProperty);
```

- [ ] **Step 4: Build to confirm no compile errors**

Run: `dotnet build src/EfSchemaVisualizer.Core/EfSchemaVisualizer.Core.csproj`
Expected: Build succeeds (these are additive optional-parameter changes; nothing else references the new fields yet).

- [ ] **Step 5: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Merging/RelationshipConfig.cs \
        src/EfSchemaVisualizer.Core/Model/RelationshipModel.cs \
        src/EfSchemaVisualizer.Core/Parsing/DiagnosticCodes.cs
git commit -m "Add PrincipalKeyProperties field and HasPrincipalKey diagnostic codes"
```

---

### Task 2: Parse `HasPrincipalKey`

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs`

**Interfaces:**
- Consumes: `RelationshipConfig.PrincipalKeyProperties` (Task 1), `FluentSyntaxHelpers.TryReadPropertyNameList(InvocationExpressionSyntax) -> IReadOnlyList<string>?` (existing), `FluentSyntaxHelpers.WalkChainedTail(InvocationExpressionSyntax, Action<InvocationExpressionSyntax>)` (existing), `DiagnosticCodes.UnreadableHasPrincipalKeyArgument` (Task 1).
- Produces: `FluentConfigParser.ParseRelationships` now populates `RelationshipConfig.PrincipalKeyProperties` for any `HasPrincipalKey(...)` call chained after `WithOne`/`WithMany`.

- [ ] **Step 1: Write the failing tests**

Add to `tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs`, near the existing `HasForeignKey`/`OnDelete` relationship tests (after `ParseRelationships_HasForeignKeyAndOnDelete_OrderReversed_BothStillRead`, ~line 2570):

```csharp
    [Fact]
    public void ParseRelationships_HasPrincipalKey_Present_IsRead()
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
                              .HasForeignKey(d => d.CustomerCode)
                              .HasPrincipalKey(p => p.Code);
                    });
                }
            }
            """;

        var entities = new List<EntityModel>
        {
            new("Customer", new List<PropertyModel>
            {
                new("Id", "int", IsNullable: false, MaxLength: null),
                new("Code", "string", IsNullable: false, MaxLength: null),
                new("Orders", "ICollection<Order>", IsNullable: false, MaxLength: null),
            }),
            new("Order", new List<PropertyModel>
            {
                new("Id", "int", IsNullable: false, MaxLength: null),
                new("CustomerCode", "string", IsNullable: false, MaxLength: null),
                new("Customer", "Customer", IsNullable: false, MaxLength: null),
            }),
        };

        var result = new FluentConfigParser().ParseRelationships(source, entities);

        Assert.Empty(result.Diagnostics);
        var relationship = Assert.Single(result.Value);
        Assert.Equal(new[] { "CustomerCode" }, relationship.ForeignKeyProperties);
        Assert.Equal(new[] { "Code" }, relationship.PrincipalKeyProperties);
    }

    [Fact]
    public void ParseRelationships_NoHasPrincipalKeyCall_PrincipalKeyPropertiesEmpty_NoDiagnostic()
    {
        var result = new FluentConfigParser().ParseRelationships(SourceWithHasOneWithManyBlockNested, OrderCustomerEntities);

        Assert.Empty(result.Diagnostics);
        var relationship = Assert.Single(result.Value);
        Assert.Empty(relationship.PrincipalKeyProperties);
    }

    [Fact]
    public void ParseRelationships_HasPrincipalKeyBeforeHasForeignKey_BothStillRead()
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
                              .HasPrincipalKey(p => p.Code)
                              .HasForeignKey(d => d.CustomerId);
                    });
                }
            }
            """;

        var result = new FluentConfigParser().ParseRelationships(source, OrderCustomerEntities);

        Assert.Empty(result.Diagnostics);
        var relationship = Assert.Single(result.Value);
        Assert.Equal(new[] { "CustomerId" }, relationship.ForeignKeyProperties);
        Assert.Equal(new[] { "Code" }, relationship.PrincipalKeyProperties);
    }

    [Fact]
    public void ParseRelationships_UnreadableHasPrincipalKeyArgument_EmitsDiagnostic_RelationshipStillRecorded()
    {
        const string source = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Order>(entity =>
                    {
                        entity.HasOne(d => d.Customer).WithMany(p => p.Orders).HasPrincipalKey(GetPkExpression());
                    });
                }
            }
            """;

        var result = new FluentConfigParser().ParseRelationships(source, OrderCustomerEntities);

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.UnreadableHasPrincipalKeyArgument, diagnostic.Code);
        Assert.Equal("Order", diagnostic.EntityName);
        var relationship = Assert.Single(result.Value);
        Assert.Empty(relationship.PrincipalKeyProperties);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter "FullyQualifiedName~ParseRelationships_HasPrincipalKey|FullyQualifiedName~ParseRelationships_NoHasPrincipalKeyCall|FullyQualifiedName~ParseRelationships_UnreadableHasPrincipalKeyArgument"`
Expected: FAIL — `relationship.PrincipalKeyProperties` doesn't exist yet (compile error) until Task 1 lands; if Task 1 is already committed, expect assertion failures instead (empty list / no diagnostic where one is expected), since the parser doesn't read `HasPrincipalKey` yet.

- [ ] **Step 3: Implement — recognize and read `HasPrincipalKey`**

In `src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs`:

Add `"HasPrincipalKey"` to `RecognizedCallNames` (line 32), next to `"HasForeignKey"`:

```csharp
        "HasOne", "HasMany", "WithOne", "WithMany", "HasForeignKey", "HasPrincipalKey", "OnDelete", "UsingEntity",
```

In `ParseRelationshipChain`, add a new local next to `hasForeignKeyCall` (line 1856):

```csharp
        InvocationExpressionSyntax? hasForeignKeyCall = null;
        InvocationExpressionSyntax? hasPrincipalKeyCall = null;
        InvocationExpressionSyntax? onDeleteCall = null;
        InvocationExpressionSyntax? usingEntityCall = null;
        InvocationExpressionSyntax? hasConstraintNameCall = null;

        FluentSyntaxHelpers.WalkChainedTail(withCall, invocation =>
        {
            switch (GetInvokedMethodName(invocation))
            {
                case "HasForeignKey": hasForeignKeyCall = invocation; break;
                case "HasPrincipalKey": hasPrincipalKeyCall = invocation; break;
                case "OnDelete": onDeleteCall = invocation; break;
                case "UsingEntity": usingEntityCall = invocation; break;
                case "HasConstraintName": hasConstraintNameCall = invocation; break;
            }
        });
```

After the existing `foreignKeyProperties` block (after line 1950, right before the `onDeleteBehavior` block), add:

```csharp
        IReadOnlyList<string> principalKeyProperties = Array.Empty<string>();
        if (hasPrincipalKeyCall is not null)
        {
            var props = FluentSyntaxHelpers.TryReadPropertyNameList(hasPrincipalKeyCall);

            if (props is null)
            {
                diagnostics.Add(new Diagnostic(
                    DiagnosticCodes.UnreadableHasPrincipalKeyArgument,
                    "HasPrincipalKey argument(s) could not be read as property name(s).",
                    dependentEntity,
                    PropertyName: null,
                    hasPrincipalKeyCall.Span));
            }
            else
            {
                principalKeyProperties = props;
            }
        }
```

Update the `results.Add(new RelationshipConfig(...))` call (~line 1994) to pass it through:

```csharp
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

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter "FullyQualifiedName~ParseRelationships_HasPrincipalKey|FullyQualifiedName~ParseRelationships_NoHasPrincipalKeyCall|FullyQualifiedName~ParseRelationships_UnreadableHasPrincipalKeyArgument"`
Expected: PASS (4 tests).

- [ ] **Step 5: Run the full parser test file to check for regressions**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter "FullyQualifiedName~FluentConfigParserTests"`
Expected: PASS, all existing relationship tests still green.

- [ ] **Step 6: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs \
        tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs
git commit -m "Parse HasPrincipalKey on relationship chains"
```

---

### Task 3: Merge passthrough

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/Merging/ModelMerger.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Merging/ModelMergerTests.cs`

**Interfaces:**
- Consumes: `RelationshipConfig.PrincipalKeyProperties` (Task 1).
- Produces: `ModelMerger.ApplyRelationships` now sets `RelationshipModel.PrincipalKeyProperties` from the config.

- [ ] **Step 1: Write the failing test**

Modify `tests/EfSchemaVisualizer.Core.Tests/Merging/ModelMergerTests.cs`'s existing
`ApplyRelationships_MapsConfigsToModels_FieldForField` (~line 536) to also cover
`PrincipalKeyProperties`:

```csharp
    [Fact]
    public void ApplyRelationships_MapsConfigsToModels_FieldForField()
    {
        var configs = new List<RelationshipConfig>
        {
            new("Customer", "Order", RelationshipKind.OneToMany,
                PrincipalNavigation: "Orders", DependentNavigation: "Customer",
                ForeignKeyProperties: new List<string> { "CustomerCode" },
                OnDeleteBehavior: "Cascade",
                PrincipalKeyProperties: new List<string> { "Code" }),
        };

        var result = ModelMerger.ApplyRelationships(configs);

        var relationship = Assert.Single(result);
        Assert.Equal("Customer", relationship.PrincipalEntity);
        Assert.Equal("Order", relationship.DependentEntity);
        Assert.Equal(RelationshipKind.OneToMany, relationship.Kind);
        Assert.Equal("Orders", relationship.PrincipalNavigation);
        Assert.Equal("Customer", relationship.DependentNavigation);
        Assert.Equal(new[] { "CustomerCode" }, relationship.ForeignKeyProperties);
        Assert.Equal("Cascade", relationship.OnDeleteBehavior);
        Assert.Equal(new[] { "Code" }, relationship.PrincipalKeyProperties);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter "FullyQualifiedName~ApplyRelationships_MapsConfigsToModels_FieldForField"`
Expected: FAIL — `relationship.PrincipalKeyProperties` is empty (merger doesn't pass it through yet).

- [ ] **Step 3: Implement**

In `src/EfSchemaVisualizer.Core/Merging/ModelMerger.cs`, update `ApplyRelationships` (~line 376):

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
                PrincipalKeyProperties: c.PrincipalKeyProperties))
            .ToList();
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter "FullyQualifiedName~ModelMergerTests"`
Expected: PASS, all tests in the file green.

- [ ] **Step 5: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Merging/ModelMerger.cs \
        tests/EfSchemaVisualizer.Core.Tests/Merging/ModelMergerTests.cs
git commit -m "Pass PrincipalKeyProperties through ModelMerger.ApplyRelationships"
```

---

### Task 4: Model-validity check for stale principal-key references

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/Validation/ModelValidityChecker.cs`
- Test: `tests/EfSchemaVisualizer.Web.Tests/DiagramModelBuilderValidityTests.cs`

**Interfaces:**
- Consumes: `RelationshipModel.PrincipalKeyProperties` (Task 1/3), `DiagnosticCodes.PrincipalKeyReferencesMissingProperty` (Task 1).
- Produces: `ModelValidityChecker.Check` now emits `PrincipalKeyReferencesMissingProperty` when a relationship's `PrincipalKeyProperties` names a property absent from the principal entity.

- [ ] **Step 1: Write the failing tests**

Add to `tests/EfSchemaVisualizer.Web.Tests/DiagramModelBuilderValidityTests.cs`, near the existing `Build_IndexReferencesRemovedProperty_EmitsDiagnostic` test (~line 303):

```csharp
    [Fact]
    public void Build_PrincipalKeyReferencesRemovedProperty_EmitsDiagnostic()
    {
        const string classSource = """
            public class Customer
            {
                public int Id { get; set; }
                public string Email { get; set; }
                public ICollection<Order> Orders { get; set; }
            }

            public class Order
            {
                public int Id { get; set; }
                public string CustomerCode { get; set; }
                public Customer Customer { get; set; }
            }
            """;

        const string configSource = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Order>(entity =>
                    {
                        entity.HasOne(o => o.Customer)
                            .WithMany(c => c.Orders)
                            .HasForeignKey(o => o.CustomerCode)
                            .HasPrincipalKey(c => c.Code);
                    });
                }
            }
            """;

        var result = DiagramModelBuilder.Build(classSource, configSource);

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.PrincipalKeyReferencesMissingProperty, diagnostic.Code);
    }

    [Fact]
    public void Build_PrincipalKeyReferencesExistingNonKeyProperty_NoDiagnostic()
    {
        const string classSource = """
            public class Customer
            {
                public int Id { get; set; }
                public string Code { get; set; }
                public ICollection<Order> Orders { get; set; }
            }

            public class Order
            {
                public int Id { get; set; }
                public string CustomerCode { get; set; }
                public Customer Customer { get; set; }
            }
            """;

        const string configSource = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Order>(entity =>
                    {
                        entity.HasOne(o => o.Customer)
                            .WithMany(c => c.Orders)
                            .HasForeignKey(o => o.CustomerCode)
                            .HasPrincipalKey(c => c.Code);
                    });
                }
            }
            """;

        var result = DiagramModelBuilder.Build(classSource, configSource);

        Assert.Empty(result.Diagnostics);
    }
```

Note the second test deliberately does **not** declare `HasAlternateKey(c => c.Code)` on `Customer` — proving the check does not require `Code` to already be a declared key, only that the property exists (see the design doc's "note on validation scope").

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/EfSchemaVisualizer.Web.Tests --filter "FullyQualifiedName~Build_PrincipalKeyReferences"`
Expected: FAIL — `Build_PrincipalKeyReferencesRemovedProperty_EmitsDiagnostic` fails because no diagnostic is emitted yet (`result.Diagnostics` is empty, not a single `PrincipalKeyReferencesMissingProperty`).

- [ ] **Step 3: Implement the check**

In `src/EfSchemaVisualizer.Core/Validation/ModelValidityChecker.cs`, add the call inside the existing relationship loop in `Check` (~line 52-55):

```csharp
        foreach (var relationship in relationships)
        {
            CheckForeignKeyTargetsKeylessPrincipal(relationship, entitiesByName, diagnostics);
            CheckPrincipalKeyReferencesMissingProperty(relationship, entitiesByName, diagnostics);
        }
```

Add the new method after `CheckForeignKeyTargetsKeylessPrincipal` (after line 201, before the closing class brace):

```csharp
    /// `HasPrincipalKey` naming a property that no longer exists on the principal entity —
    /// typically left behind after the property was renamed or removed. Deliberately does
    /// NOT check whether the named properties already form a declared key (PK or
    /// `HasAlternateKey`): calling `HasPrincipalKey` on properties that aren't already a key
    /// implicitly creates the alternate key for you at EF model-build time, so that shape is
    /// valid, common usage — only a missing property is a genuine build-time failure.
    private static void CheckPrincipalKeyReferencesMissingProperty(
        RelationshipModel relationship,
        Dictionary<string, EntityModel> entitiesByName,
        List<Diagnostic> diagnostics)
    {
        if (relationship.PrincipalKeyProperties.Count == 0
            || relationship.Kind is RelationshipKind.Inheritance or RelationshipKind.Owned)
        {
            return;
        }

        if (!entitiesByName.TryGetValue(relationship.PrincipalEntity, out var principal))
        {
            return;
        }

        var propertyNames = principal.Properties.Select(p => p.Name).ToHashSet();
        var missing = relationship.PrincipalKeyProperties.Where(name => !propertyNames.Contains(name)).ToList();
        if (missing.Count == 0)
        {
            return;
        }

        var missingList = string.Join(", ", missing);
        diagnostics.Add(new Diagnostic(
            DiagnosticCodes.PrincipalKeyReferencesMissingProperty,
            $"Relationship from '{relationship.DependentEntity}' references principal key propert{(missing.Count == 1 ? "y" : "ies")} '{missingList}' on '{relationship.PrincipalEntity}', which no longer exist on the entity.",
            relationship.DependentEntity,
            PropertyName: null,
            TextSpan.FromBounds(0, 0),
            DiagnosticCategory.ModelValidity));
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/EfSchemaVisualizer.Web.Tests --filter "FullyQualifiedName~Build_PrincipalKeyReferences"`
Expected: PASS (2 tests).

- [ ] **Step 5: Run the full validity test file to check for regressions**

Run: `dotnet test tests/EfSchemaVisualizer.Web.Tests --filter "FullyQualifiedName~DiagramModelBuilderValidityTests"`
Expected: PASS, all existing checks (index, keyless-principal, etc.) still green.

- [ ] **Step 6: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Validation/ModelValidityChecker.cs \
        tests/EfSchemaVisualizer.Web.Tests/DiagramModelBuilderValidityTests.cs
git commit -m "Flag HasPrincipalKey referencing a removed principal property"
```

---

### Task 5: Rewrite `HasPrincipalKey`

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs`

**Interfaces:**
- Consumes: `RelationshipModel.PrincipalKeyProperties` (Task 1).
- Produces: `OnModelCreatingRewriter.SetRelationship` now emits a `.HasPrincipalKey(...)` call when `PrincipalKeyProperties` is non-empty, for both `OneToOne` (generic `HasPrincipalKey<TPrincipal>`) and `OneToMany` (non-generic) relationships.

- [ ] **Step 1: Write the failing tests**

Add to `tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs`, near the existing `SetRelationship_OneToMany_WithForeignKey_EmitsHasForeignKey` test (~line 2604):

```csharp
    [Fact]
    public void SetRelationship_OneToMany_WithPrincipalKey_EmitsHasPrincipalKey()
    {
        var relationship = new RelationshipModel(
            "Blog", "Post", RelationshipKind.OneToMany, null, null,
            ForeignKeyProperties: new List<string> { "BlogCode" },
            PrincipalKeyProperties: new List<string> { "Code" });

        var result = new OnModelCreatingRewriter()
            .SetRelationship(SourceWithNoRelationshipConfig, relationship);

        Assert.Contains("entity.HasOne<Blog>().WithMany().HasForeignKey(d => d.BlogCode).HasPrincipalKey(p => p.Code)", result);
    }

    [Fact]
    public void SetRelationship_NoPrincipalKey_OmitsHasPrincipalKeyCall()
    {
        var relationship = new RelationshipModel(
            "Blog", "Post", RelationshipKind.OneToMany, null, null,
            ForeignKeyProperties: new List<string> { "BlogId" });

        var result = new OnModelCreatingRewriter()
            .SetRelationship(SourceWithNoRelationshipConfig, relationship);

        Assert.DoesNotContain("HasPrincipalKey", result);
    }

    [Fact]
    public void SetRelationship_OneToMany_WithCompositePrincipalKey_EmitsAnonymousObject()
    {
        var relationship = new RelationshipModel(
            "Blog", "Post", RelationshipKind.OneToMany, null, null,
            ForeignKeyProperties: new List<string> { "BlogCode", "BlogTenant" },
            PrincipalKeyProperties: new List<string> { "Code", "Tenant" });

        var result = new OnModelCreatingRewriter()
            .SetRelationship(SourceWithNoRelationshipConfig, relationship);

        Assert.Contains("HasPrincipalKey(p => new { p.Code, p.Tenant })", result);
    }

    [Fact]
    public void SetRelationship_OneToOne_EmitsGenericHasPrincipalKey()
    {
        var relationship = new RelationshipModel(
            "Blog", "Post", RelationshipKind.OneToOne, null, null,
            ForeignKeyProperties: new List<string> { "BlogCode" },
            PrincipalKeyProperties: new List<string> { "Code" });

        var result = new OnModelCreatingRewriter()
            .SetRelationship(SourceWithNoRelationshipConfig, relationship);

        Assert.Contains("entity.HasOne<Blog>().WithOne().HasForeignKey<Post>(d => d.BlogCode).HasPrincipalKey<Blog>(p => p.Code)", result);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter "FullyQualifiedName~SetRelationship_OneToMany_WithPrincipalKey|FullyQualifiedName~SetRelationship_NoPrincipalKey|FullyQualifiedName~SetRelationship_OneToMany_WithCompositePrincipalKey|FullyQualifiedName~SetRelationship_OneToOne_EmitsGenericHasPrincipalKey"`
Expected: FAIL — no `HasPrincipalKey` call is emitted yet, so the `Assert.Contains` checks fail.

- [ ] **Step 3: Implement `AppendHasPrincipalKey` and wire it in**

In `src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs`, add a new method right after `AppendHasForeignKey` (after line 2001, before `AppendOnDelete`):

```csharp
    private static ExpressionSyntax AppendHasPrincipalKey(ExpressionSyntax chain, IReadOnlyList<string> principalKeyProperties, string? principalGeneric)
    {
        if (principalKeyProperties.Count == 0)
        {
            return chain;
        }

        SimpleNameSyntax methodIdentifier = principalGeneric is null
            ? SyntaxFactory.IdentifierName("HasPrincipalKey")
            : SyntaxFactory.GenericName(SyntaxFactory.Identifier("HasPrincipalKey"))
                .WithTypeArgumentList(SyntaxFactory.TypeArgumentList(
                    SyntaxFactory.SingletonSeparatedList<TypeSyntax>(SyntaxFactory.IdentifierName(principalGeneric))));

        const string lambdaParam = "p";
        ExpressionSyntax body = principalKeyProperties.Count == 1
            ? SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                SyntaxFactory.IdentifierName(lambdaParam),
                SyntaxFactory.IdentifierName(principalKeyProperties[0]))
            : SyntaxFactory.AnonymousObjectCreationExpression(
                SyntaxFactory.SeparatedList(principalKeyProperties.Select(name =>
                    SyntaxFactory.AnonymousObjectMemberDeclarator(
                        SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            SyntaxFactory.IdentifierName(lambdaParam),
                            SyntaxFactory.IdentifierName(name))))));

        var argumentList = SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(
            SyntaxFactory.Argument(
                SyntaxFactory.SimpleLambdaExpression(SyntaxFactory.Parameter(SyntaxFactory.Identifier(lambdaParam)), body))));

        return SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, chain, methodIdentifier),
            argumentList);
    }
```

Update `BuildRelationshipStatement` (lines 1924-1940) to call it right after `AppendHasForeignKey` in both branches:

```csharp
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
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter "FullyQualifiedName~SetRelationship_OneToMany_WithPrincipalKey|FullyQualifiedName~SetRelationship_NoPrincipalKey|FullyQualifiedName~SetRelationship_OneToMany_WithCompositePrincipalKey|FullyQualifiedName~SetRelationship_OneToOne_EmitsGenericHasPrincipalKey"`
Expected: PASS (4 tests).

- [ ] **Step 5: Run the full rewriter test file to check for regressions**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter "FullyQualifiedName~OnModelCreatingRewriterTests"`
Expected: PASS, all existing `SetRelationship`/`HasForeignKey`/`HasConstraintName` tests still green.

- [ ] **Step 6: Commit**

```bash
git add src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs \
        tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs
git commit -m "Emit HasPrincipalKey when rewriting relationships"
```

---

### Task 6: `DiagramEditor.SetRelationshipShape` support

**Files:**
- Modify: `src/EfSchemaVisualizer.Web/Diagram/DiagramEditor.cs`
- Test: `tests/EfSchemaVisualizer.Web.Tests/Diagram/DiagramEditorPropertyPanelTests.cs`

**Interfaces:**
- Consumes: `RelationshipModel.PrincipalKeyProperties` (Task 1), `OnModelCreatingRewriter.SetRelationship`/`RemoveRelationship` (existing, now emits `HasPrincipalKey` per Task 5).
- Produces: `DiagramEditor.SetRelationshipShape(RelationshipModel relationship, RelationshipKind newKind, IReadOnlyList<string> newForeignKeyProperties, string? newOnDeleteBehavior, string? newConstraintName = null, IReadOnlyList<string>? newPrincipalKeyProperties = null) -> DiagramEditResult`.

- [ ] **Step 1: Write the failing tests**

Add to `tests/EfSchemaVisualizer.Web.Tests/Diagram/DiagramEditorPropertyPanelTests.cs`, near the existing `SetRelationshipShape_SettingConstraintName_WritesHasConstraintNameCall` test (~line 348). First, add a new fixture pair next to `RelationshipClassSource`/`RelationshipConfigSource` (~line 296):

```csharp
    private const string PrincipalKeyClassSource = """
        public class Blog
        {
            public int Id { get; set; }
            public string Code { get; set; } = "";
            public ICollection<Post> Posts { get; set; } = new List<Post>();
        }

        public class Post
        {
            public int Id { get; set; }
            public string BlogCode { get; set; } = "";
            public Blog Blog { get; set; } = null!;
        }
        """;

    private const string PrincipalKeyConfigSource = """
        modelBuilder.Entity<Post>(entity =>
        {
            entity.HasOne(p => p.Blog)
                .WithMany(b => b.Posts)
                .HasForeignKey(p => p.BlogCode);
        });
        """;
```

Then add the tests:

```csharp
    [Fact]
    public void SetRelationshipShape_SettingPrincipalKeyProperties_WritesHasPrincipalKeyCall()
    {
        var editor = new DiagramEditor(PrincipalKeyClassSource, PrincipalKeyConfigSource);
        var relationship = editor.Current.Relationships.Single();

        var result = editor.SetRelationshipShape(
            relationship, relationship.Kind, relationship.ForeignKeyProperties, relationship.OnDeleteBehavior,
            newConstraintName: null, newPrincipalKeyProperties: new List<string> { "Code" });

        Assert.True(result.Success);
        Assert.Equal(new[] { "Code" }, editor.Current.Relationships.Single().PrincipalKeyProperties);
        Assert.Contains("HasPrincipalKey(p => p.Code)", editor.ConfigSource);
    }

    [Fact]
    public void SetRelationshipShape_SamePrincipalKeyProperties_IsNoOp()
    {
        var editor = new DiagramEditor(PrincipalKeyClassSource, PrincipalKeyConfigSource);
        var relationship = editor.Current.Relationships.Single();
        var configSourceBefore = editor.ConfigSource;

        var result = editor.SetRelationshipShape(
            relationship, relationship.Kind, relationship.ForeignKeyProperties, relationship.OnDeleteBehavior,
            relationship.ConstraintName, relationship.PrincipalKeyProperties);

        Assert.True(result.Success);
        Assert.Equal(configSourceBefore, editor.ConfigSource);
    }

    [Fact]
    public void SetRelationshipShape_UnknownPrincipalKeyProperty_Fails()
    {
        var editor = new DiagramEditor(PrincipalKeyClassSource, PrincipalKeyConfigSource);
        var relationship = editor.Current.Relationships.Single();

        var result = editor.SetRelationshipShape(
            relationship, relationship.Kind, relationship.ForeignKeyProperties, relationship.OnDeleteBehavior,
            newConstraintName: null, newPrincipalKeyProperties: new List<string> { "DoesNotExist" });

        Assert.False(result.Success);
        Assert.Empty(editor.Current.Relationships.Single().PrincipalKeyProperties);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/EfSchemaVisualizer.Web.Tests --filter "FullyQualifiedName~SetRelationshipShape_SettingPrincipalKeyProperties|FullyQualifiedName~SetRelationshipShape_SamePrincipalKeyProperties|FullyQualifiedName~SetRelationshipShape_UnknownPrincipalKeyProperty"`
Expected: FAIL — compile error (`SetRelationshipShape` has no `newPrincipalKeyProperties` parameter yet).

- [ ] **Step 3: Implement**

In `src/EfSchemaVisualizer.Web/Diagram/DiagramEditor.cs`, update `SetRelationshipShape` (~line 1366):

```csharp
    public DiagramEditResult SetRelationshipShape(
        RelationshipModel relationship,
        RelationshipKind newKind,
        IReadOnlyList<string> newForeignKeyProperties,
        string? newOnDeleteBehavior,
        string? newConstraintName = null,
        IReadOnlyList<string>? newPrincipalKeyProperties = null)
    {
        var principalKeyProperties = newPrincipalKeyProperties ?? Array.Empty<string>();

        if (!Current.Relationships.Contains(relationship))
        {
            return DiagramEditResult.Fail("Relationship no longer exists.");
        }

        if (newKind == relationship.Kind
            && newForeignKeyProperties.SequenceEqual(relationship.ForeignKeyProperties)
            && newOnDeleteBehavior == relationship.OnDeleteBehavior
            && newConstraintName == relationship.ConstraintName
            && principalKeyProperties.SequenceEqual(relationship.PrincipalKeyProperties))
        {
            return DiagramEditResult.Ok();
        }

        if (newKind == RelationshipKind.ManyToMany && (newForeignKeyProperties.Count > 0 || principalKeyProperties.Count > 0))
        {
            return DiagramEditResult.Fail("Many-to-many relationships cannot have a foreign key.");
        }

        var dependent = Current.Entities.First(e => e.Name == relationship.DependentEntity);
        var missingProperty = newForeignKeyProperties.FirstOrDefault(name => !dependent.Properties.Any(p => p.Name == name));
        if (missingProperty is not null)
        {
            return DiagramEditResult.Fail($"'{missingProperty}' is not a property of '{relationship.DependentEntity}'.");
        }

        var principal = Current.Entities.First(e => e.Name == relationship.PrincipalEntity);
        var missingPrincipalProperty = principalKeyProperties.FirstOrDefault(name => !principal.Properties.Any(p => p.Name == name));
        if (missingPrincipalProperty is not null)
        {
            return DiagramEditResult.Fail($"'{missingPrincipalProperty}' is not a property of '{relationship.PrincipalEntity}'.");
        }

        var updated = relationship with
        {
            Kind = newKind,
            ForeignKeyProperties = newForeignKeyProperties,
            OnDeleteBehavior = newOnDeleteBehavior,
            ConstraintName = newConstraintName,
            PrincipalKeyProperties = principalKeyProperties,
        };

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
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/EfSchemaVisualizer.Web.Tests --filter "FullyQualifiedName~SetRelationshipShape_SettingPrincipalKeyProperties|FullyQualifiedName~SetRelationshipShape_SamePrincipalKeyProperties|FullyQualifiedName~SetRelationshipShape_UnknownPrincipalKeyProperty"`
Expected: PASS (3 tests).

- [ ] **Step 5: Run the full DiagramEditor test file to check for regressions**

Run: `dotnet test tests/EfSchemaVisualizer.Web.Tests --filter "FullyQualifiedName~DiagramEditorPropertyPanelTests"`
Expected: PASS, all existing `SetRelationshipShape`/`AddRelationship`/`RemoveRelationship` tests still green (they all call with the old 4-5 positional args, which still compile thanks to the new trailing optional parameter).

- [ ] **Step 6: Commit**

```bash
git add src/EfSchemaVisualizer.Web/Diagram/DiagramEditor.cs \
        tests/EfSchemaVisualizer.Web.Tests/Diagram/DiagramEditorPropertyPanelTests.cs
git commit -m "Support editing HasPrincipalKey via DiagramEditor.SetRelationshipShape"
```

---

### Task 7: UI — Principal key checkboxes in `RelationshipLinkLabel.razor`

**Files:**
- Modify: `src/EfSchemaVisualizer.Web/Diagram/RelationshipLinkLabel.razor`

**Interfaces:**
- Consumes: `DiagramEditor.SetRelationshipShape(..., newPrincipalKeyProperties)` (Task 6), `RelationshipModel.PrincipalKeyProperties` (Task 1).
- Produces: no new public interface — this is the terminal UI consumer.

This task has no automated test (the codebase's Blazor components aren't unit-tested elsewhere in this file); it's verified manually per Step 3 below, consistent with how the rest of this component was built.

- [ ] **Step 1: Add the "Principal key" markup block**

In `src/EfSchemaVisualizer.Web/Diagram/RelationshipLinkLabel.razor`, insert a new block after the existing "Foreign key" `<div>` (after line 54) and before the "On delete" `<label>` (line 55):

```razor
                <div style="display: block;">
                    Principal key:
                    @foreach (var property in PrincipalProperties)
                    {
                        <label style="display: block;">
                            <input type="checkbox" checked="@_principalKeyProperties.Contains(property.Name)"
                                   @onchange="e => TogglePrincipalKeyProperty(property.Name, (bool)(e.Value ?? false))"
                                   @onpointerdown:stopPropagation="true"
                                   @onmousedown:stopPropagation="true" />
                            @property.Name
                        </label>
                    }
                </div>
```

- [ ] **Step 2: Add supporting state, computed property, and methods**

In the `@code` block, add a computed property next to `DependentProperties` (~line 100):

```csharp
    private IEnumerable<PropertyModel> PrincipalProperties =>
        EditContext.Editor.Current.Entities.FirstOrDefault(e => e.Name == Label.Relationship.PrincipalEntity)?.Properties
            ?? Enumerable.Empty<PropertyModel>();
```

Add a new field next to `_foreignKeyProperties` (~line 95):

```csharp
    private List<string> _principalKeyProperties = new();
```

Update `ToggleExpand` (~line 119) to seed it:

```csharp
    private void ToggleExpand()
    {
        _expanded = !_expanded;
        if (_expanded)
        {
            _kind = Label.Relationship.Kind;
            _foreignKeyProperties = Label.Relationship.ForeignKeyProperties.ToList();
            _principalKeyProperties = Label.Relationship.PrincipalKeyProperties.ToList();
            _onDeleteBehavior = Label.Relationship.OnDeleteBehavior;
            _constraintName = Label.Relationship.ConstraintName;
            _error = null;
        }
    }
```

Add a new toggle method next to `ToggleForeignKeyProperty` (~line 143):

```csharp
    private async Task TogglePrincipalKeyProperty(string propertyName, bool include)
    {
        if (include)
        {
            if (!_principalKeyProperties.Contains(propertyName))
            {
                _principalKeyProperties.Add(propertyName);
            }
        }
        else
        {
            _principalKeyProperties.Remove(propertyName);
        }

        await Commit();
    }
```

Update `Commit` (~line 172) to compute and pass the new value:

```csharp
    private async Task Commit()
    {
        var foreignKeyProperties = _kind == RelationshipKind.ManyToMany
            ? Array.Empty<string>()
            : _foreignKeyProperties.ToArray();
        var principalKeyProperties = _kind == RelationshipKind.ManyToMany
            ? Array.Empty<string>()
            : _principalKeyProperties.ToArray();
        var onDeleteBehavior = _kind == RelationshipKind.ManyToMany ? null : _onDeleteBehavior;
        var constraintName = _kind == RelationshipKind.ManyToMany ? null : _constraintName;

        var result = SafeEdit(() => EditContext.Editor.SetRelationshipShape(
            Label.Relationship, _kind, foreignKeyProperties, onDeleteBehavior, constraintName, principalKeyProperties));
        if (result.Success)
        {
            _error = null;
            await EditContext.NotifyChangedAsync();
        }
        else
        {
            _error = result.Error;
        }
    }
```

- [ ] **Step 3: Manually verify in the running app**

Run: `dotnet run --project src/EfSchemaVisualizer.Web`
Open the app, upload or paste a sample with a one-to-many relationship (e.g. `Blog`/`Post` with a `Code` property on `Blog`), click the relationship label to expand it, and confirm:
- A "Principal key" checkbox list appears listing `Blog`'s properties.
- Checking `Code` adds `.HasPrincipalKey(p => p.Code)` to the downloaded/regenerated config (visible via the app's download or diff view).
- Toggling it off removes the call again.
- The block disappears when the relationship kind is switched to many-to-many.

- [ ] **Step 4: Build the whole solution to catch any Razor compile errors**

Run: `dotnet build src/EfSchemaVisualizer.Web/EfSchemaVisualizer.Web.csproj`
Expected: Build succeeds.

- [ ] **Step 5: Commit**

```bash
git add src/EfSchemaVisualizer.Web/Diagram/RelationshipLinkLabel.razor
git commit -m "Add principal key editor to relationship panel UI"
```

---

### Task 8: Fuzz corpus coverage and docs

**Files:**
- Modify: `tests/EfSchemaVisualizer.Core.Tests/RoundTripFuzzTests.cs`
- Modify: `README.md`
- Modify: `docs/backlog.md`

**Interfaces:**
- Consumes: everything from Tasks 1-5 (parse/rewrite round-trip).
- Produces: nothing new — this task extends existing regression coverage and closes out documentation.

- [ ] **Step 1: Add `HasPrincipalKey` to the fuzz corpus**

In `tests/EfSchemaVisualizer.Core.Tests/RoundTripFuzzTests.cs`, update the `ConfigSource`'s `Post` entity block (line 64) to add a `HasPrincipalKey` call targeting `Blog.Url` — which is already declared as an alternate key on line 56 (`entity.HasAlternateKey(e => e.Url);`):

```csharp
                    entity.HasOne(e => e.Blog).WithMany(b => b.Posts).HasForeignKey(e => e.BlogId).HasPrincipalKey(e => e.Url);
```

Update the corresponding assertion in `EditingOnePropertyPreservesEverythingElseVerbatim_IncludingUnsupportedConstructs` (line 304) to match:

```csharp
        Assert.Contains("entity.HasOne(e => e.Blog).WithMany(b => b.Posts).HasForeignKey(e => e.BlogId).HasPrincipalKey(e => e.Url);", renamedConfigSource);
```

- [ ] **Step 2: Run the fuzz test file to verify it passes**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter "FullyQualifiedName~RoundTripFuzzTests"`
Expected: PASS — the added `HasPrincipalKey` call is preserved verbatim by the rename-elsewhere test since it's on the untouched `Post` entity's relationship line, unaffected by the `Post.Title` rename that test performs.

- [ ] **Step 3: Update README**

In `README.md`, remove the `HasPrincipalKey` bullet from the "Unsupported EF Core features" list (line 91):

```diff
 - `HasDefaultValueSql` (only literal `HasDefaultValue` is read).
-- `HasPrincipalKey`.
 - `UsingEntity`'s nested join-entity configuration (the join entity itself is
```

- [ ] **Step 4: Update backlog.md**

In `docs/backlog.md`, change the `HasPrincipalKey` item (line 620) from `- [ ]` to `- [x]` and append an `**Update:**` note, following the exact style of the `Value converters and enums` entry immediately above it (which reads `— Fixed 2026-07-29. See ...`):

```diff
-- [ ] **`[found]` `HasPrincipalKey`.** Already noted as unsupported in the README;
-      relevant now that alternate keys are parsed, since a relationship can
-      legitimately target one.
+- [x] **`[found]` `HasPrincipalKey`.** Already noted as unsupported in the README;
+      relevant now that alternate keys are parsed, since a relationship can
+      legitimately target one.
+      — Fixed 2026-07-30. See
+      `docs/superpowers/specs/2026-07-30-has-principal-key-design.md`.
+      `HasPrincipalKey(...)` is now fully parsed (`FluentConfigParser.ParseRelationships`,
+      new `RelationshipConfig`/`RelationshipModel.PrincipalKeyProperties` field),
+      merged (`ModelMerger.ApplyRelationships`), rewritten
+      (`OnModelCreatingRewriter.AppendHasPrincipalKey`), and editable via
+      `DiagramEditor.SetRelationshipShape`'s new `newPrincipalKeyProperties`
+      parameter, with a matching "Principal key" checkbox list in
+      `RelationshipLinkLabel.razor`. New
+      `ModelValidityChecker.CheckPrincipalKeyReferencesMissingProperty` flags a
+      `HasPrincipalKey` property that no longer exists on the principal entity
+      (stale after rename/removal) — deliberately does not require the named
+      properties to already form a declared key, since EF implicitly creates
+      the alternate key itself when they don't.
```

- [ ] **Step 5: Run the full test suite**

Run: `dotnet test`
Expected: PASS — every test project green, no regressions across the whole solution.

- [ ] **Step 6: Commit**

```bash
git add tests/EfSchemaVisualizer.Core.Tests/RoundTripFuzzTests.cs README.md docs/backlog.md
git commit -m "Cover HasPrincipalKey in fuzz corpus; update README and backlog"
```

---

## Self-Review Notes

- **Spec coverage:** Model (Task 1), parse (Task 2), merge (Task 3), validity check (Task 4), rewrite (Task 5), `DiagramEditor` (Task 6), UI (Task 7), fuzz corpus + docs (Task 8) — every section of the design spec maps to exactly one task.
- **Type consistency:** `PrincipalKeyProperties: IReadOnlyList<string>` is spelled identically across `RelationshipConfig`, `RelationshipModel`, `ModelMerger`, `ModelValidityChecker`, `OnModelCreatingRewriter`, `DiagramEditor`, and the `.razor` file. `AppendHasPrincipalKey`'s signature (`chain, principalKeyProperties, principalGeneric`) matches its two call sites in `BuildRelationshipStatement` exactly.
- **Placeholder scan:** no TBD/TODO; every step has literal code, not descriptions of code.
