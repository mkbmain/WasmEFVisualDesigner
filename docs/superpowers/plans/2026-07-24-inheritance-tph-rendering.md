# Inheritance / TPH Rendering Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make a TPH inheritance hierarchy (`Student : Person`) render as one
connected shape — derived entities show their inherited properties and key,
and a distinct edge links each derived entity to its base — instead of three
disconnected, keyless fragments.

**Architecture:** Parse the base class name off each entity's class
declaration (`EntityClassParser`), fold inherited properties/keys into
derived entities and synthesize an inheritance `RelationshipModel` in a new
pure module (`Core.Inference.InheritanceInference`), wire it into
`DiagramModelBuilder.Build` right after key inference runs, then teach
`DiagramEditor`'s property-scoped edit methods to resolve which entity
actually declares a given property before rewriting source, and teach the
diagram/link rendering to draw+label the new inheritance edge distinctly and
read-only.

**Tech Stack:** C#/.NET, Roslyn (`Microsoft.CodeAnalysis.CSharp`), xUnit,
Blazor (`.razor` components, tested via markup-source assertions per this
repo's existing pattern — no bUnit).

## Global Constraints

- Backlog source: `docs/backlog.md` item **W2** (Priority 1). Design spec:
  `docs/superpowers/specs/2026-07-24-inheritance-tph-design.md`.
- Every new/changed field must default in a way that leaves every existing
  positional-or-named-arg call site (tests included) compiling unchanged —
  this repo's established pattern for additive model fields (see `IsKeyInferred`,
  `IsInferred` on `RelationshipModel`).
- No behavior change for any entity without a resolvable `BaseEntityName` —
  every new code path must be a no-op for non-inheriting models.
- Out of scope (do not implement): TPT/TPC mapping strategies,
  `HasDiscriminator`/`HasValue` parsing, removing an inheritance edge,
  synthetic `Discriminator` shadow column, multi-property (composite) index /
  alternate-key ownership routing across inheritance (existing behavior for
  those is left as-is; only the 13 single-scalar-property edit methods listed
  in Task 6/7 are rerouted).
- Full solution test command: `dotnet test` from the repo root
  (`/root/RiderProjects/EfSchemaVisualizer`). Every task ends with running
  this (or a narrower `--filter` while iterating, then the full suite before
  commit).

---

### Task 1: Model changes — `BaseEntityName`, `DeclaringEntityName`, `RelationshipKind.Inheritance`

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/Model/EntityModel.cs`
- Modify: `src/EfSchemaVisualizer.Core/Model/PropertyModel.cs`
- Modify: `src/EfSchemaVisualizer.Core/Model/RelationshipKind.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Model/PropertyModelTests.cs`

**Interfaces:**
- Produces: `EntityModel.BaseEntityName` (`string?`, default `null`),
  `PropertyModel.DeclaringEntityName` (`string?`, default `null`),
  `RelationshipKind.Inheritance` (new enum member). All later tasks depend on
  these three.

- [ ] **Step 1: Write the failing tests**

Append to `tests/EfSchemaVisualizer.Core.Tests/Model/PropertyModelTests.cs`
(inside the `PropertyModelTests` class, e.g. right after
`IsShadow`/`WithIsShadow` at the end of the file):

```csharp
    [Fact]
    public void DeclaringEntityName_DefaultsToNull()
    {
        var property = new PropertyModel("Id", "int", IsNullable: false, MaxLength: null);

        Assert.Null(property.DeclaringEntityName);
    }

    [Fact]
    public void WithDeclaringEntityName_ProducesUpdatedCopy_LeavingOriginalUnchanged()
    {
        var original = new PropertyModel("Id", "int", IsNullable: false, MaxLength: null);

        var updated = original with { DeclaringEntityName = "Person" };

        Assert.Null(original.DeclaringEntityName);
        Assert.Equal("Person", updated.DeclaringEntityName);
    }

    [Fact]
    public void EntityModel_BaseEntityName_DefaultsToNull()
    {
        var entity = new EntityModel("Student", new List<PropertyModel>());

        Assert.Null(entity.BaseEntityName);
    }

    [Fact]
    public void EntityModel_WithBaseEntityName_ProducesUpdatedCopy_LeavingOriginalUnchanged()
    {
        var original = new EntityModel("Student", new List<PropertyModel>());

        var updated = original with { BaseEntityName = "Person" };

        Assert.Null(original.BaseEntityName);
        Assert.Equal("Person", updated.BaseEntityName);
    }

    [Fact]
    public void RelationshipKind_HasInheritanceMember()
    {
        var relationship = new RelationshipModel(
            "Person", "Student", RelationshipKind.Inheritance,
            PrincipalNavigation: null, DependentNavigation: null);

        Assert.Equal(RelationshipKind.Inheritance, relationship.Kind);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~PropertyModelTests"`
Expected: FAIL to compile — `DeclaringEntityName`, `BaseEntityName`, and
`RelationshipKind.Inheritance` don't exist yet.

- [ ] **Step 3: Add the fields**

In `src/EfSchemaVisualizer.Core/Model/PropertyModel.cs`, add
`DeclaringEntityName` as the last positional parameter:

```csharp
namespace EfSchemaVisualizer.Core.Model;

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
    string? DeclaringEntityName = null);
```

In `src/EfSchemaVisualizer.Core/Model/EntityModel.cs`, add `BaseEntityName`
as the last positional parameter of the record header (leave the `init`
properties below the header untouched):

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
    string? BaseEntityName = null)
{
    public IReadOnlyList<string> KeyPropertyNames { get; init; } = KeyPropertyNames ?? new List<string>();
    public IReadOnlyList<IndexModel> Indexes { get; init; } = Indexes ?? new List<IndexModel>();
    public IReadOnlyList<IReadOnlyList<string>> AlternateKeys { get; init; } = AlternateKeys ?? new List<IReadOnlyList<string>>();
    public IReadOnlyList<string> SplitTables { get; init; } = SplitTables ?? new List<string>();
}
```

In `src/EfSchemaVisualizer.Core/Model/RelationshipKind.cs`:

```csharp
namespace EfSchemaVisualizer.Core.Model;

