# Drop a Property Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add `EntityClassRewriter.RemoveProperty` (deletes a property from a class/record/struct) and `OnModelCreatingRewriter.RemoveMaxLength` (strips a `.HasMaxLength(...)` call for a property, leaving the bare `Property()` call behind).

**Architecture:** Two independent methods on the two existing rewriter classes, following the exact patterns already established by `EntityClassRewriter.AddProperty` and `OnModelCreatingRewriter.RewriteMaxLength`. No new types, no orchestration between the two methods — callers compose them.

**Tech Stack:** C# / .NET, `Microsoft.CodeAnalysis.CSharp` (Roslyn), xUnit.

## Global Constraints

- `EntityClassRewriter.RemoveProperty(sourceCode, className, propertyName)`:
  - Locates the target type using the same top-level-only rule as `AddProperty`/`EntityClassParser.Parse`: `!t.Ancestors().OfType<TypeDeclarationSyntax>().Any()`.
  - Throws `InvalidOperationException` if the class isn't found.
  - Throws `InvalidOperationException` if no `PropertyDeclarationSyntax` member named `propertyName` exists on that type.
  - Removes the member, then `NormalizeWhitespace().ToFullString()`.
- `OnModelCreatingRewriter.RemoveMaxLength(sourceCode, entityName, propertyName)`:
  - Reuses the existing lookup pattern from `RewriteMaxLength`: `FluentSyntaxHelpers.FindEntityConfigInvocations` + `FindCallsNamed(..., "HasMaxLength")` + `GetPropertyNameFor`.
  - If no matching `HasMaxLength` call exists: **no-op**, return `sourceCode` unchanged (not an error).
  - If found: strip only the `.HasMaxLength(...)` segment, leaving the underlying `Property()` call in place. Do not delete the statement.
  - `NormalizeWhitespace().ToFullString()` on the edited tree (only reached when a call was actually stripped).
- No fluent config kind other than `HasMaxLength` exists yet — `RemoveMaxLength` is the only config-removal method needed.
- Record positional parameters are out of scope for `RemoveProperty` — only body (member-list) properties are removable.

---

### Task 1: `EntityClassRewriter.RemoveProperty`

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/CodeGen/EntityClassRewriter.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/CodeGen/EntityClassRewriterTests.cs`

**Interfaces:**
- Consumes: nothing new (uses `Microsoft.CodeAnalysis.CSharp` types already imported in the file).
- Produces: `EfSchemaVisualizer.Core.CodeGen.EntityClassRewriter.RemoveProperty(string sourceCode, string className, string propertyName) -> string`, a new public method alongside the existing `AddProperty`.

- [ ] **Step 1: Write the failing tests**

Add to `tests/EfSchemaVisualizer.Core.Tests/CodeGen/EntityClassRewriterTests.cs` (inside the existing `EntityClassRewriterTests` class):

```csharp
    private const string SourceWithThreeProperties = """
        public class Person
        {
            // unrelated comment that must survive
            public int Id { get; set; }
            public string Name { get; set; }
            public string Email { get; set; }
        }
        """;

    [Fact]
    public void RemoveProperty_ExistingProperty_RemovesItAndLeavesSiblingsUntouched()
    {
        var result = new EntityClassRewriter().RemoveProperty(
            SourceWithThreeProperties, className: "Person", propertyName: "Email");

        Assert.DoesNotContain("Email", result);
        Assert.Contains("public int Id { get; set; }", result);
        Assert.Contains("public string Name { get; set; }", result);
        Assert.Contains("// unrelated comment that must survive", result);
    }

    private const string RecordWithTwoProperties = """
        public record Person
        {
            public int Id { get; set; }
            public string Name { get; set; }
        }
        """;

    [Fact]
    public void RemoveProperty_RecordBodyProperty_RemovesFromMemberList()
    {
        var result = new EntityClassRewriter().RemoveProperty(
            RecordWithTwoProperties, className: "Person", propertyName: "Name");

        Assert.DoesNotContain("Name", result);
        Assert.Contains("public int Id { get; set; }", result);
    }

    [Fact]
    public void RemoveProperty_PropertyNotFoundOnExistingClass_Throws()
    {
        var rewriter = new EntityClassRewriter();

        Assert.Throws<InvalidOperationException>(() =>
            rewriter.RemoveProperty(SourceWithThreeProperties, className: "Person", propertyName: "DoesNotExist"));
    }

    [Fact]
    public void RemoveProperty_ClassNotFound_Throws()
    {
        var rewriter = new EntityClassRewriter();

        Assert.Throws<InvalidOperationException>(() =>
            rewriter.RemoveProperty(SourceWithThreeProperties, className: "Vehicle", propertyName: "Name"));
    }

    private const string SourceWithMultipleTopLevelTypesForRemoval = """
        public class Person
        {
            public int Id { get; set; }
            public string Name { get; set; }
        }

        public class Address
        {
            public string Line1 { get; set; }
        }
        """;

    [Fact]
    public void RemoveProperty_MultipleTopLevelTypes_OnlyModifiesTargetType()
    {
        var result = new EntityClassRewriter().RemoveProperty(
            SourceWithMultipleTopLevelTypesForRemoval, className: "Person", propertyName: "Name");

        Assert.DoesNotContain("Name", result);
        Assert.Contains("public int Id { get; set; }", result);
        Assert.Contains("public string Line1 { get; set; }", result);
    }
