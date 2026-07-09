# IsRequired Fluent Config Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add parse → merge → rewrite support for EF Core's fluent `.IsRequired(...)` call, mirroring the existing `HasMaxLength` pattern end-to-end.

**Architecture:** Same three-layer pattern as `HasMaxLength`: `FluentConfigParser` reads `.IsRequired(...)` calls into a new `IsRequiredConfig` DTO; `ModelMerger` folds that into a new `PropertyModel.IsRequiredOverride` field (kept separate from the CLR-derived `IsNullable`); `OnModelCreatingRewriter` gains `RewriteIsRequired`/`RemoveIsRequired`, reusing the same syntax-tree helpers already used for `HasMaxLength`.

**Tech Stack:** C#, Roslyn (`Microsoft.CodeAnalysis.CSharp`), xUnit.

## Global Constraints

- `IsRequiredOverride` is `null` when no fluent call configures the property; `IsNullable` (CLR-derived) remains untouched and authoritative in that case.
- Bare `.IsRequired()` means `true`; `.IsRequired(false)` / `.IsRequired(true)` are read from a boolean literal argument only — any other argument shape is a diagnostic (`UnreadableIsRequiredArgument`), never silently dropped.
- Codegen: `true` is always emitted as bare `.IsRequired()`; `false` is always emitted as explicit `.IsRequired(false)`.
- `PropertyModel`'s new field must default to `null` so none of the ~10 existing `new PropertyModel(...)` call sites across `src/` and `tests/` need to change.
- Every new method mirrors an existing sibling method 1:1 in structure (`ParseMaxLengths` → `ParseIsRequired`, `ApplyMaxLengths` → `ApplyIsRequired`, `RewriteMaxLength` → `RewriteIsRequired`, `RemoveMaxLength` → `RemoveIsRequired`) — no new architecture, only new leaf logic.

---

