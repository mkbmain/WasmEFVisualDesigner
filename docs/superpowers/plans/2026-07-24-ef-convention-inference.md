# EF Convention Inference (Backlog W1) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Infer `Id`/`<Type>Id` primary keys and navigation+FK-property relationships for convention-only EF models (no fluent config, no attributes), render them visibly distinct from explicit config, and make editing them materialize explicit fluent config instead of failing.

**Architecture:** A new pure `EfSchemaVisualizer.Core.Inference.ConventionInference` static class computes inferred keys/relationships from the already-merged `EntityModel` list. It's invoked from `DiagramModelBuilder.Build` after all explicit config has been applied, so inference only fills gaps and is recomputed fresh on every parse — nothing it produces is persisted except through existing edit gestures. Two new record fields (`EntityModel.IsKeyInferred`, `RelationshipModel.IsInferred`) flow through to the Blazor rendering layer to style inferred items distinctly.

**Tech Stack:** C#/.NET, Roslyn (`Microsoft.CodeAnalysis`) for the existing parser this plan reuses helpers from, xUnit for tests, Blazor + Z.Blazor.Diagrams for the diagram UI.

## Global Constraints

- Every new record field defaults to `false` so no existing positional-constructor call site in the codebase or tests breaks.
- Inference must never override explicit config: `ConventionInference.InferKey` is a no-op whenever `KeyPropertyNames` is already non-empty or the entity is keyless; `ConventionInference.InferRelationships` results are dropped whenever an explicit relationship already claims the same `(DependentEntity, ForeignKeyProperties)`.
- No changes to the SVG/Mermaid exporters, owned-type/inheritance handling, or how navigation-typed properties render as columns — all explicitly out of scope per the design spec (`docs/superpowers/specs/2026-07-24-ef-convention-inference-design.md`).
- Run `dotnet test` from the repo root (`/root/RiderProjects/EfSchemaVisualizer`) after every task; the full suite must stay green throughout.

---

### Task 1: `ConventionInference.InferKey` — primary-key convention inference

**Files:**
- Create: `src/EfSchemaVisualizer.Core/Inference/ConventionInference.cs`
- Modify: `src/EfSchemaVisualizer.Core/Model/EntityModel.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Inference/ConventionInferenceTests.cs`

**Interfaces:**
- Consumes: `EntityModel` (`src/EfSchemaVisualizer.Core/Model/EntityModel.cs`), `PropertyModel` (`src/EfSchemaVisualizer.Core/Model/PropertyModel.cs`).
- Produces: `EfSchemaVisualizer.Core.Inference.ConventionInference.InferKey(EntityModel entity) -> EntityModel`, and `EntityModel.IsKeyInferred` (`bool`, default `false`) for Task 3 and the rendering tasks to consume.

- [ ] **Step 1: Add `IsKeyInferred` to `EntityModel`**

Edit `src/EfSchemaVisualizer.Core/Model/EntityModel.cs` — add the new parameter right after `IsKeyless` (same boolean-flag grouping) and keep every other parameter and its position unchanged:

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
    bool IsKeyInferred = false,
    string? ViewName = null,
    string? SqlQuery = null,
    IReadOnlyList<IReadOnlyList<string>>? AlternateKeys = null,
    bool HasQueryFilter = false,
    string? Comment = null,
    bool IsJson = false,
    string? JsonColumnName = null,
    bool IsTemporal = false,
    IReadOnlyList<string>? SplitTables = null)
{
    public IReadOnlyList<string> KeyPropertyNames { get; init; } = KeyPropertyNames ?? new List<string>();
    public IReadOnlyList<IndexModel> Indexes { get; init; } = Indexes ?? new List<IndexModel>();
    public IReadOnlyList<IReadOnlyList<string>> AlternateKeys { get; init; } = AlternateKeys ?? new List<IReadOnlyList<string>>();
    public IReadOnlyList<string> SplitTables { get; init; } = SplitTables ?? new List<string>();
}
```

Note: this inserts a new positional parameter in the middle of the parameter list. Search the codebase for any positional (non-named-argument) `new EntityModel(...)` call that passes more than 6 positional arguments, which would now bind to the wrong parameter:

```bash
grep -rn "new EntityModel(" src tests --include=*.cs
```

Every call site found either uses named arguments past `IsKeyless`/`KeyPropertyNames` or passes 6 or fewer positional arguments (verified against the current codebase as of this plan). If a call site is found that passes more than 6 positional args with no names, convert its trailing arguments to named arguments before proceeding — do not reorder the record's own parameter list to avoid this, since `IsKeyInferred` belongs next to `IsKeyless` for readability.

- [ ] **Step 2: Write the failing tests**

Create `tests/EfSchemaVisualizer.Core.Tests/Inference/ConventionInferenceTests.cs`:

```csharp
using System.Collections.Generic;
using System.Linq;
using EfSchemaVisualizer.Core.Inference;
using EfSchemaVisualizer.Core.Model;
using Xunit;

namespace EfSchemaVisualizer.Core.Tests.Inference;

public class ConventionInferenceTests
{
    private static PropertyModel Property(string name, string clrType, bool isNullable = false) =>
        new(name, clrType, isNullable, MaxLength: null);

    [Fact]
    public void InferKey_PropertyNamedId_InfersItAsKey()
    {
        var entity = new EntityModel("Customer", new[] { Property("Id", "int"), Property("Name", "string") });

        var result = ConventionInference.InferKey(entity);

        Assert.Equal(new[] { "Id" }, result.KeyPropertyNames);
        Assert.True(result.IsKeyInferred);
    }