```

- [ ] **Step 2: Run tests to verify they fail with a compile error**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter EntityClassRewriterTests`
Expected: FAIL to build — `RemoveProperty` does not exist yet.

- [ ] **Step 3: Implement `RemoveProperty`**

In `src/EfSchemaVisualizer.Core/CodeGen/EntityClassRewriter.cs`, add this method to the existing `EntityClassRewriter` class (alongside `AddProperty`):

```csharp
    public string RemoveProperty(string sourceCode, string className, string propertyName)
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
            .FirstOrDefault(p => p.Identifier.Text == propertyName)
            ?? throw new InvalidOperationException($"No property named '{propertyName}' found on type '{className}'.");

        var newType = targetType.RemoveNode(targetProperty, SyntaxRemoveOptions.KeepNoTrivia)!;

        var newRoot = root.ReplaceNode(targetType, newType);
        return newRoot.NormalizeWhitespace().ToFullString();
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter EntityClassRewriterTests`
Expected: PASS (12 tests: 7 existing + 5 new).

- [ ] **Step 5: Commit**

```bash
git add src/EfSchemaVisualizer.Core/CodeGen/EntityClassRewriter.cs tests/EfSchemaVisualizer.Core.Tests/CodeGen/EntityClassRewriterTests.cs
git commit -m "Add EntityClassRewriter.RemoveProperty for deleting a property from a class or record"
```

---

### Task 2: `OnModelCreatingRewriter.RemoveMaxLength`

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs`

**Interfaces:**
- Consumes: `EfSchemaVisualizer.Core.Parsing.FluentSyntaxHelpers.FindEntityConfigInvocations(CompilationUnitSyntax root, string entityName)`, `FluentSyntaxHelpers.FindCallsNamed(SyntaxNode scope, string methodName)`, `FluentSyntaxHelpers.GetPropertyNameFor(InvocationExpressionSyntax fluentCall)` — all pre-existing, unchanged, already used by `RewriteMaxLength` in this same file.
- Produces: `EfSchemaVisualizer.Core.CodeGen.OnModelCreatingRewriter.RemoveMaxLength(string sourceCode, string entityName, string propertyName) -> string`, a new public method alongside the existing `RewriteMaxLength`.

- [ ] **Step 1: Write the failing tests**

Add to `tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs` (inside the existing `OnModelCreatingRewriterTests` class; it already has a `Source` constant at the top of the class with `Person.Name` (`HasMaxLength(100)`), `Person.Email` (`HasMaxLength(255)`), and `Address.Line1` (`HasMaxLength(200)`) — reuse it):

```csharp
    [Fact]
    public void RemoveMaxLength_ExistingCall_StripsHasMaxLengthLeavesBarePropertyCall()
    {
        var result = new OnModelCreatingRewriter()
            .RemoveMaxLength(Source, entityName: "Person", propertyName: "Name");

        Assert.Contains("entity.Property(e => e.Name);", result);
        Assert.DoesNotContain("HasMaxLength(100)", result);

        // Untouched: Person.Email, Address.Line1.
        Assert.Contains("entity.Property(e => e.Email).HasMaxLength(255)", result);
        Assert.Contains("entity.Property(e => e.Line1).HasMaxLength(200)", result);
    }

    [Fact]
    public void RemoveMaxLength_NoMatchingCall_ReturnsSourceUnchanged()
    {
        var result = new OnModelCreatingRewriter()
            .RemoveMaxLength(Source, entityName: "Person", propertyName: "DoesNotExist");

        Assert.Equal(Source, result);
    }

    [Fact]
    public void RemoveMaxLength_EntityHasNoConfigAtAll_ReturnsSourceUnchanged()
    {
        var result = new OnModelCreatingRewriter()
            .RemoveMaxLength(Source, entityName: "Vehicle", propertyName: "Name");

        Assert.Equal(Source, result);
    }

    [Fact]
    public void RemoveMaxLength_MultiEntitySource_OnlyStripsTargetEntitysCall()
    {
        var result = new OnModelCreatingRewriter()
            .RemoveMaxLength(Source, entityName: "Address", propertyName: "Line1");

        Assert.Contains("entity.Property(e => e.Line1);", result);
        Assert.DoesNotContain("HasMaxLength(200)", result);

        // Person's calls are untouched.
        Assert.Contains("entity.Property(e => e.Name).HasMaxLength(100)", result);
        Assert.Contains("entity.Property(e => e.Email).HasMaxLength(255)", result);
    }
