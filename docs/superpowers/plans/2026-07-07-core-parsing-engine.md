# EF Schema Visualizer — Core Parsing Engine Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Prove, with tests, that we can parse an EF Core entity class plus its `OnModelCreating` fluent configuration into an in-memory model, and surgically regenerate just one changed property's configuration without disturbing anything else in the file.

**Architecture:** A standalone .NET class library (`EfSchemaVisualizer.Core`) with no UI and no Blazor dependency. Uses Roslyn's syntax-only APIs (`CSharpSyntaxTree.ParseText`, syntax-tree traversal, `SyntaxNode.ReplaceNode`) — never `CSharpCompilation` or the scripting APIs, since those don't work under Blazor WebAssembly (see design doc). This library is what the Blazor app will reference in the next plan; nothing here depends on the browser or any UI framework, so it's fully testable with plain xUnit today.

**Tech Stack:** .NET 10, Microsoft.CodeAnalysis.CSharp (Roslyn), xUnit.

## Global Constraints

- Target framework: `net10.0` (per design doc's Blazor WebAssembly target; this library must stay WASM-compatible).
- Roslyn usage restricted to syntax-tree APIs only — no `CSharpCompilation`, no `Microsoft.CodeAnalysis.CSharp.Scripting` (per design doc's WASM compatibility finding).
- v1 scope per design doc: `OnModelCreating` fluent API style only (not `IEntityTypeConfiguration<T>` — that's a fast-follow, out of scope here).
- This plan covers exactly one property-level concern (`HasMaxLength`) end-to-end, as the design doc's flagged spike. Relationships, keys, indexes, and other fluent calls (`IsRequired`, etc.) are explicitly out of scope for this plan — they follow the same pattern established here in later plans.
- No placeholders, no TODOs — every task ships working, tested code.

---

## File Structure

```
EfSchemaVisualizer.sln
src/
  EfSchemaVisualizer.Core/
    EfSchemaVisualizer.Core.csproj
    Model/
      PropertyModel.cs
      EntityModel.cs
    Parsing/
      FluentSyntaxHelpers.cs
      EntityClassParser.cs
      MaxLengthConfig.cs
      FluentConfigParser.cs
      ModelMerger.cs
    CodeGen/
      OnModelCreatingRewriter.cs
tests/
  EfSchemaVisualizer.Core.Tests/
    EfSchemaVisualizer.Core.Tests.csproj
    Model/
      PropertyModelTests.cs
    Parsing/
      EntityClassParserTests.cs
      FluentConfigParserTests.cs
      ModelMergerTests.cs
    CodeGen/
      OnModelCreatingRewriterTests.cs
    RoundTripTests.cs
```

- `Model/` — plain data records, no logic.
- `Parsing/` — reading C# source into the model (POCO classes and `OnModelCreating` config).
- `CodeGen/` — writing model changes back into C# source.
- `FluentSyntaxHelpers` is shared internal traversal logic used by both `FluentConfigParser` (read) and `OnModelCreatingRewriter` (write), so the two don't duplicate the same Roslyn tree-walking.

---

### Task 1: Solution and project scaffolding

**Files:**
- Create: `EfSchemaVisualizer.sln`
- Create: `src/EfSchemaVisualizer.Core/EfSchemaVisualizer.Core.csproj`
- Create: `tests/EfSchemaVisualizer.Core.Tests/EfSchemaVisualizer.Core.Tests.csproj`

**Interfaces:**
- Produces: a buildable, empty solution with the two projects referenced, ready for Task 2 to add real code.

- [ ] **Step 1: Create the solution and projects**

```bash
cd /root/RiderProjects/EfSchemaVisualizer
dotnet new sln -n EfSchemaVisualizer
dotnet new classlib -n EfSchemaVisualizer.Core -o src/EfSchemaVisualizer.Core -f net10.0
dotnet new xunit -n EfSchemaVisualizer.Core.Tests -o tests/EfSchemaVisualizer.Core.Tests -f net10.0
dotnet sln add src/EfSchemaVisualizer.Core/EfSchemaVisualizer.Core.csproj
dotnet sln add tests/EfSchemaVisualizer.Core.Tests/EfSchemaVisualizer.Core.Tests.csproj
dotnet add tests/EfSchemaVisualizer.Core.Tests/EfSchemaVisualizer.Core.Tests.csproj reference src/EfSchemaVisualizer.Core/EfSchemaVisualizer.Core.csproj
```

- [ ] **Step 2: Add Roslyn to the Core project**

```bash
dotnet add src/EfSchemaVisualizer.Core/EfSchemaVisualizer.Core.csproj package Microsoft.CodeAnalysis.CSharp
```

- [ ] **Step 3: Remove the template's default `Class1.cs`**

```bash
rm src/EfSchemaVisualizer.Core/Class1.cs
```

- [ ] **Step 4: Verify the solution builds**

Run: `dotnet build`
Expected: `Build succeeded.` with 0 errors.

- [ ] **Step 5: Verify the empty test project runs**

Run: `dotnet test`
Expected: `Passed! ... - Failed: 0, Passed: 1` (the xUnit template's sample test).

- [ ] **Step 6: Remove the template's sample test and commit**

```bash
rm tests/EfSchemaVisualizer.Core.Tests/UnitTest1.cs
git add -A
git commit -m "Scaffold EfSchemaVisualizer.Core library and test project"
```

---

### Task 2: Core model records

**Files:**
- Create: `src/EfSchemaVisualizer.Core/Model/PropertyModel.cs`
- Create: `src/EfSchemaVisualizer.Core/Model/EntityModel.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Model/PropertyModelTests.cs`

**Interfaces:**
- Produces:
  - `record PropertyModel(string Name, string ClrType, bool IsNullable, int? MaxLength)`
  - `record EntityModel(string Name, IReadOnlyList<PropertyModel> Properties)`
  - Both are `namespace EfSchemaVisualizer.Core.Model`.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/EfSchemaVisualizer.Core.Tests/Model/PropertyModelTests.cs
using EfSchemaVisualizer.Core.Model;
using Xunit;

namespace EfSchemaVisualizer.Core.Tests.Model;

public class PropertyModelTests
{
    [Fact]
    public void WithMaxLength_ProducesUpdatedCopy_LeavingOriginalUnchanged()
    {
        var original = new PropertyModel("Name", "string", IsNullable: true, MaxLength: null);

        var updated = original with { MaxLength = 100 };

        Assert.Null(original.MaxLength);
        Assert.Equal(100, updated.MaxLength);
        Assert.Equal(original.Name, updated.Name);
    }

    [Fact]
    public void EntityModel_ExposesNameAndProperties()
    {
        var properties = new List<PropertyModel>
        {
            new("Id", "int", IsNullable: false, MaxLength: null),
            new("Name", "string", IsNullable: true, MaxLength: null),
        };

        var entity = new EntityModel("Person", properties);

        Assert.Equal("Person", entity.Name);
        Assert.Equal(2, entity.Properties.Count);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test --filter PropertyModelTests`
Expected: FAIL — compile error, `PropertyModel`/`EntityModel` do not exist.

- [ ] **Step 3: Implement the model records**

```csharp
// src/EfSchemaVisualizer.Core/Model/PropertyModel.cs
namespace EfSchemaVisualizer.Core.Model;

public sealed record PropertyModel(
    string Name,
    string ClrType,
    bool IsNullable,
    int? MaxLength);
```

```csharp
// src/EfSchemaVisualizer.Core/Model/EntityModel.cs
namespace EfSchemaVisualizer.Core.Model;

public sealed record EntityModel(
    string Name,
    IReadOnlyList<PropertyModel> Properties);
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test --filter PropertyModelTests`
Expected: `Passed! ... - Failed: 0, Passed: 2`

- [ ] **Step 5: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Model tests/EfSchemaVisualizer.Core.Tests/Model
git commit -m "Add EntityModel and PropertyModel records"
```

---

### Task 3: Entity class parser (read POCO properties)

**Files:**
- Create: `src/EfSchemaVisualizer.Core/Parsing/EntityClassParser.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Parsing/EntityClassParserTests.cs`

**Interfaces:**
- Consumes: `EntityModel`, `PropertyModel` from Task 2.
- Produces: `class EntityClassParser` with `public EntityModel Parse(string sourceCode)` — assumes `sourceCode` contains exactly one `class` declaration with auto-implemented properties; `namespace EfSchemaVisualizer.Core.Parsing`.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/EfSchemaVisualizer.Core.Tests/Parsing/EntityClassParserTests.cs
using EfSchemaVisualizer.Core.Parsing;
using Xunit;

namespace EfSchemaVisualizer.Core.Tests.Parsing;

public class EntityClassParserTests
{
    [Fact]
    public void Parse_ReadsClassNameAndProperties_WithNullability()
    {
        const string source = """
            public class Person
            {
                public int Id { get; set; }
                public string? Name { get; set; }
                public string Email { get; set; }
            }
            """;

        var entity = new EntityClassParser().Parse(source);

        Assert.Equal("Person", entity.Name);
        Assert.Equal(3, entity.Properties.Count);

        var id = entity.Properties.Single(p => p.Name == "Id");
        Assert.Equal("int", id.ClrType);
        Assert.False(id.IsNullable);

        var name = entity.Properties.Single(p => p.Name == "Name");
        Assert.Equal("string", name.ClrType);
        Assert.True(name.IsNullable);

        var email = entity.Properties.Single(p => p.Name == "Email");
        Assert.Equal("string", email.ClrType);
        Assert.False(email.IsNullable);
    }
}
```

Add `using System.Linq;` to the test file for `.Single(...)`.

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test --filter EntityClassParserTests`
Expected: FAIL — compile error, `EntityClassParser` does not exist.

- [ ] **Step 3: Implement the parser**

```csharp
// src/EfSchemaVisualizer.Core/Parsing/EntityClassParser.cs
using System.Linq;
using EfSchemaVisualizer.Core.Model;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EfSchemaVisualizer.Core.Parsing;

public sealed class EntityClassParser
{
    public EntityModel Parse(string sourceCode)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var classDeclaration = root.DescendantNodes()
            .OfType<ClassDeclarationSyntax>()
            .First();

        var properties = classDeclaration.Members
            .OfType<PropertyDeclarationSyntax>()
            .Select(ParseProperty)
            .ToList();

        return new EntityModel(classDeclaration.Identifier.Text, properties);
    }

    private static PropertyModel ParseProperty(PropertyDeclarationSyntax property)
    {
        var isNullable = property.Type is NullableTypeSyntax;
        var clrType = property.Type is NullableTypeSyntax nullableType
            ? nullableType.ElementType.ToString()
            : property.Type.ToString();

        return new PropertyModel(property.Identifier.Text, clrType, isNullable, MaxLength: null);
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test --filter EntityClassParserTests`
Expected: `Passed! ... - Failed: 0, Passed: 1`

- [ ] **Step 5: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Parsing/EntityClassParser.cs tests/EfSchemaVisualizer.Core.Tests/Parsing/EntityClassParserTests.cs
git commit -m "Add EntityClassParser to read POCO properties into EntityModel"
```

---

### Task 4: Shared fluent-syntax helpers + `OnModelCreating` max-length parser

**Files:**
- Create: `src/EfSchemaVisualizer.Core/Parsing/FluentSyntaxHelpers.cs`
- Create: `src/EfSchemaVisualizer.Core/Parsing/MaxLengthConfig.cs`
- Create: `src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs`
- Create: `src/EfSchemaVisualizer.Core/Parsing/ModelMerger.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Parsing/ModelMergerTests.cs`

**Interfaces:**
- Consumes: `EntityModel`, `PropertyModel` from Task 2.
- Produces:
  - `internal static class FluentSyntaxHelpers` with:
    - `IEnumerable<InvocationExpressionSyntax> FindEntityConfigInvocations(CompilationUnitSyntax root, string entityName)`
    - `IEnumerable<InvocationExpressionSyntax> FindCallsNamed(SyntaxNode scope, string methodName)`
    - `string? GetPropertyNameFor(InvocationExpressionSyntax fluentCall)`
  - `record MaxLengthConfig(string EntityName, string PropertyName, int MaxLength)`
  - `class FluentConfigParser` with `public IReadOnlyList<MaxLengthConfig> ParseMaxLengths(string sourceCode)`
  - `static class ModelMerger` with `public static EntityModel ApplyMaxLengths(EntityModel entity, IReadOnlyList<MaxLengthConfig> configs)`
  - This task's later consumer is Task 5 (`OnModelCreatingRewriter`), which reuses `FluentSyntaxHelpers`.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs
using EfSchemaVisualizer.Core.Parsing;
using Xunit;

namespace EfSchemaVisualizer.Core.Tests.Parsing;

public class FluentConfigParserTests
{
    private const string Source = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.Property(e => e.Name).HasMaxLength(100);
                    entity.Property(e => e.Email).HasMaxLength(255);
                });

                modelBuilder.Entity<Address>(entity =>
                {
                    entity.Property(e => e.Line1).HasMaxLength(200);
                });
            }
        }
        """;

    [Fact]
    public void ParseMaxLengths_ReadsEveryConfiguredProperty_AcrossMultipleEntities()
    {
        var configs = new FluentConfigParser().ParseMaxLengths(Source);

        Assert.Equal(3, configs.Count);
        Assert.Contains(configs, c => c is { EntityName: "Person", PropertyName: "Name", MaxLength: 100 });
        Assert.Contains(configs, c => c is { EntityName: "Person", PropertyName: "Email", MaxLength: 255 });
        Assert.Contains(configs, c => c is { EntityName: "Address", PropertyName: "Line1", MaxLength: 200 });
    }
}
```

```csharp
// tests/EfSchemaVisualizer.Core.Tests/Parsing/ModelMergerTests.cs
using System.Collections.Generic;
using System.Linq;
using EfSchemaVisualizer.Core.Model;
using EfSchemaVisualizer.Core.Parsing;
using Xunit;

namespace EfSchemaVisualizer.Core.Tests.Parsing;

public class ModelMergerTests
{
    [Fact]
    public void ApplyMaxLengths_SetsMaxLengthOnMatchingProperty_LeavesOthersUntouched()
    {
        var entity = new EntityModel("Person", new List<PropertyModel>
        {
            new("Id", "int", IsNullable: false, MaxLength: null),
            new("Name", "string", IsNullable: true, MaxLength: null),
        });

        var configs = new List<MaxLengthConfig>
        {
            new("Person", "Name", 100),
            new("Address", "Line1", 200), // different entity, must not affect Person
        };

        var merged = ModelMerger.ApplyMaxLengths(entity, configs);

        Assert.Null(merged.Properties.Single(p => p.Name == "Id").MaxLength);
        Assert.Equal(100, merged.Properties.Single(p => p.Name == "Name").MaxLength);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter "FluentConfigParserTests|ModelMergerTests"`
Expected: FAIL — compile errors, `FluentConfigParser`/`ModelMerger`/`MaxLengthConfig` do not exist.

- [ ] **Step 3: Implement the shared syntax helpers**

```csharp
// src/EfSchemaVisualizer.Core/Parsing/FluentSyntaxHelpers.cs
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EfSchemaVisualizer.Core.Parsing;

internal static class FluentSyntaxHelpers
{
    /// Finds every `modelBuilder.Entity&lt;{entityName}&gt;(entity => { ... })` invocation.
    public static IEnumerable<InvocationExpressionSyntax> FindEntityConfigInvocations(
        CompilationUnitSyntax root, string entityName)
    {
        return root.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(invocation => GetConfiguredEntityName(invocation) == entityName);
    }

    /// Finds every invocation named `methodName` within the given scope, e.g. all `HasMaxLength(...)` calls.
    public static IEnumerable<InvocationExpressionSyntax> FindCallsNamed(SyntaxNode scope, string methodName)
    {
        return scope.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(invocation => invocation.Expression is MemberAccessExpressionSyntax
            {
                Name.Identifier.Text: var name
            } && name == methodName);
    }

    /// Given a fluent call like `entity.Property(e => e.Name).HasMaxLength(100)`, returns "Name".
    public static string? GetPropertyNameFor(InvocationExpressionSyntax fluentCall)
    {
        if (fluentCall.Expression is not MemberAccessExpressionSyntax
            {
                Expression: InvocationExpressionSyntax propertyInvocation
            })
        {
            return null;
        }

        if (propertyInvocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Property" })
        {
            return null;
        }

        var lambdaArg = propertyInvocation.ArgumentList.Arguments
            .Select(a => a.Expression)
            .OfType<SimpleLambdaExpressionSyntax>()
            .FirstOrDefault();

        return lambdaArg?.ExpressionBody is MemberAccessExpressionSyntax { Name.Identifier.Text: var propertyName }
            ? propertyName
            : null;
    }

    private static string? GetConfiguredEntityName(InvocationExpressionSyntax invocation)
    {
        return invocation.Expression is MemberAccessExpressionSyntax
        {
            Name: GenericNameSyntax { Identifier.Text: "Entity" } generic
        }
            ? generic.TypeArgumentList.Arguments.FirstOrDefault()?.ToString()
            : null;
    }
}
```

- [ ] **Step 4: Implement `MaxLengthConfig` and `FluentConfigParser`**

```csharp
// src/EfSchemaVisualizer.Core/Parsing/MaxLengthConfig.cs
namespace EfSchemaVisualizer.Core.Parsing;

public sealed record MaxLengthConfig(string EntityName, string PropertyName, int MaxLength);
```

```csharp
// src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EfSchemaVisualizer.Core.Parsing;

public sealed class FluentConfigParser
{
    public IReadOnlyList<MaxLengthConfig> ParseMaxLengths(string sourceCode)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var results = new List<MaxLengthConfig>();

        // Distinct entity names configured anywhere in the file.
        var entityNames = root.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Select(inv => inv.Expression)
            .OfType<MemberAccessExpressionSyntax>()
            .Select(m => m.Name)
            .OfType<GenericNameSyntax>()
            .Where(g => g.Identifier.Text == "Entity")
            .Select(g => g.TypeArgumentList.Arguments.FirstOrDefault()?.ToString())
            .Where(name => name is not null)
            .Distinct()!;

        foreach (var entityName in entityNames)
        {
            foreach (var entityInvocation in FluentSyntaxHelpers.FindEntityConfigInvocations(root, entityName!))
            {
                foreach (var maxLengthCall in FluentSyntaxHelpers.FindCallsNamed(entityInvocation, "HasMaxLength"))
                {
                    var propertyName = FluentSyntaxHelpers.GetPropertyNameFor(maxLengthCall);
                    var arg = maxLengthCall.ArgumentList.Arguments.FirstOrDefault();

                    if (propertyName is null || arg is null)
                    {
                        continue;
                    }

                    if (int.TryParse(arg.Expression.ToString(), out var maxLength))
                    {
                        results.Add(new MaxLengthConfig(entityName!, propertyName, maxLength));
                    }
                }
            }
        }

        return results;
    }
}
```

- [ ] **Step 5: Implement `ModelMerger`**

```csharp
// src/EfSchemaVisualizer.Core/Parsing/ModelMerger.cs
using System.Collections.Generic;
using System.Linq;
using EfSchemaVisualizer.Core.Model;

namespace EfSchemaVisualizer.Core.Parsing;

public static class ModelMerger
{
    public static EntityModel ApplyMaxLengths(EntityModel entity, IReadOnlyList<MaxLengthConfig> configs)
    {
        var updatedProperties = entity.Properties
            .Select(property =>
            {
                var config = configs.FirstOrDefault(c =>
                    c.EntityName == entity.Name && c.PropertyName == property.Name);

                return config is null ? property : property with { MaxLength = config.MaxLength };
            })
            .ToList();

        return entity with { Properties = updatedProperties };
    }
}
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test --filter "FluentConfigParserTests|ModelMergerTests"`
Expected: `Passed! ... - Failed: 0, Passed: 2`

If `FluentConfigParserTests` fails on the entity-name traversal, check that `GetConfiguredEntityName`'s pattern match on `GenericNameSyntax` is matching `Entity<Person>` and not the outer `modelBuilder.Entity<Person>(...)` member-access chain incorrectly — add a diagnostic `Console.WriteLine` of `entityNames` in the test temporarily to inspect what was found, per systematic-debugging.

- [ ] **Step 7: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Parsing/FluentSyntaxHelpers.cs \
        src/EfSchemaVisualizer.Core/Parsing/MaxLengthConfig.cs \
        src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs \
        src/EfSchemaVisualizer.Core/Parsing/ModelMerger.cs \
        tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs \
        tests/EfSchemaVisualizer.Core.Tests/Parsing/ModelMergerTests.cs
git commit -m "Parse OnModelCreating HasMaxLength config and merge into EntityModel"
```

---

### Task 5: `OnModelCreating` surgical rewriter

**Files:**
- Create: `src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs`

**Interfaces:**
- Consumes: `FluentSyntaxHelpers` from Task 4 (`FindEntityConfigInvocations`, `FindCallsNamed`, `GetPropertyNameFor`).
- Produces: `class OnModelCreatingRewriter` with `public string RewriteMaxLength(string sourceCode, string entityName, string propertyName, int newMaxLength)` — throws `InvalidOperationException` if no matching `HasMaxLength` call is found.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs
using EfSchemaVisualizer.Core.CodeGen;
using Xunit;

namespace EfSchemaVisualizer.Core.Tests.CodeGen;

public class OnModelCreatingRewriterTests
{
    private const string Source = """
        public class AppDbContext : DbContext
        {
            // unrelated comment that must survive untouched
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.Property(e => e.Name).HasMaxLength(100);
                    entity.Property(e => e.Email).HasMaxLength(255);
                });

                modelBuilder.Entity<Address>(entity =>
                {
                    entity.Property(e => e.Line1).HasMaxLength(200);
                });
            }
        }
        """;

    [Fact]
    public void RewriteMaxLength_ChangesOnlyTargetedCall_LeavesEverythingElseIdentical()
    {
        var result = new OnModelCreatingRewriter()
            .RewriteMaxLength(Source, entityName: "Person", propertyName: "Name", newMaxLength: 150);

        Assert.Contains("entity.Property(e => e.Name).HasMaxLength(150)", result);

        // Untouched: Person.Email, Address.Line1, and the unrelated comment.
        Assert.Contains("entity.Property(e => e.Email).HasMaxLength(255)", result);
        Assert.Contains("entity.Property(e => e.Line1).HasMaxLength(200)", result);
        Assert.Contains("// unrelated comment that must survive untouched", result);

        Assert.DoesNotContain("HasMaxLength(100)", result);
    }

    [Fact]
    public void RewriteMaxLength_UnknownEntity_Throws()
    {
        var rewriter = new OnModelCreatingRewriter();

        Assert.Throws<InvalidOperationException>(() =>
            rewriter.RewriteMaxLength(Source, entityName: "Vehicle", propertyName: "Name", newMaxLength: 10));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter OnModelCreatingRewriterTests`
Expected: FAIL — compile error, `OnModelCreatingRewriter` does not exist.

- [ ] **Step 3: Implement the rewriter**

```csharp
// src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs
using System;
using System.Linq;
using EfSchemaVisualizer.Core.Parsing;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EfSchemaVisualizer.Core.CodeGen;

public sealed class OnModelCreatingRewriter
{
    public string RewriteMaxLength(string sourceCode, string entityName, string propertyName, int newMaxLength)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var targetCall = FluentSyntaxHelpers.FindEntityConfigInvocations(root, entityName)
            .SelectMany(entityInvocation => FluentSyntaxHelpers.FindCallsNamed(entityInvocation, "HasMaxLength"))
            .FirstOrDefault(call => FluentSyntaxHelpers.GetPropertyNameFor(call) == propertyName);

        if (targetCall is null)
        {
            throw new InvalidOperationException(
                $"No HasMaxLength call found for {entityName}.{propertyName}");
        }

        var newArgument = SyntaxFactory.Argument(
            SyntaxFactory.LiteralExpression(
                SyntaxKind.NumericLiteralExpression,
                SyntaxFactory.Literal(newMaxLength)));

        var newCall = targetCall.WithArgumentList(
            targetCall.ArgumentList.WithArguments(
                SyntaxFactory.SingletonSeparatedList(newArgument)));

        var newRoot = root.ReplaceNode(targetCall, newCall);
        return newRoot.ToFullString();
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter OnModelCreatingRewriterTests`
Expected: `Passed! ... - Failed: 0, Passed: 2`

- [ ] **Step 5: Commit**

```bash
git add src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs \
        tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs
git commit -m "Add OnModelCreatingRewriter for surgical HasMaxLength edits"
```

---

### Task 6: Round-trip proof (the actual spike)

**Files:**
- Test: `tests/EfSchemaVisualizer.Core.Tests/RoundTripTests.cs`

**Interfaces:**
- Consumes: `EntityClassParser` (Task 3), `FluentConfigParser` + `ModelMerger` (Task 4), `OnModelCreatingRewriter` (Task 5). No new production code — this task only proves the pieces compose correctly, which is the deliverable the design doc's risk note asked for.

- [ ] **Step 1: Write the round-trip tests**

```csharp
// tests/EfSchemaVisualizer.Core.Tests/RoundTripTests.cs
using System.Linq;
using EfSchemaVisualizer.Core.CodeGen;
using EfSchemaVisualizer.Core.Parsing;
using Xunit;

namespace EfSchemaVisualizer.Core.Tests;

public class RoundTripTests
{
    private const string EntitySource = """
        public class Person
        {
            public int Id { get; set; }
            public string Name { get; set; }
            public string? Email { get; set; }
        }
        """;

    private const string ContextSource = """
        public class AppDbContext : DbContext
        {
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
    public void Parse_Merge_NoEdit_RegeneratesConfigIdenticalToOriginal()
    {
        var baseEntity = new EntityClassParser().Parse(EntitySource);
        var configs = new FluentConfigParser().ParseMaxLengths(ContextSource);
        var merged = ModelMerger.ApplyMaxLengths(baseEntity, configs);

        Assert.Equal(100, merged.Properties.Single(p => p.Name == "Name").MaxLength);

        // Regenerating with the *same* value the model already holds must
        // produce byte-identical output to the original source.
        var nameProperty = merged.Properties.Single(p => p.Name == "Name");
        var regenerated = new OnModelCreatingRewriter()
            .RewriteMaxLength(ContextSource, "Person", "Name", nameProperty.MaxLength!.Value);

        Assert.Equal(ContextSource, regenerated);
    }

    [Fact]
    public void Parse_Edit_Regenerate_ChangesOnlyTheEditedProperty()
    {
        var configs = new FluentConfigParser().ParseMaxLengths(ContextSource);
        var addressLine1 = configs.Single(c => c is { EntityName: "Address", PropertyName: "Line1" });

        var regenerated = new OnModelCreatingRewriter()
            .RewriteMaxLength(ContextSource, "Person", "Name", newMaxLength: 150);

        // The edited entity's call changed...
        Assert.Contains("entity.Property(e => e.Name).HasMaxLength(150)", regenerated);

        // ...but the untouched entity's config, parsed fresh from the
        // regenerated source, still reports its original value.
        var configsAfter = new FluentConfigParser().ParseMaxLengths(regenerated);
        var addressLine1After = configsAfter.Single(c => c is { EntityName: "Address", PropertyName: "Line1" });
        Assert.Equal(addressLine1.MaxLength, addressLine1After.MaxLength);
    }
}
```

- [ ] **Step 2: Run the tests**

Run: `dotnet test --filter RoundTripTests`
Expected: `Passed! ... - Failed: 0, Passed: 2`

If Step 1's identical-output assertion fails, the likely cause is `ToFullString()` normalizing whitespace/trivia differently than the source — inspect the actual vs. expected diff the test runner prints and adjust `RewriteMaxLength` to preserve trivia (e.g. via `WithTriviaFrom`) rather than changing the parsing logic, per systematic-debugging: reproduce, isolate, fix the smallest thing.

- [ ] **Step 3: Run the full test suite**

Run: `dotnet test`
Expected: `Passed! ... - Failed: 0` (all tests from Tasks 2–6 passing together).

- [ ] **Step 4: Commit**

```bash
git add tests/EfSchemaVisualizer.Core.Tests/RoundTripTests.cs
git commit -m "Add round-trip tests proving isolated OnModelCreating edits"
```

---

## Self-Review Notes

- **Spec coverage:** This plan implements the design doc's flagged Open Risk ("safely rewriting one entity's slice of a shared `OnModelCreating` method... worth a small throwaway spike") end-to-end for one representative fluent call (`HasMaxLength`), across multiple entities in one file, proving isolation. It deliberately does not cover relationships, keys, indexes, or `IEntityTypeConfiguration<T>` — those are future plans once this pattern is validated, per the design doc's sequencing.
- **No placeholders:** every step has complete, runnable code.
- **Type consistency:** `PropertyModel`, `EntityModel` (Task 2) are reused unchanged through Tasks 3–6. `MaxLengthConfig` (Task 4) is consumed by `ModelMerger` (Task 4) and `RoundTripTests` (Task 6) with the same field names throughout. `FluentSyntaxHelpers` (Task 4) methods are called with matching signatures in `FluentConfigParser` (Task 4) and `OnModelCreatingRewriter` (Task 5).

## What's next (not in this plan)

Once this passes, the next plan is the Blazor WebAssembly shell: reference this library, add a minimal diagram UI (read-only render of parsed `EntityModel`s first), then wire editing to `OnModelCreatingRewriter`, then zip upload/download, then the GitHub Actions → GitHub Pages deploy. Each of those is independently testable and should be its own plan.
