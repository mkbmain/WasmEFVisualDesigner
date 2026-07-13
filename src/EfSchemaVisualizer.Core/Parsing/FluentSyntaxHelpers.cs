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
    /// Finds every `&lt;receiver&gt;.Entity&lt;{entityName}&gt;(entity => { ... })` invocation, regardless of receiver identifier.
    public static IEnumerable<InvocationExpressionSyntax> FindEntityConfigInvocations(
        CompilationUnitSyntax root, string entityName)
    {
        return root.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(invocation => GetConfiguredEntityName(invocation) == entityName);
    }

    /// Finds every invocation named `methodName` within the given scope, e.g. all `HasMaxLength(...)` calls.
    /// A nested `Entity&lt;...&gt;(...)` invocation (regardless of receiver) is treated as an opaque boundary: its
    /// subtree is not descended into, so calls belonging to a nested entity's configuration are never
    /// misattributed to the outer scope's entity.
    public static IEnumerable<InvocationExpressionSyntax> FindCallsNamed(SyntaxNode scope, string methodName)
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

                if (child is InvocationExpressionSyntax invocation
                    && invocation.Expression is MemberAccessExpressionSyntax { Name.Identifier.Text: var name }
                    && name == methodName)
                {
                    results.Add(invocation);
                }

                Walk(child);
            }
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

        var argumentExpression = propertyInvocation.ArgumentList.Arguments
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
    public static string GetPropertyLambdaParameterName(InvocationExpressionSyntax entityInvocation)
    {
        foreach (var propertyCall in FindCallsNamed(entityInvocation, "Property"))
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
