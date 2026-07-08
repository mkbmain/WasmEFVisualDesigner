# Rename an Entity or Property Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add four rename methods — `EntityClassRewriter.RenameClass`, `EntityClassRewriter.RenameProperty`, `OnModelCreatingRewriter.RenameEntityReferences`, `OnModelCreatingRewriter.RenamePropertyReferences` — so an entity class or a property can be renamed while keeping the POCO, the `OnModelCreating` fluent config, and the `DbSet<T>` declaration in sync.

**Architecture:** Four independent methods on the two existing rewriter classes, following the exact patterns already established by `AddProperty`/`RemoveProperty` and `RewriteMaxLength`/`RemoveMaxLength`. Rename is a direct identifier/token swap (`WithIdentifier`/`WithName`/node replacement), not a composition of remove+add — that would reorder members and lose config values. No new types, no orchestration between the four methods — callers compose them.

**Tech Stack:** C# / .NET, `Microsoft.CodeAnalysis.CSharp` (Roslyn), xUnit.

## Global Constraints

- `EntityClassRewriter.RenameClass(sourceCode, oldClassName, newClassName)`:
  - Locates the target type using the same top-level-only rule as `AddProperty`/`RemoveProperty`: `!t.Ancestors().OfType<TypeDeclarationSyntax>().Any()`.
  - Throws `InvalidOperationException` if `oldClassName` isn't found.
  - Renames the type's `Identifier` token, and also renames any `ConstructorDeclarationSyntax` member whose name equals `oldClassName` (C# requires constructor and type names to match).
  - `NormalizeWhitespace().ToFullString()` — always edits or throws, no no-op path.
- `EntityClassRewriter.RenameProperty(sourceCode, className, oldPropertyName, newPropertyName)`:
  - Same class lookup/throw as `RenameClass`.
  - Throws `InvalidOperationException` if no `PropertyDeclarationSyntax` member named `oldPropertyName` exists on that type.
  - Renames the property's `Identifier` token. `NormalizeWhitespace().ToFullString()`.
  - Record positional parameters are out of scope — only body (member-list) properties.
- `OnModelCreatingRewriter.RenameEntityReferences(sourceCode, oldEntityName, newEntityName)`:
  - Single pass over the file: renames the type argument on every `Entity<OldName>(...)` invocation (via `FluentSyntaxHelpers.GetConfiguredEntityName`) **and** on every `DbSet<OldName>` property declaration.
  - If neither pattern matches anywhere: **no-op**, return `sourceCode` unchanged.
  - If any match: `NormalizeWhitespace().ToFullString()`.
- `OnModelCreatingRewriter.RenamePropertyReferences(sourceCode, entityName, oldPropertyName, newPropertyName)`:
  - Reuses the lookup pattern from `RewriteMaxLength`/`RemoveMaxLength`: `FluentSyntaxHelpers.FindEntityConfigInvocations` + `FindCallsNamed(..., "Property")` + `GetPropertyNameForPropertyCall`.
  - Renames whichever form matched: expression-bodied lambda (`e => e.Name`), block-bodied lambda (`e => { return e.Name; }`), or string overload (`"Name"`).
  - No matching `Property()` call: **no-op**, return `sourceCode` unchanged.
  - If found: `NormalizeWhitespace().ToFullString()`.
- No fluent config kind other than `Property`/`HasMaxLength` exists yet.
- No orchestrated "rename everywhere" entry point — callers compose the four methods.

---

