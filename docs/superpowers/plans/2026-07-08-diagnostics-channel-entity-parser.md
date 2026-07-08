# Diagnostics Channel & EntityClassParser Rewrite Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a diagnostics channel to the parsing engine and rewrite `EntityClassParser` so it never throws on a class-less file, reads multiple classes/records/structs per file (including record positional-parameter properties), and filters out non-mapped properties (`[NotMapped]`, `static`, get-only/computed).

**Architecture:** Introduce `Diagnostic` (a single reported issue with a stable `Code`, message, optional entity/property name, and source `TextSpan`) and a generic `ParseResult<T>` wrapper (`Value` + `IReadOnlyList<Diagnostic> Diagnostics`). `EntityClassParser.Parse` changes from returning a single `EntityModel` to `ParseResult<IReadOnlyList<EntityModel>>`. The parser walks all `TypeDeclarationSyntax` nodes that are `ClassDeclarationSyntax`, `StructDeclarationSyntax`, or `RecordDeclarationSyntax` (this naturally excludes `InterfaceDeclarationSyntax`, which also derives from `TypeDeclarationSyntax`), builds properties from both primary-constructor parameters and body-declared properties, and filters non-mapped members before emitting each `EntityModel`.

**Tech Stack:** C# / .NET 10, Roslyn (`Microsoft.CodeAnalysis.CSharp` 5.6.0), xUnit.

## Global Constraints

- `FluentConfigParser` and `FluentSyntaxHelpers` are **out of scope** — do not modify them in this plan. Their fixes are separate follow-up plans.
- `ModelMerger.ApplyMaxLengths` signature (`EntityModel` in, `EntityModel` out) does **not** change — callers now loop over the list `EntityClassParser.Parse` returns.
- No symbol/semantic resolution — all checks (e.g. `[NotMapped]`) are syntactic, matching the existing codebase's syntax-only approach.
- Follow existing repo conventions: one type per file under `EfSchemaVisualizer.Core.Parsing`, `sealed record`/`sealed class`, xUnit `[Fact]` tests with C# raw string literals (`"""`) for source fixtures, matching the style in `EntityClassParserTests.cs` and `RoundTripTests.cs`.

---

### Task 1: Diagnostics types + wrap `EntityClassParser.Parse` in `ParseResult`

**Files:**
- Create: `src/EfSchemaVisualizer.Core/Parsing/Diagnostic.cs`
- Create: `src/EfSchemaVisualizer.Core/Parsing/ParseResult.cs`
- Modify: `src/EfSchemaVisualizer.Core/Parsing/EntityClassParser.cs`
- Modify: `tests/EfSchemaVisualizer.Core.Tests/Parsing/EntityClassParserTests.cs`
- Modify: `tests/EfSchemaVisualizer.Core.Tests/RoundTripTests.cs`

**Interfaces:**
- Produces: `Diagnostic(string Code, string Message, string? EntityName, string? PropertyName, TextSpan Span)`; `ParseResult<T>(T Value, IReadOnlyList<Diagnostic> Diagnostics)`; `EntityClassParser.Parse(string sourceCode) : ParseResult<IReadOnlyList<EntityModel>>`.

- [ ] **Step 1: Write the failing test — update `EntityClassParserTests`**

Replace the whole file:

```csharp
using System.Linq;
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

        var result = new EntityClassParser().Parse(source);

        Assert.Empty(result.Diagnostics);

        var entity = result.Value.Single();
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

- [ ] **Step 2: Update `RoundTripTests` to unwrap `ParseResult`**

In `tests/EfSchemaVisualizer.Core.Tests/RoundTripTests.cs`, change line 40 from:

```csharp
        var baseEntity = new EntityClassParser().Parse(EntitySource);
```

to:

```csharp
        var baseEntity = new EntityClassParser().Parse(EntitySource).Value.Single();
