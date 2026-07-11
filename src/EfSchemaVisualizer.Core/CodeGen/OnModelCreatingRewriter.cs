using System;
using System.Collections.Generic;
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

    public string RewriteIsRequired(string sourceCode, string entityName, string propertyName, bool newIsRequired)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var entityInvocations = FluentSyntaxHelpers.FindEntityConfigInvocations(root, entityName).ToList();

        var existingIsRequiredCall = entityInvocations
            .SelectMany(entityInvocation => FluentSyntaxHelpers.FindCallsNamed(entityInvocation, "IsRequired"))
            .FirstOrDefault(call => FluentSyntaxHelpers.GetPropertyNameFor(call) == propertyName);

        if (existingIsRequiredCall is not null)
        {
            return MutateExistingIsRequired(root, existingIsRequiredCall, newIsRequired);
        }

        var existingPropertyCall = entityInvocations
            .SelectMany(entityInvocation => FluentSyntaxHelpers.FindCallsNamed(entityInvocation, "Property"))
            .FirstOrDefault(call => FluentSyntaxHelpers.GetPropertyNameForPropertyCall(call) == propertyName);

        if (existingPropertyCall is not null)
        {
            return AppendIsRequiredToPropertyCall(root, existingPropertyCall, newIsRequired);
        }

        var existingEntityInvocation = entityInvocations.FirstOrDefault();

        if (existingEntityInvocation is not null)
        {
            return InsertIsRequiredPropertyStatement(root, existingEntityInvocation, propertyName, newIsRequired);
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

    private static string InsertIsRequiredPropertyStatement(CompilationUnitSyntax root, InvocationExpressionSyntax entityInvocation, string propertyName, bool newIsRequired)
    {
        var lambda = (SimpleLambdaExpressionSyntax)entityInvocation.ArgumentList.Arguments.Single().Expression;
        var block = lambda.Block!;
        var blockReceiverName = lambda.Parameter.Identifier.Text;
        var propertyLambdaParam = FluentSyntaxHelpers.GetPropertyLambdaParameterName(entityInvocation);

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

    public string AddEntity(string sourceCode, string entityName, string dbSetPropertyName)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

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

    private static MethodDeclarationSyntax FindOnModelCreatingMethod(CompilationUnitSyntax root)
    {
        return root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(m => m.Identifier.Text == "OnModelCreating")
            ?? throw new InvalidOperationException("No OnModelCreating method found in source.");
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

    public string RemoveIsRequired(string sourceCode, string entityName, string propertyName)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var entityInvocations = FluentSyntaxHelpers.FindEntityConfigInvocations(root, entityName).ToList();

        var existingIsRequiredCall = entityInvocations
            .SelectMany(entityInvocation => FluentSyntaxHelpers.FindCallsNamed(entityInvocation, "IsRequired"))
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