### Task 1: `EntityClassRewriter.RenameClass`

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/CodeGen/EntityClassRewriter.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/CodeGen/EntityClassRewriterTests.cs`

**Interfaces:**
- Consumes: nothing new (uses `Microsoft.CodeAnalysis.CSharp` types already imported in the file).
- Produces: `EfSchemaVisualizer.Core.CodeGen.EntityClassRewriter.RenameClass(string sourceCode, string oldClassName, string newClassName) -> string`, a new public method alongside `AddProperty`/`RemoveProperty`.

- [ ] **Step 1: Write the failing tests**

Add to `tests/EfSchemaVisualizer.Core.Tests/CodeGen/EntityClassRewriterTests.cs` (inside the existing `EntityClassRewriterTests` class, after the `RemoveProperty` tests). This reuses the existing `SourceWithoutMatchingClass` (`class Vehicle`) and `SourceWithMultipleTopLevelTypes` (`class Person` + `class Address`) constants already defined earlier in the file:

```csharp
    private const string ClassWithConstructor = """
        public class Person
        {
            public Person()
            {
            }

            public int Id { get; set; }
        }
        """;

    [Fact]
    public void RenameClass_ClassWithExplicitConstructor_RenamesConstructorToo()
    {
        var result = new EntityClassRewriter().RenameClass(
            ClassWithConstructor, oldClassName: "Person", newClassName: "Customer");

        Assert.Contains("class Customer", result);
        Assert.Contains("public Customer()", result);
        Assert.DoesNotContain("Person", result);
    }

    private const string RecordForRename = """
        public record Person
        {
            public int Id { get; set; }
        }
        """;

    [Fact]
    public void RenameClass_Record_RenamesIdentifier()
    {
        var result = new EntityClassRewriter().RenameClass(
            RecordForRename, oldClassName: "Person", newClassName: "Customer");

        Assert.Contains("record Customer", result);
        Assert.DoesNotContain("Person", result);
    }

    private const string StructForRename = """
        public struct Point
        {
            public int X { get; set; }
        }
        """;

    [Fact]
    public void RenameClass_Struct_RenamesIdentifier()
    {
        var result = new EntityClassRewriter().RenameClass(
            StructForRename, oldClassName: "Point", newClassName: "Coordinate");

        Assert.Contains("struct Coordinate", result);
        Assert.DoesNotContain("Point", result);
    }

    [Fact]
    public void RenameClass_ClassNotFound_Throws()
    {
        var rewriter = new EntityClassRewriter();

        Assert.Throws<InvalidOperationException>(() =>
            rewriter.RenameClass(SourceWithoutMatchingClass, oldClassName: "Person", newClassName: "Customer"));
    }

    [Fact]
    public void RenameClass_MultipleTopLevelTypes_OnlyModifiesTargetType()
    {
        var result = new EntityClassRewriter().RenameClass(
            SourceWithMultipleTopLevelTypes, oldClassName: "Person", newClassName: "Customer");

        Assert.Contains("class Customer", result);
        Assert.Contains("class Address", result);
        Assert.DoesNotContain("class Person", result);
    }
```

- [ ] **Step 2: Run tests to verify they fail with a compile error**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter EntityClassRewriterTests`
Expected: FAIL to build — `RenameClass` does not exist yet.

- [ ] **Step 3: Implement `RenameClass`**

In `src/EfSchemaVisualizer.Core/CodeGen/EntityClassRewriter.cs`, add this method to the existing `EntityClassRewriter` class (alongside `AddProperty`/`RemoveProperty`):

```csharp
    public string RenameClass(string sourceCode, string oldClassName, string newClassName)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var targetType = root.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .Where(t => !t.Ancestors().OfType<TypeDeclarationSyntax>().Any())
            .FirstOrDefault(t => t.Identifier.Text == oldClassName)
            ?? throw new InvalidOperationException($"No top-level class, record, or struct named '{oldClassName}' found in source.");

        var newType = targetType.WithIdentifier(SyntaxFactory.Identifier(newClassName));

        newType = newType.ReplaceNodes(
            newType.Members.OfType<ConstructorDeclarationSyntax>().Where(c => c.Identifier.Text == oldClassName),
            (ctor, _) => ctor.WithIdentifier(SyntaxFactory.Identifier(newClassName)));

        var newRoot = root.ReplaceNode(targetType, newType);
        return newRoot.NormalizeWhitespace().ToFullString();
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter EntityClassRewriterTests`
Expected: PASS (17 tests: 12 existing + 5 new).

