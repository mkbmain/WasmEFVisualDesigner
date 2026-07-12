using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EfSchemaVisualizer.Core.Parsing;

public sealed class FluentConfigParser
{
    public ParseResult<IReadOnlyList<MaxLengthConfig>> ParseMaxLengths(string sourceCode)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var results = new List<MaxLengthConfig>();
        var diagnostics = new List<Diagnostic>();

        // Distinct entity names configured anywhere in the file.
        var entityNames = root.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Select(FluentSyntaxHelpers.GetConfiguredEntityName)
            .Where(name => name is not null)
            .Distinct()!;

        foreach (var entityName in entityNames)
        {
            foreach (var entityInvocation in FluentSyntaxHelpers.FindEntityConfigInvocations(root, entityName!))
            {
                foreach (var maxLengthCall in FluentSyntaxHelpers.FindCallsNamed(entityInvocation, "HasMaxLength"))
                {
                    var propertyName = FluentSyntaxHelpers.GetPropertyNameFor(maxLengthCall);
                    var arg = maxLengthCall.ArgumentList.Arguments.FirstOrDefault();

                    if (arg is null)
                    {
                        continue;
                    }

                    if (propertyName is null)
                    {
                        diagnostics.Add(new Diagnostic(
                            "UnresolvablePropertyName",
                            "Could not determine which property this HasMaxLength call configures.",
                            entityName,
                            PropertyName: null,
                            maxLengthCall.Span));
                        continue;
                    }

                    if (int.TryParse(arg.Expression.ToString(), out var maxLength))
                    {
                        results.Add(new MaxLengthConfig(entityName!, propertyName, maxLength));
                    }
                    else
                    {
                        diagnostics.Add(new Diagnostic(
                            "UnreadableMaxLengthArgument",
                            "HasMaxLength argument is not an integer literal and could not be read.",
                            entityName,
                            propertyName,
                            arg.Span));
                    }
                }
            }
        }

