using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

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
    /// Also resolves the string overload `entity.Property("Name")` and a block-bodied lambda with
    /// a single `return e.Name;` statement.
    public static string? GetPropertyNameFor(InvocationExpressionSyntax fluentCall)
    {
        if (fluentCall.Expression is not MemberAccessExpressionSyntax
            {
                Expression: InvocationExpressionSyntax propertyInvocation
            })
        {
            return null;
        }

        return GetPropertyNameForPropertyCall(propertyInvocation);
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
}