- [ ] **Step 5: Commit**

```bash
git add src/EfSchemaVisualizer.Core/CodeGen/EntityClassRewriter.cs tests/EfSchemaVisualizer.Core.Tests/CodeGen/EntityClassRewriterTests.cs
git commit -m "Add EntityClassRewriter.RenameClass for renaming an entity type"
```

---

### Task 2: `EntityClassRewriter.RenameProperty`

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/CodeGen/EntityClassRewriter.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/CodeGen/EntityClassRewriterTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: `EfSchemaVisualizer.Core.CodeGen.EntityClassRewriter.RenameProperty(string sourceCode, string className, string oldPropertyName, string newPropertyName) -> string`, a new public method alongside `RenameClass`.

- [ ] **Step 1: Write the failing tests**

Add to `tests/EfSchemaVisualizer.Core.Tests/CodeGen/EntityClassRewriterTests.cs` (after the `RenameClass` tests from Task 1). Reuses the existing `SourceWithThreeProperties` (Person: `Id`, `Name`, `Email`), `RecordWithTwoProperties` (Person: `Id`, `Name`), and `SourceWithMultipleTopLevelTypesForRemoval` (Person: `Id`, `Name`; Address: `Line1`) constants already defined earlier in the file:

```csharp
    [Fact]
    public void RenameProperty_ExistingProperty_RenamesItAndLeavesSiblingsUntouched()
    {
        var result = new EntityClassRewriter().RenameProperty(
            SourceWithThreeProperties, className: "Person", oldPropertyName: "Email", newPropertyName: "EmailAddress");

        Assert.Contains("public string EmailAddress { get; set; }", result);
        Assert.DoesNotContain("public string Email {", result);
        Assert.Contains("public int Id { get; set; }", result);
        Assert.Contains("public string Name { get; set; }", result);
        Assert.Contains("// unrelated comment that must survive", result);
    }

    [Fact]
    public void RenameProperty_RecordBodyProperty_RenamesInMemberList()
    {
        var result = new EntityClassRewriter().RenameProperty(
            RecordWithTwoProperties, className: "Person", oldPropertyName: "Name", newPropertyName: "FullName");

        Assert.Contains("public string FullName { get; set; }", result);
        Assert.Contains("public int Id { get; set; }", result);
    }

    [Fact]
    public void RenameProperty_PropertyNotFoundOnExistingClass_Throws()
    {
        var rewriter = new EntityClassRewriter();

        Assert.Throws<InvalidOperationException>(() =>
            rewriter.RenameProperty(SourceWithThreeProperties, className: "Person", oldPropertyName: "DoesNotExist", newPropertyName: "Whatever"));
    }

    [Fact]
    public void RenameProperty_ClassNotFound_Throws()
    {
        var rewriter = new EntityClassRewriter();

        Assert.Throws<InvalidOperationException>(() =>
            rewriter.RenameProperty(SourceWithThreeProperties, className: "Vehicle", oldPropertyName: "Name", newPropertyName: "Whatever"));
    }

    [Fact]
    public void RenameProperty_MultipleTopLevelTypes_OnlyModifiesTargetType()
    {
        var result = new EntityClassRewriter().RenameProperty(
            SourceWithMultipleTopLevelTypesForRemoval, className: "Person", oldPropertyName: "Name", newPropertyName: "FullName");

        Assert.Contains("public string FullName { get; set; }", result);
        Assert.Contains("public string Line1 { get; set; }", result);
        Assert.DoesNotContain("public string Name {", result);
    }
```