```

Leave the rest of the file unchanged.

- [ ] **Step 3: Run tests to verify they fail to compile**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests/EfSchemaVisualizer.Core.Tests.csproj`
Expected: Build errors — `Diagnostic`/`ParseResult<T>` don't exist yet, and `EntityClassParser.Parse` still returns `EntityModel` (no `.Value`/`.Diagnostics` members).

- [ ] **Step 4: Create `Diagnostic.cs`**

```csharp
using Microsoft.CodeAnalysis.Text;

namespace EfSchemaVisualizer.Core.Parsing;

public sealed record Diagnostic(
    string Code,
    string Message,
    string? EntityName,
    string? PropertyName,
    TextSpan Span);
```

- [ ] **Step 5: Create `ParseResult.cs`**

```csharp
using System.Collections.Generic;

namespace EfSchemaVisualizer.Core.Parsing;

public sealed record ParseResult<T>(T Value, IReadOnlyList<Diagnostic> Diagnostics);
```

- [ ] **Step 6: Rewrite `EntityClassParser.cs` to wrap the existing single-class behavior**

This step only changes the return type/shape — multi-class, class-less, record, and filtering behavior come in later tasks.

```csharp
using System.Collections.Generic;
using System.Linq;
using EfSchemaVisualizer.Core.Model;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EfSchemaVisualizer.Core.Parsing;

public sealed class EntityClassParser
{
    public ParseResult<IReadOnlyList<EntityModel>> Parse(string sourceCode)
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

        var entity = new EntityModel(classDeclaration.Identifier.Text, properties);

        return new ParseResult<IReadOnlyList<EntityModel>>(
            new List<EntityModel> { entity },
            new List<Diagnostic>());
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

- [ ] **Step 7: Run tests to verify they pass**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests/EfSchemaVisualizer.Core.Tests.csproj`
Expected: All tests PASS.

- [ ] **Step 8: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Parsing/Diagnostic.cs src/EfSchemaVisualizer.Core/Parsing/ParseResult.cs src/EfSchemaVisualizer.Core/Parsing/EntityClassParser.cs tests/EfSchemaVisualizer.Core.Tests/Parsing/EntityClassParserTests.cs tests/EfSchemaVisualizer.Core.Tests/RoundTripTests.cs
git commit -m "Add diagnostics channel and wrap EntityClassParser.Parse in ParseResult"
```

---

### Task 2: Class-less file — no exception, emit a diagnostic

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/Parsing/EntityClassParser.cs`
- Modify: `tests/EfSchemaVisualizer.Core.Tests/Parsing/EntityClassParserTests.cs`

