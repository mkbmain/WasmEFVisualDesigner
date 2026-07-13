using System;
using System.Collections.Generic;
using System.Linq;
using EfSchemaVisualizer.Core.Model;
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

        foreach (var (entityName, scope) in FluentSyntaxHelpers.FindConfigurationScopes(root))
        {
            foreach (var maxLengthCall in FluentSyntaxHelpers.FindCallsNamed(scope, "HasMaxLength"))
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
                        DiagnosticCodes.UnresolvablePropertyName,
                        "Could not determine which property this HasMaxLength call configures.",
                        entityName,
                        PropertyName: null,
                        maxLengthCall.Span));
                    continue;
                }

                if (int.TryParse(arg.Expression.ToString(), out var maxLength))
                {
                    results.Add(new MaxLengthConfig(entityName, propertyName, maxLength));
                }
                else
                {
                    diagnostics.Add(new Diagnostic(
                        DiagnosticCodes.UnreadableMaxLengthArgument,
                        "HasMaxLength argument is not an integer literal and could not be read.",
                        entityName,
                        propertyName,
                        arg.Span));
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
                            DiagnosticCodes.UnresolvablePropertyName,
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
                            DiagnosticCodes.UnreadableHasPrecisionArgument,
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
                            DiagnosticCodes.UnreadableHasPrecisionArgument,
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
                            DiagnosticCodes.UnresolvablePropertyName,
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
                            DiagnosticCodes.UnreadableIsRequiredArgument,
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
                    var propertyNames = FluentSyntaxHelpers.TryReadPropertyNameList(hasKeyCall);

                    if (propertyNames is null)
                    {
                        diagnostics.Add(new Diagnostic(
                            DiagnosticCodes.UnreadableHasKeyArgument,
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
                            DiagnosticCodes.UnreadableToTableArgument,
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
                                DiagnosticCodes.UnreadableToTableArgument,
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
                            DiagnosticCodes.UnresolvablePropertyName,
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
                            DiagnosticCodes.UnreadableHasColumnNameArgument,
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
                            DiagnosticCodes.UnresolvablePropertyName,
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
                            DiagnosticCodes.UnreadableHasColumnTypeArgument,
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

    public ParseResult<IReadOnlyList<DefaultValueConfig>> ParseDefaultValues(string sourceCode)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var results = new List<DefaultValueConfig>();
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
                foreach (var call in FluentSyntaxHelpers.FindCallsNamed(entityInvocation, "HasDefaultValue"))
                {
                    var propertyName = FluentSyntaxHelpers.GetPropertyNameFor(call);

                    if (propertyName is null)
                    {
                        diagnostics.Add(new Diagnostic(
                            DiagnosticCodes.UnresolvablePropertyName,
                            "Could not determine which property this HasDefaultValue call configures.",
                            entityName,
                            PropertyName: null,
                            call.Span));
                        continue;
                    }

                    var arg = call.ArgumentList.Arguments.FirstOrDefault();

                    if (arg?.Expression is LiteralExpressionSyntax literal)
                    {
                        results.Add(new DefaultValueConfig(entityName!, propertyName, literal.ToString()));
                    }
                    else
                    {
                        diagnostics.Add(new Diagnostic(
                            DiagnosticCodes.UnreadableHasDefaultValueArgument,
                            "HasDefaultValue argument is not a literal and could not be read.",
                            entityName,
                            propertyName,
                            (arg ?? (SyntaxNode)call).Span));
                    }
                }
            }
        }

        return new ParseResult<IReadOnlyList<DefaultValueConfig>>(results, diagnostics);
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
                            DiagnosticCodes.UnreadableHasIndexArgument,
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

    public ParseResult<IReadOnlyList<RelationshipConfig>> ParseRelationships(
        string sourceCode, IReadOnlyList<EntityModel> entities)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var results = new List<RelationshipConfig>();
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
                var calls = FluentSyntaxHelpers.FindCallsNamed(entityInvocation, "HasOne")
                    .Concat(FluentSyntaxHelpers.FindCallsNamed(entityInvocation, "HasMany"))
                    .ToList();

                if (FluentSyntaxHelpers.FindChainedCall(entityInvocation, "HasOne") is { } chainedHasOne)
                {
                    calls.Add(chainedHasOne);
                }

                if (FluentSyntaxHelpers.FindChainedCall(entityInvocation, "HasMany") is { } chainedHasMany)
                {
                    calls.Add(chainedHasMany);
                }

                foreach (var call in calls)
                {
                    ParseRelationshipChain(call, entityName!, entities, results, diagnostics);
                }
            }
        }

        return new ParseResult<IReadOnlyList<RelationshipConfig>>(results, diagnostics);
    }

    private static void ParseRelationshipChain(
        InvocationExpressionSyntax call,
        string configuringEntityName,
        IReadOnlyList<EntityModel> entities,
        List<RelationshipConfig> results,
        List<Diagnostic> diagnostics)
    {
        var isHasMany = GetInvokedMethodName(call) == "HasMany";

        var withCall = FluentSyntaxHelpers.FindChainedCall(call, "WithMany")
            ?? FluentSyntaxHelpers.FindChainedCall(call, "WithOne");

        if (withCall is null)
        {
            return;
        }

        var isWithMany = GetInvokedMethodName(withCall) == "WithMany";

        var (targetEntityName, targetResolved) = ResolveRelatedEntity(call, configuringEntityName, entities);

        if (!targetResolved)
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticCodes.UnresolvableRelationshipTarget,
                $"Could not determine the related entity for this {(isHasMany ? "HasMany" : "HasOne")} call.",
                configuringEntityName,
                PropertyName: null,
                call.Span));
            return;
        }

        var kind = (isHasMany, isWithMany) switch
        {
            (false, true) => RelationshipKind.OneToMany,  // HasOne...WithMany
            (true, false) => RelationshipKind.OneToMany,  // HasMany...WithOne
            (true, true) => RelationshipKind.ManyToMany,  // HasMany...WithMany
            (false, false) => RelationshipKind.OneToOne,  // HasOne...WithOne
        };

        InvocationExpressionSyntax? hasForeignKeyCall = null;
        InvocationExpressionSyntax? onDeleteCall = null;
        InvocationExpressionSyntax? usingEntityCall = null;

        WalkRelationshipTailChain(withCall, invocation =>
        {
            switch (GetInvokedMethodName(invocation))
            {
                case "HasForeignKey": hasForeignKeyCall = invocation; break;
                case "OnDelete": onDeleteCall = invocation; break;
                case "UsingEntity": usingEntityCall = invocation; break;
            }
        });

        string principalEntity;
        string dependentEntity;

        if (kind == RelationshipKind.OneToOne)
        {
            var explicitDependent = TryGetGenericTypeArgument(hasForeignKeyCall);
            dependentEntity = explicitDependent ?? configuringEntityName;
            principalEntity = dependentEntity == configuringEntityName ? targetEntityName! : configuringEntityName;
        }
        else if (kind == RelationshipKind.ManyToMany)
        {
            principalEntity = configuringEntityName;
            dependentEntity = targetEntityName!;
        }
        else if (!isHasMany) // HasOne...WithMany
        {
            principalEntity = targetEntityName!;
            dependentEntity = configuringEntityName;
        }
        else // HasMany...WithOne
        {
            principalEntity = configuringEntityName;
            dependentEntity = targetEntityName!;
        }

        var configuringNav = TryReadNavigationName(call);
        var targetNav = TryReadNavigationName(withCall);

        string? principalNavigation;
        string? dependentNavigation;

        if (kind == RelationshipKind.OneToOne)
        {
            if (dependentEntity == configuringEntityName)
            {
                dependentNavigation = configuringNav;
                principalNavigation = targetNav;
            }
            else
            {
                dependentNavigation = targetNav;
                principalNavigation = configuringNav;
            }
        }
        else if (kind == RelationshipKind.ManyToMany)
        {
            principalNavigation = configuringNav;
            dependentNavigation = targetNav;
        }
        else if (!isHasMany) // HasOne...WithMany
        {
            dependentNavigation = configuringNav;
            principalNavigation = targetNav;
        }
        else // HasMany...WithOne
        {
            principalNavigation = configuringNav;
            dependentNavigation = targetNav;
        }

        IReadOnlyList<string> foreignKeyProperties = Array.Empty<string>();
        if (hasForeignKeyCall is not null)
        {
            var props = FluentSyntaxHelpers.TryReadPropertyNameList(hasForeignKeyCall);

            if (props is null)
            {
                diagnostics.Add(new Diagnostic(
                    DiagnosticCodes.UnreadableHasForeignKeyArgument,
                    "HasForeignKey argument(s) could not be read as property name(s).",
                    dependentEntity,
                    PropertyName: null,
                    hasForeignKeyCall.Span));
            }
            else
            {
                foreignKeyProperties = props;
            }
        }

        string? onDeleteBehavior = null;
        if (onDeleteCall is not null)
        {
            var arg = onDeleteCall.ArgumentList.Arguments.FirstOrDefault();

            if (arg?.Expression is MemberAccessExpressionSyntax { Name.Identifier.Text: var behaviorName })
            {
                onDeleteBehavior = behaviorName;
            }
            else
            {
                diagnostics.Add(new Diagnostic(
                    DiagnosticCodes.UnreadableOnDeleteArgument,
                    "OnDelete argument is not a DeleteBehavior member access and could not be read.",
                    dependentEntity,
                    PropertyName: null,
                    arg?.Span ?? onDeleteCall.Span));
            }
        }

        var joinEntityName = kind == RelationshipKind.ManyToMany ? TryGetGenericTypeArgument(usingEntityCall) : null;

        results.Add(new RelationshipConfig(
            principalEntity,
            dependentEntity,
            kind,
            principalNavigation,
            dependentNavigation,
            foreignKeyProperties,
            onDeleteBehavior,
            joinEntityName));
    }

    private static (string? EntityName, bool Resolved) ResolveRelatedEntity(
        InvocationExpressionSyntax call, string configuringEntityName, IReadOnlyList<EntityModel> entities)
    {
        var explicitTarget = TryGetGenericTypeArgument(call);
        if (explicitTarget is not null)
        {
            return (explicitTarget, true);
        }

        var navigationName = TryReadNavigationName(call);
        if (navigationName is null)
        {
            return (null, false);
        }

        var configuringEntity = entities.FirstOrDefault(e => e.Name == configuringEntityName);
        var property = configuringEntity?.Properties.FirstOrDefault(p => p.Name == navigationName);

        if (property is null)
        {
            return (null, false);
        }

        var elementTypeName = FluentSyntaxHelpers.TryGetElementTypeName(property.ClrType);

        return elementTypeName is null ? (null, false) : (elementTypeName, true);
    }

    private static string? TryReadNavigationName(InvocationExpressionSyntax call)
    {
        var argumentExpression = call.ArgumentList.Arguments
            .Select(a => a.Expression)
            .FirstOrDefault();

        return argumentExpression switch
        {
            SimpleLambdaExpressionSyntax { ExpressionBody: MemberAccessExpressionSyntax { Name.Identifier.Text: var name } } => name,
            ParenthesizedLambdaExpressionSyntax { ExpressionBody: MemberAccessExpressionSyntax { Name.Identifier.Text: var name } } => name,
            _ => null,
        };
    }

    private static string? GetInvokedMethodName(InvocationExpressionSyntax invocation)
    {
        return invocation.Expression is MemberAccessExpressionSyntax { Name: SimpleNameSyntax simpleName }
            ? simpleName.Identifier.Text
            : null;
    }

    private static string? TryGetGenericTypeArgument(InvocationExpressionSyntax? invocation)
    {
        return invocation?.Expression is MemberAccessExpressionSyntax { Name: GenericNameSyntax { TypeArgumentList.Arguments: [var typeArg] } }
            ? typeArg.ToString()
            : null;
    }

    private static void WalkRelationshipTailChain(InvocationExpressionSyntax withCall, Action<InvocationExpressionSyntax> visit)
    {
        SyntaxNode? cursor = withCall.Parent;

        while (cursor is not null && cursor is not StatementSyntax)
        {
            if (cursor is MemberAccessExpressionSyntax && cursor.Parent is InvocationExpressionSyntax invocation)
            {
                visit(invocation);
            }

            cursor = cursor.Parent;
        }
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
                    DiagnosticCodes.UnreadableIsUniqueArgument,
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