- [ ] **Step 2: Run tests to verify they fail with a compile error**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter EntityClassRewriterTests`
Expected: FAIL to build — `RenameProperty` does not exist yet.

- [ ] **Step 3: Implement `RenameProperty`**

In `src/EfSchemaVisualizer.Core/CodeGen/EntityClassRewriter.cs`, add this method to the existing `EntityClassRewriter` class (alongside `RenameClass`):

```csharp
    public string RenameProperty(string sourceCode, string className, string oldPropertyName, string newPropertyName)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var targetType = root.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .Where(t => !t.Ancestors().OfType<TypeDeclarationSyntax>().Any())
            .FirstOrDefault(t => t.Identifier.Text == className)
            ?? throw new InvalidOperationException($"No top-level class, record, or struct named '{className}' found in source.");

        var targetProperty = targetType.Members
            .OfType<PropertyDeclarationSyntax>()
            .FirstOrDefault(p => p.Identifier.Text == oldPropertyName)
            ?? throw new InvalidOperationException($"No property named '{oldPropertyName}' found on type '{className}'.");

        var newProperty = targetProperty.WithIdentifier(SyntaxFactory.Identifier(newPropertyName));

        var newRoot = root.ReplaceNode(targetProperty, newProperty);
        return newRoot.NormalizeWhitespace().ToFullString();
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter EntityClassRewriterTests`
Expected: PASS (22 tests: 17 from Task 1 + 5 new).

- [ ] **Step 5: Commit**

```bash
git add src/EfSchemaVisualizer.Core/CodeGen/EntityClassRewriter.cs tests/EfSchemaVisualizer.Core.Tests/CodeGen/EntityClassRewriterTests.cs
git commit -m "Add EntityClassRewriter.RenameProperty for renaming a class property"
```

---

### Task 3: `OnModelCreatingRewriter.RenameEntityReferences`

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs`

**Interfaces:**
- Consumes: `EfSchemaVisualizer.Core.Parsing.FluentSyntaxHelpers.GetConfiguredEntityName(InvocationExpressionSyntax invocation)` — pre-existing, unchanged (already used internally by `FindEntityConfigInvocations`).
- Produces: `EfSchemaVisualizer.Core.CodeGen.OnModelCreatingRewriter.RenameEntityReferences(string sourceCode, string oldEntityName, string newEntityName) -> string`, a new public method alongside `RewriteMaxLength`/`RemoveMaxLength`.

- [ ] **Step 1: Write the failing tests**

Add to `tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs` (inside the existing `OnModelCreatingRewriterTests` class, after the `RemoveMaxLength` tests):

```csharp
    private const string SourceWithEntityConfigOnly = """
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
    public void RenameEntityReferences_EntityConfigOnly_RenamesGenericTypeArgument()
    {
        var result = new OnModelCreatingRewriter()
            .RenameEntityReferences(SourceWithEntityConfigOnly, oldEntityName: "Person", newEntityName: "Customer");

        Assert.Contains("modelBuilder.Entity<Customer>(entity =>", result);
        Assert.DoesNotContain("Entity<Person>", result);
    }

    private const string SourceWithDbSetOnly = """
        public class AppDbContext : DbContext
        {
            public DbSet<Person> People { get; set; }
        }
        """;

    [Fact]
    public void RenameEntityReferences_DbSetOnly_RenamesGenericTypeArgument()
    {
        var result = new OnModelCreatingRewriter()
            .RenameEntityReferences(SourceWithDbSetOnly, oldEntityName: "Person", newEntityName: "Customer");

        Assert.Contains("public DbSet<Customer> People { get; set; }", result);
    }

    private const string SourceWithDbSetAndEntityConfig = """
        public class AppDbContext : DbContext
        {
            public DbSet<Person> People { get; set; }
            public DbSet<Address> Addresses { get; set; }

            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.Property(e => e.Name).HasMaxLength(100);
                });

                modelBuilder.Entity<Address>(entity =>
                {
                    entity.Property(e => e.Line1).HasMaxLength(200);
                });
            }
        }
        """;

    [Fact]
    public void RenameEntityReferences_BothEntityConfigAndDbSetPresent_RenamesBothInOnePass()
    {
        var result = new OnModelCreatingRewriter()
            .RenameEntityReferences(SourceWithDbSetAndEntityConfig, oldEntityName: "Person", newEntityName: "Customer");

        Assert.Contains("public DbSet<Customer> People { get; set; }", result);
        Assert.Contains("modelBuilder.Entity<Customer>(entity =>", result);
    }

    [Fact]
    public void RenameEntityReferences_NoMatchingReferences_ReturnsSourceUnchanged()
    {
        var result = new OnModelCreatingRewriter()
            .RenameEntityReferences(SourceWithDbSetAndEntityConfig, oldEntityName: "Vehicle", newEntityName: "Car");

        Assert.Equal(SourceWithDbSetAndEntityConfig, result);
    }

    [Fact]
    public void RenameEntityReferences_MultiEntitySource_SiblingEntityReferencesUntouched()
    {
        var result = new OnModelCreatingRewriter()
            .RenameEntityReferences(SourceWithDbSetAndEntityConfig, oldEntityName: "Person", newEntityName: "Customer");

        Assert.Contains("public DbSet<Address> Addresses { get; set; }", result);
        Assert.Contains("modelBuilder.Entity<Address>(entity =>", result);
        Assert.Contains("entity.Property(e => e.Line1).HasMaxLength(200)", result);
    }
```

