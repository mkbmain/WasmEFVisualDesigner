# IEntityTypeConfiguration<T> Rewriter Write-Back Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Every `OnModelCreatingRewriter` mutator writes into whichever scope an entity's config already lives in — a `modelBuilder.Entity<T>(...)` block or an `IEntityTypeConfiguration<T>.Configure(...)` method — instead of only ever recognizing the former.

**Architecture:** Replace every mutator's `FluentSyntaxHelpers.FindEntityConfigInvocations(root, entityName)` lookup (which only matches `Entity<T>()` invocations) with a call to the already-existing `FluentSyntaxHelpers.FindConfigurationScopes(root)` (which yields both shapes), filtered to the target entity. A new shared helper, `GetScopeBlockAndReceiver`, extracts "the statement block to insert into and the identifier fluent calls chain off" uniformly from either an `Entity<T>()` invocation's lambda or a `Configure` method's body. `FluentSyntaxHelpers.FindCallsNamed`/`GetPropertyNameFor` already operate on `SyntaxNode` generically and need no changes. `RenameEntityReferences` and `RemoveEntity` get new branches for the class-declaration-level concerns (base-list generic argument, `Configure` parameter type, whole-class deletion) that have no `Entity<T>()` equivalent.

**Tech Stack:** C#, Roslyn (Microsoft.CodeAnalysis.CSharp), xUnit.

## Global Constraints

- Every mutator must keep its exact current behavior for `Entity<T>()`-only sources — this is a pure additive generalization, verified by the existing 85+ `OnModelCreatingRewriterTests` continuing to pass unchanged.
- When an entity has config in both an `Entity<T>()` block and an `IEntityTypeConfiguration<T>` class, the `Entity<T>()` block wins (edits land there; the `IEntityTypeConfiguration<T>` class is left untouched). This falls out of `FluentSyntaxHelpers.FindConfigurationScopes`'s existing yield order (all invocation-scopes are yielded before all class-scopes) — no new ordering logic needed, only verified by a test.
- Brand-new entities added via `AddEntity` always synthesize a `modelBuilder.Entity<T>(...)` block in `OnModelCreating`, never an `IEntityTypeConfiguration<T>` class — unchanged from today.
- `FluentSyntaxHelpers` is `internal static`; `OnModelCreatingRewriter` is in the same assembly (`EfSchemaVisualizer.Core`), so no `InternalsVisibleTo` changes are needed to call its internal members.
- Run `dotnet test` from `/root/RiderProjects/EfSchemaVisualizer` after every task; all tests (338+ before this plan, growing as tasks add tests) must stay green.

---

### Task 1: Shared scope-resolution helper + migrate `RewriteMaxLength`/`RemoveMaxLength`

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/Parsing/FluentSyntaxHelpers.cs:108-119` (`GetPropertyLambdaParameterName`)
- Modify: `src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs:14-101` (`RewriteMaxLength` and its private helpers), `:1296-1316` (`RemoveMaxLength`)
- Test: `tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs`

**Interfaces:**
- Produces: `OnModelCreatingRewriter.GetScopeBlockAndReceiver(SyntaxNode scope) : (BlockSyntax Block, string ReceiverName)` and `OnModelCreatingRewriter.FindConfigScopes(CompilationUnitSyntax root, string entityName) : List<SyntaxNode>` — both private static, used by every subsequent task in this plan.
- Consumes: `FluentSyntaxHelpers.FindConfigurationScopes(CompilationUnitSyntax root) : IEnumerable<(string EntityName, SyntaxNode Scope)>` (already exists, unchanged) and `FluentSyntaxHelpers.FindCallsNamed(SyntaxNode scope, string methodName)` (already exists, unchanged — already scope-shape-agnostic).

- [ ] **Step 1: Write the failing tests**

Add to `OnModelCreatingRewriterTests.cs`:

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
    public void RewriteMaxLength_EntityTypeConfigurationStyle_MutatesExistingCall()
    {
        var result = new OnModelCreatingRewriter()
            .RewriteMaxLength(SourceUsingEntityTypeConfiguration, entityName: "Person", propertyName: "Name", newMaxLength: 150);

        Assert.Contains("builder.Property(e => e.Name).HasMaxLength(150)", result);
        Assert.DoesNotContain("HasMaxLength(100)", result);
    }

    private const string SourceUsingEntityTypeConfigurationNoMaxLengthYet = """
        public class PersonConfiguration : IEntityTypeConfiguration<Person>
        {
            public void Configure(EntityTypeBuilder<Person> builder)
            {
                builder.Property(e => e.Name);
            }
        }
        """;

    [Fact]
    public void RewriteMaxLength_EntityTypeConfigurationStyle_AppendsOntoExistingPropertyCall()
    {
        var result = new OnModelCreatingRewriter()
            .RewriteMaxLength(SourceUsingEntityTypeConfigurationNoMaxLengthYet, entityName: "Person", propertyName: "Name", newMaxLength: 50);

        Assert.Contains("builder.Property(e => e.Name).HasMaxLength(50)", result);
    }

    private const string SourceUsingEntityTypeConfigurationEmptyConfigure = """
        public class PersonConfiguration : IEntityTypeConfiguration<Person>
        {
            public void Configure(EntityTypeBuilder<Person> builder)
            {
            }
        }
        """;

    [Fact]
    public void RewriteMaxLength_EntityTypeConfigurationStyle_InsertsNewStatementIntoConfigureBody()
    {
        var result = new OnModelCreatingRewriter()
            .RewriteMaxLength(SourceUsingEntityTypeConfigurationEmptyConfigure, entityName: "Person", propertyName: "Email", newMaxLength: 255);

        Assert.Contains("builder.Property(e => e.Email).HasMaxLength(255)", result);
    }

    [Fact]
    public void RemoveMaxLength_EntityTypeConfigurationStyle_RemovesCallButKeepsPropertyCall()
    {
        var result = new OnModelCreatingRewriter()
            .RemoveMaxLength(SourceUsingEntityTypeConfiguration, entityName: "Person", propertyName: "Name");

        Assert.Contains("builder.Property(e => e.Name);", result);
        Assert.DoesNotContain("HasMaxLength", result);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test /root/RiderProjects/EfSchemaVisualizer/tests/EfSchemaVisualizer.Core.Tests --filter "FullyQualifiedName~RewriteMaxLength_EntityTypeConfigurationStyle|FullyQualifiedName~RemoveMaxLength_EntityTypeConfigurationStyle"`
Expected: FAIL — `RewriteMaxLength`/`RemoveMaxLength` don't find anything inside a `Configure` method today, so `RewriteMaxLength_EntityTypeConfigurationStyle_MutatesExistingCall` and `_AppendsOntoExistingPropertyCall` fall through to `InsertEntityBlock`/`InsertPrecisionEntityBlock`-style synthesis (throws `InvalidOperationException: No OnModelCreating method found in source.` since the fixture has no `OnModelCreating` method at all), and `RemoveMaxLength_EntityTypeConfigurationStyle_RemovesCallButKeepsPropertyCall` returns the source unchanged.

- [ ] **Step 3: Generalize `GetPropertyLambdaParameterName` to accept any scope**

In `src/EfSchemaVisualizer.Core/Parsing/FluentSyntaxHelpers.cs`, change:

```csharp
    public static string GetPropertyLambdaParameterName(InvocationExpressionSyntax entityInvocation)
    {
        foreach (var propertyCall in FindCallsNamed(entityInvocation, "Property"))
```

to:

```csharp
    public static string GetPropertyLambdaParameterName(SyntaxNode scope)
    {
        foreach (var propertyCall in FindCallsNamed(scope, "Property"))
```

(No other lines in that method change — `FindCallsNamed` already takes `SyntaxNode`.)

- [ ] **Step 4: Add the shared scope helpers to `OnModelCreatingRewriter`**

In `src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs`, add near `FindOnModelCreatingMethod` (after line 1222, right before `BuildEntityInvocationStatement`):

```csharp
    /// Given a config scope from `FluentSyntaxHelpers.FindConfigurationScopes` — either an
    /// `Entity&lt;T&gt;(entity =&gt; { ... })` invocation or an `IEntityTypeConfiguration&lt;T&gt;.Configure(...)`
    /// method — returns the statement block to search/insert into and the identifier fluent
    /// calls are chained off (the `Entity&lt;T&gt;()` lambda's parameter, or `Configure`'s own parameter).
    private static (BlockSyntax Block, string ReceiverName) GetScopeBlockAndReceiver(SyntaxNode scope)
    {
        if (scope is InvocationExpressionSyntax entityInvocation)
        {
            var lambda = (SimpleLambdaExpressionSyntax)entityInvocation.ArgumentList.Arguments.Single().Expression;
            return (lambda.Block!, lambda.Parameter.Identifier.Text);
        }

        if (scope is MethodDeclarationSyntax configureMethod)
        {
            return (configureMethod.Body!, configureMethod.ParameterList.Parameters.Single().Identifier.Text);
        }

        throw new InvalidOperationException($"Unsupported configuration scope node type: {scope.GetType().Name}");
    }

    /// All config scopes for `entityName` — `Entity&lt;T&gt;()` invocations first (in file order),
    /// then `IEntityTypeConfiguration&lt;T&gt;` `Configure` methods, matching
    /// `FluentSyntaxHelpers.FindConfigurationScopes`'s yield order. Callers that pick
    /// `.FirstOrDefault()` therefore prefer an existing `Entity&lt;T&gt;()` block over a config class
    /// when both exist for the same entity.
    private static List<SyntaxNode> FindConfigScopes(CompilationUnitSyntax root, string entityName)
    {
        return FluentSyntaxHelpers.FindConfigurationScopes(root)
            .Where(s => s.EntityName == entityName)
            .Select(s => s.Scope)
            .ToList();
    }
```