public enum RelationshipKind
{
    OneToOne,
    OneToMany,
    ManyToMany,
    Inheritance,
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~PropertyModelTests"`
Expected: PASS (all tests in the file, including the 5 new ones).

- [ ] **Step 5: Run the full test suite**

Run: `dotnet test`
Expected: PASS — every existing test still compiles and passes (both new
fields are purely additive with `null`/enum-append defaults).

- [ ] **Step 6: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Model/EntityModel.cs \
        src/EfSchemaVisualizer.Core/Model/PropertyModel.cs \
        src/EfSchemaVisualizer.Core/Model/RelationshipKind.cs \
        tests/EfSchemaVisualizer.Core.Tests/Model/PropertyModelTests.cs
git commit -m "Add BaseEntityName/DeclaringEntityName/RelationshipKind.Inheritance model fields"
```

---

### Task 2: `EntityClassParser` resolves `BaseEntityName`

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/Parsing/EntityClassParser.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Parsing/EntityClassParserTests.cs`

**Interfaces:**
- Consumes: `EntityModel.BaseEntityName` (Task 1).
- Produces: `EntityClassParser.Parse` now populates `BaseEntityName` on the
  returned `EntityModel`s when the class's first base-list entry resolves to
  a sibling entity in the same parse. Task 3/4 depend on this being set
  correctly (including staying `null` for non-entity bases).

- [ ] **Step 1: Write the failing tests**

Add to `tests/EfSchemaVisualizer.Core.Tests/Parsing/EntityClassParserTests.cs`
(inside the `EntityClassParserTests` class):

```csharp
    [Fact]
    public void Parse_ClassInheritsFromSiblingEntity_SetsBaseEntityName()
    {
        const string source = """
            public class Person
            {
                public int Id { get; set; }
                public string Name { get; set; }
            }

            public class Student : Person
            {
                public string Course { get; set; }
            }
            """;

        var result = new EntityClassParser().Parse(source);

        var student = result.Value.Single(e => e.Name == "Student");
        Assert.Equal("Person", student.BaseEntityName);

        var person = result.Value.Single(e => e.Name == "Person");
        Assert.Null(person.BaseEntityName);
    }

    [Fact]
    public void Parse_ClassInheritsFromInterfaceOnly_BaseEntityNameStaysNull()
    {
        const string source = """
            public interface IAuditable
            {
                DateTime CreatedAt { get; set; }
            }

            public class Person : IAuditable
            {
                public int Id { get; set; }
                public DateTime CreatedAt { get; set; }
            }
            """;

        var result = new EntityClassParser().Parse(source);

        var person = result.Value.Single(e => e.Name == "Person");
        Assert.Null(person.BaseEntityName);
    }

    [Fact]
    public void Parse_ClassInheritsFromUnmappedExternalType_BaseEntityNameStaysNull()
    {
        const string source = """
            public class DomainException : Exception
            {
                public int Id { get; set; }
            }
            """;

        var result = new EntityClassParser().Parse(source);

        var entity = result.Value.Single();
        Assert.Null(entity.BaseEntityName);
    }

    [Fact]
    public void Parse_ClassWithNoBaseList_BaseEntityNameIsNull()
    {
        const string source = """
            public class Person
            {
                public int Id { get; set; }
            }
            """;

        var result = new EntityClassParser().Parse(source);

        Assert.Null(result.Value.Single().BaseEntityName);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~EntityClassParserTests"`
Expected: FAIL — `Parse_ClassInheritsFromSiblingEntity_SetsBaseEntityName`
asserts `"Person"` but gets `null` (not implemented yet); the other three
already pass trivially (nothing sets `BaseEntityName` yet) but must keep
passing after Step 3.

- [ ] **Step 3: Implement base-entity-name capture and resolution**

In `src/EfSchemaVisualizer.Core/Parsing/EntityClassParser.cs`, add a private
helper near `ParseTableAttribute` (after it, before
`ParseIndexAttributes`):

```csharp
    private static string? ParseBaseEntityCandidate(TypeDeclarationSyntax typeDeclaration)
    {
        if (typeDeclaration is not ClassDeclarationSyntax { BaseList.Types.Count: > 0 } classDeclaration)
        {
            return null;
        }

        return classDeclaration.BaseList!.Types[0].Type switch
        {
            IdentifierNameSyntax id => id.Identifier.Text,
            QualifiedNameSyntax { Right: IdentifierNameSyntax id } => id.Identifier.Text,
            _ => null,
        };
    }
```

Change `ParseEntity` to capture the candidate and stamp it onto the
returned `EntityModel` (add the parameter/argument to the existing
constructor call at the end of the method):

```csharp
    private static EntityModel ParseEntity(TypeDeclarationSyntax typeDeclaration)
    {
        var positionalProperties = typeDeclaration is RecordDeclarationSyntax
            ? typeDeclaration.ParameterList?.Parameters.Select(ParseParameterProperty) ?? Enumerable.Empty<PropertyModel>()
            : Enumerable.Empty<PropertyModel>();

        var mappedProperties = typeDeclaration.Members
            .OfType<PropertyDeclarationSyntax>()
            .Where(IsMappedInstanceProperty)
            .ToList();

        var bodyProperties = mappedProperties.Select(ParseProperty);

        var properties = positionalProperties.Concat(bodyProperties).ToList();

        var keyPropertyNames = ResolveKeyPropertyNames(mappedProperties);
        var (tableName, schema) = ParseTableAttribute(typeDeclaration.AttributeLists);
        var isKeyless = FindAttribute(typeDeclaration.AttributeLists, "Keyless") is not null;
        var baseEntityCandidate = ParseBaseEntityCandidate(typeDeclaration);

        return new EntityModel(
            typeDeclaration.Identifier.Text,
            properties,
            keyPropertyNames,
            TableName: tableName,
            Schema: schema,
            IsKeyless: isKeyless,
            BaseEntityName: baseEntityCandidate);
    }
```

Finally, in `Parse`, resolve the candidate against sibling entity names
right before returning (replace the existing tail of the method — the
`deduplicatedEntities` assignment and `return` — with the version below):

```csharp
        var deduplicatedEntities = entities
            .GroupBy(e => e.Name)
            .Select(g => g.First())
            .ToList();

        var entityNames = deduplicatedEntities.Select(e => e.Name).ToHashSet();
        var resolvedEntities = deduplicatedEntities
            .Select(e => e.BaseEntityName is not null && entityNames.Contains(e.BaseEntityName)
                ? e
                : e with { BaseEntityName = null })
            .ToList();

        return new ParseResult<IReadOnlyList<EntityModel>>(resolvedEntities, diagnostics);
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~EntityClassParserTests"`
Expected: PASS (all tests in the file).

- [ ] **Step 5: Run the full test suite**

Run: `dotnet test`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Parsing/EntityClassParser.cs \
        tests/EfSchemaVisualizer.Core.Tests/Parsing/EntityClassParserTests.cs
git commit -m "Resolve BaseEntityName from class declarations in EntityClassParser"
```

---

### Task 3: `InheritanceInference.Fold` — new module

**Files:**
- Create: `src/EfSchemaVisualizer.Core/Inference/InheritanceInference.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Inference/InheritanceInferenceTests.cs` (new)

**Interfaces:**
- Consumes: `EntityModel.BaseEntityName`/`PropertyModel.DeclaringEntityName`
  (Task 1/2), `RelationshipKind.Inheritance` (Task 1), `RelationshipModel`
  (existing).
- Produces: `InheritanceFoldResult(IReadOnlyList<EntityModel> Entities,
  IReadOnlyList<RelationshipModel> Relationships)` and
  `InheritanceInference.Fold(IReadOnlyList<EntityModel> entities) ->
  InheritanceFoldResult`. Task 4 calls this directly.

- [ ] **Step 1: Write the failing tests**

Create `tests/EfSchemaVisualizer.Core.Tests/Inference/InheritanceInferenceTests.cs`:

```csharp
using System.Collections.Generic;
using System.Linq;
using EfSchemaVisualizer.Core.Inference;
using EfSchemaVisualizer.Core.Model;
using Xunit;

namespace EfSchemaVisualizer.Core.Tests.Inference;

public class InheritanceInferenceTests
{
    private static PropertyModel Property(string name, string clrType) =>
        new(name, clrType, IsNullable: false, MaxLength: null);

    [Fact]
    public void Fold_NoEntityHasBaseEntityName_ReturnsEntitiesUnchangedAndNoRelationships()
    {
        var person = new EntityModel("Person", new[] { Property("Id", "int") }, KeyPropertyNames: new[] { "Id" });

        var result = InheritanceInference.Fold(new[] { person });

        Assert.Same(person, Assert.Single(result.Entities));
        Assert.Empty(result.Relationships);
    }

    [Fact]
    public void Fold_DerivedEntity_InheritsBaseProperties()
    {
        var person = new EntityModel(
            "Person",
            new[] { Property("Id", "int"), Property("Name", "string") },
            KeyPropertyNames: new[] { "Id" });
        var student = new EntityModel(
            "Student",
            new[] { Property("Course", "string") },
            BaseEntityName: "Person");

        var result = InheritanceInference.Fold(new[] { person, student });

        var foldedStudent = result.Entities.Single(e => e.Name == "Student");
        Assert.Equal(new[] { "Id", "Name", "Course" }, foldedStudent.Properties.Select(p => p.Name));
        Assert.Equal("Person", foldedStudent.Properties.Single(p => p.Name == "Id").DeclaringEntityName);
        Assert.Equal("Person", foldedStudent.Properties.Single(p => p.Name == "Name").DeclaringEntityName);
        Assert.Null(foldedStudent.Properties.Single(p => p.Name == "Course").DeclaringEntityName);
    }

    [Fact]
    public void Fold_DerivedEntity_InheritsBaseKeyAndMarksItInferred()
    {
        var person = new EntityModel(
            "Person",
            new[] { Property("Id", "int") },
            KeyPropertyNames: new[] { "Id" });
        var student = new EntityModel("Student", new[] { Property("Course", "string") }, BaseEntityName: "Person");

        var result = InheritanceInference.Fold(new[] { person, student });

        var foldedStudent = result.Entities.Single(e => e.Name == "Student");
        Assert.Equal(new[] { "Id" }, foldedStudent.KeyPropertyNames);
        Assert.True(foldedStudent.IsKeyInferred);
    }

