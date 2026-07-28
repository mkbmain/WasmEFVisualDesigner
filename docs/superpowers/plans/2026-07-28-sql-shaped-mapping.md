# SQL-shaped mapping Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Parse, model, edit, and render `HasComputedColumnSql`, `HasCheckConstraint`, and `HasSequence`/`UseSequence` — the remaining "SQL-shaped mapping" backlog item — and fix the `RecognizedCallNames` scoping bug where recognizing `HasName` globally silently swallows `HasAlternateKey(...).HasName(...)` / `HasSequence(...).HasName(...)`.

**Architecture:** Every feature follows the project's existing five-layer pipeline (Parser → Merger → Rewriter → Editor → UI), wired into `DiagramModelBuilder.Build`. `HasComputedColumnSql` and `UseSequence` extend the existing generic string-arg-call rewriter helpers (shared today by `HasDefaultValueSql`/`HasColumnType`). `HasCheckConstraint` gets new repeatable-list rewriter methods (no existing helper fits a multi-per-entity call). `HasSequence` is model-level, following `HasDefaultSchema`'s `FindModelLevelCalls` pattern, extended to read a chained tail of `StartsAt`/`IncrementsBy`/`HasMin`/`HasMax`/`IsCyclic`.

**Tech Stack:** C# / .NET, Roslyn (`Microsoft.CodeAnalysis.CSharp`) for parsing/rewriting, Blazor for the UI, xUnit for tests.

## Global Constraints

- Every new model field is a nullable (or empty-list-default), trailing record parameter — no existing constructor call site may need to change.
- Every new `DiagramEditor` mutation method wired into `EntityNode.razor`/`RelationshipLinkLabel.razor` must be called through the file's existing `SafeEdit(...)` wrapper (verified automatically by `GestureHandlerSafeEditTests.EveryEditorMutationCall_IsWrappedInSafeEdit` — no new test needed as long as this rule is followed).
- Follow the exact five-layer pattern already used by `HasDefaultValueSql` (`docs/superpowers/specs/2026-07-28-sql-shaped-mapping-design.md`, Feature 1) unless a task below says otherwise.
- Full spec: `docs/superpowers/specs/2026-07-28-sql-shaped-mapping-design.md`.

---

## Task 1: HasComputedColumnSql — model + parser + merger + wiring

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/Model/PropertyModel.cs`
- Modify: `src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs` (add `RecognizedCallNames` entry, add `ParseComputedColumnSqls`)
- Modify: `src/EfSchemaVisualizer.Core/Parsing/DiagnosticCodes.cs`
- Create: `src/EfSchemaVisualizer.Core/Merging/ComputedColumnSqlConfig.cs`
- Modify: `src/EfSchemaVisualizer.Core/Merging/ModelMerger.cs` (add `ApplyComputedColumnSqls`)
- Modify: `src/EfSchemaVisualizer.Web/DiagramModelBuilder.cs` (wire the new parse+merge step)
- Test: `tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Merging/ModelMergerTests.cs`

**Interfaces:**
- Produces: `PropertyModel.ComputedColumnSql` (`string?`), `PropertyModel.ComputedColumnSqlIsStored` (`bool?`); `ComputedColumnSqlConfig(string EntityName, string PropertyName, string Sql, bool? IsStored)`; `FluentConfigParser.ParseComputedColumnSqls(string sourceCode) : ParseResult<IReadOnlyList<ComputedColumnSqlConfig>>`; `ModelMerger.ApplyComputedColumnSqls(EntityModel entity, IReadOnlyList<ComputedColumnSqlConfig> configs) : EntityModel`; `DiagnosticCodes.UnreadableHasComputedColumnSqlArgument`.
- Consumes: `Diagnostic`, `ParseResult<T>`, `FluentSyntaxHelpers.FindConfigurationScopes`/`FindCallsNamed`/`GetPropertyNameFor` (all existing, unchanged).

- [ ] **Step 1: Write the failing parser tests**

Add to `tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs` (near the existing `ParseDefaultValueSqls_*` tests):

```csharp
private const string ComputedColumnSqlSource = """
    public class AppDbContext : DbContext
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Order>(entity =>
            {
                entity.Property(e => e.Total).HasComputedColumnSql("[Quantity] * [UnitPrice]", stored: true);
            });
        }
    }
    """;

[Fact]
public void ParseComputedColumnSqls_ReadsSqlAndStoredArguments()
{
    var result = new FluentConfigParser().ParseComputedColumnSqls(ComputedColumnSqlSource);

    Assert.Empty(result.Diagnostics);
    var config = Assert.Single(result.Value);
    Assert.Equal("Order", config.EntityName);
    Assert.Equal("Total", config.PropertyName);
    Assert.Equal("[Quantity] * [UnitPrice]", config.Sql);
    Assert.True(config.IsStored);
}

private const string ComputedColumnSqlSourceNoStored = """
    public class AppDbContext : DbContext
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Order>(entity =>
            {
                entity.Property(e => e.Total).HasComputedColumnSql("[Quantity] * [UnitPrice]");
            });
        }
    }
    """;

[Fact]
public void ParseComputedColumnSqls_NoStoredArgument_LeavesIsStoredNull()
{
    var result = new FluentConfigParser().ParseComputedColumnSqls(ComputedColumnSqlSourceNoStored);

    Assert.Empty(result.Diagnostics);
    var config = Assert.Single(result.Value);
    Assert.Null(config.IsStored);
}

private const string ComputedColumnSqlSourceWithNonLiteralArg = """
    public class AppDbContext : DbContext
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Order>(entity =>
            {
                entity.Property(e => e.Total).HasComputedColumnSql(SomeSqlConstant);
            });
        }
    }
    """;

[Fact]
public void ParseComputedColumnSqls_NonLiteralArgument_EmitsUnreadableDiagnostic()
{
    var result = new FluentConfigParser().ParseComputedColumnSqls(ComputedColumnSqlSourceWithNonLiteralArg);

    Assert.Empty(result.Value);
    var diagnostic = Assert.Single(result.Diagnostics);
    Assert.Equal(DiagnosticCodes.UnreadableHasComputedColumnSqlArgument, diagnostic.Code);
    Assert.Equal("Order", diagnostic.EntityName);
    Assert.Equal("Total", diagnostic.PropertyName);
}
```

- [ ] **Step 2: Run the parser tests to verify they fail**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter "FullyQualifiedName~ParseComputedColumnSqls"`
Expected: FAIL (compile error — `ParseComputedColumnSqls`/`ComputedColumnSqlConfig`/`UnreadableHasComputedColumnSqlArgument` don't exist yet)

- [ ] **Step 3: Add the model fields**

In `src/EfSchemaVisualizer.Core/Model/PropertyModel.cs`, add two new trailing parameters to the `PropertyModel` record (after `OwnerNavigationProperty`):

```csharp
    string? OwnerNavigationProperty = null,
    string? ComputedColumnSql = null,
    bool? ComputedColumnSqlIsStored = null);
```

- [ ] **Step 4: Add the diagnostic code**

In `src/EfSchemaVisualizer.Core/Parsing/DiagnosticCodes.cs`, add next to `UnreadableHasDefaultSchemaArgument`:

```csharp
    public const string UnreadableHasComputedColumnSqlArgument = nameof(UnreadableHasComputedColumnSqlArgument);
```

- [ ] **Step 5: Add the config record**

Create `src/EfSchemaVisualizer.Core/Merging/ComputedColumnSqlConfig.cs`:

```csharp
namespace EfSchemaVisualizer.Core.Merging;

public sealed record ComputedColumnSqlConfig(string EntityName, string PropertyName, string Sql, bool? IsStored);
```

- [ ] **Step 6: Add the parser method**

In `src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs`, add `"HasComputedColumnSql"` to `RecognizedCallNames` (the existing flat set at the top of the class), and add this method next to `ParseDefaultValueSqls`:

```csharp
public ParseResult<IReadOnlyList<ComputedColumnSqlConfig>> ParseComputedColumnSqls(string sourceCode)
{
    var tree = CSharpSyntaxTree.ParseText(sourceCode);
    var root = tree.GetCompilationUnitRoot();

    var results = new List<ComputedColumnSqlConfig>();
    var diagnostics = new List<Diagnostic>();

    foreach (var (entityName, scope) in FluentSyntaxHelpers.FindConfigurationScopes(root))
    {
        foreach (var call in FluentSyntaxHelpers.FindCallsNamed(scope, "HasComputedColumnSql"))
        {
            var propertyName = FluentSyntaxHelpers.GetPropertyNameFor(call);

            if (propertyName is null)
            {
                diagnostics.Add(new Diagnostic(
                    DiagnosticCodes.UnresolvablePropertyName,
                    "Could not determine which property this HasComputedColumnSql call configures.",
                    entityName,
                    PropertyName: null,
                    call.Span));
                continue;
            }

            var arguments = call.ArgumentList.Arguments;
            var sqlArg = arguments.FirstOrDefault();

            if (sqlArg?.Expression is not LiteralExpressionSyntax sqlLiteral || !sqlLiteral.IsKind(SyntaxKind.StringLiteralExpression))
            {
                diagnostics.Add(new Diagnostic(
                    DiagnosticCodes.UnreadableHasComputedColumnSqlArgument,
                    "HasComputedColumnSql argument is not a string literal and could not be read.",
                    entityName,
                    propertyName,
                    (sqlArg ?? (SyntaxNode)call).Span));
                continue;
            }

            bool? isStored = null;
            if (arguments.Count >= 2
                && arguments[1].Expression is LiteralExpressionSyntax storedLiteral
                && (storedLiteral.IsKind(SyntaxKind.TrueLiteralExpression) || storedLiteral.IsKind(SyntaxKind.FalseLiteralExpression)))
            {
                isStored = storedLiteral.IsKind(SyntaxKind.TrueLiteralExpression);
            }

            results.Add(new ComputedColumnSqlConfig(entityName, propertyName, sqlLiteral.Token.ValueText, isStored));
        }
    }

    return new ParseResult<IReadOnlyList<ComputedColumnSqlConfig>>(results, diagnostics);
}
```

- [ ] **Step 7: Run the parser tests to verify they pass**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter "FullyQualifiedName~ParseComputedColumnSqls"`
Expected: PASS

- [ ] **Step 8: Write the failing merger test**

Add to `tests/EfSchemaVisualizer.Core.Tests/Merging/ModelMergerTests.cs` (near `ApplyDefaultValueSqls_*`):

```csharp
[Fact]
public void ApplyComputedColumnSqls_SetsSqlAndIsStoredOnMatchingProperty_LeavesOthersUntouched()
{
    var entity = new EntityModel("Order", new List<PropertyModel>
    {
        new("Total", "decimal", IsNullable: false, MaxLength: null),
        new("Quantity", "int", IsNullable: false, MaxLength: null),
    });

    var configs = new List<ComputedColumnSqlConfig>
    {
        new("Order", "Total", "[Quantity] * [UnitPrice]", true),
    };

    var result = ModelMerger.ApplyComputedColumnSqls(entity, configs);

    var total = result.Properties.Single(p => p.Name == "Total");
    Assert.Equal("[Quantity] * [UnitPrice]", total.ComputedColumnSql);
    Assert.True(total.ComputedColumnSqlIsStored);

    var quantity = result.Properties.Single(p => p.Name == "Quantity");
    Assert.Null(quantity.ComputedColumnSql);
}
```

- [ ] **Step 9: Run the merger test to verify it fails**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter "FullyQualifiedName~ApplyComputedColumnSqls"`
Expected: FAIL (compile error — `ApplyComputedColumnSqls` doesn't exist yet)

- [ ] **Step 10: Add the merger method**

In `src/EfSchemaVisualizer.Core/Merging/ModelMerger.cs`, add next to `ApplyDefaultValueSqls`:

```csharp
public static EntityModel ApplyComputedColumnSqls(EntityModel entity, IReadOnlyList<ComputedColumnSqlConfig> configs)
{
    var byProperty = IndexByProperty(entity.Name, configs, c => c.EntityName, c => c.PropertyName);

    var updatedProperties = entity.Properties
        .Select(property => byProperty.TryGetValue(property.Name, out var config)
            ? property with { ComputedColumnSql = config.Sql, ComputedColumnSqlIsStored = config.IsStored }
            : property)
        .ToList();

    return entity with { Properties = updatedProperties };
}
```

- [ ] **Step 11: Run the merger test to verify it passes**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter "FullyQualifiedName~ApplyComputedColumnSqls"`
Expected: PASS

- [ ] **Step 12: Wire into DiagramModelBuilder.Build**

In `src/EfSchemaVisualizer.Web/DiagramModelBuilder.cs`, add alongside the `defaultValueSqls` parse call:

```csharp
var computedColumnSqls = configParser.ParseComputedColumnSqls(configSource);
```

Add to the diagnostics-collection block:

```csharp
diagnostics.AddRange(computedColumnSqls.Diagnostics);
```

Add to the entity-pipeline `.Select(...)` chain, immediately after `.Select(entity => ModelMerger.ApplyDefaultValueSqls(entity, defaultValueSqls.Value))`:

```csharp
            .Select(entity => ModelMerger.ApplyComputedColumnSqls(entity, computedColumnSqls.Value))
```

- [ ] **Step 13: Run the full Core + Web test suites**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests && dotnet test tests/EfSchemaVisualizer.Web.Tests`
Expected: PASS, no regressions

- [ ] **Step 14: Commit**

```bash
git add -A
git commit -m "Parse and model HasComputedColumnSql"
```

---

## Task 2: HasComputedColumnSql — rewriter

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs`

**Interfaces:**
- Consumes: `ComputedColumnSqlConfig` (Task 1, unused directly here — the rewriter takes primitives, matching `SetDefaultValueSql`'s shape).
- Produces: `OnModelCreatingRewriter.SetComputedColumnSql(string sourceCode, string entityName, string propertyName, string sql, bool? isStored) : string`; `OnModelCreatingRewriter.RemoveComputedColumnSql(string sourceCode, string entityName, string propertyName) : string`.

This task extends the shared string-arg-call helper family (currently used by `HasColumnType` and `HasDefaultValueSql`) with an optional second boolean argument. Every existing call site keeps compiling because the new parameter defaults to `null`.

- [ ] **Step 1: Write the failing rewriter tests**

Add to `tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs` (near the `SetDefaultValueSql_*` tests). Reuse the existing `SourceWithPropertyButNoDefaultValue` fixture already defined in that file (an `Order` entity with a bare `Quantity` property call and no `HasDefaultValueSql`/`HasComputedColumnSql`):

```csharp
[Fact]
public void SetComputedColumnSql_BarePropertyCall_AppendsHasComputedColumnSqlWithStored()
{
    var result = new OnModelCreatingRewriter()
        .SetComputedColumnSql(SourceWithPropertyButNoDefaultValue, entityName: "Order", propertyName: "Quantity", sql: "[A] + [B]", isStored: true);

    Assert.Contains("entity.Property(e => e.Quantity).HasComputedColumnSql(\"[A] + [B]\", true)", result);
}

[Fact]
public void SetComputedColumnSql_NoIsStored_AppendsHasComputedColumnSqlWithOneArgument()
{
    var result = new OnModelCreatingRewriter()
        .SetComputedColumnSql(SourceWithPropertyButNoDefaultValue, entityName: "Order", propertyName: "Quantity", sql: "[A] + [B]", isStored: null);

    Assert.Contains("entity.Property(e => e.Quantity).HasComputedColumnSql(\"[A] + [B]\")", result);
    Assert.DoesNotContain(", true", result);
    Assert.DoesNotContain(", false", result);
}

[Fact]
public void SetComputedColumnSql_ExistingCall_MutatesArgument()
{
    var source = new OnModelCreatingRewriter()
        .SetComputedColumnSql(SourceWithPropertyButNoDefaultValue, entityName: "Order", propertyName: "Quantity", sql: "[A] + [B]", isStored: true);

    var result = new OnModelCreatingRewriter()
        .SetComputedColumnSql(source, entityName: "Order", propertyName: "Quantity", sql: "[C] + [D]", isStored: false);

    Assert.Contains("entity.Property(e => e.Quantity).HasComputedColumnSql(\"[C] + [D]\", false)", result);
    Assert.DoesNotContain("[A] + [B]", result);
}

[Fact]
public void RemoveComputedColumnSql_ExistingCall_RemovesCall_LeavesBarePropertyCall()
{
    var source = new OnModelCreatingRewriter()
        .SetComputedColumnSql(SourceWithPropertyButNoDefaultValue, entityName: "Order", propertyName: "Quantity", sql: "[A] + [B]", isStored: true);

    var result = new OnModelCreatingRewriter()
        .RemoveComputedColumnSql(source, entityName: "Order", propertyName: "Quantity");

    Assert.DoesNotContain("HasComputedColumnSql", result);
    Assert.Contains("entity.Property(e => e.Quantity)", result);
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter "FullyQualifiedName~ComputedColumnSql"`
Expected: FAIL (compile error — methods don't exist yet)

- [ ] **Step 3: Extend the shared string-arg helper family with an optional second bool argument**

In `src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs`, replace each of the following six private helpers with the versions below (same method, new trailing `bool? secondArg = null` parameter threaded through; every existing caller — `HasColumnType`'s `Set`/insert paths and `HasDefaultValueSql`'s — passes no value, so behavior for them is unchanged):

```csharp
private static string MutateExistingStringArgCall(CompilationUnitSyntax root, InvocationExpressionSyntax targetCall, string value, bool? secondArg = null)
{
    var arguments = new List<ArgumentSyntax>
    {
        SyntaxFactory.Argument(SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(value))),
    };

    if (secondArg is not null)
    {
        arguments.Add(SyntaxFactory.Argument(SyntaxFactory.LiteralExpression(
            secondArg.Value ? SyntaxKind.TrueLiteralExpression : SyntaxKind.FalseLiteralExpression)));
    }

    var newCall = targetCall.WithArgumentList(targetCall.ArgumentList.WithArguments(SyntaxFactory.SeparatedList(arguments)));

    var newRoot = root.ReplaceNode(targetCall, newCall);
    return newRoot.ToFullString();
}

private static string AppendStringArgCallToPropertyCall(CompilationUnitSyntax root, InvocationExpressionSyntax propertyCall, string methodName, string value, bool? secondArg = null)
{
    var newCall = BuildStringArgCall(propertyCall, methodName, value, secondArg);

    var newRoot = root.ReplaceNode(propertyCall, newCall);
    return newRoot.NormalizeWhitespace().ToFullString();
}

private static string InsertStringArgPropertyStatement(CompilationUnitSyntax root, SyntaxNode scope, string propertyName, string methodName, string value, bool? secondArg = null)
{
    var (block, blockReceiverName) = GetScopeBlockAndReceiver(scope);
    var propertyLambdaParam = FluentSyntaxHelpers.GetPropertyLambdaParameterName(scope);

    var newStatement = BuildStringArgPropertyStatement(blockReceiverName, propertyLambdaParam, propertyName, methodName, value, secondArg);
    var newBlock = block.AddStatements(newStatement);

    var newRoot = root.ReplaceNode(block, newBlock);
    return newRoot.NormalizeWhitespace().ToFullString();
}

private static string InsertStringArgEntityBlock(CompilationUnitSyntax root, string entityName, string propertyName, string methodName, string value, bool? secondArg = null)
{
    var method = FindOnModelCreatingMethod(root);

    var methodBody = method.Body
        ?? throw new InvalidOperationException("OnModelCreating has no method body.");

    var modelBuilderParamName = method.ParameterList.Parameters.Single().Identifier.Text;

    var propertyStatement = BuildStringArgPropertyStatement("entity", "e", propertyName, methodName, value, secondArg);
    var entityBlockStatement = BuildEntityInvocationStatement(modelBuilderParamName, entityName, SyntaxFactory.Block(propertyStatement));

    var newMethodBody = methodBody.AddStatements(entityBlockStatement);
    var newRoot = root.ReplaceNode(methodBody, newMethodBody);
    return newRoot.NormalizeWhitespace().ToFullString();
}

private static ExpressionStatementSyntax BuildStringArgPropertyStatement(string blockReceiverName, string propertyLambdaParam, string propertyName, string methodName, string value, bool? secondArg = null)
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

    return SyntaxFactory.ExpressionStatement(BuildStringArgCall(propertyCall, methodName, value, secondArg));
}

private static InvocationExpressionSyntax BuildStringArgCall(ExpressionSyntax propertyCallExpression, string methodName, string value, bool? secondArg = null)
{
    var arguments = new List<ArgumentSyntax>
    {
        SyntaxFactory.Argument(SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(value))),
    };

    if (secondArg is not null)
    {
        arguments.Add(SyntaxFactory.Argument(SyntaxFactory.LiteralExpression(
            secondArg.Value ? SyntaxKind.TrueLiteralExpression : SyntaxKind.FalseLiteralExpression)));
    }

    return SyntaxFactory.InvocationExpression(
        SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression,
            propertyCallExpression,
            SyntaxFactory.IdentifierName(methodName)),
        SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(arguments)));
}
```

Delete the earlier (incorrect, self-referential) `MutateExistingStringArgCall` draft above if you pasted it — only the second, corrected version should remain in the file.

- [ ] **Step 4: Add SetComputedColumnSql / RemoveComputedColumnSql**

Add next to `SetDefaultValueSql`/`RemoveDefaultValueSql`:

```csharp
public string SetComputedColumnSql(string sourceCode, string entityName, string propertyName, string sql, bool? isStored)
{
    var tree = CSharpSyntaxTree.ParseText(sourceCode);
    var root = tree.GetCompilationUnitRoot();

    var scopes = FindConfigScopes(root, entityName);

    var existingCall = scopes
        .SelectMany(scope => FluentSyntaxHelpers.FindCallsNamed(scope, "HasComputedColumnSql"))
        .FirstOrDefault(call => FluentSyntaxHelpers.GetPropertyNameFor(call) == propertyName);

    if (existingCall is not null)
    {
        return MutateExistingStringArgCall(root, existingCall, sql, isStored);
    }

    var existingPropertyCall = scopes
        .SelectMany(scope => FluentSyntaxHelpers.FindCallsNamed(scope, "Property"))
        .FirstOrDefault(call => FluentSyntaxHelpers.GetPropertyNameForPropertyCall(call) == propertyName);

    if (existingPropertyCall is not null)
    {
        return AppendStringArgCallToPropertyCall(root, existingPropertyCall, "HasComputedColumnSql", sql, isStored);
    }

    var existingScope = scopes.FirstOrDefault();

    if (existingScope is not null)
    {
        return InsertStringArgPropertyStatement(root, existingScope, propertyName, "HasComputedColumnSql", sql, isStored);
    }

    return InsertStringArgEntityBlock(root, entityName, propertyName, "HasComputedColumnSql", sql, isStored);
}