**Interfaces:**
- Consumes: `Diagnostic`, `ParseResult<T>` from Task 1.
- Produces: `EntityClassParser.Parse` returns an empty entity list plus a `Diagnostic` with `Code == "NoEntityDeclarations"` when the file has no class declaration (structs/records not yet handled — that's Task 4).

- [ ] **Step 1: Write the failing test**

Add to `EntityClassParserTests.cs`:

```csharp
    [Fact]
    public void Parse_ClassLessFile_ReturnsEmptyListAndDiagnostic_NoException()
    {
        const string source = """
            public enum Status
            {
                Active,
                Inactive
            }
            """;

        var result = new EntityClassParser().Parse(source);

        Assert.Empty(result.Value);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("NoEntityDeclarations", diagnostic.Code);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests/EfSchemaVisualizer.Core.Tests.csproj --filter Parse_ClassLessFile_ReturnsEmptyListAndDiagnostic_NoException`
Expected: FAIL — current code throws `InvalidOperationException` from `.First()`.

- [ ] **Step 3: Fix `EntityClassParser.Parse`**

Replace the body of `Parse` in `EntityClassParser.cs`:

```csharp
    public ParseResult<IReadOnlyList<EntityModel>> Parse(string sourceCode)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var classDeclarations = root.DescendantNodes()
            .OfType<ClassDeclarationSyntax>()
            .ToList();

        if (classDeclarations.Count == 0)
        {
            var diagnostic = new Diagnostic(
                "NoEntityDeclarations",
                "No class, record, or struct declarations found in file; nothing to parse.",
                EntityName: null,
                PropertyName: null,
                root.Span);

            return new ParseResult<IReadOnlyList<EntityModel>>(
                new List<EntityModel>(),
                new List<Diagnostic> { diagnostic });
        }

        var entities = classDeclarations.Select(ParseEntity).ToList();

        return new ParseResult<IReadOnlyList<EntityModel>>(entities, new List<Diagnostic>());
    }

    private static EntityModel ParseEntity(ClassDeclarationSyntax classDeclaration)
    {
        var properties = classDeclaration.Members
            .OfType<PropertyDeclarationSyntax>()
            .Select(ParseProperty)
            .ToList();

        return new EntityModel(classDeclaration.Identifier.Text, properties);
    }
```

Remove the old inline single-class logic from `Parse` (it's now in `ParseEntity`).

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests/EfSchemaVisualizer.Core.Tests.csproj`
Expected: All tests PASS, including the new one.

- [ ] **Step 5: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Parsing/EntityClassParser.cs tests/EfSchemaVisualizer.Core.Tests/Parsing/EntityClassParserTests.cs
git commit -m "Handle class-less files gracefully with a diagnostic instead of throwing"
```

---

### Task 3: Multiple classes per file

**Files:**
- Modify: `tests/EfSchemaVisualizer.Core.Tests/Parsing/EntityClassParserTests.cs`

No production code change is needed — Task 2's `classDeclarations.Select(ParseEntity).ToList()` already handles every class in the file. This task exists to lock the behavior in with a test.

**Interfaces:**
- Consumes: `EntityClassParser.Parse` from Task 2 (already returns one `EntityModel` per class found).

- [ ] **Step 1: Write the test**

Add to `EntityClassParserTests.cs`:

```csharp
    [Fact]
    public void Parse_MultipleClassesInOneFile_ReturnsOneEntityPerClass()
    {
        const string source = """
            public class Person
            {
                public int Id { get; set; }
            }

            public class Address
            {
                public int Id { get; set; }
                public string Line1 { get; set; }
            }
            """;

        var result = new EntityClassParser().Parse(source);

        Assert.Empty(result.Diagnostics);
        Assert.Equal(2, result.Value.Count);
        Assert.Contains(result.Value, e => e.Name == "Person" && e.Properties.Count == 1);
        Assert.Contains(result.Value, e => e.Name == "Address" && e.Properties.Count == 2);
    }
```

- [ ] **Step 2: Run test to verify it passes**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests/EfSchemaVisualizer.Core.Tests.csproj --filter Parse_MultipleClassesInOneFile_ReturnsOneEntityPerClass`
Expected: PASS (no production change was required).

- [ ] **Step 3: Commit**

```bash
git add tests/EfSchemaVisualizer.Core.Tests/Parsing/EntityClassParserTests.cs
git commit -m "Add test locking in multiple-classes-per-file support"
```

---

### Task 4: Record and struct entities + primary-constructor-parameter properties

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/Parsing/EntityClassParser.cs`
- Modify: `tests/EfSchemaVisualizer.Core.Tests/Parsing/EntityClassParserTests.cs`

**Interfaces:**
- Produces: `EntityClassParser.Parse` now walks `ClassDeclarationSyntax`, `StructDeclarationSyntax`, and `RecordDeclarationSyntax` nodes (interfaces are excluded since `InterfaceDeclarationSyntax` is not in that list). Properties for a type = primary-constructor parameters (if any) followed by body-declared `PropertyDeclarationSyntax` members, in that order.

- [ ] **Step 1: Write the failing tests**

Add to `EntityClassParserTests.cs`:

```csharp
    [Fact]
    public void Parse_PositionalRecord_ReadsParametersAsProperties()
    {
        const string source = """
            public record Product(int Id, string Name);
            """;

        var result = new EntityClassParser().Parse(source);

        var entity = result.Value.Single();
        Assert.Equal("Product", entity.Name);
        Assert.Equal(2, entity.Properties.Count);
        Assert.Equal("int", entity.Properties.Single(p => p.Name == "Id").ClrType);
        Assert.Equal("string", entity.Properties.Single(p => p.Name == "Name").ClrType);
    }

    [Fact]
    public void Parse_RecordWithPositionalAndBodyProperties_MergesBothInOrder()
    {
        const string source = """
            public record Product(int Id, string Name)
            {
                public decimal Price { get; set; }
            }
            """;

        var result = new EntityClassParser().Parse(source);

        var entity = result.Value.Single();
        Assert.Equal(new[] { "Id", "Name", "Price" }, entity.Properties.Select(p => p.Name));
    }

    [Fact]
    public void Parse_StructEntity_IsRead()
    {
        const string source = """
            public struct Point
            {
                public int X { get; set; }
                public int Y { get; set; }
            }
            """;

        var result = new EntityClassParser().Parse(source);

        var entity = result.Value.Single();
        Assert.Equal("Point", entity.Name);
        Assert.Equal(2, entity.Properties.Count);
    }

    [Fact]
    public void Parse_InterfaceAlongsideClass_OnlyClassBecomesEntity_NoDiagnostic()
    {
        const string source = """
            public interface IAudited
            {
                DateTime CreatedAt { get; }
            }

            public class Person
            {
                public int Id { get; set; }
            }
            """;

        var result = new EntityClassParser().Parse(source);

        Assert.Empty(result.Diagnostics);
        var entity = Assert.Single(result.Value);
        Assert.Equal("Person", entity.Name);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests/EfSchemaVisualizer.Core.Tests.csproj`
Expected: The four new tests FAIL (records/structs aren't walked yet; positional parameters aren't read).

- [ ] **Step 3: Rewrite `EntityClassParser.cs` to walk all entity-like type declarations**

Replace the whole file:

```csharp
using System.Collections.Generic;
using System.Linq;
using EfSchemaVisualizer.Core.Model;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EfSchemaVisualizer.Core.Parsing;

public sealed class EntityClassParser
{
    public ParseResult<IReadOnlyList<EntityModel>> Parse(string sourceCode)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var typeDeclarations = root.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .Where(t => t is ClassDeclarationSyntax or StructDeclarationSyntax or RecordDeclarationSyntax)
            .ToList();

        if (typeDeclarations.Count == 0)
        {
            var diagnostic = new Diagnostic(
                "NoEntityDeclarations",
                "No class, record, or struct declarations found in file; nothing to parse.",
                EntityName: null,
                PropertyName: null,
                root.Span);

            return new ParseResult<IReadOnlyList<EntityModel>>(
                new List<EntityModel>(),
                new List<Diagnostic> { diagnostic });
        }

        var entities = typeDeclarations.Select(ParseEntity).ToList();

        return new ParseResult<IReadOnlyList<EntityModel>>(entities, new List<Diagnostic>());
    }

    private static EntityModel ParseEntity(TypeDeclarationSyntax typeDeclaration)
    {
        var positionalProperties = typeDeclaration.ParameterList?.Parameters
            .Select(ParseParameterProperty) ?? Enumerable.Empty<PropertyModel>();

        var bodyProperties = typeDeclaration.Members
            .OfType<PropertyDeclarationSyntax>()
            .Select(ParseProperty);

        var properties = positionalProperties.Concat(bodyProperties).ToList();

        return new EntityModel(typeDeclaration.Identifier.Text, properties);
    }

    private static PropertyModel ParseParameterProperty(ParameterSyntax parameter)
    {
        var type = parameter.Type!;
        var isNullable = type is NullableTypeSyntax nullableType;
        var clrType = type is NullableTypeSyntax nullable
            ? nullable.ElementType.ToString()
            : type.ToString();

        return new PropertyModel(parameter.Identifier.Text, clrType, isNullable, MaxLength: null);
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

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests/EfSchemaVisualizer.Core.Tests.csproj`
Expected: All tests PASS.

- [ ] **Step 5: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Parsing/EntityClassParser.cs tests/EfSchemaVisualizer.Core.Tests/Parsing/EntityClassParserTests.cs
git commit -m "Support record and struct entities, including positional-parameter properties"
```

---

### Task 5: Property filtering — `[NotMapped]`, `static`, get-only/computed

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/Parsing/EntityClassParser.cs`
- Modify: `tests/EfSchemaVisualizer.Core.Tests/Parsing/EntityClassParserTests.cs`

**Interfaces:**
- Produces: `ParseEntity` only emits body properties that are mapped instance properties: no `[NotMapped]` attribute, not `static`, and have a settable accessor (`set` or `init`) — a `get`-only accessor or an expression-bodied (`=>`) property is excluded. Positional-parameter properties are always emitted unchanged by this task (filtering `[property: NotMapped]` on positional parameters is not implemented — out of scope, no test covers it).

- [ ] **Step 1: Write the failing tests**

Add to `EntityClassParserTests.cs`:

```csharp
    [Fact]
    public void Parse_NotMappedProperty_IsExcluded()
    {
        const string source = """
            using System.ComponentModel.DataAnnotations.Schema;

            public class Person
            {
                public int Id { get; set; }

                [NotMapped]
                public string ScratchNote { get; set; }
            }
            """;

        var result = new EntityClassParser().Parse(source);

        var entity = result.Value.Single();
        Assert.Single(entity.Properties);
        Assert.Equal("Id", entity.Properties[0].Name);
    }

    [Fact]
    public void Parse_StaticProperty_IsExcluded()
    {
        const string source = """
            public class Person
            {
                public int Id { get; set; }
                public static int InstanceCount { get; set; }
            }
            """;

        var result = new EntityClassParser().Parse(source);

        var entity = result.Value.Single();
        Assert.Single(entity.Properties);
        Assert.Equal("Id", entity.Properties[0].Name);
    }

    [Fact]
    public void Parse_GetOnlyProperty_IsExcluded()
    {
        const string source = """
            public class Person
            {
                public int Id { get; set; }
                public string ReadOnlyLabel { get; }
            }
            """;

        var result = new EntityClassParser().Parse(source);

        var entity = result.Value.Single();
        Assert.Single(entity.Properties);
        Assert.Equal("Id", entity.Properties[0].Name);
    }

    [Fact]
    public void Parse_ExpressionBodiedProperty_IsExcluded()
    {
        const string source = """
            public class Person
            {
                public int Id { get; set; }
                public string First { get; set; }
                public string Last { get; set; }
                public string FullName => First + " " + Last;
            }
            """;

        var result = new EntityClassParser().Parse(source);

        var entity = result.Value.Single();
        Assert.Equal(new[] { "Id", "First", "Last" }, entity.Properties.Select(p => p.Name));
    }

    [Fact]
    public void Parse_InitOnlyProperty_IsIncluded()
    {
        const string source = """
            public class Person
            {
                public int Id { get; init; }
            }
            """;

        var result = new EntityClassParser().Parse(source);

        var entity = result.Value.Single();
        Assert.Single(entity.Properties);
        Assert.Equal("Id", entity.Properties[0].Name);
    }
```

- [ ] **Step 2: Run tests to verify the new ones fail**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests/EfSchemaVisualizer.Core.Tests.csproj`
Expected: `Parse_NotMappedProperty_IsExcluded`, `Parse_StaticProperty_IsExcluded`, `Parse_GetOnlyProperty_IsExcluded`, `Parse_ExpressionBodiedProperty_IsExcluded` FAIL (properties not yet filtered); `Parse_InitOnlyProperty_IsIncluded` already PASSES (documents current behavior, locks it in going forward).

- [ ] **Step 3: Add filtering to `EntityClassParser.cs`**

In `ParseEntity`, change:

```csharp
        var bodyProperties = typeDeclaration.Members
            .OfType<PropertyDeclarationSyntax>()
            .Select(ParseProperty);
```

to:

```csharp
        var bodyProperties = typeDeclaration.Members
            .OfType<PropertyDeclarationSyntax>()
            .Where(IsMappedInstanceProperty)
            .Select(ParseProperty);
```

Add these two new private static methods to the class (near `ParseProperty`):

```csharp
    private static bool IsMappedInstanceProperty(PropertyDeclarationSyntax property)
    {
        if (property.Modifiers.Any(SyntaxKind.StaticKeyword))
        {
            return false;
        }

        if (HasNotMappedAttribute(property.AttributeLists))
        {
            return false;
        }

        if (property.ExpressionBody is not null)
        {
            return false;
        }

        var hasSetter = property.AccessorList?.Accessors
            .Any(a => a.Kind() is SyntaxKind.SetAccessorDeclaration or SyntaxKind.InitAccessorDeclaration) ?? false;

        return hasSetter;
    }

    private static bool HasNotMappedAttribute(SyntaxList<AttributeListSyntax> attributeLists)
    {
        return attributeLists
            .SelectMany(list => list.Attributes)
            .Any(attribute => attribute.Name.ToString() is "NotMapped" or "NotMappedAttribute");
    }
```

Add `using Microsoft.CodeAnalysis;` at the top of the file if `SyntaxList<>` isn't already resolvable (it lives in `Microsoft.CodeAnalysis`, while `SyntaxKind` lives in `Microsoft.CodeAnalysis.CSharp` which is already imported).

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests/EfSchemaVisualizer.Core.Tests.csproj`
Expected: All tests PASS.

- [ ] **Step 5: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Parsing/EntityClassParser.cs tests/EfSchemaVisualizer.Core.Tests/Parsing/EntityClassParserTests.cs
git commit -m "Filter NotMapped, static, and get-only/computed properties out of parsed entities"
```

---

### Task 6: Update the backlog

**Files:**
- Modify: `docs/backlog.md`

- [ ] **Step 1: Check off the completed Priority 0 items**

In `docs/backlog.md`, change these five checkboxes under "Priority 0" from `- [ ]` to `- [x]`:
- "Parser silently drops config it doesn't understand — add a diagnostics / "unsupported" channel." (note: channel exists now; `FluentConfigParser` doesn't populate it yet — leave a trailing note, see below)
- "`EntityClassParser.Parse` throws on a class-less file."
- "Record and struct entities are invisible."
- "No property filtering."

Leave unchecked (out of scope for this plan): "Hardcoded `modelBuilder` receiver name", "`Property` string-overload and block-bodied lambdas not read", "Non-literal `HasMaxLength` arguments dropped silently".

For the diagnostics-channel item, append a note rather than fully checking it off, since only `EntityClassParser` populates it so far:

```markdown
- [ ] **`[found]` Parser silently drops config it doesn't understand — add a diagnostics / "unsupported" channel.**
      Today anything the parser can't model is discarded with no signal. A user
      would see an incomplete model and not know it. Introduce an
      `UnsupportedConfig` / diagnostics list on the parse result so the UI can
      warn "N fluent calls could not be read" rather than silently hiding them.
      This is the single most important trust fix.
      **Update:** `Diagnostic`/`ParseResult<T>` now exist and `EntityClassParser`
      populates them (see `2026-07-08-diagnostics-channel-entity-parser-design.md`).
      `FluentConfigParser` still needs to be wired in — tracked by the
      remaining unchecked P0 items below.
```

- [ ] **Step 2: Commit**

```bash
git add docs/backlog.md
git commit -m "Check off P0 backlog items completed by the diagnostics/EntityClassParser pass"
```
