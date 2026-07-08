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

    private static string InsertEntityBlock(CompilationUnitSyntax root, string entityName, string propertyName, int newMaxLength)
    {
        var method = root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(m => m.Identifier.Text == "OnModelCreating")
            ?? throw new InvalidOperationException("No OnModelCreating method found in source.");

        var methodBody = method.Body
            ?? throw new InvalidOperationException("OnModelCreating has no method body.");

        var modelBuilderParamName = method.ParameterList.Parameters.Single().Identifier.Text;

        var propertyStatement = BuildPropertyStatement("entity", "e", propertyName, newMaxLength);

        var entityBlockStatement = SyntaxFactory.ExpressionStatement(
            SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.IdentifierName(modelBuilderParamName),
                    SyntaxFactory.GenericName(SyntaxFactory.Identifier("Entity"))
                        .WithTypeArgumentList(
                            SyntaxFactory.TypeArgumentList(
                                SyntaxFactory.SingletonSeparatedList<TypeSyntax>(
                                    SyntaxFactory.IdentifierName(entityName))))),
                SyntaxFactory.ArgumentList(
                    SyntaxFactory.SingletonSeparatedList(
                        SyntaxFactory.Argument(
                            SyntaxFactory.SimpleLambdaExpression(
                                SyntaxFactory.Parameter(SyntaxFactory.Identifier("entity")),
                                SyntaxFactory.Block(propertyStatement)))))));

        var newMethodBody = methodBody.AddStatements(entityBlockStatement);
        var newRoot = root.ReplaceNode(methodBody, newMethodBody);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    private static ExpressionStatementSyntax BuildPropertyStatement(string blockReceiverName, string propertyLambdaParam, string propertyName, int maxLength)
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

        return SyntaxFactory.ExpressionStatement(BuildMaxLengthCall(propertyCall, maxLength));
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
}