- [ ] **Step 5: Migrate `RewriteMaxLength` and its private helpers**

Replace lines 14-84 of `OnModelCreatingRewriter.cs`:

```csharp
    public string RewriteMaxLength(string sourceCode, string entityName, string propertyName, int newMaxLength)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var entityInvocations = FluentSyntaxHelpers.FindEntityConfigInvocations(root, entityName).ToList();

        var existingMaxLengthCall = entityInvocations
            .SelectMany(entityInvocation => FluentSyntaxHelpers.FindCallsNamed(entityInvocation, "HasMaxLength"))
            .FirstOrDefault(call => FluentSyntaxHelpers.GetPropertyNameFor(call) == propertyName);

        if (existingMaxLengthCall is not null)
        {
            return MutateExistingMaxLength(root, existingMaxLengthCall, newMaxLength);
        }

        var existingPropertyCall = entityInvocations
            .SelectMany(entityInvocation => FluentSyntaxHelpers.FindCallsNamed(entityInvocation, "Property"))
            .FirstOrDefault(call => FluentSyntaxHelpers.GetPropertyNameForPropertyCall(call) == propertyName);

        if (existingPropertyCall is not null)
        {
            return AppendMaxLengthToPropertyCall(root, existingPropertyCall, newMaxLength);
        }

        var existingEntityInvocation = entityInvocations.FirstOrDefault();

        if (existingEntityInvocation is not null)
        {
            return InsertPropertyStatement(root, existingEntityInvocation, propertyName, newMaxLength);
        }

        return InsertEntityBlock(root, entityName, propertyName, newMaxLength);
    }

    private static string MutateExistingMaxLength(CompilationUnitSyntax root, InvocationExpressionSyntax targetCall, int newMaxLength)
    {
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

    private static string AppendMaxLengthToPropertyCall(CompilationUnitSyntax root, InvocationExpressionSyntax propertyCall, int newMaxLength)
    {
        var maxLengthCall = BuildMaxLengthCall(propertyCall, newMaxLength);

        var newRoot = root.ReplaceNode(propertyCall, maxLengthCall);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    private static string InsertPropertyStatement(CompilationUnitSyntax root, InvocationExpressionSyntax entityInvocation, string propertyName, int newMaxLength)
    {
        var lambda = (SimpleLambdaExpressionSyntax)entityInvocation.ArgumentList.Arguments.Single().Expression;
        var block = lambda.Block!;
        var blockReceiverName = lambda.Parameter.Identifier.Text;
        var propertyLambdaParam = FluentSyntaxHelpers.GetPropertyLambdaParameterName(entityInvocation);

        var newStatement = BuildPropertyStatement(blockReceiverName, propertyLambdaParam, propertyName, newMaxLength);
        var newBlock = block.AddStatements(newStatement);

        var newRoot = root.ReplaceNode(block, newBlock);
        return newRoot.NormalizeWhitespace().ToFullString();
    }
```

with:

```csharp
    public string RewriteMaxLength(string sourceCode, string entityName, string propertyName, int newMaxLength)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var scopes = FindConfigScopes(root, entityName);

        var existingMaxLengthCall = scopes
            .SelectMany(scope => FluentSyntaxHelpers.FindCallsNamed(scope, "HasMaxLength"))
            .FirstOrDefault(call => FluentSyntaxHelpers.GetPropertyNameFor(call) == propertyName);

        if (existingMaxLengthCall is not null)
        {
            return MutateExistingMaxLength(root, existingMaxLengthCall, newMaxLength);
        }

        var existingPropertyCall = scopes
            .SelectMany(scope => FluentSyntaxHelpers.FindCallsNamed(scope, "Property"))
            .FirstOrDefault(call => FluentSyntaxHelpers.GetPropertyNameForPropertyCall(call) == propertyName);

        if (existingPropertyCall is not null)
        {
            return AppendMaxLengthToPropertyCall(root, existingPropertyCall, newMaxLength);
        }

        var existingScope = scopes.FirstOrDefault();

        if (existingScope is not null)
        {
            return InsertPropertyStatement(root, existingScope, propertyName, newMaxLength);
        }

        return InsertEntityBlock(root, entityName, propertyName, newMaxLength);
    }

    private static string MutateExistingMaxLength(CompilationUnitSyntax root, InvocationExpressionSyntax targetCall, int newMaxLength)
    {
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

    private static string AppendMaxLengthToPropertyCall(CompilationUnitSyntax root, InvocationExpressionSyntax propertyCall, int newMaxLength)
    {
        var maxLengthCall = BuildMaxLengthCall(propertyCall, newMaxLength);

        var newRoot = root.ReplaceNode(propertyCall, maxLengthCall);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    private static string InsertPropertyStatement(CompilationUnitSyntax root, SyntaxNode scope, string propertyName, int newMaxLength)
    {
        var (block, blockReceiverName) = GetScopeBlockAndReceiver(scope);
        var propertyLambdaParam = FluentSyntaxHelpers.GetPropertyLambdaParameterName(scope);

        var newStatement = BuildPropertyStatement(blockReceiverName, propertyLambdaParam, propertyName, newMaxLength);
        var newBlock = block.AddStatements(newStatement);

        var newRoot = root.ReplaceNode(block, newBlock);
        return newRoot.NormalizeWhitespace().ToFullString();
    }
```

- [ ] **Step 6: Migrate `RemoveMaxLength`**

Replace lines 1296-1316:

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

with:

```csharp
    public string RemoveMaxLength(string sourceCode, string entityName, string propertyName)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var scopes = FindConfigScopes(root, entityName);

        var existingMaxLengthCall = scopes
            .SelectMany(scope => FluentSyntaxHelpers.FindCallsNamed(scope, "HasMaxLength"))
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

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test /root/RiderProjects/EfSchemaVisualizer/tests/EfSchemaVisualizer.Core.Tests`
Expected: PASS, all tests (including the 4 new ones and all pre-existing `OnModelCreatingRewriterTests`).

- [ ] **Step 8: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Parsing/FluentSyntaxHelpers.cs src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs
git commit -m "$(cat <<'EOF'
Generalize RewriteMaxLength/RemoveMaxLength to IEntityTypeConfiguration<T> scopes

