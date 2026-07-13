# `IEntityTypeConfiguration<T>` Support Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make all 10 `FluentConfigParser.Parse*` methods recognize `IEntityTypeConfiguration<T>` config classes (`class X : IEntityTypeConfiguration<T> { void Configure(EntityTypeBuilder<T> builder) {...} }`) transparently, alongside the existing `modelBuilder.Entity<T>(...)` style, with zero changes to public method signatures.

**Architecture:** A new `FluentSyntaxHelpers.FindConfigurationScopes` helper discovers `(entityName, scope)` pairs from either shape in one pass. Every `Parse*` method is refactored to loop over this helper instead of its own duplicated entity-name-scan boilerplate. `FindCallsNamed`/`GetPropertyNameFor` and all other call-reading primitives are reused unchanged, since they already operate on a generic `SyntaxNode` scope. `ModelMerger` needs no changes.

**Tech Stack:** C#, .NET 10, Roslyn syntax-tree APIs (`Microsoft.CodeAnalysis.CSharp`), xUnit.

## Global Constraints

- Roslyn usage restricted to syntax-tree APIs only — no `CSharpCompilation`, no `Microsoft.CodeAnalysis.CSharp.Scripting` (WASM compatibility requirement from the design doc).
- No new public API surface — `Parse*` method signatures are unchanged.
- No rewriter work in this plan — parsing only, per the design doc's scope decision.
- No placeholders, no TODOs — every task ships working, tested code.
- Every task ends with `dotnet test` passing for the full suite before moving on.

---

## File Structure

- Modify: `src/EfSchemaVisualizer.Core/Parsing/FluentSyntaxHelpers.cs` — add `FindConfigurationScopes` and `TryGetEntityTypeConfigurationEntityName`.
- Modify: `src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs` — refactor all 10 `Parse*` methods to use the new helper.
- Modify: `tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentSyntaxHelpersTests.cs` — tests for `FindConfigurationScopes`.
- Modify: `tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs` — new `IEntityTypeConfiguration<T>`-shaped test cases.
- Modify: `docs/backlog.md` — tick off the Priority 3 item with an `Update:` note (final task).

---

