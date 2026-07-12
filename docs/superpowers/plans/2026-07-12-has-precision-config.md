# `HasPrecision` Fluent Config Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add full round-trip support (model, parse, merge, rewrite) for EF Core's `HasPrecision(precision)` / `HasPrecision(precision, scale)` fluent config on decimal properties.

**Architecture:** Follows the existing `HasMaxLength` parse → merge → rewrite pattern exactly. `PropertyModel` gains nullable `Precision`/`Scale` fields. `FluentConfigParser.ParsePrecisions` reads one- and two-argument literal calls into a new `PrecisionConfig` DTO. `ModelMerger.ApplyPrecisions` folds it into `PropertyModel`. `OnModelCreatingRewriter.RewritePrecision`/`RemovePrecision` reuse the same four-case dispatch (mutate, append-to-bare-`Property`, insert-statement, synthesize-block) already built for `HasMaxLength`.

**Tech Stack:** C# / .NET 10, Roslyn (`Microsoft.CodeAnalysis.CSharp`), xUnit.

## Global Constraints

- Spec: `docs/superpowers/specs/2026-07-12-has-precision-config-design.md` — read it before starting; this plan implements it verbatim.
- `TargetFramework` is `net10.0`, `Nullable` is enabled, on both `src/EfSchemaVisualizer.Core` and `tests/EfSchemaVisualizer.Core.Tests` — match existing nullable-annotation style in the files you touch.
- Never silently drop config the parser can't read — unreadable shapes must emit a `Diagnostic` (Priority 0 project rule; see spec's "New diagnostic code" section).
- New public methods are separate composed calls, not orchestrated into existing ones (e.g. `ApplyPrecisions` is called alongside `ApplyMaxLengths`/`ApplyIsRequired` by whatever composes them, not folded into either).
- Run tests with `dotnet test --filter "FullyQualifiedName~<TestClassName>"` from the repo root (`/root/RiderProjects/EfSchemaVisualizer`); this project has no solution-wide script beyond `dotnet test`.
- Commit after each task, not each step.

---

