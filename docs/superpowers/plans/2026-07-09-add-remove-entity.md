# Add / Remove an Entity Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add four methods — `EntityClassRewriter.AddClass`, `EntityClassRewriter.RemoveClass`, `OnModelCreatingRewriter.AddEntity`, `OnModelCreatingRewriter.RemoveEntity` — so a whole new entity (POCO class + `DbSet<T>` property + `Entity<T>(...)` config block) can be minted or removed without disturbing sibling entities.

**Architecture:** Four independent methods on the two existing rewriter classes, following the exact split already established by rename/add/drop-property: POCO-side and config-side stay separate composed calls, not one orchestrated operation. `OnModelCreatingRewriter.AddEntity`/`RemoveEntity` each handle both the `DbSet<T>` property and the `Entity<T>(...)` block together in one pass (mirroring how `RenameEntityReferences` already treats both forms as one "entity identity" concern), since both live in the same `DbContext` file. This plan also refactors the existing private `InsertEntityBlock` helper to share its `Entity<T>(...)` statement construction with the new `AddEntity` method, instead of duplicating it.

**Tech Stack:** C# / .NET, `Microsoft.CodeAnalysis.CSharp` (Roslyn), xUnit.

## Global Constraints

- `EntityClassRewriter.AddClass(sourceCode, className)`:
  - Synthesizes an empty `public class {className} { }` and appends it as the **last top-level member** of the compilation unit (or the only member, if none exist yet).
  - No collision detection — blindly appends even if a type with that name already exists.
  - `NormalizeWhitespace().ToFullString()` — always edits, no no-op path.
- `EntityClassRewriter.RemoveClass(sourceCode, className)`:
  - Locates the target type using the existing `FindTopLevelType` helper (top-level-only rule: `!t.Ancestors().OfType<TypeDeclarationSyntax>().Any()`), already shared by `AddProperty`/`RemoveProperty`/`RenameClass`/`RenameProperty`.
  - Throws `InvalidOperationException` if `className` isn't found (`FindTopLevelType` already does this).
  - Removes the type declaration from the compilation unit's member list, `NormalizeWhitespace().ToFullString()`.
- `OnModelCreatingRewriter.AddEntity(sourceCode, entityName, dbSetPropertyName)`:
  - `dbSetPropertyName` is a required explicit parameter — no pluralization inference.
  - Locates `OnModelCreating` the same way the existing private `InsertEntityBlock` does; throws `InvalidOperationException` if no such method exists, or if it has no body.
  - In one pass over the type declaration containing `OnModelCreating`: appends an empty `{modelBuilderParam}.Entity<{entityName}>(entity => { });` statement to the method body, **and** appends a `public DbSet<{entityName}> {dbSetPropertyName} { get; set; }` property as the last member of that same type.
  - No duplicate detection — blindly appends even if the entity already has a `DbSet<T>`/`Entity<T>()`.
  - `NormalizeWhitespace().ToFullString()` — always edits, no no-op path.
- `OnModelCreatingRewriter.RemoveEntity(sourceCode, entityName)`:
  - Reuses `RenameEntityReferences`'s existing target-collection logic (`FluentSyntaxHelpers.GetConfiguredEntityName` for `Entity<T>(...)` invocations, `DbSet<T>` property-declaration matching) to find every match for `entityName`.
  - For each matched `Entity<T>(...)` invocation, removes the enclosing `ExpressionStatementSyntax` — only the bare, unchained statement shape (`invocation.Parent is ExpressionStatementSyntax`) is handled.
  - For each matched `DbSet<T>` property declaration, removes that property member.
  - If nothing matches: **no-op**, return `sourceCode` unchanged (an entity configured purely by convention, with no explicit `Entity<T>()` call or no `DbSet<T>`, is normal — not an error).
  - If anything matches: remove all matched nodes in one `RemoveNodes` pass, `NormalizeWhitespace().ToFullString()`.