- [ ] **Step 2: Run tests to verify they fail with a compile error**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter OnModelCreatingRewriterTests`
Expected: FAIL to build — `RenameEntityReferences` does not exist yet.

- [ ] **Step 3: Implement `RenameEntityReferences`**

In `src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs`, add `using System.Collections.Generic;` to the `using` list at the top of the file, then add this method to the `OnModelCreatingRewriter` class (alongside `RewriteMaxLength`/`RemoveMaxLength`):

```csharp
    public string RenameEntityReferences(string sourceCode, string oldEntityName, string newEntityName)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var targets = new List<IdentifierNameSyntax>();

        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (FluentSyntaxHelpers.GetConfiguredEntityName(invocation) == oldEntityName
                && invocation.Expression is MemberAccessExpressionSyntax { Name: GenericNameSyntax entityGeneric }
                && entityGeneric.TypeArgumentList.Arguments.FirstOrDefault() is IdentifierNameSyntax entityTypeArgument)
            {
                targets.Add(entityTypeArgument);
            }
        }

        foreach (var property in root.DescendantNodes().OfType<PropertyDeclarationSyntax>())
        {
            if (property.Type is GenericNameSyntax { Identifier.Text: "DbSet" } dbSetGeneric
                && dbSetGeneric.TypeArgumentList.Arguments.Count == 1
                && dbSetGeneric.TypeArgumentList.Arguments[0] is IdentifierNameSyntax dbSetTypeArgument
                && dbSetTypeArgument.Identifier.Text == oldEntityName)
            {
                targets.Add(dbSetTypeArgument);
            }
        }

        if (targets.Count == 0)
        {
            return sourceCode;
        }

        var newRoot = root.ReplaceNodes(targets, (_, _) => SyntaxFactory.IdentifierName(newEntityName));
        return newRoot.NormalizeWhitespace().ToFullString();
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter OnModelCreatingRewriterTests`
Expected: PASS (18 tests: 13 existing + 5 new).

- [ ] **Step 5: Commit**

```bash
git add src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs
git commit -m "Add OnModelCreatingRewriter.RenameEntityReferences for entity/DbSet renames"
```

---

### Task 4: `OnModelCreatingRewriter.RenamePropertyReferences`

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs`

**Interfaces:**
- Consumes: `FluentSyntaxHelpers.FindEntityConfigInvocations`, `FluentSyntaxHelpers.FindCallsNamed`, `FluentSyntaxHelpers.GetPropertyNameForPropertyCall` — all pre-existing, unchanged, already used by `RewriteMaxLength`/`RemoveMaxLength` in this same file.
- Produces: `EfSchemaVisualizer.Core.CodeGen.OnModelCreatingRewriter.RenamePropertyReferences(string sourceCode, string entityName, string oldPropertyName, string newPropertyName) -> string`, a new public method alongside `RenameEntityReferences`.

