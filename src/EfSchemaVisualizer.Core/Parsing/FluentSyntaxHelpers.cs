using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("EfSchemaVisualizer.Core.Tests")]

namespace EfSchemaVisualizer.Core.Parsing;

internal static class FluentSyntaxHelpers
{
    /// Finds every invocation named `methodName` within the given scope, e.g. all `HasMaxLength(...)` calls.
    /// A nested `Entity&lt;...&gt;(...)` invocation (regardless of receiver) is treated as an opaque boundary: its
    /// subtree is not descended into, so calls belonging to a nested entity's configuration are never
    /// misattributed to the outer scope's entity.
    public static IEnumerable<InvocationExpressionSyntax> FindCallsNamed(SyntaxNode scope, string methodName)
    {
        return FindAllCalls(scope).Where(invocation =>
            invocation.Expression is MemberAccessExpressionSyntax { Name.Identifier.Text: var name } && name == methodName);
    }

    /// Finds every invocation within the given scope, respecting the same nested-`Entity&lt;T&gt;()`
    /// opaque boundary as <see cref="FindCallsNamed"/>.
    private static IEnumerable<InvocationExpressionSyntax> FindAllCalls(SyntaxNode scope)
    {
        var results = new List<InvocationExpressionSyntax>();
        Walk(scope);
        return results;

        void Walk(SyntaxNode node)
        {
            foreach (var child in node.ChildNodes())
            {
                if (child is InvocationExpressionSyntax nestedEntityInvocation
                    && GetConfiguredEntityName(nestedEntityInvocation) is not null)
                {
                    // Opaque boundary: don't descend into a nested Entity<> configuration's subtree.
                    continue;
                }

                if (child is InvocationExpressionSyntax invocation)
                {
                    results.Add(invocation);
                }

                Walk(child);
            }
        }
    }

    /// Finds every invocation forming a fluent config chain within `scope` — e.g. every link in
    /// `entity.Property(e => e.Name).HasMaxLength(100).IsRequired();` — without descending into
    /// argument expressions (lambda bodies, helper-method arguments), where an unrelated invocation
    /// could otherwise be mistaken for a chain link. Also includes any call chained directly onto
    /// `scope` itself when `scope` is the bare `Entity&lt;T&gt;()` invocation (e.g. the
    /// `.HasOne(...).WithMany(...)` style with no lambda block). A chain rooted at a nested
    /// `Entity&lt;T&gt;()` call is skipped (opaque boundary), since it belongs to a different entity's
    /// own scope.
    internal static IEnumerable<InvocationExpressionSyntax> FindConfigChainCalls(SyntaxNode scope)
    {
        var results = new List<InvocationExpressionSyntax>();

        foreach (var rootInvocation in FindStatementRootInvocations(scope))
        {
            WalkChainDown(rootInvocation);
        }

        if (scope is InvocationExpressionSyntax scopeInvocation)
        {
            WalkChainedTail(scopeInvocation, results.Add);
        }

        return results;

        void WalkChainDown(InvocationExpressionSyntax invocation)
        {
            if (GetConfiguredEntityName(invocation) is not null)
            {
                return;
            }

            var current = invocation;
            while (true)
            {
                results.Add(current);

                if (current.Expression is MemberAccessExpressionSyntax { Expression: InvocationExpressionSyntax inner })
                {
                    current = inner;
                }
                else
                {
                    break;
                }
            }
        }
    }

    private static IEnumerable<InvocationExpressionSyntax> FindStatementRootInvocations(SyntaxNode scope)
    {
        switch (scope)
        {
            case MethodDeclarationSyntax method:
                if (method.Body is not null)
                {
                    foreach (var statement in method.Body.Statements.OfType<ExpressionStatementSyntax>())
                    {
                        if (statement.Expression is InvocationExpressionSyntax invocation)
                        {
                            yield return invocation;
                        }
                    }
                }
                else if (method.ExpressionBody?.Expression is InvocationExpressionSyntax bodyInvocation)
                {
                    yield return bodyInvocation;
                }

                break;

            case InvocationExpressionSyntax entityInvocation:
                var lambda = entityInvocation.ArgumentList.Arguments
                    .Select(a => a.Expression)
                    .OfType<AnonymousFunctionExpressionSyntax>()
                    .FirstOrDefault();

                if (lambda?.Block is not null)
                {
                    foreach (var statement in lambda.Block.Statements.OfType<ExpressionStatementSyntax>())
                    {
                        if (statement.Expression is InvocationExpressionSyntax invocation)
                        {
                            yield return invocation;
                        }
                    }
                }
                else if (lambda?.ExpressionBody is InvocationExpressionSyntax lambdaBodyInvocation)
                {
                    yield return lambdaBodyInvocation;
                }

                break;
        }
    }