        return new ParseResult<IReadOnlyList<MaxLengthConfig>>(results, diagnostics);
    }

    public ParseResult<IReadOnlyList<PrecisionConfig>> ParsePrecisions(string sourceCode)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var results = new List<PrecisionConfig>();
        var diagnostics = new List<Diagnostic>();

        var entityNames = root.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Select(FluentSyntaxHelpers.GetConfiguredEntityName)
            .Where(name => name is not null)
            .Distinct()!;

        foreach (var entityName in entityNames)
        {
            foreach (var entityInvocation in FluentSyntaxHelpers.FindEntityConfigInvocations(root, entityName!))
            {
                foreach (var precisionCall in FluentSyntaxHelpers.FindCallsNamed(entityInvocation, "HasPrecision"))
                {
                    var propertyName = FluentSyntaxHelpers.GetPropertyNameFor(precisionCall);

                    if (propertyName is null)
                    {
                        diagnostics.Add(new Diagnostic(
                            "UnresolvablePropertyName",
                            "Could not determine which property this HasPrecision call configures.",
                            entityName,
                            PropertyName: null,
                            precisionCall.Span));
                        continue;
                    }

                    var arguments = precisionCall.ArgumentList.Arguments;

                    if (arguments.Count == 0)
                    {
                        continue;
                    }

                    if (!int.TryParse(arguments[0].Expression.ToString(), out var precision))
                    {
                        diagnostics.Add(new Diagnostic(
                            "UnreadableHasPrecisionArgument",
                            "HasPrecision argument is not an integer literal and could not be read.",
                            entityName,
                            propertyName,
                            arguments[0].Span));
                        continue;
                    }

                    if (arguments.Count == 1)
                    {
                        results.Add(new PrecisionConfig(entityName!, propertyName, precision, Scale: null));
                        continue;
                    }

                    if (!int.TryParse(arguments[1].Expression.ToString(), out var scale))
                    {
                        diagnostics.Add(new Diagnostic(
                            "UnreadableHasPrecisionArgument",
                            "HasPrecision argument is not an integer literal and could not be read.",
                            entityName,
                            propertyName,
                            arguments[1].Span));
                        continue;
                    }

                    results.Add(new PrecisionConfig(entityName!, propertyName, precision, scale));
                }
            }
        }

        return new ParseResult<IReadOnlyList<PrecisionConfig>>(results, diagnostics);
    }

    public ParseResult<IReadOnlyList<IsRequiredConfig>> ParseIsRequired(string sourceCode)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var results = new List<IsRequiredConfig>();
        var diagnostics = new List<Diagnostic>();

        var entityNames = root.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Select(FluentSyntaxHelpers.GetConfiguredEntityName)
            .Where(name => name is not null)
            .Distinct()!;

        foreach (var entityName in entityNames)
        {
            foreach (var entityInvocation in FluentSyntaxHelpers.FindEntityConfigInvocations(root, entityName!))
            {
                foreach (var isRequiredCall in FluentSyntaxHelpers.FindCallsNamed(entityInvocation, "IsRequired"))
                {
                    var propertyName = FluentSyntaxHelpers.GetPropertyNameFor(isRequiredCall);
                    var arg = isRequiredCall.ArgumentList.Arguments.FirstOrDefault();

                    if (propertyName is null)
                    {
                        diagnostics.Add(new Diagnostic(
                            "UnresolvablePropertyName",
                            "Could not determine which property this IsRequired call configures.",
                            entityName,
                            PropertyName: null,
                            isRequiredCall.Span));
                        continue;
                    }

                    if (arg is null)
                    {
                        results.Add(new IsRequiredConfig(entityName!, propertyName, IsRequired: true));
                        continue;
                    }

                    if (arg.Expression is LiteralExpressionSyntax literal
                        && (literal.IsKind(SyntaxKind.TrueLiteralExpression) || literal.IsKind(SyntaxKind.FalseLiteralExpression)))
                    {
                        results.Add(new IsRequiredConfig(entityName!, propertyName, literal.IsKind(SyntaxKind.TrueLiteralExpression)));
                    }
                    else
                    {
                        diagnostics.Add(new Diagnostic(
                            "UnreadableIsRequiredArgument",
                            "IsRequired argument is not a boolean literal and could not be read.",
                            entityName,
                            propertyName,
                            arg.Span));
                    }
                }
            }
        }

        return new ParseResult<IReadOnlyList<IsRequiredConfig>>(results, diagnostics);
    }

    public ParseResult<IReadOnlyList<KeyConfig>> ParseKeys(string sourceCode)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var results = new List<KeyConfig>();
        var diagnostics = new List<Diagnostic>();

        var entityNames = root.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Select(FluentSyntaxHelpers.GetConfiguredEntityName)
            .Where(name => name is not null)
            .Distinct()!;

        foreach (var entityName in entityNames)
        {
            foreach (var entityInvocation in FluentSyntaxHelpers.FindEntityConfigInvocations(root, entityName!))
            {
                foreach (var hasKeyCall in FluentSyntaxHelpers.FindCallsNamed(entityInvocation, "HasKey"))
                {
                    var propertyNames = TryReadKeyPropertyNames(hasKeyCall);

                    if (propertyNames is null)
                    {
                        diagnostics.Add(new Diagnostic(
                            "UnreadableHasKeyArgument",
                            "HasKey argument(s) could not be read as property name(s).",
                            entityName,
                            PropertyName: null,
                            hasKeyCall.Span));
                        continue;
                    }

                    results.Add(new KeyConfig(entityName!, propertyNames));
                }
            }
        }

        return new ParseResult<IReadOnlyList<KeyConfig>>(results, diagnostics);
    }

    private static IReadOnlyList<string>? TryReadKeyPropertyNames(InvocationExpressionSyntax hasKeyCall)
    {
        var arguments = hasKeyCall.ArgumentList.Arguments;

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
            return TryReadKeyPropertyNamesFromLambdaBody(body);
        }

        return null;
    }

    private static IReadOnlyList<string>? TryReadKeyPropertyNamesFromLambdaBody(ExpressionSyntax body)
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

    public ParseResult<IReadOnlyList<TableConfig>> ParseTableMappings(string sourceCode)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var results = new List<TableConfig>();
        var diagnostics = new List<Diagnostic>();

        var entityNames = root.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Select(FluentSyntaxHelpers.GetConfiguredEntityName)
            .Where(name => name is not null)
            .Distinct()!;

        foreach (var entityName in entityNames)
        {
            foreach (var entityInvocation in FluentSyntaxHelpers.FindEntityConfigInvocations(root, entityName!))
            {
                foreach (var toTableCall in FluentSyntaxHelpers.FindCallsNamed(entityInvocation, "ToTable"))
                {
                    var arguments = toTableCall.ArgumentList.Arguments;

                    if (arguments.Count == 0
                        || arguments[0].Expression is not LiteralExpressionSyntax { } tableNameLiteral
                        || !tableNameLiteral.IsKind(SyntaxKind.StringLiteralExpression))
                    {
                        diagnostics.Add(new Diagnostic(
                            "UnreadableToTableArgument",
                            "ToTable argument is not a string literal and could not be read.",
                            entityName,
                            PropertyName: null,
                            toTableCall.Span));
                        continue;
                    }

                    string? schema = null;
                    if (arguments.Count >= 2)
                    {
                        if (arguments[1].Expression is LiteralExpressionSyntax { } schemaLiteral
                            && schemaLiteral.IsKind(SyntaxKind.StringLiteralExpression))
                        {
                            schema = schemaLiteral.Token.ValueText;
                        }
                        else
                        {
                            diagnostics.Add(new Diagnostic(
                                "UnreadableToTableArgument",
                                "ToTable schema argument is not a string literal and could not be read.",
                                entityName,
                                PropertyName: null,
                                toTableCall.Span));
                            continue;
                        }
                    }

                    results.Add(new TableConfig(entityName!, tableNameLiteral.Token.ValueText, schema));
                }
            }
        }

        return new ParseResult<IReadOnlyList<TableConfig>>(results, diagnostics);
    }

    public ParseResult<IReadOnlyList<ColumnNameConfig>> ParseColumnNames(string sourceCode)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var results = new List<ColumnNameConfig>();
        var diagnostics = new List<Diagnostic>();

        var entityNames = root.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Select(FluentSyntaxHelpers.GetConfiguredEntityName)
            .Where(name => name is not null)
            .Distinct()!;

        foreach (var entityName in entityNames)
        {
            foreach (var entityInvocation in FluentSyntaxHelpers.FindEntityConfigInvocations(root, entityName!))
            {
                foreach (var call in FluentSyntaxHelpers.FindCallsNamed(entityInvocation, "HasColumnName"))
                {
                    var propertyName = FluentSyntaxHelpers.GetPropertyNameFor(call);

                    if (propertyName is null)
                    {
                        diagnostics.Add(new Diagnostic(
                            "UnresolvablePropertyName",
                            "Could not determine which property this HasColumnName call configures.",
                            entityName,
                            PropertyName: null,
                            call.Span));
                        continue;
                    }

                    var arg = call.ArgumentList.Arguments.FirstOrDefault();

                    if (arg?.Expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression))
                    {
                        results.Add(new ColumnNameConfig(entityName!, propertyName, literal.Token.ValueText));
                    }
                    else
                    {
                        diagnostics.Add(new Diagnostic(
                            "UnreadableHasColumnNameArgument",
                            "HasColumnName argument is not a string literal and could not be read.",
                            entityName,
                            propertyName,
                            (arg ?? (SyntaxNode)call).Span));
                    }
                }
            }
        }

        return new ParseResult<IReadOnlyList<ColumnNameConfig>>(results, diagnostics);
    }

    public ParseResult<IReadOnlyList<ColumnTypeConfig>> ParseColumnTypes(string sourceCode)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var results = new List<ColumnTypeConfig>();
        var diagnostics = new List<Diagnostic>();

        var entityNames = root.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Select(FluentSyntaxHelpers.GetConfiguredEntityName)
            .Where(name => name is not null)
            .Distinct()!;

        foreach (var entityName in entityNames)
        {
            foreach (var entityInvocation in FluentSyntaxHelpers.FindEntityConfigInvocations(root, entityName!))
            {
                foreach (var call in FluentSyntaxHelpers.FindCallsNamed(entityInvocation, "HasColumnType"))
                {
                    var propertyName = FluentSyntaxHelpers.GetPropertyNameFor(call);

                    if (propertyName is null)
                    {
                        diagnostics.Add(new Diagnostic(
                            "UnresolvablePropertyName",
                            "Could not determine which property this HasColumnType call configures.",
                            entityName,
                            PropertyName: null,
                            call.Span));
                        continue;
                    }

                    var arg = call.ArgumentList.Arguments.FirstOrDefault();

                    if (arg?.Expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression))
                    {
                        results.Add(new ColumnTypeConfig(entityName!, propertyName, literal.Token.ValueText));
                    }
                    else
                    {
                        diagnostics.Add(new Diagnostic(
                            "UnreadableHasColumnTypeArgument",
                            "HasColumnType argument is not a string literal and could not be read.",
                            entityName,
                            propertyName,
                            (arg ?? (SyntaxNode)call).Span));
                    }
                }
            }
        }

        return new ParseResult<IReadOnlyList<ColumnTypeConfig>>(results, diagnostics);
    }

    public ParseResult<IReadOnlyList<IndexConfig>> ParseIndexes(string sourceCode)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var results = new List<IndexConfig>();
        var diagnostics = new List<Diagnostic>();

        var entityNames = root.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Select(FluentSyntaxHelpers.GetConfiguredEntityName)
            .Where(name => name is not null)
            .Distinct()!;

        foreach (var entityName in entityNames)
        {
            foreach (var entityInvocation in FluentSyntaxHelpers.FindEntityConfigInvocations(root, entityName!))
            {
                foreach (var hasIndexCall in FluentSyntaxHelpers.FindCallsNamed(entityInvocation, "HasIndex"))
                {
                    var indexArgs = FluentSyntaxHelpers.TryReadIndexPropertyNames(hasIndexCall);

                    if (indexArgs is null)
                    {
                        diagnostics.Add(new Diagnostic(
                            "UnreadableHasIndexArgument",
                            "HasIndex argument(s) could not be read as property name(s).",
                            entityName,
                            PropertyName: null,
                            hasIndexCall.Span));
                        continue;
                    }

                    var (isUnique, isUniqueDiag) = TryReadIsUnique(hasIndexCall, entityName!);
                    if (isUniqueDiag is not null)
                        diagnostics.Add(isUniqueDiag);

                    results.Add(new IndexConfig(entityName!, indexArgs.Value.PropertyNames, isUnique, indexArgs.Value.Name));
                }
            }
        }

        return new ParseResult<IReadOnlyList<IndexConfig>>(results, diagnostics);
    }

    private static (bool IsUnique, Diagnostic? Diagnostic) TryReadIsUnique(
        InvocationExpressionSyntax hasIndexCall, string entityName)
    {
        SyntaxNode? cursor = hasIndexCall.Parent;
        while (cursor is not null && cursor is not StatementSyntax)
        {
            if (cursor is MemberAccessExpressionSyntax { Name.Identifier.Text: "IsUnique" }
                && cursor.Parent is InvocationExpressionSyntax isUniqueInvocation)
            {
                var arg = isUniqueInvocation.ArgumentList.Arguments.FirstOrDefault();
                if (arg is null)
                    return (true, null);

                if (arg.Expression is LiteralExpressionSyntax literal
                    && (literal.IsKind(SyntaxKind.TrueLiteralExpression)
                        || literal.IsKind(SyntaxKind.FalseLiteralExpression)))
                    return (literal.IsKind(SyntaxKind.TrueLiteralExpression), null);

                return (false, new Diagnostic(
                    "UnreadableIsUniqueArgument",
                    "IsUnique argument is not a boolean literal and could not be read.",
                    entityName,
                    PropertyName: null,
                    arg.Span));
            }

            cursor = cursor.Parent;
        }

        return (false, null);
    }
}
