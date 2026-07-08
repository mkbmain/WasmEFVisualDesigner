# Add Property to Entity Class Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add an `EntityClassRewriter.AddProperty` method that inserts a new auto-property into an existing POCO class/record/struct declaration.

**Architecture:** A new `EntityClassRewriter` class in `src/EfSchemaVisualizer.Core/CodeGen/`, parallel to the existing `OnModelCreatingRewriter`. It parses the source with Roslyn, finds the named top-level type declaration, appends a synthesized `PropertyDeclarationSyntax` as the last member, and returns `NormalizeWhitespace().ToFullString()` — the same insertion pattern `OnModelCreatingRewriter` already uses.

**Tech Stack:** C# / .NET, `Microsoft.CodeAnalysis.CSharp` (Roslyn), xUnit.

## Global Constraints

- Reuse the existing `PropertyModel(Name, ClrType, IsNullable, MaxLength)` record as the input shape; `MaxLength` is ignored by this method.
- Only top-level type declarations are eligible targets — a type nested inside another type must never match (same rule as `EntityClassParser.Parse`: `!t.Ancestors().OfType<TypeDeclarationSyntax>().Any()`).
- Missing target type → throw `InvalidOperationException`.
- New property is always a full auto-property (`public {Type} {Name} { get; set; }`, `?` suffix if nullable), always appended as the last member — no accessor-style inference, no positioning near other properties.
- Insertion paths use whole-tree `NormalizeWhitespace()` — no trivia preservation (matches `OnModelCreatingRewriter`'s established trade-off).
- Record positional parameters are out of scope — only body (member-list) properties are synthesized.

---

### Task 1: `EntityClassRewriter.AddProperty` — core insertion behavior

**Files:**
- Create: `src/EfSchemaVisualizer.Core/CodeGen/EntityClassRewriter.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/CodeGen/EntityClassRewriterTests.cs`

**Interfaces:**
- Consumes: `EfSchemaVisualizer.Core.Model.PropertyModel(string Name, string ClrType, bool IsNullable, int? MaxLength)` (already exists, no changes).
- Produces: `EfSchemaVisualizer.Core.CodeGen.EntityClassRewriter.AddProperty(string sourceCode, string className, PropertyModel property) -> string`. No other public members. Later tasks (guard clause) extend this same method's body — no new public surface.

- [ ] **Step 1: Write the failing tests for the success paths**

Create `tests/EfSchemaVisualizer.Core.Tests/CodeGen/EntityClassRewriterTests.cs`:

```csharp
using EfSchemaVisualizer.Core.CodeGen;
using EfSchemaVisualizer.Core.Model;
using Xunit;

namespace EfSchemaVisualizer.Core.Tests.CodeGen;

public class EntityClassRewriterTests
{
    private const string SourceWithExistingProperties = """
        public class Person
        {
            // unrelated comment that must survive
            public int Id { get; set; }
            public string Name { get; set; }
        }
        """;

    [Fact]
    public void AddProperty_ClassWithExistingProperties_AppendsAsLastMember()
    {
        var result = new EntityClassRewriter().AddProperty(
            SourceWithExistingProperties,
            className: "Person",
            property: new PropertyModel("Email", "string", IsNullable: false, MaxLength: null));

        Assert.Contains("public string Email { get; set; }", result);
        Assert.Contains("public int Id { get; set; }", result);
        Assert.Contains("public string Name { get; set; }", result);
        Assert.Contains("// unrelated comment that must survive", result);

        // Appended after Name, not before it or interleaved.
        var nameIndex = result.IndexOf("Name { get; set; }", StringComparison.Ordinal);
        var emailIndex = result.IndexOf("Email { get; set; }", StringComparison.Ordinal);
        Assert.True(emailIndex > nameIndex);
    }

    private const string SourceWithEmptyClassBody = """
        public class Person
        {
        }
        """;

    [Fact]
    public void AddProperty_ClassWithNoExistingProperties_InsertsSingleProperty()
    {
        var result = new EntityClassRewriter().AddProperty(
            SourceWithEmptyClassBody,
            className: "Person",
            property: new PropertyModel("Name", "string", IsNullable: false, MaxLength: null));

        Assert.Contains("public string Name { get; set; }", result);
    }

    [Fact]
    public void AddProperty_NullablePropertyModel_AppendsQuestionMarkSuffix()
    {
        var result = new EntityClassRewriter().AddProperty(
            SourceWithEmptyClassBody,
            className: "Person",
            property: new PropertyModel("MiddleName", "string", IsNullable: true, MaxLength: null));

        Assert.Contains("public string? MiddleName { get; set; }", result);
    }

    private const string SourceWithRecord = """
        public record Person
        {
            public int Id { get; set; }
        }
        """;

    [Fact]
    public void AddProperty_RecordWithBodyProperties_AppendsToMemberList()
    {
        var result = new EntityClassRewriter().AddProperty(
            SourceWithRecord,
            className: "Person",
            property: new PropertyModel("Name", "string", IsNullable: false, MaxLength: null));

        Assert.Contains("public string Name { get; set; }", result);
        Assert.Contains("public int Id { get; set; }", result);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail with a compile error**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter EntityClassRewriterTests`
Expected: FAIL to build — `EntityClassRewriter` does not exist yet.

- [ ] **Step 3: Implement `EntityClassRewriter.AddProperty`**

Create `src/EfSchemaVisualizer.Core/CodeGen/EntityClassRewriter.cs`:

```csharp
using System;
using System.Linq;
using EfSchemaVisualizer.Core.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EfSchemaVisualizer.Core.CodeGen;

public sealed class EntityClassRewriter
{
    public string AddProperty(string sourceCode, string className, PropertyModel property)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var targetType = root.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .Where(t => !t.Ancestors().OfType<TypeDeclarationSyntax>().Any())
            .FirstOrDefault(t => t.Identifier.Text == className)
            ?? throw new InvalidOperationException($"No top-level class, record, or struct named '{className}' found in source.");

        var newProperty = BuildPropertyDeclaration(property);
        var newType = targetType.AddMembers(newProperty);

        var newRoot = root.ReplaceNode(targetType, newType);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    private static PropertyDeclarationSyntax BuildPropertyDeclaration(PropertyModel property)
    {
        TypeSyntax typeSyntax = SyntaxFactory.ParseTypeName(property.ClrType);

        if (property.IsNullable)
        {
            typeSyntax = SyntaxFactory.NullableType(typeSyntax);
        }

        return SyntaxFactory.PropertyDeclaration(typeSyntax, property.Name)
            .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword))
            .AddAccessorListAccessors(
                SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                    .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)),
                SyntaxFactory.AccessorDeclaration(SyntaxKind.SetAccessorDeclaration)
                    .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)));
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter EntityClassRewriterTests`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add src/EfSchemaVisualizer.Core/CodeGen/EntityClassRewriter.cs tests/EfSchemaVisualizer.Core.Tests/CodeGen/EntityClassRewriterTests.cs
git commit -m "Add EntityClassRewriter.AddProperty for inserting a new property into a class or record"
```

---

### Task 2: Guard clause and multi-declaration isolation

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/CodeGen/EntityClassRewriter.cs` (no code change expected — the guard clause already exists from Task 1; this task is pure test coverage for it and for sibling-isolation)
- Test: `tests/EfSchemaVisualizer.Core.Tests/CodeGen/EntityClassRewriterTests.cs`

**Interfaces:**
- Consumes: `EntityClassRewriter.AddProperty` from Task 1 (unchanged signature).
- Produces: nothing new — this task only adds test coverage confirming the guard clause and isolation behavior already present in Task 1's implementation.

- [ ] **Step 1: Write the failing tests**

Add to `tests/EfSchemaVisualizer.Core.Tests/CodeGen/EntityClassRewriterTests.cs` (inside the existing `EntityClassRewriterTests` class, after the record test):

```csharp
    private const string SourceWithoutMatchingClass = """
        public class Vehicle
        {
            public int Id { get; set; }
        }
        """;

    [Fact]
    public void AddProperty_ClassNameNotFound_Throws()
    {
        var rewriter = new EntityClassRewriter();

        Assert.Throws<InvalidOperationException>(() =>
            rewriter.AddProperty(SourceWithoutMatchingClass, className: "Person", property: new PropertyModel("Name", "string", IsNullable: false, MaxLength: null)));
    }

    private const string SourceWithMultipleTopLevelTypes = """
        public class Person
        {
            public int Id { get; set; }
        }

        public class Address
        {
            public string Line1 { get; set; }
        }
        """;

    [Fact]
    public void AddProperty_MultipleTopLevelTypes_OnlyModifiesTargetType()
    {
        var result = new EntityClassRewriter().AddProperty(
            SourceWithMultipleTopLevelTypes,
            className: "Person",
            property: new PropertyModel("Name", "string", IsNullable: false, MaxLength: null));

        Assert.Contains("public string Name { get; set; }", result);
        Assert.Contains("public string Line1 { get; set; }", result);

        // Name was added to Person, not Address.
        var addressBlockStart = result.IndexOf("class Address", StringComparison.Ordinal);
        var nameIndex = result.IndexOf("Name { get; set; }", StringComparison.Ordinal);
        Assert.True(nameIndex < addressBlockStart);
    }

    private const string SourceWithNestedTypeSameName = """
        public class Person
        {
            public int Id { get; set; }

            public class Address
            {
                public string Line1 { get; set; }
            }
        }
        """;

    [Fact]
    public void AddProperty_NameMatchesOnlyNestedType_ThrowsBecauseNestedTypesAreNotEligibleTargets()
    {
        var rewriter = new EntityClassRewriter();

        Assert.Throws<InvalidOperationException>(() =>
            rewriter.AddProperty(SourceWithNestedTypeSameName, className: "Address", property: new PropertyModel("Line2", "string", IsNullable: false, MaxLength: null)));
    }
```

- [ ] **Step 2: Run tests to verify current state**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter EntityClassRewriterTests`
Expected: PASS (7 tests) — Task 1's implementation already satisfies these; this task exists to lock the guard-clause and isolation behavior in with explicit coverage rather than to add new production code.

- [ ] **Step 3: Commit**

```bash
git add tests/EfSchemaVisualizer.Core.Tests/CodeGen/EntityClassRewriterTests.cs
git commit -m "Add test coverage for EntityClassRewriter guard clause and multi-type isolation"
```

---

### Task 3: Update backlog

**Files:**
- Modify: `docs/backlog.md:83`

**Interfaces:**
- None — documentation-only change.

- [ ] **Step 1: Check off the completed backlog item**

In `docs/backlog.md`, change:

```
- [ ] **`[found]/[plan]` Add a property** to an entity (POCO class + optional config).
```

to:

```
- [x] **`[found]/[plan]` Add a property** to an entity (POCO class + optional config).
      **Update:** `EntityClassRewriter.AddProperty` appends a new auto-property
      to a class/record/struct's member list — see
      `2026-07-08-add-property-design.md`. Fluent config (e.g. max length)
      for the new property is a separate, composed call to
      `OnModelCreatingRewriter.RewriteMaxLength`, not orchestrated by this
      method. Record positional parameters are not supported — only body
      properties are synthesized.
```

- [ ] **Step 2: Run the full test suite to confirm nothing regressed**

Run: `dotnet test`
Expected: PASS (all tests, including the 7 new `EntityClassRewriterTests`).

- [ ] **Step 3: Commit**

```bash
git add docs/backlog.md
git commit -m "Check off 'add a property' in backlog"
```
