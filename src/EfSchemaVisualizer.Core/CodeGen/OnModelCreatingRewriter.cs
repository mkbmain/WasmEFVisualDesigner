using System;
using System.Collections.Generic;
using System.Linq;
using EfSchemaVisualizer.Core.Model;
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

    private static string InsertEntityBlock(CompilationUnitSyntax root, string entityName, string propertyName, int newMaxLength)
    {
        var method = FindOnModelCreatingMethod(root);

        var methodBody = method.Body
            ?? throw new InvalidOperationException("OnModelCreating has no method body.");

        var modelBuilderParamName = method.ParameterList.Parameters.Single().Identifier.Text;

        var propertyStatement = BuildPropertyStatement("entity", "e", propertyName, newMaxLength);
        var entityBlockStatement = BuildEntityInvocationStatement(modelBuilderParamName, entityName, SyntaxFactory.Block(propertyStatement));

        var newMethodBody = methodBody.AddStatements(entityBlockStatement);
        var newRoot = root.ReplaceNode(methodBody, newMethodBody);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

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

    private static string MutateExistingPrecision(CompilationUnitSyntax root, InvocationExpressionSyntax targetCall, int precision, int? scale)
    {
        var newCall = targetCall.WithArgumentList(BuildPrecisionArgumentList(precision, scale));

        var newRoot = root.ReplaceNode(targetCall, newCall);
        return newRoot.ToFullString();
    }

    private static string AppendPrecisionToPropertyCall(CompilationUnitSyntax root, InvocationExpressionSyntax propertyCall, int precision, int? scale)
    {
        var precisionCall = BuildPrecisionCall(propertyCall, precision, scale);

        var newRoot = root.ReplaceNode(propertyCall, precisionCall);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    private static string InsertPrecisionStatement(CompilationUnitSyntax root, SyntaxNode scope, string propertyName, int precision, int? scale)
    {
        var (block, blockReceiverName) = GetScopeBlockAndReceiver(scope);
        var propertyLambdaParam = FluentSyntaxHelpers.GetPropertyLambdaParameterName(scope);

        var newStatement = BuildPrecisionPropertyStatement(blockReceiverName, propertyLambdaParam, propertyName, precision, scale);
        var newBlock = block.AddStatements(newStatement);

        var newRoot = root.ReplaceNode(block, newBlock);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    private static string InsertPrecisionEntityBlock(CompilationUnitSyntax root, string entityName, string propertyName, int precision, int? scale)
    {
        var method = FindOnModelCreatingMethod(root);

        var methodBody = method.Body
            ?? throw new InvalidOperationException("OnModelCreating has no method body.");

        var modelBuilderParamName = method.ParameterList.Parameters.Single().Identifier.Text;

        var propertyStatement = BuildPrecisionPropertyStatement("entity", "e", propertyName, precision, scale);
        var entityBlockStatement = BuildEntityInvocationStatement(modelBuilderParamName, entityName, SyntaxFactory.Block(propertyStatement));

        var newMethodBody = methodBody.AddStatements(entityBlockStatement);
        var newRoot = root.ReplaceNode(methodBody, newMethodBody);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    private static ExpressionStatementSyntax BuildPrecisionPropertyStatement(string blockReceiverName, string propertyLambdaParam, string propertyName, int precision, int? scale)
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

        return SyntaxFactory.ExpressionStatement(BuildPrecisionCall(propertyCall, precision, scale));
    }

    private static InvocationExpressionSyntax BuildPrecisionCall(ExpressionSyntax propertyCallExpression, int precision, int? scale)
    {
        return SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                propertyCallExpression,
                SyntaxFactory.IdentifierName("HasPrecision")),
            BuildPrecisionArgumentList(precision, scale));
    }

    private static ArgumentListSyntax BuildPrecisionArgumentList(int precision, int? scale)
    {
        var precisionArg = SyntaxFactory.Argument(
            SyntaxFactory.LiteralExpression(
                SyntaxKind.NumericLiteralExpression,
                SyntaxFactory.Literal(precision)));

        if (scale is null)
        {
            return SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(precisionArg));
        }

        var scaleArg = SyntaxFactory.Argument(
            SyntaxFactory.LiteralExpression(
                SyntaxKind.NumericLiteralExpression,
                SyntaxFactory.Literal(scale.Value)));

        return SyntaxFactory.ArgumentList(
            SyntaxFactory.SeparatedList(
                new[] { precisionArg, scaleArg },
                new[] { SyntaxFactory.Token(SyntaxKind.CommaToken).WithTrailingTrivia(SyntaxFactory.Space) }));
    }

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

    private static string MutateExistingIsRequired(CompilationUnitSyntax root, InvocationExpressionSyntax targetCall, bool newIsRequired)
    {
        var newCall = targetCall.WithArgumentList(BuildIsRequiredArgumentList(newIsRequired));

        var newRoot = root.ReplaceNode(targetCall, newCall);
        return newRoot.ToFullString();
    }

    private static string AppendIsRequiredToPropertyCall(CompilationUnitSyntax root, InvocationExpressionSyntax propertyCall, bool newIsRequired)
    {
        var isRequiredCall = BuildIsRequiredCall(propertyCall, newIsRequired);

        var newRoot = root.ReplaceNode(propertyCall, isRequiredCall);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    private static InvocationExpressionSyntax BuildIsRequiredCall(ExpressionSyntax propertyCallExpression, bool isRequired)
    {
        return SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                propertyCallExpression,
                SyntaxFactory.IdentifierName("IsRequired")),
            BuildIsRequiredArgumentList(isRequired));
    }

    private static ArgumentListSyntax BuildIsRequiredArgumentList(bool isRequired)
    {
        if (isRequired)
        {
            return SyntaxFactory.ArgumentList();
        }

        return SyntaxFactory.ArgumentList(
            SyntaxFactory.SingletonSeparatedList(
                SyntaxFactory.Argument(
                    SyntaxFactory.LiteralExpression(SyntaxKind.FalseLiteralExpression))));
    }

    private static string InsertIsRequiredPropertyStatement(CompilationUnitSyntax root, SyntaxNode scope, string propertyName, bool newIsRequired)
    {
        var (block, blockReceiverName) = GetScopeBlockAndReceiver(scope);
        var propertyLambdaParam = FluentSyntaxHelpers.GetPropertyLambdaParameterName(scope);

        var newStatement = BuildIsRequiredPropertyStatement(blockReceiverName, propertyLambdaParam, propertyName, newIsRequired);
        var newBlock = block.AddStatements(newStatement);

        var newRoot = root.ReplaceNode(block, newBlock);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    private static string InsertIsRequiredEntityBlock(CompilationUnitSyntax root, string entityName, string propertyName, bool newIsRequired)
    {
        var method = FindOnModelCreatingMethod(root);

        var methodBody = method.Body
            ?? throw new InvalidOperationException("OnModelCreating has no method body.");

        var modelBuilderParamName = method.ParameterList.Parameters.Single().Identifier.Text;

        var propertyStatement = BuildIsRequiredPropertyStatement("entity", "e", propertyName, newIsRequired);
        var entityBlockStatement = BuildEntityInvocationStatement(modelBuilderParamName, entityName, SyntaxFactory.Block(propertyStatement));

        var newMethodBody = methodBody.AddStatements(entityBlockStatement);
        var newRoot = root.ReplaceNode(methodBody, newMethodBody);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    private static ExpressionStatementSyntax BuildIsRequiredPropertyStatement(string blockReceiverName, string propertyLambdaParam, string propertyName, bool isRequired)
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

        return SyntaxFactory.ExpressionStatement(BuildIsRequiredCall(propertyCall, isRequired));
    }

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
        var newExpression = BuildHasKeyExpression(blockReceiverName, propertyNames, name);

        var outermostChainedCall = FindOutermostChainedCall(targetCall);

        if (outermostChainedCall.Parent is ExpressionStatementSyntax existingStatement)
        {
            var newStatement = SyntaxFactory.ExpressionStatement(newExpression);
            var newRoot = root.ReplaceNode(existingStatement, newStatement);
            return newRoot.NormalizeWhitespace().ToFullString();
        }
        else
        {
            var newRoot = root.ReplaceNode(outermostChainedCall, newExpression);
            return newRoot.NormalizeWhitespace().ToFullString();
        }
    }

    /// Starting from a call in a fluent chain (e.g. `HasKey(...)`), walks up through any
    /// calls already chained onto it (e.g. a pre-existing `.HasName(...)`) to find the
    /// outermost invocation that is still part of the same chain. Mirrors the traversal
    /// direction of FluentSyntaxHelpers.WalkChainedTail, but walking up instead of down.
    private static InvocationExpressionSyntax FindOutermostChainedCall(InvocationExpressionSyntax invocation)
    {
        var current = invocation;

        while (current.Parent is MemberAccessExpressionSyntax memberAccess
            && memberAccess.Expression == current
            && memberAccess.Parent is InvocationExpressionSyntax outer)
        {
            current = outer;
        }

        return current;
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
        => SyntaxFactory.ExpressionStatement(BuildHasKeyExpression(blockReceiverName, propertyNames, name));

    private static ExpressionSyntax BuildHasKeyExpression(string blockReceiverName, IReadOnlyList<string> propertyNames, string? name)
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

        return expression;
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

    public string AddAlternateKey(string sourceCode, string entityName, IReadOnlyList<string> propertyNames)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var scopes = FindConfigScopes(root, entityName);

        var alreadyExists = scopes
            .SelectMany(scope => FluentSyntaxHelpers.FindCallsNamed(scope, "HasAlternateKey"))
            .Any(call => FluentSyntaxHelpers.TryReadPropertyNameList(call) is { } existing
                && existing.SequenceEqual(propertyNames));

        if (alreadyExists)
        {
            return sourceCode;
        }

        var existingScope = scopes.FirstOrDefault();

        if (existingScope is not null)
        {
            return InsertAlternateKeyStatement(root, existingScope, propertyNames);
        }

        return InsertAlternateKeyEntityBlock(root, entityName, propertyNames);
    }

    private static string InsertAlternateKeyStatement(CompilationUnitSyntax root, SyntaxNode scope, IReadOnlyList<string> propertyNames)
    {
        var (block, blockReceiverName) = GetScopeBlockAndReceiver(scope);

        var newStatement = BuildHasAlternateKeyStatement(blockReceiverName, propertyNames);
        var newBlock = block.AddStatements(newStatement);

        var newRoot = root.ReplaceNode(block, newBlock);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    private static string InsertAlternateKeyEntityBlock(CompilationUnitSyntax root, string entityName, IReadOnlyList<string> propertyNames)
    {
        var method = FindOnModelCreatingMethod(root);

        var methodBody = method.Body
            ?? throw new InvalidOperationException("OnModelCreating has no method body.");

        var modelBuilderParamName = method.ParameterList.Parameters.Single().Identifier.Text;

        var alternateKeyStatement = BuildHasAlternateKeyStatement("entity", propertyNames);
        var entityBlockStatement = BuildEntityInvocationStatement(modelBuilderParamName, entityName, SyntaxFactory.Block(alternateKeyStatement));

        var newMethodBody = methodBody.AddStatements(entityBlockStatement);
        var newRoot = root.ReplaceNode(methodBody, newMethodBody);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    private static ExpressionStatementSyntax BuildHasAlternateKeyStatement(string blockReceiverName, IReadOnlyList<string> propertyNames)
    {
        return SyntaxFactory.ExpressionStatement(
            SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.IdentifierName(blockReceiverName),
                    SyntaxFactory.IdentifierName("HasAlternateKey")),
                BuildHasKeyArgumentList(propertyNames)));
    }

    public string RemoveAlternateKey(string sourceCode, string entityName, IReadOnlyList<string> propertyNames)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var scopes = FindConfigScopes(root, entityName);

        var existingCall = scopes
            .SelectMany(scope => FluentSyntaxHelpers.FindCallsNamed(scope, "HasAlternateKey"))
            .FirstOrDefault(call => FluentSyntaxHelpers.TryReadPropertyNameList(call) is { } existing
                && existing.SequenceEqual(propertyNames));

        if (existingCall is null || existingCall.Parent is not ExpressionStatementSyntax statement)
        {
            return sourceCode;
        }

        var newRoot = root.RemoveNode(statement, SyntaxRemoveOptions.KeepNoTrivia)!;
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    public string SetKeyless(string sourceCode, string entityName)
    {
        var withoutKey = RemoveKey(sourceCode, entityName);

        var tree = CSharpSyntaxTree.ParseText(withoutKey);
        var root = tree.GetCompilationUnitRoot();

        var scopes = FindConfigScopes(root, entityName);

        var existingHasNoKeyCall = scopes
            .SelectMany(scope => FluentSyntaxHelpers.FindCallsNamed(scope, "HasNoKey"))
            .FirstOrDefault();

        if (existingHasNoKeyCall is not null)
        {
            return withoutKey;
        }

        var existingScope = scopes.FirstOrDefault();

        if (existingScope is not null)
        {
            return InsertKeylessStatement(root, existingScope);
        }

        return InsertKeylessEntityBlock(root, entityName);
    }

    private static string InsertKeylessStatement(CompilationUnitSyntax root, SyntaxNode scope)
    {
        var (block, blockReceiverName) = GetScopeBlockAndReceiver(scope);

        var newStatement = BuildHasNoKeyStatement(blockReceiverName);
        var newBlock = block.AddStatements(newStatement);

        var newRoot = root.ReplaceNode(block, newBlock);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    private static string InsertKeylessEntityBlock(CompilationUnitSyntax root, string entityName)
    {
        var method = FindOnModelCreatingMethod(root);

        var methodBody = method.Body
            ?? throw new InvalidOperationException("OnModelCreating has no method body.");

        var modelBuilderParamName = method.ParameterList.Parameters.Single().Identifier.Text;

        var keylessStatement = BuildHasNoKeyStatement("entity");
        var entityBlockStatement = BuildEntityInvocationStatement(modelBuilderParamName, entityName, SyntaxFactory.Block(keylessStatement));

        var newMethodBody = methodBody.AddStatements(entityBlockStatement);
        var newRoot = root.ReplaceNode(methodBody, newMethodBody);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    private static ExpressionStatementSyntax BuildHasNoKeyStatement(string blockReceiverName)
    {
        return SyntaxFactory.ExpressionStatement(
            SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.IdentifierName(blockReceiverName),
                    SyntaxFactory.IdentifierName("HasNoKey")),
                SyntaxFactory.ArgumentList()));
    }

    public string RemoveKeyless(string sourceCode, string entityName)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var scopes = FindConfigScopes(root, entityName);

        var existingHasNoKeyCall = scopes
            .SelectMany(scope => FluentSyntaxHelpers.FindCallsNamed(scope, "HasNoKey"))
            .FirstOrDefault();

        if (existingHasNoKeyCall is null || existingHasNoKeyCall.Parent is not ExpressionStatementSyntax statement)
        {
            return sourceCode;
        }

        var newRoot = root.RemoveNode(statement, SyntaxRemoveOptions.KeepNoTrivia)!;
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    public string SetMappingStrategy(string sourceCode, string entityName, MappingStrategy strategy)
    {
        var withoutExisting = RemoveMappingStrategy(sourceCode, entityName);

        if (strategy == MappingStrategy.Tph)
        {
            return withoutExisting;
        }

        var tree = CSharpSyntaxTree.ParseText(withoutExisting);
        var root = tree.GetCompilationUnitRoot();
        var methodName = strategy == MappingStrategy.Tpt ? "UseTptMappingStrategy" : "UseTpcMappingStrategy";

        var scopes = FindConfigScopes(root, entityName);
        var existingScope = scopes.FirstOrDefault();

        if (existingScope is not null)
        {
            var (block, blockReceiverName) = GetScopeBlockAndReceiver(existingScope);
            var newStatement = BuildBareEntityCallStatement(blockReceiverName, methodName);
            var newBlock = block.AddStatements(newStatement);

            var newRoot = root.ReplaceNode(block, newBlock);
            return newRoot.NormalizeWhitespace().ToFullString();
        }

        var method = FindOnModelCreatingMethod(root);
        var methodBody = method.Body
            ?? throw new InvalidOperationException("OnModelCreating has no method body.");

        var modelBuilderParamName = method.ParameterList.Parameters.Single().Identifier.Text;
        var statement = BuildBareEntityCallStatement("entity", methodName);
        var entityBlockStatement = BuildEntityInvocationStatement(modelBuilderParamName, entityName, SyntaxFactory.Block(statement));

        var newMethodBody = methodBody.AddStatements(entityBlockStatement);
        var newRoot2 = root.ReplaceNode(methodBody, newMethodBody);
        return newRoot2.NormalizeWhitespace().ToFullString();
    }

    public string RemoveMappingStrategy(string sourceCode, string entityName)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var scopes = FindConfigScopes(root, entityName);
        var existingCall = scopes
            .SelectMany(scope => FluentSyntaxHelpers.FindCallsNamed(scope, "UseTptMappingStrategy")
                .Concat(FluentSyntaxHelpers.FindCallsNamed(scope, "UseTpcMappingStrategy")))
            .FirstOrDefault();

        if (existingCall is null || existingCall.Parent is not ExpressionStatementSyntax statement)
        {
            return sourceCode;
        }

        var newRoot = root.RemoveNode(statement, SyntaxRemoveOptions.KeepNoTrivia)!;
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    public string SetDiscriminator(
        string sourceCode, string rootEntityName, string columnName, string clrType,
        IReadOnlyList<(string DerivedEntityName, string Value)> values)
    {
        var withoutExisting = RemoveDiscriminator(sourceCode, rootEntityName);

        var tree = CSharpSyntaxTree.ParseText(withoutExisting);
        var root = tree.GetCompilationUnitRoot();

        var scopes = FindConfigScopes(root, rootEntityName);
        var existingScope = scopes.FirstOrDefault();

        if (existingScope is not null)
        {
            var (block, blockReceiverName) = GetScopeBlockAndReceiver(existingScope);
            var statement = SyntaxFactory.ExpressionStatement(BuildDiscriminatorExpression(blockReceiverName, columnName, clrType, values));
            var newBlock = block.AddStatements(statement);

            var newRoot = root.ReplaceNode(block, newBlock);
            return newRoot.NormalizeWhitespace().ToFullString();
        }

        var method = FindOnModelCreatingMethod(root);
        var methodBody = method.Body
            ?? throw new InvalidOperationException("OnModelCreating has no method body.");

        var modelBuilderParamName = method.ParameterList.Parameters.Single().Identifier.Text;
        var entityStatement = SyntaxFactory.ExpressionStatement(BuildDiscriminatorExpression("entity", columnName, clrType, values));
        var entityBlockStatement = BuildEntityInvocationStatement(modelBuilderParamName, rootEntityName, SyntaxFactory.Block(entityStatement));

        var newMethodBody = methodBody.AddStatements(entityBlockStatement);
        var newRoot2 = root.ReplaceNode(methodBody, newMethodBody);
        return newRoot2.NormalizeWhitespace().ToFullString();
    }

    public string RemoveDiscriminator(string sourceCode, string rootEntityName)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var scopes = FindConfigScopes(root, rootEntityName);
        var existingCall = scopes
            .SelectMany(scope => FluentSyntaxHelpers.FindCallsNamed(scope, "HasDiscriminator"))
            .FirstOrDefault();

        if (existingCall is null)
        {
            return sourceCode;
        }

        var outermostChainedCall = FindOutermostChainedCall(existingCall);
        var statement = outermostChainedCall.Ancestors().OfType<ExpressionStatementSyntax>().First();

        var newRoot = root.RemoveNode(statement, SyntaxRemoveOptions.KeepNoTrivia)!;
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    private static ExpressionSyntax BuildDiscriminatorExpression(
        string receiverName, string columnName, string clrType,
        IReadOnlyList<(string DerivedEntityName, string Value)> values)
    {
        SimpleNameSyntax hasDiscriminatorName = SyntaxFactory.GenericName(SyntaxFactory.Identifier("HasDiscriminator"))
            .WithTypeArgumentList(SyntaxFactory.TypeArgumentList(SyntaxFactory.SingletonSeparatedList<TypeSyntax>(SyntaxFactory.ParseTypeName(clrType))));

        ExpressionSyntax expression = SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, SyntaxFactory.IdentifierName(receiverName), hasDiscriminatorName),
            SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(
                SyntaxFactory.Argument(SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(columnName))))));

        foreach (var (derivedEntityName, value) in values)
        {
            var hasValueName = SyntaxFactory.GenericName(SyntaxFactory.Identifier("HasValue"))
                .WithTypeArgumentList(SyntaxFactory.TypeArgumentList(SyntaxFactory.SingletonSeparatedList<TypeSyntax>(SyntaxFactory.ParseTypeName(derivedEntityName))));

            expression = SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, expression, hasValueName),
                SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.Argument(SyntaxFactory.ParseExpression(value)))));
        }

        return expression;
    }

    private static ExpressionStatementSyntax BuildBareEntityCallStatement(string blockReceiverName, string methodName)
    {
        return SyntaxFactory.ExpressionStatement(
            SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.IdentifierName(blockReceiverName),
                    SyntaxFactory.IdentifierName(methodName)),
                SyntaxFactory.ArgumentList()));
    }

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

    private static string MutateExistingTable(CompilationUnitSyntax root, InvocationExpressionSyntax targetCall, string tableName, string? schema)
    {
        var newCall = targetCall.WithArgumentList(BuildToTableArgumentList(tableName, schema));

        var newRoot = root.ReplaceNode(targetCall, newCall);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    private static string InsertTableStatement(CompilationUnitSyntax root, SyntaxNode scope, string tableName, string? schema)
    {
        var (block, blockReceiverName) = GetScopeBlockAndReceiver(scope);

        var newStatement = BuildToTableStatement(blockReceiverName, tableName, schema);
        var newBlock = block.AddStatements(newStatement);

        var newRoot = root.ReplaceNode(block, newBlock);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    private static string InsertTableEntityBlock(CompilationUnitSyntax root, string entityName, string tableName, string? schema)
    {
        var method = FindOnModelCreatingMethod(root);

        var methodBody = method.Body
            ?? throw new InvalidOperationException("OnModelCreating has no method body.");

        var modelBuilderParamName = method.ParameterList.Parameters.Single().Identifier.Text;

        var tableStatement = BuildToTableStatement("entity", tableName, schema);
        var entityBlockStatement = BuildEntityInvocationStatement(modelBuilderParamName, entityName, SyntaxFactory.Block(tableStatement));

        var newMethodBody = methodBody.AddStatements(entityBlockStatement);
        var newRoot = root.ReplaceNode(methodBody, newMethodBody);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    private static ExpressionStatementSyntax BuildToTableStatement(string blockReceiverName, string tableName, string? schema)
    {
        return SyntaxFactory.ExpressionStatement(
            SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.IdentifierName(blockReceiverName),
                    SyntaxFactory.IdentifierName("ToTable")),
                BuildToTableArgumentList(tableName, schema)));
    }

    private static ArgumentListSyntax BuildToTableArgumentList(string tableName, string? schema)
    {
        var tableNameArg = SyntaxFactory.Argument(
            SyntaxFactory.LiteralExpression(
                SyntaxKind.StringLiteralExpression,
                SyntaxFactory.Literal(tableName)));

        if (schema is null)
        {
            return SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(tableNameArg));
        }

        var schemaArg = SyntaxFactory.Argument(
            SyntaxFactory.LiteralExpression(
                SyntaxKind.StringLiteralExpression,
                SyntaxFactory.Literal(schema)));

        return SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(new[] { tableNameArg, schemaArg }));
    }

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

    public string RemoveColumnName(string sourceCode, string entityName, string propertyName)
    {
        return RemoveStringArgCall(sourceCode, entityName, propertyName, "HasColumnName");
    }

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

    public string RemoveColumnType(string sourceCode, string entityName, string propertyName)
    {
        return RemoveStringArgCall(sourceCode, entityName, propertyName, "HasColumnType");
    }

    private static string MutateExistingStringArgCall(CompilationUnitSyntax root, InvocationExpressionSyntax targetCall, string value, bool? secondArg = null)
    {
        var newCall = targetCall.WithArgumentList(targetCall.ArgumentList.WithArguments(BuildStringArgArguments(value, secondArg)));

        var newRoot = root.ReplaceNode(targetCall, newCall);
        return newRoot.ToFullString();
    }

    // Builds the argument list shared by MutateExistingStringArgCall and BuildStringArgCall.
    // Uses an explicit comma separator with trailing-space trivia (rather than a bare node list,
    // which SyntaxFactory.SeparatedList would join with a space-less comma) so that
    // MutateExistingStringArgCall — which intentionally skips a whole-tree NormalizeWhitespace to
    // preserve the file's existing formatting — still renders "value, secondArg" correctly.
    private static SeparatedSyntaxList<ArgumentSyntax> BuildStringArgArguments(string value, bool? secondArg)
    {
        var valueArgument = SyntaxFactory.Argument(SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(value)));

        if (secondArg is null)
        {
            return SyntaxFactory.SingletonSeparatedList(valueArgument);
        }

        var secondArgument = SyntaxFactory.Argument(SyntaxFactory.LiteralExpression(
            secondArg.Value ? SyntaxKind.TrueLiteralExpression : SyntaxKind.FalseLiteralExpression));

        return SyntaxFactory.SeparatedList(
            new[] { valueArgument, secondArgument },
            new[] { SyntaxFactory.Token(SyntaxKind.CommaToken).WithTrailingTrivia(SyntaxFactory.Space) });
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
        return SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                propertyCallExpression,
                SyntaxFactory.IdentifierName(methodName)),
            SyntaxFactory.ArgumentList(BuildStringArgArguments(value, secondArg)));
    }

    private static InvocationExpressionSyntax BuildTypeArgCall(ExpressionSyntax receiverExpression, string typeArgText)
    {
        SimpleNameSyntax name = SyntaxFactory.GenericName(SyntaxFactory.Identifier("HasConversion"))
            .WithTypeArgumentList(SyntaxFactory.TypeArgumentList(SyntaxFactory.SingletonSeparatedList<TypeSyntax>(SyntaxFactory.ParseTypeName(typeArgText))));

        return SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, receiverExpression, name),
            SyntaxFactory.ArgumentList());
    }

    private static string MutateExistingTypeArgCall(CompilationUnitSyntax root, InvocationExpressionSyntax targetCall, string typeArgText)
    {
        var receiverExpression = ((MemberAccessExpressionSyntax)targetCall.Expression).Expression;
        var newCall = BuildTypeArgCall(receiverExpression, typeArgText);

        var newRoot = root.ReplaceNode(targetCall, newCall);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    private static string AppendTypeArgCallToPropertyCall(CompilationUnitSyntax root, InvocationExpressionSyntax propertyCall, string typeArgText)
    {
        var newCall = BuildTypeArgCall(propertyCall, typeArgText);

        var newRoot = root.ReplaceNode(propertyCall, newCall);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    private static string InsertTypeArgPropertyStatement(CompilationUnitSyntax root, SyntaxNode scope, string propertyName, string typeArgText)
    {
        var (block, blockReceiverName) = GetScopeBlockAndReceiver(scope);
        var propertyLambdaParam = FluentSyntaxHelpers.GetPropertyLambdaParameterName(scope);

        var newStatement = BuildTypeArgPropertyStatement(blockReceiverName, propertyLambdaParam, propertyName, typeArgText);
        var newBlock = block.AddStatements(newStatement);

        var newRoot = root.ReplaceNode(block, newBlock);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    private static string InsertTypeArgEntityBlock(CompilationUnitSyntax root, string entityName, string propertyName, string typeArgText)
    {
        var method = FindOnModelCreatingMethod(root);

        var methodBody = method.Body
            ?? throw new InvalidOperationException("OnModelCreating has no method body.");

        var modelBuilderParamName = method.ParameterList.Parameters.Single().Identifier.Text;

        var propertyStatement = BuildTypeArgPropertyStatement("entity", "e", propertyName, typeArgText);
        var entityBlockStatement = BuildEntityInvocationStatement(modelBuilderParamName, entityName, SyntaxFactory.Block(propertyStatement));

        var newMethodBody = methodBody.AddStatements(entityBlockStatement);
        var newRoot = root.ReplaceNode(methodBody, newMethodBody);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    private static ExpressionStatementSyntax BuildTypeArgPropertyStatement(string blockReceiverName, string propertyLambdaParam, string propertyName, string typeArgText)
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

        return SyntaxFactory.ExpressionStatement(BuildTypeArgCall(propertyCall, typeArgText));
    }

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

    private static string MutateExistingDefaultValue(CompilationUnitSyntax root, InvocationExpressionSyntax targetCall, string literalText)
    {
        var newCall = targetCall.WithArgumentList(BuildDefaultValueArgumentList(literalText));

        var newRoot = root.ReplaceNode(targetCall, newCall);
        return newRoot.ToFullString();
    }

    private static string AppendDefaultValueToPropertyCall(CompilationUnitSyntax root, InvocationExpressionSyntax propertyCall, string literalText)
    {
        var newCall = BuildDefaultValueCall(propertyCall, literalText);

        var newRoot = root.ReplaceNode(propertyCall, newCall);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    private static string InsertDefaultValuePropertyStatement(CompilationUnitSyntax root, SyntaxNode scope, string propertyName, string literalText)
    {
        var (block, blockReceiverName) = GetScopeBlockAndReceiver(scope);
        var propertyLambdaParam = FluentSyntaxHelpers.GetPropertyLambdaParameterName(scope);

        var newStatement = BuildDefaultValuePropertyStatement(blockReceiverName, propertyLambdaParam, propertyName, literalText);
        var newBlock = block.AddStatements(newStatement);

        var newRoot = root.ReplaceNode(block, newBlock);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    private static string InsertDefaultValueEntityBlock(CompilationUnitSyntax root, string entityName, string propertyName, string literalText)
    {
        var method = FindOnModelCreatingMethod(root);

        var methodBody = method.Body
            ?? throw new InvalidOperationException("OnModelCreating has no method body.");

        var modelBuilderParamName = method.ParameterList.Parameters.Single().Identifier.Text;

        var propertyStatement = BuildDefaultValuePropertyStatement("entity", "e", propertyName, literalText);
        var entityBlockStatement = BuildEntityInvocationStatement(modelBuilderParamName, entityName, SyntaxFactory.Block(propertyStatement));

        var newMethodBody = methodBody.AddStatements(entityBlockStatement);
        var newRoot = root.ReplaceNode(methodBody, newMethodBody);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    private static ExpressionStatementSyntax BuildDefaultValuePropertyStatement(string blockReceiverName, string propertyLambdaParam, string propertyName, string literalText)
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

        return SyntaxFactory.ExpressionStatement(BuildDefaultValueCall(propertyCall, literalText));
    }

    private static InvocationExpressionSyntax BuildDefaultValueCall(ExpressionSyntax propertyCallExpression, string literalText)
    {
        return SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                propertyCallExpression,
                SyntaxFactory.IdentifierName("HasDefaultValue")),
            BuildDefaultValueArgumentList(literalText));
    }

    private static ArgumentListSyntax BuildDefaultValueArgumentList(string literalText)
    {
        var expression = SyntaxFactory.ParseExpression(literalText);

        return SyntaxFactory.ArgumentList(
            SyntaxFactory.SingletonSeparatedList(
                SyntaxFactory.Argument(expression)));
    }

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

    public string SetDefaultValueSql(string sourceCode, string entityName, string propertyName, string sql)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var scopes = FindConfigScopes(root, entityName);

        var existingCall = scopes
            .SelectMany(scope => FluentSyntaxHelpers.FindCallsNamed(scope, "HasDefaultValueSql"))
            .FirstOrDefault(call => FluentSyntaxHelpers.GetPropertyNameFor(call) == propertyName);

        if (existingCall is not null)
        {
            return MutateExistingStringArgCall(root, existingCall, sql);
        }

        var existingPropertyCall = scopes
            .SelectMany(scope => FluentSyntaxHelpers.FindCallsNamed(scope, "Property"))
            .FirstOrDefault(call => FluentSyntaxHelpers.GetPropertyNameForPropertyCall(call) == propertyName);

        if (existingPropertyCall is not null)
        {
            return AppendStringArgCallToPropertyCall(root, existingPropertyCall, "HasDefaultValueSql", sql);
        }

        var existingScope = scopes.FirstOrDefault();

        if (existingScope is not null)
        {
            return InsertStringArgPropertyStatement(root, existingScope, propertyName, "HasDefaultValueSql", sql);
        }

        return InsertStringArgEntityBlock(root, entityName, propertyName, "HasDefaultValueSql", sql);
    }

    public string RemoveDefaultValueSql(string sourceCode, string entityName, string propertyName)
    {
        return RemoveStringArgCall(sourceCode, entityName, propertyName, "HasDefaultValueSql");
    }

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

    public string SetValueConversion(string sourceCode, string entityName, string propertyName, string providerClrType)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var scopes = FindConfigScopes(root, entityName);

        var existingCall = scopes
            .SelectMany(scope => FluentSyntaxHelpers.FindCallsNamed(scope, "HasConversion"))
            .FirstOrDefault(call => FluentSyntaxHelpers.GetPropertyNameFor(call) == propertyName);

        if (existingCall is not null)
        {
            return MutateExistingTypeArgCall(root, existingCall, providerClrType);
        }

        var existingPropertyCall = scopes
            .SelectMany(scope => FluentSyntaxHelpers.FindCallsNamed(scope, "Property"))
            .FirstOrDefault(call => FluentSyntaxHelpers.GetPropertyNameForPropertyCall(call) == propertyName);

        if (existingPropertyCall is not null)
        {
            return AppendTypeArgCallToPropertyCall(root, existingPropertyCall, providerClrType);
        }

        var existingScope = scopes.FirstOrDefault();

        if (existingScope is not null)
        {
            return InsertTypeArgPropertyStatement(root, existingScope, propertyName, providerClrType);
        }

        return InsertTypeArgEntityBlock(root, entityName, propertyName, providerClrType);
    }

    public string RemoveValueConversion(string sourceCode, string entityName, string propertyName)
    {
        return RemoveStringArgCall(sourceCode, entityName, propertyName, "HasConversion");
    }

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

    public string SetView(string sourceCode, string entityName, string viewName, string? schema)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var scopes = FindConfigScopes(root, entityName);

        var existingToViewCall = scopes
            .SelectMany(scope => FluentSyntaxHelpers.FindCallsNamed(scope, "ToView"))
            .FirstOrDefault();

        if (existingToViewCall is not null)
        {
            return MutateExistingView(root, existingToViewCall, viewName, schema);
        }

        var existingScope = scopes.FirstOrDefault();

        if (existingScope is not null)
        {
            return InsertViewStatement(root, existingScope, viewName, schema);
        }

        return InsertViewEntityBlock(root, entityName, viewName, schema);
    }

    private static string MutateExistingView(CompilationUnitSyntax root, InvocationExpressionSyntax targetCall, string viewName, string? schema)
    {
        var newCall = targetCall.WithArgumentList(BuildToViewArgumentList(viewName, schema));

        var newRoot = root.ReplaceNode(targetCall, newCall);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    private static string InsertViewStatement(CompilationUnitSyntax root, SyntaxNode scope, string viewName, string? schema)
    {
        var (block, blockReceiverName) = GetScopeBlockAndReceiver(scope);

        var newStatement = BuildToViewStatement(blockReceiverName, viewName, schema);
        var newBlock = block.AddStatements(newStatement);

        var newRoot = root.ReplaceNode(block, newBlock);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    private static string InsertViewEntityBlock(CompilationUnitSyntax root, string entityName, string viewName, string? schema)
    {
        var method = FindOnModelCreatingMethod(root);

        var methodBody = method.Body
            ?? throw new InvalidOperationException("OnModelCreating has no method body.");

        var modelBuilderParamName = method.ParameterList.Parameters.Single().Identifier.Text;

        var viewStatement = BuildToViewStatement("entity", viewName, schema);
        var entityBlockStatement = BuildEntityInvocationStatement(modelBuilderParamName, entityName, SyntaxFactory.Block(viewStatement));

        var newMethodBody = methodBody.AddStatements(entityBlockStatement);
        var newRoot = root.ReplaceNode(methodBody, newMethodBody);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    private static ExpressionStatementSyntax BuildToViewStatement(string blockReceiverName, string viewName, string? schema)
    {
        return SyntaxFactory.ExpressionStatement(
            SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.IdentifierName(blockReceiverName),
                    SyntaxFactory.IdentifierName("ToView")),
                BuildToViewArgumentList(viewName, schema)));
    }

    private static ArgumentListSyntax BuildToViewArgumentList(string viewName, string? schema)
    {
        var viewNameArg = SyntaxFactory.Argument(
            SyntaxFactory.LiteralExpression(
                SyntaxKind.StringLiteralExpression,
                SyntaxFactory.Literal(viewName)));

        if (schema is null)
        {
            return SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(viewNameArg));
        }

        var schemaArg = SyntaxFactory.Argument(
            SyntaxFactory.LiteralExpression(
                SyntaxKind.StringLiteralExpression,
                SyntaxFactory.Literal(schema)));

        return SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(new[] { viewNameArg, schemaArg }));
    }

    public string RemoveView(string sourceCode, string entityName)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var scopes = FindConfigScopes(root, entityName);

        var existingToViewCall = scopes
            .SelectMany(scope => FluentSyntaxHelpers.FindCallsNamed(scope, "ToView"))
            .FirstOrDefault();

        if (existingToViewCall is null || existingToViewCall.Parent is not ExpressionStatementSyntax statement)
        {
            return sourceCode;
        }

        var newRoot = root.RemoveNode(statement, SyntaxRemoveOptions.KeepNoTrivia)!;
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    public string SetSqlQuery(string sourceCode, string entityName, string sql)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var scopes = FindConfigScopes(root, entityName);

        var existingCall = scopes
            .SelectMany(scope => FluentSyntaxHelpers.FindCallsNamed(scope, "ToSqlQuery"))
            .FirstOrDefault();

        if (existingCall is not null)
        {
            return MutateExistingSqlQuery(root, existingCall, sql);
        }

        var existingScope = scopes.FirstOrDefault();

        if (existingScope is not null)
        {
            return InsertSqlQueryStatement(root, existingScope, sql);
        }

        return InsertSqlQueryEntityBlock(root, entityName, sql);
    }

    private static string MutateExistingSqlQuery(CompilationUnitSyntax root, InvocationExpressionSyntax targetCall, string sql)
    {
        var newArgument = SyntaxFactory.Argument(
            SyntaxFactory.LiteralExpression(
                SyntaxKind.StringLiteralExpression,
                SyntaxFactory.Literal(sql)));

        var newCall = targetCall.WithArgumentList(
            targetCall.ArgumentList.WithArguments(
                SyntaxFactory.SingletonSeparatedList(newArgument)));

        var newRoot = root.ReplaceNode(targetCall, newCall);
        return newRoot.ToFullString();
    }

    private static string InsertSqlQueryStatement(CompilationUnitSyntax root, SyntaxNode scope, string sql)
    {
        var (block, blockReceiverName) = GetScopeBlockAndReceiver(scope);

        var newStatement = BuildToSqlQueryStatement(blockReceiverName, sql);
        var newBlock = block.AddStatements(newStatement);

        var newRoot = root.ReplaceNode(block, newBlock);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    private static string InsertSqlQueryEntityBlock(CompilationUnitSyntax root, string entityName, string sql)
    {
        var method = FindOnModelCreatingMethod(root);

        var methodBody = method.Body
            ?? throw new InvalidOperationException("OnModelCreating has no method body.");

        var modelBuilderParamName = method.ParameterList.Parameters.Single().Identifier.Text;

        var sqlQueryStatement = BuildToSqlQueryStatement("entity", sql);
        var entityBlockStatement = BuildEntityInvocationStatement(modelBuilderParamName, entityName, SyntaxFactory.Block(sqlQueryStatement));

        var newMethodBody = methodBody.AddStatements(entityBlockStatement);
        var newRoot = root.ReplaceNode(methodBody, newMethodBody);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    private static ExpressionStatementSyntax BuildToSqlQueryStatement(string blockReceiverName, string sql)
    {
        return SyntaxFactory.ExpressionStatement(
            SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.IdentifierName(blockReceiverName),
                    SyntaxFactory.IdentifierName("ToSqlQuery")),
                SyntaxFactory.ArgumentList(
                    SyntaxFactory.SingletonSeparatedList(
                        SyntaxFactory.Argument(
                            SyntaxFactory.LiteralExpression(
                                SyntaxKind.StringLiteralExpression,
                                SyntaxFactory.Literal(sql)))))));
    }

    public string RemoveSqlQuery(string sourceCode, string entityName)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var scopes = FindConfigScopes(root, entityName);

        var existingCall = scopes
            .SelectMany(scope => FluentSyntaxHelpers.FindCallsNamed(scope, "ToSqlQuery"))
            .FirstOrDefault();

        if (existingCall is null || existingCall.Parent is not ExpressionStatementSyntax statement)
        {
            return sourceCode;
        }

        var newRoot = root.RemoveNode(statement, SyntaxRemoveOptions.KeepNoTrivia)!;
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    public string AddEntity(string sourceCode, string entityName, string dbSetPropertyName)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        if (!root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>().Any())
        {
            // Bare fluent-config source: just top-level `modelBuilder.Entity<T>(...)` statements,
            // with no wrapping OnModelCreating method or DbContext class at all - the form the
            // app's own sample data and pasted-snippet workflow both use. There's no class to add
            // a DbSet<T> property to, so just append the new entity's config block as another
            // top-level statement, matching the existing bare statements' shape. This is distinct
            // from "a real DbContext class exists but its OnModelCreating override is missing",
            // which is still an error (see AddEntity_NoOnModelCreatingMethod_Throws) - there we
            // can't tell whether adding a synthesized method is the right fix.
            var bareModelBuilderParamName = FindBareReceiverName(root) ?? "modelBuilder";
            var bareEntityStatement = BuildEntityInvocationStatement(bareModelBuilderParamName, entityName, SyntaxFactory.Block());
            var newBareRoot = root.AddMembers(SyntaxFactory.GlobalStatement(bareEntityStatement));
            return newBareRoot.NormalizeWhitespace().ToFullString();
        }

        var method = FindOnModelCreatingMethod(root);

        var methodBody = method.Body
            ?? throw new InvalidOperationException("OnModelCreating has no method body.");

        var modelBuilderParamName = method.ParameterList.Parameters.Single().Identifier.Text;
        var containingClass = method.Ancestors().OfType<TypeDeclarationSyntax>().First();

        var entityBlockStatement = BuildEntityInvocationStatement(modelBuilderParamName, entityName, SyntaxFactory.Block());
        var newMethodBody = methodBody.AddStatements(entityBlockStatement);
        var classWithNewMethod = containingClass.ReplaceNode(methodBody, newMethodBody);

        var dbSetProperty = BuildDbSetProperty(entityName, dbSetPropertyName);
        var classWithBoth = classWithNewMethod.AddMembers(dbSetProperty);

        var newRoot = root.ReplaceNode(containingClass, classWithBoth);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

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

    private static string InsertRelationshipStatement(CompilationUnitSyntax root, SyntaxNode scope, RelationshipModel relationship)
    {
        var (block, blockReceiverName) = GetScopeBlockAndReceiver(scope);

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

    private static ExpressionSyntax BuildUsingEntityCall(ExpressionSyntax chain, string joinEntityName)
    {
        var methodIdentifier = SyntaxFactory.GenericName(SyntaxFactory.Identifier("UsingEntity"))
            .WithTypeArgumentList(SyntaxFactory.TypeArgumentList(
                SyntaxFactory.SingletonSeparatedList<TypeSyntax>(SyntaxFactory.IdentifierName(joinEntityName))));

        return SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, chain, methodIdentifier),
            SyntaxFactory.ArgumentList());
    }

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

    private static bool HasGenericTypeArgument(InvocationExpressionSyntax call, string typeName)
    {
        return call.Expression is MemberAccessExpressionSyntax { Name: GenericNameSyntax generic }
            && generic.TypeArgumentList.Arguments.Count == 1
            && generic.TypeArgumentList.Arguments[0] is IdentifierNameSyntax { Identifier.Text: var text }
            && text == typeName;
    }

    private static string? TryGetNavigationPropertyName(InvocationExpressionSyntax call)
    {
        var lambdaArgument = call.ArgumentList.Arguments.FirstOrDefault()?.Expression;
        return lambdaArgument switch
        {
            SimpleLambdaExpressionSyntax { Body: MemberAccessExpressionSyntax memberAccess } => memberAccess.Name.Identifier.Text,
            ParenthesizedLambdaExpressionSyntax { Body: MemberAccessExpressionSyntax memberAccess } => memberAccess.Name.Identifier.Text,
            _ => null,
        };
    }

    private static MethodDeclarationSyntax FindOnModelCreatingMethod(CompilationUnitSyntax root)
    {
        return TryFindOnModelCreatingMethod(root)
            ?? throw new InvalidOperationException("No OnModelCreating method found in source.");
    }

    private static MethodDeclarationSyntax? TryFindOnModelCreatingMethod(CompilationUnitSyntax root)
    {
        return root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(m => m.Identifier.Text == "OnModelCreating");
    }

    /// Finds the receiver identifier (e.g. "modelBuilder", "builder") used by any existing
    /// top-level `receiver.Entity&lt;T&gt;(...)` invocation in a bare fluent-config source, so a
    /// newly appended entity statement can match it instead of assuming a hardcoded name.
    /// Returns null when the source has no such invocation at all (e.g. a genuinely empty file).
    private static string? FindBareReceiverName(CompilationUnitSyntax root)
    {
        return root.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(invocation => FluentSyntaxHelpers.GetConfiguredEntityName(invocation) is not null)
            .Select(invocation => ((MemberAccessExpressionSyntax)invocation.Expression).Expression)
            .OfType<IdentifierNameSyntax>()
            .Select(receiver => receiver.Identifier.Text)
            .FirstOrDefault();
    }

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
            if (configureMethod.Body is null)
            {
                throw new InvalidOperationException(
                    $"Cannot insert a new statement into expression-bodied Configure method '{configureMethod.Identifier.Text}'. " +
                    "Rewrite it with a block body before applying this edit.");
            }

            return (configureMethod.Body, configureMethod.ParameterList.Parameters.Single().Identifier.Text);
        }

        if (scope is BlockSyntax { Parent: SimpleLambdaExpressionSyntax simpleBuilderLambda } simpleBuilderBlock)
        {
            // An OwnsOne/OwnsMany/ComplexProperty builder-lambda block, as returned by
            // FindOrCreateOwnedConfigScope — the scope IS the block itself, not an invocation
            // wrapping one, so the receiver name comes from the enclosing lambda's parameter.
            return (simpleBuilderBlock, simpleBuilderLambda.Parameter.Identifier.Text);
        }

        if (scope is BlockSyntax { Parent: ParenthesizedLambdaExpressionSyntax parenthesizedBuilderLambda } parenthesizedBuilderBlock)
        {
            // Same as above, but for a user-authored `(b) => { ... }` builder lambda (parsed source
            // isn't guaranteed to use the simple-lambda form) — single parameter, parenthesized.
            return (parenthesizedBuilderBlock, parenthesizedBuilderLambda.ParameterList.Parameters[0].Identifier.Text);
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

    /// Locates the builder-lambda block of an existing `OwnsOne(nav, builder)`/`OwnsMany(nav, builder)`/
    /// `ComplexProperty(nav, builder)` call for `navPropertyName` within `ownerEntityName`'s own
    /// Entity&lt;T&gt;()/Configure scope(s), or synthesizes one by adding a second lambda argument to a
    /// currently-bare `OwnsOne(nav)`-shaped call if the call exists but has no builder lambda yet.
    /// Mirrors FindConfigScopes/InsertEntityBlock's "find, else synthesize" shape for Entity&lt;T&gt;(), but
    /// targets the call's own builder lambda instead of a top-level Entity&lt;T&gt;() block — a plain
    /// InsertEntityBlock-style `modelBuilder.Entity&lt;Address&gt;(...)` would be wrong here, since Address
    /// isn't a real top-level Entity&lt;T&gt;() target once it's owned/complex-folded.
    /// Returns null if no OwnsOne/OwnsMany/ComplexProperty call targeting `navPropertyName` exists
    /// anywhere in `ownerEntityName`'s scope(s) — callers should surface this as an edit failure
    /// rather than silently no-op'ing.
    private static (SyntaxNode Scope, CompilationUnitSyntax NewRoot)? FindOrCreateOwnedConfigScope(
        CompilationUnitSyntax root, string ownerEntityName, string navPropertyName)
    {
        var ownerScopes = FindConfigScopes(root, ownerEntityName);

        foreach (var callName in new[] { "OwnsOne", "OwnsMany", "ComplexProperty" })
        {
            var call = ownerScopes
                .SelectMany(scope => FluentSyntaxHelpers.FindCallsNamed(scope, callName))
                .FirstOrDefault(c => FluentSyntaxHelpers.TryReadSinglePropertyNameArgument(c) == navPropertyName);

            if (call is null)
            {
                continue;
            }

            var builderLambda = FluentSyntaxHelpers.TryGetFoldingBuilderLambda(call);

            if (builderLambda?.Block is { } existingBlock)
            {
                return (existingBlock, root);
            }

            // Bare OwnsOne(e => e.Foo) — synthesize the second (builder) lambda argument:
            // OwnsOne(e => e.Foo, b => { }).
            var navLambdaParam = call.ArgumentList.Arguments[0].Expression is SimpleLambdaExpressionSyntax navLambda
                ? navLambda.Parameter.Identifier.Text
                : "e";
            var builderParamName = navLambdaParam == "b" ? "builder" : "b";

            var newBlock = SyntaxFactory.Block();
            var newBuilderArgument = SyntaxFactory.Argument(
                SyntaxFactory.SimpleLambdaExpression(
                    SyntaxFactory.Parameter(SyntaxFactory.Identifier(builderParamName)),
                    newBlock));

            var newCall = call.WithArgumentList(
                call.ArgumentList.WithArguments(call.ArgumentList.Arguments.Add(newBuilderArgument)));

            var newRoot = (CompilationUnitSyntax)root.ReplaceNode(call, newCall);

            // Re-locate the block in the new tree (the old `newBlock` node instance isn't part of
            // `newRoot` — ReplaceNode produces fresh nodes throughout the ancestor chain) by
            // re-running the same lookup against it. Find the builder lambda by the same
            // position-based search used above (not a hard-coded `Arguments[1]`) so this stays
            // correct even if the argument list shape ever changes.
            var relocatedCall = FindConfigScopes(newRoot, ownerEntityName)
                .SelectMany(scope => FluentSyntaxHelpers.FindCallsNamed(scope, callName))
                .First(c => FluentSyntaxHelpers.TryReadSinglePropertyNameArgument(c) == navPropertyName);
            var relocatedBlock = FluentSyntaxHelpers.TryGetFoldingBuilderLambda(relocatedCall)?.Block
                ?? throw new InvalidOperationException(
                    "Synthesized builder lambda not found after relocating the call in the new tree.");

            return (relocatedBlock, newRoot);
        }

        return null;
    }

    /// Shared shape behind every `SetXxxOnOwnedProperty` entry point: resolve (or synthesize) the
    /// owner's OwnsOne/OwnsMany/ComplexProperty builder-lambda scope via FindOrCreateOwnedConfigScope,
    /// then either mutate an existing `callName(...)` call already chained onto the target property,
    /// append `callName(...)` onto an existing bare `.Property(...)` call for it, or insert a brand
    /// new `b.Property(x => x.Prop).callName(...)` statement into the builder-lambda block. Mirrors
    /// the non-owned siblings' "mutate existing call, else append to Property call, else insert new
    /// statement" shape (see e.g. SetColumnName/RewriteMaxLength above), but targets the owned
    /// builder-lambda block resolved by FindOrCreateOwnedConfigScope instead of a top-level
    /// Entity&lt;T&gt;() scope — folded owned/complex properties have no such top-level scope to insert
    /// into (there's no `Entity&lt;Address&gt;()` once `Address` is owned/complex-folded). `buildCall`
    /// builds the outer fluent invocation given the property-access expression it's chained onto,
    /// reusing the same Build*Call helpers (BuildStringArgCall, BuildMaxLengthCall, etc.) the
    /// non-owned siblings already use.
    private static string SetOnOwnedProperty(
        string sourceCode, string ownerEntityName, string navPropertyName, string propertyName,
        string callName, Func<ExpressionSyntax, InvocationExpressionSyntax> buildCall)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var resolved = FindOrCreateOwnedConfigScope(root, ownerEntityName, navPropertyName)
            ?? throw new InvalidOperationException(
                $"No OwnsOne/OwnsMany/ComplexProperty call for '{navPropertyName}' found on '{ownerEntityName}'.");

        var (scope, newRoot) = resolved;

        var existingCall = FluentSyntaxHelpers.FindCallsNamed(scope, callName)
            .FirstOrDefault(call => FluentSyntaxHelpers.GetPropertyNameFor(call) == propertyName);

        if (existingCall is not null)
        {
            var existingPropertyCallExpression = ((MemberAccessExpressionSyntax)existingCall.Expression).Expression;
            var mutatedCall = buildCall(existingPropertyCallExpression);
            return newRoot.ReplaceNode(existingCall, mutatedCall).NormalizeWhitespace().ToFullString();
        }

        var existingPropertyCall = FluentSyntaxHelpers.FindCallsNamed(scope, "Property")
            .FirstOrDefault(call => FluentSyntaxHelpers.GetPropertyNameForPropertyCall(call) == propertyName);

        var propertyLambdaParam = FluentSyntaxHelpers.GetPropertyLambdaParameterName(scope);
        var (block, blockReceiverName) = GetScopeBlockAndReceiver(scope);

        ExpressionSyntax propertyCallExpression = existingPropertyCall
            ?? SyntaxFactory.InvocationExpression(
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

        var newFluentCall = buildCall(propertyCallExpression);

        if (existingPropertyCall is not null)
        {
            return newRoot.ReplaceNode(existingPropertyCall, newFluentCall).NormalizeWhitespace().ToFullString();
        }

        var newStatement = SyntaxFactory.ExpressionStatement(newFluentCall);
        var newBlock = block.AddStatements(newStatement);
        return newRoot.ReplaceNode(block, newBlock).NormalizeWhitespace().ToFullString();
    }

    /// Shared shape behind every `RemoveXxxOnOwnedProperty` entry point: locates `callName(...)`
    /// chained onto `propertyName` within the owner's owned/complex builder-lambda scope (resolved via
    /// the same `FindOrCreateOwnedConfigScope` the `Set*` path uses) and unwraps it back to the bare
    /// `.Property(...)` call, mirroring RemoveStringArgCall/RemoveBareMarkerCall's non-owned shape.
    /// No-ops (returns `sourceCode` unchanged, discarding whatever `FindOrCreateOwnedConfigScope`
    /// resolved or synthesized) both when the owner has no OwnsOne/OwnsMany/ComplexProperty call for
    /// `navPropertyName` at all, and when that scope exists but has no `callName(...)` call for
    /// `propertyName` to remove — this deliberately throws away a builder lambda
    /// `FindOrCreateOwnedConfigScope` may have just synthesized on a bare `OwnsOne(nav)` call, rather
    /// than returning a source that gained an empty `, b => { }` for no actual removal.
    private static string RemoveOnOwnedProperty(
        string sourceCode, string ownerEntityName, string navPropertyName, string propertyName, string callName)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var resolved = FindOrCreateOwnedConfigScope(root, ownerEntityName, navPropertyName);
        if (resolved is null)
        {
            return sourceCode;
        }

        var (scope, newRoot) = resolved.Value;

        var existingCall = FluentSyntaxHelpers.FindCallsNamed(scope, callName)
            .FirstOrDefault(call => FluentSyntaxHelpers.GetPropertyNameFor(call) == propertyName);

        if (existingCall is null)
        {
            return sourceCode;
        }

        var propertyCallExpression = ((MemberAccessExpressionSyntax)existingCall.Expression).Expression;

        return newRoot.ReplaceNode(existingCall, propertyCallExpression).NormalizeWhitespace().ToFullString();
    }

    public string SetColumnNameOnOwnedProperty(
        string sourceCode, string ownerEntityName, string navPropertyName, string propertyName, string columnName) =>
        SetOnOwnedProperty(sourceCode, ownerEntityName, navPropertyName, propertyName, "HasColumnName",
            expr => BuildStringArgCall(expr, "HasColumnName", columnName));

    public string RemoveColumnNameOnOwnedProperty(
        string sourceCode, string ownerEntityName, string navPropertyName, string propertyName) =>
        RemoveOnOwnedProperty(sourceCode, ownerEntityName, navPropertyName, propertyName, "HasColumnName");

    public string SetColumnTypeOnOwnedProperty(
        string sourceCode, string ownerEntityName, string navPropertyName, string propertyName, string columnType) =>
        SetOnOwnedProperty(sourceCode, ownerEntityName, navPropertyName, propertyName, "HasColumnType",
            expr => BuildStringArgCall(expr, "HasColumnType", columnType));

    public string RemoveColumnTypeOnOwnedProperty(
        string sourceCode, string ownerEntityName, string navPropertyName, string propertyName) =>
        RemoveOnOwnedProperty(sourceCode, ownerEntityName, navPropertyName, propertyName, "HasColumnType");

    public string SetMaxLengthOnOwnedProperty(
        string sourceCode, string ownerEntityName, string navPropertyName, string propertyName, int maxLength) =>
        SetOnOwnedProperty(sourceCode, ownerEntityName, navPropertyName, propertyName, "HasMaxLength",
            expr => BuildMaxLengthCall(expr, maxLength));

    public string RemoveMaxLengthOnOwnedProperty(
        string sourceCode, string ownerEntityName, string navPropertyName, string propertyName) =>
        RemoveOnOwnedProperty(sourceCode, ownerEntityName, navPropertyName, propertyName, "HasMaxLength");

    public string SetPrecisionOnOwnedProperty(
        string sourceCode, string ownerEntityName, string navPropertyName, string propertyName, int precision, int? scale) =>
        SetOnOwnedProperty(sourceCode, ownerEntityName, navPropertyName, propertyName, "HasPrecision",
            expr => BuildPrecisionCall(expr, precision, scale));

    public string RemovePrecisionOnOwnedProperty(
        string sourceCode, string ownerEntityName, string navPropertyName, string propertyName) =>
        RemoveOnOwnedProperty(sourceCode, ownerEntityName, navPropertyName, propertyName, "HasPrecision");

    public string SetIsRequiredOnOwnedProperty(
        string sourceCode, string ownerEntityName, string navPropertyName, string propertyName, bool isRequired) =>
        SetOnOwnedProperty(sourceCode, ownerEntityName, navPropertyName, propertyName, "IsRequired",
            expr => BuildIsRequiredCall(expr, isRequired));

    public string RemoveIsRequiredOnOwnedProperty(
        string sourceCode, string ownerEntityName, string navPropertyName, string propertyName) =>
        RemoveOnOwnedProperty(sourceCode, ownerEntityName, navPropertyName, propertyName, "IsRequired");

    public string SetDefaultValueOnOwnedProperty(
        string sourceCode, string ownerEntityName, string navPropertyName, string propertyName, string literalText) =>
        SetOnOwnedProperty(sourceCode, ownerEntityName, navPropertyName, propertyName, "HasDefaultValue",
            expr => BuildDefaultValueCall(expr, literalText));

    public string RemoveDefaultValueOnOwnedProperty(
        string sourceCode, string ownerEntityName, string navPropertyName, string propertyName) =>
        RemoveOnOwnedProperty(sourceCode, ownerEntityName, navPropertyName, propertyName, "HasDefaultValue");

    public string SetDefaultValueSqlOnOwnedProperty(
        string sourceCode, string ownerEntityName, string navPropertyName, string propertyName, string sql) =>
        SetOnOwnedProperty(sourceCode, ownerEntityName, navPropertyName, propertyName, "HasDefaultValueSql",
            expr => BuildStringArgCall(expr, "HasDefaultValueSql", sql));

    public string RemoveDefaultValueSqlOnOwnedProperty(
        string sourceCode, string ownerEntityName, string navPropertyName, string propertyName) =>
        RemoveOnOwnedProperty(sourceCode, ownerEntityName, navPropertyName, propertyName, "HasDefaultValueSql");

    public string SetComputedColumnSqlOnOwnedProperty(
        string sourceCode, string ownerEntityName, string navPropertyName, string propertyName, string sql, bool? isStored) =>
        SetOnOwnedProperty(sourceCode, ownerEntityName, navPropertyName, propertyName, "HasComputedColumnSql",
            expr => BuildStringArgCall(expr, "HasComputedColumnSql", sql, isStored));

    public string RemoveComputedColumnSqlOnOwnedProperty(
        string sourceCode, string ownerEntityName, string navPropertyName, string propertyName) =>
        RemoveOnOwnedProperty(sourceCode, ownerEntityName, navPropertyName, propertyName, "HasComputedColumnSql");

    public string SetValueConversionOnOwnedProperty(
        string sourceCode, string ownerEntityName, string navPropertyName, string propertyName, string providerClrType) =>
        SetOnOwnedProperty(sourceCode, ownerEntityName, navPropertyName, propertyName, "HasConversion",
            expr => BuildTypeArgCall(expr, providerClrType));

    public string RemoveValueConversionOnOwnedProperty(
        string sourceCode, string ownerEntityName, string navPropertyName, string propertyName) =>
        RemoveOnOwnedProperty(sourceCode, ownerEntityName, navPropertyName, propertyName, "HasConversion");

    public string SetUseSequenceOnOwnedProperty(
        string sourceCode, string ownerEntityName, string navPropertyName, string propertyName, string sequenceName, string? schema) =>
        SetOnOwnedProperty(sourceCode, ownerEntityName, navPropertyName, propertyName, "UseSequence",
            expr => BuildUseSequenceCall(expr, sequenceName, schema));

    public string RemoveUseSequenceOnOwnedProperty(
        string sourceCode, string ownerEntityName, string navPropertyName, string propertyName) =>
        RemoveOnOwnedProperty(sourceCode, ownerEntityName, navPropertyName, propertyName, "UseSequence");

    public string SetRowVersionOnOwnedProperty(
        string sourceCode, string ownerEntityName, string navPropertyName, string propertyName) =>
        SetOnOwnedProperty(sourceCode, ownerEntityName, navPropertyName, propertyName, "IsRowVersion",
            expr => BuildBareMarkerCall(expr, "IsRowVersion"));

    public string RemoveRowVersionOnOwnedProperty(
        string sourceCode, string ownerEntityName, string navPropertyName, string propertyName) =>
        RemoveOnOwnedProperty(sourceCode, ownerEntityName, navPropertyName, propertyName, "IsRowVersion");

    public string SetConcurrencyTokenOnOwnedProperty(
        string sourceCode, string ownerEntityName, string navPropertyName, string propertyName) =>
        SetOnOwnedProperty(sourceCode, ownerEntityName, navPropertyName, propertyName, "IsConcurrencyToken",
            expr => BuildBareMarkerCall(expr, "IsConcurrencyToken"));

    public string RemoveConcurrencyTokenOnOwnedProperty(
        string sourceCode, string ownerEntityName, string navPropertyName, string propertyName) =>
        RemoveOnOwnedProperty(sourceCode, ownerEntityName, navPropertyName, propertyName, "IsConcurrencyToken");

    private static ExpressionStatementSyntax BuildEntityInvocationStatement(string modelBuilderParamName, string entityName, BlockSyntax block)
    {
        return SyntaxFactory.ExpressionStatement(
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
                                block))))));
    }

    private static PropertyDeclarationSyntax BuildDbSetProperty(string entityName, string dbSetPropertyName)
    {
        var dbSetType = SyntaxFactory.GenericName(SyntaxFactory.Identifier("DbSet"))
            .WithTypeArgumentList(
                SyntaxFactory.TypeArgumentList(
                    SyntaxFactory.SingletonSeparatedList<TypeSyntax>(
                        SyntaxFactory.IdentifierName(entityName))));

        return SyntaxFactory.PropertyDeclaration(dbSetType, dbSetPropertyName)
            .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword))
            .AddAccessorListAccessors(
                SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                    .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)),
                SyntaxFactory.AccessorDeclaration(SyntaxKind.SetAccessorDeclaration)
                    .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)));
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

    public string SetRowVersion(string sourceCode, string entityName, string propertyName) =>
        SetBareMarkerCall(sourceCode, entityName, propertyName, "IsRowVersion");

    public string RemoveRowVersion(string sourceCode, string entityName, string propertyName) =>
        RemoveBareMarkerCall(sourceCode, entityName, propertyName, "IsRowVersion");

    public string SetConcurrencyToken(string sourceCode, string entityName, string propertyName) =>
        SetBareMarkerCall(sourceCode, entityName, propertyName, "IsConcurrencyToken");

    public string RemoveConcurrencyToken(string sourceCode, string entityName, string propertyName) =>
        RemoveBareMarkerCall(sourceCode, entityName, propertyName, "IsConcurrencyToken");

    /// Idempotently ensures a bare, no-argument fluent call (e.g. `.IsRowVersion()`) is chained onto
    /// the given property's `.Property(...)` call. Shared by SetRowVersion/SetConcurrencyToken since
    /// both are structurally identical bare property-scoped markers.
    private static string SetBareMarkerCall(string sourceCode, string entityName, string propertyName, string callName)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var scopes = FindConfigScopes(root, entityName);

        var existingCall = scopes
            .SelectMany(scope => FluentSyntaxHelpers.FindCallsNamed(scope, callName))
            .FirstOrDefault(call => FluentSyntaxHelpers.GetPropertyNameFor(call) == propertyName);

        if (existingCall is not null)
        {
            return sourceCode;
        }

        var existingPropertyCall = scopes
            .SelectMany(scope => FluentSyntaxHelpers.FindCallsNamed(scope, "Property"))
            .FirstOrDefault(call => FluentSyntaxHelpers.GetPropertyNameForPropertyCall(call) == propertyName);

        if (existingPropertyCall is not null)
        {
            var markerCall = BuildBareMarkerCall(existingPropertyCall, callName);
            var newRoot = root.ReplaceNode(existingPropertyCall, markerCall);
            return newRoot.NormalizeWhitespace().ToFullString();
        }

        var existingScope = scopes.FirstOrDefault();

        if (existingScope is not null)
        {
            var (block, blockReceiverName) = GetScopeBlockAndReceiver(existingScope);
            var propertyLambdaParam = FluentSyntaxHelpers.GetPropertyLambdaParameterName(existingScope);

            var newStatement = BuildBareMarkerPropertyStatement(blockReceiverName, propertyLambdaParam, propertyName, callName);
            var newBlock = block.AddStatements(newStatement);

            var newRoot = root.ReplaceNode(block, newBlock);
            return newRoot.NormalizeWhitespace().ToFullString();
        }

        var method = FindOnModelCreatingMethod(root);
        var methodBody = method.Body
            ?? throw new InvalidOperationException("OnModelCreating has no method body.");
        var modelBuilderParamName = method.ParameterList.Parameters.Single().Identifier.Text;

        var propertyStatement = BuildBareMarkerPropertyStatement("entity", "e", propertyName, callName);
        var entityBlockStatement = BuildEntityInvocationStatement(modelBuilderParamName, entityName, SyntaxFactory.Block(propertyStatement));

        var newMethodBody = methodBody.AddStatements(entityBlockStatement);
        var finalRoot = root.ReplaceNode(methodBody, newMethodBody);
        return finalRoot.NormalizeWhitespace().ToFullString();
    }

    private static InvocationExpressionSyntax BuildBareMarkerCall(ExpressionSyntax propertyCallExpression, string callName)
    {
        return SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                propertyCallExpression,
                SyntaxFactory.IdentifierName(callName)),
            SyntaxFactory.ArgumentList());
    }

    private static ExpressionStatementSyntax BuildBareMarkerPropertyStatement(
        string blockReceiverName, string propertyLambdaParam, string propertyName, string callName)
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

        return SyntaxFactory.ExpressionStatement(BuildBareMarkerCall(propertyCall, callName));
    }

    /// Removes a bare, no-argument fluent call (e.g. `.IsRowVersion()`) chained onto a property's
    /// `.Property(...)` call, unwrapping back to the bare property call. No-ops if absent.
    private static string RemoveBareMarkerCall(string sourceCode, string entityName, string propertyName, string callName)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var scopes = FindConfigScopes(root, entityName);

        var existingCall = scopes
            .SelectMany(scope => FluentSyntaxHelpers.FindCallsNamed(scope, callName))
            .FirstOrDefault(call => FluentSyntaxHelpers.GetPropertyNameFor(call) == propertyName);

        if (existingCall is null)
        {
            return sourceCode;
        }

        var propertyCallExpression = ((MemberAccessExpressionSyntax)existingCall.Expression).Expression;

        var newRoot = root.ReplaceNode(existingCall, propertyCallExpression);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

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

        // `HasData(new Person { ... })` seed rows aren't otherwise tracked by the parser, but the
        // object-creation type name still needs to follow an entity rename or the regenerated source
        // won't compile (referencing a class that no longer exists).
        foreach (var hasDataCall in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (hasDataCall.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "HasData" })
            {
                continue;
            }

            foreach (var objectCreation in hasDataCall.ArgumentList.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
            {
                if (objectCreation.Type is IdentifierNameSyntax { Identifier.Text: var seedTypeName } typeName
                    && seedTypeName == oldEntityName)
                {
                    targets.Add(typeName);
                }
            }
        }

        if (targets.Count == 0)
        {
            return sourceCode;
        }

        var newRoot = root.ReplaceNodes(targets, (_, _) => SyntaxFactory.IdentifierName(newEntityName));
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    /// Renaming an owner's navigation property (e.g. `Order.ShippingAddress` -> `Order.DeliveryAddress`)
    /// must also patch the outer `OwnsOne(e => e.ShippingAddress, ...)` call's lambda parameter, not
    /// just the property declaration on Order's class — `RenamePropertyReferences` only rewrites
    /// `Property(e => e.X)`-shaped calls and doesn't know to look here. No-ops (returns `sourceCode`
    /// unchanged) if no OwnsOne/OwnsMany/ComplexProperty call targets `oldNavName` on `ownerEntityName`.
    public string RenameOwnedNavigationReference(
        string sourceCode, string ownerEntityName, string oldNavName, string newNavName)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var scopes = FindConfigScopes(root, ownerEntityName);

        foreach (var callName in new[] { "OwnsOne", "OwnsMany", "ComplexProperty" })
        {
            var call = scopes
                .SelectMany(scope => FluentSyntaxHelpers.FindCallsNamed(scope, callName))
                .FirstOrDefault(c => FluentSyntaxHelpers.TryReadSinglePropertyNameArgument(c) == oldNavName);

            if (call is null)
            {
                continue;
            }

            var navArgument = call.ArgumentList.Arguments[0];
            var argumentExpression = navArgument.Expression;

            ArgumentSyntax newArgument;

            // Both SimpleLambdaExpressionSyntax (`e => e.X`) and ParenthesizedLambdaExpressionSyntax
            // (`(e) => e.X`) are valid, idiomatic shapes a real nav-selector lambda can take —
            // TryReadSinglePropertyNameArgument above already resolves both when finding `call`, so
            // the rewrite here must handle both too, or a parenthesized-lambda call would silently
            // fall through to `continue` below and leave the config unchanged while the class
            // declaration has already been renamed by the caller (DiagramEditor.RenameProperty),
            // producing a Success=true result with mismatched, non-compiling sources.
            if (argumentExpression is LambdaExpressionSyntax { ExpressionBody: MemberAccessExpressionSyntax expressionBodyAccess } exprLambda)
            {
                var newLambda = exprLambda.WithExpressionBody(expressionBodyAccess.WithName(SyntaxFactory.IdentifierName(newNavName)));
                newArgument = navArgument.WithExpression(newLambda);
            }
            else if (argumentExpression is LambdaExpressionSyntax { Block: BlockSyntax block } blockLambda
                && block.Statements is [ReturnStatementSyntax { Expression: MemberAccessExpressionSyntax blockAccess } returnStatement])
            {
                var newReturnStatement = returnStatement.WithExpression(blockAccess.WithName(SyntaxFactory.IdentifierName(newNavName)));
                var newBlock = block.WithStatements(SyntaxFactory.SingletonList<StatementSyntax>(newReturnStatement));
                var newLambda = blockLambda.WithBlock(newBlock);
                newArgument = navArgument.WithExpression(newLambda);
            }
            else if (argumentExpression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression))
            {
                var newLiteral = SyntaxFactory.LiteralExpression(
                    SyntaxKind.StringLiteralExpression,
                    SyntaxFactory.Literal(newNavName));
                newArgument = navArgument.WithExpression(newLiteral);
            }
            else
            {
                // Should be unreachable: TryReadSinglePropertyNameArgument only matched `call` above
                // because it recognized this same argument shape. Throwing here (rather than silently
                // `continue`-ing past this call) avoids ever reporting Success with a stale nav
                // reference left in the config — see the Important review finding this guards against.
                throw new InvalidOperationException(
                    $"Unsupported navigation-selector argument shape for '{oldNavName}' in '{callName}' call.");
            }

            var newCall = call.WithArgumentList(
                call.ArgumentList.WithArguments(call.ArgumentList.Arguments.Replace(navArgument, newArgument)));

            var newRoot = root.ReplaceNode(call, newCall);
            return newRoot.NormalizeWhitespace().ToFullString();
        }

        return sourceCode;
    }

    public string RenamePropertyReferences(string sourceCode, string entityName, string oldPropertyName, string newPropertyName)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var scopes = FindConfigScopes(root, entityName);

        return RenamePropertyReferencesInScopes(root, scopes, oldPropertyName, newPropertyName) ?? sourceCode;
    }

    /// Same job as `RenamePropertyReferences`, but for a property folded in from an owned/complex
    /// type: `Property(a => a.OldName)`-shaped calls live inside the OWNER's OwnsOne/OwnsMany/
    /// ComplexProperty builder-lambda scope (resolved via the same `FindOrCreateOwnedConfigScope`
    /// `SetOnOwnedProperty`/`RemoveOnOwnedProperty` already use), not in a top-level Entity&lt;T&gt;()
    /// scope — there is no such scope to search once the declaring type is folded away. Discards
    /// (returns `sourceCode` unchanged) both when `navPropertyName` has no owning call at all, and
    /// when that call's scope exists but has no `Property(...)` reference to `oldPropertyName` to
    /// rename — mirroring RemoveOnOwnedProperty's discard-if-nothing-to-do behavior, so a bare
    /// `OwnsOne(nav)` call that FindOrCreateOwnedConfigScope had to synthesize a builder lambda for
    /// doesn't get written back as an empty `, b => { }` for no actual rename.
    public string RenamePropertyReferencesInOwnedScope(
        string sourceCode, string ownerEntityName, string navPropertyName, string oldPropertyName, string newPropertyName)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var resolved = FindOrCreateOwnedConfigScope(root, ownerEntityName, navPropertyName);
        if (resolved is null)
        {
            return sourceCode;
        }

        var (scope, newRoot) = resolved.Value;

        return RenamePropertyReferencesInScopes(newRoot, new[] { scope }, oldPropertyName, newPropertyName) ?? sourceCode;
    }

    /// Core rewrite shared by `RenamePropertyReferences` and `RenamePropertyReferencesInOwnedScope`:
    /// finds the `Property(...)` call within `scopes` selecting `oldPropertyName` and rewrites its
    /// selector argument to `newPropertyName`, preserving whichever of the argument's supported
    /// shapes (expression-bodied lambda, block-bodied lambda, or string literal) it already used.
    /// Returns null when no matching `Property(...)` call is found in any of `scopes`.
    private static string? RenamePropertyReferencesInScopes(
        CompilationUnitSyntax root, IEnumerable<SyntaxNode> scopes, string oldPropertyName, string newPropertyName)
    {
        var existingPropertyCall = scopes
            .SelectMany(scope => FluentSyntaxHelpers.FindCallsNamed(scope, "Property"))
            .FirstOrDefault(call => FluentSyntaxHelpers.GetPropertyNameForPropertyCall(call) == oldPropertyName);

        if (existingPropertyCall is null)
        {
            return null;
        }

        var argumentExpression = existingPropertyCall.ArgumentList.Arguments.Single().Expression;

        ArgumentSyntax newArgument;

        if (argumentExpression is SimpleLambdaExpressionSyntax { ExpressionBody: MemberAccessExpressionSyntax expressionBodyAccess } exprLambda)
        {
            var newLambda = exprLambda.WithExpressionBody(expressionBodyAccess.WithName(SyntaxFactory.IdentifierName(newPropertyName)));
            newArgument = SyntaxFactory.Argument(newLambda);
        }
        else if (argumentExpression is SimpleLambdaExpressionSyntax { Block: BlockSyntax block } blockLambda
            && block.Statements is [ReturnStatementSyntax { Expression: MemberAccessExpressionSyntax blockAccess } returnStatement])
        {
            var newReturnStatement = returnStatement.WithExpression(blockAccess.WithName(SyntaxFactory.IdentifierName(newPropertyName)));
            var newBlock = block.WithStatements(SyntaxFactory.SingletonList<StatementSyntax>(newReturnStatement));
            var newLambda = blockLambda.WithBlock(newBlock);
            newArgument = SyntaxFactory.Argument(newLambda);
        }
        else if (argumentExpression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression))
        {
            var newLiteral = SyntaxFactory.LiteralExpression(
                SyntaxKind.StringLiteralExpression,
                SyntaxFactory.Literal(newPropertyName));
            newArgument = SyntaxFactory.Argument(newLiteral);
        }
        else
        {
            throw new InvalidOperationException($"Unsupported Property() argument shape for '{oldPropertyName}'.");
        }

        var newCall = existingPropertyCall.WithArgumentList(
            existingPropertyCall.ArgumentList.WithArguments(SyntaxFactory.SingletonSeparatedList(newArgument)));

        var newRoot = root.ReplaceNode(existingPropertyCall, newCall);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    public string SetIndex(
        string sourceCode,
        string entityName,
        IReadOnlyList<string> propertyNames,
        bool isUnique,
        string? name = null,
        string? filter = null,
        IReadOnlyList<bool>? isDescending = null,
        IReadOnlyList<string>? includeProperties = null)
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
            return MutateExistingIndex(root, existingHasIndexCall, propertyNames, isUnique, name, filter, isDescending, includeProperties);
        }

        var existingScope = scopes.FirstOrDefault();

        if (existingScope is not null)
        {
            return InsertIndexStatement(root, existingScope, propertyNames, isUnique, name, filter, isDescending, includeProperties);
        }

        return InsertIndexEntityBlock(root, entityName, propertyNames, isUnique, name, filter, isDescending, includeProperties);
    }

    private static string MutateExistingIndex(
        CompilationUnitSyntax root,
        InvocationExpressionSyntax hasIndexCall,
        IReadOnlyList<string> propertyNames,
        bool isUnique,
        string? name,
        string? filter,
        IReadOnlyList<bool>? isDescending,
        IReadOnlyList<string>? includeProperties)
    {
        var blockReceiverName = ((MemberAccessExpressionSyntax)hasIndexCall.Expression).Expression.ToString();
        var existingStatement = hasIndexCall.Ancestors().OfType<ExpressionStatementSyntax>().First();
        var newStatement = BuildHasIndexStatement(blockReceiverName, propertyNames, isUnique, name, filter, isDescending, includeProperties);

        var newRoot = root.ReplaceNode(existingStatement, newStatement);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    private static string InsertIndexStatement(
        CompilationUnitSyntax root,
        SyntaxNode scope,
        IReadOnlyList<string> propertyNames,
        bool isUnique,
        string? name,
        string? filter,
        IReadOnlyList<bool>? isDescending,
        IReadOnlyList<string>? includeProperties)
    {
        var (block, blockReceiverName) = GetScopeBlockAndReceiver(scope);

        var newStatement = BuildHasIndexStatement(blockReceiverName, propertyNames, isUnique, name, filter, isDescending, includeProperties);
        var newBlock = block.AddStatements(newStatement);

        var newRoot = root.ReplaceNode(block, newBlock);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    private static string InsertIndexEntityBlock(
        CompilationUnitSyntax root,
        string entityName,
        IReadOnlyList<string> propertyNames,
        bool isUnique,
        string? name,
        string? filter,
        IReadOnlyList<bool>? isDescending,
        IReadOnlyList<string>? includeProperties)
    {
        var method = FindOnModelCreatingMethod(root);

        var methodBody = method.Body
            ?? throw new InvalidOperationException("OnModelCreating has no method body.");

        var modelBuilderParamName = method.ParameterList.Parameters.Single().Identifier.Text;

        var indexStatement = BuildHasIndexStatement("entity", propertyNames, isUnique, name, filter, isDescending, includeProperties);
        var entityBlockStatement = BuildEntityInvocationStatement(
            modelBuilderParamName, entityName, SyntaxFactory.Block(indexStatement));

        var newMethodBody = methodBody.AddStatements(entityBlockStatement);
        var newRoot = root.ReplaceNode(methodBody, newMethodBody);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    private static ExpressionStatementSyntax BuildHasIndexStatement(
        string blockReceiverName,
        IReadOnlyList<string> propertyNames,
        bool isUnique,
        string? name,
        string? filter,
        IReadOnlyList<bool>? isDescending,
        IReadOnlyList<string>? includeProperties)
    {
        ExpressionSyntax expression = SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                SyntaxFactory.IdentifierName(blockReceiverName),
                SyntaxFactory.IdentifierName("HasIndex")),
            BuildHasIndexArgumentList(propertyNames, name));

        if (isUnique)
        {
            expression = ChainBareCall(expression, "IsUnique");
        }

        if (filter is not null)
        {
            expression = ChainCall(expression, "HasFilter", SyntaxFactory.Argument(
                SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(filter))));
        }

        if (includeProperties is { Count: > 0 })
        {
            expression = ChainCall(expression, "IncludeProperties", BuildHasIndexArgumentList(includeProperties, name: null).Arguments.ToArray());
        }

        if (isDescending is not null)
        {
            var boolArgs = isDescending
                .Select(d => SyntaxFactory.Argument(SyntaxFactory.LiteralExpression(
                    d ? SyntaxKind.TrueLiteralExpression : SyntaxKind.FalseLiteralExpression)))
                .ToArray();
            expression = ChainCall(expression, "IsDescending", boolArgs);
        }

        return SyntaxFactory.ExpressionStatement(expression);
    }

    private static ExpressionSyntax ChainBareCall(ExpressionSyntax expression, string methodName)
    {
        return SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                expression,
                SyntaxFactory.IdentifierName(methodName)),
            SyntaxFactory.ArgumentList());
    }

    private static ExpressionSyntax ChainCall(ExpressionSyntax expression, string methodName, params ArgumentSyntax[] arguments)
    {
        return SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                expression,
                SyntaxFactory.IdentifierName(methodName)),
            SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(arguments)));
    }

    private static ArgumentListSyntax BuildHasIndexArgumentList(IReadOnlyList<string> propertyNames, string? name)
    {
        const string lambdaParam = "e";

        ExpressionSyntax body = propertyNames.Count == 1
            ? SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                SyntaxFactory.IdentifierName(lambdaParam),
                SyntaxFactory.IdentifierName(propertyNames[0]))
            : SyntaxFactory.AnonymousObjectCreationExpression(
                SyntaxFactory.SeparatedList(
                    propertyNames.Select(n => SyntaxFactory.AnonymousObjectMemberDeclarator(
                        SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            SyntaxFactory.IdentifierName(lambdaParam),
                            SyntaxFactory.IdentifierName(n))))));

        var lambdaArg = SyntaxFactory.Argument(
            SyntaxFactory.SimpleLambdaExpression(
                SyntaxFactory.Parameter(SyntaxFactory.Identifier(lambdaParam)),
                body));

        if (name is not null)
        {
            return SyntaxFactory.ArgumentList(
                SyntaxFactory.SeparatedList(new[]
                {
                    lambdaArg,
                    SyntaxFactory.Argument(
                        SyntaxFactory.LiteralExpression(
                            SyntaxKind.StringLiteralExpression,
                            SyntaxFactory.Literal(name)))
                }));
        }

        return SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(lambdaArg));
    }

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
                // Bare top-level fluent statements (no wrapping class/method) are
                // parsed as GlobalStatementSyntax. Removing only the inner
                // ExpressionStatementSyntax leaves the GlobalStatementSyntax with a
                // null Statement child, which Roslyn rejects.
                nodesToRemove.Add(statement.Parent is GlobalStatementSyntax globalStatement
                    ? globalStatement
                    : statement);
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

    public string SetSequence(
        string sourceCode, string name, string? schema, string? clrType,
        long? startsAt, int? incrementsBy, long? minValue, long? maxValue, bool? isCyclic)
    {
        var withoutExisting = RemoveSequence(sourceCode, name);

        var tree = CSharpSyntaxTree.ParseText(withoutExisting);
        var root = tree.GetCompilationUnitRoot();

        if (!root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>().Any())
        {
            // Bare fluent-config source: just top-level `modelBuilder.HasSequence(...)`-shaped
            // statements, with no wrapping OnModelCreating method or DbContext class at all - the
            // form the app's own sample data and pasted-snippet workflow both use (see
            // AddEntity's identical bare-config branch). There's no method body to append into,
            // so append the new sequence statement as another top-level statement instead. This
            // is distinct from "a real class exists (e.g. an IEntityTypeConfiguration<T> config
            // class) but has no OnModelCreating method" - there we can't tell whether adding a
            // synthesized method is the right fix, so we fall through and let
            // FindOnModelCreatingMethod throw below (see AddEntity's identical distinction).
            var bareModelBuilderParamName = FindBareReceiverName(root) ?? "modelBuilder";
            var bareStatement = SyntaxFactory.ExpressionStatement(
                BuildSequenceExpression(bareModelBuilderParamName, name, schema, clrType, startsAt, incrementsBy, minValue, maxValue, isCyclic));
            var newBareRoot = root.AddMembers(SyntaxFactory.GlobalStatement(bareStatement));
            return newBareRoot.NormalizeWhitespace().ToFullString();
        }

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

        // Bare top-level fluent statements (no wrapping class/method) are parsed as
        // GlobalStatementSyntax. Removing only the inner ExpressionStatementSyntax leaves the
        // GlobalStatementSyntax with a null Statement child, which Roslyn rejects (see
        // RemoveEntity's identical fix above).
        SyntaxNode nodeToRemove = statement.Parent is GlobalStatementSyntax globalStatement
            ? globalStatement
            : statement;

        var newRoot = root.RemoveNode(nodeToRemove, SyntaxRemoveOptions.KeepNoTrivia)!;
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
            expression = ChainCall(expression, "StartsAt", SyntaxFactory.Argument(BuildLongLiteral(startsAt.Value)));
        }

        if (incrementsBy is not null)
        {
            expression = ChainCall(expression, "IncrementsBy", SyntaxFactory.Argument(
                SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(incrementsBy.Value))));
        }

        if (minValue is not null)
        {
            expression = ChainCall(expression, "HasMin", SyntaxFactory.Argument(BuildLongLiteral(minValue.Value)));
        }

        if (maxValue is not null)
        {
            expression = ChainCall(expression, "HasMax", SyntaxFactory.Argument(BuildLongLiteral(maxValue.Value)));
        }

        if (isCyclic == true)
        {
            expression = ChainBareCall(expression, "IsCyclic");
        }

        return expression;
    }

    /// Builds a numeric literal for a `long` argument without the `L` suffix that
    /// `SyntaxFactory.Literal(long)` appends by default (e.g. `1000` rather than `1000L`),
    /// matching the plain-integer style the rest of this rewriter emits. A suffix-free decimal
    /// literal is still valid C# for values beyond `int.Max`/`uint.Max` — the compiler assigns it
    /// the first of int/uint/long/ulong that fits — so this is safe for the full `long` range.
    private static LiteralExpressionSyntax BuildLongLiteral(long value)
    {
        return SyntaxFactory.LiteralExpression(
            SyntaxKind.NumericLiteralExpression,
            SyntaxFactory.Literal(value.ToString(System.Globalization.CultureInfo.InvariantCulture), value));
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
}
