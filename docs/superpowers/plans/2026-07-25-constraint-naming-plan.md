# PK/FK Constraint Naming + Index Chain-Form Fix Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add full round-trip support (parse, model, edit, rewrite) for EF Core's `HasKey(...).HasName(...)` (primary key constraint name) and `.HasForeignKey(...).HasConstraintName(...)` (foreign key constraint name), plus fix a read-only gap where `HasIndex(...).HasDatabaseName(...)`/`.HasName(...)` (chained index-name form) is silently unrecognized on parse.

**Architecture:** Follows the codebase's existing per-field pattern used throughout `FluentConfigParser`/`ModelMerger`/`OnModelCreatingRewriter`/`DiagramEditor`/Blazor components: a nullable field flows from a Roslyn-syntax parse, through a `Config` DTO, through `ModelMerger` into the immutable `EntityModel`/`RelationshipModel`, out to the Blazor diagram UI, and back through `DiagramEditor` into `OnModelCreatingRewriter`, which regenerates the fluent call chain via Roslyn `SyntaxFactory`.

**Tech Stack:** C#, Roslyn (`Microsoft.CodeAnalysis.CSharp`), Blazor (`.razor` components), xUnit.

## Global Constraints

- Every new field is nullable (`string?`) and defaults to `null` — untouched files must round-trip byte-for-byte (or whitespace-only, matching existing `SetKey`/`SetIndex` behavior) with no new output.
- All new optional constructor/method parameters are added as **trailing** parameters with default values, so every existing call site (tests included) keeps compiling unchanged.
- New diagnostic codes: `UnreadableHasKeyNameArgument`, `UnreadableHasConstraintNameArgument`. No new diagnostic code for the index chain-form fix (reuses `UnreadableHasIndexArgument`).
- Out of scope: `HasComputedColumnSql`, `HasCheckConstraint`, `HasSequence`/`UseSequence` (tracked separately in `docs/backlog.md`).
- Spec: `docs/superpowers/specs/2026-07-25-constraint-naming-design.md`.

---

### Task 1: PK constraint name — parsing (`ParseKeys`, `KeyConfig`, `EntityModel`)

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/Model/EntityModel.cs`
- Modify: `src/EfSchemaVisualizer.Core/Merging/KeyConfig.cs`
- Modify: `src/EfSchemaVisualizer.Core/Parsing/DiagnosticCodes.cs`
- Modify: `src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs` (`RecognizedCallNames`, `ParseKeys`)
- Test: `tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs`

**Interfaces:**
- Produces: `EntityModel.KeyName` (`string?`, new optional trailing record parameter, default `null`); `KeyConfig(string EntityName, IReadOnlyList<string> PropertyNames, string? Name = null)`; `DiagnosticCodes.UnreadableHasKeyNameArgument`.

- [ ] **Step 1: Write the failing tests**

Add to `tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs` (near the existing `ParseKeys_*` tests around line 452):

```csharp
private const string SourceWithKeyName = """
    public class AppDbContext : DbContext
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Person>(entity =>
            {
                entity.HasKey(e => e.Id).HasName("PK_Person");
            });
        }
    }
    """;

[Fact]
public void ParseKeys_HasName_IsReadAsKeyName()
{
    var result = new FluentConfigParser().ParseKeys(SourceWithKeyName);

    Assert.Empty(result.Diagnostics);
    var config = Assert.Single(result.Value);
    Assert.Equal("PK_Person", config.Name);
    Assert.Equal(new[] { "Id" }, config.PropertyNames);
}

private const string SourceWithUnreadableKeyName = """
    public class AppDbContext : DbContext
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Person>(entity =>
            {
                entity.HasKey(e => e.Id).HasName(someVariable);
            });
        }
    }
    """;

[Fact]
public void ParseKeys_HasName_UnreadableArgument_EmitsDiagnosticButKeepsPropertyNames()
{
    var result = new FluentConfigParser().ParseKeys(SourceWithUnreadableKeyName);

    var diagnostic = Assert.Single(result.Diagnostics);
    Assert.Equal(DiagnosticCodes.UnreadableHasKeyNameArgument, diagnostic.Code);
    var config = Assert.Single(result.Value);
    Assert.Null(config.Name);
    Assert.Equal(new[] { "Id" }, config.PropertyNames);
}

private const string SourceWithKeyNoName = """
    public class AppDbContext : DbContext
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Person>(entity =>
            {
                entity.HasKey(e => e.Id);
            });
        }
    }
    """;

