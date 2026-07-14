# Editable Diagram Phase 5 (Relationships) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Complete the editable ER diagram feature (`docs/superpowers/specs/2026-07-14-editable-diagram-design.md`) with its fifth and final phase: creating relationships via port-based drag-to-connect, and editing/removing existing relationships by clicking their link label.

**Architecture:** Two brand-new `Core` rewriter methods (`OnModelCreatingRewriter.SetRelationship`/`RemoveRelationship`, the only genuinely new `Core` surface any phase has needed since Phase 1) provide the write-back half of the already-built `ParseRelationships`/`ApplyRelationships` read path. Three new `DiagramEditor` methods (`AddRelationship`, `SetRelationshipShape`, `RemoveRelationship`) compose those two rewriter calls — editing is always "remove the old chain, insert a new one," matching the "two separate composed calls" precedent set by every prior add/remove/rename phase. On the diagram itself, `EntityNodeModel` gains two ports (Left/Right) so `Z.Blazor.Diagrams`' built-in `DragNewLinkBehavior` can create a link by dragging from one entity's port dot to another's; a new `RelationshipLinkLabelModel`/`RelationshipLinkLabel.razor` pair (registered the same way `EntityNodeModel`/`EntityNode` are) replaces the plain read-only link label with a clickable, expandable one that edits Kind/foreign-key or removes the relationship.

**Tech Stack:** C#/.NET, Roslyn (`Microsoft.CodeAnalysis.CSharp`) for the `Core` rewriter, Blazor WebAssembly + `Z.Blazor.Diagrams` 3.0.4.1 for the Web UI, xUnit for `Core` tests.

## Global Constraints