    [Fact]
    public void Fold_DerivedEntityWithItsOwnExplicitKey_KeepsItsOwnKey()
    {
        var person = new EntityModel("Person", new[] { Property("Id", "int") }, KeyPropertyNames: new[] { "Id" });
        var student = new EntityModel(
            "Student",
            new[] { Property("StudentNumber", "string") },
            KeyPropertyNames: new[] { "StudentNumber" },
            BaseEntityName: "Person");

        var result = InheritanceInference.Fold(new[] { person, student });

        var foldedStudent = result.Entities.Single(e => e.Name == "Student");
        Assert.Equal(new[] { "StudentNumber" }, foldedStudent.KeyPropertyNames);
        Assert.False(foldedStudent.IsKeyInferred);
    }

    [Fact]
    public void Fold_OwnPropertyWithSameNameAsAncestorProperty_OwnPropertyWins()
    {
        var person = new EntityModel("Person", new[] { Property("Name", "string") });
        var student = new EntityModel(
            "Student",
            new[] { Property("Name", "string?") },
            BaseEntityName: "Person");

        var result = InheritanceInference.Fold(new[] { person, student });

        var foldedStudent = result.Entities.Single(e => e.Name == "Student");
        var name = Assert.Single(foldedStudent.Properties, p => p.Name == "Name");
        Assert.Equal("string?", name.ClrType);
        Assert.Null(name.DeclaringEntityName);
    }

    [Fact]
    public void Fold_MultiLevelChain_FoldsAllAncestorsInRootFirstOrder()
    {
        var entity = new EntityModel("Entity", new[] { Property("Id", "int") }, KeyPropertyNames: new[] { "Id" });
        var person = new EntityModel("Person", new[] { Property("Name", "string") }, BaseEntityName: "Entity");
        var student = new EntityModel("Student", new[] { Property("Course", "string") }, BaseEntityName: "Person");

        var result = InheritanceInference.Fold(new[] { entity, person, student });

        var foldedStudent = result.Entities.Single(e => e.Name == "Student");
        Assert.Equal(new[] { "Id", "Name", "Course" }, foldedStudent.Properties.Select(p => p.Name));
        Assert.Equal("Entity", foldedStudent.Properties.Single(p => p.Name == "Id").DeclaringEntityName);
        Assert.Equal("Person", foldedStudent.Properties.Single(p => p.Name == "Name").DeclaringEntityName);
        Assert.Equal(new[] { "Id" }, foldedStudent.KeyPropertyNames);
    }

    [Fact]
    public void Fold_MalformedCycle_DoesNotThrowAndStopsAtCycle()
    {
        var a = new EntityModel("A", new[] { Property("X", "int") }, BaseEntityName: "B");
        var b = new EntityModel("B", new[] { Property("Y", "int") }, BaseEntityName: "A");

        var result = InheritanceInference.Fold(new[] { a, b });

        Assert.Equal(2, result.Entities.Count);
    }

    [Fact]
    public void Fold_BaseEntityNameDoesNotResolve_TreatedAsRootEntity()
    {
        var student = new EntityModel("Student", new[] { Property("Course", "string") }, BaseEntityName: "Ignored");

        var result = InheritanceInference.Fold(new[] { student });

        var folded = Assert.Single(result.Entities);
        Assert.Equal(new[] { "Course" }, folded.Properties.Select(p => p.Name));
        Assert.Empty(result.Relationships);
    }

    [Fact]
    public void Fold_DerivedEntity_ProducesInheritanceRelationshipToDirectBase()
    {
        var person = new EntityModel("Person", new[] { Property("Id", "int") }, KeyPropertyNames: new[] { "Id" });
        var student = new EntityModel("Student", new[] { Property("Course", "string") }, BaseEntityName: "Person");

        var result = InheritanceInference.Fold(new[] { person, student });

        var relationship = Assert.Single(result.Relationships);
        Assert.Equal("Person", relationship.PrincipalEntity);
        Assert.Equal("Student", relationship.DependentEntity);
        Assert.Equal(RelationshipKind.Inheritance, relationship.Kind);
        Assert.False(relationship.IsInferred);
        Assert.Empty(relationship.ForeignKeyProperties);
    }