- [ ] **Step 1: Write the failing tests**

Add to `tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs` (after the `RenameEntityReferences` tests from Task 3). Reuses the existing `Source` constant at the top of the class (`Person.Name` → `HasMaxLength(100)`, `Person.Email` → `HasMaxLength(255)`, `Address.Line1` → `HasMaxLength(200)`):

```csharp
    [Fact]
    public void RenamePropertyReferences_ExpressionBodiedLambda_RenamesMemberAccess()
    {
        var result = new OnModelCreatingRewriter()
            .RenamePropertyReferences(Source, entityName: "Person", oldPropertyName: "Name", newPropertyName: "FullName");

        Assert.Contains("entity.Property(e => e.FullName).HasMaxLength(100)", result);
        Assert.Contains("entity.Property(e => e.Email).HasMaxLength(255)", result);
    }

    private const string SourceWithBlockBodiedPropertyLambda = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.Property(e =>
                    {
                        return e.Name;
                    }).HasMaxLength(100);
                });
            }
        }
        """;

    [Fact]
    public void RenamePropertyReferences_BlockBodiedLambda_RenamesReturnedMemberAccess()
    {
        var result = new OnModelCreatingRewriter()
            .RenamePropertyReferences(SourceWithBlockBodiedPropertyLambda, entityName: "Person", oldPropertyName: "Name", newPropertyName: "FullName");

        Assert.Contains("return e.FullName;", result);
        Assert.DoesNotContain("return e.Name;", result);
    }

    private const string SourceWithStringOverload = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.Property("Name").HasMaxLength(100);
                });
            }
        }
        """;

    [Fact]
    public void RenamePropertyReferences_StringOverload_RenamesLiteral()
    {
        var result = new OnModelCreatingRewriter()
            .RenamePropertyReferences(SourceWithStringOverload, entityName: "Person", oldPropertyName: "Name", newPropertyName: "FullName");

        Assert.Contains("entity.Property(\"FullName\").HasMaxLength(100)", result);
    }

    [Fact]
    public void RenamePropertyReferences_NoMatchingCall_ReturnsSourceUnchanged()
    {
        var result = new OnModelCreatingRewriter()
            .RenamePropertyReferences(Source, entityName: "Person", oldPropertyName: "DoesNotExist", newPropertyName: "Whatever");

        Assert.Equal(Source, result);
    }

    [Fact]
    public void RenamePropertyReferences_MultiEntitySource_OnlyRenamesTargetEntitysProperty()
    {
        var result = new OnModelCreatingRewriter()
            .RenamePropertyReferences(Source, entityName: "Person", oldPropertyName: "Name", newPropertyName: "FullName");

        Assert.Contains("entity.Property(e => e.FullName).HasMaxLength(100)", result);
        Assert.Contains("entity.Property(e => e.Line1).HasMaxLength(200)", result);
    }
```