- No orchestrated "add/remove an entity everywhere" entry point — callers compose `AddClass`+`AddEntity` (or `RemoveClass`+`RemoveEntity`) themselves.
- No fluent config kind other than `Property`/`HasMaxLength` exists yet.

---

### Task 1: `EntityClassRewriter.AddClass`

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/CodeGen/EntityClassRewriter.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/CodeGen/EntityClassRewriterTests.cs`

**Interfaces:**
- Consumes: nothing new (uses `Microsoft.CodeAnalysis.CSharp` types already imported in the file).
- Produces: `EfSchemaVisualizer.Core.CodeGen.EntityClassRewriter.AddClass(string sourceCode, string className) -> string`, a new public method alongside `AddProperty`/`RemoveProperty`/`RenameClass`/`RenameProperty`.

- [ ] **Step 1: Write the failing tests**

Add to `tests/EfSchemaVisualizer.Core.Tests/CodeGen/EntityClassRewriterTests.cs` (inside the existing `EntityClassRewriterTests` class, after the `RenameProperty` tests). Reuses the existing `SourceWithMultipleTopLevelTypes` constant (`class Person` + `class Address`) already defined earlier in the file:

```csharp
    [Fact]
    public void AddClass_FileWithExistingClasses_AppendsNewClassAsLastMember()
    {
        var result = new EntityClassRewriter().AddClass(SourceWithMultipleTopLevelTypes, className: "Order");

        Assert.Contains("public class Order", result);
        Assert.Contains("class Person", result);
        Assert.Contains("class Address", result);

        var addressIndex = result.IndexOf("class Address", StringComparison.Ordinal);
        var orderIndex = result.IndexOf("class Order", StringComparison.Ordinal);
        Assert.True(orderIndex > addressIndex);
    }

    private const string EmptyFile = "";

    [Fact]
    public void AddClass_FileWithNoTopLevelTypes_NewClassBecomesOnlyMember()
    {
        var result = new EntityClassRewriter().AddClass(EmptyFile, className: "Person");

        Assert.Contains("public class Person", result);
    }
```

- [ ] **Step 2: Run tests to verify they fail with a compile error**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter EntityClassRewriterTests`
Expected: FAIL to build — `AddClass` does not exist yet.

- [ ] **Step 3: Implement `AddClass`**

In `src/EfSchemaVisualizer.Core/CodeGen/EntityClassRewriter.cs`, add this method to the existing `EntityClassRewriter` class (alongside `AddProperty`/`RemoveProperty`/`RenameClass`/`RenameProperty`):

```csharp
    public string AddClass(string sourceCode, string className)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var newClass = SyntaxFactory.ClassDeclaration(className)
            .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword));

        var newRoot = root.AddMembers(newClass);
        return newRoot.NormalizeWhitespace().ToFullString();
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter EntityClassRewriterTests`
Expected: PASS (24 tests: 22 existing + 2 new).

- [ ] **Step 5: Commit**

```bash
git add src/EfSchemaVisualizer.Core/CodeGen/EntityClassRewriter.cs tests/EfSchemaVisualizer.Core.Tests/CodeGen/EntityClassRewriterTests.cs
git commit -m "Add EntityClassRewriter.AddClass for minting a new entity class"
```

---