    [Fact]
    public void Fold_TwoSiblingsSharingOneBase_ProducesOneRelationshipPerSibling()
    {
        var person = new EntityModel("Person", new[] { Property("Id", "int") }, KeyPropertyNames: new[] { "Id" });
        var student = new EntityModel("Student", new[] { Property("Course", "string") }, BaseEntityName: "Person");
        var teacher = new EntityModel("Teacher", new[] { Property("Salary", "decimal") }, BaseEntityName: "Person");

        var result = InheritanceInference.Fold(new[] { person, student, teacher });

        Assert.Equal(2, result.Relationships.Count);
        Assert.Contains(result.Relationships, r => r.DependentEntity == "Student" && r.PrincipalEntity == "Person");
        Assert.Contains(result.Relationships, r => r.DependentEntity == "Teacher" && r.PrincipalEntity == "Person");
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~InheritanceInferenceTests"`
Expected: FAIL to compile — `InheritanceInference` doesn't exist yet.

- [ ] **Step 3: Implement `InheritanceInference`**

Create `src/EfSchemaVisualizer.Core/Inference/InheritanceInference.cs`:

```csharp
using System.Collections.Generic;
using System.Linq;
using EfSchemaVisualizer.Core.Model;

namespace EfSchemaVisualizer.Core.Inference;

public sealed record InheritanceFoldResult(
    IReadOnlyList<EntityModel> Entities,
    IReadOnlyList<RelationshipModel> Relationships);

public static class InheritanceInference
{
    public static InheritanceFoldResult Fold(IReadOnlyList<EntityModel> entities)
    {
        var byName = entities.ToDictionary(e => e.Name);
        var foldedEntities = new List<EntityModel>();
        var relationships = new List<RelationshipModel>();

        foreach (var entity in entities)
        {
            if (entity.BaseEntityName is null || !byName.ContainsKey(entity.BaseEntityName))
            {
                foldedEntities.Add(entity);
                continue;
            }

            var nearestFirstChain = BuildAncestorChain(entity, byName);

            var seenNames = new HashSet<string>(entity.Properties.Select(p => p.Name));
            var foldedProperties = new List<PropertyModel>();

            foreach (var ancestor in nearestFirstChain.AsEnumerable().Reverse())
            {
                foreach (var property in ancestor.Properties)
                {
                    if (!seenNames.Add(property.Name))
                    {
                        continue;
                    }

                    foldedProperties.Add(property with { DeclaringEntityName = ancestor.Name });
                }
            }

            foldedProperties.AddRange(entity.Properties);

            var keyPropertyNames = entity.KeyPropertyNames;
            var isKeyInferred = entity.IsKeyInferred;
            if (keyPropertyNames.Count == 0 && !entity.IsKeyless)
            {
                var nearestKeyedAncestor = nearestFirstChain.FirstOrDefault(a => a.KeyPropertyNames.Count > 0);
                if (nearestKeyedAncestor is not null)
                {
                    keyPropertyNames = nearestKeyedAncestor.KeyPropertyNames;
                    isKeyInferred = true;
                }
            }

            foldedEntities.Add(entity with
            {
                Properties = foldedProperties,
                KeyPropertyNames = keyPropertyNames,
                IsKeyInferred = isKeyInferred,
            });

            var directBase = byName[entity.BaseEntityName];
            relationships.Add(new RelationshipModel(
                directBase.Name,
                entity.Name,
                RelationshipKind.Inheritance,
                PrincipalNavigation: null,
                DependentNavigation: null,
                ForeignKeyProperties: new List<string>(),
                IsInferred: false));
        }

        return new InheritanceFoldResult(foldedEntities, relationships);
    }

    /// Nearest-ancestor-first (immediate parent, grandparent, ...). Cycle-guarded: a
    /// malformed `BaseEntityName` loop stops instead of looping forever.
    private static List<EntityModel> BuildAncestorChain(
        EntityModel entity, Dictionary<string, EntityModel> byName)
    {
        var chain = new List<EntityModel>();
        var visited = new HashSet<string> { entity.Name };
        var current = entity;

        while (current.BaseEntityName is not null && byName.TryGetValue(current.BaseEntityName, out var ancestor))
        {
            if (!visited.Add(ancestor.Name))
            {
                break;
            }

            chain.Add(ancestor);
            current = ancestor;
        }

        return chain;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~InheritanceInferenceTests"`
Expected: PASS (all 11 tests).

- [ ] **Step 5: Run the full test suite**

Run: `dotnet test`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Inference/InheritanceInference.cs \
        tests/EfSchemaVisualizer.Core.Tests/Inference/InheritanceInferenceTests.cs
git commit -m "Add InheritanceInference.Fold: property/key folding + inheritance edges"
```

---

### Task 4: Wire `InheritanceInference` into `DiagramModelBuilder.Build`

**Files:**
- Modify: `src/EfSchemaVisualizer.Web/DiagramModelBuilder.cs`
- Test: `tests/EfSchemaVisualizer.Web.Tests/DiagramModelBuilderTests.cs`

**Interfaces:**
- Consumes: `InheritanceInference.Fold` (Task 3).
- Produces: `DiagramModelBuilder.Build(...).Entities` now includes folded
  properties/keys for derived entities; `.Relationships` now includes one
  `Kind == Inheritance` entry per derived entity. Task 5 (DiagramSync/label
  rendering) and Task 6/7 (DiagramEditor routing) depend on this.

- [ ] **Step 1: Write the failing test**

Add to `tests/EfSchemaVisualizer.Web.Tests/DiagramModelBuilderTests.cs`
(inside `DiagramModelBuilderTests`, e.g. after
`Build_ExplicitRelationshipWithoutHasForeignKey_DoesNotProduceDuplicateInferredRelationship`):

```csharp
    [Fact]
    public void Build_TphHierarchy_FoldsInheritedPropertiesAndKeyAndAddsInheritanceEdge()
    {
        const string classSource = """
            public class Person
            {
                public int Id { get; set; }
                public string Name { get; set; }
            }

            public class Student : Person
            {
                public string Course { get; set; }
            }

            public class Teacher : Person
            {
                public decimal Salary { get; set; }
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

        var student = result.Entities.Single(e => e.Name == "Student");
        Assert.Equal(new[] { "Id", "Name", "Course" }, student.Properties.Select(p => p.Name));
        Assert.Equal(new[] { "Id" }, student.KeyPropertyNames);
        Assert.True(student.IsKeyInferred);

        var teacher = result.Entities.Single(e => e.Name == "Teacher");
        Assert.Equal(new[] { "Id", "Name", "Salary" }, teacher.Properties.Select(p => p.Name));
        Assert.Equal(new[] { "Id" }, teacher.KeyPropertyNames);

        var inheritanceEdges = result.Relationships.Where(r => r.Kind == RelationshipKind.Inheritance).ToList();
        Assert.Equal(2, inheritanceEdges.Count);
        Assert.Contains(inheritanceEdges, r => r.PrincipalEntity == "Person" && r.DependentEntity == "Student");
        Assert.Contains(inheritanceEdges, r => r.PrincipalEntity == "Person" && r.DependentEntity == "Teacher");
    }
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~Build_TphHierarchy_FoldsInheritedPropertiesAndKeyAndAddsInheritanceEdge"`
Expected: FAIL — `Student`/`Teacher` currently have only their own
properties (`Course`/`Salary`), empty `KeyPropertyNames`, and no
`Inheritance`-kind relationship.

- [ ] **Step 3: Wire the fold step into `Build`**

In `src/EfSchemaVisualizer.Web/DiagramModelBuilder.cs`, add the import:

```csharp
using EfSchemaVisualizer.Core.Inference;
```

(This import already exists — `ConventionInference` is used from the same
namespace — so this step is a no-op if already present; verify rather than
duplicate the `using`.)

Change the `entities` declaration (currently `var entities = ...ToList();`
ending in `.Select(ConventionInference.InferKey)`) to be explicitly typed so
it can be reassigned, and fold immediately after:

```csharp
        IReadOnlyList<EntityModel> entities = entityResult.Value
            .Where(entity => !ignoredEntityNames.Contains(entity.Name))
            .Select(entity => ModelMerger.ApplyMaxLengths(entity, maxLengths.Value))
            .Select(entity => ModelMerger.ApplyPrecisions(entity, precisions.Value))
            .Select(entity => ModelMerger.ApplyIsRequired(entity, isRequired.Value))
            .Select(entity => ModelMerger.ApplyKeys(entity, keys.Value))
            .Select(entity => ModelMerger.ApplyAlternateKeys(entity, alternateKeys.Value))
            .Select(entity => ModelMerger.ApplyQueryFilters(entity, queryFilters.Value))
            .Select(entity => ModelMerger.ApplyEntityComments(entity, comments.Entities.Value))
            .Select(entity => ModelMerger.ApplyPropertyComments(entity, comments.Properties.Value))
            .Select(entity => ModelMerger.ApplyUnicodeFlags(entity, unicodeFlags.Value))
            .Select(entity => ModelMerger.ApplyFixedLengthFlags(entity, fixedLengthFlags.Value))
            .Select(entity => ModelMerger.ApplyCollations(entity, collations.Value))
            .Select(entity => ModelMerger.ApplyJsonMappings(entity, jsonMappings.Value))
            .Select(entity => ModelMerger.ApplySplitTables(entity, splitTables.Value))
            .Select(entity => ModelMerger.ApplyTableMapping(entity, tables.Value.Tables))
            .Select(entity => ModelMerger.ApplyTemporal(entity, tables.Value.Temporal))
            .Select(entity => ModelMerger.ApplyViewMapping(entity, views.Value))
            .Select(entity => ModelMerger.ApplySqlQuery(entity, sqlQueries.Value))
            .Select(entity => entity.IsKeyless || fluentKeylessNames.Contains(entity.Name)
                ? entity with { IsKeyless = true }
                : entity)
            .Select(entity => ModelMerger.ApplyColumnNames(entity, columnNames.Value))
            .Select(entity => ModelMerger.ApplyColumnTypes(entity, columnTypes.Value))
            .Select(entity => ModelMerger.ApplyDefaultValues(entity, defaultValues.Value))
            .Select(entity => ModelMerger.ApplyDefaultValueSqls(entity, defaultValueSqls.Value))
            .Select(entity => ModelMerger.ApplyIndexes(entity, mergedIndexConfigs))
            .Select(entity => ModelMerger.ApplyValueGeneration(entity, valueGeneration.Value))
            .Select(entity => ModelMerger.ApplyConcurrencyTokens(entity, concurrencyTokens.Value))
            .Select(entity => ModelMerger.ApplyIgnoredProperties(entity, ignoredProperties.Value))
            .Select(entity => ModelMerger.ApplyShadowProperties(entity, shadowProperties.Value))
            .Select(ConventionInference.InferKey)
            .ToList();

        var inheritanceFold = InheritanceInference.Fold(entities);
        entities = inheritanceFold.Entities;
```

Then change the final return-building section — the existing
`inferredRelationships`/`allRelationships` block — to include the
inheritance relationships:

```csharp
        var inferredRelationships = ConventionInference.InferRelationships(entities)
            .Where(r => !explicitRelationshipKeys.Contains(RelationshipModelDedupeKey(r))
                && !explicitNavigationKeys.Contains((r.DependentEntity, r.DependentNavigation)))
            .ToList();

        var allRelationships = relationshipModels
            .Concat(inferredRelationships)
            .Concat(inheritanceFold.Relationships)
            .ToList();

        return new DiagramModelResult(entities, allRelationships, diagnostics);
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~Build_TphHierarchy_FoldsInheritedPropertiesAndKeyAndAddsInheritanceEdge"`
Expected: PASS.

- [ ] **Step 5: Run the full test suite**

Run: `dotnet test`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/EfSchemaVisualizer.Web/DiagramModelBuilder.cs \
        tests/EfSchemaVisualizer.Web.Tests/DiagramModelBuilderTests.cs
git commit -m "Wire InheritanceInference.Fold into DiagramModelBuilder.Build"
```

---

### Task 5: Render the inheritance edge distinctly and read-only

**Files:**
- Modify: `src/EfSchemaVisualizer.Web/Diagram/DiagramSync.cs`
- Modify: `src/EfSchemaVisualizer.Web/Diagram/RelationshipLabels.cs`
- Modify: `src/EfSchemaVisualizer.Web/Diagram/RelationshipLinkLabel.razor`
- Test: `tests/EfSchemaVisualizer.Web.Tests/Diagram/DiagramSyncTests.cs`
- Test: `tests/EfSchemaVisualizer.Web.Tests/Diagram/RelationshipLabelsTests.cs` (new)

**Interfaces:**
- Consumes: `RelationshipModel` with `Kind == RelationshipKind.Inheritance`
  (Task 4 produces these; this task only changes how they render).
- No new interfaces produced — this task is a leaf.

- [ ] **Step 1: Write the failing tests**

Add to `tests/EfSchemaVisualizer.Web.Tests/Diagram/DiagramSyncTests.cs`
(inside `DiagramSyncTests`, after `Rebuild_ExplicitRelationship_RendersWithDefaultLinkColor`):

```csharp
    [Fact]
    public void Rebuild_InheritanceRelationship_RendersWithDistinctColorNotInferredGray()
    {
        var diagram = NewDiagram();
        var entityIds = new Dictionary<string, Guid> { ["Person"] = Guid.NewGuid(), ["Student"] = Guid.NewGuid() };
        var relationship = new RelationshipModel(
            "Person", "Student", RelationshipKind.Inheritance,
            PrincipalNavigation: null, DependentNavigation: null);

        DiagramSync.Rebuild(diagram, new DiagramModelResult(
            new[] { Entity("Person"), Entity("Student") },
            new[] { relationship },
            Array.Empty<Core.Parsing.Diagnostic>()), entityIds);

        var link = diagram.Links.OfType<LinkModel>().Single();
        Assert.NotNull(link.Color);
        Assert.NotEqual("#aaaaaa", link.Color);
    }
```

Create `tests/EfSchemaVisualizer.Web.Tests/Diagram/RelationshipLabelsTests.cs`:

```csharp
using EfSchemaVisualizer.Core.Model;
using EfSchemaVisualizer.Web.Diagram;
using Xunit;

namespace EfSchemaVisualizer.Web.Tests.Diagram;

public class RelationshipLabelsTests
{
    [Fact]
    public void For_Inheritance_ReturnsDistinctLabel()
    {
        var label = RelationshipLabels.For(RelationshipKind.Inheritance);

        Assert.NotEqual("?", label);
        Assert.NotEqual(RelationshipLabels.For(RelationshipKind.OneToOne), label);
        Assert.NotEqual(RelationshipLabels.For(RelationshipKind.OneToMany), label);
        Assert.NotEqual(RelationshipLabels.For(RelationshipKind.ManyToMany), label);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~RelationshipLabelsTests|FullyQualifiedName~Rebuild_InheritanceRelationship_RendersWithDistinctColorNotInferredGray"`
Expected: FAIL — `RelationshipLabels.For(RelationshipKind.Inheritance)`
currently falls through to `"?"` (equal to itself, so the "NotEqual" checks
against the OneToOne/OneToMany/ManyToMany labels still happen to pass, but
`Assert.NotEqual("?", label)` fails); the `DiagramSync` link color is
currently always `null` for a non-inferred relationship, so
`Assert.NotNull(link.Color)` fails.

- [ ] **Step 3: Implement the rendering changes**

In `src/EfSchemaVisualizer.Web/Diagram/RelationshipLabels.cs`:

```csharp
using EfSchemaVisualizer.Core.Model;

namespace EfSchemaVisualizer.Web.Diagram;

public static class RelationshipLabels
{
    public static string For(RelationshipKind kind) => kind switch
    {
        RelationshipKind.OneToOne => "1—1",
        RelationshipKind.OneToMany => "1—*",
        RelationshipKind.ManyToMany => "*—*",
        RelationshipKind.Inheritance => "▷",
        _ => "?",
    };
}
```

In `src/EfSchemaVisualizer.Web/Diagram/DiagramSync.cs`, change the
relationship loop's color logic (currently `if (relationship.IsInferred) {
link.Color = "#aaaaaa"; }`) to also color inheritance edges distinctly:

```csharp
        foreach (var relationship in result.Relationships)
        {
            if (!nodesByEntityName.TryGetValue(relationship.PrincipalEntity, out var principalNode) ||
                !nodesByEntityName.TryGetValue(relationship.DependentEntity, out var dependentNode))
            {
                continue;
            }

            var link = new LinkModel(dependentNode, principalNode);
            if (relationship.Kind == RelationshipKind.Inheritance)
            {
                link.Color = "#4a5a8a";
            }
            else if (relationship.IsInferred)
            {
                link.Color = "#aaaaaa";
            }
            link.Labels.Add(new RelationshipLinkLabelModel(link, relationship));
            diagram.Links.Add(link);
        }
```

In `src/EfSchemaVisualizer.Web/Diagram/RelationshipLinkLabel.razor`, replace
the `@if (_expanded) { ... }` block's inner content so that an inheritance
relationship shows a read-only line instead of the Kind/FK/On-delete/Remove
controls. Replace the existing block:

```razor
@if (_expanded)
{
    <div style="position: absolute; background: white; border: 1px solid #999; border-radius: 3px; padding: 6px; font-size: 0.75em; white-space: nowrap; z-index: 1000;"
         @onpointerdown:stopPropagation="true"
         @onmousedown:stopPropagation="true">
        @if (Label.Relationship.Kind == RelationshipKind.Inheritance)
        {
            <div style="display: block;">@Label.Relationship.DependentEntity extends @Label.Relationship.PrincipalEntity</div>
        }
        else
        {
            <label style="display: block;">
                Kind:
                <select value="@_kind" @onchange="e => CommitKind(e.Value?.ToString())">
                    <option value="OneToMany">One-to-many</option>
                    <option value="OneToOne">One-to-one</option>
                    <option value="ManyToMany">Many-to-many</option>
                </select>
            </label>
            @if (_kind != RelationshipKind.ManyToMany)
            {
                <div style="display: block;">
                    Foreign key:
                    @if (!DependentProperties.Any())
                    {
                        <span>(none — shadow FK)</span>
                    }
                    @foreach (var property in DependentProperties)
                    {
                        <label style="display: block;">
                            <input type="checkbox" checked="@_foreignKeyProperties.Contains(property.Name)"
                                   @onchange="e => ToggleForeignKeyProperty(property.Name, (bool)(e.Value ?? false))"
                                   @onpointerdown:stopPropagation="true"
                                   @onmousedown:stopPropagation="true" />
                            @property.Name
                        </label>
                    }
                </div>
                <label style="display: block;">
                    On delete:
                    <select value="@_onDeleteBehavior" @onchange="e => CommitOnDeleteBehavior(e.Value?.ToString())">
                        <option value="">(default)</option>
                        <option value="Cascade">Cascade</option>
                        <option value="Restrict">Restrict</option>
                        <option value="SetNull">SetNull</option>
                        <option value="NoAction">NoAction</option>
                    </select>
                </label>
            }
            else if (Label.Relationship.JoinEntityName is not null)
            {
                <div style="display: block;">Join entity: @Label.Relationship.JoinEntityName</div>
            }
            @if (_error is not null)
            {
                <div style="color: red;">@_error</div>
            }
            <button type="button" @onclick="Remove">Remove relationship</button>
        }
    </div>
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~RelationshipLabelsTests|FullyQualifiedName~DiagramSyncTests"`
Expected: PASS.

- [ ] **Step 5: Run the full test suite**

Run: `dotnet test`
Expected: PASS. In particular re-check
`FullyQualifiedName~GestureHandlerSafeEditTests` explicitly — the `.razor`
edit must not introduce any new unwrapped `EditContext.Editor.*` mutation
call; it only wraps the existing calls in an additional `@if` branch.

- [ ] **Step 6: Commit**

```bash
git add src/EfSchemaVisualizer.Web/Diagram/DiagramSync.cs \
        src/EfSchemaVisualizer.Web/Diagram/RelationshipLabels.cs \
        src/EfSchemaVisualizer.Web/Diagram/RelationshipLinkLabel.razor \
        tests/EfSchemaVisualizer.Web.Tests/Diagram/DiagramSyncTests.cs \
        tests/EfSchemaVisualizer.Web.Tests/Diagram/RelationshipLabelsTests.cs
git commit -m "Render inheritance edges distinctly and read-only"
```

---

### Task 6: `DiagramEditor` — add `ResolveDeclaringEntity` and route class-source edits

**Files:**
- Modify: `src/EfSchemaVisualizer.Web/Diagram/DiagramEditor.cs`
- Test: `tests/EfSchemaVisualizer.Web.Tests/Diagram/DiagramEditorInheritanceTests.cs` (new)

**Interfaces:**
- Consumes: `EntityModel.Properties[].DeclaringEntityName` (Task 1/3/4).
- Produces: `private string ResolveDeclaringEntity(string entityName, string
  propertyName)` on `DiagramEditor` — Task 7 reuses this exact method on the
  remaining config-only property methods.

This task covers the three methods that touch `ClassSource`:
`RenameProperty`, `ChangePropertyType`, `RemoveProperty`.

- [ ] **Step 1: Write the failing tests**

Create `tests/EfSchemaVisualizer.Web.Tests/Diagram/DiagramEditorInheritanceTests.cs`:

```csharp
using EfSchemaVisualizer.Web.Diagram;
using Xunit;

namespace EfSchemaVisualizer.Web.Tests.Diagram;

public class DiagramEditorInheritanceTests
{
    private const string ClassSource = """
        public class Person
        {
            public int Id { get; set; }
            public string Name { get; set; }
        }

        public class Student : Person
        {
            public string Course { get; set; }
        }
        """;

    private const string ConfigSource = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
            }
        }
        """;

    [Fact]
    public void RenameProperty_InheritedPropertyViewedFromDerivedEntity_RenamesItOnTheBaseClass()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);

        var result = editor.RenameProperty("Student", "Name", "FullName");

        Assert.True(result.Success);
        Assert.Contains("public string FullName { get; set; }", editor.ClassSource);
        Assert.DoesNotContain("public string Name { get; set; }", editor.ClassSource);
        Assert.Contains(editor.Current.Entities.Single(e => e.Name == "Student").Properties, p => p.Name == "FullName");
    }