    /// Walks every invocation chained directly onto `invocation` via `.methodName(...)` links
    /// (e.g. given the `WithMany(...)` invocation, visits `.HasForeignKey(...)`, then `.OnDelete(...)`
    /// chained after it), stopping at the end of the statement.
    internal static void WalkChainedTail(InvocationExpressionSyntax invocation, Action<InvocationExpressionSyntax> visit)
    {
        SyntaxNode? cursor = invocation.Parent;

        while (cursor is not null && cursor is not StatementSyntax)
        {
            if (cursor is MemberAccessExpressionSyntax && cursor.Parent is InvocationExpressionSyntax chained)
            {
                visit(chained);
            }

            cursor = cursor.Parent;
        }
    }

    /// Given a fluent call like `entity.Property(e => e.Name).HasMaxLength(100)`, returns "Name".
    /// Also resolves the string overload `entity.Property("Name")`, a block-bodied lambda with
    /// a single `return e.Name;` statement, and any number of other fluent calls chained between
    /// `Property(...)` and `fluentCall` itself (e.g. `entity.Property(e => e.Name).IsRequired().HasMaxLength(100)`
    /// resolves "Name" for both the `IsRequired()` and the `HasMaxLength(100)` call).
    public static string? GetPropertyNameFor(InvocationExpressionSyntax fluentCall)
    {
        var current = fluentCall;

        while (current.Expression is MemberAccessExpressionSyntax { Expression: InvocationExpressionSyntax innerInvocation })
        {
            var name = GetPropertyNameForPropertyCall(innerInvocation);

            if (name is not null)
            {
                return name;
            }

            current = innerInvocation;
        }

        return null;
    }