Adds shared GetScopeBlockAndReceiver/FindConfigScopes helpers to
OnModelCreatingRewriter so edits land in whichever scope an entity's
config already lives in, not only modelBuilder.Entity<T>() blocks.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 2: Migrate `RewritePrecision`/`RemovePrecision` and `RewriteIsRequired`/`RemoveIsRequired`

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs:103-183` (`RewritePrecision`, `InsertPrecisionStatement`, `InsertPrecisionEntityBlock`), `:238-341` (`RewriteIsRequired`, `InsertIsRequiredPropertyStatement`, `InsertIsRequiredEntityBlock`), `:1318-1360` (`RemovePrecision`, `RemoveIsRequired`)
- Test: `tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs`

**Interfaces:**
- Consumes: `GetScopeBlockAndReceiver`, `FindConfigScopes` from Task 1.

- [ ] **Step 1: Write the failing tests**

```csharp
    private const string SourceUsingEntityTypeConfigurationForPrecisionAndRequired = """
        public class PersonConfiguration : IEntityTypeConfiguration<Person>
        {
            public void Configure(EntityTypeBuilder<Person> builder)
            {
                builder.Property(e => e.Balance).HasPrecision(18, 2);
                builder.Property(e => e.Name).IsRequired();
            }
        }
        """;

    [Fact]
    public void RewritePrecision_EntityTypeConfigurationStyle_MutatesExistingCall()
    {
        var result = new OnModelCreatingRewriter()
            .RewritePrecision(SourceUsingEntityTypeConfigurationForPrecisionAndRequired, entityName: "Person", propertyName: "Balance", precision: 10, scale: 4);

        Assert.Contains("builder.Property(e => e.Balance).HasPrecision(10, 4)", result);
    }

    [Fact]
    public void RemovePrecision_EntityTypeConfigurationStyle_RemovesCall()
    {
        var result = new OnModelCreatingRewriter()
            .RemovePrecision(SourceUsingEntityTypeConfigurationForPrecisionAndRequired, entityName: "Person", propertyName: "Balance");

        Assert.DoesNotContain("HasPrecision", result);
    }

    [Fact]
    public void RewriteIsRequired_EntityTypeConfigurationStyle_MutatesExistingCall()
    {
        var result = new OnModelCreatingRewriter()
            .RewriteIsRequired(SourceUsingEntityTypeConfigurationForPrecisionAndRequired, entityName: "Person", propertyName: "Name", newIsRequired: false);

        Assert.Contains("builder.Property(e => e.Name).IsRequired(false)", result);
    }

    [Fact]
    public void RemoveIsRequired_EntityTypeConfigurationStyle_RemovesCall()
    {
        var result = new OnModelCreatingRewriter()
            .RemoveIsRequired(SourceUsingEntityTypeConfigurationForPrecisionAndRequired, entityName: "Person", propertyName: "Name");

        Assert.DoesNotContain("IsRequired", result);
    }

    private const string SourceUsingEntityTypeConfigurationNoPrecisionYet = """
        public class PersonConfiguration : IEntityTypeConfiguration<Person>
        {
            public void Configure(EntityTypeBuilder<Person> builder)
            {
            }
        }
        """;

    [Fact]
    public void RewritePrecision_EntityTypeConfigurationStyle_InsertsNewStatement()
    {
        var result = new OnModelCreatingRewriter()
            .RewritePrecision(SourceUsingEntityTypeConfigurationNoPrecisionYet, entityName: "Person", propertyName: "Balance", precision: 12, scale: null);

        Assert.Contains("builder.Property(e => e.Balance).HasPrecision(12)", result);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test /root/RiderProjects/EfSchemaVisualizer/tests/EfSchemaVisualizer.Core.Tests --filter "FullyQualifiedName~EntityTypeConfigurationStyle&FullyQualifiedName~Precision|FullyQualifiedName~EntityTypeConfigurationStyle&FullyQualifiedName~IsRequired"`
Expected: FAIL, same reason as Task 1 (falls through to `InsertPrecisionEntityBlock`/`InsertIsRequiredEntityBlock`, which throw since there's no `OnModelCreating` method in these fixtures).

- [ ] **Step 3: Migrate `RewritePrecision`**

In `RewritePrecision` (lines 103-136), replace every `FluentSyntaxHelpers.FindEntityConfigInvocations(root, entityName).ToList()` with `FindConfigScopes(root, entityName)`, rename the local `entityInvocations` to `scopes`, rename the `.SelectMany(entityInvocation => ...)` lambda parameter to `scope`, and rename `existingEntityInvocation` to `existingScope`:

```csharp
    public string RewritePrecision(string sourceCode, string entityName, string propertyName, int precision, int? scale)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var scopes = FindConfigScopes(root, entityName);

        var existingPrecisionCall = scopes
            .SelectMany(scope => FluentSyntaxHelpers.FindCallsNamed(scope, "HasPrecision"))
            .FirstOrDefault(call => FluentSyntaxHelpers.GetPropertyNameFor(call) == propertyName);

        if (existingPrecisionCall is not null)
        {
            return MutateExistingPrecision(root, existingPrecisionCall, precision, scale);
        }

        var existingPropertyCall = scopes
            .SelectMany(scope => FluentSyntaxHelpers.FindCallsNamed(scope, "Property"))
            .FirstOrDefault(call => FluentSyntaxHelpers.GetPropertyNameForPropertyCall(call) == propertyName);

        if (existingPropertyCall is not null)
        {
            return AppendPrecisionToPropertyCall(root, existingPropertyCall, precision, scale);
        }

        var existingScope = scopes.FirstOrDefault();

        if (existingScope is not null)
        {
            return InsertPrecisionStatement(root, existingScope, propertyName, precision, scale);
        }

        return InsertPrecisionEntityBlock(root, entityName, propertyName, precision, scale);
    }
```

Replace `InsertPrecisionStatement` (lines 154-166):

```csharp
    private static string InsertPrecisionStatement(CompilationUnitSyntax root, SyntaxNode scope, string propertyName, int precision, int? scale)
    {
        var (block, blockReceiverName) = GetScopeBlockAndReceiver(scope);
        var propertyLambdaParam = FluentSyntaxHelpers.GetPropertyLambdaParameterName(scope);

        var newStatement = BuildPrecisionPropertyStatement(blockReceiverName, propertyLambdaParam, propertyName, precision, scale);
        var newBlock = block.AddStatements(newStatement);

        var newRoot = root.ReplaceNode(block, newBlock);
        return newRoot.NormalizeWhitespace().ToFullString();
    }
```

- [ ] **Step 4: Migrate `RewriteIsRequired`**

Apply the identical rename pattern to `RewriteIsRequired` (lines 238-271):

```csharp
    public string RewriteIsRequired(string sourceCode, string entityName, string propertyName, bool newIsRequired)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var scopes = FindConfigScopes(root, entityName);

        var existingIsRequiredCall = scopes
            .SelectMany(scope => FluentSyntaxHelpers.FindCallsNamed(scope, "IsRequired"))
            .FirstOrDefault(call => FluentSyntaxHelpers.GetPropertyNameFor(call) == propertyName);

        if (existingIsRequiredCall is not null)
        {
            return MutateExistingIsRequired(root, existingIsRequiredCall, newIsRequired);
        }

        var existingPropertyCall = scopes
            .SelectMany(scope => FluentSyntaxHelpers.FindCallsNamed(scope, "Property"))
            .FirstOrDefault(call => FluentSyntaxHelpers.GetPropertyNameForPropertyCall(call) == propertyName);

        if (existingPropertyCall is not null)
        {
            return AppendIsRequiredToPropertyCall(root, existingPropertyCall, newIsRequired);
        }

        var existingScope = scopes.FirstOrDefault();

        if (existingScope is not null)
        {
            return InsertIsRequiredPropertyStatement(root, existingScope, propertyName, newIsRequired);
        }

        return InsertIsRequiredEntityBlock(root, entityName, propertyName, newIsRequired);
    }
```

Replace `InsertIsRequiredPropertyStatement` (lines 312-324):

```csharp
    private static string InsertIsRequiredPropertyStatement(CompilationUnitSyntax root, SyntaxNode scope, string propertyName, bool newIsRequired)
    {
        var (block, blockReceiverName) = GetScopeBlockAndReceiver(scope);
        var propertyLambdaParam = FluentSyntaxHelpers.GetPropertyLambdaParameterName(scope);

        var newStatement = BuildIsRequiredPropertyStatement(blockReceiverName, propertyLambdaParam, propertyName, newIsRequired);
        var newBlock = block.AddStatements(newStatement);

        var newRoot = root.ReplaceNode(block, newBlock);
        return newRoot.NormalizeWhitespace().ToFullString();
    }
```

- [ ] **Step 5: Migrate `RemovePrecision` and `RemoveIsRequired`**

Replace lines 1318-1338 (`RemovePrecision`):

```csharp
    public string RemovePrecision(string sourceCode, string entityName, string propertyName)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var scopes = FindConfigScopes(root, entityName);

        var existingPrecisionCall = scopes
            .SelectMany(scope => FluentSyntaxHelpers.FindCallsNamed(scope, "HasPrecision"))
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

Replace lines 1340-1360 (`RemoveIsRequired`):