    [Fact]
    public void ChangePropertyType_InheritedPropertyViewedFromDerivedEntity_ChangesItOnTheBaseClass()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);

        var result = editor.ChangePropertyType("Student", "Name", "string", newIsNullable: true);

        Assert.True(result.Success);
        Assert.Contains("public string? Name { get; set; }", editor.ClassSource);
    }

    [Fact]
    public void RemoveProperty_InheritedPropertyViewedFromDerivedEntity_RemovesItFromTheBaseClass()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);

        var result = editor.RemoveProperty("Student", "Name");

        Assert.True(result.Success);
        Assert.DoesNotContain("public string Name { get; set; }", editor.ClassSource);
        Assert.DoesNotContain(editor.Current.Entities.Single(e => e.Name == "Student").Properties, p => p.Name == "Name");
        Assert.DoesNotContain(editor.Current.Entities.Single(e => e.Name == "Person").Properties, p => p.Name == "Name");
    }

    [Fact]
    public void RenameProperty_OwnPropertyOnDerivedEntity_StillWorksUnaffected()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);

        var result = editor.RenameProperty("Student", "Course", "Class");

        Assert.True(result.Success);
        Assert.Contains("public string Class { get; set; }", editor.ClassSource);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~DiagramEditorInheritanceTests"`
