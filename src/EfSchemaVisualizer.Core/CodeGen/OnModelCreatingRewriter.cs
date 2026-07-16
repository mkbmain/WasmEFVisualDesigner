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

    public string SetKey(string sourceCode, string entityName, IReadOnlyList<string> propertyNames)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var entityInvocations = FluentSyntaxHelpers.FindEntityConfigInvocations(root, entityName).ToList();

        var existingHasKeyCall = entityInvocations
            .SelectMany(entityInvocation => FluentSyntaxHelpers.FindCallsNamed(entityInvocation, "HasKey"))
            .FirstOrDefault();

        if (existingHasKeyCall is not null)
        {
            return MutateExistingKey(root, existingHasKeyCall, propertyNames);
        }

        var existingEntityInvocation = entityInvocations.FirstOrDefault();

        if (existingEntityInvocation is not null)
        {
            return InsertKeyStatement(root, existingEntityInvocation, propertyNames);
        }

        return InsertKeyEntityBlock(root, entityName, propertyNames);
    }

    private static string MutateExistingKey(CompilationUnitSyntax root, InvocationExpressionSyntax targetCall, IReadOnlyList<string> propertyNames)
    {
        var newCall = targetCall.WithArgumentList(BuildHasKeyArgumentList(propertyNames));

        var newRoot = root.ReplaceNode(targetCall, newCall);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    private static string InsertKeyStatement(CompilationUnitSyntax root, InvocationExpressionSyntax entityInvocation, IReadOnlyList<string> propertyNames)
    {
        var lambda = (SimpleLambdaExpressionSyntax)entityInvocation.ArgumentList.Arguments.Single().Expression;
        var block = lambda.Block!;
        var blockReceiverName = lambda.Parameter.Identifier.Text;

        var newStatement = BuildHasKeyStatement(blockReceiverName, propertyNames);
        var newBlock = block.AddStatements(newStatement);

        var newRoot = root.ReplaceNode(block, newBlock);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    private static string InsertKeyEntityBlock(CompilationUnitSyntax root, string entityName, IReadOnlyList<string> propertyNames)
    {
        var method = FindOnModelCreatingMethod(root);

        var methodBody = method.Body
            ?? throw new InvalidOperationException("OnModelCreating has no method body.");

        var modelBuilderParamName = method.ParameterList.Parameters.Single().Identifier.Text;

        var keyStatement = BuildHasKeyStatement("entity", propertyNames);
        var entityBlockStatement = BuildEntityInvocationStatement(modelBuilderParamName, entityName, SyntaxFactory.Block(keyStatement));

        var newMethodBody = methodBody.AddStatements(entityBlockStatement);
        var newRoot = root.ReplaceNode(methodBody, newMethodBody);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    private static ExpressionStatementSyntax BuildHasKeyStatement(string blockReceiverName, IReadOnlyList<string> propertyNames)
    {
        return SyntaxFactory.ExpressionStatement(
            SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.IdentifierName(blockReceiverName),
                    SyntaxFactory.IdentifierName("HasKey")),
                BuildHasKeyArgumentList(propertyNames)));
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

        var entityInvocations = FluentSyntaxHelpers.FindEntityConfigInvocations(root, entityName).ToList();

        var existingHasKeyCall = entityInvocations
            .SelectMany(entityInvocation => FluentSyntaxHelpers.FindCallsNamed(entityInvocation, "HasKey"))
            .FirstOrDefault();

        if (existingHasKeyCall is null || existingHasKeyCall.Parent is not ExpressionStatementSyntax statement)
        {
            return sourceCode;
        }

        var newRoot = root.RemoveNode(statement, SyntaxRemoveOptions.KeepNoTrivia)!;
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    public string SetTable(string sourceCode, string entityName, string tableName, string? schema)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var entityInvocations = FluentSyntaxHelpers.FindEntityConfigInvocations(root, entityName).ToList();

        var existingToTableCall = entityInvocations
            .SelectMany(entityInvocation => FluentSyntaxHelpers.FindCallsNamed(entityInvocation, "ToTable"))
            .FirstOrDefault();

        if (existingToTableCall is not null)
        {
            return MutateExistingTable(root, existingToTableCall, tableName, schema);
        }

        var existingEntityInvocation = entityInvocations.FirstOrDefault();

        if (existingEntityInvocation is not null)
        {
            return InsertTableStatement(root, existingEntityInvocation, tableName, schema);
        }

        return InsertTableEntityBlock(root, entityName, tableName, schema);
    }

    private static string MutateExistingTable(CompilationUnitSyntax root, InvocationExpressionSyntax targetCall, string tableName, string? schema)
    {
        var newCall = targetCall.WithArgumentList(BuildToTableArgumentList(tableName, schema));

        var newRoot = root.ReplaceNode(targetCall, newCall);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    private static string InsertTableStatement(CompilationUnitSyntax root, InvocationExpressionSyntax entityInvocation, string tableName, string? schema)
    {
        var lambda = (SimpleLambdaExpressionSyntax)entityInvocation.ArgumentList.Arguments.Single().Expression;
        var block = lambda.Block!;
        var blockReceiverName = lambda.Parameter.Identifier.Text;

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

        var entityInvocations = FluentSyntaxHelpers.FindEntityConfigInvocations(root, entityName).ToList();

        var existingCall = entityInvocations
            .SelectMany(entityInvocation => FluentSyntaxHelpers.FindCallsNamed(entityInvocation, "HasColumnName"))
            .FirstOrDefault(call => FluentSyntaxHelpers.GetPropertyNameFor(call) == propertyName);

        if (existingCall is not null)
        {
            return MutateExistingStringArgCall(root, existingCall, columnName);
        }

        var existingPropertyCall = entityInvocations
            .SelectMany(entityInvocation => FluentSyntaxHelpers.FindCallsNamed(entityInvocation, "Property"))
            .FirstOrDefault(call => FluentSyntaxHelpers.GetPropertyNameForPropertyCall(call) == propertyName);

        if (existingPropertyCall is not null)
        {
            return AppendStringArgCallToPropertyCall(root, existingPropertyCall, "HasColumnName", columnName);
        }

        var existingEntityInvocation = entityInvocations.FirstOrDefault();

        if (existingEntityInvocation is not null)
        {
            return InsertStringArgPropertyStatement(root, existingEntityInvocation, propertyName, "HasColumnName", columnName);
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

        var entityInvocations = FluentSyntaxHelpers.FindEntityConfigInvocations(root, entityName).ToList();

        var existingCall = entityInvocations
            .SelectMany(entityInvocation => FluentSyntaxHelpers.FindCallsNamed(entityInvocation, "HasColumnType"))
            .FirstOrDefault(call => FluentSyntaxHelpers.GetPropertyNameFor(call) == propertyName);

        if (existingCall is not null)
        {
            return MutateExistingStringArgCall(root, existingCall, columnType);
        }

        var existingPropertyCall = entityInvocations
            .SelectMany(entityInvocation => FluentSyntaxHelpers.FindCallsNamed(entityInvocation, "Property"))
            .FirstOrDefault(call => FluentSyntaxHelpers.GetPropertyNameForPropertyCall(call) == propertyName);

        if (existingPropertyCall is not null)
        {
            return AppendStringArgCallToPropertyCall(root, existingPropertyCall, "HasColumnType", columnType);
        }

        var existingEntityInvocation = entityInvocations.FirstOrDefault();

        if (existingEntityInvocation is not null)
        {
            return InsertStringArgPropertyStatement(root, existingEntityInvocation, propertyName, "HasColumnType", columnType);
        }

        return InsertStringArgEntityBlock(root, entityName, propertyName, "HasColumnType", columnType);
    }

    public string RemoveColumnType(string sourceCode, string entityName, string propertyName)
    {
        return RemoveStringArgCall(sourceCode, entityName, propertyName, "HasColumnType");
    }

    private static string MutateExistingStringArgCall(CompilationUnitSyntax root, InvocationExpressionSyntax targetCall, string value)
    {
        var newArgument = SyntaxFactory.Argument(
            SyntaxFactory.LiteralExpression(
                SyntaxKind.StringLiteralExpression,
                SyntaxFactory.Literal(value)));

        var newCall = targetCall.WithArgumentList(
            targetCall.ArgumentList.WithArguments(
                SyntaxFactory.SingletonSeparatedList(newArgument)));

        var newRoot = root.ReplaceNode(targetCall, newCall);
        return newRoot.ToFullString();
    }

    private static string AppendStringArgCallToPropertyCall(CompilationUnitSyntax root, InvocationExpressionSyntax propertyCall, string methodName, string value)
    {
        var newCall = BuildStringArgCall(propertyCall, methodName, value);

        var newRoot = root.ReplaceNode(propertyCall, newCall);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    private static string InsertStringArgPropertyStatement(CompilationUnitSyntax root, InvocationExpressionSyntax entityInvocation, string propertyName, string methodName, string value)
    {
        var lambda = (SimpleLambdaExpressionSyntax)entityInvocation.ArgumentList.Arguments.Single().Expression;
        var block = lambda.Block!;
        var blockReceiverName = lambda.Parameter.Identifier.Text;
        var propertyLambdaParam = FluentSyntaxHelpers.GetPropertyLambdaParameterName(entityInvocation);

        var newStatement = BuildStringArgPropertyStatement(blockReceiverName, propertyLambdaParam, propertyName, methodName, value);
        var newBlock = block.AddStatements(newStatement);

        var newRoot = root.ReplaceNode(block, newBlock);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    private static string InsertStringArgEntityBlock(CompilationUnitSyntax root, string entityName, string propertyName, string methodName, string value)
    {
        var method = FindOnModelCreatingMethod(root);

        var methodBody = method.Body
            ?? throw new InvalidOperationException("OnModelCreating has no method body.");

        var modelBuilderParamName = method.ParameterList.Parameters.Single().Identifier.Text;

        var propertyStatement = BuildStringArgPropertyStatement("entity", "e", propertyName, methodName, value);
        var entityBlockStatement = BuildEntityInvocationStatement(modelBuilderParamName, entityName, SyntaxFactory.Block(propertyStatement));

        var newMethodBody = methodBody.AddStatements(entityBlockStatement);
        var newRoot = root.ReplaceNode(methodBody, newMethodBody);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    private static ExpressionStatementSyntax BuildStringArgPropertyStatement(string blockReceiverName, string propertyLambdaParam, string propertyName, string methodName, string value)
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

        return SyntaxFactory.ExpressionStatement(BuildStringArgCall(propertyCall, methodName, value));
    }

    private static InvocationExpressionSyntax BuildStringArgCall(ExpressionSyntax propertyCallExpression, string methodName, string value)
    {
        return SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                propertyCallExpression,
                SyntaxFactory.IdentifierName(methodName)),
            SyntaxFactory.ArgumentList(
                SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.Argument(
                        SyntaxFactory.LiteralExpression(
                            SyntaxKind.StringLiteralExpression,
                            SyntaxFactory.Literal(value))))));
    }

    private static string RemoveStringArgCall(string sourceCode, string entityName, string propertyName, string methodName)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var entityInvocations = FluentSyntaxHelpers.FindEntityConfigInvocations(root, entityName).ToList();

        var existingCall = entityInvocations
            .SelectMany(entityInvocation => FluentSyntaxHelpers.FindCallsNamed(entityInvocation, methodName))
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

        var entityInvocations = FluentSyntaxHelpers.FindEntityConfigInvocations(root, entityName).ToList();

        var existingCall = entityInvocations
            .SelectMany(entityInvocation => FluentSyntaxHelpers.FindCallsNamed(entityInvocation, "HasDefaultValue"))
            .FirstOrDefault(call => FluentSyntaxHelpers.GetPropertyNameFor(call) == propertyName);

        if (existingCall is not null)
        {
            return MutateExistingDefaultValue(root, existingCall, literalText);
        }

        var existingPropertyCall = entityInvocations
            .SelectMany(entityInvocation => FluentSyntaxHelpers.FindCallsNamed(entityInvocation, "Property"))
            .FirstOrDefault(call => FluentSyntaxHelpers.GetPropertyNameForPropertyCall(call) == propertyName);

        if (existingPropertyCall is not null)
        {
            return AppendDefaultValueToPropertyCall(root, existingPropertyCall, literalText);
        }

        var existingEntityInvocation = entityInvocations.FirstOrDefault();

        if (existingEntityInvocation is not null)
        {
            return InsertDefaultValuePropertyStatement(root, existingEntityInvocation, propertyName, literalText);
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

    private static string InsertDefaultValuePropertyStatement(CompilationUnitSyntax root, InvocationExpressionSyntax entityInvocation, string propertyName, string literalText)
    {
        var lambda = (SimpleLambdaExpressionSyntax)entityInvocation.ArgumentList.Arguments.Single().Expression;
        var block = lambda.Block!;
        var blockReceiverName = lambda.Parameter.Identifier.Text;
        var propertyLambdaParam = FluentSyntaxHelpers.GetPropertyLambdaParameterName(entityInvocation);

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

        var entityInvocations = FluentSyntaxHelpers.FindEntityConfigInvocations(root, entityName).ToList();

        var existingCall = entityInvocations
            .SelectMany(entityInvocation => FluentSyntaxHelpers.FindCallsNamed(entityInvocation, "HasDefaultValue"))
            .FirstOrDefault(call => FluentSyntaxHelpers.GetPropertyNameFor(call) == propertyName);

        if (existingCall is null)
        {
            return sourceCode;
        }

        var propertyCallExpression = ((MemberAccessExpressionSyntax)existingCall.Expression).Expression;

        var newRoot = root.ReplaceNode(existingCall, propertyCallExpression);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    public string RemoveTable(string sourceCode, string entityName)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var entityInvocations = FluentSyntaxHelpers.FindEntityConfigInvocations(root, entityName).ToList();

        var existingToTableCall = entityInvocations
            .SelectMany(entityInvocation => FluentSyntaxHelpers.FindCallsNamed(entityInvocation, "ToTable"))
            .FirstOrDefault();

        if (existingToTableCall is null || existingToTableCall.Parent is not ExpressionStatementSyntax statement)
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

        var entityInvocations = FluentSyntaxHelpers.FindEntityConfigInvocations(root, scopeEntityName).ToList();

        var matchingCall = entityInvocations
            .SelectMany(entityInvocation => FluentSyntaxHelpers.FindCallsNamed(entityInvocation, methodName))
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

        if (targets.Count == 0)
        {
            return sourceCode;
        }

        var newRoot = root.ReplaceNodes(targets, (_, _) => SyntaxFactory.IdentifierName(newEntityName));
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    public string RenamePropertyReferences(string sourceCode, string entityName, string oldPropertyName, string newPropertyName)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var entityInvocations = FluentSyntaxHelpers.FindEntityConfigInvocations(root, entityName).ToList();

        var existingPropertyCall = entityInvocations
            .SelectMany(entityInvocation => FluentSyntaxHelpers.FindCallsNamed(entityInvocation, "Property"))
            .FirstOrDefault(call => FluentSyntaxHelpers.GetPropertyNameForPropertyCall(call) == oldPropertyName);

        if (existingPropertyCall is null)
        {
            return sourceCode;
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

    public string SetIndex(string sourceCode, string entityName, IReadOnlyList<string> propertyNames, bool isUnique, string? name = null)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var entityInvocations = FluentSyntaxHelpers.FindEntityConfigInvocations(root, entityName).ToList();

        var existingHasIndexCall = entityInvocations
            .SelectMany(inv => FluentSyntaxHelpers.FindCallsNamed(inv, "HasIndex"))
            .FirstOrDefault(call =>
            {
                var args = FluentSyntaxHelpers.TryReadIndexPropertyNames(call);
                return args is not null && args.Value.PropertyNames.SequenceEqual(propertyNames);
            });

        if (existingHasIndexCall is not null)
        {
            return MutateExistingIndex(root, existingHasIndexCall, propertyNames, isUnique, name);
        }

        var existingEntityInvocation = entityInvocations.FirstOrDefault();

        if (existingEntityInvocation is not null)
        {
            return InsertIndexStatement(root, existingEntityInvocation, propertyNames, isUnique, name);
        }

        return InsertIndexEntityBlock(root, entityName, propertyNames, isUnique, name);
    }

    private static string MutateExistingIndex(
        CompilationUnitSyntax root,
        InvocationExpressionSyntax hasIndexCall,
        IReadOnlyList<string> propertyNames,
        bool isUnique,
        string? name)
    {
        var blockReceiverName = ((MemberAccessExpressionSyntax)hasIndexCall.Expression).Expression.ToString();
        var existingStatement = hasIndexCall.Ancestors().OfType<ExpressionStatementSyntax>().First();
        var newStatement = BuildHasIndexStatement(blockReceiverName, propertyNames, isUnique, name);

        var newRoot = root.ReplaceNode(existingStatement, newStatement);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    private static string InsertIndexStatement(
        CompilationUnitSyntax root,
        InvocationExpressionSyntax entityInvocation,
        IReadOnlyList<string> propertyNames,
        bool isUnique,
        string? name)
    {
        var lambda = (SimpleLambdaExpressionSyntax)entityInvocation.ArgumentList.Arguments.Single().Expression;
        var block = lambda.Block!;
        var blockReceiverName = lambda.Parameter.Identifier.Text;

        var newStatement = BuildHasIndexStatement(blockReceiverName, propertyNames, isUnique, name);
        var newBlock = block.AddStatements(newStatement);

        var newRoot = root.ReplaceNode(block, newBlock);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    private static string InsertIndexEntityBlock(
        CompilationUnitSyntax root,
        string entityName,
        IReadOnlyList<string> propertyNames,
        bool isUnique,
        string? name)
    {
        var method = FindOnModelCreatingMethod(root);

        var methodBody = method.Body
            ?? throw new InvalidOperationException("OnModelCreating has no method body.");

        var modelBuilderParamName = method.ParameterList.Parameters.Single().Identifier.Text;

        var indexStatement = BuildHasIndexStatement("entity", propertyNames, isUnique, name);
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
        string? name)
    {
        ExpressionSyntax expression = SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                SyntaxFactory.IdentifierName(blockReceiverName),
                SyntaxFactory.IdentifierName("HasIndex")),
            BuildHasIndexArgumentList(propertyNames, name));

        if (isUnique)
        {
            expression = SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    expression,
                    SyntaxFactory.IdentifierName("IsUnique")),
                SyntaxFactory.ArgumentList());
        }

        return SyntaxFactory.ExpressionStatement(expression);
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

        var entityInvocations = FluentSyntaxHelpers.FindEntityConfigInvocations(root, entityName).ToList();

        var existingHasIndexCall = entityInvocations
            .SelectMany(inv => FluentSyntaxHelpers.FindCallsNamed(inv, "HasIndex"))
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

        if (nodesToRemove.Count == 0)
        {
            return sourceCode;
        }

        var newRoot = root.RemoveNodes(nodesToRemove, SyntaxRemoveOptions.KeepNoTrivia)!;
        return newRoot.NormalizeWhitespace().ToFullString();
    }
}