```csharp
    public string RemoveIsRequired(string sourceCode, string entityName, string propertyName)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var scopes = FindConfigScopes(root, entityName);

        var existingIsRequiredCall = scopes
            .SelectMany(scope => FluentSyntaxHelpers.FindCallsNamed(scope, "IsRequired"))
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

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test /root/RiderProjects/EfSchemaVisualizer/tests/EfSchemaVisualizer.Core.Tests`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs
git commit -m "$(cat <<'EOF'
Generalize Precision/IsRequired rewriters to IEntityTypeConfiguration<T> scopes

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 3: Migrate `SetKey`/`RemoveKey` and `SetTable`/`RemoveTable`

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs:363-425` (`SetKey`, `InsertKeyStatement`), `:463-481` (`RemoveKey`), `:483-528` (`SetTable`, `InsertTableStatement`), `:912-930` (`RemoveTable`)
- Test: `tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs`

**Interfaces:**
- Consumes: `GetScopeBlockAndReceiver`, `FindConfigScopes` from Task 1.

- [ ] **Step 1: Write the failing tests**

```csharp
    private const string SourceUsingEntityTypeConfigurationForKeyAndTable = """
        public class PersonConfiguration : IEntityTypeConfiguration<Person>
        {
            public void Configure(EntityTypeBuilder<Person> builder)
            {
                builder.HasKey(e => e.Id);
                builder.ToTable("People");
            }
        }
        """;

    [Fact]
    public void SetKey_EntityTypeConfigurationStyle_MutatesExistingCall()
    {
        var result = new OnModelCreatingRewriter()
            .SetKey(SourceUsingEntityTypeConfigurationForKeyAndTable, entityName: "Person", propertyNames: new[] { "PersonId" });

        Assert.Contains("builder.HasKey(e => e.PersonId)", result);
    }

    [Fact]
    public void RemoveKey_EntityTypeConfigurationStyle_RemovesStatement()
    {
        var result = new OnModelCreatingRewriter()
            .RemoveKey(SourceUsingEntityTypeConfigurationForKeyAndTable, entityName: "Person");

        Assert.DoesNotContain("HasKey", result);
    }

    [Fact]
    public void SetTable_EntityTypeConfigurationStyle_MutatesExistingCall()
    {
        var result = new OnModelCreatingRewriter()
            .SetTable(SourceUsingEntityTypeConfigurationForKeyAndTable, entityName: "Person", tableName: "Persons", schema: null);

        Assert.Contains("builder.ToTable(\"Persons\")", result);
    }

    [Fact]
    public void RemoveTable_EntityTypeConfigurationStyle_RemovesStatement()
    {
        var result = new OnModelCreatingRewriter()
            .RemoveTable(SourceUsingEntityTypeConfigurationForKeyAndTable, entityName: "Person");

        Assert.DoesNotContain("ToTable", result);
    }

    private const string SourceUsingEntityTypeConfigurationEmptyForKeyAndTable = """
        public class PersonConfiguration : IEntityTypeConfiguration<Person>
        {
            public void Configure(EntityTypeBuilder<Person> builder)
            {
            }
        }
        """;

    [Fact]
    public void SetKey_EntityTypeConfigurationStyle_InsertsNewStatement()
    {
        var result = new OnModelCreatingRewriter()
            .SetKey(SourceUsingEntityTypeConfigurationEmptyForKeyAndTable, entityName: "Person", propertyNames: new[] { "Id" });

        Assert.Contains("builder.HasKey(e => e.Id)", result);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test /root/RiderProjects/EfSchemaVisualizer/tests/EfSchemaVisualizer.Core.Tests --filter "FullyQualifiedName~EntityTypeConfigurationStyle&(FullyQualifiedName~SetKey|FullyQualifiedName~RemoveKey|FullyQualifiedName~SetTable|FullyQualifiedName~RemoveTable)"`
Expected: FAIL.

- [ ] **Step 3: Migrate `SetKey`**

Replace lines 363-387:

```csharp
    public string SetKey(string sourceCode, string entityName, IReadOnlyList<string> propertyNames)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var scopes = FindConfigScopes(root, entityName);

        var existingHasKeyCall = scopes
            .SelectMany(scope => FluentSyntaxHelpers.FindCallsNamed(scope, "HasKey"))
            .FirstOrDefault();

        if (existingHasKeyCall is not null)
        {
            return MutateExistingKey(root, existingHasKeyCall, propertyNames);
        }

        var existingScope = scopes.FirstOrDefault();

        if (existingScope is not null)
        {
            return InsertKeyStatement(root, existingScope, propertyNames);
        }

        return InsertKeyEntityBlock(root, entityName, propertyNames);
    }
```

Replace `InsertKeyStatement` (lines 397-408):

```csharp
    private static string InsertKeyStatement(CompilationUnitSyntax root, SyntaxNode scope, IReadOnlyList<string> propertyNames)
    {
        var (block, blockReceiverName) = GetScopeBlockAndReceiver(scope);

        var newStatement = BuildHasKeyStatement(blockReceiverName, propertyNames);
        var newBlock = block.AddStatements(newStatement);

        var newRoot = root.ReplaceNode(block, newBlock);
        return newRoot.NormalizeWhitespace().ToFullString();
    }
```

- [ ] **Step 4: Migrate `RemoveKey`**

Replace lines 463-481:

```csharp
    public string RemoveKey(string sourceCode, string entityName)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var scopes = FindConfigScopes(root, entityName);

        var existingHasKeyCall = scopes
            .SelectMany(scope => FluentSyntaxHelpers.FindCallsNamed(scope, "HasKey"))
            .FirstOrDefault();

        if (existingHasKeyCall is null || existingHasKeyCall.Parent is not ExpressionStatementSyntax statement)
        {
            return sourceCode;
        }

        var newRoot = root.RemoveNode(statement, SyntaxRemoveOptions.KeepNoTrivia)!;
        return newRoot.NormalizeWhitespace().ToFullString();
    }
```

- [ ] **Step 5: Migrate `SetTable`**

Replace lines 483-507:

```csharp
    public string SetTable(string sourceCode, string entityName, string tableName, string? schema)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var scopes = FindConfigScopes(root, entityName);

        var existingToTableCall = scopes
            .SelectMany(scope => FluentSyntaxHelpers.FindCallsNamed(scope, "ToTable"))
            .FirstOrDefault();

        if (existingToTableCall is not null)
        {
            return MutateExistingTable(root, existingToTableCall, tableName, schema);
        }

        var existingScope = scopes.FirstOrDefault();

        if (existingScope is not null)
        {
            return InsertTableStatement(root, existingScope, tableName, schema);
        }

        return InsertTableEntityBlock(root, entityName, tableName, schema);
    }
```

Replace `InsertTableStatement` (lines 517-528):

```csharp
    private static string InsertTableStatement(CompilationUnitSyntax root, SyntaxNode scope, string tableName, string? schema)
    {
        var (block, blockReceiverName) = GetScopeBlockAndReceiver(scope);

        var newStatement = BuildToTableStatement(blockReceiverName, tableName, schema);
        var newBlock = block.AddStatements(newStatement);

        var newRoot = root.ReplaceNode(block, newBlock);
        return newRoot.NormalizeWhitespace().ToFullString();
    }
```

- [ ] **Step 6: Migrate `RemoveTable`**

Replace lines 912-930:

```csharp
    public string RemoveTable(string sourceCode, string entityName)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var scopes = FindConfigScopes(root, entityName);

        var existingToTableCall = scopes
            .SelectMany(scope => FluentSyntaxHelpers.FindCallsNamed(scope, "ToTable"))
            .FirstOrDefault();

        if (existingToTableCall is null || existingToTableCall.Parent is not ExpressionStatementSyntax statement)
        {
            return sourceCode;
        }

        var newRoot = root.RemoveNode(statement, SyntaxRemoveOptions.KeepNoTrivia)!;
        return newRoot.NormalizeWhitespace().ToFullString();
    }
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test /root/RiderProjects/EfSchemaVisualizer/tests/EfSchemaVisualizer.Core.Tests`
Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs
git commit -m "$(cat <<'EOF'
Generalize Key/Table rewriters to IEntityTypeConfiguration<T> scopes

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 4: Migrate `SetColumnName`/`RemoveColumnName`, `SetColumnType`/`RemoveColumnType`, `SetDefaultValue`/`RemoveDefaultValue`

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs:578-767` (`SetColumnName`, `SetColumnType`, `RemoveStringArgCall`, `InsertStringArgPropertyStatement`), `:769-910` (`SetDefaultValue`, `InsertDefaultValuePropertyStatement`, `RemoveDefaultValue`)
- Test: `tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs`

**Interfaces:**
- Consumes: `GetScopeBlockAndReceiver`, `FindConfigScopes` from Task 1.

- [ ] **Step 1: Write the failing tests**

```csharp
    private const string SourceUsingEntityTypeConfigurationForColumnsAndDefault = """
        public class PersonConfiguration : IEntityTypeConfiguration<Person>
        {
            public void Configure(EntityTypeBuilder<Person> builder)
            {
                builder.Property(e => e.Name).HasColumnName("full_name").HasColumnType("varchar(100)");
                builder.Property(e => e.Status).HasDefaultValue("Active");
            }
        }
        """;

    [Fact]
    public void SetColumnName_EntityTypeConfigurationStyle_MutatesExistingCall()
    {
        var result = new OnModelCreatingRewriter()
            .SetColumnName(SourceUsingEntityTypeConfigurationForColumnsAndDefault, entityName: "Person", propertyName: "Name", columnName: "display_name");

        Assert.Contains("HasColumnName(\"display_name\")", result);
    }

    [Fact]
    public void RemoveColumnName_EntityTypeConfigurationStyle_RemovesCall()
    {
        var result = new OnModelCreatingRewriter()
            .RemoveColumnName(SourceUsingEntityTypeConfigurationForColumnsAndDefault, entityName: "Person", propertyName: "Name");

        Assert.DoesNotContain("HasColumnName", result);
        Assert.Contains("HasColumnType(\"varchar(100)\")", result);
    }

    [Fact]
    public void SetColumnType_EntityTypeConfigurationStyle_MutatesExistingCall()
    {
        var result = new OnModelCreatingRewriter()
            .SetColumnType(SourceUsingEntityTypeConfigurationForColumnsAndDefault, entityName: "Person", propertyName: "Name", columnType: "text");

        Assert.Contains("HasColumnType(\"text\")", result);
    }

    [Fact]
    public void RemoveColumnType_EntityTypeConfigurationStyle_RemovesCall()
    {
        var result = new OnModelCreatingRewriter()
            .RemoveColumnType(SourceUsingEntityTypeConfigurationForColumnsAndDefault, entityName: "Person", propertyName: "Name");

        Assert.DoesNotContain("HasColumnType", result);
        Assert.Contains("HasColumnName(\"full_name\")", result);
    }

    [Fact]
    public void SetDefaultValue_EntityTypeConfigurationStyle_MutatesExistingCall()
    {
        var result = new OnModelCreatingRewriter()
            .SetDefaultValue(SourceUsingEntityTypeConfigurationForColumnsAndDefault, entityName: "Person", propertyName: "Status", literalText: "\"Inactive\"");

        Assert.Contains("HasDefaultValue(\"Inactive\")", result);
    }

    [Fact]
    public void RemoveDefaultValue_EntityTypeConfigurationStyle_RemovesCall()
    {
        var result = new OnModelCreatingRewriter()
            .RemoveDefaultValue(SourceUsingEntityTypeConfigurationForColumnsAndDefault, entityName: "Person", propertyName: "Status");

        Assert.DoesNotContain("HasDefaultValue", result);
    }

    private const string SourceUsingEntityTypeConfigurationEmptyForColumns = """
        public class PersonConfiguration : IEntityTypeConfiguration<Person>
        {
            public void Configure(EntityTypeBuilder<Person> builder)
            {
            }
        }
        """;

    [Fact]
    public void SetColumnName_EntityTypeConfigurationStyle_InsertsNewStatement()
    {
        var result = new OnModelCreatingRewriter()
            .SetColumnName(SourceUsingEntityTypeConfigurationEmptyForColumns, entityName: "Person", propertyName: "Name", columnName: "full_name");

        Assert.Contains("builder.Property(e => e.Name).HasColumnName(\"full_name\")", result);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test /root/RiderProjects/EfSchemaVisualizer/tests/EfSchemaVisualizer.Core.Tests --filter "FullyQualifiedName~EntityTypeConfigurationStyle&(FullyQualifiedName~ColumnName|FullyQualifiedName~ColumnType|FullyQualifiedName~DefaultValue)"`