    [Fact]
    public void InferKey_PropertyNamedIdCaseInsensitive_InfersItAsKey()
    {
        var entity = new EntityModel("Customer", new[] { Property("ID", "int") });

        var result = ConventionInference.InferKey(entity);

        Assert.Equal(new[] { "ID" }, result.KeyPropertyNames);
        Assert.True(result.IsKeyInferred);
    }

    [Fact]
    public void InferKey_NoIdButTypeNameIdPresent_InfersTypeNameIdAsKey()
    {
        var entity = new EntityModel("Customer", new[] { Property("CustomerId", "int"), Property("Name", "string") });

        var result = ConventionInference.InferKey(entity);

        Assert.Equal(new[] { "CustomerId" }, result.KeyPropertyNames);
        Assert.True(result.IsKeyInferred);
    }

    [Fact]
    public void InferKey_BothIdAndTypeNameIdPresent_IdWins()
    {
        var entity = new EntityModel("Customer", new[] { Property("Id", "int"), Property("CustomerId", "int") });

        var result = ConventionInference.InferKey(entity);

        Assert.Equal(new[] { "Id" }, result.KeyPropertyNames);
    }

    [Fact]
    public void InferKey_NeitherPatternMatches_NoKeyInferred()
    {
        var entity = new EntityModel("Customer", new[] { Property("Name", "string") });

        var result = ConventionInference.InferKey(entity);

        Assert.Empty(result.KeyPropertyNames);
        Assert.False(result.IsKeyInferred);
    }

    [Fact]
    public void InferKey_ExplicitKeyAlreadyPresent_LeavesEntityUnchanged()
    {
        var entity = new EntityModel(
            "Customer",
            new[] { Property("Id", "int"), Property("Ssn", "string") },
            KeyPropertyNames: new[] { "Ssn" });

        var result = ConventionInference.InferKey(entity);

        Assert.Equal(new[] { "Ssn" }, result.KeyPropertyNames);
        Assert.False(result.IsKeyInferred);
    }