- [ ] **Step 2: Run tests to verify they fail with a compile error**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter OnModelCreatingRewriterTests`
Expected: FAIL to build — `RenamePropertyReferences` does not exist yet.

- [ ] **Step 3: Implement `RenamePropertyReferences`**

In `src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs`, add this method to the `OnModelCreatingRewriter` class (alongside `RenameEntityReferences`):

```csharp
    public string RenamePropertyReferences(string sourceCode, string entityName, string oldPropertyName, string newPropertyName)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var entityInvocations = FluentSyntaxHelpers.FindEntityConfigInvocations(root, entityName).ToList();

        var existingPropertyCall = entityInvocations
            .SelectMany(entityInvocation => FluentSyntaxHelpers.FindCallsNamed(entityInvocation, "Property"))
            .FirstOrDefault(call => FluentSyntaxHelpers.GetPropertyNameForPropertyCall(call) == oldPropertyName);

        if (existingPropertyCall is null)
        {
            return sourceCode;
        }

        var argumentExpression = existingPropertyCall.ArgumentList.Arguments.Single().Expression;

        ArgumentSyntax newArgument;

        if (argumentExpression is SimpleLambdaExpressionSyntax { ExpressionBody: MemberAccessExpressionSyntax expressionBodyAccess } exprLambda)
        {
            var newLambda = exprLambda.WithExpressionBody(expressionBodyAccess.WithName(SyntaxFactory.IdentifierName(newPropertyName)));
            newArgument = SyntaxFactory.Argument(newLambda);
        }
        else if (argumentExpression is SimpleLambdaExpressionSyntax { Block: BlockSyntax block } blockLambda
            && block.Statements is [ReturnStatementSyntax { Expression: MemberAccessExpressionSyntax blockAccess } returnStatement])
        {
            var newReturnStatement = returnStatement.WithExpression(blockAccess.WithName(SyntaxFactory.IdentifierName(newPropertyName)));
            var newBlock = block.WithStatements(SyntaxFactory.SingletonList<StatementSyntax>(newReturnStatement));
            var newLambda = blockLambda.WithBlock(newBlock);
            newArgument = SyntaxFactory.Argument(newLambda);
        }
        else if (argumentExpression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression))
        {
            var newLiteral = SyntaxFactory.LiteralExpression(
                SyntaxKind.StringLiteralExpression,
                SyntaxFactory.Literal(newPropertyName));
            newArgument = SyntaxFactory.Argument(newLiteral);
        }
        else
        {
            throw new InvalidOperationException($"Unsupported Property() argument shape for '{oldPropertyName}'.");
        }

        var newCall = existingPropertyCall.WithArgumentList(
            existingPropertyCall.ArgumentList.WithArguments(SyntaxFactory.SingletonSeparatedList(newArgument)));

        var newRoot = root.ReplaceNode(existingPropertyCall, newCall);
        return newRoot.NormalizeWhitespace().ToFullString();
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter OnModelCreatingRewriterTests`
Expected: PASS (23 tests: 18 from Task 3 + 5 new).

- [ ] **Step 5: Commit**

```bash
git add src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs
git commit -m "Add OnModelCreatingRewriter.RenamePropertyReferences for property renames"
```

---

### Task 5: Update backlog

**Files:**
- Modify: `docs/backlog.md:99`

**Interfaces:**
- None — documentation-only change.

- [ ] **Step 1: Check off the completed backlog item**

In `docs/backlog.md`, change:

```
- [ ] **`[plan]` Rename** an entity or property (class member + every referencing fluent call & lambda body).
```

to:

```
- [x] **`[plan]` Rename** an entity or property (class member + every referencing fluent call & lambda body).
      **Update:** `EntityClassRewriter.RenameClass`/`RenameProperty` rename
      the POCO side; `OnModelCreatingRewriter.RenameEntityReferences`/
      `RenamePropertyReferences` fix `Entity<T>`/`DbSet<T>` type arguments
      and `Property()` lambda/string references (see
      `2026-07-08-rename-entity-property-design.md`). Four separate
      composed calls, not one orchestrated operation, matching the
      add/drop-property split. Record positional parameters and
      free-text references outside these patterns remain out of scope.
```

(Note: verify the exact current line text with `grep -n "Rename" docs/backlog.md` before editing, since line numbers may have shifted since this plan was written.)

- [ ] **Step 2: Run the full test suite to confirm nothing regressed**

Run: `dotnet test`
Expected: PASS (all tests, including the 10 new `EntityClassRewriterTests` and 10 new `OnModelCreatingRewriterTests`).

- [ ] **Step 3: Commit**

```bash
git add docs/backlog.md
git commit -m "Check off 'rename an entity or property' in backlog"
```