Expected: FAIL.

- [ ] **Step 3: Migrate `SetColumnName` and `SetColumnType`**

Replace lines 578-611 (`SetColumnName`):

```csharp
    public string SetColumnName(string sourceCode, string entityName, string propertyName, string columnName)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var scopes = FindConfigScopes(root, entityName);

        var existingCall = scopes
            .SelectMany(scope => FluentSyntaxHelpers.FindCallsNamed(scope, "HasColumnName"))
            .FirstOrDefault(call => FluentSyntaxHelpers.GetPropertyNameFor(call) == propertyName);

        if (existingCall is not null)
        {
            return MutateExistingStringArgCall(root, existingCall, columnName);
        }

        var existingPropertyCall = scopes
            .SelectMany(scope => FluentSyntaxHelpers.FindCallsNamed(scope, "Property"))
            .FirstOrDefault(call => FluentSyntaxHelpers.GetPropertyNameForPropertyCall(call) == propertyName);

        if (existingPropertyCall is not null)
        {
            return AppendStringArgCallToPropertyCall(root, existingPropertyCall, "HasColumnName", columnName);
        }

        var existingScope = scopes.FirstOrDefault();

        if (existingScope is not null)
        {
            return InsertStringArgPropertyStatement(root, existingScope, propertyName, "HasColumnName", columnName);
        }

        return InsertStringArgEntityBlock(root, entityName, propertyName, "HasColumnName", columnName);
    }
```

Replace lines 618-651 (`SetColumnType`):

```csharp
    public string SetColumnType(string sourceCode, string entityName, string propertyName, string columnType)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var scopes = FindConfigScopes(root, entityName);

        var existingCall = scopes
            .SelectMany(scope => FluentSyntaxHelpers.FindCallsNamed(scope, "HasColumnType"))
            .FirstOrDefault(call => FluentSyntaxHelpers.GetPropertyNameFor(call) == propertyName);

        if (existingCall is not null)
        {
            return MutateExistingStringArgCall(root, existingCall, columnType);
        }

        var existingPropertyCall = scopes
            .SelectMany(scope => FluentSyntaxHelpers.FindCallsNamed(scope, "Property"))
            .FirstOrDefault(call => FluentSyntaxHelpers.GetPropertyNameForPropertyCall(call) == propertyName);

        if (existingPropertyCall is not null)
        {
            return AppendStringArgCallToPropertyCall(root, existingPropertyCall, "HasColumnType", columnType);
        }

        var existingScope = scopes.FirstOrDefault();

        if (existingScope is not null)
        {
            return InsertStringArgPropertyStatement(root, existingScope, propertyName, "HasColumnType", columnType);
        }

        return InsertStringArgEntityBlock(root, entityName, propertyName, "HasColumnType", columnType);
    }
```

Replace `InsertStringArgPropertyStatement` (lines 681-693):

```csharp
    private static string InsertStringArgPropertyStatement(CompilationUnitSyntax root, SyntaxNode scope, string propertyName, string methodName, string value)
    {
        var (block, blockReceiverName) = GetScopeBlockAndReceiver(scope);
        var propertyLambdaParam = FluentSyntaxHelpers.GetPropertyLambdaParameterName(scope);

        var newStatement = BuildStringArgPropertyStatement(blockReceiverName, propertyLambdaParam, propertyName, methodName, value);
        var newBlock = block.AddStatements(newStatement);

        var newRoot = root.ReplaceNode(block, newBlock);
        return newRoot.NormalizeWhitespace().ToFullString();
    }
```

- [ ] **Step 4: Migrate `RemoveStringArgCall`** (backs both `RemoveColumnName` and `RemoveColumnType`)

Replace lines 747-767:

```csharp
    private static string RemoveStringArgCall(string sourceCode, string entityName, string propertyName, string methodName)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var scopes = FindConfigScopes(root, entityName);

        var existingCall = scopes
            .SelectMany(scope => FluentSyntaxHelpers.FindCallsNamed(scope, methodName))
            .FirstOrDefault(call => FluentSyntaxHelpers.GetPropertyNameFor(call) == propertyName);

        if (existingCall is null)
        {
            return sourceCode;
        }

        var propertyCallExpression = ((MemberAccessExpressionSyntax)existingCall.Expression).Expression;

        var newRoot = root.ReplaceNode(existingCall, propertyCallExpression);
        return newRoot.NormalizeWhitespace().ToFullString();
    }
```

- [ ] **Step 5: Migrate `SetDefaultValue` and `RemoveDefaultValue`**

Replace lines 769-802 (`SetDefaultValue`):

```csharp
    public string SetDefaultValue(string sourceCode, string entityName, string propertyName, string literalText)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var scopes = FindConfigScopes(root, entityName);

        var existingCall = scopes
            .SelectMany(scope => FluentSyntaxHelpers.FindCallsNamed(scope, "HasDefaultValue"))
            .FirstOrDefault(call => FluentSyntaxHelpers.GetPropertyNameFor(call) == propertyName);

        if (existingCall is not null)
        {
            return MutateExistingDefaultValue(root, existingCall, literalText);
        }

        var existingPropertyCall = scopes
            .SelectMany(scope => FluentSyntaxHelpers.FindCallsNamed(scope, "Property"))
            .FirstOrDefault(call => FluentSyntaxHelpers.GetPropertyNameForPropertyCall(call) == propertyName);

        if (existingPropertyCall is not null)
        {
            return AppendDefaultValueToPropertyCall(root, existingPropertyCall, literalText);
        }

        var existingScope = scopes.FirstOrDefault();

        if (existingScope is not null)
        {
            return InsertDefaultValuePropertyStatement(root, existingScope, propertyName, literalText);
        }

        return InsertDefaultValueEntityBlock(root, entityName, propertyName, literalText);
    }
```

Replace `InsertDefaultValuePropertyStatement` (lines 820-832):

```csharp
    private static string InsertDefaultValuePropertyStatement(CompilationUnitSyntax root, SyntaxNode scope, string propertyName, string literalText)
    {
        var (block, blockReceiverName) = GetScopeBlockAndReceiver(scope);
        var propertyLambdaParam = FluentSyntaxHelpers.GetPropertyLambdaParameterName(scope);

        var newStatement = BuildDefaultValuePropertyStatement(blockReceiverName, propertyLambdaParam, propertyName, literalText);
        var newBlock = block.AddStatements(newStatement);

        var newRoot = root.ReplaceNode(block, newBlock);
        return newRoot.NormalizeWhitespace().ToFullString();
    }
```

Replace `RemoveDefaultValue` (lines 890-910):