- `RelationshipModel` (`src/EfSchemaVisualizer.Core/Model/RelationshipModel.cs`) has exactly 3 `RelationshipKind` values: `OneToOne`, `OneToMany`, `ManyToMany`. Fields: `PrincipalEntity`, `DependentEntity`, `Kind`, `PrincipalNavigation`, `DependentNavigation`, `ForeignKeyProperties` (defaults to empty list, never null), `OnDeleteBehavior`, `JoinEntityName`.
- **The writer always writes from a fixed, canonical scope, never mutates an existing chain in place:** for `OneToMany`/`OneToOne` it always inserts into the **dependent** entity's `Entity<T>(entity => { ... })` config block (`entity.HasOne<TPrincipal>(...).WithMany/WithOne(...).HasForeignKey(...)`); for `ManyToMany` it always inserts into the **principal** entity's block (`entity.HasMany<TDependent>(...).WithMany(...)`). Editing an existing relationship (via `DiagramEditor.SetRelationshipShape`) is composed as `RemoveRelationship` (old shape) then `SetRelationship` (new shape) — there is no in-place mutate path in `Core`, unlike `HasMaxLength`/`HasKey`/etc. This mirrors the precedent set by rename/add/drop-property phases ("two separate composed calls, not one orchestrated operation").
- **The writer always emits an explicit generic type argument** on `HasOne<T>`/`HasMany<T>` (and `HasForeignKey<T>` for `OneToOne`) regardless of whether a navigation-property lambda is also present. This guarantees the emitted code is always valid EF (no reliance on a nav property existing) and gives `RemoveRelationship` a reliable, purely-syntactic way to relocate the exact chain to delete (by matching the generic argument's type name) without needing to re-resolve navigation properties against a whole `IReadOnlyList<EntityModel>` the way the parser does.
- **`DiagramEditor` never touches `ClassSource`/`ConfigSource` directly** — every edit method calls `_configRewriter`/`_classRewriter`, then the private `Apply(...)` funnel (`DiagramEditor.cs:597`), which is what recomputes `Current` and reconciles `_entityIds`. All new methods in this phase only touch `ConfigSource` (no `Core` model/POCO changes needed for relationships).
- **Blank/no-op inputs are a success no-op, not an error** — e.g. re-committing the same Kind/FK is `DiagramEditResult.Ok()` with no source change, matching every existing `Set*` method's guard (`ToggleKey`, `SetPrecision`, etc.).
- **Drag-to-connect requires the user to release the drag exactly over the target entity's port dot, not just anywhere on its card body.** This is a hard constraint of the library (`PortModel.CanAttachTo` only returns true for another `PortModel` with a different parent node — dropping on a bare node body is rejected when the drag started from a port), not a choice made in this plan. Document this in the UI as short helper text.
- Every interactive element added to `EntityNode.razor`/`RelationshipLinkLabel.razor` must carry `@onpointerdown:stopPropagation="true"` and `@onmousedown:stopPropagation="true"` (except `<PortRenderer>`, which already stops propagation internally) — this is a hard, universal convention in this codebase to keep the diagram library's own node-drag/link-drag gestures from firing underneath UI controls.
- **Test strategy:** `Core` gets new xUnit `[Fact]`s in `tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs` (naming convention `<MethodName>_<Scenario>_<ExpectedBehavior>`, one `[Fact]` per scenario, asserting both on the raw output string and, where useful, round-tripping through `new FluentConfigParser().ParseRelationships(result, entities).Value`) because `Core` methods are genuinely new this phase — unlike Phases 3/4. The Web project has no test suite; verification there is `dotnet build`/`dotnet publish` plus an explicit, honest "not performed" note for interactive browser verification, matching every prior phase (this sandbox has no browser/Node/Playwright).
- **Out of scope for this phase** (deliberate, matching the original relationship-parsing spec's own exclusions): editing `OnDelete`/`UsingEntity`/join-entity-name, editing navigation-property names, composite (multi-property) foreign keys via the UI (the `Core` writer supports composite FKs; the UI only offers a single-property picker), and duplicate-relationship detection. These may be revisited in a future pass but are not required to consider Phase 5 "done."

## File Structure

- Modify `src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs` — add `SetRelationship`/`RemoveRelationship` plus private chain-building helpers.
- Modify `tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs` — new `[Fact]`s for both methods.
- Modify `src/EfSchemaVisualizer.Web/Diagram/DiagramEditor.cs` — add `AddRelationship`, `SetRelationshipShape`, `RemoveRelationship`.
- Modify `src/EfSchemaVisualizer.Web/Diagram/EntityNodeModel.cs` — add two ports (Left/Right) in the constructor.
- Create `src/EfSchemaVisualizer.Web/Diagram/RelationshipLinkLabelModel.cs` — `LinkLabelModel` subclass carrying the `RelationshipModel` a link represents.
- Create `src/EfSchemaVisualizer.Web/Diagram/RelationshipLinkLabel.razor` — click-to-expand Kind/FK editing + remove, registered as the component for `RelationshipLinkLabelModel`.
- Modify `src/EfSchemaVisualizer.Web/Diagram/DiagramSync.cs` — build `RelationshipLinkLabelModel` instead of a plain label.
- Modify `src/EfSchemaVisualizer.Web/Diagram/EntityNode.razor` — render the two ports as small draggable dots on the card.
- Modify `src/EfSchemaVisualizer.Web/Pages/Home.razor` — register `RelationshipLinkLabelModel`/`RelationshipLinkLabel`; wire `diagram.Links.Added`/`BaseLinkModel.TargetAttached` to call `DiagramEditor.AddRelationship` when a drag completes.
- Modify `docs/superpowers/specs/2026-07-14-editable-diagram-design.md` — record Phase 5's "Update"/"Verification" entry (final task).
- Modify `docs/backlog.md` — mark the editable-diagram item's Phase 5 line and the whole item as done (final task).

---

### Task 1: `OnModelCreatingRewriter.SetRelationship`/`RemoveRelationship`

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs`

**Interfaces:**
- Consumes: `FluentSyntaxHelpers.FindEntityConfigInvocations(CompilationUnitSyntax root, string entityName)`, `FluentSyntaxHelpers.FindCallsNamed(SyntaxNode scope, string methodName)` (both already `internal static` in `EfSchemaVisualizer.Core.Parsing`, visible from `EfSchemaVisualizer.Core.CodeGen` since it's the same assembly), the existing private `FindOnModelCreatingMethod(CompilationUnitSyntax root)` and `BuildEntityInvocationStatement(string modelBuilderParamName, string entityName, BlockSyntax block)` helpers already in this file.
- Produces: `public string SetRelationship(string sourceCode, RelationshipModel relationship)` and `public string RemoveRelationship(string sourceCode, RelationshipModel relationship)` — both consumed by `DiagramEditor` in Task 2.

- [ ] **Step 1: Add the `using` for the model namespace**

At the top of `src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs`, add:

```csharp
using EfSchemaVisualizer.Core.Model;
```

(alongside the existing `using EfSchemaVisualizer.Core.Parsing;` on line 4).

- [ ] **Step 2: Write the failing tests for `SetRelationship`**

Add to `tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs` (anywhere after the existing `SetKey`/`RemoveKey` test group is a good spot, matching the file's loose grouping-by-feature convention):

```csharp
private const string SourceWithNoRelationshipConfig = """
    public class AppDbContext : DbContext
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Blog>(entity =>
            {
                entity.HasKey(e => e.Id);
            });
            modelBuilder.Entity<Post>(entity =>
            {
                entity.HasKey(e => e.Id);
            });
        }
    }
    """;

private const string SourceWithNoEntityConfigAtAll = """
    public class AppDbContext : DbContext
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
        }
    }
    """;

[Fact]
public void SetRelationship_OneToMany_ExistingDependentBlock_AppendsChain()
{
    var relationship = new RelationshipModel("Blog", "Post", RelationshipKind.OneToMany, null, null);

    var result = new OnModelCreatingRewriter()
        .SetRelationship(SourceWithNoRelationshipConfig, relationship);

    Assert.Contains("entity.HasOne<Blog>().WithMany()", result);
    Assert.Contains("entity.HasKey(e => e.Id)", result);

    var entities = new List<EntityModel>
    {
        new("Blog", new List<PropertyModel> { new("Id", "int", false, null) }),
        new("Post", new List<PropertyModel> { new("Id", "int", false, null), new("BlogId", "int", false, null) }),
    };
    var configs = new FluentConfigParser().ParseRelationships(result, entities).Value;
    Assert.Contains(configs, c => c.PrincipalEntity == "Blog" && c.DependentEntity == "Post" && c.Kind == RelationshipKind.OneToMany);
}

[Fact]
public void SetRelationship_OneToMany_WithForeignKey_EmitsHasForeignKey()
{
    var relationship = new RelationshipModel(
        "Blog", "Post", RelationshipKind.OneToMany, null, null,
        ForeignKeyProperties: new List<string> { "BlogId" });

    var result = new OnModelCreatingRewriter()
        .SetRelationship(SourceWithNoRelationshipConfig, relationship);

    Assert.Contains("entity.HasOne<Blog>().WithMany().HasForeignKey(d => d.BlogId)", result);
}

[Fact]
public void SetRelationship_OneToMany_WithCompositeForeignKey_EmitsAnonymousObject()
{
    var relationship = new RelationshipModel(
        "Blog", "Post", RelationshipKind.OneToMany, null, null,
        ForeignKeyProperties: new List<string> { "BlogId", "TenantId" });

    var result = new OnModelCreatingRewriter()
        .SetRelationship(SourceWithNoRelationshipConfig, relationship);

    Assert.Contains("entity.HasOne<Blog>().WithMany().HasForeignKey(d => new { d.BlogId, d.TenantId })", result);
}

[Fact]
public void SetRelationship_OneToMany_WithNavigationNames_EmitsLambdas()
{
    var relationship = new RelationshipModel("Blog", "Post", RelationshipKind.OneToMany, "Posts", "Blog");

    var result = new OnModelCreatingRewriter()
        .SetRelationship(SourceWithNoRelationshipConfig, relationship);

    Assert.Contains("entity.HasOne<Blog>(x => x.Blog).WithMany(x => x.Posts)", result);
}

[Fact]
public void SetRelationship_OneToOne_EmitsGenericHasForeignKey()
{
    var relationship = new RelationshipModel(
        "Blog", "Post", RelationshipKind.OneToOne, null, null,
        ForeignKeyProperties: new List<string> { "BlogId" });

    var result = new OnModelCreatingRewriter()
        .SetRelationship(SourceWithNoRelationshipConfig, relationship);

    Assert.Contains("entity.HasOne<Blog>().WithOne().HasForeignKey<Post>(d => d.BlogId)", result);
}

[Fact]
public void SetRelationship_ManyToMany_InsertsIntoPrincipalScope()
{
    var relationship = new RelationshipModel("Blog", "Post", RelationshipKind.ManyToMany, null, null);

    var result = new OnModelCreatingRewriter()
        .SetRelationship(SourceWithNoRelationshipConfig, relationship);

    var blogBlockStart = result.IndexOf("modelBuilder.Entity<Blog>", StringComparison.Ordinal);
    var postBlockStart = result.IndexOf("modelBuilder.Entity<Post>", StringComparison.Ordinal);
    var hasManyIndex = result.IndexOf("entity.HasMany<Post>().WithMany()", StringComparison.Ordinal);

    Assert.True(hasManyIndex > blogBlockStart && hasManyIndex < postBlockStart);
}

[Fact]
public void SetRelationship_ManyToMany_WithJoinEntity_EmitsUsingEntity()
{
    var relationship = new RelationshipModel(
        "Blog", "Post", RelationshipKind.ManyToMany, null, null,
        JoinEntityName: "BlogPost");

    var result = new OnModelCreatingRewriter()
        .SetRelationship(SourceWithNoRelationshipConfig, relationship);

    Assert.Contains("entity.HasMany<Post>().WithMany().UsingEntity<BlogPost>()", result);
}

[Fact]
public void SetRelationship_WithOnDelete_EmitsOnDeleteCall()
{
    var relationship = new RelationshipModel(
        "Blog", "Post", RelationshipKind.OneToMany, null, null,
        OnDeleteBehavior: "Cascade");

    var result = new OnModelCreatingRewriter()
        .SetRelationship(SourceWithNoRelationshipConfig, relationship);

    Assert.Contains("entity.HasOne<Blog>().WithMany().OnDelete(DeleteBehavior.Cascade)", result);
}

[Fact]
public void SetRelationship_DependentEntityHasNoConfigBlockYet_InsertsWholeEntityBlock()
{
    var relationship = new RelationshipModel("Blog", "Post", RelationshipKind.OneToMany, null, null);

    var result = new OnModelCreatingRewriter()
        .SetRelationship(SourceWithNoEntityConfigAtAll, relationship);

    Assert.Contains("modelBuilder.Entity<Post>(entity =>", result);
    Assert.Contains("entity.HasOne<Blog>().WithMany()", result);
}
```

- [ ] **Step 3: Run the new tests to confirm they fail**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter "FullyQualifiedName~SetRelationship"`
Expected: compile error (`SetRelationship` does not exist yet) or, once the method stub compiles, `FAIL` on every assertion.

- [ ] **Step 4: Implement `SetRelationship` and its private helpers**

Insert the following as new public/private members of `OnModelCreatingRewriter` (a good insertion point is right after `AddEntity` and before `FindOnModelCreatingMethod`, i.e. after line 953 in the file as it stood before this task):

```csharp
public string SetRelationship(string sourceCode, RelationshipModel relationship)
{
    var tree = CSharpSyntaxTree.ParseText(sourceCode);
    var root = tree.GetCompilationUnitRoot();

    var scopeEntityName = relationship.Kind == RelationshipKind.ManyToMany
        ? relationship.PrincipalEntity
        : relationship.DependentEntity;

    var entityInvocations = FluentSyntaxHelpers.FindEntityConfigInvocations(root, scopeEntityName).ToList();
    var existingEntityInvocation = entityInvocations.FirstOrDefault();

    if (existingEntityInvocation is not null)
    {
        return InsertRelationshipStatement(root, existingEntityInvocation, relationship);
    }

    return InsertRelationshipEntityBlock(root, scopeEntityName, relationship);
}

private static string InsertRelationshipStatement(CompilationUnitSyntax root, InvocationExpressionSyntax entityInvocation, RelationshipModel relationship)
{
    var lambda = (SimpleLambdaExpressionSyntax)entityInvocation.ArgumentList.Arguments.Single().Expression;
    var block = lambda.Block!;
    var blockReceiverName = lambda.Parameter.Identifier.Text;

    var newStatement = BuildRelationshipStatement(blockReceiverName, relationship);
    var newBlock = block.AddStatements(newStatement);

    var newRoot = root.ReplaceNode(block, newBlock);
    return newRoot.NormalizeWhitespace().ToFullString();
}

private static string InsertRelationshipEntityBlock(CompilationUnitSyntax root, string scopeEntityName, RelationshipModel relationship)
{
    var method = FindOnModelCreatingMethod(root);
    var methodBody = method.Body
        ?? throw new InvalidOperationException("OnModelCreating has no method body.");
    var modelBuilderParamName = method.ParameterList.Parameters.Single().Identifier.Text;

    var statement = BuildRelationshipStatement("entity", relationship);
    var entityBlockStatement = BuildEntityInvocationStatement(modelBuilderParamName, scopeEntityName, SyntaxFactory.Block(statement));

    var newMethodBody = methodBody.AddStatements(entityBlockStatement);
    var newRoot = root.ReplaceNode(methodBody, newMethodBody);
    return newRoot.NormalizeWhitespace().ToFullString();
}

private static ExpressionStatementSyntax BuildRelationshipStatement(string blockReceiverName, RelationshipModel relationship)
{
    ExpressionSyntax chain = SyntaxFactory.IdentifierName(blockReceiverName);

    if (relationship.Kind == RelationshipKind.ManyToMany)
    {
        chain = BuildRelationshipCall(chain, "HasMany", relationship.DependentEntity, relationship.PrincipalNavigation);
        chain = BuildRelationshipCall(chain, "WithMany", targetEntityName: null, relationship.DependentNavigation);

        if (relationship.JoinEntityName is not null)
        {
            chain = BuildUsingEntityCall(chain, relationship.JoinEntityName);
        }

        return SyntaxFactory.ExpressionStatement(chain);
    }

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
}

private static InvocationExpressionSyntax BuildRelationshipCall(ExpressionSyntax receiver, string methodName, string? targetEntityName, string? navPropertyName)
{
    SimpleNameSyntax methodIdentifier = targetEntityName is null
        ? SyntaxFactory.IdentifierName(methodName)
        : SyntaxFactory.GenericName(SyntaxFactory.Identifier(methodName))
            .WithTypeArgumentList(SyntaxFactory.TypeArgumentList(
                SyntaxFactory.SingletonSeparatedList<TypeSyntax>(SyntaxFactory.IdentifierName(targetEntityName))));

    var argumentList = navPropertyName is null
        ? SyntaxFactory.ArgumentList()
        : SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(
            SyntaxFactory.Argument(
                SyntaxFactory.SimpleLambdaExpression(
                    SyntaxFactory.Parameter(SyntaxFactory.Identifier("x")),
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("x"),
                        SyntaxFactory.IdentifierName(navPropertyName))))));

    return SyntaxFactory.InvocationExpression(
        SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, receiver, methodIdentifier),
        argumentList);
}

private static ExpressionSyntax AppendHasForeignKey(ExpressionSyntax chain, IReadOnlyList<string> foreignKeyProperties, string? dependentGeneric)
{
    if (foreignKeyProperties.Count == 0)
    {
        return chain;
    }

    SimpleNameSyntax methodIdentifier = dependentGeneric is null
        ? SyntaxFactory.IdentifierName("HasForeignKey")
        : SyntaxFactory.GenericName(SyntaxFactory.Identifier("HasForeignKey"))
            .WithTypeArgumentList(SyntaxFactory.TypeArgumentList(
                SyntaxFactory.SingletonSeparatedList<TypeSyntax>(SyntaxFactory.IdentifierName(dependentGeneric))));

    const string lambdaParam = "d";
    ExpressionSyntax body = foreignKeyProperties.Count == 1
        ? SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression,
            SyntaxFactory.IdentifierName(lambdaParam),
            SyntaxFactory.IdentifierName(foreignKeyProperties[0]))
        : SyntaxFactory.AnonymousObjectCreationExpression(
            SyntaxFactory.SeparatedList(foreignKeyProperties.Select(name =>
                SyntaxFactory.AnonymousObjectMemberDeclarator(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName(lambdaParam),
                        SyntaxFactory.IdentifierName(name))))));

    var argumentList = SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(
        SyntaxFactory.Argument(
            SyntaxFactory.SimpleLambdaExpression(SyntaxFactory.Parameter(SyntaxFactory.Identifier(lambdaParam)), body))));

    return SyntaxFactory.InvocationExpression(
        SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, chain, methodIdentifier),
        argumentList);
}

private static ExpressionSyntax AppendOnDelete(ExpressionSyntax chain, string? onDeleteBehavior)
{
    if (onDeleteBehavior is null)
    {
        return chain;
    }

    var argument = SyntaxFactory.Argument(
        SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression,
            SyntaxFactory.IdentifierName("DeleteBehavior"),
            SyntaxFactory.IdentifierName(onDeleteBehavior)));

    return SyntaxFactory.InvocationExpression(
        SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, chain, SyntaxFactory.IdentifierName("OnDelete")),
        SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(argument)));
}

private static ExpressionSyntax BuildUsingEntityCall(ExpressionSyntax chain, string joinEntityName)
{
    var methodIdentifier = SyntaxFactory.GenericName(SyntaxFactory.Identifier("UsingEntity"))
        .WithTypeArgumentList(SyntaxFactory.TypeArgumentList(
            SyntaxFactory.SingletonSeparatedList<TypeSyntax>(SyntaxFactory.IdentifierName(joinEntityName))));

    return SyntaxFactory.InvocationExpression(
        SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, chain, methodIdentifier),
        SyntaxFactory.ArgumentList());
}
```

- [ ] **Step 5: Run the tests again to confirm they pass**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter "FullyQualifiedName~SetRelationship"`
Expected: all `SetRelationship_*` tests `PASS`.

- [ ] **Step 6: Write the failing tests for `RemoveRelationship`**

Add to the same test file:

```csharp
private const string SourceWithOneToManyRelationship = """
    public class AppDbContext : DbContext
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Blog>(entity =>
            {
                entity.HasKey(e => e.Id);
            });
            modelBuilder.Entity<Post>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.HasOne<Blog>().WithMany().HasForeignKey(d => d.BlogId);
            });
        }
    }
    """;

private const string SourceWithManyToManyRelationship = """
    public class AppDbContext : DbContext
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Blog>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.HasMany<Post>().WithMany();
            });
            modelBuilder.Entity<Post>(entity =>
            {
                entity.HasKey(e => e.Id);
            });
        }
    }
    """;

[Fact]
public void RemoveRelationship_OneToMany_RemovesWholeStatementFromDependentScope()
{
    var relationship = new RelationshipModel(
        "Blog", "Post", RelationshipKind.OneToMany, null, null,
        ForeignKeyProperties: new List<string> { "BlogId" });

    var result = new OnModelCreatingRewriter()
        .RemoveRelationship(SourceWithOneToManyRelationship, relationship);

    Assert.DoesNotContain("HasOne<Blog>()", result);
    Assert.Contains("entity.HasKey(e => e.Id)", result);
}

[Fact]
public void RemoveRelationship_ManyToMany_RemovesWholeStatementFromPrincipalScope()
{
    var relationship = new RelationshipModel("Blog", "Post", RelationshipKind.ManyToMany, null, null);

    var result = new OnModelCreatingRewriter()
        .RemoveRelationship(SourceWithManyToManyRelationship, relationship);

    Assert.DoesNotContain("HasMany<Post>()", result);
}

[Fact]
public void RemoveRelationship_NoMatch_ReturnsSourceUnchanged()
{
    var relationship = new RelationshipModel("Blog", "Comment", RelationshipKind.OneToMany, null, null);

    var result = new OnModelCreatingRewriter()
        .RemoveRelationship(SourceWithOneToManyRelationship, relationship);

    Assert.Equal(SourceWithOneToManyRelationship, result);
}
```

- [ ] **Step 7: Run the new tests to confirm they fail**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter "FullyQualifiedName~RemoveRelationship"`
Expected: compile error or `FAIL`.

- [ ] **Step 8: Implement `RemoveRelationship`**

Add to `OnModelCreatingRewriter`, after the `SetRelationship`-related members added in Step 4:

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

    var entityInvocations = FluentSyntaxHelpers.FindEntityConfigInvocations(root, scopeEntityName).ToList();

    var matchingCall = entityInvocations
        .SelectMany(entityInvocation => FluentSyntaxHelpers.FindCallsNamed(entityInvocation, methodName))
        .FirstOrDefault(call => HasGenericTypeArgument(call, otherEntityName));

    if (matchingCall is null
        || matchingCall.Ancestors().OfType<ExpressionStatementSyntax>().FirstOrDefault() is not { } statement)
    {
        return sourceCode;
    }

    var newRoot = root.RemoveNode(statement, SyntaxRemoveOptions.KeepNoTrivia)!;
    return newRoot.NormalizeWhitespace().ToFullString();
}

private static bool HasGenericTypeArgument(InvocationExpressionSyntax call, string typeName)
{
    return call.Expression is MemberAccessExpressionSyntax { Name: GenericNameSyntax generic }
        && generic.TypeArgumentList.Arguments.Count == 1
        && generic.TypeArgumentList.Arguments[0] is IdentifierNameSyntax { Identifier.Text: var text }
        && text == typeName;
}
```

- [ ] **Step 9: Run the tests again to confirm they pass**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter "FullyQualifiedName~RemoveRelationship"`
Expected: all `RemoveRelationship_*` tests `PASS`.

- [ ] **Step 10: Run the full `Core` test suite**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests`
Expected: all tests pass (this file already has 277+ tests before this phase's additions).

- [ ] **Step 11: Commit**

```bash
git add src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs
git commit -m "Add OnModelCreatingRewriter.SetRelationship/RemoveRelationship for editable-diagram Phase 5"
```

---

### Task 2: `DiagramEditor.AddRelationship`/`SetRelationshipShape`/`RemoveRelationship`

**Files:**
- Modify: `src/EfSchemaVisualizer.Web/Diagram/DiagramEditor.cs`

**Interfaces:**
- Consumes: `OnModelCreatingRewriter.SetRelationship(string, RelationshipModel)` / `RemoveRelationship(string, RelationshipModel)` (Task 1), `Current.Entities` (`IReadOnlyList<EntityModel>`), `Current.Relationships` (`IReadOnlyList<RelationshipModel>`), the private `Apply(string, string)` funnel.
- Produces: `DiagramEditResult AddRelationship(string dependentEntityName, string principalEntityName)`, `DiagramEditResult SetRelationshipShape(RelationshipModel relationship, RelationshipKind newKind, IReadOnlyList<string> newForeignKeyProperties)`, `DiagramEditResult RemoveRelationship(RelationshipModel relationship)` — all three consumed by Task 5 (Home.razor) and Task 4 (`RelationshipLinkLabel.razor`).

- [ ] **Step 1: Add the three methods**

Insert into `src/EfSchemaVisualizer.Web/Diagram/DiagramEditor.cs`, immediately after `SetDefaultValue` (currently ending at line 557, right before `private static string GenerateUniquePropertyName`):

```csharp
public DiagramEditResult AddRelationship(string dependentEntityName, string principalEntityName)
{
    var dependent = Current.Entities.FirstOrDefault(e => e.Name == dependentEntityName);
    if (dependent is null)
    {
        return DiagramEditResult.Fail($"Entity '{dependentEntityName}' not found.");
    }

    var principal = Current.Entities.FirstOrDefault(e => e.Name == principalEntityName);
    if (principal is null)
    {
        return DiagramEditResult.Fail($"Entity '{principalEntityName}' not found.");
    }

    var relationship = new RelationshipModel(principalEntityName, dependentEntityName, RelationshipKind.OneToMany, null, null);

    var newConfigSource = _configRewriter.SetRelationship(ConfigSource, relationship);
    Apply(ClassSource, newConfigSource);
    return DiagramEditResult.Ok();
}

public DiagramEditResult SetRelationshipShape(RelationshipModel relationship, RelationshipKind newKind, IReadOnlyList<string> newForeignKeyProperties)
{
    if (!Current.Relationships.Contains(relationship))
    {
        return DiagramEditResult.Fail("Relationship no longer exists.");
    }

    if (newKind == relationship.Kind && newForeignKeyProperties.SequenceEqual(relationship.ForeignKeyProperties))
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

    var updated = relationship with { Kind = newKind, ForeignKeyProperties = newForeignKeyProperties };

    var withoutOld = _configRewriter.RemoveRelationship(ConfigSource, relationship);
    var withNew = _configRewriter.SetRelationship(withoutOld, updated);
    Apply(ClassSource, withNew);
    return DiagramEditResult.Ok();
}

public DiagramEditResult RemoveRelationship(RelationshipModel relationship)
{
    if (!Current.Relationships.Contains(relationship))
    {
        return DiagramEditResult.Fail("Relationship no longer exists.");
    }

    var newConfigSource = _configRewriter.RemoveRelationship(ConfigSource, relationship);
    Apply(ClassSource, newConfigSource);
    return DiagramEditResult.Ok();
}
```

- [ ] **Step 2: Build to confirm it compiles**

Run: `dotnet build src/EfSchemaVisualizer.Web/EfSchemaVisualizer.Web.csproj`
Expected: `Build succeeded.`, 0 errors.

- [ ] **Step 3: Commit**

```bash
git add src/EfSchemaVisualizer.Web/Diagram/DiagramEditor.cs
git commit -m "Add DiagramEditor.AddRelationship/SetRelationshipShape/RemoveRelationship for Phase 5"
```

---

### Task 3: Ports on `EntityNodeModel` + rendering in `EntityNode.razor`

**Files:**
- Modify: `src/EfSchemaVisualizer.Web/Diagram/EntityNodeModel.cs`
- Modify: `src/EfSchemaVisualizer.Web/Diagram/EntityNode.razor`

**Interfaces:**
- Consumes: `Blazor.Diagrams.Core.Models.PortAlignment`, `NodeModel.AddPort(PortAlignment)`, `NodeModel.GetPort(PortAlignment)`, `Blazor.Diagrams.Components.Renderers.PortRenderer` (parameters `Port`, `Class`, `Style`).
- Produces: every `EntityNodeModel` now exposes a `Left` and a `Right` port (via the inherited `Ports`/`GetPort` members) that Task 5's drag-to-connect wiring and the library's own `DragNewLinkBehavior` rely on.

- [ ] **Step 1: Add two ports in `EntityNodeModel`'s constructor**

In `src/EfSchemaVisualizer.Web/Diagram/EntityNodeModel.cs`, change:

```csharp
    public EntityNodeModel(EntityModel entity, Guid entityId, Point position) : base(position)
    {
        Entity = entity;
        EntityId = entityId;
        Title = entity.Name;
    }
```

to:

```csharp
    public EntityNodeModel(EntityModel entity, Guid entityId, Point position) : base(position)
    {
        Entity = entity;
        EntityId = entityId;
        Title = entity.Name;
        AddPort(PortAlignment.Left);
        AddPort(PortAlignment.Right);
    }
```

(`PortAlignment` is in `Blazor.Diagrams.Core.Models`, already imported by this file's existing `using Blazor.Diagrams.Core.Models;`.)

- [ ] **Step 2: Render the ports as draggable dots in `EntityNode.razor`**

In `src/EfSchemaVisualizer.Web/Diagram/EntityNode.razor`, add two new `@using` lines after the existing ones (after line 5):

```razor
@using Blazor.Diagrams.Core.Models
@using Blazor.Diagrams.Components.Renderers
```

Change the outer card `<div>` (line 7) from:

```razor
<div class="card" style="width: 260px; border: 1px solid #444;">
```

to:

```razor
<div class="card" style="width: 260px; border: 1px solid #444; position: relative;">
    <PortRenderer Port="@Node.GetPort(PortAlignment.Left)!" Class="entity-port"
                  Style="position: absolute; left: -6px; top: 20px; width: 12px; height: 12px; border-radius: 50%; background: #666; cursor: crosshair;">
    </PortRenderer>
    <PortRenderer Port="@Node.GetPort(PortAlignment.Right)!" Class="entity-port"
                  Style="position: absolute; right: -6px; top: 20px; width: 12px; height: 12px; border-radius: 50%; background: #666; cursor: crosshair;">
    </PortRenderer>
```

Immediately after (still inside the card, before the existing `<div class="card-header" ...>`), add a one-line hint so the drop-precision constraint (see Global Constraints) is discoverable:

```razor
    <div style="font-size: 0.65em; color: #888; padding: 2px 8px 0;">Drag a dot to another entity's dot to create a relationship.</div>
```

- [ ] **Step 3: Build to confirm it compiles**

Run: `dotnet build src/EfSchemaVisualizer.Web/EfSchemaVisualizer.Web.csproj`
Expected: `Build succeeded.`, 0 errors.

- [ ] **Step 4: Commit**

```bash
git add src/EfSchemaVisualizer.Web/Diagram/EntityNodeModel.cs src/EfSchemaVisualizer.Web/Diagram/EntityNode.razor
git commit -m "Add drag-to-connect ports to entity nodes for editable-diagram Phase 5"
```

---

### Task 4: `RelationshipLinkLabelModel` + `RelationshipLinkLabel.razor` (click-to-expand editing)

**Files:**
- Create: `src/EfSchemaVisualizer.Web/Diagram/RelationshipLinkLabelModel.cs`
- Create: `src/EfSchemaVisualizer.Web/Diagram/RelationshipLinkLabel.razor`
- Modify: `src/EfSchemaVisualizer.Web/Diagram/DiagramSync.cs`

**Interfaces:**
- Consumes: `RelationshipLabels.For(RelationshipKind)` (existing), `DiagramEditContext.Editor.SetRelationshipShape`/`RemoveRelationship` (Task 2), `DiagramEditContext.NotifyChangedAsync`.
- Produces: `RelationshipLinkLabelModel` (a `LinkLabelModel` subclass exposing `Relationship`), consumed by `DiagramSync.Rebuild` (this task) and registered against `RelationshipLinkLabel` in Task 5 (`Home.razor`).

- [ ] **Step 1: Create `RelationshipLinkLabelModel.cs`**

```csharp
using Blazor.Diagrams.Core.Models;
using Blazor.Diagrams.Core.Models.Base;
using EfSchemaVisualizer.Core.Model;

namespace EfSchemaVisualizer.Web.Diagram;

public sealed class RelationshipLinkLabelModel : LinkLabelModel
{
    public RelationshipLinkLabelModel(BaseLinkModel parent, RelationshipModel relationship)
        : base(parent, RelationshipLabels.For(relationship.Kind))
    {
        Relationship = relationship;
    }

    public RelationshipModel Relationship { get; }
}
```

- [ ] **Step 2: Update `DiagramSync.Rebuild` to attach a `RelationshipLinkLabelModel` instead of a plain label**

In `src/EfSchemaVisualizer.Web/Diagram/DiagramSync.cs`, change:

```csharp
            var link = new LinkModel(dependentNode, principalNode);
            link.AddLabel(RelationshipLabels.For(relationship.Kind));
            diagram.Links.Add(link);
```

to:

```csharp
            var link = new LinkModel(dependentNode, principalNode);
            link.Labels.Add(new RelationshipLinkLabelModel(link, relationship));
            diagram.Links.Add(link);
```

- [ ] **Step 3: Create `RelationshipLinkLabel.razor`**

```razor
@namespace EfSchemaVisualizer.Web.Diagram
@using Microsoft.AspNetCore.Components
@using Microsoft.AspNetCore.Components.Web
@using System.Linq
@using EfSchemaVisualizer.Core.Model

<div style="background: white; border: 1px solid #999; border-radius: 3px; padding: 1px 5px; font-size: 0.75em; cursor: pointer; white-space: nowrap;"
     @onclick="ToggleExpand"
     @onpointerdown:stopPropagation="true"
     @onmousedown:stopPropagation="true">
    @RelationshipLabels.For(Label.Relationship.Kind)
</div>
@if (_expanded)
{
    <div style="position: absolute; background: white; border: 1px solid #999; border-radius: 3px; padding: 6px; font-size: 0.75em; white-space: nowrap; z-index: 1000;"
         @onpointerdown:stopPropagation="true"
         @onmousedown:stopPropagation="true">
        <label style="display: block;">
            Kind:
            <select value="@_kind" @onchange="e => CommitKind(e.Value?.ToString())">
                <option value="OneToMany">One-to-many</option>
                <option value="OneToOne">One-to-one</option>
                <option value="ManyToMany">Many-to-many</option>
            </select>
        </label>
        @if (_kind != RelationshipKind.ManyToMany)
        {
            <label style="display: block;">
                Foreign key:
                <select value="@_foreignKeyProperty" @onchange="e => CommitForeignKey(e.Value?.ToString())">
                    <option value="">(none — shadow FK)</option>
                    @foreach (var property in DependentProperties)
                    {
                        <option value="@property.Name">@property.Name</option>
                    }
                </select>
            </label>
        }
        @if (_error is not null)
        {
            <div style="color: red;">@_error</div>
        }
        <button type="button" @onclick="Remove">Remove relationship</button>
    </div>
}

@code {
    [Parameter]
    public RelationshipLinkLabelModel Label { get; set; } = null!;

    [CascadingParameter]
    public DiagramEditContext EditContext { get; set; } = null!;

    private bool _expanded;
    private RelationshipKind _kind;
    private string _foreignKeyProperty = string.Empty;
    private string? _error;

    private IEnumerable<PropertyModel> DependentProperties =>
        EditContext.Editor.Current.Entities.FirstOrDefault(e => e.Name == Label.Relationship.DependentEntity)?.Properties
            ?? Enumerable.Empty<PropertyModel>();

    private void ToggleExpand()
    {
        _expanded = !_expanded;
        if (_expanded)
        {
            _kind = Label.Relationship.Kind;
            _foreignKeyProperty = Label.Relationship.ForeignKeyProperties.FirstOrDefault() ?? string.Empty;
            _error = null;
        }
    }

    private async Task CommitKind(string? newKind)
    {
        if (newKind is null || !Enum.TryParse<RelationshipKind>(newKind, out var kind))
        {
            return;
        }

        _kind = kind;
        await Commit();
    }

    private async Task CommitForeignKey(string? propertyName)
    {
        _foreignKeyProperty = propertyName ?? string.Empty;
        await Commit();
    }

    private async Task Commit()
    {
        var foreignKeyProperties = _kind == RelationshipKind.ManyToMany || string.IsNullOrEmpty(_foreignKeyProperty)
            ? Array.Empty<string>()
            : new[] { _foreignKeyProperty };

        var result = EditContext.Editor.SetRelationshipShape(Label.Relationship, _kind, foreignKeyProperties);
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

    private async Task Remove()
    {
        var result = EditContext.Editor.RemoveRelationship(Label.Relationship);
        if (result.Success)
        {
            await EditContext.NotifyChangedAsync();
        }
        else
        {
            _error = result.Error;
        }
    }
}
```

- [ ] **Step 4: Build to confirm it compiles**

Run: `dotnet build src/EfSchemaVisualizer.Web/EfSchemaVisualizer.Web.csproj`
Expected: `Build succeeded.`, 0 errors.

- [ ] **Step 5: Commit**

```bash
git add src/EfSchemaVisualizer.Web/Diagram/RelationshipLinkLabelModel.cs src/EfSchemaVisualizer.Web/Diagram/RelationshipLinkLabel.razor src/EfSchemaVisualizer.Web/Diagram/DiagramSync.cs
git commit -m "Add click-to-expand relationship editing (Kind/foreign key) for editable-diagram Phase 5"
```

---

### Task 5: Wire drag-to-connect in `Home.razor`

**Files:**
- Modify: `src/EfSchemaVisualizer.Web/Pages/Home.razor`

**Interfaces:**
- Consumes: `DiagramEditor.AddRelationship(string, string)` (Task 2), `RelationshipLinkLabelModel`/`RelationshipLinkLabel` (Task 4), `Blazor.Diagrams.Core.Models.Base.BaseLinkModel.TargetAttached` event, `Blazor.Diagrams.Core.Anchors.Anchor.Model`, `Blazor.Diagrams.Core.Models.PortModel.Parent`.
- Produces: nothing consumed by later tasks — this is the last wiring point.

- [ ] **Step 1: Add the new `@using`s**

At the top of `src/EfSchemaVisualizer.Web/Pages/Home.razor`, add after the existing `@using Blazor.Diagrams.Options` (line 9):

```razor
@using Blazor.Diagrams.Core.Anchors
@using Blazor.Diagrams.Core.Models.Base
```

- [ ] **Step 2: Register the new label component and subscribe to link-added events**

In the `RenderDiagram` method, change:

```csharp
            var diagram = new BlazorDiagram(new BlazorDiagramOptions
            {
                AllowMultiSelection = true,
            });
            diagram.RegisterComponent<EntityNodeModel, EntityNode>();

            DiagramSync.Rebuild(diagram, _editor.Current, _editor.EntityIds);
```

to:

```csharp
            var diagram = new BlazorDiagram(new BlazorDiagramOptions
            {
                AllowMultiSelection = true,
            });
            diagram.RegisterComponent<EntityNodeModel, EntityNode>();
            diagram.RegisterComponent<RelationshipLinkLabelModel, RelationshipLinkLabel>();
            diagram.Links.Added += OnLinkAdded;

            DiagramSync.Rebuild(diagram, _editor.Current, _editor.EntityIds);
```

- [ ] **Step 3: Add the drag-to-connect event handlers**

Add these methods to the `@code` block, right after `RenderDiagram`:

```csharp
    private void OnLinkAdded(BaseLinkModel link)
    {
        link.TargetAttached += OnRelationshipLinkAttached;
    }

    private void OnRelationshipLinkAttached(BaseLinkModel link)
    {
        link.TargetAttached -= OnRelationshipLinkAttached;

        if (_editor is null || _diagram is null)
        {
            return;
        }

        var dependentEntityName = ResolveEntityName(link.Source);
        var principalEntityName = ResolveEntityName(link.Target);

        if (dependentEntityName is null || principalEntityName is null)
        {
            _diagram.Links.Remove(link);
            return;
        }

        var result = _editor.AddRelationship(dependentEntityName, principalEntityName);
        if (!result.Success)
        {
            _error = result.Error;
            _diagram.Links.Remove(link);
            InvokeAsync(StateHasChanged);
            return;
        }

        InvokeAsync(async () => await OnDiagramEditedAsync());
    }

    private static string? ResolveEntityName(Anchor anchor) => anchor.Model switch
    {
        EntityNodeModel node => node.Entity.Name,
        Blazor.Diagrams.Core.Models.PortModel { Parent: EntityNodeModel node } => node.Entity.Name,
        _ => null,
    };
```

(`RelationshipLinkLabelModel.Relationship` is only ever read by `RelationshipLinkLabel.razor` — the just-created raw `LinkModel` created by the drag gesture has no labels at all yet; the label gets attached the moment `DiagramSync.Rebuild` runs inside `OnDiagramEditedAsync`, which fully replaces this link with a fresh one built from `Current.Relationships`. This is why `OnRelationshipLinkAttached` only needs to resolve entity names and call `AddRelationship` — it never touches labels itself.)

- [ ] **Step 4: Build to confirm it compiles**

Run: `dotnet build src/EfSchemaVisualizer.Web/EfSchemaVisualizer.Web.csproj`
Expected: `Build succeeded.`, 0 errors.

- [ ] **Step 5: Commit**

```bash
git add src/EfSchemaVisualizer.Web/Pages/Home.razor
git commit -m "Wire drag-to-connect relationship creation for editable-diagram Phase 5"
```

---

### Task 6: Full-solution verification and design-doc/backlog update

**Files:**
- Modify: `docs/superpowers/specs/2026-07-14-editable-diagram-design.md`
- Modify: `docs/backlog.md`

- [ ] **Step 1: Run the full `Core` test suite**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests`
Expected: all tests pass (baseline was 277 before this phase; expect 277 + the ~13 new `[Fact]`s from Task 1).

- [ ] **Step 2: Build the whole solution**

Run: `dotnet build`
Expected: `Build succeeded.`, 0 warnings, 0 errors for both `EfSchemaVisualizer.Core` and `EfSchemaVisualizer.Web`.

- [ ] **Step 3: Publish the Web project**

Run: `dotnet publish src/EfSchemaVisualizer.Web/EfSchemaVisualizer.Web.csproj -c Release`
Expected: succeeds, producing a working `wwwroot` output.

- [ ] **Step 4: Attempt interactive browser verification**

Run: `which chromium chromium-browser google-chrome firefox node npx playwright`

If any of these are available, serve the published `wwwroot` locally and manually run through:
1. Render the default sample diagram (`Blog`/`Post`, already related via `HasOne(e => e.Blog).WithMany().HasForeignKey(e => e.BlogId)`) — confirm the existing relationship line renders with a clickable "1—*" label.
2. Click the label, confirm the Kind/foreign-key panel expands; change Kind to "One-to-one", confirm the source regenerates with `HasOne<Blog>().WithOne().HasForeignKey<Post>(...)`.
3. Click "Remove relationship", confirm the `HasOne`/`HasForeignKey` chain disappears from the config textarea entirely.
4. Add a new entity via "+ Entity", drag from one entity's port dot to another entity's port dot, confirm a new default one-to-many relationship (`HasOne<X>().WithMany()`) appears in the config source and a "1—*" label renders on the new link.
5. Attempt to drop a drag gesture on empty canvas and confirm the in-progress link disappears (no relationship created, no error).

If none of these tools are available (expected, matching every prior phase in this sandbox), record that explicitly rather than claiming verification occurred.

- [ ] **Step 5: Update the design spec's Sequencing section**

In `docs/superpowers/specs/2026-07-14-editable-diagram-design.md`, replace the line:

```
      - **Phase 5 (relationships) — not started.**
```

(from the most recent edit to this file) with an "Update"/"Verification" entry matching the style of the Phase 3/Phase 4 entries already in that file — state what was built (the two new `Core` methods, the three new `DiagramEditor` methods, the port-based drag-to-connect UI, the click-to-expand Kind/FK/remove UI), the exact `dotnet test`/`dotnet build`/`dotnet publish` results from Steps 1–3, and an honest "Interactive browser verification... was **not performed**" paragraph (or, if Step 4's tools were actually available, the real results) — consistent with every phase before it. Also add a closing note that this completes all five phases of the editable-diagram spec.

- [ ] **Step 6: Update `docs/backlog.md`**

Replace the line:

```
      - **Phase 5 (relationships) — not started.** Builds
        `SetRelationship`/`RemoveRelationship` in `Core` first, then wires
        drag-to-connect (default one-to-many) and click-to-expand
        link-label editing (kind, FK property) in the diagram. This is the
        last phase of the editable-diagram slice.
```

with an "— done." entry in the same style as the Phase 3/Phase 4 entries immediately above it in that file, summarizing what was built and pointing at this plan file (`2026-07-14-editable-diagram-phase5-relationships.md`). Also change the parent bullet's status (currently `- [ ]` for "Editable diagram wired to the rewriter") to `- [x]`, since all five phases are now complete.

- [ ] **Step 7: Commit**

```bash
git add docs/superpowers/specs/2026-07-14-editable-diagram-design.md docs/backlog.md
git commit -m "Record Phase 5 (relationships) verification results and complete the editable-diagram backlog item"
```

## Self-Review

**Spec coverage:**
- "Builds `SetRelationship`/`RemoveRelationship` in Core" → Task 1.
- "wires drag-to-connect (default one-to-many)" → Tasks 2, 3, 5 (`AddRelationship` always creates `RelationshipKind.OneToMany`, per the design spec's own words).
- "click-to-expand link-label editing (kind, FK property)" → Task 4.
- Design doc's "Known gap across all merged phases" (no browser available) → Task 6, Step 4, handled the same honest way as every prior phase.

**Placeholder scan:** No "TBD"/"TODO"/"implement later" language anywhere above; every step has literal, complete code, not a description of code.

**Type consistency:** `RelationshipModel`/`RelationshipKind` used identically across Task 1 (`Core`), Task 2 (`DiagramEditor`), and Task 4 (`RelationshipLinkLabel.razor`) — same field names (`PrincipalEntity`, `DependentEntity`, `Kind`, `ForeignKeyProperties`) throughout. `DiagramEditor.AddRelationship(string dependentEntityName, string principalEntityName)` parameter order matches exactly how Task 5's `ResolveEntityName(link.Source)`/`ResolveEntityName(link.Target)` are passed (source = dependent, target = principal), consistent with the design's "drag from the dependent (many/child) side to the principal (one/parent) side" default-Kind convention.