Expected: FAIL — today, `RenameProperty("Student", "Name", "FullName")` calls
`_classRewriter.RenameProperty(ClassSource, "Student", "Name", "FullName")`,
which can't find a member named `Name` inside `class Student` (it's declared
on `Person`), so the source comes back unchanged and
`editor.ClassSource` still contains the old `Name` declaration. Same shape
of failure for `ChangePropertyType`/`RemoveProperty`.

- [ ] **Step 3: Add the resolver and route the three methods**

In `src/EfSchemaVisualizer.Web/Diagram/DiagramEditor.cs`, add the helper
near the other private helpers (e.g. right before `GenerateUniquePropertyName`
at line 1066):

```csharp
    private string ResolveDeclaringEntity(string entityName, string propertyName)
    {
        var property = Current.Entities
            .FirstOrDefault(e => e.Name == entityName)?.Properties
            .FirstOrDefault(p => p.Name == propertyName);

        return property?.DeclaringEntityName ?? entityName;
    }
```

In `RenameProperty` (around line 160), change:

```csharp
        var newClassSource = _classRewriter.RenameProperty(ClassSource, entityName, oldPropertyName, newPropertyName);
        var newConfigSource = _configRewriter.RenamePropertyReferences(ConfigSource, entityName, oldPropertyName, newPropertyName);
        Apply(newClassSource, newConfigSource);
        return DiagramEditResult.Ok();
```

to:

```csharp
        var owningEntityName = ResolveDeclaringEntity(entityName, oldPropertyName);
        var newClassSource = _classRewriter.RenameProperty(ClassSource, owningEntityName, oldPropertyName, newPropertyName);
        var newConfigSource = _configRewriter.RenamePropertyReferences(ConfigSource, owningEntityName, oldPropertyName, newPropertyName);
        Apply(newClassSource, newConfigSource);
        return DiagramEditResult.Ok();
```

In `ChangePropertyType` (around line 184), change:

```csharp
        var newClassSource = _classRewriter.ChangePropertyType(ClassSource, entityName, propertyName, newClrType, newIsNullable);
        Apply(newClassSource, ConfigSource);
        return DiagramEditResult.Ok();
```

to:

```csharp
        var owningEntityName = ResolveDeclaringEntity(entityName, propertyName);
        var newClassSource = _classRewriter.ChangePropertyType(ClassSource, owningEntityName, propertyName, newClrType, newIsNullable);
        Apply(newClassSource, ConfigSource);
        return DiagramEditResult.Ok();
```

In `RemoveProperty` (around line 274), change:

```csharp
        var newClassSource = _classRewriter.RemoveProperty(ClassSource, entityName, propertyName);
        Apply(newClassSource, ConfigSource);
        return DiagramEditResult.Ok();
```

to:

```csharp
        var owningEntityName = ResolveDeclaringEntity(entityName, propertyName);
        var newClassSource = _classRewriter.RemoveProperty(ClassSource, owningEntityName, propertyName);
        Apply(newClassSource, ConfigSource);
        return DiagramEditResult.Ok();
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~DiagramEditorInheritanceTests"`
Expected: PASS.

- [ ] **Step 5: Run the full test suite**