```csharp
    public string RemoveDefaultValue(string sourceCode, string entityName, string propertyName)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var scopes = FindConfigScopes(root, entityName);

        var existingCall = scopes
            .SelectMany(scope => FluentSyntaxHelpers.FindCallsNamed(scope, "HasDefaultValue"))
            .FirstOrDefault(call => FluentSyntaxHelpers.GetPropertyNameFor(call) == propertyName);

        if (existingCall is null)
        {
            return sourceCode;
        }

        var propertyCallExpression = ((MemberAccessExpressionSyntax)existingCall.Expression).Expression;

        var newRoot = root.ReplaceNode(existingCall, propertyCallExpression);
        return newRoot.NormalizeWhitespace().ToFullString();
    }
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test /root/RiderProjects/EfSchemaVisualizer/tests/EfSchemaVisualizer.Core.Tests`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs
git commit -m "$(cat <<'EOF'
Generalize ColumnName/ColumnType/DefaultValue rewriters to IEntityTypeConfiguration<T> scopes

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 5: Migrate `SetIndex`/`RemoveIndex`

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs:1448-1619`
- Test: `tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs`

**Interfaces:**
- Consumes: `GetScopeBlockAndReceiver`, `FindConfigScopes` from Task 1.

- [ ] **Step 1: Write the failing tests**

```csharp
    private const string SourceUsingEntityTypeConfigurationForIndex = """
        public class PersonConfiguration : IEntityTypeConfiguration<Person>
        {
            public void Configure(EntityTypeBuilder<Person> builder)
            {
                builder.HasIndex(e => e.Email);
            }
        }
        """;

    [Fact]
    public void SetIndex_EntityTypeConfigurationStyle_MutatesExistingCall()
    {
        var result = new OnModelCreatingRewriter()
            .SetIndex(SourceUsingEntityTypeConfigurationForIndex, entityName: "Person", propertyNames: new[] { "Email" }, isUnique: true, name: "IX_Person_Email");

        Assert.Contains("builder.HasIndex(e => e.Email, \"IX_Person_Email\").IsUnique()", result);
    }

    [Fact]
    public void RemoveIndex_EntityTypeConfigurationStyle_RemovesStatement()
    {
        var result = new OnModelCreatingRewriter()
            .RemoveIndex(SourceUsingEntityTypeConfigurationForIndex, entityName: "Person", propertyNames: new[] { "Email" });

        Assert.DoesNotContain("HasIndex", result);
    }

    private const string SourceUsingEntityTypeConfigurationEmptyForIndex = """
        public class PersonConfiguration : IEntityTypeConfiguration<Person>
        {
            public void Configure(EntityTypeBuilder<Person> builder)
            {
            }
        }
        """;

    [Fact]
    public void SetIndex_EntityTypeConfigurationStyle_InsertsNewStatement()
    {
        var result = new OnModelCreatingRewriter()
            .SetIndex(SourceUsingEntityTypeConfigurationEmptyForIndex, entityName: "Person", propertyNames: new[] { "Name" }, isUnique: false, name: null);

        Assert.Contains("builder.HasIndex(e => e.Name)", result);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test /root/RiderProjects/EfSchemaVisualizer/tests/EfSchemaVisualizer.Core.Tests --filter "FullyQualifiedName~EntityTypeConfigurationStyle&FullyQualifiedName~Index"`
Expected: FAIL.

- [ ] **Step 3: Migrate `SetIndex`**

Replace lines 1448-1476:

```csharp
    public string SetIndex(string sourceCode, string entityName, IReadOnlyList<string> propertyNames, bool isUnique, string? name = null)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var scopes = FindConfigScopes(root, entityName);

        var existingHasIndexCall = scopes
            .SelectMany(scope => FluentSyntaxHelpers.FindCallsNamed(scope, "HasIndex"))
            .FirstOrDefault(call =>
            {
                var args = FluentSyntaxHelpers.TryReadIndexPropertyNames(call);
                return args is not null && args.Value.PropertyNames.SequenceEqual(propertyNames);
            });

        if (existingHasIndexCall is not null)
        {
            return MutateExistingIndex(root, existingHasIndexCall, propertyNames, isUnique, name);
        }

        var existingScope = scopes.FirstOrDefault();

        if (existingScope is not null)
        {
            return InsertIndexStatement(root, existingScope, propertyNames, isUnique, name);
        }

        return InsertIndexEntityBlock(root, entityName, propertyNames, isUnique, name);
    }
```

Replace `InsertIndexStatement` (lines 1493-1509):

```csharp
    private static string InsertIndexStatement(
        CompilationUnitSyntax root,
        SyntaxNode scope,
        IReadOnlyList<string> propertyNames,
        bool isUnique,
        string? name)
    {
        var (block, blockReceiverName) = GetScopeBlockAndReceiver(scope);

        var newStatement = BuildHasIndexStatement(blockReceiverName, propertyNames, isUnique, name);
        var newBlock = block.AddStatements(newStatement);

        var newRoot = root.ReplaceNode(block, newBlock);
        return newRoot.NormalizeWhitespace().ToFullString();
    }
```

- [ ] **Step 4: Migrate `RemoveIndex`**

Replace lines 1598-1619:

```csharp
    public string RemoveIndex(string sourceCode, string entityName, IReadOnlyList<string> propertyNames)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var scopes = FindConfigScopes(root, entityName);

        var existingHasIndexCall = scopes
            .SelectMany(scope => FluentSyntaxHelpers.FindCallsNamed(scope, "HasIndex"))
            .FirstOrDefault(call =>
            {
                var args = FluentSyntaxHelpers.TryReadIndexPropertyNames(call);
                return args is not null && args.Value.PropertyNames.SequenceEqual(propertyNames);
            });

        if (existingHasIndexCall is null)
            return sourceCode;

        var statement = existingHasIndexCall.Ancestors().OfType<ExpressionStatementSyntax>().First();
        var newRoot = root.RemoveNode(statement, SyntaxRemoveOptions.KeepNoTrivia)!;
        return newRoot.NormalizeWhitespace().ToFullString();
    }
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test /root/RiderProjects/EfSchemaVisualizer/tests/EfSchemaVisualizer.Core.Tests`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs
git commit -m "$(cat <<'EOF'
Generalize Index rewriters to IEntityTypeConfiguration<T> scopes

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 6: Migrate `SetRelationship`/`RemoveRelationship` and `RenamePropertyReferences`

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs:972-1018` (`SetRelationship`, `InsertRelationshipStatement`), `:1143-1175` (`RemoveRelationship`), `:1396-1446` (`RenamePropertyReferences`)
- Test: `tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs`

**Interfaces:**
- Consumes: `GetScopeBlockAndReceiver`, `FindConfigScopes` from Task 1.

- [ ] **Step 1: Write the failing tests**

```csharp
    private const string SourceUsingEntityTypeConfigurationForRelationshipAndRename = """
        public class BlogConfiguration : IEntityTypeConfiguration<Blog>
        {
            public void Configure(EntityTypeBuilder<Blog> builder)
            {
                builder.Property(e => e.Title).HasMaxLength(100);
            }
        }
        """;

    [Fact]
    public void SetRelationship_EntityTypeConfigurationStyle_InsertsIntoExistingScope()
    {
        // SetRelationship scopes a OneToMany relationship onto DependentEntity (the FK-holding
        // side), so DependentEntity must be "Blog" to land in this fixture's existing
        // BlogConfiguration : IEntityTypeConfiguration<Blog> scope.
        var relationship = new RelationshipModel(
            PrincipalEntity: "Post",
            DependentEntity: "Blog",
            Kind: RelationshipKind.OneToMany,
            PrincipalNavigation: "Blogs",
            DependentNavigation: "Post",
            ForeignKeyProperties: new[] { "PostId" });

        var result = new OnModelCreatingRewriter()
            .SetRelationship(SourceUsingEntityTypeConfigurationForRelationshipAndRename, relationship);

        Assert.Contains("builder.HasOne<Post>(x => x.Post).WithMany(x => x.Blogs).HasForeignKey(d => d.PostId)", result);
    }

    [Fact]
    public void RenamePropertyReferences_EntityTypeConfigurationStyle_RenamesPropertyLambda()
    {
        var result = new OnModelCreatingRewriter()
            .RenamePropertyReferences(SourceUsingEntityTypeConfigurationForRelationshipAndRename, entityName: "Blog", oldPropertyName: "Title", newPropertyName: "Headline");

        Assert.Contains("builder.Property(e => e.Headline).HasMaxLength(100)", result);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test /root/RiderProjects/EfSchemaVisualizer/tests/EfSchemaVisualizer.Core.Tests --filter "FullyQualifiedName~EntityTypeConfigurationStyle&(FullyQualifiedName~SetRelationship|FullyQualifiedName~RenamePropertyReferences)"`
Expected: FAIL — `SetRelationship` today only recognizes `Entity<T>()` scopes, so this fixture (which has no `Entity<T>()` invocation and no `OnModelCreating` method) falls through to `InsertRelationshipEntityBlock`, which throws `InvalidOperationException: No OnModelCreating method found in source.` `RenameEntityReferences`... (n/a here, this is `RenamePropertyReferences`, which returns the source unchanged since it finds no matching `Property()` call outside a `Entity<T>()` scope).
`RelationshipModel` is declared as `sealed record RelationshipModel(string PrincipalEntity, string DependentEntity, RelationshipKind Kind, string? PrincipalNavigation, string? DependentNavigation, IReadOnlyList<string>? ForeignKeyProperties = null, string? OnDeleteBehavior = null, string? JoinEntityName = null)` in `src/EfSchemaVisualizer.Core/Model/RelationshipModel.cs` — the test in Step 1 already uses this exact shape via named arguments.

- [ ] **Step 3: Migrate `SetRelationship`**

Replace lines 972-990:

```csharp
    public string SetRelationship(string sourceCode, RelationshipModel relationship)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var scopeEntityName = relationship.Kind == RelationshipKind.ManyToMany
            ? relationship.PrincipalEntity
            : relationship.DependentEntity;

        var scopes = FindConfigScopes(root, scopeEntityName);
        var existingScope = scopes.FirstOrDefault();

        if (existingScope is not null)
        {
            return InsertRelationshipStatement(root, existingScope, relationship);
        }

        return InsertRelationshipEntityBlock(root, scopeEntityName, relationship);
    }
```

Replace `InsertRelationshipStatement` (lines 992-1003):