### Task 1: `FindConfigurationScopes` helper in `FluentSyntaxHelpers`

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/Parsing/FluentSyntaxHelpers.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentSyntaxHelpersTests.cs`

**Interfaces:**
- Consumes: `GetConfiguredEntityName(InvocationExpressionSyntax)`, `FindEntityConfigInvocations(CompilationUnitSyntax, string)` — both already exist in this file, unchanged.
- Produces: `internal static IEnumerable<(string EntityName, SyntaxNode Scope)> FindConfigurationScopes(CompilationUnitSyntax root)` — consumed by every `Parse*` method in Tasks 2–4.

- [ ] **Step 1: Write the failing tests**

Add to `tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentSyntaxHelpersTests.cs`, inside the existing `FluentSyntaxHelpersTests` class (add a `using System.Linq;` and `using Microsoft.CodeAnalysis;` if not already present — `System.Linq` is already imported; add `using Microsoft.CodeAnalysis.CSharp.Syntax;` is already imported too):

```csharp
    private static CompilationUnitSyntax ParseRoot(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        return tree.GetCompilationUnitRoot();
    }

    [Fact]
    public void FindConfigurationScopes_EntityGenericStyle_ReturnsInvocationScope()
    {
        var root = ParseRoot("""
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
            """);

        var scopes = FluentSyntaxHelpers.FindConfigurationScopes(root).ToList();

        var scope = Assert.Single(scopes);
        Assert.Equal("Person", scope.EntityName);
        Assert.IsType<InvocationExpressionSyntax>(scope.Scope);
    }

    [Fact]
    public void FindConfigurationScopes_EntityTypeConfigurationClass_ReturnsConfigureMethodScope()
    {
        var root = ParseRoot("""
            public class PersonConfiguration : IEntityTypeConfiguration<Person>
            {
                public void Configure(EntityTypeBuilder<Person> builder)
                {
                    builder.Property(e => e.Name).HasMaxLength(100);
                }
            }
            """);

        var scopes = FluentSyntaxHelpers.FindConfigurationScopes(root).ToList();

        var scope = Assert.Single(scopes);
        Assert.Equal("Person", scope.EntityName);
        Assert.IsType<MethodDeclarationSyntax>(scope.Scope);
        Assert.Equal("Configure", ((MethodDeclarationSyntax)scope.Scope).Identifier.Text);
    }

    [Fact]
    public void FindConfigurationScopes_MultipleEntityTypeConfigurationClasses_ReturnsAllScopes()
    {
        var root = ParseRoot("""
            public class PersonConfiguration : IEntityTypeConfiguration<Person>
            {
                public void Configure(EntityTypeBuilder<Person> builder)
                {
                    builder.Property(e => e.Name).HasMaxLength(100);
                }
            }

            public class AddressConfiguration : IEntityTypeConfiguration<Address>
            {
                public void Configure(EntityTypeBuilder<Address> builder)
                {
                    builder.Property(e => e.Line1).HasMaxLength(200);
                }
            }
            """);

        var scopes = FluentSyntaxHelpers.FindConfigurationScopes(root).ToList();

        Assert.Equal(2, scopes.Count);
        Assert.Contains(scopes, s => s.EntityName == "Person");
        Assert.Contains(scopes, s => s.EntityName == "Address");
    }

    [Fact]
    public void FindConfigurationScopes_MixedStyles_ReturnsBoth()
    {
        var root = ParseRoot("""
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

            public class AddressConfiguration : IEntityTypeConfiguration<Address>
            {
                public void Configure(EntityTypeBuilder<Address> builder)
                {
                    builder.Property(e => e.Line1).HasMaxLength(200);
                }
            }
            """);

        var scopes = FluentSyntaxHelpers.FindConfigurationScopes(root).ToList();

        Assert.Equal(2, scopes.Count);
        Assert.Contains(scopes, s => s.EntityName == "Person" && s.Scope is InvocationExpressionSyntax);
        Assert.Contains(scopes, s => s.EntityName == "Address" && s.Scope is MethodDeclarationSyntax);
    }

    [Fact]
    public void FindConfigurationScopes_QualifiedInterfaceName_ResolvesSameAsBareForm()
    {
        var root = ParseRoot("""
            public class PersonConfiguration : Microsoft.EntityFrameworkCore.IEntityTypeConfiguration<Person>
            {
                public void Configure(EntityTypeBuilder<Person> builder)
                {
                    builder.Property(e => e.Name).HasMaxLength(100);
                }
            }
            """);

        var scopes = FluentSyntaxHelpers.FindConfigurationScopes(root).ToList();

        var scope = Assert.Single(scopes);
        Assert.Equal("Person", scope.EntityName);
    }

    [Fact]
    public void FindConfigurationScopes_ClassImplementsInterfaceWithNoConfigureMethod_YieldsNoScope()
    {
        var root = ParseRoot("""
            public class PersonConfiguration : IEntityTypeConfiguration<Person>
            {
            }
            """);

        var scopes = FluentSyntaxHelpers.FindConfigurationScopes(root).ToList();

        Assert.Empty(scopes);
    }

    [Fact]
    public void FindConfigurationScopes_UnrelatedGenericInterface_IsIgnored()
    {
        var root = ParseRoot("""
            public class PersonValidator : IValidatableObject<Person>
            {
                public void Configure(EntityTypeBuilder<Person> builder)
                {
                }
            }
            """);

        var scopes = FluentSyntaxHelpers.FindConfigurationScopes(root).ToList();

        Assert.Empty(scopes);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter FindConfigurationScopes`
Expected: FAIL to compile — `FluentSyntaxHelpers.FindConfigurationScopes` does not exist yet.

- [ ] **Step 3: Implement `FindConfigurationScopes`**

Add to `src/EfSchemaVisualizer.Core/Parsing/FluentSyntaxHelpers.cs`, immediately after `GetConfiguredEntityName` (currently ending around line 234):

```csharp
    /// Finds every entity-name+scope pair configured in the source, from either the
    /// `receiver.Entity&lt;T&gt;(...)` fluent style or the `IEntityTypeConfiguration&lt;T&gt;`
    /// class style. `Scope` is the node whose descendants should be searched for fluent
    /// config calls: the `Entity&lt;T&gt;(...)` invocation itself, or the `Configure` method
    /// declaration for a config class. A single entity name can appear more than once
    /// (e.g. configured across multiple `Entity&lt;T&gt;()` blocks in one file).
    internal static IEnumerable<(string EntityName, SyntaxNode Scope)> FindConfigurationScopes(
        CompilationUnitSyntax root)
    {
        var entityInvocationNames = root.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Select(GetConfiguredEntityName)
            .Where(name => name is not null)
            .Distinct()!;

        foreach (var entityName in entityInvocationNames)
        {
            foreach (var entityInvocation in FindEntityConfigInvocations(root, entityName!))
            {
                yield return (entityName!, entityInvocation);
            }
        }

        foreach (var classDeclaration in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            var entityName = TryGetEntityTypeConfigurationEntityName(classDeclaration);
            if (entityName is null)
            {
                continue;
            }

            var configureMethod = classDeclaration.Members
                .OfType<MethodDeclarationSyntax>()
                .FirstOrDefault(m => m.Identifier.Text == "Configure");

            if (configureMethod is not null)
            {
                yield return (entityName, configureMethod);
            }
        }
    }

    /// Returns the `T` type argument text if `classDeclaration`'s base list includes
    /// `IEntityTypeConfiguration&lt;T&gt;`, bare or namespace-qualified (e.g.
    /// `Microsoft.EntityFrameworkCore.IEntityTypeConfiguration&lt;T&gt;`); otherwise null.
    private static string? TryGetEntityTypeConfigurationEntityName(ClassDeclarationSyntax classDeclaration)
    {
        if (classDeclaration.BaseList is null)
        {
            return null;
        }

        foreach (var baseType in classDeclaration.BaseList.Types)
        {
            var generic = baseType.Type switch
            {
                GenericNameSyntax g => g,
                QualifiedNameSyntax { Right: GenericNameSyntax g } => g,
                _ => null,
            };

            if (generic is { Identifier.Text: "IEntityTypeConfiguration", TypeArgumentList.Arguments: [var typeArg] })
            {
                return typeArg.ToString();
            }
        }

        return null;
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter FindConfigurationScopes`
Expected: PASS (7 tests)

- [ ] **Step 5: Run the full test suite to confirm no regression**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests`
Expected: PASS (all existing tests still green)

- [ ] **Step 6: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Parsing/FluentSyntaxHelpers.cs tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentSyntaxHelpersTests.cs
git commit -m "Add FluentSyntaxHelpers.FindConfigurationScopes for IEntityTypeConfiguration<T> discovery"
```

---

### Task 2: Refactor `ParseMaxLengths` to use `FindConfigurationScopes`

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs:13-71`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs`

**Interfaces:**
- Consumes: `FluentSyntaxHelpers.FindConfigurationScopes(CompilationUnitSyntax)` from Task 1.
- Produces: no new public interface — `ParseMaxLengths(string sourceCode)` signature is unchanged. This task is the template every method in Task 3 repeats.

- [ ] **Step 1: Write the failing tests**

Add to `tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs`, inside `FluentConfigParserTests` (anywhere after the existing `Source`/`ParseMaxLengths_*` tests, e.g. right after `ParseMaxLengths_RenamedBuilderParameter_StillResolvesEntity`):

```csharp
    private const string SourceUsingEntityTypeConfiguration = """
        public class PersonConfiguration : IEntityTypeConfiguration<Person>
        {
            public void Configure(EntityTypeBuilder<Person> builder)
            {
                builder.Property(e => e.Name).HasMaxLength(100);
            }
        }
        """;

    [Fact]
    public void ParseMaxLengths_EntityTypeConfigurationStyle_ReadsConfiguredProperty()
    {
        var result = new FluentConfigParser().ParseMaxLengths(SourceUsingEntityTypeConfiguration);

        Assert.Empty(result.Diagnostics);
        Assert.Contains(result.Value, c => c is { EntityName: "Person", PropertyName: "Name", MaxLength: 100 });
    }

    private const string SourceMixingBothStylesForMaxLength = """
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

        public class AddressConfiguration : IEntityTypeConfiguration<Address>
        {
            public void Configure(EntityTypeBuilder<Address> builder)
            {
                builder.Property(e => e.Line1).HasMaxLength(200);
            }
        }
        """;

    [Fact]
    public void ParseMaxLengths_MixedStyles_ReadsBoth()
    {
        var result = new FluentConfigParser().ParseMaxLengths(SourceMixingBothStylesForMaxLength);

        Assert.Empty(result.Diagnostics);
        Assert.Equal(2, result.Value.Count);
        Assert.Contains(result.Value, c => c is { EntityName: "Person", PropertyName: "Name", MaxLength: 100 });
        Assert.Contains(result.Value, c => c is { EntityName: "Address", PropertyName: "Line1", MaxLength: 200 });
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter "ParseMaxLengths_EntityTypeConfigurationStyle_ReadsConfiguredProperty|ParseMaxLengths_MixedStyles_ReadsBoth"`
Expected: FAIL — `IEntityTypeConfiguration<T>`-style source yields zero results because `ParseMaxLengths` doesn't look for it yet.

- [ ] **Step 3: Refactor `ParseMaxLengths`**

Replace the full body of `ParseMaxLengths` in `src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs` (lines 13-71) with:

```csharp
    public ParseResult<IReadOnlyList<MaxLengthConfig>> ParseMaxLengths(string sourceCode)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var results = new List<MaxLengthConfig>();
        var diagnostics = new List<Diagnostic>();

        foreach (var (entityName, scope) in FluentSyntaxHelpers.FindConfigurationScopes(root))
        {
            foreach (var maxLengthCall in FluentSyntaxHelpers.FindCallsNamed(scope, "HasMaxLength"))
            {
                var propertyName = FluentSyntaxHelpers.GetPropertyNameFor(maxLengthCall);
                var arg = maxLengthCall.ArgumentList.Arguments.FirstOrDefault();

                if (arg is null)
                {
                    continue;
                }

                if (propertyName is null)
                {
                    diagnostics.Add(new Diagnostic(
                        DiagnosticCodes.UnresolvablePropertyName,
                        "Could not determine which property this HasMaxLength call configures.",
                        entityName,
                        PropertyName: null,
                        maxLengthCall.Span));
                    continue;
                }

                if (int.TryParse(arg.Expression.ToString(), out var maxLength))
                {
                    results.Add(new MaxLengthConfig(entityName, propertyName, maxLength));
                }
                else
                {
                    diagnostics.Add(new Diagnostic(
                        DiagnosticCodes.UnreadableMaxLengthArgument,
                        "HasMaxLength argument is not an integer literal and could not be read.",
                        entityName,
                        propertyName,
                        arg.Span));
                }
            }
        }

        return new ParseResult<IReadOnlyList<MaxLengthConfig>>(results, diagnostics);
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter FluentConfigParserTests`
Expected: PASS — both new tests, and all pre-existing `ParseMaxLengths_*` tests (`ReadsEveryConfiguredProperty_AcrossMultipleEntities`, `NestedEntityConfig_DoesNotAttributeNestedCallsToOuterEntity`, `RenamedBuilderParameter_StillResolvesEntity`) still pass unchanged.

- [ ] **Step 5: Run the full test suite**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests`
Expected: PASS

- [ ] **Step 6: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs
git commit -m "Recognize IEntityTypeConfiguration<T> classes in ParseMaxLengths"
```

---

### Task 3: Refactor the remaining 8 non-relationship `Parse*` methods

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs` (`ParsePrecisions`, `ParseIsRequired`, `ParseKeys`, `ParseTableMappings`, `ParseColumnNames`, `ParseColumnTypes`, `ParseDefaultValues`, `ParseIndexes`)
- Test: `tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs`

**Interfaces:**
- Consumes: `FluentSyntaxHelpers.FindConfigurationScopes` from Task 1 — same pattern as Task 2, applied to 8 more methods with no shape differences from `ParseMaxLengths`.
- Produces: no new public interface — all 8 signatures unchanged.

- [ ] **Step 1: Write the failing tests**

Add to `tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs`, near the end of the class (before the closing brace, after the last existing test):

```csharp
    [Fact]
    public void ParsePrecisions_EntityTypeConfigurationStyle_ReadsConfiguredProperty()
    {
        const string source = """
            public class ProductConfiguration : IEntityTypeConfiguration<Product>
            {
                public void Configure(EntityTypeBuilder<Product> builder)
                {
                    builder.Property(e => e.Price).HasPrecision(18, 2);
                }
            }
            """;

        var result = new FluentConfigParser().ParsePrecisions(source);

        Assert.Empty(result.Diagnostics);
        Assert.Contains(result.Value, c => c is { EntityName: "Product", PropertyName: "Price", Precision: 18, Scale: 2 });
    }

    [Fact]
    public void ParseIsRequired_EntityTypeConfigurationStyle_ReadsConfiguredProperty()
    {
        const string source = """
            public class PersonConfiguration : IEntityTypeConfiguration<Person>
            {
                public void Configure(EntityTypeBuilder<Person> builder)
                {
                    builder.Property(e => e.Name).IsRequired();
                }
            }
            """;

        var result = new FluentConfigParser().ParseIsRequired(source);

        Assert.Empty(result.Diagnostics);
        Assert.Contains(result.Value, c => c is { EntityName: "Person", PropertyName: "Name", IsRequired: true });
    }

    [Fact]
    public void ParseKeys_EntityTypeConfigurationStyle_ReadsConfiguredKey()
    {
        const string source = """
            public class PersonConfiguration : IEntityTypeConfiguration<Person>
            {
                public void Configure(EntityTypeBuilder<Person> builder)
                {
                    builder.HasKey(e => e.Id);
                }
            }
            """;

        var result = new FluentConfigParser().ParseKeys(source);

        Assert.Empty(result.Diagnostics);
        var config = Assert.Single(result.Value);
        Assert.Equal("Person", config.EntityName);
        Assert.Equal(new[] { "Id" }, config.PropertyNames);
    }

    [Fact]
    public void ParseTableMappings_EntityTypeConfigurationStyle_ReadsConfiguredTable()
    {
        const string source = """
            public class PersonConfiguration : IEntityTypeConfiguration<Person>
            {
                public void Configure(EntityTypeBuilder<Person> builder)
                {
                    builder.ToTable("People", "dbo");
                }
            }
            """;

        var result = new FluentConfigParser().ParseTableMappings(source);

        Assert.Empty(result.Diagnostics);
        var config = Assert.Single(result.Value);
        Assert.Equal("Person", config.EntityName);
        Assert.Equal("People", config.TableName);
        Assert.Equal("dbo", config.Schema);
    }

    [Fact]
    public void ParseColumnNames_EntityTypeConfigurationStyle_ReadsConfiguredColumnName()
    {
        const string source = """
            public class PersonConfiguration : IEntityTypeConfiguration<Person>
            {
                public void Configure(EntityTypeBuilder<Person> builder)
                {
                    builder.Property(e => e.Name).HasColumnName("full_name");
                }
            }
            """;

        var result = new FluentConfigParser().ParseColumnNames(source);

        Assert.Empty(result.Diagnostics);
        Assert.Contains(result.Value, c => c is { EntityName: "Person", PropertyName: "Name", ColumnName: "full_name" });
    }

    [Fact]
    public void ParseColumnTypes_EntityTypeConfigurationStyle_ReadsConfiguredColumnType()
    {
        const string source = """
            public class PersonConfiguration : IEntityTypeConfiguration<Person>
            {
                public void Configure(EntityTypeBuilder<Person> builder)
                {
                    builder.Property(e => e.Name).HasColumnType("varchar(100)");
                }
            }
            """;

        var result = new FluentConfigParser().ParseColumnTypes(source);

        Assert.Empty(result.Diagnostics);
        Assert.Contains(result.Value, c => c is { EntityName: "Person", PropertyName: "Name", ColumnType: "varchar(100)" });
    }

    [Fact]
    public void ParseDefaultValues_EntityTypeConfigurationStyle_ReadsConfiguredDefault()
    {
        const string source = """
            public class PersonConfiguration : IEntityTypeConfiguration<Person>
            {
                public void Configure(EntityTypeBuilder<Person> builder)
                {
                    builder.Property(e => e.IsActive).HasDefaultValue(true);
                }
            }
            """;

        var result = new FluentConfigParser().ParseDefaultValues(source);

        Assert.Empty(result.Diagnostics);
        Assert.Contains(result.Value, c => c is { EntityName: "Person", PropertyName: "IsActive" } && c.LiteralText == "true");
    }

    [Fact]
    public void ParseIndexes_EntityTypeConfigurationStyle_ReadsConfiguredIndex()
    {
        const string source = """
            public class PersonConfiguration : IEntityTypeConfiguration<Person>
            {
                public void Configure(EntityTypeBuilder<Person> builder)
                {
                    builder.HasIndex(e => e.Email).IsUnique();
                }
            }
            """;

        var result = new FluentConfigParser().ParseIndexes(source);

        Assert.Empty(result.Diagnostics);
        var config = Assert.Single(result.Value);
        Assert.Equal("Person", config.EntityName);
        Assert.Equal(new[] { "Email" }, config.PropertyNames);
        Assert.True(config.IsUnique);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter "ParsePrecisions_EntityTypeConfigurationStyle_ReadsConfiguredProperty|ParseIsRequired_EntityTypeConfigurationStyle_ReadsConfiguredProperty|ParseKeys_EntityTypeConfigurationStyle_ReadsConfiguredKey|ParseTableMappings_EntityTypeConfigurationStyle_ReadsConfiguredTable|ParseColumnNames_EntityTypeConfigurationStyle_ReadsConfiguredColumnName|ParseColumnTypes_EntityTypeConfigurationStyle_ReadsConfiguredColumnType|ParseDefaultValues_EntityTypeConfigurationStyle_ReadsConfiguredDefault|ParseIndexes_EntityTypeConfigurationStyle_ReadsConfiguredIndex"`
Expected: FAIL — all 8 return empty results.

- [ ] **Step 3: Refactor all 8 methods**

In `src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs`, replace each method body with the versions below (each replaces the method's `var entityNames = ...` through its closing `foreach` braces, same mechanical pattern as Task 2 — remove the `entityNames` scan, loop `FindConfigurationScopes(root)` directly, replace the inner scope argument with `scope`, and drop the now-unneeded `!` null-forgiving suffixes on `entityName`).

`ParsePrecisions`:
```csharp
    public ParseResult<IReadOnlyList<PrecisionConfig>> ParsePrecisions(string sourceCode)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var results = new List<PrecisionConfig>();
        var diagnostics = new List<Diagnostic>();

        foreach (var (entityName, scope) in FluentSyntaxHelpers.FindConfigurationScopes(root))
        {
            foreach (var precisionCall in FluentSyntaxHelpers.FindCallsNamed(scope, "HasPrecision"))
            {
                var propertyName = FluentSyntaxHelpers.GetPropertyNameFor(precisionCall);

                if (propertyName is null)
                {
                    diagnostics.Add(new Diagnostic(
                        DiagnosticCodes.UnresolvablePropertyName,
                        "Could not determine which property this HasPrecision call configures.",
                        entityName,
                        PropertyName: null,
                        precisionCall.Span));
                    continue;
                }

                var arguments = precisionCall.ArgumentList.Arguments;

                if (arguments.Count == 0)
                {
                    continue;
                }

                if (!int.TryParse(arguments[0].Expression.ToString(), out var precision))
                {
                    diagnostics.Add(new Diagnostic(
                        DiagnosticCodes.UnreadableHasPrecisionArgument,
                        "HasPrecision argument is not an integer literal and could not be read.",
                        entityName,
                        propertyName,
                        arguments[0].Span));
                    continue;
                }

                if (arguments.Count == 1)
                {
                    results.Add(new PrecisionConfig(entityName, propertyName, precision, Scale: null));
                    continue;
                }

                if (!int.TryParse(arguments[1].Expression.ToString(), out var scale))
                {
                    diagnostics.Add(new Diagnostic(
                        DiagnosticCodes.UnreadableHasPrecisionArgument,
                        "HasPrecision argument is not an integer literal and could not be read.",
                        entityName,
                        propertyName,
                        arguments[1].Span));
                    continue;
                }

                results.Add(new PrecisionConfig(entityName, propertyName, precision, scale));
            }
        }

        return new ParseResult<IReadOnlyList<PrecisionConfig>>(results, diagnostics);
    }
```

`ParseIsRequired`:
```csharp
    public ParseResult<IReadOnlyList<IsRequiredConfig>> ParseIsRequired(string sourceCode)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var results = new List<IsRequiredConfig>();
        var diagnostics = new List<Diagnostic>();

        foreach (var (entityName, scope) in FluentSyntaxHelpers.FindConfigurationScopes(root))
        {
            foreach (var isRequiredCall in FluentSyntaxHelpers.FindCallsNamed(scope, "IsRequired"))
            {
                var propertyName = FluentSyntaxHelpers.GetPropertyNameFor(isRequiredCall);
                var arg = isRequiredCall.ArgumentList.Arguments.FirstOrDefault();

                if (propertyName is null)
                {
                    diagnostics.Add(new Diagnostic(
                        DiagnosticCodes.UnresolvablePropertyName,
                        "Could not determine which property this IsRequired call configures.",
                        entityName,
                        PropertyName: null,
                        isRequiredCall.Span));
                    continue;
                }

                if (arg is null)
                {
                    results.Add(new IsRequiredConfig(entityName, propertyName, IsRequired: true));
                    continue;
                }

                if (arg.Expression is LiteralExpressionSyntax literal
                    && (literal.IsKind(SyntaxKind.TrueLiteralExpression) || literal.IsKind(SyntaxKind.FalseLiteralExpression)))
                {
                    results.Add(new IsRequiredConfig(entityName, propertyName, literal.IsKind(SyntaxKind.TrueLiteralExpression)));
                }
                else
                {
                    diagnostics.Add(new Diagnostic(
                        DiagnosticCodes.UnreadableIsRequiredArgument,
                        "IsRequired argument is not a boolean literal and could not be read.",
                        entityName,
                        propertyName,
                        arg.Span));
                }
            }
        }

        return new ParseResult<IReadOnlyList<IsRequiredConfig>>(results, diagnostics);
    }
```

`ParseKeys`:
```csharp
    public ParseResult<IReadOnlyList<KeyConfig>> ParseKeys(string sourceCode)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var results = new List<KeyConfig>();
        var diagnostics = new List<Diagnostic>();

        foreach (var (entityName, scope) in FluentSyntaxHelpers.FindConfigurationScopes(root))
        {
            foreach (var hasKeyCall in FluentSyntaxHelpers.FindCallsNamed(scope, "HasKey"))
            {
                var propertyNames = FluentSyntaxHelpers.TryReadPropertyNameList(hasKeyCall);

                if (propertyNames is null)
                {
                    diagnostics.Add(new Diagnostic(
                        DiagnosticCodes.UnreadableHasKeyArgument,
                        "HasKey argument(s) could not be read as property name(s).",
                        entityName,
                        PropertyName: null,
                        hasKeyCall.Span));
                    continue;
                }

                results.Add(new KeyConfig(entityName, propertyNames));
            }
        }

        return new ParseResult<IReadOnlyList<KeyConfig>>(results, diagnostics);
    }
```

`ParseTableMappings`:
```csharp
    public ParseResult<IReadOnlyList<TableConfig>> ParseTableMappings(string sourceCode)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var results = new List<TableConfig>();
        var diagnostics = new List<Diagnostic>();

        foreach (var (entityName, scope) in FluentSyntaxHelpers.FindConfigurationScopes(root))
        {
            foreach (var toTableCall in FluentSyntaxHelpers.FindCallsNamed(scope, "ToTable"))
            {
                var arguments = toTableCall.ArgumentList.Arguments;

                if (arguments.Count == 0
                    || arguments[0].Expression is not LiteralExpressionSyntax { } tableNameLiteral
                    || !tableNameLiteral.IsKind(SyntaxKind.StringLiteralExpression))
                {
                    diagnostics.Add(new Diagnostic(
                        DiagnosticCodes.UnreadableToTableArgument,
                        "ToTable argument is not a string literal and could not be read.",
                        entityName,
                        PropertyName: null,
                        toTableCall.Span));
                    continue;
                }

                string? schema = null;
                if (arguments.Count >= 2)
                {
                    if (arguments[1].Expression is LiteralExpressionSyntax { } schemaLiteral
                        && schemaLiteral.IsKind(SyntaxKind.StringLiteralExpression))
                    {
                        schema = schemaLiteral.Token.ValueText;
                    }
                    else
                    {
                        diagnostics.Add(new Diagnostic(
                            DiagnosticCodes.UnreadableToTableArgument,
                            "ToTable schema argument is not a string literal and could not be read.",
                            entityName,
                            PropertyName: null,
                            toTableCall.Span));
                        continue;
                    }
                }

                results.Add(new TableConfig(entityName, tableNameLiteral.Token.ValueText, schema));
            }
        }

        return new ParseResult<IReadOnlyList<TableConfig>>(results, diagnostics);
    }
```

`ParseColumnNames`:
```csharp
    public ParseResult<IReadOnlyList<ColumnNameConfig>> ParseColumnNames(string sourceCode)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var results = new List<ColumnNameConfig>();
        var diagnostics = new List<Diagnostic>();

        foreach (var (entityName, scope) in FluentSyntaxHelpers.FindConfigurationScopes(root))
        {
            foreach (var call in FluentSyntaxHelpers.FindCallsNamed(scope, "HasColumnName"))
            {
                var propertyName = FluentSyntaxHelpers.GetPropertyNameFor(call);

                if (propertyName is null)
                {
                    diagnostics.Add(new Diagnostic(
                        DiagnosticCodes.UnresolvablePropertyName,
                        "Could not determine which property this HasColumnName call configures.",
                        entityName,
                        PropertyName: null,
                        call.Span));
                    continue;
                }

                var arg = call.ArgumentList.Arguments.FirstOrDefault();

                if (arg?.Expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression))
                {
                    results.Add(new ColumnNameConfig(entityName, propertyName, literal.Token.ValueText));
                }
                else
                {
                    diagnostics.Add(new Diagnostic(
                        DiagnosticCodes.UnreadableHasColumnNameArgument,
                        "HasColumnName argument is not a string literal and could not be read.",
                        entityName,
                        propertyName,
                        (arg ?? (SyntaxNode)call).Span));
                }
            }
        }

        return new ParseResult<IReadOnlyList<ColumnNameConfig>>(results, diagnostics);
    }
```

`ParseColumnTypes`:
```csharp
    public ParseResult<IReadOnlyList<ColumnTypeConfig>> ParseColumnTypes(string sourceCode)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var results = new List<ColumnTypeConfig>();
        var diagnostics = new List<Diagnostic>();

        foreach (var (entityName, scope) in FluentSyntaxHelpers.FindConfigurationScopes(root))
        {
            foreach (var call in FluentSyntaxHelpers.FindCallsNamed(scope, "HasColumnType"))
            {
                var propertyName = FluentSyntaxHelpers.GetPropertyNameFor(call);

                if (propertyName is null)
                {
                    diagnostics.Add(new Diagnostic(
                        DiagnosticCodes.UnresolvablePropertyName,
                        "Could not determine which property this HasColumnType call configures.",
                        entityName,
                        PropertyName: null,
                        call.Span));
                    continue;
                }

                var arg = call.ArgumentList.Arguments.FirstOrDefault();

                if (arg?.Expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression))
                {
                    results.Add(new ColumnTypeConfig(entityName, propertyName, literal.Token.ValueText));
                }
                else
                {
                    diagnostics.Add(new Diagnostic(
                        DiagnosticCodes.UnreadableHasColumnTypeArgument,
                        "HasColumnType argument is not a string literal and could not be read.",
                        entityName,
                        propertyName,
                        (arg ?? (SyntaxNode)call).Span));
                }
            }
        }

        return new ParseResult<IReadOnlyList<ColumnTypeConfig>>(results, diagnostics);
    }
```

`ParseDefaultValues`:
```csharp
    public ParseResult<IReadOnlyList<DefaultValueConfig>> ParseDefaultValues(string sourceCode)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var results = new List<DefaultValueConfig>();
        var diagnostics = new List<Diagnostic>();

        foreach (var (entityName, scope) in FluentSyntaxHelpers.FindConfigurationScopes(root))
        {
            foreach (var call in FluentSyntaxHelpers.FindCallsNamed(scope, "HasDefaultValue"))
            {
                var propertyName = FluentSyntaxHelpers.GetPropertyNameFor(call);

                if (propertyName is null)
                {
                    diagnostics.Add(new Diagnostic(
                        DiagnosticCodes.UnresolvablePropertyName,
                        "Could not determine which property this HasDefaultValue call configures.",
                        entityName,
                        PropertyName: null,
                        call.Span));
                    continue;
                }

                var arg = call.ArgumentList.Arguments.FirstOrDefault();

                if (arg?.Expression is LiteralExpressionSyntax literal)
                {
                    results.Add(new DefaultValueConfig(entityName, propertyName, literal.ToString()));
                }
                else
                {
                    diagnostics.Add(new Diagnostic(
                        DiagnosticCodes.UnreadableHasDefaultValueArgument,
                        "HasDefaultValue argument is not a literal and could not be read.",
                        entityName,
                        propertyName,
                        (arg ?? (SyntaxNode)call).Span));
                }
            }
        }

        return new ParseResult<IReadOnlyList<DefaultValueConfig>>(results, diagnostics);
    }
```

`ParseIndexes`:
```csharp
    public ParseResult<IReadOnlyList<IndexConfig>> ParseIndexes(string sourceCode)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var results = new List<IndexConfig>();
        var diagnostics = new List<Diagnostic>();

        foreach (var (entityName, scope) in FluentSyntaxHelpers.FindConfigurationScopes(root))
        {
            foreach (var hasIndexCall in FluentSyntaxHelpers.FindCallsNamed(scope, "HasIndex"))
            {
                var indexArgs = FluentSyntaxHelpers.TryReadIndexPropertyNames(hasIndexCall);

                if (indexArgs is null)
                {
                    diagnostics.Add(new Diagnostic(
                        DiagnosticCodes.UnreadableHasIndexArgument,
                        "HasIndex argument(s) could not be read as property name(s).",
                        entityName,
                        PropertyName: null,
                        hasIndexCall.Span));
                    continue;
                }

                var (isUnique, isUniqueDiag) = TryReadIsUnique(hasIndexCall, entityName);
                if (isUniqueDiag is not null)
                    diagnostics.Add(isUniqueDiag);

                results.Add(new IndexConfig(entityName, indexArgs.Value.PropertyNames, isUnique, indexArgs.Value.Name));
            }
        }

        return new ParseResult<IReadOnlyList<IndexConfig>>(results, diagnostics);
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter FluentConfigParserTests`
Expected: PASS — the 8 new tests, and every pre-existing test for these 8 methods (`Entity<T>()`-style coverage, nested-boundary coverage, renamed-builder coverage, diagnostic coverage) unchanged.

- [ ] **Step 5: Run the full test suite**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests`
Expected: PASS

- [ ] **Step 6: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs
git commit -m "Recognize IEntityTypeConfiguration<T> classes in the remaining 8 config-kind parsers"
```

---

### Task 4: Refactor `ParseRelationships` (scope-shape guard for the bare-chained style)

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs:524-565`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs`

**Interfaces:**
- Consumes: `FluentSyntaxHelpers.FindConfigurationScopes` from Task 1; `FluentSyntaxHelpers.FindChainedCall(InvocationExpressionSyntax, string)` (existing, unchanged); `ParseRelationshipChain` (existing private method in this file, unchanged signature).
- Produces: no new public interface — `ParseRelationships(string sourceCode, IReadOnlyList<EntityModel> entities)` signature unchanged.

- [ ] **Step 1: Write the failing tests**

Add to `tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs`, near the other `ParseRelationships_*` tests (the existing `OrderCustomerEntities` and `PostTagEntities` static fields are reused — no new fixtures needed):

```csharp
    [Fact]
    public void ParseRelationships_EntityTypeConfigurationStyle_ResolvesOneToMany()
    {
        const string source = """
            public class OrderConfiguration : IEntityTypeConfiguration<Order>
            {
                public void Configure(EntityTypeBuilder<Order> builder)
                {
                    builder.HasOne(d => d.Customer)
                        .WithMany(p => p.Orders);
                }
            }
            """;

        var result = new FluentConfigParser().ParseRelationships(source, OrderCustomerEntities);

        Assert.Empty(result.Diagnostics);
        var relationship = Assert.Single(result.Value);
        Assert.Equal(RelationshipKind.OneToMany, relationship.Kind);
        Assert.Equal("Customer", relationship.PrincipalEntity);
        Assert.Equal("Order", relationship.DependentEntity);
    }

    [Fact]
    public void ParseRelationships_BareChainedStyleInsideConfigureMethod_NotMatched_MalformedChainSkippedSilently()
    {
        const string source = """
            public class OrderConfiguration : IEntityTypeConfiguration<Order>
            {
                public void Configure(EntityTypeBuilder<Order> builder)
                {
                    builder.HasOne(d => d.Customer);
                }
            }
            """;

        var result = new FluentConfigParser().ParseRelationships(source, OrderCustomerEntities);

        Assert.Empty(result.Diagnostics);
        Assert.Empty(result.Value);
    }

    [Fact]
    public void ParseRelationships_MixedStyles_ParsesEntityGenericStyleRelationship()
    {
        const string source = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Post>(entity =>
                    {
                        entity.HasMany(p => p.Tags).WithMany(t => t.Posts);
                    });
                }
            }

            public class TagConfiguration : IEntityTypeConfiguration<Tag>
            {
                public void Configure(EntityTypeBuilder<Tag> builder)
                {
                }
            }
            """;

        var result = new FluentConfigParser().ParseRelationships(source, PostTagEntities);

        Assert.Empty(result.Diagnostics);
        var relationship = Assert.Single(result.Value);
        Assert.Equal(RelationshipKind.ManyToMany, relationship.Kind);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter "ParseRelationships_EntityTypeConfigurationStyle_ResolvesOneToMany|ParseRelationships_BareChainedStyleInsideConfigureMethod_NotMatched_MalformedChainSkippedSilently|ParseRelationships_MixedStyles_ParsesEntityGenericStyleRelationship"`
Expected: The first and third FAIL (zero relationships found because `Configure`-method scopes aren't discovered yet). The second currently PASSes vacuously (also zero results) but for the wrong reason — it will keep passing after the refactor for the *right* reason (guarded, not just undiscovered). Proceed to implementation regardless.

- [ ] **Step 3: Refactor `ParseRelationships`**

Replace the full body of `ParseRelationships` in `src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs` (lines 524-565) with:

```csharp
    public ParseResult<IReadOnlyList<RelationshipConfig>> ParseRelationships(
        string sourceCode, IReadOnlyList<EntityModel> entities)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var results = new List<RelationshipConfig>();
        var diagnostics = new List<Diagnostic>();

        foreach (var (entityName, scope) in FluentSyntaxHelpers.FindConfigurationScopes(root))
        {
            var calls = FluentSyntaxHelpers.FindCallsNamed(scope, "HasOne")
                .Concat(FluentSyntaxHelpers.FindCallsNamed(scope, "HasMany"))
                .ToList();

            // The bare-chained style (`modelBuilder.Entity<Order>().HasOne(...)...`, with
            // no lambda block) only exists when the scope itself is the `Entity<T>()`
            // invocation. A `Configure` method declaration has nothing chained onto it.
            if (scope is InvocationExpressionSyntax entityInvocation)
            {
                if (FluentSyntaxHelpers.FindChainedCall(entityInvocation, "HasOne") is { } chainedHasOne)
                {
                    calls.Add(chainedHasOne);
                }

                if (FluentSyntaxHelpers.FindChainedCall(entityInvocation, "HasMany") is { } chainedHasMany)
                {
                    calls.Add(chainedHasMany);
                }
            }

            foreach (var call in calls)
            {
                ParseRelationshipChain(call, entityName, entities, results, diagnostics);
            }
        }

        return new ParseResult<IReadOnlyList<RelationshipConfig>>(results, diagnostics);
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter FluentConfigParserTests`
Expected: PASS — the 3 new tests, and every pre-existing `ParseRelationships_*` test (block-nested, bare-chained-off-`Entity<T>()`, all four relationship shapes, `HasForeignKey`/`OnDelete`/`UsingEntity` coverage, diagnostic coverage) unchanged.

- [ ] **Step 5: Run the full test suite**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests`
Expected: PASS — every test in the project, across all files.

- [ ] **Step 6: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs
git commit -m "Recognize IEntityTypeConfiguration<T> classes in ParseRelationships"
```

---

### Task 5: Update the backlog

**Files:**
- Modify: `docs/backlog.md:211-213`

**Interfaces:**
- Consumes: nothing (documentation-only task).
- Produces: nothing consumed by other tasks — this is the final task.

- [ ] **Step 1: Tick off the Priority 3 item**

In `docs/backlog.md`, replace:

```markdown
- [ ] **`[spec/plan]` `IEntityTypeConfiguration<T>` support.** Fast-follow after
      `OnModelCreating`. Structurally easier (one entity per file), but a
      separate parsing/codegen path.
```

with:

```markdown
- [x] **`[spec/plan]` `IEntityTypeConfiguration<T>` support.** Fast-follow after
      `OnModelCreating`. Structurally easier (one entity per file), but a
      separate parsing/codegen path.
      **Update:** All 10 `FluentConfigParser.Parse*` methods now transparently
      recognize `IEntityTypeConfiguration<T>` classes via a shared
      `FluentSyntaxHelpers.FindConfigurationScopes` helper, alongside the
      existing `Entity<T>()` style — same method signatures, no new public API
      (see `2026-07-13-ientitytypeconfiguration-support-design.md`). Parse +
      merge only; the rewriter (write-back into config classes) is deferred to
      a follow-up spec, matching the precedent set by relationships.
```

- [ ] **Step 2: Run the full test suite one last time**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests`
Expected: PASS

- [ ] **Step 3: Commit**

```bash
git add docs/backlog.md
git commit -m "Check off IEntityTypeConfiguration<T> support backlog item"
```

---

## Self-Review Notes

- **Spec coverage:** Every section of `2026-07-13-ientitytypeconfiguration-support-design.md` maps to a task: `FindConfigurationScopes` + `TryGetEntityTypeConfigurationEntityName` → Task 1; the 10-method `Parse*` refactor → Tasks 2–4; the relationships scope-shape guard → Task 4; testing plan (scope-detection edge cases, one representative case per config kind, relationships block-nested/bare-chained/mixed-style, regression via full-suite reruns) → covered across Tasks 1–4's test steps.
- **No placeholders:** every step has complete, runnable code — no method body in Tasks 2–4 is abbreviated or referenced by pointer to another task.
- **Type consistency:** `FindConfigurationScopes` returns `(string EntityName, SyntaxNode Scope)` in Task 1; every consumer in Tasks 2–4 destructures it identically as `(entityName, scope)` and passes `scope` directly into `FluentSyntaxHelpers.FindCallsNamed`, matching that method's existing `SyntaxNode scope` parameter type. `TryGetEntityTypeConfigurationEntityName` returns `string?`, consumed by `FindConfigurationScopes` with an `is null` guard before use — no nullability mismatch.