Run: `dotnet test`
Expected: PASS — every pre-existing `RenameProperty`/`ChangePropertyType`/
`RemoveProperty` test still passes because `ResolveDeclaringEntity` returns
`entityName` unchanged whenever `DeclaringEntityName` is `null` (the
non-inheriting case, which is every existing test's fixture).

- [ ] **Step 6: Commit**

```bash
git add src/EfSchemaVisualizer.Web/Diagram/DiagramEditor.cs \
        tests/EfSchemaVisualizer.Web.Tests/Diagram/DiagramEditorInheritanceTests.cs
git commit -m "Route class-source property edits to the entity that actually declares the property"
```

---

### Task 7: `DiagramEditor` — route remaining config-only property edits

**Files:**
- Modify: `src/EfSchemaVisualizer.Web/Diagram/DiagramEditor.cs`
- Test: `tests/EfSchemaVisualizer.Web.Tests/Diagram/DiagramEditorInheritanceTests.cs`

**Interfaces:**
- Consumes: `ResolveDeclaringEntity` (Task 6).
- No new interfaces produced — this task is a leaf.

This task covers the remaining 10 property-scoped methods:
`ToggleKey`, `SetColumnName`, `SetColumnType`, `SetMaxLength`,
`SetRequiredOverride`, `SetRowVersion`, `SetConcurrencyToken`,
`SetPrecision`, `SetDefaultValue`, `SetDefaultValueSql`.

- [ ] **Step 1: Write the failing tests**

Add to `tests/EfSchemaVisualizer.Web.Tests/Diagram/DiagramEditorInheritanceTests.cs`
(inside `DiagramEditorInheritanceTests`, after the existing tests):

```csharp
    [Fact]
    public void SetMaxLength_InheritedPropertyViewedFromDerivedEntity_WritesConfigUnderTheBaseEntityScope()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);

        var result = editor.SetMaxLength("Student", "Name", 50);

        Assert.True(result.Success);
        Assert.Contains("modelBuilder.Entity<Person>", editor.ConfigSource);
        Assert.Contains("HasMaxLength(50)", editor.ConfigSource);
        Assert.DoesNotContain("modelBuilder.Entity<Student>", editor.ConfigSource);
    }

    [Fact]
    public void ToggleKey_InheritedPropertyViewedFromDerivedEntity_WritesHasKeyUnderTheBaseEntityScope()
    {
        const string classSourceNoOwnKey = """
            public class Person
            {
                public int Id { get; set; }
            }

            public class Student : Person
            {
                public string Course { get; set; }
            }
            """;

        var editor = new DiagramEditor(classSourceNoOwnKey, ConfigSource);

        var result = editor.ToggleKey("Student", "Id", isKey: true);

        Assert.True(result.Success);
        Assert.Contains("modelBuilder.Entity<Person>", editor.ConfigSource);
        Assert.Contains("HasKey", editor.ConfigSource);
    }

    [Fact]
    public void SetColumnName_OwnPropertyOnDerivedEntity_StillWritesUnderTheDerivedEntityScope()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);

        var result = editor.SetColumnName("Student", "Course", "class_name");

        Assert.True(result.Success);
        Assert.Contains("modelBuilder.Entity<Student>", editor.ConfigSource);
        Assert.Contains("class_name", editor.ConfigSource);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~DiagramEditorInheritanceTests"`
Expected: FAIL on the two new inherited-property cases —
`_configRewriter.RewriteMaxLength(ConfigSource, "Student", "Name", 50)` and
`_configRewriter.SetKey(ConfigSource, "Student", ["Id"])` currently insert
a new `modelBuilder.Entity<Student>(...)` scope instead of targeting
`Person`, since nothing routes them yet. The own-property case already
passes and must keep passing.

- [ ] **Step 3: Route the remaining 10 methods**

For each method below, insert `var owningEntityName =
ResolveDeclaringEntity(entityName, propertyName);` as the first statement
after the existing `property is null`/property-existence check, and replace
`entityName` with `owningEntityName` **only in the `_configRewriter.*` call
arguments** shown (leave every other use of `entityName` — validation,
error-message interpolation — unchanged).

`ToggleKey` (around line 279-310), the tail changes from:

```csharp
        var newKeyPropertyNames = isKey
            ? entity.KeyPropertyNames.Append(propertyName).ToList()
            : entity.KeyPropertyNames.Where(name => name != propertyName).ToList();

        if (newKeyPropertyNames.Count == 0)
        {
            return DiagramEditResult.Fail($"'{entityName}' must have at least one key property.");
        }

        var newConfigSource = _configRewriter.SetKey(ConfigSource, entityName, newKeyPropertyNames);
        Apply(ClassSource, newConfigSource);
        return DiagramEditResult.Ok();
```

to:

```csharp
        var newKeyPropertyNames = isKey
            ? entity.KeyPropertyNames.Append(propertyName).ToList()
            : entity.KeyPropertyNames.Where(name => name != propertyName).ToList();

        if (newKeyPropertyNames.Count == 0)
        {
            return DiagramEditResult.Fail($"'{entityName}' must have at least one key property.");
        }

        var owningEntityName = ResolveDeclaringEntity(entityName, propertyName);
        var newConfigSource = _configRewriter.SetKey(ConfigSource, owningEntityName, newKeyPropertyNames);
        Apply(ClassSource, newConfigSource);
        return DiagramEditResult.Ok();
```

`SetColumnName` (around line 638-663), change:

```csharp
        var newConfigSource = normalizedColumnName is null
            ? _configRewriter.RemoveColumnName(ConfigSource, entityName, propertyName)
            : _configRewriter.SetColumnName(ConfigSource, entityName, propertyName, normalizedColumnName);
        Apply(ClassSource, newConfigSource);
        return DiagramEditResult.Ok();
```

to:

```csharp
        var owningEntityName = ResolveDeclaringEntity(entityName, propertyName);
        var newConfigSource = normalizedColumnName is null
            ? _configRewriter.RemoveColumnName(ConfigSource, owningEntityName, propertyName)
            : _configRewriter.SetColumnName(ConfigSource, owningEntityName, propertyName, normalizedColumnName);
        Apply(ClassSource, newConfigSource);
        return DiagramEditResult.Ok();
```

`SetColumnType` (around line 665-690), change:

```csharp
        var newConfigSource = normalizedColumnType is null
            ? _configRewriter.RemoveColumnType(ConfigSource, entityName, propertyName)
            : _configRewriter.SetColumnType(ConfigSource, entityName, propertyName, normalizedColumnType);
        Apply(ClassSource, newConfigSource);
        return DiagramEditResult.Ok();
```

to:

```csharp
        var owningEntityName = ResolveDeclaringEntity(entityName, propertyName);
        var newConfigSource = normalizedColumnType is null
            ? _configRewriter.RemoveColumnType(ConfigSource, owningEntityName, propertyName)
            : _configRewriter.SetColumnType(ConfigSource, owningEntityName, propertyName, normalizedColumnType);
        Apply(ClassSource, newConfigSource);
        return DiagramEditResult.Ok();
```

`SetMaxLength` (around line 692-726), change:

```csharp
        if (maxLength is null)
        {
            var clearedConfigSource = _configRewriter.RemoveMaxLength(ConfigSource, entityName, propertyName);
            Apply(ClassSource, clearedConfigSource);
            return DiagramEditResult.Ok();
        }

        if (maxLength <= 0)
        {
            return DiagramEditResult.Fail("Max length must be a positive number.");
        }

        var newConfigSource = _configRewriter.RewriteMaxLength(ConfigSource, entityName, propertyName, maxLength.Value);
        Apply(ClassSource, newConfigSource);
        return DiagramEditResult.Ok();
```

to:

```csharp
        var owningEntityName = ResolveDeclaringEntity(entityName, propertyName);

        if (maxLength is null)
        {
            var clearedConfigSource = _configRewriter.RemoveMaxLength(ConfigSource, owningEntityName, propertyName);
            Apply(ClassSource, clearedConfigSource);
            return DiagramEditResult.Ok();
        }

        if (maxLength <= 0)
        {
            return DiagramEditResult.Fail("Max length must be a positive number.");
        }

        var newConfigSource = _configRewriter.RewriteMaxLength(ConfigSource, owningEntityName, propertyName, maxLength.Value);
        Apply(ClassSource, newConfigSource);
        return DiagramEditResult.Ok();
```

`SetRequiredOverride` (around line 728-757), change:

```csharp
        if (isRequired is null)
        {
            var clearedConfigSource = _configRewriter.RemoveIsRequired(ConfigSource, entityName, propertyName);
            Apply(ClassSource, clearedConfigSource);
            return DiagramEditResult.Ok();
        }

        var newConfigSource = _configRewriter.RewriteIsRequired(ConfigSource, entityName, propertyName, isRequired.Value);
        Apply(ClassSource, newConfigSource);
        return DiagramEditResult.Ok();
```

to:

```csharp
        var owningEntityName = ResolveDeclaringEntity(entityName, propertyName);

        if (isRequired is null)
        {
            var clearedConfigSource = _configRewriter.RemoveIsRequired(ConfigSource, owningEntityName, propertyName);
            Apply(ClassSource, clearedConfigSource);
            return DiagramEditResult.Ok();
        }

        var newConfigSource = _configRewriter.RewriteIsRequired(ConfigSource, owningEntityName, propertyName, isRequired.Value);
        Apply(ClassSource, newConfigSource);
        return DiagramEditResult.Ok();
```

`SetRowVersion` (around line 759-794), change:

```csharp
        if (!isRowVersion)
        {
            var clearedConfigSource = _configRewriter.RemoveRowVersion(ConfigSource, entityName, propertyName);
            if (clearedConfigSource == ConfigSource)
            {
                return DiagramEditResult.Fail(
                    $"'{propertyName}' is marked as a row version by a [Timestamp] attribute on the class; remove the attribute to clear it.");
            }

            Apply(ClassSource, clearedConfigSource);
            return DiagramEditResult.Ok();
        }

        var newConfigSource = _configRewriter.SetRowVersion(ConfigSource, entityName, propertyName);
        Apply(ClassSource, newConfigSource);
        return DiagramEditResult.Ok();
```

to:

```csharp
        var owningEntityName = ResolveDeclaringEntity(entityName, propertyName);

        if (!isRowVersion)
        {
            var clearedConfigSource = _configRewriter.RemoveRowVersion(ConfigSource, owningEntityName, propertyName);
            if (clearedConfigSource == ConfigSource)
            {
                return DiagramEditResult.Fail(
                    $"'{propertyName}' is marked as a row version by a [Timestamp] attribute on the class; remove the attribute to clear it.");
            }

            Apply(ClassSource, clearedConfigSource);
            return DiagramEditResult.Ok();
        }

        var newConfigSource = _configRewriter.SetRowVersion(ConfigSource, owningEntityName, propertyName);
        Apply(ClassSource, newConfigSource);
        return DiagramEditResult.Ok();
```

`SetConcurrencyToken` (around line 796-831), change:

```csharp
        if (!isConcurrencyToken)
        {
            var clearedConfigSource = _configRewriter.RemoveConcurrencyToken(ConfigSource, entityName, propertyName);
            if (clearedConfigSource == ConfigSource)
            {
                return DiagramEditResult.Fail(
                    $"'{propertyName}' is marked as a concurrency token by a [ConcurrencyCheck] attribute on the class; remove the attribute to clear it.");
            }

            Apply(ClassSource, clearedConfigSource);
            return DiagramEditResult.Ok();
        }

        var newConfigSource = _configRewriter.SetConcurrencyToken(ConfigSource, entityName, propertyName);
        Apply(ClassSource, newConfigSource);
        return DiagramEditResult.Ok();
```

to:

```csharp
        var owningEntityName = ResolveDeclaringEntity(entityName, propertyName);

        if (!isConcurrencyToken)
        {
            var clearedConfigSource = _configRewriter.RemoveConcurrencyToken(ConfigSource, owningEntityName, propertyName);
            if (clearedConfigSource == ConfigSource)
            {
                return DiagramEditResult.Fail(
                    $"'{propertyName}' is marked as a concurrency token by a [ConcurrencyCheck] attribute on the class; remove the attribute to clear it.");
            }

            Apply(ClassSource, clearedConfigSource);
            return DiagramEditResult.Ok();
        }

        var newConfigSource = _configRewriter.SetConcurrencyToken(ConfigSource, owningEntityName, propertyName);
        Apply(ClassSource, newConfigSource);
        return DiagramEditResult.Ok();
```

`SetPrecision` (around line 833-877), change:

```csharp
        if (precision is null)
        {
            if (property.Precision is null && property.Scale is null)
            {
                return DiagramEditResult.Ok();
            }

            var clearedConfigSource = _configRewriter.RemovePrecision(ConfigSource, entityName, propertyName);
            Apply(ClassSource, clearedConfigSource);
            return DiagramEditResult.Ok();
        }

        if (precision <= 0)
        {
            return DiagramEditResult.Fail("Precision must be a positive number.");
        }

        if (scale is not null && scale < 0)
        {
            return DiagramEditResult.Fail("Scale cannot be negative.");
        }

        if (precision == property.Precision && scale == property.Scale)
        {
            return DiagramEditResult.Ok();
        }

        var newConfigSource = _configRewriter.RewritePrecision(ConfigSource, entityName, propertyName, precision.Value, scale);
        Apply(ClassSource, newConfigSource);
        return DiagramEditResult.Ok();
```

to:

```csharp
        var owningEntityName = ResolveDeclaringEntity(entityName, propertyName);

        if (precision is null)
        {
            if (property.Precision is null && property.Scale is null)
            {
                return DiagramEditResult.Ok();
            }

            var clearedConfigSource = _configRewriter.RemovePrecision(ConfigSource, owningEntityName, propertyName);
            Apply(ClassSource, clearedConfigSource);
            return DiagramEditResult.Ok();
        }

        if (precision <= 0)
        {
            return DiagramEditResult.Fail("Precision must be a positive number.");
        }

        if (scale is not null && scale < 0)
        {
            return DiagramEditResult.Fail("Scale cannot be negative.");
        }

        if (precision == property.Precision && scale == property.Scale)
        {
            return DiagramEditResult.Ok();
        }

        var newConfigSource = _configRewriter.RewritePrecision(ConfigSource, owningEntityName, propertyName, precision.Value, scale);
        Apply(ClassSource, newConfigSource);
        return DiagramEditResult.Ok();
```

`SetDefaultValue` (around line 879-916), change:

```csharp
        var newConfigSource = normalizedLiteral is null
            ? _configRewriter.RemoveDefaultValue(ConfigSource, entityName, propertyName)
            : _configRewriter.SetDefaultValue(ConfigSource, entityName, propertyName, normalizedLiteral);
        Apply(ClassSource, newConfigSource);
        return DiagramEditResult.Ok();
```

to:

```csharp
        var owningEntityName = ResolveDeclaringEntity(entityName, propertyName);
        var newConfigSource = normalizedLiteral is null
            ? _configRewriter.RemoveDefaultValue(ConfigSource, owningEntityName, propertyName)
            : _configRewriter.SetDefaultValue(ConfigSource, owningEntityName, propertyName, normalizedLiteral);
        Apply(ClassSource, newConfigSource);
        return DiagramEditResult.Ok();
```

`SetDefaultValueSql` (around line 918-943), change:

```csharp
        var newConfigSource = normalizedSql is null
            ? _configRewriter.RemoveDefaultValueSql(ConfigSource, entityName, propertyName)
            : _configRewriter.SetDefaultValueSql(ConfigSource, entityName, propertyName, normalizedSql);
        Apply(ClassSource, newConfigSource);
        return DiagramEditResult.Ok();
```

to:

```csharp
        var owningEntityName = ResolveDeclaringEntity(entityName, propertyName);
        var newConfigSource = normalizedSql is null
            ? _configRewriter.RemoveDefaultValueSql(ConfigSource, owningEntityName, propertyName)
            : _configRewriter.SetDefaultValueSql(ConfigSource, owningEntityName, propertyName, normalizedSql);
        Apply(ClassSource, newConfigSource);
        return DiagramEditResult.Ok();
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~DiagramEditorInheritanceTests"`
Expected: PASS (all 7 tests in the file).

- [ ] **Step 5: Run the full test suite**

Run: `dotnet test`
Expected: PASS — same reasoning as Task 6 (`ResolveDeclaringEntity` is a
no-op for every non-inheriting fixture in the existing suite).

- [ ] **Step 6: Commit**

```bash
git add src/EfSchemaVisualizer.Web/Diagram/DiagramEditor.cs \
        tests/EfSchemaVisualizer.Web.Tests/Diagram/DiagramEditorInheritanceTests.cs
git commit -m "Route remaining single-property config edits to the declaring entity"
```

---

### Task 8: Update the backlog

**Files:**
- Modify: `docs/backlog.md`

**Interfaces:**
- None — documentation only.

- [ ] **Step 1: Mark W2 done**

In `docs/backlog.md`, change the W2 checkbox from `- [ ]` to `- [x]` and
append a "Fixed" paragraph in the same style as W1 (see the existing W1
entry immediately above it for the exact format: what changed, which files,
what was verified, what's still out of scope). Summarize: `BaseEntityName`/
`DeclaringEntityName` added, `InheritanceInference.Fold` folds properties
and key and emits a `RelationshipKind.Inheritance` edge, `DiagramEditor`'s
scalar property-edit methods route to the declaring entity, rendering shows
a distinct read-only edge. Note TPT/TPC, `HasDiscriminator`/`HasValue`
editing, and removing an inheritance edge remain out of scope (link to the
design spec: `docs/superpowers/specs/2026-07-24-inheritance-tph-design.md`).

- [ ] **Step 2: Run the full test suite one last time**

Run: `dotnet test`
Expected: PASS.

- [ ] **Step 3: Commit**

```bash
git add docs/backlog.md
git commit -m "Mark backlog W2 (inheritance/TPH rendering) done"
```