### Task 1: `PropertyModel.Precision`/`Scale` and `PrecisionConfig` DTO

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/Model/PropertyModel.cs`
- Create: `src/EfSchemaVisualizer.Core/Parsing/PrecisionConfig.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Model/PropertyModelTests.cs`

**Interfaces:**
- Produces: `PropertyModel.Precision` (`int?`), `PropertyModel.Scale` (`int?`), both default `null`.
- Produces: `PrecisionConfig(string EntityName, string PropertyName, int Precision, int? Scale)`.

- [ ] **Step 1: Write the failing test**

Add to `tests/EfSchemaVisualizer.Core.Tests/Model/PropertyModelTests.cs`, inside the `PropertyModelTests` class, after `WithIsRequiredOverride_ProducesUpdatedCopy_LeavingOriginalUnchanged`:

```csharp
    [Fact]
    public void WithPrecisionAndScale_ProducesUpdatedCopy_LeavingOriginalUnchanged()
    {
        var original = new PropertyModel("Total", "decimal", IsNullable: false, MaxLength: null);

        var updated = original with { Precision = 18, Scale = 2 };

        Assert.Null(original.Precision);
        Assert.Null(original.Scale);
        Assert.Equal(18, updated.Precision);
        Assert.Equal(2, updated.Scale);
        Assert.Equal(original.Name, updated.Name);
    }

    [Fact]
    public void Precision_And_Scale_DefaultToNull()
    {
        var property = new PropertyModel("Total", "decimal", IsNullable: false, MaxLength: null);

        Assert.Null(property.Precision);
        Assert.Null(property.Scale);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~PropertyModelTests"`
Expected: build error — `PropertyModel` has no member `Precision`/`Scale`.

- [ ] **Step 3: Implement**

Read the current file first (`Read src/EfSchemaVisualizer.Core/Model/PropertyModel.cs`), then replace its contents with:

```csharp
namespace EfSchemaVisualizer.Core.Model;

public sealed record PropertyModel(
    string Name,
    string ClrType,
    bool IsNullable,
    int? MaxLength,
    bool? IsRequiredOverride = null,
    int? Precision = null,
    int? Scale = null);
```

Create `src/EfSchemaVisualizer.Core/Parsing/PrecisionConfig.cs`:

```csharp
namespace EfSchemaVisualizer.Core.Parsing;

public sealed record PrecisionConfig(string EntityName, string PropertyName, int Precision, int? Scale);
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~PropertyModelTests"`
Expected: PASS, all tests in the file green.

- [ ] **Step 5: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Model/PropertyModel.cs src/EfSchemaVisualizer.Core/Parsing/PrecisionConfig.cs tests/EfSchemaVisualizer.Core.Tests/Model/PropertyModelTests.cs
git commit -m "Add Precision/Scale to PropertyModel and PrecisionConfig DTO"
```

---

### Task 2: `FluentConfigParser.ParsePrecisions`

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs`

**Interfaces:**
- Consumes: `FluentSyntaxHelpers.GetConfiguredEntityName`, `FindEntityConfigInvocations`, `FindCallsNamed`, `GetPropertyNameFor` (all existing, unchanged).
- Produces: `FluentConfigParser.ParsePrecisions(string sourceCode) : ParseResult<IReadOnlyList<PrecisionConfig>>`. New diagnostic code `UnreadableHasPrecisionArgument` (entity + property populated).

- [ ] **Step 1: Write the failing test**

Add to `tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs`, inside the `FluentConfigParserTests` class (place near the end, before the closing brace):

```csharp
    private const string PrecisionSource = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Order>(entity =>
                {
                    entity.Property(e => e.Total).HasPrecision(18, 2);
                    entity.Property(e => e.Rate).HasPrecision(5);
                });
            }
        }
        """;

    [Fact]
    public void ParsePrecisions_ReadsPrecisionOnlyAndPrecisionWithScale()
    {
        var result = new FluentConfigParser().ParsePrecisions(PrecisionSource);

        Assert.Empty(result.Diagnostics);
        Assert.Equal(2, result.Value.Count);
        Assert.Contains(result.Value, c => c is { EntityName: "Order", PropertyName: "Total", Precision: 18, Scale: 2 });
        Assert.Contains(result.Value, c => c is { EntityName: "Order", PropertyName: "Rate", Precision: 5, Scale: null });
    }

    private const string PrecisionSourceWithNonLiteralFirstArg = """
        public class AppDbContext : DbContext
        {
            private const int DefaultPrecision = 18;

            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Order>(entity =>
                {
                    entity.Property(e => e.Total).HasPrecision(DefaultPrecision, 2);
                });
            }
        }
        """;

    [Fact]
    public void ParsePrecisions_NonLiteralFirstArgument_EmitsUnreadableHasPrecisionArgumentDiagnostic()
    {
        var result = new FluentConfigParser().ParsePrecisions(PrecisionSourceWithNonLiteralFirstArg);

        Assert.Empty(result.Value);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("UnreadableHasPrecisionArgument", diagnostic.Code);
        Assert.Equal("Order", diagnostic.EntityName);
        Assert.Equal("Total", diagnostic.PropertyName);
    }

    private const string PrecisionSourceWithNonLiteralSecondArg = """
        public class AppDbContext : DbContext
        {
            private const int DefaultScale = 2;

            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Order>(entity =>
                {
                    entity.Property(e => e.Total).HasPrecision(18, DefaultScale);
                });
            }
        }
        """;

    [Fact]
    public void ParsePrecisions_NonLiteralSecondArgument_EmitsUnreadableHasPrecisionArgumentDiagnostic()
    {
        var result = new FluentConfigParser().ParsePrecisions(PrecisionSourceWithNonLiteralSecondArg);

        Assert.Empty(result.Value);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("UnreadableHasPrecisionArgument", diagnostic.Code);
        Assert.Equal("Order", diagnostic.EntityName);
        Assert.Equal("Total", diagnostic.PropertyName);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~FluentConfigParserTests"`
Expected: build error — `FluentConfigParser` has no method `ParsePrecisions`.

- [ ] **Step 3: Implement**

Read the current file first (`Read src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs`), then add this method to the `FluentConfigParser` class (place it after `ParseMaxLengths`):

```csharp
    public ParseResult<IReadOnlyList<PrecisionConfig>> ParsePrecisions(string sourceCode)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var results = new List<PrecisionConfig>();
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
                foreach (var precisionCall in FluentSyntaxHelpers.FindCallsNamed(entityInvocation, "HasPrecision"))
                {
                    var propertyName = FluentSyntaxHelpers.GetPropertyNameFor(precisionCall);

                    if (propertyName is null)
                    {
                        diagnostics.Add(new Diagnostic(
                            "UnresolvablePropertyName",
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
                            "UnreadableHasPrecisionArgument",
                            "HasPrecision argument is not an integer literal and could not be read.",
                            entityName,
                            propertyName,
                            arguments[0].Span));
                        continue;
                    }

                    if (arguments.Count == 1)
                    {
                        results.Add(new PrecisionConfig(entityName!, propertyName, precision, Scale: null));
                        continue;
                    }

                    if (!int.TryParse(arguments[1].Expression.ToString(), out var scale))
                    {
                        diagnostics.Add(new Diagnostic(
                            "UnreadableHasPrecisionArgument",
                            "HasPrecision argument is not an integer literal and could not be read.",
                            entityName,
                            propertyName,
                            arguments[1].Span));
                        continue;
                    }

                    results.Add(new PrecisionConfig(entityName!, propertyName, precision, scale));
                }
            }
        }

        return new ParseResult<IReadOnlyList<PrecisionConfig>>(results, diagnostics);
    }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~FluentConfigParserTests"`
Expected: PASS, all tests in the file green.

- [ ] **Step 5: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs
git commit -m "Add FluentConfigParser.ParsePrecisions"
```

---

### Task 3: `ModelMerger.ApplyPrecisions`

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/Parsing/ModelMerger.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Parsing/ModelMergerTests.cs`

**Interfaces:**
- Consumes: `PrecisionConfig` (Task 1), `PropertyModel.Precision`/`Scale` (Task 1).
- Produces: `ModelMerger.ApplyPrecisions(EntityModel entity, IReadOnlyList<PrecisionConfig> configs) : EntityModel`.

- [ ] **Step 1: Write the failing test**

Add to `tests/EfSchemaVisualizer.Core.Tests/Parsing/ModelMergerTests.cs`, inside the `ModelMergerTests` class, after `ApplyIsRequired_SetsIsRequiredOverrideOnMatchingProperty_LeavesOthersUntouched`:

```csharp
    [Fact]
    public void ApplyPrecisions_SetsPrecisionAndScaleOnMatchingProperty_LeavesOthersUntouched()
    {
        var entity = new EntityModel("Order", new List<PropertyModel>
        {
            new("Id", "int", IsNullable: false, MaxLength: null),
            new("Total", "decimal", IsNullable: false, MaxLength: null),
        });

        var configs = new List<PrecisionConfig>
        {
            new("Order", "Total", 18, 2),
            new("Address", "Line1", 10, null), // different entity, must not affect Order
        };

        var merged = ModelMerger.ApplyPrecisions(entity, configs);

        Assert.Null(merged.Properties.Single(p => p.Name == "Id").Precision);
        Assert.Equal(18, merged.Properties.Single(p => p.Name == "Total").Precision);
        Assert.Equal(2, merged.Properties.Single(p => p.Name == "Total").Scale);
    }

    [Fact]
    public void ApplyPrecisions_NoMatchingConfig_LeavesPrecisionAndScaleNull()
    {
        var entity = new EntityModel("Order", new List<PropertyModel>
        {
            new("Rate", "decimal", IsNullable: false, MaxLength: null),
        });

        var merged = ModelMerger.ApplyPrecisions(entity, new List<PrecisionConfig>());

        Assert.Null(merged.Properties.Single(p => p.Name == "Rate").Precision);
        Assert.Null(merged.Properties.Single(p => p.Name == "Rate").Scale);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~ModelMergerTests"`
Expected: build error — `ModelMerger` has no method `ApplyPrecisions`.

- [ ] **Step 3: Implement**

Read the current file first (`Read src/EfSchemaVisualizer.Core/Parsing/ModelMerger.cs`), then add this method to the `ModelMerger` class (place it after `ApplyIsRequired`):

```csharp
    public static EntityModel ApplyPrecisions(EntityModel entity, IReadOnlyList<PrecisionConfig> configs)
    {
        var updatedProperties = entity.Properties
            .Select(property =>
            {
                var config = configs.FirstOrDefault(c =>
                    c.EntityName == entity.Name && c.PropertyName == property.Name);

                return config is null ? property : property with { Precision = config.Precision, Scale = config.Scale };
            })
            .ToList();

        return entity with { Properties = updatedProperties };
    }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~ModelMergerTests"`
Expected: PASS, all tests in the file green.

- [ ] **Step 5: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Parsing/ModelMerger.cs tests/EfSchemaVisualizer.Core.Tests/Parsing/ModelMergerTests.cs
git commit -m "Add ModelMerger.ApplyPrecisions"
```

---

### Task 4: `OnModelCreatingRewriter.RewritePrecision`

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs`

**Interfaces:**
- Consumes: `FluentSyntaxHelpers.FindEntityConfigInvocations`, `FindCallsNamed`, `GetPropertyNameFor`, `GetPropertyNameForPropertyCall`, `GetPropertyLambdaParameterName` (all existing, unchanged); `OnModelCreatingRewriter.FindOnModelCreatingMethod`, `BuildEntityInvocationStatement` (existing private helpers, same class).
- Produces: `OnModelCreatingRewriter.RewritePrecision(string sourceCode, string entityName, string propertyName, int precision, int? scale) : string`.

- [ ] **Step 1: Write the failing test**

Add to `tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs`, inside the `OnModelCreatingRewriterTests` class (place near the end, before the closing brace):

```csharp
    private const string PrecisionSource = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Order>(entity =>
                {
                    entity.Property(e => e.Total).HasPrecision(18, 2);
                    entity.Property(e => e.Rate).HasPrecision(5);
                });
            }
        }
        """;

    [Fact]
    public void RewritePrecision_ExistingCall_MutatesArguments()
    {
        var result = new OnModelCreatingRewriter()
            .RewritePrecision(PrecisionSource, entityName: "Order", propertyName: "Total", precision: 20, scale: 4);

        Assert.Contains("entity.Property(e => e.Total).HasPrecision(20, 4)", result);
        Assert.Contains("entity.Property(e => e.Rate).HasPrecision(5)", result);
        Assert.DoesNotContain("HasPrecision(18, 2)", result);
    }

    [Fact]
    public void RewritePrecision_ExistingCall_MutatesFromPrecisionScaleToPrecisionOnly()
    {
        var result = new OnModelCreatingRewriter()
            .RewritePrecision(PrecisionSource, entityName: "Order", propertyName: "Total", precision: 10, scale: null);

        Assert.Contains("entity.Property(e => e.Total).HasPrecision(10)", result);
        Assert.DoesNotContain("HasPrecision(18, 2)", result);
    }

    private const string SourceWithPropertyButNoPrecision = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Order>(entity =>
                {
                    entity.Property(e => e.Total);
                });
            }
        }
        """;

    [Fact]
    public void RewritePrecision_BarePropertyCall_AppendsHasPrecision()
    {
        var result = new OnModelCreatingRewriter()
            .RewritePrecision(SourceWithPropertyButNoPrecision, entityName: "Order", propertyName: "Total", precision: 18, scale: 2);

        Assert.Contains("entity.Property(e => e.Total).HasPrecision(18, 2)", result);
    }

    private const string SourceWithEntityConfiguredNoPrecisionProperty = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Order>(entity =>
                {
                    entity.Property(e => e.Rate).HasPrecision(5);
                });
            }
        }
        """;

    [Fact]
    public void RewritePrecision_EntityConfiguredWithoutTargetProperty_InsertsNewStatement()
    {
        var result = new OnModelCreatingRewriter()
            .RewritePrecision(SourceWithEntityConfiguredNoPrecisionProperty, entityName: "Order", propertyName: "Total", precision: 18, scale: 2);

        Assert.Contains("entity.Property(e => e.Total).HasPrecision(18, 2)", result);
        Assert.Contains("entity.Property(e => e.Rate).HasPrecision(5)", result);
    }

    [Fact]
    public void RewritePrecision_UnknownEntity_InsertsNewEntityBlock()
    {
        var result = new OnModelCreatingRewriter()
            .RewritePrecision(PrecisionSource, entityName: "Vehicle", propertyName: "Weight", precision: 8, scale: 1);

        Assert.Contains("modelBuilder.Entity<Vehicle>(entity =>", result);
        Assert.Contains("entity.Property(e => e.Weight).HasPrecision(8, 1)", result);

        var configs = new FluentConfigParser().ParsePrecisions(result).Value;
        Assert.Contains(configs, c => c is { EntityName: "Vehicle", PropertyName: "Weight", Precision: 8, Scale: 1 });
        Assert.Contains(configs, c => c is { EntityName: "Order", PropertyName: "Total", Precision: 18, Scale: 2 });
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~OnModelCreatingRewriterTests"`
Expected: build error — `OnModelCreatingRewriter` has no method `RewritePrecision`.

- [ ] **Step 3: Implement**

Read the current file first (`Read src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs`), then add these methods to the `OnModelCreatingRewriter` class (place them after `RewriteMaxLength` and its private helpers, i.e. right before `public string RewriteIsRequired(...)`):

```csharp
    public string RewritePrecision(string sourceCode, string entityName, string propertyName, int precision, int? scale)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var entityInvocations = FluentSyntaxHelpers.FindEntityConfigInvocations(root, entityName).ToList();

        var existingPrecisionCall = entityInvocations
            .SelectMany(entityInvocation => FluentSyntaxHelpers.FindCallsNamed(entityInvocation, "HasPrecision"))
            .FirstOrDefault(call => FluentSyntaxHelpers.GetPropertyNameFor(call) == propertyName);

        if (existingPrecisionCall is not null)
        {
            return MutateExistingPrecision(root, existingPrecisionCall, precision, scale);
        }

        var existingPropertyCall = entityInvocations
            .SelectMany(entityInvocation => FluentSyntaxHelpers.FindCallsNamed(entityInvocation, "Property"))
            .FirstOrDefault(call => FluentSyntaxHelpers.GetPropertyNameForPropertyCall(call) == propertyName);

        if (existingPropertyCall is not null)
        {
            return AppendPrecisionToPropertyCall(root, existingPropertyCall, precision, scale);
        }

        var existingEntityInvocation = entityInvocations.FirstOrDefault();

        if (existingEntityInvocation is not null)
        {
            return InsertPrecisionStatement(root, existingEntityInvocation, propertyName, precision, scale);
        }

        return InsertPrecisionEntityBlock(root, entityName, propertyName, precision, scale);
    }

    private static string MutateExistingPrecision(CompilationUnitSyntax root, InvocationExpressionSyntax targetCall, int precision, int? scale)
    {
        var newCall = targetCall.WithArgumentList(BuildPrecisionArgumentList(precision, scale));

        var newRoot = root.ReplaceNode(targetCall, newCall);
        return newRoot.ToFullString();
    }

    private static string AppendPrecisionToPropertyCall(CompilationUnitSyntax root, InvocationExpressionSyntax propertyCall, int precision, int? scale)
    {
        var precisionCall = BuildPrecisionCall(propertyCall, precision, scale);

        var newRoot = root.ReplaceNode(propertyCall, precisionCall);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    private static string InsertPrecisionStatement(CompilationUnitSyntax root, InvocationExpressionSyntax entityInvocation, string propertyName, int precision, int? scale)
    {
        var lambda = (SimpleLambdaExpressionSyntax)entityInvocation.ArgumentList.Arguments.Single().Expression;
        var block = lambda.Block!;
        var blockReceiverName = lambda.Parameter.Identifier.Text;
        var propertyLambdaParam = FluentSyntaxHelpers.GetPropertyLambdaParameterName(entityInvocation);

        var newStatement = BuildPrecisionPropertyStatement(blockReceiverName, propertyLambdaParam, propertyName, precision, scale);
        var newBlock = block.AddStatements(newStatement);

        var newRoot = root.ReplaceNode(block, newBlock);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    private static string InsertPrecisionEntityBlock(CompilationUnitSyntax root, string entityName, string propertyName, int precision, int? scale)
    {
        var method = FindOnModelCreatingMethod(root);

        var methodBody = method.Body
            ?? throw new InvalidOperationException("OnModelCreating has no method body.");

        var modelBuilderParamName = method.ParameterList.Parameters.Single().Identifier.Text;

        var propertyStatement = BuildPrecisionPropertyStatement("entity", "e", propertyName, precision, scale);
        var entityBlockStatement = BuildEntityInvocationStatement(modelBuilderParamName, entityName, SyntaxFactory.Block(propertyStatement));

        var newMethodBody = methodBody.AddStatements(entityBlockStatement);
        var newRoot = root.ReplaceNode(methodBody, newMethodBody);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    private static ExpressionStatementSyntax BuildPrecisionPropertyStatement(string blockReceiverName, string propertyLambdaParam, string propertyName, int precision, int? scale)
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

        return SyntaxFactory.ExpressionStatement(BuildPrecisionCall(propertyCall, precision, scale));
    }

    private static InvocationExpressionSyntax BuildPrecisionCall(ExpressionSyntax propertyCallExpression, int precision, int? scale)
    {
        return SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                propertyCallExpression,
                SyntaxFactory.IdentifierName("HasPrecision")),
            BuildPrecisionArgumentList(precision, scale));
    }

    private static ArgumentListSyntax BuildPrecisionArgumentList(int precision, int? scale)
    {
        var precisionArg = SyntaxFactory.Argument(
            SyntaxFactory.LiteralExpression(
                SyntaxKind.NumericLiteralExpression,
                SyntaxFactory.Literal(precision)));

        if (scale is null)
        {
            return SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(precisionArg));
        }

        var scaleArg = SyntaxFactory.Argument(
            SyntaxFactory.LiteralExpression(
                SyntaxKind.NumericLiteralExpression,
                SyntaxFactory.Literal(scale.Value)));

        return SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(new[] { precisionArg, scaleArg }));
    }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~OnModelCreatingRewriterTests"`
Expected: PASS, all tests in the file green.

- [ ] **Step 5: Commit**

```bash
git add src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs
git commit -m "Add OnModelCreatingRewriter.RewritePrecision"
```

---

### Task 5: `OnModelCreatingRewriter.RemovePrecision`

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs`

**Interfaces:**
- Consumes: `FluentSyntaxHelpers.FindEntityConfigInvocations`, `FindCallsNamed`, `GetPropertyNameFor` (existing).
- Produces: `OnModelCreatingRewriter.RemovePrecision(string sourceCode, string entityName, string propertyName) : string`.

- [ ] **Step 1: Write the failing test**

Add to `tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs`, inside the `OnModelCreatingRewriterTests` class, after the tests added in Task 4:

```csharp
    [Fact]
    public void RemovePrecision_ExistingCall_RemovesHasPrecisionCall_LeavesBarePropertyCall()
    {
        var result = new OnModelCreatingRewriter()
            .RemovePrecision(PrecisionSource, entityName: "Order", propertyName: "Total");

        Assert.Contains("entity.Property(e => e.Total);", result);
        Assert.DoesNotContain("HasPrecision(18, 2)", result);
        Assert.Contains("entity.Property(e => e.Rate).HasPrecision(5)", result);
    }

    [Fact]
    public void RemovePrecision_NoMatchingCall_ReturnsSourceUnchanged()
    {
        var result = new OnModelCreatingRewriter()
            .RemovePrecision(SourceWithPropertyButNoPrecision, entityName: "Order", propertyName: "Total");

        Assert.Equal(SourceWithPropertyButNoPrecision, result);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~OnModelCreatingRewriterTests"`
Expected: build error — `OnModelCreatingRewriter` has no method `RemovePrecision`.

- [ ] **Step 3: Implement**

Add this method to the `OnModelCreatingRewriter` class (place it after `RemoveMaxLength`):

```csharp
    public string RemovePrecision(string sourceCode, string entityName, string propertyName)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var entityInvocations = FluentSyntaxHelpers.FindEntityConfigInvocations(root, entityName).ToList();

        var existingPrecisionCall = entityInvocations
            .SelectMany(entityInvocation => FluentSyntaxHelpers.FindCallsNamed(entityInvocation, "HasPrecision"))
            .FirstOrDefault(call => FluentSyntaxHelpers.GetPropertyNameFor(call) == propertyName);

        if (existingPrecisionCall is null)
        {
            return sourceCode;
        }

        var propertyCallExpression = ((MemberAccessExpressionSyntax)existingPrecisionCall.Expression).Expression;

        var newRoot = root.ReplaceNode(existingPrecisionCall, propertyCallExpression);
        return newRoot.NormalizeWhitespace().ToFullString();
    }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~OnModelCreatingRewriterTests"`
Expected: PASS, all tests in the file green.

- [ ] **Step 5: Run the full test suite**

Run: `dotnet test`
Expected: PASS, all tests across the whole solution green (confirms no regression in `HasMaxLength`/`HasKey`/etc.).

- [ ] **Step 6: Commit**

```bash
git add src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs
git commit -m "Add OnModelCreatingRewriter.RemovePrecision"
```

---

### Task 6: Update the backlog

**Files:**
- Modify: `docs/backlog.md`

- [ ] **Step 1: Check off the Precision/Scale item**

In `docs/backlog.md`, find this line in the Priority 2 section:

```
- [ ] **`[spec/plan]` Precision / scale** (`HasPrecision`) for decimal.
```

Replace it with:

```
- [x] **`[spec/plan]` Precision / scale** (`HasPrecision`) for decimal.
      **Update:** `FluentConfigParser.ParsePrecisions` reads `HasPrecision(18)`
      and `HasPrecision(18, 2)` into `PrecisionConfig`; `ModelMerger.ApplyPrecisions`
      folds that into `PropertyModel.Precision`/`Scale`;
      `OnModelCreatingRewriter.RewritePrecision`/`RemovePrecision` reuse the
      same four-case dispatch built for `HasMaxLength` (see
      `2026-07-12-has-precision-config-design.md`).
```

- [ ] **Step 2: Commit**

```bash
git add docs/backlog.md
git commit -m "Check off HasPrecision backlog item"
```

---

## Self-Review Notes

- **Spec coverage:** model fields (`Precision`/`Scale`), `PrecisionConfig` DTO, `ParsePrecisions` (one-arg, two-arg, non-literal-first, non-literal-second, `UnreadableHasPrecisionArgument` diagnostic), `ApplyPrecisions`, and `RewritePrecision`/`RemovePrecision`'s full four-case dispatch (mutate both directions, append, insert-statement, synthesize-block) plus remove/no-op are each covered by a task and at least one test. Spec's "Out of scope" items (CLR-type validation, `[Precision]` attribute, UI) are correctly not implemented.
- **Placeholder scan:** no TBD/TODO; every step shows complete code.
- **Type consistency:** `PrecisionConfig.Precision`/`Scale`, `PropertyModel.Precision`/`Scale`, `RewritePrecision`'s `precision`/`scale` parameters, and `ApplyPrecisions`'s `configs` parameter are all `int`/`int?` end-to-end, matching the spec.