```csharp
    private static string InsertRelationshipStatement(CompilationUnitSyntax root, SyntaxNode scope, RelationshipModel relationship)
    {
        var (block, blockReceiverName) = GetScopeBlockAndReceiver(scope);

        var newStatement = BuildRelationshipStatement(blockReceiverName, relationship);
        var newBlock = block.AddStatements(newStatement);

        var newRoot = root.ReplaceNode(block, newBlock);
        return newRoot.NormalizeWhitespace().ToFullString();
    }
```

- [ ] **Step 4: Migrate `RemoveRelationship`**

Replace lines 1143-1175:

```csharp
    public string RemoveRelationship(string sourceCode, RelationshipModel relationship)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var scopeEntityName = relationship.Kind == RelationshipKind.ManyToMany
            ? relationship.PrincipalEntity
            : relationship.DependentEntity;
        var otherEntityName = relationship.Kind == RelationshipKind.ManyToMany
            ? relationship.DependentEntity
            : relationship.PrincipalEntity;
        var methodName = relationship.Kind == RelationshipKind.ManyToMany ? "HasMany" : "HasOne";
        var expectedNavigation = relationship.Kind == RelationshipKind.ManyToMany
            ? relationship.PrincipalNavigation
            : relationship.DependentNavigation;

        var scopes = FindConfigScopes(root, scopeEntityName);

        var matchingCall = scopes
            .SelectMany(scope => FluentSyntaxHelpers.FindCallsNamed(scope, methodName))
            .FirstOrDefault(call =>
                HasGenericTypeArgument(call, otherEntityName)
                || (expectedNavigation is not null && TryGetNavigationPropertyName(call) == expectedNavigation));

        if (matchingCall is null
            || matchingCall.Ancestors().OfType<ExpressionStatementSyntax>().FirstOrDefault() is not { } statement)
        {
            return sourceCode;
        }

        var newRoot = root.RemoveNode(statement, SyntaxRemoveOptions.KeepNoTrivia)!;
        return newRoot.NormalizeWhitespace().ToFullString();
    }
```

- [ ] **Step 5: Migrate `RenamePropertyReferences`**

Replace lines 1396-1410 (the lookup portion; leave the argument-shape branching below line 1412 unchanged):

```csharp
    public string RenamePropertyReferences(string sourceCode, string entityName, string oldPropertyName, string newPropertyName)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var scopes = FindConfigScopes(root, entityName);

        var existingPropertyCall = scopes
            .SelectMany(scope => FluentSyntaxHelpers.FindCallsNamed(scope, "Property"))
            .FirstOrDefault(call => FluentSyntaxHelpers.GetPropertyNameForPropertyCall(call) == oldPropertyName);

        if (existingPropertyCall is null)
        {
            return sourceCode;
        }
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test /root/RiderProjects/EfSchemaVisualizer/tests/EfSchemaVisualizer.Core.Tests`
Expected: PASS. If `SetRelationship_EntityTypeConfigurationStyle_InsertsIntoExistingScope` fails only on the exact generated call text (e.g. `HasOne<Blog>` vs a different generic-argument choice), inspect `BuildRelationshipCall`/`BuildRelationshipStatement` (lines 1020-1076, unchanged by this task) and adjust the assertion to match what that existing, un-migrated code actually emits — do not change `BuildRelationshipStatement` itself, this task only touches scope lookup.

- [ ] **Step 7: Commit**

```bash
git add src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs
git commit -m "$(cat <<'EOF'
Generalize Relationship/RenamePropertyReferences rewriters to IEntityTypeConfiguration<T> scopes

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 7: Extend `RenameEntityReferences` to patch `IEntityTypeConfiguration<T>` class references

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/Parsing/FluentSyntaxHelpers.cs` (new helpers)
- Modify: `src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs:1362-1394` (`RenameEntityReferences`)
- Test: `tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs`

**Interfaces:**
- Produces: `FluentSyntaxHelpers.TryGetEntityTypeConfigurationTypeArgument(ClassDeclarationSyntax classDeclaration, string entityName) : IdentifierNameSyntax?` and `FluentSyntaxHelpers.TryGetConfigureParameterEntityTypeArgument(MethodDeclarationSyntax configureMethod, string entityName) : IdentifierNameSyntax?`, both `internal static`.

- [ ] **Step 1: Write the failing test**

```csharp
    private const string SourceUsingEntityTypeConfigurationForRename = """
        public class BlogConfiguration : IEntityTypeConfiguration<Blog>
        {
            public void Configure(EntityTypeBuilder<Blog> builder)
            {
                builder.Property(e => e.Title).HasMaxLength(100);
            }
        }
        """;

    [Fact]
    public void RenameEntityReferences_EntityTypeConfigurationStyle_PatchesBaseListAndConfigureParameter()
    {
        var result = new OnModelCreatingRewriter()
            .RenameEntityReferences(SourceUsingEntityTypeConfigurationForRename, oldEntityName: "Blog", newEntityName: "Journal");

        Assert.Contains("IEntityTypeConfiguration<Journal>", result);
        Assert.Contains("Configure(EntityTypeBuilder<Journal> builder)", result);
        Assert.DoesNotContain("Blog", result);
    }
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test /root/RiderProjects/EfSchemaVisualizer/tests/EfSchemaVisualizer.Core.Tests --filter "FullyQualifiedName~RenameEntityReferences_EntityTypeConfigurationStyle"`
Expected: FAIL — `RenameEntityReferences` currently finds zero targets for this source (no `Entity<T>()` invocation, no `DbSet<T>` property) and returns it unchanged.

- [ ] **Step 3: Add the two lookup helpers to `FluentSyntaxHelpers`**

Add after `TryGetEntityTypeConfigurationEntityName` (after line 319 in `FluentSyntaxHelpers.cs`), and change that existing method's accessibility from `private` to `internal` so `OnModelCreatingRewriter` can call the shared base-type matching logic it needs (used internally by the new helper below):

```csharp
    /// If `classDeclaration` implements `IEntityTypeConfiguration&lt;entityName&gt;`, returns the
    /// `IdentifierNameSyntax` node for `entityName` in that base-list type argument (so a caller
    /// can rename it via `ReplaceNodes`); otherwise null.
    internal static IdentifierNameSyntax? TryGetEntityTypeConfigurationTypeArgument(
        ClassDeclarationSyntax classDeclaration, string entityName)
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

            if (generic is { Identifier.Text: "IEntityTypeConfiguration", TypeArgumentList.Arguments: [IdentifierNameSyntax typeArg] }
                && typeArg.Identifier.Text == entityName)
            {
                return typeArg;
            }
        }

        return null;
    }

    /// If `configureMethod` is `Configure(EntityTypeBuilder&lt;entityName&gt; builder)`, returns the
    /// `IdentifierNameSyntax` node for `entityName` in the parameter's type argument; otherwise null.
    internal static IdentifierNameSyntax? TryGetConfigureParameterEntityTypeArgument(
        MethodDeclarationSyntax configureMethod, string entityName)
    {
        if (configureMethod.Identifier.Text != "Configure")
        {
            return null;
        }

        var parameter = configureMethod.ParameterList.Parameters.SingleOrDefault();

        return parameter?.Type is GenericNameSyntax { Identifier.Text: "EntityTypeBuilder", TypeArgumentList.Arguments: [IdentifierNameSyntax typeArg] }
            && typeArg.Identifier.Text == entityName
                ? typeArg
                : null;
    }
```

Change `private static string? TryGetEntityTypeConfigurationEntityName` (line 296) to `internal static string? TryGetEntityTypeConfigurationEntityName` — no body changes.

- [ ] **Step 4: Extend `RenameEntityReferences`**