### Task 2: `EntityClassRewriter.RemoveClass`

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/CodeGen/EntityClassRewriter.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/CodeGen/EntityClassRewriterTests.cs`

**Interfaces:**
- Consumes: the existing private `FindTopLevelType(CompilationUnitSyntax root, string className)` helper in this file (already used by `AddProperty`/`RemoveProperty`/`RenameClass`/`RenameProperty`).
- Produces: `EfSchemaVisualizer.Core.CodeGen.EntityClassRewriter.RemoveClass(string sourceCode, string className) -> string`, a new public method alongside `AddClass`.

- [ ] **Step 1: Write the failing tests**

Add to `tests/EfSchemaVisualizer.Core.Tests/CodeGen/EntityClassRewriterTests.cs` (after the `AddClass` tests from Task 1). Reuses the existing `SourceWithMultipleTopLevelTypes`, `SourceWithoutMatchingClass` (`class Vehicle`), and `SourceWithEmptyClassBody` (`class Person` with an empty body) constants already defined earlier in the file:

```csharp
    [Fact]
    public void RemoveClass_MultipleTopLevelTypes_OnlyRemovesTargetType()
    {
        var result = new EntityClassRewriter().RemoveClass(SourceWithMultipleTopLevelTypes, className: "Address");

        Assert.DoesNotContain("class Address", result);
        Assert.Contains("class Person", result);
    }

    [Fact]
    public void RemoveClass_ClassNotFound_Throws()
    {
        var rewriter = new EntityClassRewriter();

        Assert.Throws<InvalidOperationException>(() =>
            rewriter.RemoveClass(SourceWithoutMatchingClass, className: "Person"));
    }

    [Fact]
    public void RemoveClass_OnlyClassInFile_LeavesEmptyCompilationUnit()
    {
        var result = new EntityClassRewriter().RemoveClass(SourceWithEmptyClassBody, className: "Person");

        Assert.DoesNotContain("class Person", result);
        Assert.Equal(string.Empty, result.Trim());
    }
```

- [ ] **Step 2: Run tests to verify they fail with a compile error**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter EntityClassRewriterTests`
Expected: FAIL to build — `RemoveClass` does not exist yet.

- [ ] **Step 3: Implement `RemoveClass`**

In `src/EfSchemaVisualizer.Core/CodeGen/EntityClassRewriter.cs`, add this method to the existing `EntityClassRewriter` class (alongside `AddClass`):

```csharp
    public string RemoveClass(string sourceCode, string className)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var targetType = FindTopLevelType(root, className);

        var newRoot = root.RemoveNode(targetType, SyntaxRemoveOptions.KeepNoTrivia)!;
        return newRoot.NormalizeWhitespace().ToFullString();
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter EntityClassRewriterTests`
Expected: PASS (27 tests: 24 from Task 1 + 3 new).

- [ ] **Step 5: Commit**

```bash
git add src/EfSchemaVisualizer.Core/CodeGen/EntityClassRewriter.cs tests/EfSchemaVisualizer.Core.Tests/CodeGen/EntityClassRewriterTests.cs
git commit -m "Add EntityClassRewriter.RemoveClass for deleting an entity class"
```

---

