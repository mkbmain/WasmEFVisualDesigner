using System;
using System.Linq;
using EfSchemaVisualizer.Core.Parsing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EfSchemaVisualizer.Core.CodeGen;

public sealed class OnModelCreatingRewriter
{
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

        throw new InvalidOperationException(
            $"No HasMaxLength call found for {entityName}.{propertyName}");
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

    private static InvocationExpressionSyntax BuildMaxLengthCall(ExpressionSyntax propertyCallExpression, int maxLength)
    {
        return SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                propertyCallExpression,
                SyntaxFactory.IdentifierName("HasMaxLength")),
            SyntaxFactory.ArgumentList(
                SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.Argument(
                        SyntaxFactory.LiteralExpression(
                            SyntaxKind.NumericLiteralExpression,
                            SyntaxFactory.Literal(maxLength))))));
    }
}