    /// Given a bare `entity.Property(e => e.Name)` invocation itself (string overload and
    /// block-bodied lambda also resolved), returns "Name" without requiring a `.HasMaxLength(...)`
    /// (or any other) call chained onto it.
    public static string? GetPropertyNameForPropertyCall(InvocationExpressionSyntax propertyInvocation)
    {
        if (propertyInvocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Property" })
        {
            return null;
        }

        return TryReadSinglePropertyNameArgument(propertyInvocation);
    }

    /// Resolves a fluent call's first argument to a property name: `e => e.Name` (expression- or
    /// single-return-block-bodied, `Simple`/`Parenthesized` lambda), or a string literal `"Name"`.
    /// Shared by `Property(...)`-name resolution above and any other single-argument fluent call
    /// keyed by property (e.g. `Ignore(e => e.X)` / `Ignore("X")`).
    internal static string? TryReadSinglePropertyNameArgument(InvocationExpressionSyntax call)
    {
        var argumentExpression = call.ArgumentList.Arguments
            .Select(a => a.Expression)
            .FirstOrDefault();

        return argumentExpression switch
        {
            SimpleLambdaExpressionSyntax { ExpressionBody: MemberAccessExpressionSyntax { Name.Identifier.Text: var name } } => name,
            SimpleLambdaExpressionSyntax { Block: { Statements: [ReturnStatementSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: var name } }] } } => name,
            ParenthesizedLambdaExpressionSyntax { ExpressionBody: MemberAccessExpressionSyntax { Name.Identifier.Text: var name } } => name,
            ParenthesizedLambdaExpressionSyntax { Block: { Statements: [ReturnStatementSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: var name } }] } } => name,
            LiteralExpressionSyntax { Token.ValueText: var text } literal when literal.IsKind(SyntaxKind.StringLiteralExpression) => text,
            _ => null,
        };
    }

    /// Returns the lambda parameter name used by an existing `entity.Property(&lt;param&gt; => ...)` call
    /// within the given `Entity&lt;T&gt;(...)` invocation's scope, so a newly synthesized `Property()`
    /// call can match the block's existing style. Falls back to "e" if the block has no such call yet.
    public static string GetPropertyLambdaParameterName(SyntaxNode scope)
    {
        foreach (var propertyCall in FindCallsNamed(scope, "Property"))
        {
            if (propertyCall.ArgumentList.Arguments.Select(a => a.Expression).FirstOrDefault() is SimpleLambdaExpressionSyntax lambda)
            {
                return lambda.Parameter.Identifier.Text;
            }
        }

        return "e";
    }

    /// Reads a property name list from an invocation's arguments: a single lambda member access
    /// (`e => e.Id`), a composite anonymous-object lambda (`e => new { e.A, e.B }`), or bare string
    /// literal params (single or composite), e.g. for `HasKey(...)` or `HasForeignKey(...)`.
    /// Returns null when the argument shape isn't recognized.
    internal static IReadOnlyList<string>? TryReadPropertyNameList(InvocationExpressionSyntax call)
    {
        var arguments = call.ArgumentList.Arguments;

        if (arguments.Count == 0)
        {
            return null;
        }

        if (arguments.All(a => a.Expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression)))
        {
            return arguments
                .Select(a => ((LiteralExpressionSyntax)a.Expression).Token.ValueText)
                .ToList();
        }

        if (arguments.Count == 1 && arguments[0].Expression is SimpleLambdaExpressionSyntax { ExpressionBody: { } body })
        {
            return TryReadPropertyNameListFromLambdaBody(body);
        }

        return null;
    }

    private static IReadOnlyList<string>? TryReadPropertyNameListFromLambdaBody(ExpressionSyntax body)
    {
        if (body is MemberAccessExpressionSyntax { Name.Identifier.Text: var singleName })
        {
            return new List<string> { singleName };
        }

        if (body is AnonymousObjectCreationExpressionSyntax anonymousObject)
        {
            var names = new List<string>();

            foreach (var initializer in anonymousObject.Initializers)
            {
                if (initializer.NameEquals is not null
                    || initializer.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: var name })
                {
                    return null;
                }

                names.Add(name);
            }

            return names;
        }

        return null;
    }

    /// Finds the invocation immediately chained onto `invocation` via `.methodName(...)`, e.g. given
    /// the `HasOne(...)` invocation, `FindChainedCall(hasOneCall, "WithMany")` finds the
    /// `.WithMany(...)` invocation wrapping it. Returns null if nothing is chained onto `invocation`,
    /// or if what's chained isn't named `methodName`.
    internal static InvocationExpressionSyntax? FindChainedCall(InvocationExpressionSyntax invocation, string methodName)
    {
        return invocation.Parent is MemberAccessExpressionSyntax { Name.Identifier.Text: var name } memberAccess
            && memberAccess.Expression == invocation
            && name == methodName
            && memberAccess.Parent is InvocationExpressionSyntax chained
                ? chained
                : null;
    }

    private static readonly string[] CollectionWrapperNames =
    {
        "ICollection", "IList", "List", "IEnumerable", "HashSet", "ISet",
    };

    /// Given a property's ClrType text (e.g. "ICollection<Order>", "Order[]", or bare "Order"),
    /// returns the element type name for recognized collection wrapper shapes, or the type text
    /// unchanged if it isn't a generic/array shape at all. Returns null for a generic wrapper shape
    /// that isn't recognized (e.g. "IQueryable<Order>").
    internal static string? TryGetElementTypeName(string clrType)
    {
        if (clrType.EndsWith("[]", StringComparison.Ordinal))
        {
            return clrType[..^2];
        }

        var genericOpen = clrType.IndexOf('<');
        if (genericOpen < 0)
        {
            return clrType;
        }

        var wrapperName = clrType[..genericOpen];
        if (!CollectionWrapperNames.Contains(wrapperName))
        {
            return null;
        }

        var genericClose = clrType.LastIndexOf('>');
        return genericClose > genericOpen
            ? clrType[(genericOpen + 1)..genericClose]
            : null;
    }

    internal static string? GetConfiguredEntityName(InvocationExpressionSyntax invocation)
    {
        return invocation.Expression is MemberAccessExpressionSyntax
        {
            Expression: IdentifierNameSyntax,
            Name: GenericNameSyntax { Identifier.Text: "Entity" } generic
        }
            ? generic.TypeArgumentList.Arguments.FirstOrDefault()?.ToString()
            : null;
    }

    /// Finds every entity-name+scope pair configured in the source, from either the
    /// `receiver.Entity&lt;T&gt;(...)` fluent style or the `IEntityTypeConfiguration&lt;T&gt;`
    /// class style. `Scope` is the node whose descendants should be searched for fluent
    /// config calls: the `Entity&lt;T&gt;(...)` invocation itself, or the `Configure` method
    /// declaration for a config class. A single entity name can appear more than once
    /// (e.g. configured across multiple `Entity&lt;T&gt;()` blocks in one file).
    internal static IEnumerable<(string EntityName, SyntaxNode Scope)> FindConfigurationScopes(
        CompilationUnitSyntax root)
    {
        var invocationsByEntity = new Dictionary<string, List<InvocationExpressionSyntax>>();
        var entityOrder = new List<string>();

        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var entityName = GetConfiguredEntityName(invocation);
            if (entityName is null)
            {
                continue;
            }

            if (!invocationsByEntity.TryGetValue(entityName, out var invocations))
            {
                invocations = new List<InvocationExpressionSyntax>();
                invocationsByEntity[entityName] = invocations;
                entityOrder.Add(entityName);
            }

            invocations.Add(invocation);
        }

        foreach (var entityName in entityOrder)
        {
            foreach (var entityInvocation in invocationsByEntity[entityName])
            {
                yield return (entityName, entityInvocation);
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
    internal static string? TryGetEntityTypeConfigurationEntityName(ClassDeclarationSyntax classDeclaration)
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

    /// Given a `public DbSet&lt;T&gt; Name { get; set; }` property declaration, returns the `T` type
    /// argument node if it's a single identifier matching `entityName`; otherwise null.
    internal static IdentifierNameSyntax? GetDbSetEntityTypeArgument(PropertyDeclarationSyntax property, string entityName)
    {
        return property.Type is GenericNameSyntax { Identifier.Text: "DbSet" } dbSetGeneric
            && dbSetGeneric.TypeArgumentList.Arguments.Count == 1
            && dbSetGeneric.TypeArgumentList.Arguments[0] is IdentifierNameSyntax dbSetTypeArgument
            && dbSetTypeArgument.Identifier.Text == entityName
                ? dbSetTypeArgument
                : null;
    }

    /// Reads the property names (and optional index name) from a `HasIndex(...)` invocation.
    /// Returns null when the argument shape is not recognized (→ diagnostic).
    internal static (IReadOnlyList<string> PropertyNames, string? Name)? TryReadIndexPropertyNames(
        InvocationExpressionSyntax hasIndexCall)
    {
        var arguments = hasIndexCall.ArgumentList.Arguments;

        if (arguments.Count == 0)
            return null;

        var firstArg = arguments[0].Expression;
        var secondArg = arguments.Count >= 2 ? arguments[1].Expression : null;

        // All bare string literals → params overload (column names; no index name).
        if (arguments.All(a => a.Expression is LiteralExpressionSyntax lit
                && lit.IsKind(SyntaxKind.StringLiteralExpression)))
        {
            var names = arguments
                .Select(a => ((LiteralExpressionSyntax)a.Expression).Token.ValueText)
                .ToList();
            return (names, null);
        }

        // Lambda (+ optional string name).
        if (firstArg is SimpleLambdaExpressionSyntax { ExpressionBody: { } body })
        {
            var props = TryReadIndexPropertyNamesFromLambdaBody(body);
            if (props is null)
                return null;

            string? indexName = null;
            if (secondArg is LiteralExpressionSyntax nameLit
                    && nameLit.IsKind(SyntaxKind.StringLiteralExpression))
                indexName = nameLit.Token.ValueText;
            else if (secondArg is not null)
                return null;

            return (props, indexName);
        }

        // new[] { "A", "B" } (+ optional string name).
        if (firstArg is ImplicitArrayCreationExpressionSyntax implicitArray)
        {
            var names = new List<string>();
            foreach (var expr in implicitArray.Initializer.Expressions)
            {
                if (expr is LiteralExpressionSyntax elemLit
                        && elemLit.IsKind(SyntaxKind.StringLiteralExpression))
                    names.Add(elemLit.Token.ValueText);
                else
                    return null;
            }

            string? indexName = null;
            if (secondArg is LiteralExpressionSyntax nameArg
                    && nameArg.IsKind(SyntaxKind.StringLiteralExpression))
                indexName = nameArg.Token.ValueText;
            else if (secondArg is not null)
                return null; // second arg present but not a string literal

            return (names, indexName);
        }

        return null;
    }

    private static IReadOnlyList<string>? TryReadIndexPropertyNamesFromLambdaBody(ExpressionSyntax body)
    {
        if (body is MemberAccessExpressionSyntax { Name.Identifier.Text: var singleName })
            return new List<string> { singleName };

        if (body is AnonymousObjectCreationExpressionSyntax anonymousObject)
        {
            var names = new List<string>();
            foreach (var initializer in anonymousObject.Initializers)
            {
                if (initializer.NameEquals is not null
                    || initializer.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: var name })
                    return null;
                names.Add(name);
            }
            return names;
        }

        return null;
    }
}