public string RemoveComputedColumnSql(string sourceCode, string entityName, string propertyName)
{
    return RemoveStringArgCall(sourceCode, entityName, propertyName, "HasComputedColumnSql");
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter "FullyQualifiedName~ComputedColumnSql"`
Expected: PASS

- [ ] **Step 6: Run the full Core test suite (regression check on HasColumnType/HasDefaultValueSql)**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests`
Expected: PASS, no regressions in `HasColumnType`/`HasDefaultValueSql` tests

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "Rewrite HasComputedColumnSql via the shared string-arg-call helpers"
```

---

## Task 3: HasComputedColumnSql — editor + UI

**Files:**
- Modify: `src/EfSchemaVisualizer.Web/Diagram/DiagramEditor.cs`
- Modify: `src/EfSchemaVisualizer.Web/Diagram/EntityNode.razor`
- Test: `tests/EfSchemaVisualizer.Web.Tests/Diagram/DiagramEditorPropertyPanelTests.cs`

**Interfaces:**
- Consumes: `OnModelCreatingRewriter.SetComputedColumnSql`/`RemoveComputedColumnSql` (Task 2), `DiagramEditor.ResolveDeclaringEntity` (existing), `DiagramEditResult` (existing).
- Produces: `DiagramEditor.SetComputedColumnSql(string entityName, string propertyName, string? sql, bool? isStored) : DiagramEditResult`.

- [ ] **Step 1: Write the failing editor tests**

Add to `tests/EfSchemaVisualizer.Web.Tests/Diagram/DiagramEditorPropertyPanelTests.cs` (this file already defines `ClassSource`/`ConfigSource` fixtures for a `Person` entity with an `Id`/`Name` property — reuse them):

```csharp
[Fact]
public void SetComputedColumnSql_NoExistingConfig_InsertsHasComputedColumnSql()
{
    var editor = new DiagramEditor(ClassSource, ConfigSource);

    var result = editor.SetComputedColumnSql("Person", "Name", "UPPER([Name])", true);

    Assert.True(result.Success);
    var property = editor.Current.Entities.Single().Properties.Single(p => p.Name == "Name");
    Assert.Equal("UPPER([Name])", property.ComputedColumnSql);
    Assert.True(property.ComputedColumnSqlIsStored);
    Assert.Contains("HasComputedColumnSql(\"UPPER([Name])\", true)", editor.ConfigSource);
}

[Fact]
public void SetComputedColumnSql_ClearingExistingConfig_RemovesHasComputedColumnSql()
{
    var editor = new DiagramEditor(ClassSource, ConfigSource);
    editor.SetComputedColumnSql("Person", "Name", "UPPER([Name])", true);

    var result = editor.SetComputedColumnSql("Person", "Name", null, null);

    Assert.True(result.Success);
    Assert.Null(editor.Current.Entities.Single().Properties.Single(p => p.Name == "Name").ComputedColumnSql);
    Assert.DoesNotContain("HasComputedColumnSql", editor.ConfigSource);
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/EfSchemaVisualizer.Web.Tests --filter "FullyQualifiedName~SetComputedColumnSql"`
Expected: FAIL (compile error — `DiagramEditor.SetComputedColumnSql` doesn't exist yet)

- [ ] **Step 3: Add the editor method**

In `src/EfSchemaVisualizer.Web/Diagram/DiagramEditor.cs`, add next to `SetDefaultValueSql`:

```csharp
public DiagramEditResult SetComputedColumnSql(string entityName, string propertyName, string? sql, bool? isStored)
{
    var entity = Current.Entities.FirstOrDefault(e => e.Name == entityName);
    if (entity is null)
    {
        return DiagramEditResult.Fail($"Entity '{entityName}' not found.");
    }

    var property = entity.Properties.FirstOrDefault(p => p.Name == propertyName);
    if (property is null)
    {
        return DiagramEditResult.Fail($"Property '{propertyName}' not found on '{entityName}'.");
    }

    var normalizedSql = string.IsNullOrWhiteSpace(sql) ? null : sql.Trim();
    if (normalizedSql == property.ComputedColumnSql && isStored == property.ComputedColumnSqlIsStored)
    {
        return DiagramEditResult.Ok();
    }

    var owningEntityName = ResolveDeclaringEntity(entityName, propertyName);
    var newConfigSource = normalizedSql is null
        ? _configRewriter.RemoveComputedColumnSql(ConfigSource, owningEntityName, propertyName)
        : _configRewriter.SetComputedColumnSql(ConfigSource, owningEntityName, propertyName, normalizedSql, isStored);
    Apply(ClassSource, newConfigSource);
    return DiagramEditResult.Ok();
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/EfSchemaVisualizer.Web.Tests --filter "FullyQualifiedName~SetComputedColumnSql"`
Expected: PASS

- [ ] **Step 5: Add the UI fields**

In `src/EfSchemaVisualizer.Web/Diagram/EntityNode.razor`, in the property detail panel, right after the existing "Default value SQL" `<label>` block (around line 269-275), add:

```razor
                                <label style="display: block;">
                                    Computed column SQL:
                                    <input style="width: 100px;" value="@property.ComputedColumnSql" placeholder="(none)"
                                           @onchange="e => CommitComputedColumnSql(property, e.Value?.ToString())"
                                           @onpointerdown:stopPropagation="true"
                                           @onmousedown:stopPropagation="true" />
                                </label>
                                <label style="display: block;">
                                    <input type="checkbox" checked="@(property.ComputedColumnSqlIsStored ?? false)"
                                           @onchange="e => CommitComputedColumnSqlIsStored(property, (bool)(e.Value ?? false))"
                                           @onpointerdown:stopPropagation="true"
                                           @onmousedown:stopPropagation="true" />
                                    Stored
                                </label>
```

In the `@code` block, right after the existing `CommitDefaultValueSql` method, add:

```csharp
    private async Task CommitComputedColumnSql(PropertyModel property, string? newSql)
    {
        var result = SafeEdit(() => EditContext.Editor.SetComputedColumnSql(Node.Entity.Name, property.Name, newSql, property.ComputedColumnSqlIsStored));
        if (result.Success)
        {
            _propertyErrors.Remove(property.Name);
            await EditContext.NotifyChangedAsync();
        }
        else
        {
            _propertyErrors[property.Name] = result.Error!;
        }
    }

    private async Task CommitComputedColumnSqlIsStored(PropertyModel property, bool isStored)
    {
        var result = SafeEdit(() => EditContext.Editor.SetComputedColumnSql(Node.Entity.Name, property.Name, property.ComputedColumnSql, isStored));
        if (result.Success)
        {
            _propertyErrors.Remove(property.Name);
            await EditContext.NotifyChangedAsync();
        }
        else
        {
            _propertyErrors[property.Name] = result.Error!;
        }
    }
```

- [ ] **Step 6: Build the Web project and run its full test suite**

Run: `dotnet build src/EfSchemaVisualizer.Web && dotnet test tests/EfSchemaVisualizer.Web.Tests`
Expected: builds clean, all tests PASS (including `GestureHandlerSafeEditTests`, which now also covers the two new handlers automatically)

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "Add HasComputedColumnSql editing to DiagramEditor and EntityNode UI"
```

---

## Task 4: HasCheckConstraint — model + parser + merger + wiring

**Files:**
- Create: `src/EfSchemaVisualizer.Core/Model/CheckConstraintModel.cs`
- Modify: `src/EfSchemaVisualizer.Core/Model/EntityModel.cs`
- Modify: `src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs`
- Modify: `src/EfSchemaVisualizer.Core/Parsing/DiagnosticCodes.cs`
- Create: `src/EfSchemaVisualizer.Core/Merging/CheckConstraintConfig.cs`
- Modify: `src/EfSchemaVisualizer.Core/Merging/ModelMerger.cs`
- Modify: `src/EfSchemaVisualizer.Web/DiagramModelBuilder.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Merging/ModelMergerTests.cs`

**Interfaces:**
- Produces: `CheckConstraintModel(string Name, string Sql)`; `EntityModel.CheckConstraints` (`IReadOnlyList<CheckConstraintModel>`, defaults to empty); `CheckConstraintConfig(string EntityName, string Name, string Sql)`; `FluentConfigParser.ParseCheckConstraints(string sourceCode) : ParseResult<IReadOnlyList<CheckConstraintConfig>>`; `ModelMerger.ApplyCheckConstraints(EntityModel entity, IReadOnlyList<CheckConstraintConfig> configs) : EntityModel`; `DiagnosticCodes.UnreadableHasCheckConstraintArgument`.

- [ ] **Step 1: Write the failing parser tests**

Add to `tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs`:

```csharp
private const string CheckConstraintSource = """
    public class AppDbContext : DbContext
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Order>(entity =>
            {
                entity.HasCheckConstraint("CK_Order_Quantity", "[Quantity] >= 0");
                entity.HasCheckConstraint("CK_Order_Total", "[Total] >= 0");
            });
        }
    }
    """;

[Fact]
public void ParseCheckConstraints_MultiplePerEntity_ReadsAll()
{
    var result = new FluentConfigParser().ParseCheckConstraints(CheckConstraintSource);

    Assert.Empty(result.Diagnostics);
    Assert.Equal(2, result.Value.Count);
    Assert.Contains(result.Value, c => c.EntityName == "Order" && c.Name == "CK_Order_Quantity" && c.Sql == "[Quantity] >= 0");
    Assert.Contains(result.Value, c => c.EntityName == "Order" && c.Name == "CK_Order_Total" && c.Sql == "[Total] >= 0");
}

private const string CheckConstraintSourceWithNonLiteralArg = """
    public class AppDbContext : DbContext
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Order>(entity =>
            {
                entity.HasCheckConstraint(SomeNameConstant, "[Quantity] >= 0");
            });
        }
    }
    """;

[Fact]
public void ParseCheckConstraints_NonLiteralArgument_EmitsUnreadableDiagnostic()
{
    var result = new FluentConfigParser().ParseCheckConstraints(CheckConstraintSourceWithNonLiteralArg);

    Assert.Empty(result.Value);
    var diagnostic = Assert.Single(result.Diagnostics);
    Assert.Equal(DiagnosticCodes.UnreadableHasCheckConstraintArgument, diagnostic.Code);
    Assert.Equal("Order", diagnostic.EntityName);
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter "FullyQualifiedName~ParseCheckConstraints"`
Expected: FAIL (compile error)

- [ ] **Step 3: Add the model**

Create `src/EfSchemaVisualizer.Core/Model/CheckConstraintModel.cs`:

```csharp
namespace EfSchemaVisualizer.Core.Model;

public sealed record CheckConstraintModel(string Name, string Sql);
```

In `src/EfSchemaVisualizer.Core/Model/EntityModel.cs`, add a new trailing constructor parameter after `KeyName`:

```csharp
    string? KeyName = null,
    IReadOnlyList<CheckConstraintModel>? CheckConstraints = null)
{
    public IReadOnlyList<string> KeyPropertyNames { get; init; } = KeyPropertyNames ?? new List<string>();
    public IReadOnlyList<IndexModel> Indexes { get; init; } = Indexes ?? new List<IndexModel>();
    public IReadOnlyList<IReadOnlyList<string>> AlternateKeys { get; init; } = AlternateKeys ?? new List<IReadOnlyList<string>>();
    public IReadOnlyList<string> SplitTables { get; init; } = SplitTables ?? new List<string>();
    public IReadOnlyList<CheckConstraintModel> CheckConstraints { get; init; } = CheckConstraints ?? new List<CheckConstraintModel>();
}
```

(Remove the old closing `}` for the record body and the old `)` line ending at `KeyName = null)` — the new parameter list ends at `CheckConstraints = null)` instead.)

- [ ] **Step 4: Add the diagnostic code**

In `src/EfSchemaVisualizer.Core/Parsing/DiagnosticCodes.cs`, add:

```csharp
    public const string UnreadableHasCheckConstraintArgument = nameof(UnreadableHasCheckConstraintArgument);
```

- [ ] **Step 5: Add the config record**

Create `src/EfSchemaVisualizer.Core/Merging/CheckConstraintConfig.cs`:

```csharp
namespace EfSchemaVisualizer.Core.Merging;

public sealed record CheckConstraintConfig(string EntityName, string Name, string Sql);
```

- [ ] **Step 6: Add the parser method**

In `src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs`, add `"HasCheckConstraint"` to `RecognizedCallNames`, and add:

```csharp
public ParseResult<IReadOnlyList<CheckConstraintConfig>> ParseCheckConstraints(string sourceCode)
{
    var tree = CSharpSyntaxTree.ParseText(sourceCode);
    var root = tree.GetCompilationUnitRoot();

    var results = new List<CheckConstraintConfig>();
    var diagnostics = new List<Diagnostic>();

    foreach (var (entityName, scope) in FluentSyntaxHelpers.FindConfigurationScopes(root))
    {
        foreach (var call in FluentSyntaxHelpers.FindCallsNamed(scope, "HasCheckConstraint"))
        {
            var arguments = call.ArgumentList.Arguments;

            if (arguments.Count < 2
                || arguments[0].Expression is not LiteralExpressionSyntax nameLiteral || !nameLiteral.IsKind(SyntaxKind.StringLiteralExpression)
                || arguments[1].Expression is not LiteralExpressionSyntax sqlLiteral || !sqlLiteral.IsKind(SyntaxKind.StringLiteralExpression))
            {
                diagnostics.Add(new Diagnostic(
                    DiagnosticCodes.UnreadableHasCheckConstraintArgument,
                    "HasCheckConstraint arguments are not both string literals and could not be read.",
                    entityName,
                    PropertyName: null,
                    call.Span));
                continue;
            }

            results.Add(new CheckConstraintConfig(entityName, nameLiteral.Token.ValueText, sqlLiteral.Token.ValueText));
        }
    }

    return new ParseResult<IReadOnlyList<CheckConstraintConfig>>(results, diagnostics);
}
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter "FullyQualifiedName~ParseCheckConstraints"`
Expected: PASS

- [ ] **Step 8: Write the failing merger test**

Add to `tests/EfSchemaVisualizer.Core.Tests/Merging/ModelMergerTests.cs`:

```csharp
[Fact]
public void ApplyCheckConstraints_MultipleForSameEntity_SetsWholeList()
{
    var entity = new EntityModel("Order", new List<PropertyModel>
    {
        new("Id", "int", IsNullable: false, MaxLength: null),
    });

    var configs = new List<CheckConstraintConfig>
    {
        new("Order", "CK_Order_Quantity", "[Quantity] >= 0"),
        new("Order", "CK_Order_Total", "[Total] >= 0"),
        new("OtherEntity", "CK_Other", "1 = 1"),
    };

    var result = ModelMerger.ApplyCheckConstraints(entity, configs);

    Assert.Equal(2, result.CheckConstraints.Count);
    Assert.Contains(result.CheckConstraints, c => c.Name == "CK_Order_Quantity" && c.Sql == "[Quantity] >= 0");
    Assert.Contains(result.CheckConstraints, c => c.Name == "CK_Order_Total" && c.Sql == "[Total] >= 0");
}
```

- [ ] **Step 9: Run the merger test to verify it fails**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter "FullyQualifiedName~ApplyCheckConstraints"`
Expected: FAIL (compile error)

- [ ] **Step 10: Add the merger method**

In `src/EfSchemaVisualizer.Core/Merging/ModelMerger.cs`, add:

```csharp
public static EntityModel ApplyCheckConstraints(EntityModel entity, IReadOnlyList<CheckConstraintConfig> configs)
{
    var constraints = configs
        .Where(c => c.EntityName == entity.Name)
        .Select(c => new CheckConstraintModel(c.Name, c.Sql))
        .ToList();

    return constraints.Count == 0 ? entity : entity with { CheckConstraints = constraints };
}
```

- [ ] **Step 11: Run the merger test to verify it passes**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter "FullyQualifiedName~ApplyCheckConstraints"`
Expected: PASS

- [ ] **Step 12: Wire into DiagramModelBuilder.Build**

In `src/EfSchemaVisualizer.Web/DiagramModelBuilder.cs`, add:

```csharp
var checkConstraints = configParser.ParseCheckConstraints(configSource);
```

```csharp
diagnostics.AddRange(checkConstraints.Diagnostics);
```

Add to the entity `.Select(...)` pipeline, right after the `ApplyComputedColumnSqls` line added in Task 1:

```csharp
            .Select(entity => ModelMerger.ApplyCheckConstraints(entity, checkConstraints.Value))
```

- [ ] **Step 13: Run the full Core + Web suites**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests && dotnet test tests/EfSchemaVisualizer.Web.Tests`
Expected: PASS, no regressions

- [ ] **Step 14: Commit**

```bash
git add -A
git commit -m "Parse and model HasCheckConstraint"
```

---

## Task 5: HasCheckConstraint — rewriter

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs`

**Interfaces:**
- Produces: `OnModelCreatingRewriter.AddCheckConstraint(string sourceCode, string entityName, string name, string sql) : string`; `OnModelCreatingRewriter.SetCheckConstraint(string sourceCode, string entityName, string oldName, string newName, string newSql) : string`; `OnModelCreatingRewriter.RemoveCheckConstraint(string sourceCode, string entityName, string name) : string`.
- Consumes: `FindConfigScopes` (existing private helper), `GetScopeBlockAndReceiver`, `FindOnModelCreatingMethod`, `BuildEntityInvocationStatement` (all existing private helpers in the same class).

- [ ] **Step 1: Write the failing rewriter tests**

Add to `tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs`. First add this fixture near the top of the file (alongside other `SourceWith*` constants), an `Order` entity with an existing empty config scope but no check constraints:

```csharp
private const string SourceWithEmptyOrderScope = """
    public class AppDbContext : DbContext
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Order>(entity =>
            {
                entity.HasKey(e => e.Id);
            });
        }
    }
    """;
```

Then add the tests:

```csharp
[Fact]
public void AddCheckConstraint_NoExisting_InsertsStatement()
{
    var result = new OnModelCreatingRewriter()
        .AddCheckConstraint(SourceWithEmptyOrderScope, entityName: "Order", name: "CK_Order_Quantity", sql: "[Quantity] >= 0");

    Assert.Contains("entity.HasCheckConstraint(\"CK_Order_Quantity\", \"[Quantity] >= 0\")", result);
}

[Fact]
public void AddCheckConstraint_SecondOne_AppendsWithoutRemovingFirst()
{
    var source = new OnModelCreatingRewriter()
        .AddCheckConstraint(SourceWithEmptyOrderScope, entityName: "Order", name: "CK_Order_Quantity", sql: "[Quantity] >= 0");

    var result = new OnModelCreatingRewriter()
        .AddCheckConstraint(source, entityName: "Order", name: "CK_Order_Total", sql: "[Total] >= 0");

    Assert.Contains("CK_Order_Quantity", result);
    Assert.Contains("CK_Order_Total", result);
}

[Fact]
public void SetCheckConstraint_ExistingName_ReplacesNameAndSql()
{
    var source = new OnModelCreatingRewriter()
        .AddCheckConstraint(SourceWithEmptyOrderScope, entityName: "Order", name: "CK_Order_Quantity", sql: "[Quantity] >= 0");

    var result = new OnModelCreatingRewriter()
        .SetCheckConstraint(source, entityName: "Order", oldName: "CK_Order_Quantity", newName: "CK_Order_Qty", newSql: "[Quantity] > 0");

    Assert.Contains("entity.HasCheckConstraint(\"CK_Order_Qty\", \"[Quantity] > 0\")", result);
    Assert.DoesNotContain("CK_Order_Quantity", result);
}

[Fact]
public void RemoveCheckConstraint_ExistingName_RemovesOnlyThatStatement()
{
    var source = new OnModelCreatingRewriter()
        .AddCheckConstraint(SourceWithEmptyOrderScope, entityName: "Order", name: "CK_Order_Quantity", sql: "[Quantity] >= 0");
    source = new OnModelCreatingRewriter()
        .AddCheckConstraint(source, entityName: "Order", name: "CK_Order_Total", sql: "[Total] >= 0");

    var result = new OnModelCreatingRewriter()
        .RemoveCheckConstraint(source, entityName: "Order", name: "CK_Order_Quantity");

    Assert.DoesNotContain("CK_Order_Quantity", result);
    Assert.Contains("CK_Order_Total", result);
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter "FullyQualifiedName~CheckConstraint"`
Expected: FAIL (compile error — methods don't exist yet)

- [ ] **Step 3: Add the rewriter methods**

In `src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs`, add:

```csharp
public string AddCheckConstraint(string sourceCode, string entityName, string name, string sql)
{
    var tree = CSharpSyntaxTree.ParseText(sourceCode);
    var root = tree.GetCompilationUnitRoot();

    var scopes = FindConfigScopes(root, entityName);
    var existingScope = scopes.FirstOrDefault();

    if (existingScope is not null)
    {
        var (block, blockReceiverName) = GetScopeBlockAndReceiver(existingScope);
        var newStatement = BuildCheckConstraintStatement(blockReceiverName, name, sql);
        var newBlock = block.AddStatements(newStatement);

        var newRoot = root.ReplaceNode(block, newBlock);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    var method = FindOnModelCreatingMethod(root);
    var methodBody = method.Body
        ?? throw new InvalidOperationException("OnModelCreating has no method body.");

    var modelBuilderParamName = method.ParameterList.Parameters.Single().Identifier.Text;
    var checkConstraintStatement = BuildCheckConstraintStatement("entity", name, sql);
    var entityBlockStatement = BuildEntityInvocationStatement(modelBuilderParamName, entityName, SyntaxFactory.Block(checkConstraintStatement));

    var newMethodBody = methodBody.AddStatements(entityBlockStatement);
    var newRoot2 = root.ReplaceNode(methodBody, newMethodBody);
    return newRoot2.NormalizeWhitespace().ToFullString();
}

public string SetCheckConstraint(string sourceCode, string entityName, string oldName, string newName, string newSql)
{
    var tree = CSharpSyntaxTree.ParseText(sourceCode);
    var root = tree.GetCompilationUnitRoot();

    var scopes = FindConfigScopes(root, entityName);

    var existingCall = scopes
        .SelectMany(scope => FluentSyntaxHelpers.FindCallsNamed(scope, "HasCheckConstraint"))
        .FirstOrDefault(call => IsCheckConstraintNamed(call, oldName));

    if (existingCall is null)
    {
        return sourceCode;
    }

    var newArguments = SyntaxFactory.SeparatedList(new[]
    {
        SyntaxFactory.Argument(SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(newName))),
        SyntaxFactory.Argument(SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(newSql))),
    });

    var newCall = existingCall.WithArgumentList(existingCall.ArgumentList.WithArguments(newArguments));
    var newRoot = root.ReplaceNode(existingCall, newCall);
    return newRoot.NormalizeWhitespace().ToFullString();
}

public string RemoveCheckConstraint(string sourceCode, string entityName, string name)
{
    var tree = CSharpSyntaxTree.ParseText(sourceCode);
    var root = tree.GetCompilationUnitRoot();

    var scopes = FindConfigScopes(root, entityName);

    var existingCall = scopes
        .SelectMany(scope => FluentSyntaxHelpers.FindCallsNamed(scope, "HasCheckConstraint"))
        .FirstOrDefault(call => IsCheckConstraintNamed(call, name));

    if (existingCall is null)
    {
        return sourceCode;
    }

    var statement = existingCall.Ancestors().OfType<ExpressionStatementSyntax>().First();
    var newRoot = root.RemoveNode(statement, SyntaxRemoveOptions.KeepNoTrivia)!;
    return newRoot.NormalizeWhitespace().ToFullString();
}

private static bool IsCheckConstraintNamed(InvocationExpressionSyntax call, string name)
{
    var nameArg = call.ArgumentList.Arguments.FirstOrDefault();
    return nameArg?.Expression is LiteralExpressionSyntax literal
        && literal.IsKind(SyntaxKind.StringLiteralExpression)
        && literal.Token.ValueText == name;
}

private static ExpressionStatementSyntax BuildCheckConstraintStatement(string blockReceiverName, string name, string sql)
{
    return SyntaxFactory.ExpressionStatement(
        SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                SyntaxFactory.IdentifierName(blockReceiverName),
                SyntaxFactory.IdentifierName("HasCheckConstraint")),
            SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(new[]
            {
                SyntaxFactory.Argument(SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(name))),
                SyntaxFactory.Argument(SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(sql))),
            }))));
}
```

This mirrors `RemoveIndex`'s removal pattern exactly (`.Ancestors().OfType<ExpressionStatementSyntax>().First()` then `RemoveNode`) — safe here because `HasCheckConstraint` statements, like `HasIndex` statements, always live inside an `Entity<T>(entity => { ... })` lambda block, never as a bare top-level `GlobalStatementSyntax` (unlike the whole-entity removal case fixed by F1).

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter "FullyQualifiedName~CheckConstraint"`
Expected: PASS

- [ ] **Step 5: Run the full Core test suite**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests`
Expected: PASS, no regressions

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "Add HasCheckConstraint rewriter support (add/set/remove by name)"
```

---

## Task 6: HasCheckConstraint — editor + UI

**Files:**
- Modify: `src/EfSchemaVisualizer.Web/Diagram/DiagramEditor.cs`
- Modify: `src/EfSchemaVisualizer.Web/Diagram/EntityNode.razor`
- Test: `tests/EfSchemaVisualizer.Web.Tests/Diagram/DiagramEditorPropertyPanelTests.cs`

**Interfaces:**
- Consumes: `OnModelCreatingRewriter.AddCheckConstraint`/`SetCheckConstraint`/`RemoveCheckConstraint` (Task 5).
- Produces: `DiagramEditor.AddCheckConstraint(string entityName, string name, string sql) : DiagramEditResult`; `DiagramEditor.SetCheckConstraint(string entityName, string oldName, string newName, string newSql) : DiagramEditResult`; `DiagramEditor.RemoveCheckConstraint(string entityName, string name) : DiagramEditResult`.

- [ ] **Step 1: Write the failing editor tests**

Add to `tests/EfSchemaVisualizer.Web.Tests/Diagram/DiagramEditorPropertyPanelTests.cs`:

```csharp
[Fact]
public void AddCheckConstraint_NewName_AddsToEntity()
{
    var editor = new DiagramEditor(ClassSource, ConfigSource);

    var result = editor.AddCheckConstraint("Person", "CK_Person_Name", "LEN([Name]) > 0");

    Assert.True(result.Success);
    var constraint = editor.Current.Entities.Single().CheckConstraints.Single();
    Assert.Equal("CK_Person_Name", constraint.Name);
    Assert.Equal("LEN([Name]) > 0", constraint.Sql);
}

[Fact]
public void AddCheckConstraint_DuplicateName_Fails()
{
    var editor = new DiagramEditor(ClassSource, ConfigSource);
    editor.AddCheckConstraint("Person", "CK_Person_Name", "LEN([Name]) > 0");

    var result = editor.AddCheckConstraint("Person", "CK_Person_Name", "1 = 1");

    Assert.False(result.Success);
    Assert.Single(editor.Current.Entities.Single().CheckConstraints);
}

[Fact]
public void RemoveCheckConstraint_ExistingName_RemovesIt()
{
    var editor = new DiagramEditor(ClassSource, ConfigSource);
    editor.AddCheckConstraint("Person", "CK_Person_Name", "LEN([Name]) > 0");

    var result = editor.RemoveCheckConstraint("Person", "CK_Person_Name");

    Assert.True(result.Success);
    Assert.Empty(editor.Current.Entities.Single().CheckConstraints);
}

[Fact]
public void SetCheckConstraint_RenamesAndUpdatesSql()
{
    var editor = new DiagramEditor(ClassSource, ConfigSource);
    editor.AddCheckConstraint("Person", "CK_Person_Name", "LEN([Name]) > 0");

    var result = editor.SetCheckConstraint("Person", "CK_Person_Name", "CK_Person_NonEmptyName", "LEN([Name]) >= 1");

    Assert.True(result.Success);
    var constraint = editor.Current.Entities.Single().CheckConstraints.Single();
    Assert.Equal("CK_Person_NonEmptyName", constraint.Name);
    Assert.Equal("LEN([Name]) >= 1", constraint.Sql);
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/EfSchemaVisualizer.Web.Tests --filter "FullyQualifiedName~CheckConstraint"`
Expected: FAIL (compile error)

- [ ] **Step 3: Add the editor methods**

In `src/EfSchemaVisualizer.Web/Diagram/DiagramEditor.cs`, add:

```csharp
public DiagramEditResult AddCheckConstraint(string entityName, string name, string sql)
{
    var entity = Current.Entities.FirstOrDefault(e => e.Name == entityName);
    if (entity is null)
    {
        return DiagramEditResult.Fail($"Entity '{entityName}' not found.");
    }

    if (string.IsNullOrWhiteSpace(name))
    {
        return DiagramEditResult.Fail("Check constraint name cannot be empty.");
    }

    if (entity.CheckConstraints.Any(c => c.Name == name))
    {
        return DiagramEditResult.Fail($"'{entityName}' already has a check constraint named '{name}'.");
    }

    var newConfigSource = _configRewriter.AddCheckConstraint(ConfigSource, entityName, name, sql);
    Apply(ClassSource, newConfigSource);
    return DiagramEditResult.Ok();
}

public DiagramEditResult SetCheckConstraint(string entityName, string oldName, string newName, string newSql)
{
    var entity = Current.Entities.FirstOrDefault(e => e.Name == entityName);
    if (entity is null)
    {
        return DiagramEditResult.Fail($"Entity '{entityName}' not found.");
    }

    if (!entity.CheckConstraints.Any(c => c.Name == oldName))
    {
        return DiagramEditResult.Fail($"'{entityName}' has no check constraint named '{oldName}'.");
    }

    if (string.IsNullOrWhiteSpace(newName))
    {
        return DiagramEditResult.Fail("Check constraint name cannot be empty.");
    }

    if (newName != oldName && entity.CheckConstraints.Any(c => c.Name == newName))
    {
        return DiagramEditResult.Fail($"'{entityName}' already has a check constraint named '{newName}'.");
    }

    var newConfigSource = _configRewriter.SetCheckConstraint(ConfigSource, entityName, oldName, newName, newSql);
    Apply(ClassSource, newConfigSource);
    return DiagramEditResult.Ok();
}

public DiagramEditResult RemoveCheckConstraint(string entityName, string name)
{
    var entity = Current.Entities.FirstOrDefault(e => e.Name == entityName);
    if (entity is null)
    {
        return DiagramEditResult.Fail($"Entity '{entityName}' not found.");
    }

    if (!entity.CheckConstraints.Any(c => c.Name == name))
    {
        return DiagramEditResult.Fail($"'{entityName}' has no check constraint named '{name}'.");
    }

    var newConfigSource = _configRewriter.RemoveCheckConstraint(ConfigSource, entityName, name);
    Apply(ClassSource, newConfigSource);
    return DiagramEditResult.Ok();
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/EfSchemaVisualizer.Web.Tests --filter "FullyQualifiedName~CheckConstraint"`
Expected: PASS

- [ ] **Step 5: Add the UI list section**

In `src/EfSchemaVisualizer.Web/Diagram/EntityNode.razor`, add a new section after the existing "Indexes:" block (around line 277-300 — find the `@foreach (var index in Node.Entity.Indexes)` block and add this immediately after its closing `}`):

```razor
                            <div style="font-size: 0.8em; margin-top: 4px;">
                                <div>Check constraints:</div>
                                @foreach (var constraint in Node.Entity.CheckConstraints)
                                {
                                    <div style="display: flex; gap: 4px; align-items: center;">
                                        <input style="width: 80px;" value="@constraint.Name"
                                               @onchange="e => CommitCheckConstraintName(constraint, e.Value?.ToString())"
                                               @onpointerdown:stopPropagation="true" @onmousedown:stopPropagation="true" />
                                        <input style="width: 120px;" value="@constraint.Sql"
                                               @onchange="e => CommitCheckConstraintSql(constraint, e.Value?.ToString())"
                                               @onpointerdown:stopPropagation="true" @onmousedown:stopPropagation="true" />
                                        <button @onclick="() => RemoveCheckConstraint(constraint)"
                                                @onpointerdown:stopPropagation="true" @onmousedown:stopPropagation="true">x</button>
                                    </div>
                                }
                                <button @onclick="AddCheckConstraint"
                                        @onpointerdown:stopPropagation="true" @onmousedown:stopPropagation="true">Add check constraint</button>
                                @if (_checkConstraintError is not null)
                                {
                                    <div style="color: red;">@_checkConstraintError</div>
                                }
                            </div>
```

In the `@code` block, add (near `_indexError`):

```csharp
    private string? _checkConstraintError;

    private async Task AddCheckConstraint()
    {
        var name = $"CK_{Node.Entity.Name}_{Node.Entity.CheckConstraints.Count + 1}";
        var result = SafeEdit(() => EditContext.Editor.AddCheckConstraint(Node.Entity.Name, name, "1 = 1"));
        if (result.Success)
        {
            _checkConstraintError = null;
            await EditContext.NotifyChangedAsync();
        }
        else
        {
            _checkConstraintError = result.Error;
        }
    }

    private async Task CommitCheckConstraintName(CheckConstraintModel constraint, string? newName)
    {
        if (newName is null)
        {
            return;
        }

        var result = SafeEdit(() => EditContext.Editor.SetCheckConstraint(Node.Entity.Name, constraint.Name, newName, constraint.Sql));
        if (result.Success)
        {
            _checkConstraintError = null;
            await EditContext.NotifyChangedAsync();
        }
        else
        {
            _checkConstraintError = result.Error;
        }
    }

    private async Task CommitCheckConstraintSql(CheckConstraintModel constraint, string? newSql)
    {
        if (newSql is null)
        {
            return;
        }

        var result = SafeEdit(() => EditContext.Editor.SetCheckConstraint(Node.Entity.Name, constraint.Name, constraint.Name, newSql));
        if (result.Success)
        {
            _checkConstraintError = null;
            await EditContext.NotifyChangedAsync();
        }
        else
        {
            _checkConstraintError = result.Error;
        }
    }

    private async Task RemoveCheckConstraint(CheckConstraintModel constraint)
    {
        var result = SafeEdit(() => EditContext.Editor.RemoveCheckConstraint(Node.Entity.Name, constraint.Name));
        if (result.Success)
        {
            _checkConstraintError = null;
            await EditContext.NotifyChangedAsync();
        }
        else
        {
            _checkConstraintError = result.Error;
        }
    }
```

Add `@using EfSchemaVisualizer.Core.Model` at the top of the file if `CheckConstraintModel` isn't already in scope (check the existing `@using` list first — `PropertyModel`/`EntityModel` from the same namespace are already used elsewhere in this file, so likely no change is needed).

- [ ] **Step 6: Build and run the full Web test suite**

Run: `dotnet build src/EfSchemaVisualizer.Web && dotnet test tests/EfSchemaVisualizer.Web.Tests`
Expected: builds clean, all tests PASS

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "Add HasCheckConstraint editing to DiagramEditor and EntityNode UI"
```

---

## Task 7: HasSequence — model + parser + merger + wiring

**Files:**
- Create: `src/EfSchemaVisualizer.Core/Model/SequenceModel.cs`
- Modify: `src/EfSchemaVisualizer.Web/DiagramModelBuilder.cs` (add `Sequences` to `DiagramModelResult`)
- Modify: `src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs`
- Modify: `src/EfSchemaVisualizer.Core/Parsing/DiagnosticCodes.cs`
- Create: `src/EfSchemaVisualizer.Core/Merging/SequenceConfig.cs`
- Modify: `src/EfSchemaVisualizer.Core/Merging/ModelMerger.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Merging/ModelMergerTests.cs`

**Interfaces:**
- Produces: `SequenceModel(string Name, string? Schema, string? ClrType, long? StartsAt, int? IncrementsBy, long? MinValue, long? MaxValue, bool? IsCyclic)`; `SequenceConfig` (same shape, in `Merging`); `FluentConfigParser.ParseSequences(string sourceCode) : ParseResult<IReadOnlyList<SequenceConfig>>`; `ModelMerger.ApplySequences(IReadOnlyList<SequenceConfig> configs) : IReadOnlyList<SequenceModel>`; `DiagramModelResult.Sequences` (`IReadOnlyList<SequenceModel>`); `DiagnosticCodes.UnreadableHasSequenceArgument`.

- [ ] **Step 1: Write the failing parser tests**

Add to `tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs`:

```csharp
private const string SequenceSource = """
    public class AppDbContext : DbContext
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.HasSequence<int>("OrderNumbers", schema: "shared")
                .StartsAt(1000)
                .IncrementsBy(5)
                .HasMin(1)
                .HasMax(1000000)
                .IsCyclic();
        }
    }
    """;

[Fact]
public void ParseSequences_ReadsNameSchemaTypeAndAllChainedOptions()
{
    var result = new FluentConfigParser().ParseSequences(SequenceSource);

    Assert.Empty(result.Diagnostics);
    var sequence = Assert.Single(result.Value);
    Assert.Equal("OrderNumbers", sequence.Name);
    Assert.Equal("shared", sequence.Schema);
    Assert.Equal("int", sequence.ClrType);
    Assert.Equal(1000, sequence.StartsAt);
    Assert.Equal(5, sequence.IncrementsBy);
    Assert.Equal(1, sequence.MinValue);
    Assert.Equal(1000000, sequence.MaxValue);
    Assert.True(sequence.IsCyclic);
}

private const string SequenceSourceMinimal = """
    public class AppDbContext : DbContext
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.HasSequence("Simple");
        }
    }
    """;

[Fact]
public void ParseSequences_NameOnly_LeavesOptionalFieldsNull()
{
    var result = new FluentConfigParser().ParseSequences(SequenceSourceMinimal);

    Assert.Empty(result.Diagnostics);
    var sequence = Assert.Single(result.Value);
    Assert.Equal("Simple", sequence.Name);
    Assert.Null(sequence.Schema);
    Assert.Null(sequence.ClrType);
    Assert.Null(sequence.StartsAt);
    Assert.Null(sequence.IncrementsBy);
    Assert.Null(sequence.MinValue);
    Assert.Null(sequence.MaxValue);
    Assert.Null(sequence.IsCyclic);
}

private const string SequenceSourceNonLiteralName = """
    public class AppDbContext : DbContext
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.HasSequence(SomeNameConstant);
        }
    }
    """;

[Fact]
public void ParseSequences_NonLiteralNameArgument_EmitsUnreadableDiagnostic()
{
    var result = new FluentConfigParser().ParseSequences(SequenceSourceNonLiteralName);

    Assert.Empty(result.Value);
    var diagnostic = Assert.Single(result.Diagnostics);
    Assert.Equal(DiagnosticCodes.UnreadableHasSequenceArgument, diagnostic.Code);
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter "FullyQualifiedName~ParseSequences"`
Expected: FAIL (compile error)

- [ ] **Step 3: Add the model**

Create `src/EfSchemaVisualizer.Core/Model/SequenceModel.cs`:

```csharp
namespace EfSchemaVisualizer.Core.Model;

public sealed record SequenceModel(
    string Name,
    string? Schema,
    string? ClrType,
    long? StartsAt,
    int? IncrementsBy,
    long? MinValue,
    long? MaxValue,
    bool? IsCyclic);
```

- [ ] **Step 4: Add the diagnostic code**

In `src/EfSchemaVisualizer.Core/Parsing/DiagnosticCodes.cs`, add:

```csharp
    public const string UnreadableHasSequenceArgument = nameof(UnreadableHasSequenceArgument);
```

- [ ] **Step 5: Add the config record**

Create `src/EfSchemaVisualizer.Core/Merging/SequenceConfig.cs`:

```csharp
namespace EfSchemaVisualizer.Core.Merging;

public sealed record SequenceConfig(
    string Name,
    string? Schema,
    string? ClrType,
    long? StartsAt,
    int? IncrementsBy,
    long? MinValue,
    long? MaxValue,
    bool? IsCyclic);
```

- [ ] **Step 6: Add the parser method**

In `src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs`, add `"HasSequence"` to `RecognizedModelLevelCallNames`, and add:

```csharp
public ParseResult<IReadOnlyList<SequenceConfig>> ParseSequences(string sourceCode)
{
    var tree = CSharpSyntaxTree.ParseText(sourceCode);
    var root = tree.GetCompilationUnitRoot();

    var results = new List<SequenceConfig>();
    var diagnostics = new List<Diagnostic>();

    foreach (var call in FluentSyntaxHelpers.FindModelLevelCalls(root))
    {
        if (call.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "HasSequence" } memberAccess)
        {
            continue;
        }

        var arguments = call.ArgumentList.Arguments;

        if (arguments.Count == 0
            || arguments[0].Expression is not LiteralExpressionSyntax nameLiteral
            || !nameLiteral.IsKind(SyntaxKind.StringLiteralExpression))
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticCodes.UnreadableHasSequenceArgument,
                "HasSequence name argument is not a string literal and could not be read.",
                EntityName: null,
                PropertyName: null,
                call.Span));
            continue;
        }

        var name = nameLiteral.Token.ValueText;

        string? schema = null;
        if (arguments.Count >= 2
            && arguments[1].Expression is LiteralExpressionSyntax schemaLiteral
            && schemaLiteral.IsKind(SyntaxKind.StringLiteralExpression))
        {
            schema = schemaLiteral.Token.ValueText;
        }

        var clrType = memberAccess.Name is GenericNameSyntax { TypeArgumentList.Arguments.Count: 1 } generic
            ? generic.TypeArgumentList.Arguments[0].ToString()
            : null;

        long? startsAt = null;
        int? incrementsBy = null;
        long? minValue = null;
        long? maxValue = null;
        bool? isCyclic = null;

        FluentSyntaxHelpers.WalkChainedTail(call, chained =>
        {
            if (chained.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: var methodName })
            {
                return;
            }

            var arg = chained.ArgumentList.Arguments.FirstOrDefault();

            switch (methodName)
            {
                case "StartsAt":
                    if (arg?.Expression is LiteralExpressionSyntax startsAtLiteral && long.TryParse(startsAtLiteral.Token.ValueText, out var startsAtValue))
                    {
                        startsAt = startsAtValue;
                    }
                    break;
                case "IncrementsBy":
                    if (arg?.Expression is LiteralExpressionSyntax incrementLiteral && int.TryParse(incrementLiteral.Token.ValueText, out var incrementValue))
                    {
                        incrementsBy = incrementValue;
                    }
                    break;
                case "HasMin":
                    if (arg?.Expression is LiteralExpressionSyntax minLiteral && long.TryParse(minLiteral.Token.ValueText, out var minValueParsed))
                    {
                        minValue = minValueParsed;
                    }
                    break;
                case "HasMax":
                    if (arg?.Expression is LiteralExpressionSyntax maxLiteral && long.TryParse(maxLiteral.Token.ValueText, out var maxValueParsed))
                    {
                        maxValue = maxValueParsed;
                    }
                    break;
                case "IsCyclic":
                    isCyclic = arg is null
                        || (arg.Expression is LiteralExpressionSyntax cyclicLiteral && cyclicLiteral.IsKind(SyntaxKind.TrueLiteralExpression));
                    break;
            }
        });

        results.Add(new SequenceConfig(name, schema, clrType, startsAt, incrementsBy, minValue, maxValue, isCyclic));
    }

    return new ParseResult<IReadOnlyList<SequenceConfig>>(results, diagnostics);
}
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter "FullyQualifiedName~ParseSequences"`
Expected: PASS

- [ ] **Step 8: Write the failing merger test**

Add to `tests/EfSchemaVisualizer.Core.Tests/Merging/ModelMergerTests.cs`:

```csharp
[Fact]
public void ApplySequences_MapsEachConfigToASequenceModel()
{
    var configs = new List<SequenceConfig>
    {
        new("OrderNumbers", "shared", "int", 1000, 5, 1, 1000000, true),
        new("Simple", null, null, null, null, null, null, null),
    };

    var result = ModelMerger.ApplySequences(configs);

    Assert.Equal(2, result.Count);
    var orderNumbers = result.Single(s => s.Name == "OrderNumbers");
    Assert.Equal("shared", orderNumbers.Schema);
    Assert.Equal("int", orderNumbers.ClrType);
    Assert.Equal(1000, orderNumbers.StartsAt);
    Assert.True(orderNumbers.IsCyclic);
}
```

- [ ] **Step 9: Run the merger test to verify it fails**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter "FullyQualifiedName~ApplySequences"`
Expected: FAIL (compile error)

- [ ] **Step 10: Add the merger method**

In `src/EfSchemaVisualizer.Core/Merging/ModelMerger.cs`, add (this one is model-level, not per-entity, so it doesn't take an `EntityModel`):

```csharp
public static IReadOnlyList<SequenceModel> ApplySequences(IReadOnlyList<SequenceConfig> configs)
{
    return configs
        .Select(c => new SequenceModel(c.Name, c.Schema, c.ClrType, c.StartsAt, c.IncrementsBy, c.MinValue, c.MaxValue, c.IsCyclic))
        .ToList();
}
```

- [ ] **Step 11: Run the merger test to verify it passes**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter "FullyQualifiedName~ApplySequences"`
Expected: PASS

- [ ] **Step 12: Wire into DiagramModelBuilder.Build**

In `src/EfSchemaVisualizer.Web/DiagramModelBuilder.cs`, change the `DiagramModelResult` record:

```csharp
public sealed record DiagramModelResult(
    IReadOnlyList<EntityModel> Entities,
    IReadOnlyList<RelationshipModel> Relationships,
    IReadOnlyList<Diagnostic> Diagnostics,
    IReadOnlyList<SequenceModel> Sequences);
```

Add the parse call alongside the others:

```csharp
var sequences = configParser.ParseSequences(configSource);
```

Add to diagnostics:

```csharp
diagnostics.AddRange(sequences.Diagnostics);
```

At the end of `Build`, change the final `return` statement:

```csharp
return new DiagramModelResult(entities, allRelationships, diagnostics, ModelMerger.ApplySequences(sequences.Value));
```

Every other call site constructing a `DiagramModelResult` (if any exist outside `DiagramModelBuilder.Build` — check with `grep -rn "new DiagramModelResult" src/`) must be updated to pass the new `Sequences` argument too; if none exist, no further change is needed.

- [ ] **Step 13: Run the full Core + Web suites**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests && dotnet test tests/EfSchemaVisualizer.Web.Tests`
Expected: PASS or clear compile errors at any other `DiagramModelResult` construction site — fix each by threading through `ModelMerger.ApplySequences(...)` (or an empty list where no config source is available) and re-run.

- [ ] **Step 14: Commit**

```bash
git add -A
git commit -m "Parse and model HasSequence, including StartsAt/IncrementsBy/HasMin/HasMax/IsCyclic"
```

---

## Task 8: UseSequence — parser + merger + wiring

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/Model/PropertyModel.cs`
- Modify: `src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs`
- Modify: `src/EfSchemaVisualizer.Core/Parsing/DiagnosticCodes.cs`
- Create: `src/EfSchemaVisualizer.Core/Merging/UseSequenceConfig.cs`
- Modify: `src/EfSchemaVisualizer.Core/Merging/ModelMerger.cs`
- Modify: `src/EfSchemaVisualizer.Web/DiagramModelBuilder.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Merging/ModelMergerTests.cs`

**Interfaces:**
- Produces: `PropertyModel.SequenceName` (`string?`), `PropertyModel.SequenceSchema` (`string?`); `UseSequenceConfig(string EntityName, string PropertyName, string SequenceName, string? Schema)`; `FluentConfigParser.ParseUseSequences(string sourceCode) : ParseResult<IReadOnlyList<UseSequenceConfig>>`; `ModelMerger.ApplyUseSequences(EntityModel entity, IReadOnlyList<UseSequenceConfig> configs) : EntityModel`; `DiagnosticCodes.UnreadableUseSequenceArgument`.

- [ ] **Step 1: Write the failing parser tests**

Add to `tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs`:

```csharp
private const string UseSequenceSource = """
    public class AppDbContext : DbContext
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Order>(entity =>
            {
                entity.Property(e => e.Number).UseSequence("OrderNumbers", "shared");
            });
        }
    }
    """;

[Fact]
public void ParseUseSequences_ReadsSequenceNameAndSchema()
{
    var result = new FluentConfigParser().ParseUseSequences(UseSequenceSource);

    Assert.Empty(result.Diagnostics);
    var config = Assert.Single(result.Value);
    Assert.Equal("Order", config.EntityName);
    Assert.Equal("Number", config.PropertyName);
    Assert.Equal("OrderNumbers", config.SequenceName);
    Assert.Equal("shared", config.Schema);
}

private const string UseSequenceSourceWithNonLiteralArg = """
    public class AppDbContext : DbContext
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Order>(entity =>
            {
                entity.Property(e => e.Number).UseSequence(SomeNameConstant);
            });
        }
    }
    """;

[Fact]
public void ParseUseSequences_NonLiteralArgument_EmitsUnreadableDiagnostic()
{
    var result = new FluentConfigParser().ParseUseSequences(UseSequenceSourceWithNonLiteralArg);

    Assert.Empty(result.Value);
    var diagnostic = Assert.Single(result.Diagnostics);
    Assert.Equal(DiagnosticCodes.UnreadableUseSequenceArgument, diagnostic.Code);
    Assert.Equal("Order", diagnostic.EntityName);
    Assert.Equal("Number", diagnostic.PropertyName);
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter "FullyQualifiedName~ParseUseSequences"`
Expected: FAIL (compile error)

- [ ] **Step 3: Add the model fields**

In `src/EfSchemaVisualizer.Core/Model/PropertyModel.cs`, add after `ComputedColumnSqlIsStored` (added in Task 1):

```csharp
    bool? ComputedColumnSqlIsStored = null,
    string? SequenceName = null,
    string? SequenceSchema = null);
```

- [ ] **Step 4: Add the diagnostic code**

In `src/EfSchemaVisualizer.Core/Parsing/DiagnosticCodes.cs`, add:

```csharp
    public const string UnreadableUseSequenceArgument = nameof(UnreadableUseSequenceArgument);
```

- [ ] **Step 5: Add the config record**

Create `src/EfSchemaVisualizer.Core/Merging/UseSequenceConfig.cs`:

```csharp
namespace EfSchemaVisualizer.Core.Merging;

public sealed record UseSequenceConfig(string EntityName, string PropertyName, string SequenceName, string? Schema);
```

- [ ] **Step 6: Add the parser method**

In `src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs`, add `"UseSequence"` to `RecognizedCallNames`, and add:

```csharp
public ParseResult<IReadOnlyList<UseSequenceConfig>> ParseUseSequences(string sourceCode)
{
    var tree = CSharpSyntaxTree.ParseText(sourceCode);
    var root = tree.GetCompilationUnitRoot();

    var results = new List<UseSequenceConfig>();
    var diagnostics = new List<Diagnostic>();

    foreach (var (entityName, scope) in FluentSyntaxHelpers.FindConfigurationScopes(root))
    {
        foreach (var call in FluentSyntaxHelpers.FindCallsNamed(scope, "UseSequence"))
        {
            var propertyName = FluentSyntaxHelpers.GetPropertyNameFor(call);

            if (propertyName is null)
            {
                diagnostics.Add(new Diagnostic(
                    DiagnosticCodes.UnresolvablePropertyName,
                    "Could not determine which property this UseSequence call configures.",
                    entityName,
                    PropertyName: null,
                    call.Span));
                continue;
            }

            var arguments = call.ArgumentList.Arguments;

            if (arguments.Count == 0
                || arguments[0].Expression is not LiteralExpressionSyntax sequenceNameLiteral
                || !sequenceNameLiteral.IsKind(SyntaxKind.StringLiteralExpression))
            {
                diagnostics.Add(new Diagnostic(
                    DiagnosticCodes.UnreadableUseSequenceArgument,
                    "UseSequence argument is not a string literal and could not be read.",
                    entityName,
                    propertyName,
                    call.Span));
                continue;
            }

            var sequenceName = sequenceNameLiteral.Token.ValueText;

            string? schema = null;
            if (arguments.Count >= 2
                && arguments[1].Expression is LiteralExpressionSyntax schemaLiteral
                && schemaLiteral.IsKind(SyntaxKind.StringLiteralExpression))
            {
                schema = schemaLiteral.Token.ValueText;
            }

            results.Add(new UseSequenceConfig(entityName, propertyName, sequenceName, schema));
        }
    }

    return new ParseResult<IReadOnlyList<UseSequenceConfig>>(results, diagnostics);
}
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter "FullyQualifiedName~ParseUseSequences"`
Expected: PASS

- [ ] **Step 8: Write the failing merger test**

Add to `tests/EfSchemaVisualizer.Core.Tests/Merging/ModelMergerTests.cs`:

```csharp
[Fact]
public void ApplyUseSequences_SetsSequenceNameAndSchemaOnMatchingProperty()
{
    var entity = new EntityModel("Order", new List<PropertyModel>
    {
        new("Number", "int", IsNullable: false, MaxLength: null),
        new("Total", "decimal", IsNullable: false, MaxLength: null),
    });

    var configs = new List<UseSequenceConfig>
    {
        new("Order", "Number", "OrderNumbers", "shared"),
    };

    var result = ModelMerger.ApplyUseSequences(entity, configs);

    var number = result.Properties.Single(p => p.Name == "Number");
    Assert.Equal("OrderNumbers", number.SequenceName);
    Assert.Equal("shared", number.SequenceSchema);

    var total = result.Properties.Single(p => p.Name == "Total");
    Assert.Null(total.SequenceName);
}
```

- [ ] **Step 9: Run the merger test to verify it fails**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter "FullyQualifiedName~ApplyUseSequences"`
Expected: FAIL (compile error)

- [ ] **Step 10: Add the merger method**

In `src/EfSchemaVisualizer.Core/Merging/ModelMerger.cs`, add:

```csharp
public static EntityModel ApplyUseSequences(EntityModel entity, IReadOnlyList<UseSequenceConfig> configs)
{
    var byProperty = IndexByProperty(entity.Name, configs, c => c.EntityName, c => c.PropertyName);

    var updatedProperties = entity.Properties
        .Select(property => byProperty.TryGetValue(property.Name, out var config)
            ? property with { SequenceName = config.SequenceName, SequenceSchema = config.Schema }
            : property)
        .ToList();

    return entity with { Properties = updatedProperties };
}
```

- [ ] **Step 11: Run the merger test to verify it passes**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter "FullyQualifiedName~ApplyUseSequences"`
Expected: PASS

- [ ] **Step 12: Wire into DiagramModelBuilder.Build**

In `src/EfSchemaVisualizer.Web/DiagramModelBuilder.cs`, add:

```csharp
var useSequences = configParser.ParseUseSequences(configSource);
```

```csharp
diagnostics.AddRange(useSequences.Diagnostics);
```

Add to the entity pipeline, right after the `ApplyCheckConstraints` line added in Task 4:

```csharp
            .Select(entity => ModelMerger.ApplyUseSequences(entity, useSequences.Value))
```

- [ ] **Step 13: Run the full Core + Web suites**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests && dotnet test tests/EfSchemaVisualizer.Web.Tests`
Expected: PASS, no regressions

- [ ] **Step 14: Commit**

```bash
git add -A
git commit -m "Parse and model UseSequence"
```

---

## Task 9: HasSequence — rewriter (model-level add/set/remove)

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs`

**Interfaces:**
- Produces: `OnModelCreatingRewriter.SetSequence(string sourceCode, string name, string? schema, string? clrType, long? startsAt, int? incrementsBy, long? minValue, long? maxValue, bool? isCyclic) : string`; `OnModelCreatingRewriter.RemoveSequence(string sourceCode, string name) : string`.
- Consumes: `ChainCall` (existing private helper), `FindModelLevelCallsRoot` — see Step 3 for how the model-level receiver name is found (there is no existing rewriter-side equivalent of `FluentSyntaxHelpers.FindModelBuilderReceiverNames`, so this task adds a small one scoped to the rewriter).

- [ ] **Step 1: Write the failing rewriter tests**

Add to `tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs`. First add this fixture (a config source with an existing `OnModelCreating` method and no sequences yet):

```csharp
private const string SourceWithNoSequences = """
    public class AppDbContext : DbContext
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Order>(entity =>
            {
                entity.HasKey(e => e.Id);
            });
        }
    }
    """;
```

Then add the tests:

```csharp
[Fact]
public void SetSequence_NoExisting_InsertsFullChain()
{
    var result = new OnModelCreatingRewriter()
        .SetSequence(SourceWithNoSequences, name: "OrderNumbers", schema: "shared", clrType: "int",
            startsAt: 1000, incrementsBy: 5, minValue: 1, maxValue: 1000000, isCyclic: true);

    Assert.Contains("modelBuilder.HasSequence<int>(\"OrderNumbers\", \"shared\")", result);
    Assert.Contains(".StartsAt(1000)", result);
    Assert.Contains(".IncrementsBy(5)", result);
    Assert.Contains(".HasMin(1)", result);
    Assert.Contains(".HasMax(1000000)", result);
    Assert.Contains(".IsCyclic()", result);
}

[Fact]
public void SetSequence_OmitsAbsentChainedOptions()
{
    var result = new OnModelCreatingRewriter()
        .SetSequence(SourceWithNoSequences, name: "Simple", schema: null, clrType: null,
            startsAt: null, incrementsBy: null, minValue: null, maxValue: null, isCyclic: null);

    Assert.Contains("modelBuilder.HasSequence(\"Simple\")", result);
    Assert.DoesNotContain("StartsAt", result);
    Assert.DoesNotContain("IsCyclic", result);
}

[Fact]
public void SetSequence_ExistingSequence_ReplacesWholeChain()
{
    var source = new OnModelCreatingRewriter()
        .SetSequence(SourceWithNoSequences, name: "OrderNumbers", schema: "shared", clrType: "int",
            startsAt: 1000, incrementsBy: null, minValue: null, maxValue: null, isCyclic: null);

    var result = new OnModelCreatingRewriter()
        .SetSequence(source, name: "OrderNumbers", schema: "shared", clrType: "int",
            startsAt: 2000, incrementsBy: null, minValue: null, maxValue: null, isCyclic: null);

    Assert.Contains(".StartsAt(2000)", result);
    Assert.DoesNotContain("StartsAt(1000)", result);
}

[Fact]
public void RemoveSequence_ExistingSequence_RemovesStatement()
{
    var source = new OnModelCreatingRewriter()
        .SetSequence(SourceWithNoSequences, name: "OrderNumbers", schema: "shared", clrType: "int",
            startsAt: 1000, incrementsBy: null, minValue: null, maxValue: null, isCyclic: null);

    var result = new OnModelCreatingRewriter()
        .RemoveSequence(source, name: "OrderNumbers");

    Assert.DoesNotContain("HasSequence", result);
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter "FullyQualifiedName~SetSequence|FullyQualifiedName~RemoveSequence"`
Expected: FAIL (compile error)

- [ ] **Step 3: Add the rewriter methods**

In `src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs`, add:

```csharp
public string SetSequence(
    string sourceCode, string name, string? schema, string? clrType,
    long? startsAt, int? incrementsBy, long? minValue, long? maxValue, bool? isCyclic)
{
    var withoutExisting = RemoveSequence(sourceCode, name);

    var tree = CSharpSyntaxTree.ParseText(withoutExisting);
    var root = tree.GetCompilationUnitRoot();

    var method = FindOnModelCreatingMethod(root);
    var methodBody = method.Body
        ?? throw new InvalidOperationException("OnModelCreating has no method body.");

    var modelBuilderParamName = method.ParameterList.Parameters.Single().Identifier.Text;

    var statement = SyntaxFactory.ExpressionStatement(
        BuildSequenceExpression(modelBuilderParamName, name, schema, clrType, startsAt, incrementsBy, minValue, maxValue, isCyclic));

    var newMethodBody = methodBody.AddStatements(statement);
    var newRoot = root.ReplaceNode(methodBody, newMethodBody);
    return newRoot.NormalizeWhitespace().ToFullString();
}

public string RemoveSequence(string sourceCode, string name)
{
    var tree = CSharpSyntaxTree.ParseText(sourceCode);
    var root = tree.GetCompilationUnitRoot();

    var existingCall = FluentSyntaxHelpers.FindModelLevelCalls(root)
        .FirstOrDefault(call => call.Expression is MemberAccessExpressionSyntax { Name.Identifier.Text: "HasSequence" }
            && IsSequenceNamed(call, name));

    if (existingCall is null)
    {
        return sourceCode;
    }

    var outermostChainedCall = FindOutermostChainedCall(existingCall);
    var statement = outermostChainedCall.Ancestors().OfType<ExpressionStatementSyntax>().First();
    var newRoot = root.RemoveNode(statement, SyntaxRemoveOptions.KeepNoTrivia)!;
    return newRoot.NormalizeWhitespace().ToFullString();
}

private static bool IsSequenceNamed(InvocationExpressionSyntax call, string name)
{
    var nameArg = call.ArgumentList.Arguments.FirstOrDefault();
    return nameArg?.Expression is LiteralExpressionSyntax literal
        && literal.IsKind(SyntaxKind.StringLiteralExpression)
        && literal.Token.ValueText == name;
}

private static ExpressionSyntax BuildSequenceExpression(
    string modelBuilderParamName, string name, string? schema, string? clrType,
    long? startsAt, int? incrementsBy, long? minValue, long? maxValue, bool? isCyclic)
{
    var arguments = new List<ArgumentSyntax>
    {
        SyntaxFactory.Argument(SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(name))),
    };

    if (schema is not null)
    {
        arguments.Add(SyntaxFactory.Argument(SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(schema))));
    }

    SimpleNameSyntax methodName = clrType is not null
        ? SyntaxFactory.GenericName(SyntaxFactory.Identifier("HasSequence"))
            .WithTypeArgumentList(SyntaxFactory.TypeArgumentList(SyntaxFactory.SingletonSeparatedList<TypeSyntax>(SyntaxFactory.ParseTypeName(clrType))))
        : SyntaxFactory.IdentifierName("HasSequence");

    ExpressionSyntax expression = SyntaxFactory.InvocationExpression(
        SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, SyntaxFactory.IdentifierName(modelBuilderParamName), methodName),
        SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(arguments)));

    if (startsAt is not null)
    {
        expression = ChainCall(expression, "StartsAt", SyntaxFactory.Argument(
            SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(startsAt.Value))));
    }

    if (incrementsBy is not null)
    {
        expression = ChainCall(expression, "IncrementsBy", SyntaxFactory.Argument(
            SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(incrementsBy.Value))));
    }

    if (minValue is not null)
    {
        expression = ChainCall(expression, "HasMin", SyntaxFactory.Argument(
            SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(minValue.Value))));
    }

    if (maxValue is not null)
    {
        expression = ChainCall(expression, "HasMax", SyntaxFactory.Argument(
            SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(maxValue.Value))));
    }

    if (isCyclic == true)
    {
        expression = ChainBareCall(expression, "IsCyclic");
    }

    return expression;
}
```

`FindOutermostChainedCall` already exists in this file (added for `SetKey`) and is reused as-is.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter "FullyQualifiedName~SetSequence|FullyQualifiedName~RemoveSequence"`
Expected: PASS

- [ ] **Step 5: Run the full Core test suite**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests`
Expected: PASS, no regressions

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "Add HasSequence rewriter support (set/remove with full chained-option rebuild)"
```

---

## Task 10: UseSequence — rewriter (extend string-arg helpers to two string arguments)

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs`

**Interfaces:**
- Produces: `OnModelCreatingRewriter.SetUseSequence(string sourceCode, string entityName, string propertyName, string sequenceName, string? schema) : string`; `OnModelCreatingRewriter.RemoveUseSequence(string sourceCode, string entityName, string propertyName) : string`.

`UseSequence` needs a two-string-argument variant of the same property-call machinery Task 2 already extended with a trailing bool. Rather than overload the existing bool-taking helpers further, this task adds a second, parallel small set of helpers scoped to `UseSequence` specifically, since `HasComputedColumnSql`'s "string + bool" shape and `UseSequence`'s "string + optional string" shape are different enough that forcing one generic signature would need an `object? secondArg` escape hatch — worse than two clear helpers.

- [ ] **Step 1: Write the failing rewriter tests**

Add to `tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs` (reusing `SourceWithPropertyButNoDefaultValue`, which has an `Order` entity with a bare `Quantity` property call — substitute a property named `Number` if that fixture doesn't have one; otherwise add a small dedicated fixture):

```csharp
private const string SourceWithNumberPropertyNoSequence = """
    public class AppDbContext : DbContext
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Order>(entity =>
            {
                entity.Property(e => e.Number);
            });
        }
    }
    """;

[Fact]
public void SetUseSequence_BarePropertyCall_AppendsUseSequenceWithSchema()
{
    var result = new OnModelCreatingRewriter()
        .SetUseSequence(SourceWithNumberPropertyNoSequence, entityName: "Order", propertyName: "Number", sequenceName: "OrderNumbers", schema: "shared");

    Assert.Contains("entity.Property(e => e.Number).UseSequence(\"OrderNumbers\", \"shared\")", result);
}

[Fact]
public void SetUseSequence_NoSchema_AppendsUseSequenceWithOneArgument()
{
    var result = new OnModelCreatingRewriter()
        .SetUseSequence(SourceWithNumberPropertyNoSequence, entityName: "Order", propertyName: "Number", sequenceName: "OrderNumbers", schema: null);

    Assert.Contains("entity.Property(e => e.Number).UseSequence(\"OrderNumbers\")", result);
}

[Fact]
public void RemoveUseSequence_ExistingCall_RemovesCall()
{
    var source = new OnModelCreatingRewriter()
        .SetUseSequence(SourceWithNumberPropertyNoSequence, entityName: "Order", propertyName: "Number", sequenceName: "OrderNumbers", schema: "shared");

    var result = new OnModelCreatingRewriter()
        .RemoveUseSequence(source, entityName: "Order", propertyName: "Number");

    Assert.DoesNotContain("UseSequence", result);
    Assert.Contains("entity.Property(e => e.Number)", result);
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter "FullyQualifiedName~UseSequence"`
Expected: FAIL (compile error)

- [ ] **Step 3: Add the rewriter methods**

In `src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs`, add:

```csharp
public string SetUseSequence(string sourceCode, string entityName, string propertyName, string sequenceName, string? schema)
{
    var tree = CSharpSyntaxTree.ParseText(sourceCode);
    var root = tree.GetCompilationUnitRoot();

    var scopes = FindConfigScopes(root, entityName);

    var existingCall = scopes
        .SelectMany(scope => FluentSyntaxHelpers.FindCallsNamed(scope, "UseSequence"))
        .FirstOrDefault(call => FluentSyntaxHelpers.GetPropertyNameFor(call) == propertyName);

    if (existingCall is not null)
    {
        var newRoot = root.ReplaceNode(existingCall, BuildUseSequenceCall(((MemberAccessExpressionSyntax)existingCall.Expression).Expression, sequenceName, schema));
        return newRoot.ToFullString();
    }

    var existingPropertyCall = scopes
        .SelectMany(scope => FluentSyntaxHelpers.FindCallsNamed(scope, "Property"))
        .FirstOrDefault(call => FluentSyntaxHelpers.GetPropertyNameForPropertyCall(call) == propertyName);

    if (existingPropertyCall is not null)
    {
        var newRoot = root.ReplaceNode(existingPropertyCall, BuildUseSequenceCall(existingPropertyCall, sequenceName, schema));
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    var existingScope = scopes.FirstOrDefault();
    var propertyCall = BuildPropertyCallExpression(existingScope, propertyName, out var block, out var blockReceiverName);

    var propertyStatement = SyntaxFactory.ExpressionStatement(BuildUseSequenceCall(propertyCall, sequenceName, schema));

    if (existingScope is not null)
    {
        var newBlock = block!.AddStatements(propertyStatement);
        var newRoot = root.ReplaceNode(block!, newBlock);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    var method = FindOnModelCreatingMethod(root);
    var methodBody = method.Body
        ?? throw new InvalidOperationException("OnModelCreating has no method body.");

    var modelBuilderParamName = method.ParameterList.Parameters.Single().Identifier.Text;
    var entityBlockStatement = BuildEntityInvocationStatement(modelBuilderParamName, entityName, SyntaxFactory.Block(propertyStatement));

    var newMethodBody = methodBody.AddStatements(entityBlockStatement);
    var newRoot2 = root.ReplaceNode(methodBody, newMethodBody);
    return newRoot2.NormalizeWhitespace().ToFullString();
}

public string RemoveUseSequence(string sourceCode, string entityName, string propertyName)
{
    return RemoveStringArgCall(sourceCode, entityName, propertyName, "UseSequence");
}

private static InvocationExpressionSyntax BuildUseSequenceCall(ExpressionSyntax propertyCallExpression, string sequenceName, string? schema)
{
    var arguments = new List<ArgumentSyntax>
    {
        SyntaxFactory.Argument(SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(sequenceName))),
    };

    if (schema is not null)
    {
        arguments.Add(SyntaxFactory.Argument(SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(schema))));
    }

    return SyntaxFactory.InvocationExpression(
        SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, propertyCallExpression, SyntaxFactory.IdentifierName("UseSequence")),
        SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(arguments)));
}

private static InvocationExpressionSyntax BuildPropertyCallExpression(SyntaxNode? scope, string propertyName, out BlockSyntax? block, out string blockReceiverName)
{
    if (scope is null)
    {
        block = null;
        blockReceiverName = "entity";
        return BuildBarePropertyCallExpression("entity", "e", propertyName);
    }

    var (scopeBlock, receiverName) = GetScopeBlockAndReceiver(scope);
    var lambdaParam = FluentSyntaxHelpers.GetPropertyLambdaParameterName(scope);
    block = scopeBlock;
    blockReceiverName = receiverName;
    return BuildBarePropertyCallExpression(receiverName, lambdaParam, propertyName);
}

private static InvocationExpressionSyntax BuildBarePropertyCallExpression(string blockReceiverName, string propertyLambdaParam, string propertyName)
{
    return SyntaxFactory.InvocationExpression(
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
}
```

`RemoveStringArgCall` (used by `RemoveUseSequence`) already exists and works generically by method name + `GetPropertyNameFor` match — no change needed there since it only removes a call and unwraps to the receiver, regardless of argument count.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter "FullyQualifiedName~UseSequence"`
Expected: PASS

- [ ] **Step 5: Run the full Core test suite**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests`
Expected: PASS, no regressions

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "Add UseSequence rewriter support"
```

---

## Task 11: HasSequence / UseSequence — editor

**Files:**
- Modify: `src/EfSchemaVisualizer.Web/Diagram/DiagramEditor.cs`
- Test: `tests/EfSchemaVisualizer.Web.Tests/Diagram/DiagramEditorPropertyPanelTests.cs`

**Interfaces:**
- Consumes: `OnModelCreatingRewriter.SetSequence`/`RemoveSequence`/`SetUseSequence`/`RemoveUseSequence` (Tasks 9-10); `DiagramEditor.Current` (existing, must expose `Sequences` — see Step 3).
- Produces: `DiagramEditor.AddSequence(string name, string? schema, string? clrType, long? startsAt, int? incrementsBy, long? minValue, long? maxValue, bool? isCyclic) : DiagramEditResult`; `DiagramEditor.SetSequence(...)` (same signature); `DiagramEditor.RemoveSequence(string name) : DiagramEditResult`; `DiagramEditor.SetUseSequence(string entityName, string propertyName, string? sequenceName, string? schema) : DiagramEditResult`.

- [ ] **Step 1: Confirm DiagramEditor.Current exposes Sequences**

Read `src/EfSchemaVisualizer.Web/Diagram/DiagramEditor.cs`'s `Current` property and `Apply` method. `Current` is a `DiagramModelResult` built via `DiagramModelBuilder.Build(classSource, configSource)` — since Task 7 already added `Sequences` to that record, `Current.Sequences` is already available with no further change needed here.

- [ ] **Step 2: Write the failing editor tests**

Add to `tests/EfSchemaVisualizer.Web.Tests/Diagram/DiagramEditorPropertyPanelTests.cs`:

```csharp
[Fact]
public void AddSequence_NewName_AddsToModel()
{
    var editor = new DiagramEditor(ClassSource, ConfigSource);

    var result = editor.AddSequence("PersonIds", schema: null, clrType: "int", startsAt: 1, incrementsBy: null, minValue: null, maxValue: null, isCyclic: null);

    Assert.True(result.Success);
    var sequence = editor.Current.Sequences.Single();
    Assert.Equal("PersonIds", sequence.Name);
    Assert.Equal(1, sequence.StartsAt);
}

[Fact]
public void AddSequence_DuplicateName_Fails()
{
    var editor = new DiagramEditor(ClassSource, ConfigSource);
    editor.AddSequence("PersonIds", null, "int", 1, null, null, null, null);

    var result = editor.AddSequence("PersonIds", null, "int", 2, null, null, null, null);

    Assert.False(result.Success);
    Assert.Single(editor.Current.Sequences);
}

[Fact]
public void RemoveSequence_ExistingName_RemovesIt()
{
    var editor = new DiagramEditor(ClassSource, ConfigSource);
    editor.AddSequence("PersonIds", null, "int", 1, null, null, null, null);

    var result = editor.RemoveSequence("PersonIds");

    Assert.True(result.Success);
    Assert.Empty(editor.Current.Sequences);
}

[Fact]
public void SetUseSequence_NoExistingConfig_LinksPropertyToSequence()
{
    var editor = new DiagramEditor(ClassSource, ConfigSource);
    editor.AddSequence("PersonIds", "shared", "int", null, null, null, null, null);

    var result = editor.SetUseSequence("Person", "Id", "PersonIds", "shared");

    Assert.True(result.Success);
    var property = editor.Current.Entities.Single().Properties.Single(p => p.Name == "Id");
    Assert.Equal("PersonIds", property.SequenceName);
    Assert.Equal("shared", property.SequenceSchema);
}

[Fact]
public void SetUseSequence_ClearingExistingConfig_RemovesUseSequence()
{
    var editor = new DiagramEditor(ClassSource, ConfigSource);
    editor.AddSequence("PersonIds", "shared", "int", null, null, null, null, null);
    editor.SetUseSequence("Person", "Id", "PersonIds", "shared");

    var result = editor.SetUseSequence("Person", "Id", null, null);

    Assert.True(result.Success);
    Assert.Null(editor.Current.Entities.Single().Properties.Single(p => p.Name == "Id").SequenceName);
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test tests/EfSchemaVisualizer.Web.Tests --filter "FullyQualifiedName~Sequence"`
Expected: FAIL (compile error)

- [ ] **Step 4: Add the editor methods**

In `src/EfSchemaVisualizer.Web/Diagram/DiagramEditor.cs`, add:

```csharp
public DiagramEditResult AddSequence(
    string name, string? schema, string? clrType,
    long? startsAt, int? incrementsBy, long? minValue, long? maxValue, bool? isCyclic)
{
    if (string.IsNullOrWhiteSpace(name))
    {
        return DiagramEditResult.Fail("Sequence name cannot be empty.");
    }

    if (Current.Sequences.Any(s => s.Name == name))
    {
        return DiagramEditResult.Fail($"A sequence named '{name}' already exists.");
    }

    var newConfigSource = _configRewriter.SetSequence(ConfigSource, name, schema, clrType, startsAt, incrementsBy, minValue, maxValue, isCyclic);
    Apply(ClassSource, newConfigSource);
    return DiagramEditResult.Ok();
}

public DiagramEditResult SetSequence(
    string name, string? schema, string? clrType,
    long? startsAt, int? incrementsBy, long? minValue, long? maxValue, bool? isCyclic)
{
    if (!Current.Sequences.Any(s => s.Name == name))
    {
        return DiagramEditResult.Fail($"No sequence named '{name}' exists.");
    }

    var newConfigSource = _configRewriter.SetSequence(ConfigSource, name, schema, clrType, startsAt, incrementsBy, minValue, maxValue, isCyclic);
    Apply(ClassSource, newConfigSource);
    return DiagramEditResult.Ok();
}

public DiagramEditResult RemoveSequence(string name)
{
    if (!Current.Sequences.Any(s => s.Name == name))
    {
        return DiagramEditResult.Fail($"No sequence named '{name}' exists.");
    }

    var newConfigSource = _configRewriter.RemoveSequence(ConfigSource, name);
    Apply(ClassSource, newConfigSource);
    return DiagramEditResult.Ok();
}

public DiagramEditResult SetUseSequence(string entityName, string propertyName, string? sequenceName, string? schema)
{
    var entity = Current.Entities.FirstOrDefault(e => e.Name == entityName);
    if (entity is null)
    {
        return DiagramEditResult.Fail($"Entity '{entityName}' not found.");
    }

    var property = entity.Properties.FirstOrDefault(p => p.Name == propertyName);
    if (property is null)
    {
        return DiagramEditResult.Fail($"Property '{propertyName}' not found on '{entityName}'.");
    }

    var normalizedSequenceName = string.IsNullOrWhiteSpace(sequenceName) ? null : sequenceName.Trim();
    if (normalizedSequenceName == property.SequenceName && schema == property.SequenceSchema)
    {
        return DiagramEditResult.Ok();
    }

    var owningEntityName = ResolveDeclaringEntity(entityName, propertyName);
    var newConfigSource = normalizedSequenceName is null
        ? _configRewriter.RemoveUseSequence(ConfigSource, owningEntityName, propertyName)
        : _configRewriter.SetUseSequence(ConfigSource, owningEntityName, propertyName, normalizedSequenceName, schema);
    Apply(ClassSource, newConfigSource);
    return DiagramEditResult.Ok();
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/EfSchemaVisualizer.Web.Tests --filter "FullyQualifiedName~Sequence"`
Expected: PASS

- [ ] **Step 6: Run the full Web test suite**

Run: `dotnet test tests/EfSchemaVisualizer.Web.Tests`
Expected: PASS, no regressions

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "Add HasSequence/UseSequence editing to DiagramEditor"
```

---

## Task 12: HasSequence / UseSequence — UI

**Files:**
- Modify: `src/EfSchemaVisualizer.Web/Diagram/EntityNode.razor` ("Uses sequence" property input)
- Modify: `src/EfSchemaVisualizer.Web/Pages/Home.razor` (new "Sequences" panel, next to the existing diagnostics panels)

**Interfaces:**
- Consumes: `DiagramEditor.AddSequence`/`SetSequence`/`RemoveSequence`/`SetUseSequence` (Task 11); `DiagramModelResult.Sequences` (Task 7); `Home.razor`'s existing `_editor` (`DiagramEditor?`), `_error` (`string?`), and `OnDiagramEditedAsync()` fields/method.

- [ ] **Step 1: Add the "Uses sequence" property input in EntityNode.razor**

In `src/EfSchemaVisualizer.Web/Diagram/EntityNode.razor`, add right after the "Computed column SQL"/"Stored" fields added in Task 3:

```razor
                                <label style="display: block;">
                                    Uses sequence:
                                    <input list="sequence-names" style="width: 100px;" value="@property.SequenceName" placeholder="(none)"
                                           @onchange="e => CommitUseSequence(property, e.Value?.ToString())"
                                           @onpointerdown:stopPropagation="true"
                                           @onmousedown:stopPropagation="true" />
                                </label>
```

Add a `<datalist>` once near the top of the component's markup (outside the per-property loop) so the browser offers autocomplete from the declared sequence names, falling back to free text if none match:

```razor
<datalist id="sequence-names">
    @foreach (var sequence in EditContext.Editor.Current.Sequences)
    {
        <option value="@sequence.Name" />
    }
</datalist>
```

In the `@code` block, add:

```csharp
    private async Task CommitUseSequence(PropertyModel property, string? sequenceName)
    {
        var schema = EditContext.Editor.Current.Sequences.FirstOrDefault(s => s.Name == sequenceName)?.Schema;
        var result = SafeEdit(() => EditContext.Editor.SetUseSequence(Node.Entity.Name, property.Name, sequenceName, schema));
        if (result.Success)
        {
            _propertyErrors.Remove(property.Name);
            await EditContext.NotifyChangedAsync();
        }
        else
        {
            _propertyErrors[property.Name] = result.Error!;
        }
    }
```

- [ ] **Step 2: Add the Sequences panel to Home.razor**

`Home.razor` doesn't use the `SafeEdit`-wrapper pattern `EntityNode.razor` uses (edits there call `_editor.SomeMethod(...)` directly and check `result.Success`, following the existing `AddRelationship` handler at line 288-297: on failure set `_error = result.Error;` and refresh the view, on success call `OnDiagramEditedAsync()`). Follow that same existing pattern here, not `SafeEdit`.

Add this panel markup right after the existing "Diagnostics:" panel block (the one ending around line 79, `@if (_diagnostics is { Count: > 0 } && ... DiagnosticCategory.Parse) ...`):

```razor
    @if (_editor is not null)
    {
        <div>
            <div>Sequences:</div>
            @foreach (var sequence in _editor.Current.Sequences)
            {
                <div style="display: flex; gap: 4px; align-items: center;">
                    <input style="width: 100px;" value="@sequence.Name" disabled />
                    <input style="width: 80px;" value="@sequence.Schema" placeholder="(schema)"
                           @onchange="e => CommitSequenceSchema(sequence, e.Value?.ToString())" />
                    <input style="width: 60px;" value="@sequence.ClrType" placeholder="(type)"
                           @onchange="e => CommitSequenceClrType(sequence, e.Value?.ToString())" />
                    <input style="width: 60px;" type="number" value="@sequence.StartsAt" title="Starts at"
                           @onchange="e => CommitSequenceStartsAt(sequence, e.Value?.ToString())" />
                    <input style="width: 60px;" type="number" value="@sequence.IncrementsBy" title="Increments by"
                           @onchange="e => CommitSequenceIncrementsBy(sequence, e.Value?.ToString())" />
                    <input style="width: 60px;" type="number" value="@sequence.MinValue" title="Min value"
                           @onchange="e => CommitSequenceMinValue(sequence, e.Value?.ToString())" />
                    <input style="width: 60px;" type="number" value="@sequence.MaxValue" title="Max value"
                           @onchange="e => CommitSequenceMaxValue(sequence, e.Value?.ToString())" />
                    <label title="Cyclic">
                        <input type="checkbox" checked="@(sequence.IsCyclic ?? false)"
                               @onchange="e => CommitSequenceIsCyclic(sequence, (bool)(e.Value ?? false))" />
                        Cyclic
                    </label>
                    <button @onclick="() => RemoveSequenceAsync(sequence)">x</button>
                </div>
            }
            <button @onclick="AddSequenceAsync">Add sequence</button>
        </div>
    }
```

In the `@code` block, add near `AddEntity`/`UndoAsync`:

```csharp
    private async Task AddSequenceAsync()
    {
        if (_editor is null)
        {
            return;
        }

        var name = $"Sequence{_editor.Current.Sequences.Count + 1}";
        var result = _editor.AddSequence(name, schema: null, clrType: "int",
            startsAt: null, incrementsBy: null, minValue: null, maxValue: null, isCyclic: null);
        if (!result.Success)
        {
            _error = result.Error;
            await InvokeAsync(StateHasChanged);
            return;
        }

        await OnDiagramEditedAsync();
    }

    private async Task RemoveSequenceAsync(SequenceModel sequence)
    {
        if (_editor is null)
        {
            return;
        }

        var result = _editor.RemoveSequence(sequence.Name);
        if (!result.Success)
        {
            _error = result.Error;
            await InvokeAsync(StateHasChanged);
            return;
        }

        await OnDiagramEditedAsync();
    }

    private async Task CommitSequenceSchema(SequenceModel sequence, string? newSchema)
    {
        if (_editor is null)
        {
            return;
        }

        var result = _editor.SetSequence(sequence.Name, newSchema, sequence.ClrType,
            sequence.StartsAt, sequence.IncrementsBy, sequence.MinValue, sequence.MaxValue, sequence.IsCyclic);
        if (!result.Success)
        {
            _error = result.Error;
            await InvokeAsync(StateHasChanged);
            return;
        }

        await OnDiagramEditedAsync();
    }

    private async Task CommitSequenceClrType(SequenceModel sequence, string? newClrType)
    {
        if (_editor is null)
        {
            return;
        }

        var result = _editor.SetSequence(sequence.Name, sequence.Schema, newClrType,
            sequence.StartsAt, sequence.IncrementsBy, sequence.MinValue, sequence.MaxValue, sequence.IsCyclic);
        if (!result.Success)
        {
            _error = result.Error;
            await InvokeAsync(StateHasChanged);
            return;
        }

        await OnDiagramEditedAsync();
    }

    private async Task CommitSequenceStartsAt(SequenceModel sequence, string? newStartsAt)
    {
        if (_editor is null)
        {
            return;
        }

        long? startsAt = long.TryParse(newStartsAt, out var parsed) ? parsed : null;
        var result = _editor.SetSequence(sequence.Name, sequence.Schema, sequence.ClrType,
            startsAt, sequence.IncrementsBy, sequence.MinValue, sequence.MaxValue, sequence.IsCyclic);
        if (!result.Success)
        {
            _error = result.Error;
            await InvokeAsync(StateHasChanged);
            return;
        }

        await OnDiagramEditedAsync();
    }

    private async Task CommitSequenceIncrementsBy(SequenceModel sequence, string? newIncrementsBy)
    {
        if (_editor is null)
        {
            return;
        }

        int? incrementsBy = int.TryParse(newIncrementsBy, out var parsed) ? parsed : null;
        var result = _editor.SetSequence(sequence.Name, sequence.Schema, sequence.ClrType,
            sequence.StartsAt, incrementsBy, sequence.MinValue, sequence.MaxValue, sequence.IsCyclic);
        if (!result.Success)
        {
            _error = result.Error;
            await InvokeAsync(StateHasChanged);
            return;
        }

        await OnDiagramEditedAsync();
    }

    private async Task CommitSequenceMinValue(SequenceModel sequence, string? newMinValue)
    {
        if (_editor is null)
        {
            return;
        }

        long? minValue = long.TryParse(newMinValue, out var parsed) ? parsed : null;
        var result = _editor.SetSequence(sequence.Name, sequence.Schema, sequence.ClrType,
            sequence.StartsAt, sequence.IncrementsBy, minValue, sequence.MaxValue, sequence.IsCyclic);
        if (!result.Success)
        {
            _error = result.Error;
            await InvokeAsync(StateHasChanged);
            return;
        }

        await OnDiagramEditedAsync();
    }

    private async Task CommitSequenceMaxValue(SequenceModel sequence, string? newMaxValue)
    {
        if (_editor is null)
        {
            return;
        }

        long? maxValue = long.TryParse(newMaxValue, out var parsed) ? parsed : null;
        var result = _editor.SetSequence(sequence.Name, sequence.Schema, sequence.ClrType,
            sequence.StartsAt, sequence.IncrementsBy, sequence.MinValue, maxValue, sequence.IsCyclic);
        if (!result.Success)
        {
            _error = result.Error;
            await InvokeAsync(StateHasChanged);
            return;
        }

        await OnDiagramEditedAsync();
    }

    private async Task CommitSequenceIsCyclic(SequenceModel sequence, bool isCyclic)
    {
        if (_editor is null)
        {
            return;
        }

        var result = _editor.SetSequence(sequence.Name, sequence.Schema, sequence.ClrType,
            sequence.StartsAt, sequence.IncrementsBy, sequence.MinValue, sequence.MaxValue, isCyclic);
        if (!result.Success)
        {
            _error = result.Error;
            await InvokeAsync(StateHasChanged);
            return;
        }

        await OnDiagramEditedAsync();
    }
```

`SequenceModel` is in `EfSchemaVisualizer.Core.Model`, already covered by this file's existing `@using EfSchemaVisualizer.Core.Model` (line 4) — no new `@using` needed.

- [ ] **Step 3: Manually verify in the running app**

Run: `dotnet run --project src/EfSchemaVisualizer.Web` and open the app in a browser. Paste a sample DbContext, add a sequence via the new panel, link a property to it via the new "Uses sequence" field, and confirm the downloaded/regenerated source contains the expected `HasSequence(...)`/`UseSequence(...)` calls.

- [ ] **Step 4: Run the full Web test suite**

Run: `dotnet test tests/EfSchemaVisualizer.Web.Tests`
Expected: PASS. `GestureHandlerSafeEditTests` covers the new `CommitUseSequence` handler in `EntityNode.razor` (wrapped in `SafeEdit`) automatically; the new `Home.razor` handlers aren't covered by that test since `Home.razor` doesn't use the `SafeEdit` pattern (see Step 2) — they're covered by the manual verification in Step 3 instead.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "Add Sequences panel and 'Uses sequence' property field to the UI"
```

---

## Task 13: RecognizedCallNames scoping fix

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/Parsing/FluentSyntaxHelpers.cs` (add `GetOwnerCallName`)
- Modify: `src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs` (add `ContextSensitiveCallNames`, fix `ParseUnrecognizedCalls`)
- Test: `tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs`

**Interfaces:**
- Produces: `FluentSyntaxHelpers.GetOwnerCallName(InvocationExpressionSyntax call) : string?`.
- Consumes: nothing new — operates purely on syntax already visited by `ParseUnrecognizedCalls`/`FindConfigChainCalls`.

- [ ] **Step 1: Write the failing regression tests**

Add to `tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs` (near the existing `ParseUnrecognizedCalls` / `HasName` tests — search the file for `HasName` first to place these alongside the existing `HasKey().HasName()`/`HasIndex().HasName()` recognition tests, so a future reader sees positive and negative cases together):

```csharp
private const string AlternateKeyWithHasNameSource = """
    public class AppDbContext : DbContext
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Order>(entity =>
            {
                entity.HasAlternateKey(e => e.Code).HasName("AK_Order_Code");
            });
        }
    }
    """;

[Fact]
public void ParseUnrecognizedCalls_HasNameChainedOntoHasAlternateKey_IsStillFlagged()
{
    var diagnostics = new FluentConfigParser().ParseUnrecognizedCalls(AlternateKeyWithHasNameSource);

    Assert.Contains(diagnostics, d => d.Code == DiagnosticCodes.UnrecognizedConfigCall);
}

private const string HasKeyWithHasNameSource = """
    public class AppDbContext : DbContext
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Order>(entity =>
            {
                entity.HasKey(e => e.Id).HasName("PK_Order");
            });
        }
    }
    """;

[Fact]
public void ParseUnrecognizedCalls_HasNameChainedOntoHasKey_IsNotFlagged()
{
    var diagnostics = new FluentConfigParser().ParseUnrecognizedCalls(HasKeyWithHasNameSource);

    Assert.DoesNotContain(diagnostics, d => d.Code == DiagnosticCodes.UnrecognizedConfigCall && d.Message.Contains("HasName"));
}
```

Also add the model-level sibling case, since `HasSequence(...).HasName(...)` is a *model*-level chain, not entity-scoped — it's covered by `ParseUnrecognizedModelLevelCalls`, not `ParseUnrecognizedCalls`, and that method has no chain-walking at all today (it only inspects calls made directly on `modelBuilder`, per `FindModelLevelCalls`), so `HasSequence(...).HasName(...)`'s trailing `.HasName(...)` is *never visited* by either unrecognized-call checker — it silently passes through untouched either way. Confirm this with a documentation-only test recording current (acceptable) behavior:

```csharp
private const string SequenceWithHasNameSource = """
    public class AppDbContext : DbContext
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.HasSequence<int>("OrderNumbers").HasName("SEQ_OrderNumbers");
        }
    }
    """;

[Fact]
public void ParseUnrecognizedModelLevelCalls_HasNameChainedOntoHasSequence_IsNotVisitedAtAll()
{
    // `.HasName(...)` chained onto a model-level call is never visited by either
    // ParseUnrecognizedCalls (entity-scoped only) or ParseUnrecognizedModelLevelCalls
    // (only inspects the call made directly on modelBuilder, not its chained tail) —
    // so it neither fires a diagnostic nor gets silently "recognized". This documents
    // that current, acceptable behavior rather than asserting a diagnostic that the
    // codebase has no mechanism to produce yet.
    var diagnostics = new FluentConfigParser().ParseUnrecognizedModelLevelCalls(SequenceWithHasNameSource);

    Assert.DoesNotContain(diagnostics, d => d.Message.Contains("HasName"));
}
```

- [ ] **Step 2: Run the tests to verify the HasAlternateKey case currently fails**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter "FullyQualifiedName~ParseUnrecognizedCalls_HasNameChainedOntoHasAlternateKey"`
Expected: FAIL — `HasName` is currently in the flat `RecognizedCallNames` set, so this call is wrongly recognized today and no diagnostic fires.

- [ ] **Step 3: Add GetOwnerCallName to FluentSyntaxHelpers**

In `src/EfSchemaVisualizer.Core/Parsing/FluentSyntaxHelpers.cs`, add near `FindChainedCall`:

```csharp
/// Returns the method name of the invocation `call` is chained onto — e.g. given the
/// `HasName` call in `entity.HasKey(e => e.Id).HasName("PK_Id")`, returns "HasKey". Returns
/// null when `call` is chained directly onto the entity/builder receiver (an identifier),
/// not onto another invocation.
internal static string? GetOwnerCallName(InvocationExpressionSyntax call)
{
    return call.Expression is MemberAccessExpressionSyntax { Expression: InvocationExpressionSyntax inner }
        && inner.Expression is MemberAccessExpressionSyntax { Name.Identifier.Text: var ownerName }
        ? ownerName
        : null;
}
```

- [ ] **Step 4: Add ContextSensitiveCallNames and fix ParseUnrecognizedCalls**

In `src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs`:

1. Remove `"HasName"` from the existing flat `RecognizedCallNames` set.
2. Add a new field right after `RecognizedCallNames`:

```csharp
    /// Method names whose recognition depends on what they're chained onto — unlike every
    /// other entry in RecognizedCallNames, these collide with real, unrelated EF constructs
    /// this parser doesn't read (e.g. `HasAlternateKey(...).HasName(...)`,
    /// `HasSequence(...).HasName(...)`). Each key maps to the set of owner-call names it's
    /// actually read under.
    private static readonly Dictionary<string, HashSet<string>> ContextSensitiveCallNames = new()
    {
        ["HasName"] = new HashSet<string> { "HasKey", "HasIndex" },
    };
```

3. Replace the recognition check inside `ParseUnrecognizedCalls`:

```csharp
                if (RecognizedCallNames.Contains(methodName))
                {
                    continue;
                }
```

with:

```csharp
                if (ContextSensitiveCallNames.TryGetValue(methodName, out var allowedOwners))
                {
                    var ownerName = FluentSyntaxHelpers.GetOwnerCallName(call);
                    if (ownerName is not null && allowedOwners.Contains(ownerName))
                    {
                        continue;
                    }
                }
                else if (RecognizedCallNames.Contains(methodName))
                {
                    continue;
                }
```

- [ ] **Step 5: Run all three new tests**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter "FullyQualifiedName~HasNameChained"`
Expected: PASS — `HasAlternateKey(...).HasName(...)` now flagged, `HasKey(...).HasName(...)` still not flagged, `HasSequence(...).HasName(...)` (model-level) unaffected as documented.

- [ ] **Step 6: Run the full Core + Web suites (this is the highest-regression-risk change in the plan — it touches every existing HasName/HasIndex test)**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests && dotnet test tests/EfSchemaVisualizer.Web.Tests`
Expected: PASS, with particular attention to every existing `ParseKeys_HasName_*` and `ParseIndexes_Chained*` test still passing unchanged.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "Scope HasName recognition to HasKey/HasIndex chains, fixing false-recognition on HasAlternateKey/HasSequence"
```

---

## Task 14: Round-trip fuzz coverage

**Files:**
- Modify: `tests/EfSchemaVisualizer.Core.Tests/RoundTripFuzzTests.cs`

**Interfaces:**
- Consumes: every editor/parser method added in Tasks 1-13.

- [ ] **Step 1: Add a check-constraint round-trip case**

In `tests/EfSchemaVisualizer.Core.Tests/RoundTripFuzzTests.cs`, following the existing pattern for the relationship `ConstraintName` round-trip test (parse → edit via `DiagramEditor` → download/reparse → assert survival), add a fixture with an entity carrying two `HasCheckConstraint` calls, rename one via `DiagramEditor.SetCheckConstraint`, remove the other via `RemoveCheckConstraint`, reparse the resulting `ConfigSource`, and assert the final `CheckConstraints` list matches expectations (one renamed constraint present, the removed one absent, and any untouched code elsewhere in the fixture unchanged).

- [ ] **Step 2: Add a computed-column and sequence round-trip case**

Add a fixture with a `HasComputedColumnSql` property and a `HasSequence`/`UseSequence` pair; edit the computed column's SQL and the sequence's `StartsAt` via `DiagramEditor`; reparse; assert both new values are present and the rest of the source (an unrelated, intentionally-unmodeled call, e.g. an existing `HasCheckConstraint` left untouched) survives byte-for-byte, following the same `Assert.Contains(...)` byte-survival style the existing `HasDefaultValueSql` case in this file uses.

- [ ] **Step 3: Run the full test suite**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests && dotnet test tests/EfSchemaVisualizer.Web.Tests`
Expected: PASS

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "Extend round-trip fuzz coverage to check constraints, computed columns, and sequences"
```

---

## Task 15: Update the backlog

**Files:**
- Modify: `docs/backlog.md`

- [ ] **Step 1: Mark the backlog item done**

In `docs/backlog.md`, under "Priority 2 — EF surface not parsed at all", change the "SQL-shaped mapping" bullet's checkbox from `[ ]` to `[x]` and append a short summary (mirroring the style of the other completed Priority 0/1 items) naming the commits/PR and noting what's still explicitly out of scope (e.g. `HasSequence(Type, string, string?)`'s non-generic-non-string overload, `HasAlternateKey`/`HasSequence` naming itself, and the model-level `HasName`-chain gap documented in Task 13's Step 1 test).

- [ ] **Step 2: Commit**

```bash
git add docs/backlog.md
git commit -m "Mark SQL-shaped mapping backlog item done"
```