### Task 3: `OnModelCreatingRewriter.AddEntity`

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs`

**Interfaces:**
- Consumes: nothing new from `FluentSyntaxHelpers`. Refactors the existing private `InsertEntityBlock` method (used internally by `RewriteMaxLength`) to share statement construction with the new public method, via two new private helpers on this class: `FindOnModelCreatingMethod(CompilationUnitSyntax root) -> MethodDeclarationSyntax` and `BuildEntityInvocationStatement(string modelBuilderParamName, string entityName, BlockSyntax block) -> ExpressionStatementSyntax`.
- Produces: `EfSchemaVisualizer.Core.CodeGen.OnModelCreatingRewriter.AddEntity(string sourceCode, string entityName, string dbSetPropertyName) -> string`, a new public method alongside `RenamePropertyReferences`.

- [ ] **Step 1: Write the failing tests**

Add to `tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs` (inside the existing `OnModelCreatingRewriterTests` class, after the `RenamePropertyReferences` tests). Reuses the existing `SourceWithDbSetOnly` constant (`public class AppDbContext : DbContext { public DbSet<Person> People { get; set; } }`, no `OnModelCreating` method) already defined earlier in the file:

```csharp
    private const string SourceWithExistingEntityForAddEntity = """
        public class AppDbContext : DbContext
        {
            public DbSet<Person> People { get; set; }

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
    public void AddEntity_ExistingEntityPresent_AppendsNewDbSetAndEmptyEntityBlock()
    {
        var result = new OnModelCreatingRewriter()
            .AddEntity(SourceWithExistingEntityForAddEntity, entityName: "Address", dbSetPropertyName: "Addresses");

        Assert.Contains("public DbSet<Address> Addresses { get; set; }", result);
        Assert.Contains("modelBuilder.Entity<Address>(entity =>", result);

        // Existing entity untouched.
        Assert.Contains("public DbSet<Person> People { get; set; }", result);
        Assert.Contains("entity.Property(e => e.Name).HasMaxLength(100)", result);
    }

    [Fact]
    public void AddEntity_NoOnModelCreatingMethod_Throws()
    {
        var rewriter = new OnModelCreatingRewriter();

        Assert.Throws<InvalidOperationException>(() =>
            rewriter.AddEntity(SourceWithDbSetOnly, entityName: "Address", dbSetPropertyName: "Addresses"));
    }
```

- [ ] **Step 2: Run tests to verify they fail with a compile error**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter OnModelCreatingRewriterTests`
Expected: FAIL to build — `AddEntity` does not exist yet.

- [ ] **Step 3: Refactor `InsertEntityBlock` and implement `AddEntity`**

In `src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs`, replace the existing private `InsertEntityBlock` method:

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

with this refactored version, plus the two new shared helpers and the new public `AddEntity` method:

```csharp
    private static string InsertEntityBlock(CompilationUnitSyntax root, string entityName, string propertyName, int newMaxLength)
    {
        var method = FindOnModelCreatingMethod(root);

        var methodBody = method.Body
            ?? throw new InvalidOperationException("OnModelCreating has no method body.");

        var modelBuilderParamName = method.ParameterList.Parameters.Single().Identifier.Text;

        var propertyStatement = BuildPropertyStatement("entity", "e", propertyName, newMaxLength);
        var entityBlockStatement = BuildEntityInvocationStatement(modelBuilderParamName, entityName, SyntaxFactory.Block(propertyStatement));

        var newMethodBody = methodBody.AddStatements(entityBlockStatement);
        var newRoot = root.ReplaceNode(methodBody, newMethodBody);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    public string AddEntity(string sourceCode, string entityName, string dbSetPropertyName)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var method = FindOnModelCreatingMethod(root);

        var methodBody = method.Body
            ?? throw new InvalidOperationException("OnModelCreating has no method body.");

        var modelBuilderParamName = method.ParameterList.Parameters.Single().Identifier.Text;
        var containingClass = method.Ancestors().OfType<TypeDeclarationSyntax>().First();

        var entityBlockStatement = BuildEntityInvocationStatement(modelBuilderParamName, entityName, SyntaxFactory.Block());
        var newMethodBody = methodBody.AddStatements(entityBlockStatement);
        var classWithNewMethod = containingClass.ReplaceNode(methodBody, newMethodBody);

        var dbSetProperty = BuildDbSetProperty(entityName, dbSetPropertyName);
        var classWithBoth = classWithNewMethod.AddMembers(dbSetProperty);

        var newRoot = root.ReplaceNode(containingClass, classWithBoth);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    private static MethodDeclarationSyntax FindOnModelCreatingMethod(CompilationUnitSyntax root)
    {
        return root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(m => m.Identifier.Text == "OnModelCreating")
            ?? throw new InvalidOperationException("No OnModelCreating method found in source.");
    }

    private static ExpressionStatementSyntax BuildEntityInvocationStatement(string modelBuilderParamName, string entityName, BlockSyntax block)
    {
        return SyntaxFactory.ExpressionStatement(
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
                                block))))));
    }

    private static PropertyDeclarationSyntax BuildDbSetProperty(string entityName, string dbSetPropertyName)
    {
        var dbSetType = SyntaxFactory.GenericName(SyntaxFactory.Identifier("DbSet"))
            .WithTypeArgumentList(
                SyntaxFactory.TypeArgumentList(
                    SyntaxFactory.SingletonSeparatedList<TypeSyntax>(
                        SyntaxFactory.IdentifierName(entityName))));

        return SyntaxFactory.PropertyDeclaration(dbSetType, dbSetPropertyName)
            .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword))
            .AddAccessorListAccessors(
                SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                    .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)),
                SyntaxFactory.AccessorDeclaration(SyntaxKind.SetAccessorDeclaration)
                    .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)));
    }
```

Note: this leaves the class with two structurally-identical `Entity<T>(...)` construction sites gone — `InsertEntityBlock` now calls `BuildEntityInvocationStatement` instead of building the invocation inline — and introduces `FindOnModelCreatingMethod` as the single place that looks up `OnModelCreating`, used by both `InsertEntityBlock` and `AddEntity`.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter OnModelCreatingRewriterTests`
Expected: PASS (25 tests: 23 existing + 2 new). Also run the full `OnModelCreatingRewriterTests` filter (not just new tests) to confirm the `InsertEntityBlock` refactor didn't break `RewriteMaxLength_UnknownEntity_InsertsNewEntityBlock` or any other existing test.

- [ ] **Step 5: Commit**

```bash
git add src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs
git commit -m "Add OnModelCreatingRewriter.AddEntity for minting a DbSet + Entity<T> block"
```

---

### Task 4: `OnModelCreatingRewriter.RemoveEntity`

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs`

**Interfaces:**
- Consumes: `EfSchemaVisualizer.Core.Parsing.FluentSyntaxHelpers.GetConfiguredEntityName(InvocationExpressionSyntax invocation)` — pre-existing, unchanged, same lookup `RenameEntityReferences` already uses.
- Produces: `EfSchemaVisualizer.Core.CodeGen.OnModelCreatingRewriter.RemoveEntity(string sourceCode, string entityName) -> string`, a new public method alongside `AddEntity`.

- [ ] **Step 1: Write the failing tests**

Add to `tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs` (after the `AddEntity` tests from Task 3). Reuses the existing `SourceWithDbSetOnly`, `SourceWithEntityConfigOnly`, and `SourceWithDbSetAndEntityConfig` constants already defined earlier in the file:

```csharp
    [Fact]
    public void RemoveEntity_DbSetOnly_RemovesProperty()
    {
        var result = new OnModelCreatingRewriter().RemoveEntity(SourceWithDbSetOnly, entityName: "Person");

        Assert.DoesNotContain("DbSet<Person>", result);
    }

    [Fact]
    public void RemoveEntity_EntityConfigOnly_RemovesStatement()
    {
        var result = new OnModelCreatingRewriter().RemoveEntity(SourceWithEntityConfigOnly, entityName: "Person");

        Assert.DoesNotContain("Entity<Person>", result);
    }

    [Fact]
    public void RemoveEntity_BothDbSetAndEntityConfigPresent_RemovesBothInOnePass()
    {
        var result = new OnModelCreatingRewriter().RemoveEntity(SourceWithDbSetAndEntityConfig, entityName: "Person");

        Assert.DoesNotContain("DbSet<Person>", result);
        Assert.DoesNotContain("Entity<Person>", result);

        // Sibling untouched.
        Assert.Contains("public DbSet<Address> Addresses { get; set; }", result);
        Assert.Contains("modelBuilder.Entity<Address>(entity =>", result);
    }

    [Fact]
    public void RemoveEntity_NoMatchingReferences_ReturnsSourceUnchanged()
    {
        var result = new OnModelCreatingRewriter().RemoveEntity(SourceWithDbSetAndEntityConfig, entityName: "Vehicle");

        Assert.Equal(SourceWithDbSetAndEntityConfig, result);
    }

    [Fact]
    public void RemoveEntity_MultiEntitySource_SiblingEntityDbSetAndConfigUntouched()
    {
        var result = new OnModelCreatingRewriter().RemoveEntity(SourceWithDbSetAndEntityConfig, entityName: "Address");

        Assert.DoesNotContain("DbSet<Address>", result);
        Assert.DoesNotContain("Entity<Address>", result);

        Assert.Contains("public DbSet<Person> People { get; set; }", result);
        Assert.Contains("modelBuilder.Entity<Person>(entity =>", result);
        Assert.Contains("entity.Property(e => e.Name).HasMaxLength(100)", result);
    }
```

- [ ] **Step 2: Run tests to verify they fail with a compile error**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter OnModelCreatingRewriterTests`
Expected: FAIL to build — `RemoveEntity` does not exist yet.

- [ ] **Step 3: Implement `RemoveEntity`**

In `src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs`, add this method to the `OnModelCreatingRewriter` class (alongside `AddEntity`). `using System.Collections.Generic;` is already present in this file's `using` list from Task 3 of the rename-entity-property plan, so no new `using` is needed:

```csharp
    public string RemoveEntity(string sourceCode, string entityName)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var nodesToRemove = new List<SyntaxNode>();

        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (FluentSyntaxHelpers.GetConfiguredEntityName(invocation) == entityName
                && invocation.Parent is ExpressionStatementSyntax statement)
            {
                nodesToRemove.Add(statement);
            }
        }

        foreach (var property in root.DescendantNodes().OfType<PropertyDeclarationSyntax>())
        {
            if (property.Type is GenericNameSyntax { Identifier.Text: "DbSet" } dbSetGeneric
                && dbSetGeneric.TypeArgumentList.Arguments.Count == 1
                && dbSetGeneric.TypeArgumentList.Arguments[0] is IdentifierNameSyntax dbSetTypeArgument
                && dbSetTypeArgument.Identifier.Text == entityName)
            {
                nodesToRemove.Add(property);
            }
        }

        if (nodesToRemove.Count == 0)
        {
            return sourceCode;
        }

        var newRoot = root.RemoveNodes(nodesToRemove, SyntaxRemoveOptions.KeepNoTrivia)!;
        return newRoot.NormalizeWhitespace().ToFullString();
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter OnModelCreatingRewriterTests`
Expected: PASS (30 tests: 25 from Task 3 + 5 new).

- [ ] **Step 5: Commit**

```bash
git add src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs
git commit -m "Add OnModelCreatingRewriter.RemoveEntity for deleting a DbSet + Entity<T> block"
```

---

### Task 5: Update backlog

**Files:**
- Modify: `docs/backlog.md`

**Interfaces:**
- None — documentation-only change.

- [ ] **Step 1: Check off the completed backlog item**

In `docs/backlog.md`, change:

```
- [ ] **`[plan]` Add / remove an entity** — mint a whole new `modelBuilder.Entity<T>(...)` block, or remove one, without disturbing siblings.
```

to:

```
- [x] **`[plan]` Add / remove an entity** — mint a whole new `modelBuilder.Entity<T>(...)` block, or remove one, without disturbing siblings.
      **Update:** `EntityClassRewriter.AddClass`/`RemoveClass` mint or
      delete the POCO side; `OnModelCreatingRewriter.AddEntity`/
      `RemoveEntity` mint or delete the `DbSet<T>` property and the
      `Entity<T>(...)` config block together in one pass each (see
      `2026-07-09-add-remove-entity-design.md`). Two separate composed
      calls, not one orchestrated operation, matching the
      rename/add/drop-property split. No duplicate/collision detection on
      either `Add*` method; `RemoveEntity` only handles the bare,
      unchained `Entity<T>(...)` statement shape.
```

(Note: verify the exact current line text with `grep -n "Add / remove an entity" docs/backlog.md` before editing, since line numbers may have shifted since this plan was written.)

- [ ] **Step 2: Run the full test suite to confirm nothing regressed**

Run: `dotnet test`
Expected: PASS (all tests, including the 5 new `EntityClassRewriterTests` and 7 new `OnModelCreatingRewriterTests` — 85 total, up from 73).

- [ ] **Step 3: Commit**

```bash
git add docs/backlog.md
git commit -m "Check off 'add / remove an entity' in backlog"
```