### Task 1: `PropertyModel.IsRequiredOverride` field

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/Model/PropertyModel.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Model/PropertyModelTests.cs`

**Interfaces:**
- Produces: `PropertyModel(string Name, string ClrType, bool IsNullable, int? MaxLength, bool? IsRequiredOverride = null)` — the trailing optional param every later task reads/writes via `with { IsRequiredOverride = ... }`.

- [ ] **Step 1: Write the failing test**

Add to `tests/EfSchemaVisualizer.Core.Tests/Model/PropertyModelTests.cs`:

```csharp
    [Fact]
    public void WithIsRequiredOverride_ProducesUpdatedCopy_LeavingOriginalUnchanged()
    {
        var original = new PropertyModel("Name", "string", IsNullable: true, MaxLength: null);

        var updated = original with { IsRequiredOverride = false };

        Assert.Null(original.IsRequiredOverride);
        Assert.False(updated.IsRequiredOverride);
        Assert.Equal(original.Name, updated.Name);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~PropertyModelTests.WithIsRequiredOverride_ProducesUpdatedCopy_LeavingOriginalUnchanged"`
Expected: FAIL — build error, `PropertyModel` has no member `IsRequiredOverride`.

- [ ] **Step 3: Write minimal implementation**

Replace the contents of `src/EfSchemaVisualizer.Core/Model/PropertyModel.cs`:

```csharp
namespace EfSchemaVisualizer.Core.Model;

public sealed record PropertyModel(
    string Name,
    string ClrType,
    bool IsNullable,
    int? MaxLength,
    bool? IsRequiredOverride = null);
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~PropertyModelTests"`
Expected: PASS (all `PropertyModelTests`, including the new one).

- [ ] **Step 5: Run the full test suite to confirm no existing call site broke**

Run: `dotnet test`
Expected: PASS — all existing tests still green (the new field has a default, so `new PropertyModel("Id", "int", IsNullable: false, MaxLength: null)` call sites are unaffected).

- [ ] **Step 6: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Model/PropertyModel.cs tests/EfSchemaVisualizer.Core.Tests/Model/PropertyModelTests.cs
git commit -m "Add PropertyModel.IsRequiredOverride field"
```

---

### Task 2: `IsRequiredConfig` DTO and `FluentConfigParser.ParseIsRequired`

**Files:**
- Create: `src/EfSchemaVisualizer.Core/Parsing/IsRequiredConfig.cs`
- Modify: `src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs`

**Interfaces:**
- Consumes: `FluentSyntaxHelpers.FindEntityConfigInvocations(CompilationUnitSyntax root, string entityName)`, `FluentSyntaxHelpers.FindCallsNamed(SyntaxNode scope, string methodName)`, `FluentSyntaxHelpers.GetPropertyNameFor(InvocationExpressionSyntax fluentCall)`, `Diagnostic(string Code, string Message, string? EntityName, string? PropertyName, TextSpan Span)`, `ParseResult<T>(T Value, IReadOnlyList<Diagnostic> Diagnostics)`.
- Produces: `IsRequiredConfig(string EntityName, string PropertyName, bool IsRequired)`; `FluentConfigParser.ParseIsRequired(string sourceCode) : ParseResult<IReadOnlyList<IsRequiredConfig>>`; new diagnostic code `"UnreadableIsRequiredArgument"`.

- [ ] **Step 1: Write the failing tests**

Add to `tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs` (inside the `FluentConfigParserTests` class, after the existing `HasMaxLength` tests):

```csharp
    private const string SourceWithIsRequiredCalls = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.Property(e => e.Name).IsRequired();
                    entity.Property(e => e.Email).IsRequired(false);
                });

                modelBuilder.Entity<Address>(entity =>
                {
                    entity.Property(e => e.Line1).IsRequired(true);
                });
            }
        }
        """;

    [Fact]
    public void ParseIsRequired_ReadsBareAndExplicitCalls_AcrossMultipleEntities()
    {
        var result = new FluentConfigParser().ParseIsRequired(SourceWithIsRequiredCalls);

        Assert.Empty(result.Diagnostics);
        Assert.Equal(3, result.Value.Count);
        Assert.Contains(result.Value, c => c is { EntityName: "Person", PropertyName: "Name", IsRequired: true });
        Assert.Contains(result.Value, c => c is { EntityName: "Person", PropertyName: "Email", IsRequired: false });
        Assert.Contains(result.Value, c => c is { EntityName: "Address", PropertyName: "Line1", IsRequired: true });
    }

    private const string SourceWithNoIsRequiredCalls = """
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
    public void ParseIsRequired_NoCallsPresent_ReturnsEmpty()
    {
        var result = new FluentConfigParser().ParseIsRequired(SourceWithNoIsRequiredCalls);

        Assert.Empty(result.Diagnostics);
        Assert.Empty(result.Value);
    }

    private const string SourceWithNonLiteralIsRequiredArgument = """
        public class AppDbContext : DbContext
        {
            private const bool NameIsRequired = true;

            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.Property(e => e.Name).IsRequired(NameIsRequired);
                });
            }
        }
        """;

    [Fact]
    public void ParseIsRequired_NonLiteralArgument_EmitsUnreadableIsRequiredArgumentDiagnostic()
    {
        var result = new FluentConfigParser().ParseIsRequired(SourceWithNonLiteralIsRequiredArgument);

        Assert.Empty(result.Value);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("UnreadableIsRequiredArgument", diagnostic.Code);
        Assert.Equal("Person", diagnostic.EntityName);
        Assert.Equal("Name", diagnostic.PropertyName);
    }

    private const string SourceWithUnresolvableIsRequiredPropertyLambda = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.Property(e =>
                    {
                        var name = e.Name;
                        return name;
                    }).IsRequired();
                });
            }
        }
        """;

    [Fact]
    public void ParseIsRequired_UnresolvablePropertyLambda_EmitsUnresolvablePropertyNameDiagnostic()
    {
        var result = new FluentConfigParser().ParseIsRequired(SourceWithUnresolvableIsRequiredPropertyLambda);

        Assert.Empty(result.Value);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("UnresolvablePropertyName", diagnostic.Code);
        Assert.Equal("Person", diagnostic.EntityName);
        Assert.Null(diagnostic.PropertyName);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~FluentConfigParserTests"`
Expected: FAIL — build error, `FluentConfigParser` has no method `ParseIsRequired` and no type `IsRequiredConfig` exists.

- [ ] **Step 3: Create the `IsRequiredConfig` DTO**

Create `src/EfSchemaVisualizer.Core/Parsing/IsRequiredConfig.cs`:

```csharp
namespace EfSchemaVisualizer.Core.Parsing;

public sealed record IsRequiredConfig(string EntityName, string PropertyName, bool IsRequired);
```

- [ ] **Step 4: Implement `FluentConfigParser.ParseIsRequired`**

Add to `src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs`, inside the `FluentConfigParser` class, after `ParseMaxLengths`:

```csharp
    public ParseResult<IReadOnlyList<IsRequiredConfig>> ParseIsRequired(string sourceCode)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var results = new List<IsRequiredConfig>();
        var diagnostics = new List<Diagnostic>();

        var entityNames = root.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Select(FluentSyntaxHelpers.GetConfiguredEntityName)
            .Where(name => name is not null)
            .Distinct()!;

        foreach (var entityName in entityNames)
        {
            foreach (var entityInvocation in FluentSyntaxHelpers.FindEntityConfigInvocations(root, entityName!))
            {
                foreach (var isRequiredCall in FluentSyntaxHelpers.FindCallsNamed(entityInvocation, "IsRequired"))
                {
                    var propertyName = FluentSyntaxHelpers.GetPropertyNameFor(isRequiredCall);
                    var arg = isRequiredCall.ArgumentList.Arguments.FirstOrDefault();

                    if (propertyName is null)
                    {
                        diagnostics.Add(new Diagnostic(
                            "UnresolvablePropertyName",
                            "Could not determine which property this IsRequired call configures.",
                            entityName,
                            PropertyName: null,
                            isRequiredCall.Span));
                        continue;
                    }

                    if (arg is null)
                    {
                        results.Add(new IsRequiredConfig(entityName!, propertyName, IsRequired: true));
                        continue;
                    }

                    if (arg.Expression is LiteralExpressionSyntax literal
                        && (literal.IsKind(SyntaxKind.TrueLiteralExpression) || literal.IsKind(SyntaxKind.FalseLiteralExpression)))
                    {
                        results.Add(new IsRequiredConfig(entityName!, propertyName, literal.IsKind(SyntaxKind.TrueLiteralExpression)));
                    }
                    else
                    {
                        diagnostics.Add(new Diagnostic(
                            "UnreadableIsRequiredArgument",
                            "IsRequired argument is not a boolean literal and could not be read.",
                            entityName,
                            propertyName,
                            arg.Span));
                    }
                }
            }
        }

        return new ParseResult<IReadOnlyList<IsRequiredConfig>>(results, diagnostics);
    }
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~FluentConfigParserTests"`
Expected: PASS — all `FluentConfigParserTests`, including the four new `ParseIsRequired` tests.

- [ ] **Step 6: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Parsing/IsRequiredConfig.cs src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs
git commit -m "Add FluentConfigParser.ParseIsRequired"
```

---

### Task 3: `ModelMerger.ApplyIsRequired`

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/Parsing/ModelMerger.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Parsing/ModelMergerTests.cs`

**Interfaces:**
- Consumes: `IsRequiredConfig(string EntityName, string PropertyName, bool IsRequired)` (Task 2), `PropertyModel.IsRequiredOverride` (Task 1), `EntityModel(string Name, IReadOnlyList<PropertyModel> Properties)`.
- Produces: `ModelMerger.ApplyIsRequired(EntityModel entity, IReadOnlyList<IsRequiredConfig> configs) : EntityModel`.

- [ ] **Step 1: Write the failing test**

Add to `tests/EfSchemaVisualizer.Core.Tests/Parsing/ModelMergerTests.cs`:

```csharp
    [Fact]
    public void ApplyIsRequired_SetsIsRequiredOverrideOnMatchingProperty_LeavesOthersUntouched()
    {
        var entity = new EntityModel("Person", new List<PropertyModel>
        {
            new("Id", "int", IsNullable: false, MaxLength: null),
            new("Name", "string", IsNullable: true, MaxLength: null),
        });

        var configs = new List<IsRequiredConfig>
        {
            new("Person", "Name", true),
            new("Address", "Line1", false), // different entity, must not affect Person
        };

        var merged = ModelMerger.ApplyIsRequired(entity, configs);

        Assert.Null(merged.Properties.Single(p => p.Name == "Id").IsRequiredOverride);
        Assert.True(merged.Properties.Single(p => p.Name == "Name").IsRequiredOverride);
        // CLR-derived IsNullable is untouched by the fluent override.
        Assert.True(merged.Properties.Single(p => p.Name == "Name").IsNullable);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~ModelMergerTests"`
Expected: FAIL — build error, `ModelMerger` has no method `ApplyIsRequired`.

- [ ] **Step 3: Implement `ModelMerger.ApplyIsRequired`**

Add to `src/EfSchemaVisualizer.Core/Parsing/ModelMerger.cs`, inside the `ModelMerger` class, after `ApplyMaxLengths`:

```csharp
    public static EntityModel ApplyIsRequired(EntityModel entity, IReadOnlyList<IsRequiredConfig> configs)
    {
        var updatedProperties = entity.Properties
            .Select(property =>
            {
                var config = configs.FirstOrDefault(c =>
                    c.EntityName == entity.Name && c.PropertyName == property.Name);

                return config is null ? property : property with { IsRequiredOverride = config.IsRequired };
            })
            .ToList();

        return entity with { Properties = updatedProperties };
    }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~ModelMergerTests"`
Expected: PASS — both `ApplyMaxLengths` and `ApplyIsRequired` tests green.

- [ ] **Step 5: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Parsing/ModelMerger.cs tests/EfSchemaVisualizer.Core.Tests/Parsing/ModelMergerTests.cs
git commit -m "Add ModelMerger.ApplyIsRequired"
```

---

### Task 4: `OnModelCreatingRewriter.RewriteIsRequired` — mutate and append cases

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs`

**Interfaces:**
- Consumes: `FluentSyntaxHelpers.FindEntityConfigInvocations`, `FluentSyntaxHelpers.FindCallsNamed`, `FluentSyntaxHelpers.GetPropertyNameFor`, `FluentSyntaxHelpers.GetPropertyNameForPropertyCall`.
- Produces (partial, extended in Task 5): `OnModelCreatingRewriter.RewriteIsRequired(string sourceCode, string entityName, string propertyName, bool newIsRequired) : string` — this task implements the "mutate existing `.IsRequired(...)` call" and "append `.IsRequired(...)` to a bare `.Property(...)` call" branches; Task 5 adds the remaining two branches (insert statement, synthesize whole block) plus `RemoveIsRequired`.

This task introduces the shared `BuildIsRequiredArgumentList(bool isRequired)` helper (bare arg list for `true`, single `false`-literal arg for `false`) that both this task and Task 5 reuse.

- [ ] **Step 1: Write the failing tests**

Add to `tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs`, after the existing `RewriteMaxLength` tests (before `RemoveMaxLength_ExistingCall_...`):

```csharp
    private const string SourceWithIsRequiredCalls = """
        public class AppDbContext : DbContext
        {
            // unrelated comment that must survive untouched
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.Property(e => e.Name).IsRequired();
                    entity.Property(e => e.Email).IsRequired(false);
                });

                modelBuilder.Entity<Address>(entity =>
                {
                    entity.Property(e => e.Line1).IsRequired();
                });
            }
        }
        """;

    [Fact]
    public void RewriteIsRequired_ExistingBareCall_MutatesToFalse()
    {
        var result = new OnModelCreatingRewriter()
            .RewriteIsRequired(SourceWithIsRequiredCalls, entityName: "Person", propertyName: "Name", newIsRequired: false);

        Assert.Contains("entity.Property(e => e.Name).IsRequired(false)", result);

        // Untouched: Person.Email, Address.Line1, and the unrelated comment.
        Assert.Contains("entity.Property(e => e.Email).IsRequired(false)", result);
        Assert.Contains("entity.Property(e => e.Line1).IsRequired()", result);
        Assert.Contains("// unrelated comment that must survive untouched", result);
    }

    [Fact]
    public void RewriteIsRequired_ExistingExplicitFalseCall_MutatesToBareTrue()
    {
        var result = new OnModelCreatingRewriter()
            .RewriteIsRequired(SourceWithIsRequiredCalls, entityName: "Person", propertyName: "Email", newIsRequired: true);

        Assert.Contains("entity.Property(e => e.Email).IsRequired()", result);
        Assert.DoesNotContain("IsRequired(false)", result);
    }

    private const string SourceWithUnconfiguredIsRequiredProperty = """
        public class AppDbContext : DbContext
        {
            // unrelated comment that must survive untouched
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.Property(e => e.Name).IsRequired();
                    entity.Property(e => e.Email);
                });
            }
        }
        """;

    [Fact]
    public void RewriteIsRequired_PropertyExistsWithoutIsRequired_AppendsBareIsRequiredCall()
    {
        var result = new OnModelCreatingRewriter()
            .RewriteIsRequired(SourceWithUnconfiguredIsRequiredProperty, entityName: "Person", propertyName: "Email", newIsRequired: true);

        Assert.Contains("entity.Property(e => e.Email).IsRequired()", result);
        Assert.Contains("// unrelated comment that must survive untouched", result);

        var configs = new FluentConfigParser().ParseIsRequired(result).Value;
        Assert.Contains(configs, c => c is { EntityName: "Person", PropertyName: "Name", IsRequired: true });
        Assert.Contains(configs, c => c is { EntityName: "Person", PropertyName: "Email", IsRequired: true });
    }

    [Fact]
    public void RewriteIsRequired_PropertyExistsWithoutIsRequired_AppendsExplicitFalseCall()
    {
        var result = new OnModelCreatingRewriter()
            .RewriteIsRequired(SourceWithUnconfiguredIsRequiredProperty, entityName: "Person", propertyName: "Email", newIsRequired: false);

        Assert.Contains("entity.Property(e => e.Email).IsRequired(false)", result);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~OnModelCreatingRewriterTests"`
Expected: FAIL — build error, `OnModelCreatingRewriter` has no method `RewriteIsRequired`.

- [ ] **Step 3: Implement the mutate/append branches**

Add to `src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs`, inside the `OnModelCreatingRewriter` class, after `RewriteMaxLength`'s private helper methods (i.e., right before `public string AddEntity(...)`):

```csharp
    public string RewriteIsRequired(string sourceCode, string entityName, string propertyName, bool newIsRequired)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var entityInvocations = FluentSyntaxHelpers.FindEntityConfigInvocations(root, entityName).ToList();

        var existingIsRequiredCall = entityInvocations
            .SelectMany(entityInvocation => FluentSyntaxHelpers.FindCallsNamed(entityInvocation, "IsRequired"))
            .FirstOrDefault(call => FluentSyntaxHelpers.GetPropertyNameFor(call) == propertyName);

        if (existingIsRequiredCall is not null)
        {
            return MutateExistingIsRequired(root, existingIsRequiredCall, newIsRequired);
        }

        var existingPropertyCall = entityInvocations
            .SelectMany(entityInvocation => FluentSyntaxHelpers.FindCallsNamed(entityInvocation, "Property"))
            .FirstOrDefault(call => FluentSyntaxHelpers.GetPropertyNameForPropertyCall(call) == propertyName);

        if (existingPropertyCall is not null)
        {
            return AppendIsRequiredToPropertyCall(root, existingPropertyCall, newIsRequired);
        }

        var existingEntityInvocation = entityInvocations.FirstOrDefault();

        if (existingEntityInvocation is not null)
        {
            return InsertIsRequiredPropertyStatement(root, existingEntityInvocation, propertyName, newIsRequired);
        }

        return InsertIsRequiredEntityBlock(root, entityName, propertyName, newIsRequired);
    }

    private static string MutateExistingIsRequired(CompilationUnitSyntax root, InvocationExpressionSyntax targetCall, bool newIsRequired)
    {
        var newCall = targetCall.WithArgumentList(BuildIsRequiredArgumentList(newIsRequired));

        var newRoot = root.ReplaceNode(targetCall, newCall);
        return newRoot.ToFullString();
    }

    private static string AppendIsRequiredToPropertyCall(CompilationUnitSyntax root, InvocationExpressionSyntax propertyCall, bool newIsRequired)
    {
        var isRequiredCall = BuildIsRequiredCall(propertyCall, newIsRequired);

        var newRoot = root.ReplaceNode(propertyCall, isRequiredCall);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    private static InvocationExpressionSyntax BuildIsRequiredCall(ExpressionSyntax propertyCallExpression, bool isRequired)
    {
        return SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                propertyCallExpression,
                SyntaxFactory.IdentifierName("IsRequired")),
            BuildIsRequiredArgumentList(isRequired));
    }

    private static ArgumentListSyntax BuildIsRequiredArgumentList(bool isRequired)
    {
        if (isRequired)
        {
            return SyntaxFactory.ArgumentList();
        }

        return SyntaxFactory.ArgumentList(
            SyntaxFactory.SingletonSeparatedList(
                SyntaxFactory.Argument(
                    SyntaxFactory.LiteralExpression(SyntaxKind.FalseLiteralExpression))));
    }
```

Note: `InsertIsRequiredPropertyStatement` and `InsertIsRequiredEntityBlock` are referenced above but implemented in Task 5 — add temporary stub versions now so the file compiles, to be replaced (not left as stubs) in Task 5:

```csharp
    private static string InsertIsRequiredPropertyStatement(CompilationUnitSyntax root, InvocationExpressionSyntax entityInvocation, string propertyName, bool newIsRequired)
    {
        throw new NotImplementedException();
    }

    private static string InsertIsRequiredEntityBlock(CompilationUnitSyntax root, string entityName, string propertyName, bool newIsRequired)
    {
        throw new NotImplementedException();
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~OnModelCreatingRewriterTests"`
Expected: PASS — the four new mutate/append tests pass; other tests unaffected. (The two `NotImplementedException` stubs are not exercised by any test yet — that happens in Task 5.)

- [ ] **Step 5: Commit**

```bash
git add src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs
git commit -m "Add OnModelCreatingRewriter.RewriteIsRequired mutate/append cases"
```

---

### Task 5: `RewriteIsRequired` insert cases and `RemoveIsRequired`

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs`

**Interfaces:**
- Consumes: `BuildIsRequiredCall`, `BuildIsRequiredArgumentList` (Task 4); `FluentSyntaxHelpers.GetPropertyLambdaParameterName`; `FindOnModelCreatingMethod`; `BuildEntityInvocationStatement`.
- Produces: completed `RewriteIsRequired` (all four branches), plus `OnModelCreatingRewriter.RemoveIsRequired(string sourceCode, string entityName, string propertyName) : string`.

- [ ] **Step 1: Write the failing tests**

Add to `tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs`, right after the Task 4 tests:

```csharp
    [Fact]
    public void RewriteIsRequired_UnknownEntity_InsertsNewEntityBlock()
    {
        var result = new OnModelCreatingRewriter()
            .RewriteIsRequired(SourceWithIsRequiredCalls, entityName: "Vehicle", propertyName: "Name", newIsRequired: true);

        Assert.Contains("modelBuilder.Entity<Vehicle>(entity =>", result);
        Assert.Contains("entity.Property(e => e.Name).IsRequired()", result);

        var configs = new FluentConfigParser().ParseIsRequired(result).Value;
        Assert.Contains(configs, c => c is { EntityName: "Vehicle", PropertyName: "Name", IsRequired: true });
        Assert.Contains(configs, c => c is { EntityName: "Person", PropertyName: "Name", IsRequired: true });
    }

    private const string SourceWithMissingIsRequiredPropertyMention = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.Property(e => e.Name).IsRequired();
                });
            }
        }
        """;

    [Fact]
    public void RewriteIsRequired_PropertyNeverMentioned_InsertsNewStatementAtEndOfBlock()
    {
        var result = new OnModelCreatingRewriter()
            .RewriteIsRequired(SourceWithMissingIsRequiredPropertyMention, entityName: "Person", propertyName: "Email", newIsRequired: false);

        Assert.Contains("entity.Property(e => e.Email).IsRequired(false)", result);

        var configs = new FluentConfigParser().ParseIsRequired(result).Value;
        Assert.Contains(configs, c => c is { EntityName: "Person", PropertyName: "Name", IsRequired: true });
        Assert.Contains(configs, c => c is { EntityName: "Person", PropertyName: "Email", IsRequired: false });
    }

    [Fact]
    public void RemoveIsRequired_ExistingCall_StripsIsRequiredLeavesBarePropertyCall()
    {
        var result = new OnModelCreatingRewriter()
            .RemoveIsRequired(SourceWithIsRequiredCalls, entityName: "Person", propertyName: "Name");

        Assert.Contains("entity.Property(e => e.Name);", result);
        Assert.DoesNotContain("e.Name).IsRequired()", result);

        // Untouched: Person.Email, Address.Line1.
        Assert.Contains("entity.Property(e => e.Email).IsRequired(false)", result);
        Assert.Contains("entity.Property(e => e.Line1).IsRequired()", result);
    }

    [Fact]
    public void RemoveIsRequired_NoMatchingCall_ReturnsSourceUnchanged()
    {
        var result = new OnModelCreatingRewriter()
            .RemoveIsRequired(SourceWithIsRequiredCalls, entityName: "Person", propertyName: "DoesNotExist");

        Assert.Equal(SourceWithIsRequiredCalls, result);
    }

    [Fact]
    public void RemoveIsRequired_EntityHasNoConfigAtAll_ReturnsSourceUnchanged()
    {
        var result = new OnModelCreatingRewriter()
            .RemoveIsRequired(SourceWithIsRequiredCalls, entityName: "Vehicle", propertyName: "Name");

        Assert.Equal(SourceWithIsRequiredCalls, result);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~OnModelCreatingRewriterTests"`
Expected: FAIL — the two new `RewriteIsRequired` insert tests throw `NotImplementedException`; `RemoveIsRequired` tests fail with a build error (method doesn't exist).

- [ ] **Step 3: Replace the stub methods with real implementations**

In `src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs`, replace the two stub methods added in Task 4:

```csharp
    private static string InsertIsRequiredPropertyStatement(CompilationUnitSyntax root, InvocationExpressionSyntax entityInvocation, string propertyName, bool newIsRequired)
    {
        var lambda = (SimpleLambdaExpressionSyntax)entityInvocation.ArgumentList.Arguments.Single().Expression;
        var block = lambda.Block!;
        var blockReceiverName = lambda.Parameter.Identifier.Text;
        var propertyLambdaParam = FluentSyntaxHelpers.GetPropertyLambdaParameterName(entityInvocation);

        var newStatement = BuildIsRequiredPropertyStatement(blockReceiverName, propertyLambdaParam, propertyName, newIsRequired);
        var newBlock = block.AddStatements(newStatement);

        var newRoot = root.ReplaceNode(block, newBlock);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    private static string InsertIsRequiredEntityBlock(CompilationUnitSyntax root, string entityName, string propertyName, bool newIsRequired)
    {
        var method = FindOnModelCreatingMethod(root);

        var methodBody = method.Body
            ?? throw new InvalidOperationException("OnModelCreating has no method body.");

        var modelBuilderParamName = method.ParameterList.Parameters.Single().Identifier.Text;

        var propertyStatement = BuildIsRequiredPropertyStatement("entity", "e", propertyName, newIsRequired);
        var entityBlockStatement = BuildEntityInvocationStatement(modelBuilderParamName, entityName, SyntaxFactory.Block(propertyStatement));

        var newMethodBody = methodBody.AddStatements(entityBlockStatement);
        var newRoot = root.ReplaceNode(methodBody, newMethodBody);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    private static ExpressionStatementSyntax BuildIsRequiredPropertyStatement(string blockReceiverName, string propertyLambdaParam, string propertyName, bool isRequired)
    {
        var propertyCall = SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                SyntaxFactory.IdentifierName(blockReceiverName),
                SyntaxFactory.IdentifierName("Property")),
            SyntaxFactory.ArgumentList(
                SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.Argument(
                        SyntaxFactory.SimpleLambdaExpression(
                            SyntaxFactory.Parameter(SyntaxFactory.Identifier(propertyLambdaParam)),
                            SyntaxFactory.MemberAccessExpression(
                                SyntaxKind.SimpleMemberAccessExpression,
                                SyntaxFactory.IdentifierName(propertyLambdaParam),
                                SyntaxFactory.IdentifierName(propertyName)))))));

        return SyntaxFactory.ExpressionStatement(BuildIsRequiredCall(propertyCall, isRequired));
    }
```

- [ ] **Step 4: Implement `RemoveIsRequired`**

Add to `src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs`, right after `RemoveMaxLength`:

```csharp
    public string RemoveIsRequired(string sourceCode, string entityName, string propertyName)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var entityInvocations = FluentSyntaxHelpers.FindEntityConfigInvocations(root, entityName).ToList();

        var existingIsRequiredCall = entityInvocations
            .SelectMany(entityInvocation => FluentSyntaxHelpers.FindCallsNamed(entityInvocation, "IsRequired"))
            .FirstOrDefault(call => FluentSyntaxHelpers.GetPropertyNameFor(call) == propertyName);

        if (existingIsRequiredCall is null)
        {
            return sourceCode;
        }

        var propertyCallExpression = ((MemberAccessExpressionSyntax)existingIsRequiredCall.Expression).Expression;

        var newRoot = root.ReplaceNode(existingIsRequiredCall, propertyCallExpression);
        return newRoot.NormalizeWhitespace().ToFullString();
    }
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~OnModelCreatingRewriterTests"`
Expected: PASS — all `RewriteIsRequired` and `RemoveIsRequired` tests green.

- [ ] **Step 6: Run the full test suite**

Run: `dotnet test`
Expected: PASS — entire solution green, no regressions in `HasMaxLength`, rename, add/remove-entity, or add/drop-property coverage.

- [ ] **Step 7: Commit**

```bash
git add src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs
git commit -m "Complete OnModelCreatingRewriter.RewriteIsRequired insert cases; add RemoveIsRequired"
```

---

### Task 6: Update backlog

**Files:**
- Modify: `docs/backlog.md`

**Interfaces:**
- None — documentation-only task.

- [ ] **Step 1: Check off the completed backlog item**

In `docs/backlog.md`, change:

```markdown
- [ ] **`[spec/plan]` `IsRequired` / nullability** as fluent config (distinct from CLR `?`).
```

to:

```markdown
- [x] **`[spec/plan]` `IsRequired` / nullability** as fluent config (distinct from CLR `?`).
      **Update:** `FluentConfigParser.ParseIsRequired` reads bare `.IsRequired()` and
      explicit `.IsRequired(true/false)` calls into `IsRequiredConfig`;
      `ModelMerger.ApplyIsRequired` folds that into `PropertyModel.IsRequiredOverride`,
      kept separate from CLR-derived `IsNullable`; `OnModelCreatingRewriter.RewriteIsRequired`/
      `RemoveIsRequired` mirror the full `HasMaxLength` rewrite/remove pattern (see
      `2026-07-09-is-required-config-design.md`).
```

- [ ] **Step 2: Commit**

```bash
git add docs/backlog.md
git commit -m "Check off 'IsRequired / nullability' in backlog"
```
