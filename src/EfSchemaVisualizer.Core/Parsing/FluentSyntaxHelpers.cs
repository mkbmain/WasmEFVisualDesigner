using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EfSchemaVisualizer.Core.Parsing;

internal static class FluentSyntaxHelpers
{
    /// Finds every `modelBuilder.Entity&lt;{entityName}&gt;(entity => { ... })` invocation.
    public static IEnumerable<InvocationExpressionSyntax> FindEntityConfigInvocations(
        CompilationUnitSyntax root, string entityName)
    {
        return root.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(invocation => GetConfiguredEntityName(invocation) == entityName);
    }

    /// Finds every invocation named `methodName` within the given scope, e.g. all `HasMaxLength(...)` calls.
    public static IEnumerable<InvocationExpressionSyntax> FindCallsNamed(SyntaxNode scope, string methodName)
    {
        return scope.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(invocation => invocation.Expression is MemberAccessExpressionSyntax
            {
                Name.Identifier.Text: var name
            } && name == methodName);
    }

    /// Given a fluent call like `entity.Property(e => e.Name).HasMaxLength(100)`, returns "Name".
    public static string? GetPropertyNameFor(InvocationExpressionSyntax fluentCall)
    {
        if (fluentCall.Expression is not MemberAccessExpressionSyntax
            {
                Expression: InvocationExpressionSyntax propertyInvocation
            })
        {
            return null;
        }

        if (propertyInvocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Property" })
        {
            return null;
        }

        var lambdaArg = propertyInvocation.ArgumentList.Arguments
            .Select(a => a.Expression)
            .OfType<SimpleLambdaExpressionSyntax>()
            .FirstOrDefault();

        return lambdaArg?.ExpressionBody is MemberAccessExpressionSyntax { Name.Identifier.Text: var propertyName }
            ? propertyName
            : null;
    }

    private static string? GetConfiguredEntityName(InvocationExpressionSyntax invocation)
    {
        return invocation.Expression is MemberAccessExpressionSyntax
        {
            Name: GenericNameSyntax { Identifier.Text: "Entity" } generic
        }
            ? generic.TypeArgumentList.Arguments.FirstOrDefault()?.ToString()
            : null;
    }
}