Replace lines 1362-1394 in `OnModelCreatingRewriter.cs`:

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
            if (FluentSyntaxHelpers.GetDbSetEntityTypeArgument(property, oldEntityName) is { } dbSetTypeArgument)
            {
                targets.Add(dbSetTypeArgument);
            }
        }

        foreach (var classDeclaration in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            if (FluentSyntaxHelpers.TryGetEntityTypeConfigurationTypeArgument(classDeclaration, oldEntityName) is { } baseListTypeArgument)
            {
                targets.Add(baseListTypeArgument);
            }
        }

        foreach (var configureMethod in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            if (FluentSyntaxHelpers.TryGetConfigureParameterEntityTypeArgument(configureMethod, oldEntityName) is { } parameterTypeArgument)
            {
                targets.Add(parameterTypeArgument);
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

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test /root/RiderProjects/EfSchemaVisualizer/tests/EfSchemaVisualizer.Core.Tests`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Parsing/FluentSyntaxHelpers.cs src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs
git commit -m "$(cat <<'EOF'
Patch IEntityTypeConfiguration<T> base-list and Configure parameter on entity rename

Without this, renaming an entity left its config class referencing a
deleted type (base list and Configure(EntityTypeBuilder<T>) parameter),
breaking compilation.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 8: Extend `RemoveEntity` to delete the whole `IEntityTypeConfiguration<T>` class

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs:1621-1653`
- Test: `tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs`

**Interfaces:**
- Consumes: `FluentSyntaxHelpers.TryGetEntityTypeConfigurationEntityName` (now `internal`, from Task 7).

- [ ] **Step 1: Write the failing test**

```csharp
    private const string SourceUsingEntityTypeConfigurationForRemove = """
        public class Blog
        {
            public int Id { get; set; }
        }

        public class BlogConfiguration : IEntityTypeConfiguration<Blog>
        {
            public void Configure(EntityTypeBuilder<Blog> builder)
            {
                builder.Property(e => e.Id);
            }
        }

        public class UnrelatedConfiguration : IEntityTypeConfiguration<Post>
        {
            public void Configure(EntityTypeBuilder<Post> builder)
            {
            }
        }
        """;

    [Fact]
    public void RemoveEntity_EntityTypeConfigurationStyle_DeletesWholeConfigClass()
    {
        var result = new OnModelCreatingRewriter()
            .RemoveEntity(SourceUsingEntityTypeConfigurationForRemove, entityName: "Blog");

        Assert.DoesNotContain("BlogConfiguration", result);
        Assert.DoesNotContain("IEntityTypeConfiguration<Blog>", result);
        Assert.Contains("UnrelatedConfiguration", result);
        Assert.Contains("IEntityTypeConfiguration<Post>", result);
    }
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test /root/RiderProjects/EfSchemaVisualizer/tests/EfSchemaVisualizer.Core.Tests --filter "FullyQualifiedName~RemoveEntity_EntityTypeConfigurationStyle"`
Expected: FAIL — `RemoveEntity` only looks at `InvocationExpressionSyntax` and `PropertyDeclarationSyntax` nodes today, so `BlogConfiguration` is left untouched and the assertion `Assert.DoesNotContain("BlogConfiguration", result)` fails.

- [ ] **Step 3: Extend `RemoveEntity`**

Replace lines 1621-1653:

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
            if (FluentSyntaxHelpers.GetDbSetEntityTypeArgument(property, entityName) is not null)
            {
                nodesToRemove.Add(property);
            }
        }

        foreach (var classDeclaration in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            if (FluentSyntaxHelpers.TryGetEntityTypeConfigurationEntityName(classDeclaration) == entityName)
            {
                nodesToRemove.Add(classDeclaration);
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

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test /root/RiderProjects/EfSchemaVisualizer/tests/EfSchemaVisualizer.Core.Tests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs
git commit -m "$(cat <<'EOF'
Delete the whole IEntityTypeConfiguration<T> class when its entity is removed

Mirrors RemoveEntity's existing modelBuilder.Entity<T>() removal:
leaves no orphaned config class referencing a deleted type.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 9: Both-styles tie-break regression test, new-entity-always-`OnModelCreating` regression test, backlog update

**Files:**
- Test: `tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs`
- Modify: `docs/backlog.md`

**Interfaces:**
- Consumes: All prior tasks' migrated methods.

- [ ] **Step 1: Write the failing tests**

```csharp
    private const string SourceWithBothStylesForSameEntity = """
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

        public class PersonConfiguration : IEntityTypeConfiguration<Person>
        {
            public void Configure(EntityTypeBuilder<Person> builder)
            {
                builder.Property(e => e.Email).HasMaxLength(50);
            }
        }
        """;

    [Fact]
    public void RewriteMaxLength_BothStylesConfigureSameEntity_PrefersEntityBlockOverConfigClass()
    {
        var result = new OnModelCreatingRewriter()
            .RewriteMaxLength(SourceWithBothStylesForSameEntity, entityName: "Person", propertyName: "Age", newMaxLength: 3);

        // No existing HasMaxLength/Property() call for "Age" in either scope, so this falls to the
        // "insert new statement into an existing scope" tier - which must pick the Entity<T>()
        // block (already has Person config) over the IEntityTypeConfiguration<T> class.
        Assert.Contains("entity.Property(e => e.Age).HasMaxLength(3)", result);
        Assert.DoesNotContain("builder.Property(e => e.Age)", result);

        // The IEntityTypeConfiguration<T> class's own existing config is untouched.
        Assert.Contains("builder.Property(e => e.Email).HasMaxLength(50)", result);
    }

    private const string SourceMixingDbContextAndEntityTypeConfigurationForAdd = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
            }
        }

        public class BlogConfiguration : IEntityTypeConfiguration<Blog>
        {
            public void Configure(EntityTypeBuilder<Blog> builder)
            {
                builder.Property(e => e.Title).HasMaxLength(100);
            }
        }
        """;

    [Fact]
    public void AddEntity_ProjectUsesEntityTypeConfigurationStyleElsewhere_StillSynthesizesOnModelCreatingBlock()
    {
        var result = new OnModelCreatingRewriter()
            .AddEntity(SourceMixingDbContextAndEntityTypeConfigurationForAdd, entityName: "Comment", dbSetPropertyName: "Comments");

        Assert.Contains("modelBuilder.Entity<Comment>(entity =>", result);
        Assert.Contains("DbSet<Comment> Comments", result);
        Assert.DoesNotContain("IEntityTypeConfiguration<Comment>", result);

        // BlogConfiguration is untouched.
        Assert.Contains("builder.Property(e => e.Title).HasMaxLength(100)", result);
    }
```

- [ ] **Step 2: Run the tests to verify they fail or pass for the right reason**

Run: `dotnet test /root/RiderProjects/EfSchemaVisualizer/tests/EfSchemaVisualizer.Core.Tests --filter "FullyQualifiedName~BothStylesConfigureSameEntity|FullyQualifiedName~AddEntity_ProjectUsesEntityTypeConfigurationStyleElsewhere"`
Expected: Both tests should already PASS if Tasks 1-8 are correct — these are pure regression/confirmation tests. `RewriteMaxLength_BothStylesConfigureSameEntity_PrefersEntityBlockOverConfigClass` confirms the ordering guarantee documented in `FindConfigScopes`; `AddEntity_ProjectUsesEntityTypeConfigurationStyleElsewhere_StillSynthesizesOnModelCreatingBlock` confirms `AddEntity` (never touched by this plan) is unaffected. If either fails, that's a real regression from an earlier task — stop and fix it there before proceeding, do not patch it here.

- [ ] **Step 3: Update `docs/backlog.md`**

In `docs/backlog.md`, find the Priority 1 item:

```
- [ ] **`[found]` `IEntityTypeConfiguration<T>` is parse-only — edits can't be
      written back.** Per Priority 3 above, the parser reads config classes but
      the rewriter (`OnModelCreatingRewriter`) only writes into
      `modelBuilder.Entity<T>(...)` blocks. So a project whose config lives in
      `IEntityTypeConfiguration` classes (which the README advertises as
      supported) renders correctly but every diagram edit silently no-ops or
      targets the wrong place. Either build the config-class rewriter path or
      disable/flag editing when the source uses that style.
```

Replace with:

```
- [x] **`[found]` `IEntityTypeConfiguration<T>` is parse-only — edits can't be
      written back.** Per Priority 3 above, the parser reads config classes but
      the rewriter (`OnModelCreatingRewriter`) only writes into
      `modelBuilder.Entity<T>(...)` blocks. So a project whose config lives in
      `IEntityTypeConfiguration` classes (which the README advertises as
      supported) renders correctly but every diagram edit silently no-ops or
      targets the wrong place. Either build the config-class rewriter path or
      disable/flag editing when the source uses that style.
      **Update:** Every `OnModelCreatingRewriter` mutator now resolves its
      target scope via the existing `FluentSyntaxHelpers.FindConfigurationScopes`
      (previously used only by the parser), so edits land in whichever scope
      an entity's config already lives in — `Entity<T>()` block or
      `IEntityTypeConfiguration<T>.Configure` method — via a shared
      `GetScopeBlockAndReceiver` helper. Rename now also patches the config
      class's base-list generic argument and `Configure` parameter type;
      remove now deletes the whole config class. New entities still always
      synthesize into `OnModelCreating`, unchanged, and an entity configured
      in both styles simultaneously has edits prefer the `Entity<T>()` block
      (see `2026-07-16-ientitytypeconfiguration-rewriter-design.md`).
```

- [ ] **Step 4: Run the full test suite**

Run: `dotnet test /root/RiderProjects/EfSchemaVisualizer`
Expected: PASS, all tests across both `EfSchemaVisualizer.Core.Tests` and `EfSchemaVisualizer.Web.Tests`.

- [ ] **Step 5: Commit**

```bash
git add tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs docs/backlog.md
git commit -m "$(cat <<'EOF'
Add both-styles/new-entity regression tests, mark backlog item done

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

## Self-Review Notes

- **Spec coverage:** Architecture (Task 1), receiver-chain matching (verified per-method by every task's tests since `FindCallsNamed`/`GetPropertyNameForPropertyCall` never checked receiver identity — confirmed while reading the source, so no separate task was needed for this), rename handling (Task 7), remove handling (Task 8), both-styles tie-break (Task 9), testing (spread across all tasks) — all covered.
- **Placeholder scan:** No TBD/TODO; every step has literal code.
- **Type consistency:** `GetScopeBlockAndReceiver`/`FindConfigScopes` signatures introduced in Task 1 are used identically (same names, same tuple shape) in every later task.