[Fact]
public void ParseKeys_NoHasNameChained_NameIsNull()
{
    var result = new FluentConfigParser().ParseKeys(SourceWithKeyNoName);

    Assert.Empty(result.Diagnostics);
    var config = Assert.Single(result.Value);
    Assert.Null(config.Name);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests/EfSchemaVisualizer.Core.Tests.csproj --filter "FullyQualifiedName~ParseKeys_HasName|FullyQualifiedName~ParseKeys_NoHasNameChained"`
Expected: FAIL — `KeyConfig` has no `Name` member yet (compile error), or once compiling, missing `HasName` support.

- [ ] **Step 3: Add `KeyName` to `EntityModel`**

In `src/EfSchemaVisualizer.Core/Model/EntityModel.cs`, add `KeyName` as a new trailing optional parameter:

```csharp
public sealed record EntityModel(
    string Name,
    IReadOnlyList<PropertyModel> Properties,
    IReadOnlyList<string>? KeyPropertyNames = null,
    IReadOnlyList<IndexModel>? Indexes = null,
    string? TableName = null,
    string? Schema = null,
    bool IsKeyless = false,
    bool IsKeyInferred = false,
    string? ViewName = null,
    string? SqlQuery = null,
    IReadOnlyList<IReadOnlyList<string>>? AlternateKeys = null,
    bool HasQueryFilter = false,
    string? Comment = null,
    bool IsJson = false,
    string? JsonColumnName = null,
    bool IsTemporal = false,
    IReadOnlyList<string>? SplitTables = null,
    string? BaseEntityName = null,
    bool IsOwned = false,
    string? KeyName = null)
{
    public IReadOnlyList<string> KeyPropertyNames { get; init; } = KeyPropertyNames ?? new List<string>();
    public IReadOnlyList<IndexModel> Indexes { get; init; } = Indexes ?? new List<IndexModel>();
    public IReadOnlyList<IReadOnlyList<string>> AlternateKeys { get; init; } = AlternateKeys ?? new List<IReadOnlyList<string>>();
    public IReadOnlyList<string> SplitTables { get; init; } = SplitTables ?? new List<string>();
}
```

- [ ] **Step 4: Add `Name` to `KeyConfig`**

Replace the contents of `src/EfSchemaVisualizer.Core/Merging/KeyConfig.cs`:

```csharp
using System.Collections.Generic;

namespace EfSchemaVisualizer.Core.Merging;

public sealed record KeyConfig(string EntityName, IReadOnlyList<string> PropertyNames, string? Name = null);
```

- [ ] **Step 5: Add the new diagnostic code**

In `src/EfSchemaVisualizer.Core/Parsing/DiagnosticCodes.cs`, add this line directly after `UnreadableHasKeyArgument`:

```csharp
    public const string UnreadableHasKeyNameArgument = nameof(UnreadableHasKeyNameArgument);
```

- [ ] **Step 6: Recognize `HasName` so it stops firing `UnrecognizedConfigCall`**

In `src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs`, add `"HasName"` to `RecognizedCallNames`:

```csharp
    private static readonly HashSet<string> RecognizedCallNames = new()
    {
        "Property", "HasMaxLength", "HasPrecision", "IsRequired", "IsUnicode", "IsFixedLength", "HasKey", "HasAlternateKey", "ToTable",
        "HasColumnName", "HasColumnType", "HasDefaultValue", "HasDefaultValueSql", "HasIndex", "IsUnique",
        "HasFilter", "IsDescending", "IncludeProperties",
        "HasOne", "HasMany", "WithOne", "WithMany", "HasForeignKey", "OnDelete", "UsingEntity",
        "Ignore", "ValueGeneratedOnAdd", "ValueGeneratedOnUpdate", "ValueGeneratedOnAddOrUpdate",
        "ValueGeneratedNever", "UseIdentityColumn", "ToView", "ToSqlQuery", "HasNoKey",
        "IsRowVersion", "IsConcurrencyToken", "HasQueryFilter", "HasComment", "UseCollation", "ToJson",
        "SplitToTable", "OwnsOne", "OwnsMany", "HasName",
    };
```

- [ ] **Step 7: Read `HasName` in `ParseKeys`**

In `src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs`, replace the body of `ParseKeys`:

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

                string? name = null;
                FluentSyntaxHelpers.WalkChainedTail(hasKeyCall, chained =>
                {
                    if (chained.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "HasName" })
                    {
                        return;
                    }

                    var arg = chained.ArgumentList.Arguments.FirstOrDefault();
                    if (arg?.Expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression))
                    {
                        name = literal.Token.ValueText;
                        return;
                    }

                    diagnostics.Add(new Diagnostic(
                        DiagnosticCodes.UnreadableHasKeyNameArgument,
                        "HasName argument is not a string literal and could not be read.",
                        entityName,
                        PropertyName: null,
                        (arg ?? (SyntaxNode)chained).Span));
                });

                results.Add(new KeyConfig(entityName, propertyNames, name));
            }
        }

        return new ParseResult<IReadOnlyList<KeyConfig>>(results, diagnostics);
    }
```

- [ ] **Step 8: Run tests to verify they pass**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests/EfSchemaVisualizer.Core.Tests.csproj --filter "FullyQualifiedName~ParseKeys_HasName|FullyQualifiedName~ParseKeys_NoHasNameChained"`
Expected: PASS (3 tests)

- [ ] **Step 9: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Model/EntityModel.cs src/EfSchemaVisualizer.Core/Merging/KeyConfig.cs src/EfSchemaVisualizer.Core/Parsing/DiagnosticCodes.cs src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs
git commit -m "Parse HasKey().HasName() as the primary key constraint name"
```

---

### Task 2: PK constraint name — merging and rewriter (`ModelMerger.ApplyKeys`, `OnModelCreatingRewriter.SetKey`)

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/Merging/ModelMerger.cs` (`ApplyKeys`)
- Modify: `src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs` (`SetKey` and its private helpers)
- Test: `tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Merging/ModelMergerTests.cs`

**Interfaces:**
- Consumes: `KeyConfig.Name` (Task 1), `EntityModel.KeyName` (Task 1).
- Produces: `OnModelCreatingRewriter.SetKey(string sourceCode, string entityName, IReadOnlyList<string> propertyNames, string? name = null)`.

- [ ] **Step 1: Write the failing `ApplyKeys` test**

`tests/EfSchemaVisualizer.Core.Tests/Merging/ModelMergerTests.cs` already covers `ApplyKeys` (see `ApplyKeys_SetsKeyPropertyNamesOnMatchingEntity_LeavesOthersUntouched` around line 92). Add a new test directly after it:

```csharp
[Fact]
public void ApplyKeys_ConfigHasName_SetsKeyNameOnMatchingEntity()
{
    var entity = new EntityModel("Person", new List<PropertyModel>
    {
        new("Id", "int", IsNullable: false, MaxLength: null),
    });

    var configs = new List<KeyConfig>
    {
        new("Person", new List<string> { "Id" }, "PK_Person"),
    };

    var merged = ModelMerger.ApplyKeys(entity, configs);

    Assert.Equal("PK_Person", merged.KeyName);
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests/EfSchemaVisualizer.Core.Tests.csproj --filter "FullyQualifiedName~ApplyKeys_ConfigHasName"`
Expected: FAIL — compile error, `KeyConfig`'s third positional argument doesn't match `EntityModel.KeyName` (doesn't exist yet).

- [ ] **Step 3: Update `ApplyKeys` to propagate the key name**

In `src/EfSchemaVisualizer.Core/Merging/ModelMerger.cs`, replace `ApplyKeys`:

```csharp
    public static EntityModel ApplyKeys(EntityModel entity, IReadOnlyList<KeyConfig> configs)
    {
        var config = configs.FirstOrDefault(c => c.EntityName == entity.Name);

        return config is null ? entity : entity with { KeyPropertyNames = config.PropertyNames, KeyName = config.Name };
    }
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests/EfSchemaVisualizer.Core.Tests.csproj --filter "FullyQualifiedName~ApplyKeys_ConfigHasName"`
Expected: PASS

- [ ] **Step 5: Write the failing rewriter tests**

Add to `tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs`, near the existing `SetKey_*` tests (around line 560):

```csharp
[Fact]
public void SetKey_WithName_AppendsHasNameCall()
{
    var result = new OnModelCreatingRewriter()
        .SetKey(SourceWithSingleKey, entityName: "Person", propertyNames: new List<string> { "Id" }, name: "PK_Person");

    Assert.Contains("entity.HasKey(e => e.Id).HasName(\"PK_Person\")", result);
}

[Fact]
public void SetKey_ExistingNamedKey_ChangingNameReplacesHasNameCall()
{
    var withName = new OnModelCreatingRewriter()
        .SetKey(SourceWithSingleKey, entityName: "Person", propertyNames: new List<string> { "Id" }, name: "PK_Person");

    var result = new OnModelCreatingRewriter()
        .SetKey(withName, entityName: "Person", propertyNames: new List<string> { "Id" }, name: "PK_PersonRenamed");

    Assert.Contains("entity.HasKey(e => e.Id).HasName(\"PK_PersonRenamed\")", result);
    Assert.DoesNotContain("PK_Person\")", result.Replace("PK_PersonRenamed\")", ""));
}

[Fact]
public void SetKey_ExistingNamedKey_ClearingNameRemovesHasNameCall()
{
    var withName = new OnModelCreatingRewriter()
        .SetKey(SourceWithSingleKey, entityName: "Person", propertyNames: new List<string> { "Id" }, name: "PK_Person");

    var result = new OnModelCreatingRewriter()
        .SetKey(withName, entityName: "Person", propertyNames: new List<string> { "Id" });

    Assert.Contains("entity.HasKey(e => e.Id);", result);
    Assert.DoesNotContain("HasName", result);
}
```

- [ ] **Step 6: Run tests to verify they fail**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests/EfSchemaVisualizer.Core.Tests.csproj --filter "FullyQualifiedName~SetKey_WithName|FullyQualifiedName~SetKey_ExistingNamedKey"`
Expected: FAIL — compile error, `SetKey` has no `name` parameter yet.

- [ ] **Step 7: Extend `SetKey` and its helpers to support a constraint name**

In `src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs`, replace `SetKey` through `BuildHasKeyArgumentList` (the block from `public string SetKey` down to the closing brace of `BuildHasKeyArgumentList`, i.e. what's currently lines 357-455) with:

```csharp
    public string SetKey(string sourceCode, string entityName, IReadOnlyList<string> propertyNames, string? name = null)
    {
        var withoutKeyless = RemoveKeyless(sourceCode, entityName);

        var tree = CSharpSyntaxTree.ParseText(withoutKeyless);
        var root = tree.GetCompilationUnitRoot();

        var scopes = FindConfigScopes(root, entityName);

        var existingHasKeyCall = scopes
            .SelectMany(scope => FluentSyntaxHelpers.FindCallsNamed(scope, "HasKey"))
            .FirstOrDefault();

        if (existingHasKeyCall is not null)
        {
            return MutateExistingKey(root, existingHasKeyCall, propertyNames, name);
        }

        var existingScope = scopes.FirstOrDefault();

        if (existingScope is not null)
        {
            return InsertKeyStatement(root, existingScope, propertyNames, name);
        }

        return InsertKeyEntityBlock(root, entityName, propertyNames, name);
    }

    private static string MutateExistingKey(
        CompilationUnitSyntax root, InvocationExpressionSyntax targetCall, IReadOnlyList<string> propertyNames, string? name)
    {
        var blockReceiverName = ((MemberAccessExpressionSyntax)targetCall.Expression).Expression.ToString();
        var existingStatement = targetCall.Ancestors().OfType<ExpressionStatementSyntax>().First();
        var newStatement = BuildHasKeyStatement(blockReceiverName, propertyNames, name);

        var newRoot = root.ReplaceNode(existingStatement, newStatement);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    private static string InsertKeyStatement(
        CompilationUnitSyntax root, SyntaxNode scope, IReadOnlyList<string> propertyNames, string? name)
    {
        var (block, blockReceiverName) = GetScopeBlockAndReceiver(scope);

        var newStatement = BuildHasKeyStatement(blockReceiverName, propertyNames, name);
        var newBlock = block.AddStatements(newStatement);

        var newRoot = root.ReplaceNode(block, newBlock);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    private static string InsertKeyEntityBlock(
        CompilationUnitSyntax root, string entityName, IReadOnlyList<string> propertyNames, string? name)
    {
        var method = FindOnModelCreatingMethod(root);

        var methodBody = method.Body
            ?? throw new InvalidOperationException("OnModelCreating has no method body.");

        var modelBuilderParamName = method.ParameterList.Parameters.Single().Identifier.Text;

        var keyStatement = BuildHasKeyStatement("entity", propertyNames, name);
        var entityBlockStatement = BuildEntityInvocationStatement(modelBuilderParamName, entityName, SyntaxFactory.Block(keyStatement));

        var newMethodBody = methodBody.AddStatements(entityBlockStatement);
        var newRoot = root.ReplaceNode(methodBody, newMethodBody);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    private static ExpressionStatementSyntax BuildHasKeyStatement(string blockReceiverName, IReadOnlyList<string> propertyNames, string? name)
    {
        ExpressionSyntax expression = SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                SyntaxFactory.IdentifierName(blockReceiverName),
                SyntaxFactory.IdentifierName("HasKey")),
            BuildHasKeyArgumentList(propertyNames));

        if (name is not null)
        {
            expression = ChainCall(expression, "HasName", SyntaxFactory.Argument(
                SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(name))));
        }

        return SyntaxFactory.ExpressionStatement(expression);
    }

    private static ArgumentListSyntax BuildHasKeyArgumentList(IReadOnlyList<string> propertyNames)
    {
        const string lambdaParam = "e";

        ExpressionSyntax body = propertyNames.Count == 1
            ? SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                SyntaxFactory.IdentifierName(lambdaParam),
                SyntaxFactory.IdentifierName(propertyNames[0]))
            : SyntaxFactory.AnonymousObjectCreationExpression(
                SyntaxFactory.SeparatedList(
                    propertyNames.Select(name => SyntaxFactory.AnonymousObjectMemberDeclarator(
                        SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            SyntaxFactory.IdentifierName(lambdaParam),
                            SyntaxFactory.IdentifierName(name))))));

        return SyntaxFactory.ArgumentList(
            SyntaxFactory.SingletonSeparatedList(
                SyntaxFactory.Argument(
                    SyntaxFactory.SimpleLambdaExpression(
                        SyntaxFactory.Parameter(SyntaxFactory.Identifier(lambdaParam)),
                        body))));
    }
```

This relies on the `ChainCall` helper already defined later in the same file (used by `BuildHasIndexStatement`) — no change needed there.

- [ ] **Step 8: Run tests to verify they pass**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests/EfSchemaVisualizer.Core.Tests.csproj --filter "FullyQualifiedName~SetKey"`
Expected: PASS — all `SetKey_*` tests, including the pre-existing ones (`SetKey_ExistingSingleKey_MutatesToCompositeKey`, `SetKey_UnknownEntity_InsertsNewEntityBlock`, etc.) and the three new ones.

- [ ] **Step 9: Run the full Core test suite to check for regressions**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests/EfSchemaVisualizer.Core.Tests.csproj`
Expected: PASS (no regressions — pay particular attention to `RoundTripFuzzTests.NoOpEdits_AreByteIdenticalAcrossEveryConfigKindInTheCorpus`, which calls `SetKey` with no name and asserts only line endings differ, and to the new `ApplyKeys_ConfigHasName_SetsKeyNameOnMatchingEntity` test from Step 1).

- [ ] **Step 10: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Merging/ModelMerger.cs src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs tests/EfSchemaVisualizer.Core.Tests/Merging/ModelMergerTests.cs
git commit -m "Support writing/updating/clearing the PK constraint name in SetKey"
```

---

### Task 3: PK constraint name — DiagramEditor and UI

**Files:**
- Modify: `src/EfSchemaVisualizer.Web/Diagram/DiagramEditor.cs`
- Modify: `src/EfSchemaVisualizer.Web/Diagram/EntityNode.razor`
- Test: `tests/EfSchemaVisualizer.Web.Tests/Diagram/DiagramEditorPropertyPanelTests.cs`

**Interfaces:**
- Consumes: `OnModelCreatingRewriter.SetKey(..., string? name = null)` (Task 2), `EntityModel.KeyName` (Task 1).
- Produces: `DiagramEditor.SetKeyName(string entityName, string? newName)` returning `DiagramEditResult`.

- [ ] **Step 1: Write the failing DiagramEditor tests**

Add to `tests/EfSchemaVisualizer.Web.Tests/Diagram/DiagramEditorPropertyPanelTests.cs`, near the other key-related tests (the class already has `ClassSource`/`ConfigSource` fixtures for a keyed `Person` entity — see lines 8-21):

```csharp
[Fact]
public void SetKeyName_NoExistingName_WritesHasNameCall()
{
    var editor = new DiagramEditor(ClassSource, ConfigSource);

    var result = editor.SetKeyName("Person", "PK_Person");

    Assert.True(result.Success);
    Assert.Equal("PK_Person", editor.Current.Entities.Single().KeyName);
    Assert.Contains("HasName(\"PK_Person\")", editor.ConfigSource);
}

[Fact]
public void SetKeyName_ClearingExistingName_RemovesHasNameCall()
{
    var editor = new DiagramEditor(ClassSource, ConfigSource);
    editor.SetKeyName("Person", "PK_Person");

    var result = editor.SetKeyName("Person", null);

    Assert.True(result.Success);
    Assert.Null(editor.Current.Entities.Single().KeyName);
    Assert.DoesNotContain("HasName", editor.ConfigSource);
}

[Fact]
public void SetKeyName_SameName_IsNoOp()
{
    var editor = new DiagramEditor(ClassSource, ConfigSource);
    editor.SetKeyName("Person", "PK_Person");
    var configSourceBefore = editor.ConfigSource;

    var result = editor.SetKeyName("Person", "PK_Person");

    Assert.True(result.Success);
    Assert.Equal(configSourceBefore, editor.ConfigSource);
}

[Fact]
public void SetKeyName_UnknownEntity_Fails()
{
    var editor = new DiagramEditor(ClassSource, ConfigSource);

    var result = editor.SetKeyName("DoesNotExist", "PK_Foo");

    Assert.False(result.Success);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/EfSchemaVisualizer.Web.Tests/EfSchemaVisualizer.Web.Tests.csproj --filter "FullyQualifiedName~SetKeyName"`
Expected: FAIL — compile error, `DiagramEditor` has no `SetKeyName` method yet.

- [ ] **Step 3: Add `SetKeyName` to `DiagramEditor`**

In `src/EfSchemaVisualizer.Web/Diagram/DiagramEditor.cs`, add this method near `RenameIndex` (around line 409):

```csharp
    public DiagramEditResult SetKeyName(string entityName, string? newName)
    {
        var entity = Current.Entities.FirstOrDefault(e => e.Name == entityName);
        if (entity is null)
        {
            return DiagramEditResult.Fail($"Entity '{entityName}' not found.");
        }

        var normalizedName = string.IsNullOrWhiteSpace(newName) ? null : newName.Trim();
        if (normalizedName == entity.KeyName)
        {
            return DiagramEditResult.Ok();
        }

        var newConfigSource = _configRewriter.SetKey(ConfigSource, entityName, entity.KeyPropertyNames, normalizedName);
        Apply(ClassSource, newConfigSource);
        return DiagramEditResult.Ok();
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/EfSchemaVisualizer.Web.Tests/EfSchemaVisualizer.Web.Tests.csproj --filter "FullyQualifiedName~SetKeyName"`
Expected: PASS (4 tests)

- [ ] **Step 5: Add the PK name input to `EntityNode.razor`**

In `src/EfSchemaVisualizer.Web/Diagram/EntityNode.razor`, the entity header currently has a row with the "Keyless" checkbox (lines 56-70):

```razor
    <div style="padding: 2px 8px; font-size: 0.75em; color: #555; display: flex; align-items: center; gap: 4px;">
        <span title="Database view name (.ToView). Mutually exclusive with Table in EF, but not enforced here.">View:</span>
        <input style="width: 80px;" value="@Node.Entity.ViewName" placeholder="(none)"
               title="Database view name (.ToView). Mutually exclusive with Table in EF, but not enforced here."
               @onchange="e => CommitViewName(e.Value?.ToString())"
               @onpointerdown:stopPropagation="true"
               @onmousedown:stopPropagation="true" />
        <label style="margin-left: 8px;">
            <input type="checkbox" checked="@Node.Entity.IsKeyless"
                   @onchange="e => CommitKeyless((bool)(e.Value ?? false))"
                   @onpointerdown:stopPropagation="true"
                   @onmousedown:stopPropagation="true" />
            Keyless (no primary key)
        </label>
    </div>
```

Replace it with the same block plus a new PK-name row immediately after:

```razor
    <div style="padding: 2px 8px; font-size: 0.75em; color: #555; display: flex; align-items: center; gap: 4px;">
        <span title="Database view name (.ToView). Mutually exclusive with Table in EF, but not enforced here.">View:</span>
        <input style="width: 80px;" value="@Node.Entity.ViewName" placeholder="(none)"
               title="Database view name (.ToView). Mutually exclusive with Table in EF, but not enforced here."
               @onchange="e => CommitViewName(e.Value?.ToString())"
               @onpointerdown:stopPropagation="true"
               @onmousedown:stopPropagation="true" />
        <label style="margin-left: 8px;">
            <input type="checkbox" checked="@Node.Entity.IsKeyless"
                   @onchange="e => CommitKeyless((bool)(e.Value ?? false))"
                   @onpointerdown:stopPropagation="true"
                   @onmousedown:stopPropagation="true" />
            Keyless (no primary key)
        </label>
    </div>
    <div style="padding: 2px 8px; font-size: 0.75em; color: #555; display: flex; align-items: center; gap: 4px;">
        <span title="Primary key constraint name (HasKey().HasName(...)).">PK name:</span>
        <input style="width: 120px;" value="@Node.Entity.KeyName" placeholder="(default)"
               disabled="@(Node.Entity.KeyPropertyNames.Count == 0)"
               title="Primary key constraint name (HasKey().HasName(...))."
               @onchange="e => CommitKeyName(e.Value?.ToString())"
               @onpointerdown:stopPropagation="true"
               @onmousedown:stopPropagation="true" />
    </div>
    @if (_keyNameError is not null)
    {
        <div style="color: red; font-size: 0.8em; padding: 0 8px;">@_keyNameError</div>
    }
```

Then, in the `@code` block, add the handler near `CommitKeyless` (around line 504):

```csharp
    private string? _keyNameError;

    private async Task CommitKeyName(string? newKeyName)
    {
        var result = SafeEdit(() => EditContext.Editor.SetKeyName(Node.Entity.Name, newKeyName));
        if (result.Success)
        {
            _keyNameError = null;
            await EditContext.NotifyChangedAsync();
        }
        else
        {
            _keyNameError = result.Error;
        }
    }
```

- [ ] **Step 6: Build the web project to confirm the Razor component compiles**

Run: `dotnet build src/EfSchemaVisualizer.Web/EfSchemaVisualizer.Web.csproj`
Expected: Build succeeded, 0 errors.

- [ ] **Step 7: Run the full Web test suite**

Run: `dotnet test tests/EfSchemaVisualizer.Web.Tests/EfSchemaVisualizer.Web.Tests.csproj`
Expected: PASS, no regressions.

- [ ] **Step 8: Commit**

```bash
git add src/EfSchemaVisualizer.Web/Diagram/DiagramEditor.cs src/EfSchemaVisualizer.Web/Diagram/EntityNode.razor tests/EfSchemaVisualizer.Web.Tests/Diagram/DiagramEditorPropertyPanelTests.cs
git commit -m "Add PK constraint name editing to DiagramEditor and the diagram UI"
```

---

### Task 4: FK constraint name — parsing (`ParseRelationshipChain`, `RelationshipConfig`, `RelationshipModel`)

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/Model/RelationshipModel.cs`
- Modify: `src/EfSchemaVisualizer.Core/Merging/RelationshipConfig.cs`
- Modify: `src/EfSchemaVisualizer.Core/Parsing/DiagnosticCodes.cs`
- Modify: `src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs` (`RecognizedCallNames`, `ParseRelationshipChain`)
- Test: `tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs`

**Interfaces:**
- Produces: `RelationshipModel.ConstraintName` (`string?`, new trailing optional parameter); `RelationshipConfig.ConstraintName` (`string?`, new trailing optional parameter); `DiagnosticCodes.UnreadableHasConstraintNameArgument`.

- [ ] **Step 1: Write the failing tests**

Add to `tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs`, near the existing `ParseRelationships_OnDelete_*` tests (around line 2030, which use the `OrderCustomerEntities` fixture at line 1749):

```csharp
[Fact]
public void ParseRelationships_HasConstraintName_IsRead()
{
    const string source = """
        public class Ctx : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Order>(entity =>
                {
                    entity.HasOne(d => d.Customer)
                          .WithMany(p => p.Orders)
                          .HasForeignKey(d => d.CustomerId)
                          .HasConstraintName("FK_Order_Customer");
                });
            }
        }
        """;

    var result = new FluentConfigParser().ParseRelationships(source, OrderCustomerEntities);

    Assert.Empty(result.Diagnostics);
    var relationship = Assert.Single(result.Value);
    Assert.Equal("FK_Order_Customer", relationship.ConstraintName);
}

[Fact]
public void ParseRelationships_HasConstraintName_UnreadableArgument_EmitsDiagnostic()
{
    const string source = """
        public class Ctx : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Order>(entity =>
                {
                    entity.HasOne(d => d.Customer)
                          .WithMany(p => p.Orders)
                          .HasForeignKey(d => d.CustomerId)
                          .HasConstraintName(someVariable);
                });
            }
        }
        """;

    var result = new FluentConfigParser().ParseRelationships(source, OrderCustomerEntities);

    var diagnostic = Assert.Single(result.Diagnostics);
    Assert.Equal(DiagnosticCodes.UnreadableHasConstraintNameArgument, diagnostic.Code);
    var relationship = Assert.Single(result.Value);
    Assert.Null(relationship.ConstraintName);
}

[Fact]
public void ParseRelationships_NoHasConstraintName_ConstraintNameIsNull()
{
    const string source = """
        public class Ctx : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Order>(entity =>
                {
                    entity.HasOne(d => d.Customer)
                          .WithMany(p => p.Orders)
                          .HasForeignKey(d => d.CustomerId);
                });
            }
        }
        """;

    var result = new FluentConfigParser().ParseRelationships(source, OrderCustomerEntities);

    Assert.Empty(result.Diagnostics);
    var relationship = Assert.Single(result.Value);
    Assert.Null(relationship.ConstraintName);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests/EfSchemaVisualizer.Core.Tests.csproj --filter "FullyQualifiedName~ParseRelationships_HasConstraintName|FullyQualifiedName~ParseRelationships_NoHasConstraintName"`
Expected: FAIL — compile error, `RelationshipModel`/no `ConstraintName` member yet.

- [ ] **Step 3: Add `ConstraintName` to `RelationshipModel`**

In `src/EfSchemaVisualizer.Core/Model/RelationshipModel.cs`:

```csharp
using System.Collections.Generic;

namespace EfSchemaVisualizer.Core.Model;

public sealed record RelationshipModel(
    string PrincipalEntity,
    string DependentEntity,
    RelationshipKind Kind,
    string? PrincipalNavigation,
    string? DependentNavigation,
    IReadOnlyList<string>? ForeignKeyProperties = null,
    string? OnDeleteBehavior = null,
    string? JoinEntityName = null,
    bool IsInferred = false,
    string? ConstraintName = null)
{
    public IReadOnlyList<string> ForeignKeyProperties { get; init; } = ForeignKeyProperties ?? new List<string>();
}
```

- [ ] **Step 4: Add `ConstraintName` to `RelationshipConfig`**

In `src/EfSchemaVisualizer.Core/Merging/RelationshipConfig.cs`:

```csharp
using System.Collections.Generic;
using EfSchemaVisualizer.Core.Model;

namespace EfSchemaVisualizer.Core.Merging;

public sealed record RelationshipConfig(
    string PrincipalEntity,
    string DependentEntity,
    RelationshipKind Kind,
    string? PrincipalNavigation,
    string? DependentNavigation,
    IReadOnlyList<string>? ForeignKeyProperties = null,
    string? OnDeleteBehavior = null,
    string? JoinEntityName = null,
    string? ConstraintName = null)
{
    public IReadOnlyList<string> ForeignKeyProperties { get; init; } = ForeignKeyProperties ?? new List<string>();
}
```

- [ ] **Step 5: Add the new diagnostic code**

In `src/EfSchemaVisualizer.Core/Parsing/DiagnosticCodes.cs`, add this line directly after `UnreadableOnDeleteArgument`:

```csharp
    public const string UnreadableHasConstraintNameArgument = nameof(UnreadableHasConstraintNameArgument);
```

- [ ] **Step 6: Recognize `HasConstraintName`**

In `src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs`, add `"HasConstraintName"` to `RecognizedCallNames`:

```csharp
    private static readonly HashSet<string> RecognizedCallNames = new()
    {
        "Property", "HasMaxLength", "HasPrecision", "IsRequired", "IsUnicode", "IsFixedLength", "HasKey", "HasAlternateKey", "ToTable",
        "HasColumnName", "HasColumnType", "HasDefaultValue", "HasDefaultValueSql", "HasIndex", "IsUnique",
        "HasFilter", "IsDescending", "IncludeProperties",
        "HasOne", "HasMany", "WithOne", "WithMany", "HasForeignKey", "OnDelete", "UsingEntity",
        "Ignore", "ValueGeneratedOnAdd", "ValueGeneratedOnUpdate", "ValueGeneratedOnAddOrUpdate",
        "ValueGeneratedNever", "UseIdentityColumn", "ToView", "ToSqlQuery", "HasNoKey",
        "IsRowVersion", "IsConcurrencyToken", "HasQueryFilter", "HasComment", "UseCollation", "ToJson",
        "SplitToTable", "OwnsOne", "OwnsMany", "HasName", "HasConstraintName",
    };
```

(This assumes Task 1's Step 6 already ran and added `"HasName"` — if executing this task out of order, add both.)

- [ ] **Step 7: Read `HasConstraintName` in `ParseRelationshipChain`**

In `src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs`, `ParseRelationshipChain` already declares three chain-call locals and walks `withCall`'s tail (this is the block starting `InvocationExpressionSyntax? hasForeignKeyCall = null;`). Replace that declaration-and-walk block:

```csharp
        InvocationExpressionSyntax? hasForeignKeyCall = null;
        InvocationExpressionSyntax? onDeleteCall = null;
        InvocationExpressionSyntax? usingEntityCall = null;

        FluentSyntaxHelpers.WalkChainedTail(withCall, invocation =>
        {
            switch (GetInvokedMethodName(invocation))
            {
                case "HasForeignKey": hasForeignKeyCall = invocation; break;
                case "OnDelete": onDeleteCall = invocation; break;
                case "UsingEntity": usingEntityCall = invocation; break;
            }
        });
```

with:

```csharp
        InvocationExpressionSyntax? hasForeignKeyCall = null;
        InvocationExpressionSyntax? onDeleteCall = null;
        InvocationExpressionSyntax? usingEntityCall = null;
        InvocationExpressionSyntax? hasConstraintNameCall = null;

        FluentSyntaxHelpers.WalkChainedTail(withCall, invocation =>
        {
            switch (GetInvokedMethodName(invocation))
            {
                case "HasForeignKey": hasForeignKeyCall = invocation; break;
                case "OnDelete": onDeleteCall = invocation; break;
                case "UsingEntity": usingEntityCall = invocation; break;
                case "HasConstraintName": hasConstraintNameCall = invocation; break;
            }
        });
```

Then, immediately after the existing `onDeleteBehavior` block (which ends with its closing `}` right before `var joinEntityName = ...`), insert:

```csharp
        string? constraintName = null;
        if (hasConstraintNameCall is not null)
        {
            var arg = hasConstraintNameCall.ArgumentList.Arguments.FirstOrDefault();

            if (arg?.Expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression))
            {
                constraintName = literal.Token.ValueText;
            }
            else
            {
                diagnostics.Add(new Diagnostic(
                    DiagnosticCodes.UnreadableHasConstraintNameArgument,
                    "HasConstraintName argument is not a string literal and could not be read.",
                    dependentEntity,
                    PropertyName: null,
                    (arg ?? (SyntaxNode)hasConstraintNameCall).Span));
            }
        }
```

Finally, update the `results.Add(new RelationshipConfig(...))` call at the end of the method to pass it through:

```csharp
        results.Add(new RelationshipConfig(
            principalEntity,
            dependentEntity,
            kind,
            principalNavigation,
            dependentNavigation,
            foreignKeyProperties,
            onDeleteBehavior,
            joinEntityName,
            constraintName));
```

- [ ] **Step 8: Run tests to verify they pass**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests/EfSchemaVisualizer.Core.Tests.csproj --filter "FullyQualifiedName~ParseRelationships"`
Expected: PASS — all `ParseRelationships_*` tests, including the pre-existing ones and the three new ones.

- [ ] **Step 9: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Model/RelationshipModel.cs src/EfSchemaVisualizer.Core/Merging/RelationshipConfig.cs src/EfSchemaVisualizer.Core/Parsing/DiagnosticCodes.cs src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs
git commit -m "Parse HasForeignKey().HasConstraintName() as the FK constraint name"
```

---

### Task 5: FK constraint name — merging and rewriter (`ApplyRelationships`, `BuildRelationshipStatement`)

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/Merging/ModelMerger.cs` (`ApplyRelationships`)
- Modify: `src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs` (`BuildRelationshipStatement` and a new `AppendHasConstraintName` helper)
- Test: `tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs`

**Interfaces:**
- Consumes: `RelationshipConfig.ConstraintName`, `RelationshipModel.ConstraintName` (Task 4).
- Produces: `OnModelCreatingRewriter.SetRelationship` now writes `HasConstraintName(...)` when `relationship.ConstraintName` is non-null (no signature change — `SetRelationship` already takes the whole `RelationshipModel`).

- [ ] **Step 1: Update `ApplyRelationships`**

In `src/EfSchemaVisualizer.Core/Merging/ModelMerger.cs`, replace `ApplyRelationships`:

```csharp
    public static IReadOnlyList<RelationshipModel> ApplyRelationships(IReadOnlyList<RelationshipConfig> configs)
    {
        return configs
            .Select(c => new RelationshipModel(
                c.PrincipalEntity,
                c.DependentEntity,
                c.Kind,
                c.PrincipalNavigation,
                c.DependentNavigation,
                c.ForeignKeyProperties,
                c.OnDeleteBehavior,
                c.JoinEntityName,
                ConstraintName: c.ConstraintName))
            .ToList();
    }
```

- [ ] **Step 2: Write the failing rewriter tests**

Add to `tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs`, directly after `SetRelationship_OneToMany_WithForeignKey_EmitsHasForeignKey` (around line 2318). Reuse the file's existing `SourceWithNoRelationshipConfig` fixture (defined at line 2272 — a `Blog`/`Post` pair, each with only a bare `HasKey`, and no relationship configured yet), the same fixture `SetRelationship_OneToMany_WithForeignKey_EmitsHasForeignKey` already uses:

```csharp
[Fact]
public void SetRelationship_WithConstraintName_AppendsHasConstraintNameCall()
{
    var relationship = new RelationshipModel(
        "Blog", "Post", RelationshipKind.OneToMany, null, null,
        ForeignKeyProperties: new List<string> { "BlogId" },
        ConstraintName: "FK_Post_Blog");

    var result = new OnModelCreatingRewriter()
        .SetRelationship(SourceWithNoRelationshipConfig, relationship);

    Assert.Contains("entity.HasOne<Blog>().WithMany().HasForeignKey(d => d.BlogId).HasConstraintName(\"FK_Post_Blog\")", result);
}

[Fact]
public void SetRelationship_NoConstraintName_OmitsHasConstraintNameCall()
{
    var relationship = new RelationshipModel(
        "Blog", "Post", RelationshipKind.OneToMany, null, null,
        ForeignKeyProperties: new List<string> { "BlogId" });

    var result = new OnModelCreatingRewriter()
        .SetRelationship(SourceWithNoRelationshipConfig, relationship);

    Assert.DoesNotContain("HasConstraintName", result);
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests/EfSchemaVisualizer.Core.Tests.csproj --filter "FullyQualifiedName~SetRelationship_WithConstraintName|FullyQualifiedName~SetRelationship_NoConstraintName"`
Expected: FAIL — `HasConstraintName` never appears; `AppendHasConstraintName` doesn't exist yet.

- [ ] **Step 4: Add `AppendHasConstraintName` and wire it into `BuildRelationshipStatement`**

In `src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs`, `BuildRelationshipStatement`'s `OneToOne` and `OneToMany` branches currently end with:

```csharp
        if (relationship.Kind == RelationshipKind.OneToOne)
        {
            chain = BuildRelationshipCall(chain, "HasOne", relationship.PrincipalEntity, relationship.DependentNavigation);
            chain = BuildRelationshipCall(chain, "WithOne", targetEntityName: null, relationship.PrincipalNavigation);
            chain = AppendHasForeignKey(chain, relationship.ForeignKeyProperties, relationship.DependentEntity);
            chain = AppendOnDelete(chain, relationship.OnDeleteBehavior);
            return SyntaxFactory.ExpressionStatement(chain);
        }

        // OneToMany
        chain = BuildRelationshipCall(chain, "HasOne", relationship.PrincipalEntity, relationship.DependentNavigation);
        chain = BuildRelationshipCall(chain, "WithMany", targetEntityName: null, relationship.PrincipalNavigation);
        chain = AppendHasForeignKey(chain, relationship.ForeignKeyProperties, dependentGeneric: null);
        chain = AppendOnDelete(chain, relationship.OnDeleteBehavior);
        return SyntaxFactory.ExpressionStatement(chain);
```

Replace with:

```csharp
        if (relationship.Kind == RelationshipKind.OneToOne)
        {
            chain = BuildRelationshipCall(chain, "HasOne", relationship.PrincipalEntity, relationship.DependentNavigation);
            chain = BuildRelationshipCall(chain, "WithOne", targetEntityName: null, relationship.PrincipalNavigation);
            chain = AppendHasForeignKey(chain, relationship.ForeignKeyProperties, relationship.DependentEntity);
            chain = AppendOnDelete(chain, relationship.OnDeleteBehavior);
            chain = AppendHasConstraintName(chain, relationship.ConstraintName);
            return SyntaxFactory.ExpressionStatement(chain);
        }

        // OneToMany
        chain = BuildRelationshipCall(chain, "HasOne", relationship.PrincipalEntity, relationship.DependentNavigation);
        chain = BuildRelationshipCall(chain, "WithMany", targetEntityName: null, relationship.PrincipalNavigation);
        chain = AppendHasForeignKey(chain, relationship.ForeignKeyProperties, dependentGeneric: null);
        chain = AppendOnDelete(chain, relationship.OnDeleteBehavior);
        chain = AppendHasConstraintName(chain, relationship.ConstraintName);
        return SyntaxFactory.ExpressionStatement(chain);
```

Then add a new private helper directly after `AppendOnDelete`:

```csharp
    private static ExpressionSyntax AppendHasConstraintName(ExpressionSyntax chain, string? constraintName)
    {
        if (constraintName is null)
        {
            return chain;
        }

        var argument = SyntaxFactory.Argument(
            SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(constraintName)));

        return SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, chain, SyntaxFactory.IdentifierName("HasConstraintName")),
            SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(argument)));
    }
```

Note: the `ManyToMany` branch does not call `AppendHasConstraintName` — EF's `HasConstraintName` isn't meaningful without a `HasForeignKey` builder, which many-to-many relationships don't expose here.

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests/EfSchemaVisualizer.Core.Tests.csproj --filter "FullyQualifiedName~SetRelationship"`
Expected: PASS

- [ ] **Step 6: Run the full Core test suite to check for regressions**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests/EfSchemaVisualizer.Core.Tests.csproj`
Expected: PASS, no regressions.

- [ ] **Step 7: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Merging/ModelMerger.cs src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs
git commit -m "Write HasConstraintName() for relationships that have one"
```

---

### Task 6: FK constraint name — DiagramEditor and UI

**Files:**
- Modify: `src/EfSchemaVisualizer.Web/Diagram/DiagramEditor.cs` (`SetRelationshipShape`)
- Modify: `src/EfSchemaVisualizer.Web/Diagram/RelationshipLinkLabel.razor`
- Test: `tests/EfSchemaVisualizer.Web.Tests/Diagram/DiagramEditorPropertyPanelTests.cs`

**Interfaces:**
- Consumes: `RelationshipModel.ConstraintName` (Task 4), `OnModelCreatingRewriter.SetRelationship` now honoring it (Task 5).
- Produces: `DiagramEditor.SetRelationshipShape(RelationshipModel relationship, RelationshipKind newKind, IReadOnlyList<string> newForeignKeyProperties, string? newOnDeleteBehavior, string? newConstraintName = null)` — extends the existing method with one new trailing optional parameter rather than adding a parallel method, keeping the single "commit the whole relationship shape" entry point the UI already uses.

- [ ] **Step 1: Write the failing DiagramEditor test**

Add to `tests/EfSchemaVisualizer.Web.Tests/Diagram/DiagramEditorPropertyPanelTests.cs`, near the existing `SetRelationshipShape_*` tests (around line 300, using the `RelationshipClassSource`/`RelationshipConfigSource` fixtures at lines 276-298):

```csharp
[Fact]
public void SetRelationshipShape_SettingConstraintName_WritesHasConstraintNameCall()
{
    var editor = new DiagramEditor(RelationshipClassSource, RelationshipConfigSource);
    var relationship = editor.Current.Relationships.Single();

    var result = editor.SetRelationshipShape(
        relationship, relationship.Kind, relationship.ForeignKeyProperties, relationship.OnDeleteBehavior, "FK_Post_Blog");

    Assert.True(result.Success);
    Assert.Equal("FK_Post_Blog", editor.Current.Relationships.Single().ConstraintName);
    Assert.Contains("HasConstraintName(\"FK_Post_Blog\")", editor.ConfigSource);
}

[Fact]
public void SetRelationshipShape_SameConstraintName_IsNoOp()
{
    var editor = new DiagramEditor(RelationshipClassSource, RelationshipConfigSource);
    var relationship = editor.Current.Relationships.Single();
    editor.SetRelationshipShape(relationship, relationship.Kind, relationship.ForeignKeyProperties, relationship.OnDeleteBehavior, "FK_Post_Blog");
    var updated = editor.Current.Relationships.Single();
    var configSourceBefore = editor.ConfigSource;

    var result = editor.SetRelationshipShape(updated, updated.Kind, updated.ForeignKeyProperties, updated.OnDeleteBehavior, updated.ConstraintName);

    Assert.True(result.Success);
    Assert.Equal(configSourceBefore, editor.ConfigSource);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/EfSchemaVisualizer.Web.Tests/EfSchemaVisualizer.Web.Tests.csproj --filter "FullyQualifiedName~SetRelationshipShape_SettingConstraintName|FullyQualifiedName~SetRelationshipShape_SameConstraintName"`
Expected: FAIL — compile error, `SetRelationshipShape` doesn't accept a 5th argument yet.

- [ ] **Step 3: Extend `SetRelationshipShape`**

In `src/EfSchemaVisualizer.Web/Diagram/DiagramEditor.cs`, replace `SetRelationshipShape`:

```csharp
    public DiagramEditResult SetRelationshipShape(
        RelationshipModel relationship,
        RelationshipKind newKind,
        IReadOnlyList<string> newForeignKeyProperties,
        string? newOnDeleteBehavior,
        string? newConstraintName = null)
    {
        if (!Current.Relationships.Contains(relationship))
        {
            return DiagramEditResult.Fail("Relationship no longer exists.");
        }

        if (newKind == relationship.Kind
            && newForeignKeyProperties.SequenceEqual(relationship.ForeignKeyProperties)
            && newOnDeleteBehavior == relationship.OnDeleteBehavior
            && newConstraintName == relationship.ConstraintName)
        {
            return DiagramEditResult.Ok();
        }

        if (newKind == RelationshipKind.ManyToMany && newForeignKeyProperties.Count > 0)
        {
            return DiagramEditResult.Fail("Many-to-many relationships cannot have a foreign key.");
        }

        var dependent = Current.Entities.First(e => e.Name == relationship.DependentEntity);
        var missingProperty = newForeignKeyProperties.FirstOrDefault(name => !dependent.Properties.Any(p => p.Name == name));
        if (missingProperty is not null)
        {
            return DiagramEditResult.Fail($"'{missingProperty}' is not a property of '{relationship.DependentEntity}'.");
        }

        var updated = relationship with
        {
            Kind = newKind,
            ForeignKeyProperties = newForeignKeyProperties,
            OnDeleteBehavior = newOnDeleteBehavior,
            ConstraintName = newConstraintName,
        };

        var withoutOld = relationship.IsInferred
            ? ConfigSource
            : _configRewriter.RemoveRelationship(ConfigSource, relationship);

        if (!relationship.IsInferred && withoutOld == ConfigSource)
        {
            return DiagramEditResult.Fail("Could not locate this relationship's existing configuration to update.");
        }

        var withNew = _configRewriter.SetRelationship(withoutOld, updated);
        Apply(ClassSource, withNew);
        return DiagramEditResult.Ok();
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/EfSchemaVisualizer.Web.Tests/EfSchemaVisualizer.Web.Tests.csproj --filter "FullyQualifiedName~SetRelationshipShape"`
Expected: PASS — all `SetRelationshipShape_*` tests, including the pre-existing ones and the two new ones.

- [ ] **Step 5: Add the constraint-name input to `RelationshipLinkLabel.razor`**

In `src/EfSchemaVisualizer.Web/Diagram/RelationshipLinkLabel.razor`, the "On delete" `<label>` block currently reads:

```razor
                <label style="display: block;">
                    On delete:
                    <select value="@_onDeleteBehavior" @onchange="e => CommitOnDeleteBehavior(e.Value?.ToString())">
                        <option value="">(default)</option>
                        <option value="Cascade">Cascade</option>
                        <option value="Restrict">Restrict</option>
                        <option value="SetNull">SetNull</option>
                        <option value="NoAction">NoAction</option>
                    </select>
                </label>
```

Add a constraint-name input directly after it (still inside the `if (_kind != RelationshipKind.ManyToMany)` block):

```razor
                <label style="display: block;">
                    On delete:
                    <select value="@_onDeleteBehavior" @onchange="e => CommitOnDeleteBehavior(e.Value?.ToString())">
                        <option value="">(default)</option>
                        <option value="Cascade">Cascade</option>
                        <option value="Restrict">Restrict</option>
                        <option value="SetNull">SetNull</option>
                        <option value="NoAction">NoAction</option>
                    </select>
                </label>
                <label style="display: block;">
                    Constraint name:
                    <input value="@_constraintName" placeholder="(default)"
                           @onchange="e => CommitConstraintName(e.Value?.ToString())"
                           @onpointerdown:stopPropagation="true"
                           @onmousedown:stopPropagation="true" />
                </label>
```

In the `@code` block, add a `_constraintName` field next to `_onDeleteBehavior`:

```csharp
    private string? _onDeleteBehavior;
    private string? _constraintName;
```

Update `ToggleExpand` to initialize it:

```csharp
    private void ToggleExpand()
    {
        _expanded = !_expanded;
        if (_expanded)
        {
            _kind = Label.Relationship.Kind;
            _foreignKeyProperties = Label.Relationship.ForeignKeyProperties.ToList();
            _onDeleteBehavior = Label.Relationship.OnDeleteBehavior;
            _constraintName = Label.Relationship.ConstraintName;
            _error = null;
        }
    }
```

Add the commit handler next to `CommitOnDeleteBehavior`:

```csharp
    private async Task CommitConstraintName(string? name)
    {
        _constraintName = string.IsNullOrWhiteSpace(name) ? null : name;
        await Commit();
    }
```

Update `Commit` to pass it through:

```csharp
    private async Task Commit()
    {
        var foreignKeyProperties = _kind == RelationshipKind.ManyToMany
            ? Array.Empty<string>()
            : _foreignKeyProperties.ToArray();
        var onDeleteBehavior = _kind == RelationshipKind.ManyToMany ? null : _onDeleteBehavior;
        var constraintName = _kind == RelationshipKind.ManyToMany ? null : _constraintName;

        var result = SafeEdit(() => EditContext.Editor.SetRelationshipShape(Label.Relationship, _kind, foreignKeyProperties, onDeleteBehavior, constraintName));
        if (result.Success)
        {
            _error = null;
            await EditContext.NotifyChangedAsync();
        }
        else
        {
            _error = result.Error;
        }
    }
```

- [ ] **Step 6: Build the web project to confirm the Razor component compiles**

Run: `dotnet build src/EfSchemaVisualizer.Web/EfSchemaVisualizer.Web.csproj`
Expected: Build succeeded, 0 errors.

- [ ] **Step 7: Run the full Web test suite**

Run: `dotnet test tests/EfSchemaVisualizer.Web.Tests/EfSchemaVisualizer.Web.Tests.csproj`
Expected: PASS, no regressions.

- [ ] **Step 8: Commit**

```bash
git add src/EfSchemaVisualizer.Web/Diagram/DiagramEditor.cs src/EfSchemaVisualizer.Web/Diagram/RelationshipLinkLabel.razor tests/EfSchemaVisualizer.Web.Tests/Diagram/DiagramEditorPropertyPanelTests.cs
git commit -m "Add FK constraint name editing to DiagramEditor and the relationship UI"
```

---

### Task 7: Index chain-form fix — read `HasDatabaseName`/`.HasName()` on `HasIndex`

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs` (`RecognizedCallNames`, `IndexExtras`, `ReadIndexExtras`, `ParseIndexes`)
- Test: `tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs`

**Interfaces:**
- Consumes: nothing new (uses existing `IndexConfig.Name`).
- Produces: `ParseIndexes` now also recognizes `HasIndex(...).HasDatabaseName(...)` and the legacy `.HasName(...)` alias.

- [ ] **Step 1: Write the failing tests**

Add to `tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs`, near the existing `ParseIndexes_HasFilter_*` tests (around line 990):

```csharp
[Fact]
public void ParseIndexes_ChainedHasDatabaseName_IsReadAsIndexName()
{
    const string source = """
        class Ctx : DbContext {
            protected override void OnModelCreating(ModelBuilder modelBuilder) {
                modelBuilder.Entity<Person>(entity => {
                    entity.HasIndex(e => e.Email).HasDatabaseName("IX_Person_Email");
                });
            }
        }
        """;

    var result = new FluentConfigParser().ParseIndexes(source);

    Assert.Empty(result.Diagnostics);
    var config = Assert.Single(result.Value);
    Assert.Equal("IX_Person_Email", config.Name);
}

[Fact]
public void ParseIndexes_ChainedHasName_IsReadAsIndexName()
{
    const string source = """
        class Ctx : DbContext {
            protected override void OnModelCreating(ModelBuilder modelBuilder) {
                modelBuilder.Entity<Person>(entity => {
                    entity.HasIndex(e => e.Email).HasName("IX_Person_Email");
                });
            }
        }
        """;

    var result = new FluentConfigParser().ParseIndexes(source);

    Assert.Empty(result.Diagnostics);
    var config = Assert.Single(result.Value);
    Assert.Equal("IX_Person_Email", config.Name);
}

[Fact]
public void ParseIndexes_StringArgNameOverload_TakesPrecedenceOverChainedName()
{
    const string source = """
        class Ctx : DbContext {
            protected override void OnModelCreating(ModelBuilder modelBuilder) {
                modelBuilder.Entity<Person>(entity => {
                    entity.HasIndex(e => e.Email, "IX_FromArg").HasDatabaseName("IX_FromChain");
                });
            }
        }
        """;

    var result = new FluentConfigParser().ParseIndexes(source);

    var config = Assert.Single(result.Value);
    Assert.Equal("IX_FromArg", config.Name);
}

[Fact]
public void ParseIndexes_ChainedHasDatabaseName_UnreadableArgument_EmitsDiagnostic()
{
    const string source = """
        class Ctx : DbContext {
            protected override void OnModelCreating(ModelBuilder modelBuilder) {
                modelBuilder.Entity<Person>(entity => {
                    entity.HasIndex(e => e.Email).HasDatabaseName(someVariable);
                });
            }
        }
        """;

    var result = new FluentConfigParser().ParseIndexes(source);

    var config = Assert.Single(result.Value);
    Assert.Null(config.Name);
    var diag = Assert.Single(result.Diagnostics);
    Assert.Equal(DiagnosticCodes.UnreadableHasIndexArgument, diag.Code);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests/EfSchemaVisualizer.Core.Tests.csproj --filter "FullyQualifiedName~ParseIndexes_Chained|FullyQualifiedName~ParseIndexes_StringArgNameOverload"`
Expected: FAIL — chained names aren't read yet, so `config.Name` is null in the first two tests.

- [ ] **Step 3: Recognize `HasDatabaseName`**

In `src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs`, add `"HasDatabaseName"` to `RecognizedCallNames` (in addition to `"HasName"` and `"HasConstraintName"` already added in Tasks 1 and 4):

```csharp
    private static readonly HashSet<string> RecognizedCallNames = new()
    {
        "Property", "HasMaxLength", "HasPrecision", "IsRequired", "IsUnicode", "IsFixedLength", "HasKey", "HasAlternateKey", "ToTable",
        "HasColumnName", "HasColumnType", "HasDefaultValue", "HasDefaultValueSql", "HasIndex", "IsUnique",
        "HasFilter", "IsDescending", "IncludeProperties",
        "HasOne", "HasMany", "WithOne", "WithMany", "HasForeignKey", "OnDelete", "UsingEntity",
        "Ignore", "ValueGeneratedOnAdd", "ValueGeneratedOnUpdate", "ValueGeneratedOnAddOrUpdate",
        "ValueGeneratedNever", "UseIdentityColumn", "ToView", "ToSqlQuery", "HasNoKey",
        "IsRowVersion", "IsConcurrencyToken", "HasQueryFilter", "HasComment", "UseCollation", "ToJson",
        "SplitToTable", "OwnsOne", "OwnsMany", "HasName", "HasConstraintName", "HasDatabaseName",
    };
```

- [ ] **Step 4: Add a `Name` field to `IndexExtras` and read it in `ReadIndexExtras`**

In `src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs`, replace the `IndexExtras` record:

```csharp
    private sealed record IndexExtras(
        bool IsUnique,
        string? Name,
        string? Filter,
        IReadOnlyList<bool>? IsDescending,
        IReadOnlyList<string>? IncludeProperties,
        IReadOnlyList<Diagnostic> Diagnostics);
```

In `ReadIndexExtras`, add a `name` local next to the existing `isUnique`/`filter` locals:

```csharp
    private static IndexExtras ReadIndexExtras(InvocationExpressionSyntax hasIndexCall, string entityName)
    {
        var isUnique = false;
        string? name = null;
        string? filter = null;
        IReadOnlyList<bool>? isDescending = null;
        IReadOnlyList<string>? includeProperties = null;
        var diagnostics = new List<Diagnostic>();
```

Add a new case to the `switch (methodName)` inside the `WalkChainedTail` callback, directly after the existing `"HasFilter"` case:

```csharp
                case "HasDatabaseName":
                case "HasName":
                    {
                        var arg = chained.ArgumentList.Arguments.FirstOrDefault();
                        if (arg?.Expression is LiteralExpressionSyntax nameLiteral && nameLiteral.IsKind(SyntaxKind.StringLiteralExpression))
                        {
                            name = nameLiteral.Token.ValueText;
                            break;
                        }

                        diagnostics.Add(new Diagnostic(
                            DiagnosticCodes.UnreadableHasIndexArgument,
                            $"{methodName} argument is not a string literal and could not be read.",
                            entityName,
                            PropertyName: null,
                            (arg ?? (SyntaxNode)chained).Span));
                        break;
                    }
```

And update the final `return` statement of `ReadIndexExtras`:

```csharp
        return new IndexExtras(isUnique, name, filter, isDescending, includeProperties, diagnostics);
```

- [ ] **Step 5: Combine the arg-overload name and the chained name in `ParseIndexes`**

In `src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs`, `ParseIndexes`'s inner loop currently reads:

```csharp
                var extras = ReadIndexExtras(hasIndexCall, entityName);
                diagnostics.AddRange(extras.Diagnostics);

                results.Add(new IndexConfig(
                    entityName,
                    indexArgs.Value.PropertyNames,
                    extras.IsUnique,
                    indexArgs.Value.Name,
                    extras.Filter,
                    extras.IsDescending,
                    extras.IncludeProperties));
```

Change the `Name` argument to prefer the string-arg overload's name, falling back to the chained name:

```csharp
                var extras = ReadIndexExtras(hasIndexCall, entityName);
                diagnostics.AddRange(extras.Diagnostics);

                results.Add(new IndexConfig(
                    entityName,
                    indexArgs.Value.PropertyNames,
                    extras.IsUnique,
                    indexArgs.Value.Name ?? extras.Name,
                    extras.Filter,
                    extras.IsDescending,
                    extras.IncludeProperties));
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests/EfSchemaVisualizer.Core.Tests.csproj --filter "FullyQualifiedName~ParseIndexes"`
Expected: PASS — all `ParseIndexes_*` tests, including the pre-existing ones and the four new ones.

- [ ] **Step 7: Run the full Core test suite to check for regressions**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests/EfSchemaVisualizer.Core.Tests.csproj`
Expected: PASS, no regressions.

- [ ] **Step 8: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs
git commit -m "Read HasIndex().HasDatabaseName()/.HasName() as the index name"
```

---

### Task 8: Round-trip fuzz coverage for all three new fields

**Files:**
- Modify: `tests/EfSchemaVisualizer.Core.Tests/RoundTripFuzzTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 1-7.
- Produces: no new production code. Deliberately does **not** touch the file's existing shared `ConfigSource`/`EntitySource` corpus (used byte-for-byte or via exact substring matches by roughly a dozen other tests in this file, e.g. `EditingOnePropertyPreservesEverythingElseVerbatim_IncludingUnsupportedConstructs` at line 183, which asserts the exact text `"entity.HasOne(e => e.Blog).WithMany(b => b.Posts).HasForeignKey(e => e.BlogId);"` including its trailing semicolon) — inserting a chained call onto any of those statements would break those unrelated assertions. Instead this task adds a small, dedicated, self-contained corpus with its own no-op round-trip test, following the same `AssertOnlyLineEndingsDiffer` style as `NoOpEdits_AreByteIdenticalAcrossEveryConfigKindInTheCorpus` (lines 137-180).

- [ ] **Step 1: Add the `EfSchemaVisualizer.Core.Model` using**

`tests/EfSchemaVisualizer.Core.Tests/RoundTripFuzzTests.cs` doesn't yet reference `RelationshipModel`/`EntityModel` directly (it only goes through `FluentConfigParser`/`OnModelCreatingRewriter`). Add this using directly after the existing `using EfSchemaVisualizer.Core.Parsing;` (line 3):

```csharp
using EfSchemaVisualizer.Core.Model;
```

- [ ] **Step 2: Write the failing test with its own dedicated corpus**

Add to `tests/EfSchemaVisualizer.Core.Tests/RoundTripFuzzTests.cs`, after `NoOpEdits_AreByteIdenticalAcrossEveryConfigKindInTheCorpus` (after line 180):

```csharp
    private const string NamingEntitySource = """
        public class Blog
        {
            public int BlogId { get; set; }
            public string Url { get; set; }
            public List<Post> Posts { get; set; }
        }

        public class Post
        {
            public int PostId { get; set; }
            public int BlogId { get; set; }
            public Blog Blog { get; set; }
        }
        """;

    private const string NamingConfigSource = """
        public class BloggingContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Blog>(entity =>
                {
                    entity.HasKey(e => e.BlogId).HasName("PK_Blog");
                    entity.HasIndex(e => e.Url).HasDatabaseName("IX_Blog_Url");
                });

                modelBuilder.Entity<Post>(entity =>
                {
                    entity.HasKey(e => e.PostId);
                    entity.HasOne(e => e.Blog).WithMany(b => b.Posts).HasForeignKey(e => e.BlogId).HasConstraintName("FK_Post_Blog");
                });
            }
        }
        """;

    [Fact]
    public void NoOpEdits_PreservePkFkAndIndexNames()
    {
        var parser = new FluentConfigParser();
        var rewriter = new OnModelCreatingRewriter();

        var blogKey = parser.ParseKeys(NamingConfigSource).Value.Single(c => c.EntityName == "Blog");
        Assert.Equal("PK_Blog", blogKey.Name);
        AssertOnlyLineEndingsDiffer(NamingConfigSource, rewriter.SetKey(NamingConfigSource, "Blog", blogKey.PropertyNames, blogKey.Name));

        var blogIndex = parser.ParseIndexes(NamingConfigSource).Value.Single(c => c.EntityName == "Blog");
        Assert.Equal("IX_Blog_Url", blogIndex.Name);
        AssertOnlyLineEndingsDiffer(
            NamingConfigSource, rewriter.SetIndex(NamingConfigSource, "Blog", blogIndex.PropertyNames, blogIndex.IsUnique, blogIndex.Name));

        var entities = new EntityClassParser().Parse(NamingEntitySource).Value;
        var relationship = parser.ParseRelationships(NamingConfigSource, entities).Value.Single();
        Assert.Equal("FK_Post_Blog", relationship.ConstraintName);

        var withoutRelationship = rewriter.RemoveRelationship(NamingConfigSource, new RelationshipModel(
            relationship.PrincipalEntity,
            relationship.DependentEntity,
            relationship.Kind,
            relationship.PrincipalNavigation,
            relationship.DependentNavigation,
            relationship.ForeignKeyProperties,
            relationship.OnDeleteBehavior,
            relationship.JoinEntityName,
            ConstraintName: relationship.ConstraintName));
        AssertOnlyLineEndingsDiffer(
            NamingConfigSource,
            rewriter.SetRelationship(withoutRelationship, new RelationshipModel(
                relationship.PrincipalEntity,
                relationship.DependentEntity,
                relationship.Kind,
                relationship.PrincipalNavigation,
                relationship.DependentNavigation,
                relationship.ForeignKeyProperties,
                relationship.OnDeleteBehavior,
                relationship.JoinEntityName,
                ConstraintName: relationship.ConstraintName)));
    }
```

Note: `parser.ParseRelationships` returns a `RelationshipConfig`, not a `RelationshipModel` (they have the same shape but are different types — see Task 4/5) — the two `new RelationshipModel(...)` constructions above translate the parsed `RelationshipConfig` into the `RelationshipModel` that `RemoveRelationship`/`SetRelationship` require, exactly as `ModelMerger.ApplyRelationships` does in production code.

- [ ] **Step 2: Run the test to verify it passes**

This test needs no new production code (Tasks 1-7 already implemented everything it exercises), so it should pass immediately if written correctly — this step is a verification, not a red/green TDD cycle.

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests/EfSchemaVisualizer.Core.Tests.csproj --filter "FullyQualifiedName~NoOpEdits_PreservePkFkAndIndexNames"`
Expected: PASS

If it fails, that indicates a gap in Tasks 1-7 (most likely: `ParseRelationships` not finding the relationship because `NamingEntitySource`'s property names don't line up with `NamingConfigSource`'s lambda references).

- [ ] **Step 3: Run the entire test suite one final time**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests/EfSchemaVisualizer.Core.Tests.csproj && dotnet test tests/EfSchemaVisualizer.Web.Tests/EfSchemaVisualizer.Web.Tests.csproj`
Expected: PASS across both projects, no regressions anywhere.

- [ ] **Step 4: Commit**

```bash
git add tests/EfSchemaVisualizer.Core.Tests/RoundTripFuzzTests.cs
git commit -m "Extend round-trip fuzz coverage to PK/FK/index naming fields"
```