```

- [ ] **Step 2: Run tests to verify they fail with a compile error**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter OnModelCreatingRewriterTests`
Expected: FAIL to build — `RemoveMaxLength` does not exist yet.

- [ ] **Step 3: Implement `RemoveMaxLength`**

In `src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs`, add this method to the existing `OnModelCreatingRewriter` class (alongside `RewriteMaxLength`):

```csharp
    public string RemoveMaxLength(string sourceCode, string entityName, string propertyName)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var entityInvocations = FluentSyntaxHelpers.FindEntityConfigInvocations(root, entityName).ToList();

        var existingMaxLengthCall = entityInvocations
            .SelectMany(entityInvocation => FluentSyntaxHelpers.FindCallsNamed(entityInvocation, "HasMaxLength"))
            .FirstOrDefault(call => FluentSyntaxHelpers.GetPropertyNameFor(call) == propertyName);

        if (existingMaxLengthCall is null)
        {
            return sourceCode;
        }

        var propertyCallExpression = ((MemberAccessExpressionSyntax)existingMaxLengthCall.Expression).Expression;

        var newRoot = root.ReplaceNode(existingMaxLengthCall, propertyCallExpression);
        return newRoot.NormalizeWhitespace().ToFullString();
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter OnModelCreatingRewriterTests`
Expected: PASS (13 tests: 9 existing + 4 new).

- [ ] **Step 5: Commit**

```bash
git add src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs
git commit -m "Add OnModelCreatingRewriter.RemoveMaxLength for stripping fluent config off a dropped property"
```

---

### Task 3: Update backlog

**Files:**
- Modify: `docs/backlog.md:84`

**Interfaces:**
- None — documentation-only change.

- [ ] **Step 1: Check off the completed backlog item**

In `docs/backlog.md`, change:

```
- [ ] **`[found]/[plan]` Drop a property** (remove from class and remove any of its config statements).
```

to:

```
- [x] **`[found]/[plan]` Drop a property** (remove from class and remove any of its config statements).
      **Update:** `EntityClassRewriter.RemoveProperty` deletes the class
      member; `OnModelCreatingRewriter.RemoveMaxLength` strips a matching
      `.HasMaxLength(...)` call, leaving the bare `Property()` call in
      place (see `2026-07-08-drop-property-design.md`). The two are
      separate composed calls, not one orchestrated operation, matching
      the `AddProperty`/config-insertion split. No config kind other than
      `HasMaxLength` exists yet to remove.
```

- [ ] **Step 2: Run the full test suite to confirm nothing regressed**

Run: `dotnet test`
Expected: PASS (all tests, including the 5 new `EntityClassRewriterTests` and 4 new `OnModelCreatingRewriterTests`).

- [ ] **Step 3: Commit**

```bash
git add docs/backlog.md
git commit -m "Check off 'drop a property' in backlog"
```