    [Fact]
    public void InferKey_EntityIsKeyless_LeavesEntityUnchanged()
    {
        var entity = new EntityModel("Customer", new[] { Property("Id", "int") }, IsKeyless: true);

        var result = ConventionInference.InferKey(entity);

        Assert.Empty(result.KeyPropertyNames);
        Assert.False(result.IsKeyInferred);
        Assert.True(result.IsKeyless);
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter FullyQualifiedName~ConventionInferenceTests`
Expected: build error (`ConventionInference` does not exist).

- [ ] **Step 4: Implement `ConventionInference.InferKey`**

Create `src/EfSchemaVisualizer.Core/Inference/ConventionInference.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using EfSchemaVisualizer.Core.Model;

namespace EfSchemaVisualizer.Core.Inference;

public static class ConventionInference
{
    public static EntityModel InferKey(EntityModel entity)
    {
        if (entity.KeyPropertyNames.Count > 0 || entity.IsKeyless)
        {
            return entity;
        }

        var idProperty = entity.Properties
            .FirstOrDefault(p => string.Equals(p.Name, "Id", StringComparison.OrdinalIgnoreCase));

        var typeIdName = entity.Name + "Id";
        var typeIdProperty = entity.Properties
            .FirstOrDefault(p => string.Equals(p.Name, typeIdName, StringComparison.OrdinalIgnoreCase));

        var keyProperty = idProperty ?? typeIdProperty;
        if (keyProperty is null)
        {
            return entity;
        }

        return entity with { KeyPropertyNames = new List<string> { keyProperty.Name }, IsKeyInferred = true };
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter FullyQualifiedName~ConventionInferenceTests`
Expected: PASS (7/7).

- [ ] **Step 6: Run the full test suite**

Run: `dotnet test`
Expected: PASS, no regressions.

- [ ] **Step 7: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Model/EntityModel.cs src/EfSchemaVisualizer.Core/Inference/ConventionInference.cs tests/EfSchemaVisualizer.Core.Tests/Inference/ConventionInferenceTests.cs
git commit -m "Add EF primary-key convention inference (Id / <Type>Id)"
```

---

### Task 2: `ConventionInference.InferRelationships` — nav+FK relationship inference

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/Model/RelationshipModel.cs`
- Modify: `src/EfSchemaVisualizer.Core/Inference/ConventionInference.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Inference/ConventionInferenceTests.cs`

**Interfaces:**
- Consumes: `EntityModel`, `PropertyModel`, `RelationshipKind` (`src/EfSchemaVisualizer.Core/Model/RelationshipKind.cs`), `FluentSyntaxHelpers.TryGetElementTypeName(string clrType) -> string?` (internal, `src/EfSchemaVisualizer.Core/Parsing/FluentSyntaxHelpers.cs:319`, same assembly so directly callable).
- Produces: `EfSchemaVisualizer.Core.Inference.ConventionInference.InferRelationships(IReadOnlyList<EntityModel> entities) -> IReadOnlyList<RelationshipModel>`, and `RelationshipModel.IsInferred` (`bool`, default `false`) for Task 3 and the rendering tasks to consume.

- [ ] **Step 1: Add `IsInferred` to `RelationshipModel`**

Edit `src/EfSchemaVisualizer.Core/Model/RelationshipModel.cs`, appending the new parameter at the end (after `JoinEntityName`) so every existing positional call site (verified: none in this codebase pass more than 5 positional args to this constructor) is unaffected:

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
    bool IsInferred = false)
{
    public IReadOnlyList<string> ForeignKeyProperties { get; init; } = ForeignKeyProperties ?? new List<string>();
}
```

- [ ] **Step 2: Write the failing tests**

Append to `tests/EfSchemaVisualizer.Core.Tests/Inference/ConventionInferenceTests.cs` (inside the `ConventionInferenceTests` class, add the `using EfSchemaVisualizer.Core.Model;` for `RelationshipKind` is already present via the existing `using` block):

```csharp
    private static EntityModel Entity(string name, params PropertyModel[] properties) =>
        new(name, properties);

    [Fact]
    public void InferRelationships_NavigationPlusMatchingNavNameIdProperty_InfersOneToMany()
    {
        var customer = Entity("Customer", Property("Id", "int"));
        var order = Entity("Order",
            Property("Id", "int"),
            Property("CustomerId", "int"),
            Property("Customer", "Customer"));

        var relationships = ConventionInference.InferRelationships(new[] { customer, order });

        var relationship = Assert.Single(relationships);
        Assert.Equal("Customer", relationship.PrincipalEntity);
        Assert.Equal("Order", relationship.DependentEntity);
        Assert.Equal(RelationshipKind.OneToMany, relationship.Kind);
        Assert.Null(relationship.PrincipalNavigation);
        Assert.Equal("Customer", relationship.DependentNavigation);
        Assert.Equal(new[] { "CustomerId" }, relationship.ForeignKeyProperties);
        Assert.True(relationship.IsInferred);
    }

    [Fact]
    public void InferRelationships_NavNamedDifferentlyFromType_FallsBackToPrincipalTypeNameId()
    {
        var customer = Entity("Customer", Property("Id", "int"));
        var order = Entity("Order",
            Property("Id", "int"),
            Property("CustomerId", "int"),
            Property("Buyer", "Customer"));

        var relationships = ConventionInference.InferRelationships(new[] { customer, order });

        var relationship = Assert.Single(relationships);
        Assert.Equal(new[] { "CustomerId" }, relationship.ForeignKeyProperties);
        Assert.Equal("Buyer", relationship.DependentNavigation);
    }

    [Fact]
    public void InferRelationships_PrincipalHasCollectionBackReference_ResolvesOneToManyWithPrincipalNavigation()
    {
        var customer = Entity("Customer", Property("Id", "int"), Property("Orders", "ICollection<Order>"));
        var order = Entity("Order", Property("Id", "int"), Property("CustomerId", "int"), Property("Customer", "Customer"));

        var relationships = ConventionInference.InferRelationships(new[] { customer, order });

        var relationship = Assert.Single(relationships);
        Assert.Equal(RelationshipKind.OneToMany, relationship.Kind);
        Assert.Equal("Orders", relationship.PrincipalNavigation);
    }

    [Fact]
    public void InferRelationships_PrincipalHasScalarBackReference_ResolvesOneToOne()
    {
        var customer = Entity("Customer", Property("Id", "int"), Property("FeaturedOrder", "Order"));
        var order = Entity("Order", Property("Id", "int"), Property("CustomerId", "int"), Property("Customer", "Customer"));

        var relationships = ConventionInference.InferRelationships(new[] { customer, order });

        var relationship = Assert.Single(relationships);
        Assert.Equal(RelationshipKind.OneToOne, relationship.Kind);
        Assert.Equal("FeaturedOrder", relationship.PrincipalNavigation);
    }

    [Fact]
    public void InferRelationships_SelfReferencingEntity_ResolvesWithoutMatchingItsOwnNavigationProperty()
    {
        var employee = Entity("Employee",
            Property("Id", "int"),
            Property("ManagerId", "int", isNullable: true),
            Property("Manager", "Employee", isNullable: true),
            Property("Reports", "ICollection<Employee>"));

        var relationships = ConventionInference.InferRelationships(new[] { employee });

        var relationship = Assert.Single(relationships);
        Assert.Equal("Employee", relationship.PrincipalEntity);
        Assert.Equal("Employee", relationship.DependentEntity);
        Assert.Equal(RelationshipKind.OneToMany, relationship.Kind);
        Assert.Equal("Reports", relationship.PrincipalNavigation);
        Assert.Equal("Manager", relationship.DependentNavigation);
    }

    [Fact]
    public void InferRelationships_NoMatchingForeignKeyProperty_InfersNothing()
    {
        var customer = Entity("Customer", Property("Id", "int"));
        var order = Entity("Order", Property("Id", "int"), Property("Customer", "Customer"));

        var relationships = ConventionInference.InferRelationships(new[] { customer, order });

        Assert.Empty(relationships);
    }

    [Fact]
    public void InferRelationships_NoNavigationProperty_InfersNothingEvenWithFkShapedProperty()
    {
        var customer = Entity("Customer", Property("Id", "int"));
        var order = Entity("Order", Property("Id", "int"), Property("CustomerId", "int"));

        var relationships = ConventionInference.InferRelationships(new[] { customer, order });

        Assert.Empty(relationships);
    }
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter FullyQualifiedName~ConventionInferenceTests`
Expected: build error (`InferRelationships` does not exist), or the new `[Fact]`s failing.

- [ ] **Step 4: Implement `ConventionInference.InferRelationships`**

Replace the full contents of `src/EfSchemaVisualizer.Core/Inference/ConventionInference.cs` with:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using EfSchemaVisualizer.Core.Model;
using EfSchemaVisualizer.Core.Parsing;

namespace EfSchemaVisualizer.Core.Inference;

public static class ConventionInference
{
    public static EntityModel InferKey(EntityModel entity)
    {
        if (entity.KeyPropertyNames.Count > 0 || entity.IsKeyless)
        {
            return entity;
        }

        var idProperty = entity.Properties
            .FirstOrDefault(p => string.Equals(p.Name, "Id", StringComparison.OrdinalIgnoreCase));

        var typeIdName = entity.Name + "Id";
        var typeIdProperty = entity.Properties
            .FirstOrDefault(p => string.Equals(p.Name, typeIdName, StringComparison.OrdinalIgnoreCase));

        var keyProperty = idProperty ?? typeIdProperty;
        if (keyProperty is null)
        {
            return entity;
        }

        return entity with { KeyPropertyNames = new List<string> { keyProperty.Name }, IsKeyInferred = true };
    }

    public static IReadOnlyList<RelationshipModel> InferRelationships(IReadOnlyList<EntityModel> entities)
    {
        var entitiesByName = entities.ToDictionary(e => e.Name);
        var results = new List<RelationshipModel>();

        foreach (var dependent in entities)
        {
            foreach (var property in dependent.Properties)
            {
                if (!entitiesByName.TryGetValue(property.ClrType, out var principal))
                {
                    continue;
                }

                var fkProperty = FindForeignKeyProperty(dependent, property.Name, principal.Name);
                if (fkProperty is null)
                {
                    continue;
                }

                var (kind, principalNavigation) = FindPrincipalBackReference(principal, dependent.Name, property);

                results.Add(new RelationshipModel(
                    principal.Name,
                    dependent.Name,
                    kind,
                    principalNavigation,
                    property.Name,
                    new List<string> { fkProperty.Name },
                    IsInferred: true));
            }
        }

        return results;
    }

    private static PropertyModel? FindForeignKeyProperty(
        EntityModel dependent, string navigationPropertyName, string principalTypeName)
    {
        var byNavName = dependent.Properties.FirstOrDefault(p =>
            string.Equals(p.Name, navigationPropertyName + "Id", StringComparison.OrdinalIgnoreCase));
        if (byNavName is not null)
        {
            return byNavName;
        }

        if (string.Equals(navigationPropertyName, principalTypeName, StringComparison.Ordinal))
        {
            return null;
        }

        return dependent.Properties.FirstOrDefault(p =>
            string.Equals(p.Name, principalTypeName + "Id", StringComparison.OrdinalIgnoreCase));
    }

    private static (RelationshipKind Kind, string? PrincipalNavigation) FindPrincipalBackReference(
        EntityModel principalEntity, string dependentEntityName, PropertyModel navigationProperty)
    {
        foreach (var property in principalEntity.Properties)
        {
            if (property == navigationProperty)
            {
                continue;
            }

            var elementTypeName = FluentSyntaxHelpers.TryGetElementTypeName(property.ClrType);

            if (elementTypeName != dependentEntityName)
            {
                continue;
            }

            var isCollection = elementTypeName != property.ClrType;
            return isCollection
                ? (RelationshipKind.OneToMany, property.Name)
                : (RelationshipKind.OneToOne, property.Name);
        }

        return (RelationshipKind.OneToMany, null);
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter FullyQualifiedName~ConventionInferenceTests`
Expected: PASS (14/14 total in the file).

- [ ] **Step 6: Run the full test suite**

Run: `dotnet test`
Expected: PASS, no regressions.

- [ ] **Step 7: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Model/RelationshipModel.cs src/EfSchemaVisualizer.Core/Inference/ConventionInference.cs tests/EfSchemaVisualizer.Core.Tests/Inference/ConventionInferenceTests.cs
git commit -m "Add EF relationship convention inference (navigation + FK property pairs)"
```

---

### Task 3: Wire inference into `DiagramModelBuilder`

**Files:**
- Modify: `src/EfSchemaVisualizer.Web/DiagramModelBuilder.cs`
- Test: `tests/EfSchemaVisualizer.Web.Tests/DiagramModelBuilderTests.cs`

**Interfaces:**
- Consumes: `ConventionInference.InferKey(EntityModel) -> EntityModel`, `ConventionInference.InferRelationships(IReadOnlyList<EntityModel>) -> IReadOnlyList<RelationshipModel>` (Tasks 1–2).
- Produces: `DiagramModelBuilder.Build(...)`'s returned `DiagramModelResult.Entities` now carry inferred keys where applicable, and `.Relationships` includes inferred relationships alongside explicit ones — this is what Tasks 5–6 (rendering) and any future caller read.

- [ ] **Step 1: Write the failing tests**

Find the existing test file `tests/EfSchemaVisualizer.Web.Tests/DiagramModelBuilderTests.cs` and append these `[Fact]`s inside the `DiagramModelBuilderTests` class:

```csharp
    [Fact]
    public void Build_ConventionOnlyModel_InfersKeysAndRelationship()
    {
        const string classSource = """
            public class Customer
            {
                public int Id { get; set; }
                public string Name { get; set; }
            }

            public class Order
            {
                public int Id { get; set; }
                public int CustomerId { get; set; }
                public Customer Customer { get; set; }
            }
            """;

        const string configSource = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                }
            }
            """;

        var result = DiagramModelBuilder.Build(classSource, configSource);

        var customer = result.Entities.Single(e => e.Name == "Customer");
        var order = result.Entities.Single(e => e.Name == "Order");
        Assert.Equal(new[] { "Id" }, customer.KeyPropertyNames);
        Assert.True(customer.IsKeyInferred);
        Assert.Equal(new[] { "Id" }, order.KeyPropertyNames);
        Assert.True(order.IsKeyInferred);

        var relationship = Assert.Single(result.Relationships);
        Assert.Equal("Customer", relationship.PrincipalEntity);
        Assert.Equal("Order", relationship.DependentEntity);
        Assert.True(relationship.IsInferred);
    }

    [Fact]
    public void Build_ExplicitFluentKeyOnConventionEntity_ExplicitWinsOverConvention()
    {
        const string classSource = """
            public class Customer
            {
                public int Id { get; set; }
                public string Ssn { get; set; }
            }
            """;

        const string configSource = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Customer>(entity =>
                    {
                        entity.HasKey(e => e.Ssn);
                    });
                }
            }
            """;

        var result = DiagramModelBuilder.Build(classSource, configSource);

        var customer = result.Entities.Single();
        Assert.Equal(new[] { "Ssn" }, customer.KeyPropertyNames);
        Assert.False(customer.IsKeyInferred);
    }

    [Fact]
    public void Build_ExplicitFluentRelationshipOnConventionShapedFk_ExplicitWinsOverConvention()
    {
        const string classSource = """
            public class Customer
            {
                public int Id { get; set; }
            }

            public class Order
            {
                public int Id { get; set; }
                public int CustomerId { get; set; }
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
                            .WithMany()
                            .HasForeignKey(o => o.CustomerId)
                            .OnDelete(DeleteBehavior.Cascade);
                    });
                }
            }
            """;

        var result = DiagramModelBuilder.Build(classSource, configSource);

        var relationship = Assert.Single(result.Relationships);
        Assert.False(relationship.IsInferred);
        Assert.Equal("Cascade", relationship.OnDeleteBehavior);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/EfSchemaVisualizer.Web.Tests --filter FullyQualifiedName~DiagramModelBuilderTests`
Expected: FAIL on the first two new tests (`Build_ConventionOnlyModel_InfersKeysAndRelationship` — no key/relationship inferred yet; the third test should already pass since it asserts current dedup-toward-explicit behavior — confirm it does).

- [ ] **Step 3: Wire `ConventionInference` into `DiagramModelBuilder.Build`**

In `src/EfSchemaVisualizer.Web/DiagramModelBuilder.cs`:

Add the import alongside the existing ones near the top:

```csharp
using EfSchemaVisualizer.Core.Inference;
```

Change the end of the `entities` pipeline (currently ending at `.Select(entity => ModelMerger.ApplyShadowProperties(entity, shadowProperties.Value)) .ToList();`) to add key inference as the last step:

```csharp
            .Select(entity => ModelMerger.ApplyShadowProperties(entity, shadowProperties.Value))
            .Select(ConventionInference.InferKey)
            .ToList();
```

Change the relationship-building tail of the method (currently):

```csharp
        var relationshipModels = ModelMerger.ApplyRelationships(mergedRelationshipConfigs);

        return new DiagramModelResult(entities, relationshipModels, diagnostics);
```

to:

```csharp
        var relationshipModels = ModelMerger.ApplyRelationships(mergedRelationshipConfigs);

        var explicitRelationshipKeys = relationshipModels
            .Select(RelationshipModelDedupeKey)
            .ToHashSet();

        var inferredRelationships = ConventionInference.InferRelationships(entities)
            .Where(r => !explicitRelationshipKeys.Contains(RelationshipModelDedupeKey(r)))
            .ToList();

        var allRelationships = relationshipModels.Concat(inferredRelationships).ToList();

        return new DiagramModelResult(entities, allRelationships, diagnostics);
```

Add the helper next to the existing `RelationshipDedupeKey`/`IndexDedupeKey` private methods at the bottom of the class:

```csharp
    private static (string DependentEntity, string ForeignKeyProperties) RelationshipModelDedupeKey(RelationshipModel relationship)
    {
        return (relationship.DependentEntity, string.Join(",", relationship.ForeignKeyProperties));
    }
```

This needs `using EfSchemaVisualizer.Core.Model;`, which the file already imports.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/EfSchemaVisualizer.Web.Tests --filter FullyQualifiedName~DiagramModelBuilderTests`
Expected: PASS, all tests in the file including the 3 new ones.

- [ ] **Step 5: Run the full test suite**

Run: `dotnet test`
Expected: PASS, no regressions.

- [ ] **Step 6: Commit**

```bash
git add src/EfSchemaVisualizer.Web/DiagramModelBuilder.cs tests/EfSchemaVisualizer.Web.Tests/DiagramModelBuilderTests.cs
git commit -m "Wire EF convention inference into DiagramModelBuilder"
```

---

### Task 4: Materialize inferred relationships on edit

**Files:**
- Modify: `src/EfSchemaVisualizer.Web/Diagram/DiagramEditor.cs:994-1054`
- Test: `tests/EfSchemaVisualizer.Web.Tests/Diagram/DiagramEditorPropertyPanelTests.cs`

**Interfaces:**
- Consumes: `RelationshipModel.IsInferred` (Task 2), `DiagramEditor.Current` (`DiagramModelResult`, existing), `OnModelCreatingRewriter.SetRelationship(string, RelationshipModel) -> string` (existing, `src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs:1392`).
- Produces: `DiagramEditor.SetRelationshipShape(...)` and `DiagramEditor.RemoveRelationship(...)` now behave correctly for an inferred relationship — no other task depends on this behavior, but the UI wiring in Task 5's `RelationshipLinkLabel.razor` (unchanged, already calls these) depends on it not failing.

- [ ] **Step 1: Write the failing tests**

In `tests/EfSchemaVisualizer.Web.Tests/Diagram/DiagramEditorPropertyPanelTests.cs`, add a source pair for a convention-only relationship near the existing `RelationshipClassSource`/`RelationshipConfigSource` constants, and two new `[Fact]`s after `SetRelationshipShape_SameKindFkAndOnDelete_IsNoOp`:

```csharp
    private const string InferredRelationshipClassSource = """
        public class Blog
        {
            public int Id { get; set; }
            public ICollection<Post> Posts { get; set; } = new List<Post>();
        }

        public class Post
        {
            public int Id { get; set; }
            public int BlogId { get; set; }
            public Blog Blog { get; set; } = null!;
        }
        """;

    private const string EmptyConfigSource = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
            }
        }
        """;

    [Fact]
    public void SetRelationshipShape_OnInferredRelationship_MaterializesExplicitConfig()
    {
        var editor = new DiagramEditor(InferredRelationshipClassSource, EmptyConfigSource);
        var relationship = editor.Current.Relationships.Single();
        Assert.True(relationship.IsInferred);

        var result = editor.SetRelationshipShape(relationship, relationship.Kind, relationship.ForeignKeyProperties, "Cascade");

        Assert.True(result.Success);
        var updated = editor.Current.Relationships.Single();
        Assert.False(updated.IsInferred);
        Assert.Equal("Cascade", updated.OnDeleteBehavior);
        Assert.Contains("OnDelete(DeleteBehavior.Cascade)", editor.ConfigSource);
    }

    [Fact]
    public void RemoveRelationship_OnInferredRelationship_FailsWithClearMessage()
    {
        var editor = new DiagramEditor(InferredRelationshipClassSource, EmptyConfigSource);
        var relationship = editor.Current.Relationships.Single();

        var result = editor.RemoveRelationship(relationship);

        Assert.False(result.Success);
        Assert.Contains("inferred from naming convention", result.Error);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/EfSchemaVisualizer.Web.Tests --filter FullyQualifiedName~DiagramEditorPropertyPanelTests`
Expected: FAIL — `SetRelationshipShape_OnInferredRelationship_MaterializesExplicitConfig` fails with `result.Success == false` (current code returns "Could not locate this relationship's existing configuration to update."); `RemoveRelationship_OnInferredRelationship_FailsWithClearMessage` fails because the current error message is "Could not locate this relationship in the source to remove." (doesn't contain "inferred from naming convention").

- [ ] **Step 3: Fix `SetRelationshipShape` and `RemoveRelationship`**

In `src/EfSchemaVisualizer.Web/Diagram/DiagramEditor.cs`, replace lines 1028–1036 (the body of `SetRelationshipShape` from the `RemoveRelationship` call through `Apply`):

```csharp
        var withoutOld = _configRewriter.RemoveRelationship(ConfigSource, relationship);
        if (withoutOld == ConfigSource)
        {
            return DiagramEditResult.Fail("Could not locate this relationship's existing configuration to update.");
        }

        var withNew = _configRewriter.SetRelationship(withoutOld, updated);
        Apply(ClassSource, withNew);
        return DiagramEditResult.Ok();
```

with:

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

Then replace the body of `RemoveRelationship` (currently):

```csharp
    public DiagramEditResult RemoveRelationship(RelationshipModel relationship)
    {
        if (!Current.Relationships.Contains(relationship))
        {
            return DiagramEditResult.Fail("Relationship no longer exists.");
        }

        var newConfigSource = _configRewriter.RemoveRelationship(ConfigSource, relationship);
        if (newConfigSource == ConfigSource)
        {
            return DiagramEditResult.Fail("Could not locate this relationship in the source to remove.");
        }

        Apply(ClassSource, newConfigSource);
        return DiagramEditResult.Ok();
    }
```

with:

```csharp
    public DiagramEditResult RemoveRelationship(RelationshipModel relationship)
    {
        if (!Current.Relationships.Contains(relationship))
        {
            return DiagramEditResult.Fail("Relationship no longer exists.");
        }

        if (relationship.IsInferred)
        {
            return DiagramEditResult.Fail(
                "This relationship is inferred from naming convention and isn't backed by explicit " +
                "configuration yet — change its kind or foreign key first to make it explicit.");
        }

        var newConfigSource = _configRewriter.RemoveRelationship(ConfigSource, relationship);
        if (newConfigSource == ConfigSource)
        {
            return DiagramEditResult.Fail("Could not locate this relationship in the source to remove.");
        }

        Apply(ClassSource, newConfigSource);
        return DiagramEditResult.Ok();
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/EfSchemaVisualizer.Web.Tests --filter FullyQualifiedName~DiagramEditorPropertyPanelTests`
Expected: PASS, all tests in the file including the 2 new ones.

- [ ] **Step 5: Run the full test suite**

Run: `dotnet test`
Expected: PASS, no regressions.

- [ ] **Step 6: Commit**

```bash
git add src/EfSchemaVisualizer.Web/Diagram/DiagramEditor.cs tests/EfSchemaVisualizer.Web.Tests/Diagram/DiagramEditorPropertyPanelTests.cs
git commit -m "Materialize inferred relationships into explicit config on edit"
```

---

### Task 5: Render inferred primary keys distinctly

**Files:**
- Modify: `src/EfSchemaVisualizer.Web/Diagram/EntityNode.razor`
- Test: `tests/EfSchemaVisualizer.Web.Tests/Diagram/EntityNodeMarkupTests.cs`

**Interfaces:**
- Consumes: `EntityModel.IsKeyInferred` (Task 1).
- Produces: no new interface — this is a leaf rendering change.

- [ ] **Step 1: Write the failing test**

Add to `tests/EfSchemaVisualizer.Web.Tests/Diagram/EntityNodeMarkupTests.cs`, following the file's existing markup-source-assertion pattern:

```csharp
    [Fact]
    public void PropertyRow_RendersMutedKeyMarker_WhenKeyIsInferred()
    {
        var markup = ReadEntityNodeRazorSource();

        Assert.Contains("Node.Entity.IsKeyInferred", markup);
        Assert.Contains("inferred-key", markup);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/EfSchemaVisualizer.Web.Tests --filter FullyQualifiedName~PropertyRow_RendersMutedKeyMarker_WhenKeyIsInferred`
Expected: FAIL (neither string present in the markup yet).

- [ ] **Step 3: Update the key marker markup**

In `src/EfSchemaVisualizer.Web/Diagram/EntityNode.razor`, find:

```razor
                    @if (isKey)
                    {
                        <text>🔑 </text>
                    }
```

and replace with:

```razor
                    @if (isKey)
                    {
                        <span class="@(Node.Entity.IsKeyInferred ? "inferred-key" : "")"
                              style="@(Node.Entity.IsKeyInferred ? "opacity: 0.5;" : "")"
                              title="@(Node.Entity.IsKeyInferred ? "Inferred from EF naming convention (Id / <Type>Id) — not yet in source" : null)">🔑 </span>
                    }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/EfSchemaVisualizer.Web.Tests --filter FullyQualifiedName~PropertyRow_RendersMutedKeyMarker_WhenKeyIsInferred`
Expected: PASS.

- [ ] **Step 5: Run the full test suite**

Run: `dotnet test`
Expected: PASS, no regressions.

- [ ] **Step 6: Commit**

```bash
git add src/EfSchemaVisualizer.Web/Diagram/EntityNode.razor tests/EfSchemaVisualizer.Web.Tests/Diagram/EntityNodeMarkupTests.cs
git commit -m "Render inferred primary keys with a muted marker"
```

---

### Task 6: Render inferred relationships distinctly

**Files:**
- Modify: `src/EfSchemaVisualizer.Web/Diagram/DiagramSync.cs`
- Test: `tests/EfSchemaVisualizer.Web.Tests/Diagram/DiagramSyncTests.cs`

**Interfaces:**
- Consumes: `RelationshipModel.IsInferred` (Task 2), `Blazor.Diagrams.Core.Models.LinkModel.Color` (`string?`, existing library property, defaults to `null`).
- Produces: no new interface — this is a leaf rendering change.

- [ ] **Step 1: Write the failing tests**

Add to `tests/EfSchemaVisualizer.Web.Tests/Diagram/DiagramSyncTests.cs`, following the file's existing pattern (`Entity(...)` helper and `DiagramModelResult` construction):

```csharp
    [Fact]
    public void Rebuild_InferredRelationship_RendersWithMutedLinkColor()
    {
        var diagram = NewDiagram();
        var entityIds = new Dictionary<string, Guid> { ["Customer"] = Guid.NewGuid(), ["Order"] = Guid.NewGuid() };
        var relationship = new RelationshipModel(
            "Customer", "Order", RelationshipKind.OneToMany,
            PrincipalNavigation: null, DependentNavigation: "Customer",
            ForeignKeyProperties: new[] { "CustomerId" }, IsInferred: true);

        DiagramSync.Rebuild(diagram, new DiagramModelResult(
            new[] { Entity("Customer"), Entity("Order") },
            new[] { relationship },
            Array.Empty<Core.Parsing.Diagnostic>()), entityIds);

        var link = diagram.Links.OfType<LinkModel>().Single();
        Assert.Equal("#aaaaaa", link.Color);
    }

    [Fact]
    public void Rebuild_ExplicitRelationship_RendersWithDefaultLinkColor()
    {
        var diagram = NewDiagram();
        var entityIds = new Dictionary<string, Guid> { ["Customer"] = Guid.NewGuid(), ["Order"] = Guid.NewGuid() };
        var relationship = new RelationshipModel(
            "Customer", "Order", RelationshipKind.OneToMany,
            PrincipalNavigation: "Orders", DependentNavigation: "Customer",
            ForeignKeyProperties: new[] { "CustomerId" });

        DiagramSync.Rebuild(diagram, new DiagramModelResult(
            new[] { Entity("Customer"), Entity("Order") },
            new[] { relationship },
            Array.Empty<Core.Parsing.Diagnostic>()), entityIds);

        var link = diagram.Links.OfType<LinkModel>().Single();
        Assert.Null(link.Color);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/EfSchemaVisualizer.Web.Tests --filter FullyQualifiedName~Rebuild_InferredRelationship_RendersWithMutedLinkColor`
Expected: FAIL — `link.Color` is `null` instead of `"#aaaaaa"`.

- [ ] **Step 3: Style inferred links**

In `src/EfSchemaVisualizer.Web/Diagram/DiagramSync.cs`, replace:

```csharp
            var link = new LinkModel(dependentNode, principalNode);
            link.Labels.Add(new RelationshipLinkLabelModel(link, relationship));
            diagram.Links.Add(link);
```

with:

```csharp
            var link = new LinkModel(dependentNode, principalNode);
            if (relationship.IsInferred)
            {
                link.Color = "#aaaaaa";
            }
            link.Labels.Add(new RelationshipLinkLabelModel(link, relationship));
            diagram.Links.Add(link);
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/EfSchemaVisualizer.Web.Tests --filter FullyQualifiedName~DiagramSyncTests`
Expected: PASS, all tests in the file including the 2 new ones.

- [ ] **Step 5: Run the full test suite**

Run: `dotnet test`
Expected: PASS, no regressions.

- [ ] **Step 6: Commit**

```bash
git add src/EfSchemaVisualizer.Web/Diagram/DiagramSync.cs tests/EfSchemaVisualizer.Web.Tests/Diagram/DiagramSyncTests.cs
git commit -m "Render inferred relationships with a muted link color"
```

---

### Task 7: Manual verification and backlog update

**Files:**
- Modify: `docs/backlog.md`

**Interfaces:**
- Consumes: nothing new — this task verifies the end-to-end behavior built in Tasks 1–6 and documents completion.
- Produces: nothing consumed by later tasks (this is the last task).

- [ ] **Step 1: Run the full test suite one more time**

Run: `dotnet test`
Expected: PASS, full suite green.

- [ ] **Step 2: Manually verify in the running app**

```bash
dotnet run --project src/EfSchemaVisualizer.Web
```

Open the app, paste this convention-only model as the class source (no fluent config):

```csharp
public class Customer
{
    public int Id { get; set; }
    public string Name { get; set; }
}

public class Order
{
    public int Id { get; set; }
    public int CustomerId { get; set; }
    public Customer Customer { get; set; }
}
```

Confirm: both entities show a muted 🔑 next to `Id`; a muted-colored line connects `Customer` and `Order`. Click the relationship label, change "On delete" to `Cascade`, confirm it succeeds (no error) and the link recolors to the default (now explicit). Stop the app (`Ctrl+C`).

- [ ] **Step 3: Update `docs/backlog.md`**

Change the W1 entry from:

```markdown
- [ ] **`[found]/[verified]` W1 — No EF conventions are applied.**
```

to `- [x]` and append a completion note in the same style as the F1–F6 entries above it (insert directly after the existing W1 paragraph, before the `- [ ] **`[found]/[verified]` W2` entry):

```markdown
      **Fixed 2026-07-24.** Added `EfSchemaVisualizer.Core.Inference.ConventionInference`,
      invoked from `DiagramModelBuilder.Build` after all explicit config is merged, so it
      only fills gaps and is recomputed fresh on every parse. `InferKey` infers `Id` (case-
      insensitive, wins on conflict) or `<TypeName>Id` as the primary key when no explicit
      `HasKey`/`[Key]`/`HasNoKey`/`[Keyless]` is present. `InferRelationships` matches a
      navigation property against a same-entity `<NavName>Id`/`<PrincipalTypeName>Id`
      property (both required — FK-alone inference was scoped out to keep false-positive
      risk low) and resolves one-to-many/one-to-one via the same principal-back-reference
      scan `EntityClassParser` already uses for `[ForeignKey]`-annotated relationships.
      Both are exposed via new `EntityModel.IsKeyInferred`/`RelationshipModel.IsInferred`
      flags (default `false`, additive) and rendered distinctly — a muted key marker in
      `EntityNode.razor`, a muted link color in `DiagramSync.cs` — so the user can always
      tell explicit from inferred. `DiagramEditor.SetRelationshipShape` previously failed
      outright on an inferred relationship (nothing in source to remove yet); it now
      materializes explicit fluent config on first edit instead, which is the concrete
      mechanism behind "never write an inferred value back to source unless the user edits
      it". Verified against the exact convention-only repro this finding was originally
      written from: `Customer`/`Order` with `int Id`, `Customer Customer`, `int
      CustomerId`, no fluent config, no attributes now render as two keyed, related
      entities instead of two disconnected keyless boxes. Composite convention keys, FK-
      alone relationship inference, and actually suppressing an inferred relationship
      remain out of scope (see the design spec).
```

- [ ] **Step 4: Commit**

```bash
git add docs/backlog.md
git commit -m "Mark backlog W1 (EF convention inference) done"
```

## Self-Review Notes

- **Spec coverage:** primary-key inference (Task 1), relationship inference (Task 2), wiring/dedup against explicit config (Task 3), edit-path materialization (Task 4), key rendering (Task 5), relationship rendering (Task 6) — every section of the design spec has a task. The design's suggested "dashed" relationship styling was refined to a muted link color during planning, since `Blazor.Diagrams.Core.Models.LinkModel` (verified via reflection against the installed `Z.Blazor.Diagrams` 3.0.4.1 package) exposes `Color`/`Width` but no stroke-dasharray hook without writing a custom link widget component — out of proportion for this fix. Both achieve the same goal: explicit vs. inferred is always visually distinguishable.
- **Placeholder scan:** none — every step has literal code, exact commands, and expected output.
- **Type consistency:** `ConventionInference.InferKey(EntityModel) -> EntityModel` and `ConventionInference.InferRelationships(IReadOnlyList<EntityModel>) -> IReadOnlyList<RelationshipModel>` are the only two public members introduced, and every later task/test calls them with these exact signatures. `EntityModel.IsKeyInferred` and `RelationshipModel.IsInferred` are introduced once (Tasks 1 and 2) and referenced identically everywhere downstream (Tasks 3–6).
