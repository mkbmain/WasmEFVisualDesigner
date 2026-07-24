using System;
using System.Collections.Generic;
using System.Linq;
using EfSchemaVisualizer.Core.Merging;
using EfSchemaVisualizer.Core.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EfSchemaVisualizer.Core.Parsing;

public sealed class FluentConfigParser
{
    /// Fluent call names read by one of the `Parse*` methods above. Anything chained onto an entity's
    /// config scope whose name isn't in this set is flagged by <see cref="ParseUnrecognizedCalls"/>.
    private static readonly HashSet<string> RecognizedCallNames = new()
    {
        "Property", "HasMaxLength", "HasPrecision", "IsRequired", "IsUnicode", "IsFixedLength", "HasKey", "HasAlternateKey", "ToTable",
        "HasColumnName", "HasColumnType", "HasDefaultValue", "HasDefaultValueSql", "HasIndex", "IsUnique",
        "HasFilter", "IsDescending", "IncludeProperties",
        "HasOne", "HasMany", "WithOne", "WithMany", "HasForeignKey", "OnDelete", "UsingEntity",
        "Ignore", "ValueGeneratedOnAdd", "ValueGeneratedOnUpdate", "ValueGeneratedOnAddOrUpdate",
        "ValueGeneratedNever", "UseIdentityColumn", "ToView", "ToSqlQuery", "HasNoKey",
        "IsRowVersion", "IsConcurrencyToken", "HasQueryFilter", "HasComment", "UseCollation", "ToJson",
        "SplitToTable",
    };

    /// Flags every fluent config call within an entity's scope whose method name isn't recognized by
    /// any of the `Parse*` methods above (e.g. `Ignore`, `HasFilter`, `HasConversion`) so it surfaces
    /// as a diagnostic instead of silently disappearing.
    public IReadOnlyList<Diagnostic> ParseUnrecognizedCalls(string sourceCode)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var diagnostics = new List<Diagnostic>();

        foreach (var (entityName, scope) in FluentSyntaxHelpers.FindConfigurationScopes(root))
        {
            foreach (var call in FluentSyntaxHelpers.FindConfigChainCalls(scope))
            {
                if (call.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: var methodName })
                {
                    continue;
                }

                if (RecognizedCallNames.Contains(methodName))
                {
                    continue;
                }

                diagnostics.Add(new Diagnostic(
                    DiagnosticCodes.UnrecognizedConfigCall,
                    $"'{methodName}' is not a recognized configuration call and was ignored.",
                    entityName,
                    PropertyName: null,
                    call.Span));
            }
        }

        return diagnostics;
    }

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

        foreach (var (entityName, scope) in FluentSyntaxHelpers.FindConfigurationScopes(root))
        {
            foreach (var precisionCall in FluentSyntaxHelpers.FindCallsNamed(scope, "HasPrecision"))
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
                    results.Add(new PrecisionConfig(entityName, propertyName, precision, Scale: null));
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

                results.Add(new PrecisionConfig(entityName, propertyName, precision, scale));
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

        foreach (var (entityName, scope) in FluentSyntaxHelpers.FindConfigurationScopes(root))
        {
            foreach (var isRequiredCall in FluentSyntaxHelpers.FindCallsNamed(scope, "IsRequired"))
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
                    results.Add(new IsRequiredConfig(entityName, propertyName, IsRequired: true));
                    continue;
                }

                if (arg.Expression is LiteralExpressionSyntax literal
                    && (literal.IsKind(SyntaxKind.TrueLiteralExpression) || literal.IsKind(SyntaxKind.FalseLiteralExpression)))
                {
                    results.Add(new IsRequiredConfig(entityName, propertyName, literal.IsKind(SyntaxKind.TrueLiteralExpression)));
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

        return new ParseResult<IReadOnlyList<IsRequiredConfig>>(results, diagnostics);
    }

    public ParseResult<IReadOnlyList<KeyConfig>> ParseKeys(string sourceCode)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var results = new List<KeyConfig>();
        var diagnostics = new List<Diagnostic>();

        foreach (var (entityName, scope) in FluentSyntaxHelpers.FindConfigurationScopes(root))
        {
            foreach (var hasKeyCall in FluentSyntaxHelpers.FindCallsNamed(scope, "HasKey"))
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

                results.Add(new KeyConfig(entityName, propertyNames));
            }
        }

        return new ParseResult<IReadOnlyList<KeyConfig>>(results, diagnostics);
    }

    public ParseResult<IReadOnlyList<AlternateKeyConfig>> ParseAlternateKeys(string sourceCode)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var results = new List<AlternateKeyConfig>();
        var diagnostics = new List<Diagnostic>();

        foreach (var (entityName, scope) in FluentSyntaxHelpers.FindConfigurationScopes(root))
        {
            foreach (var hasAlternateKeyCall in FluentSyntaxHelpers.FindCallsNamed(scope, "HasAlternateKey"))
            {
                var propertyNames = FluentSyntaxHelpers.TryReadPropertyNameList(hasAlternateKeyCall);

                if (propertyNames is null)
                {
                    diagnostics.Add(new Diagnostic(
                        DiagnosticCodes.UnreadableHasAlternateKeyArgument,
                        "HasAlternateKey argument(s) could not be read as property name(s).",
                        entityName,
                        PropertyName: null,
                        hasAlternateKeyCall.Span));
                    continue;
                }

                results.Add(new AlternateKeyConfig(entityName, propertyNames));
            }
        }

        return new ParseResult<IReadOnlyList<AlternateKeyConfig>>(results, diagnostics);
    }

    /// Reads both `ToTable("Name"[, "schema"])` and the config-lambda overloads
    /// (`ToTable(b => b.IsTemporal())`, `ToTable("Name", b => b.IsTemporal())`) in one pass, since
    /// both read the same call name — a second full walk of every `ToTable` call would be
    /// redundant. Only `IsTemporal()` is recognized inside a config lambda; any other builder
    /// configuration inside it is not read and produces no diagnostic (same scope cut as
    /// `SplitToTable`'s builder-lambda internals), but a known table name (two-arg overload) is
    /// still captured even when the lambda's other configuration isn't understood.
    public ParseResult<(IReadOnlyList<TableConfig> Tables, IReadOnlyList<TemporalConfig> Temporal)> ParseTableMappings(string sourceCode)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var tables = new List<TableConfig>();
        var temporal = new List<TemporalConfig>();
        var diagnostics = new List<Diagnostic>();

        foreach (var (entityName, scope) in FluentSyntaxHelpers.FindConfigurationScopes(root))
        {
            foreach (var toTableCall in FluentSyntaxHelpers.FindCallsNamed(scope, "ToTable"))
            {
                var arguments = toTableCall.ArgumentList.Arguments;

                if (arguments.Count == 0)
                {
                    diagnostics.Add(new Diagnostic(
                        DiagnosticCodes.UnreadableToTableArgument,
                        "ToTable argument is not a string literal and could not be read.",
                        entityName,
                        PropertyName: null,
                        toTableCall.Span));
                    continue;
                }

                if (arguments.Count == 1 && arguments[0].Expression is AnonymousFunctionExpressionSyntax singleLambda)
                {
                    if (ContainsIsTemporalCall(singleLambda))
                    {
                        temporal.Add(new TemporalConfig(entityName));
                    }

                    continue;
                }

                if (arguments[0].Expression is not LiteralExpressionSyntax { } tableNameLiteral
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

                if (arguments.Count >= 2 && arguments[1].Expression is AnonymousFunctionExpressionSyntax pairedLambda)
                {
                    if (ContainsIsTemporalCall(pairedLambda))
                    {
                        temporal.Add(new TemporalConfig(entityName));
                    }
                }
                else if (arguments.Count >= 2)
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

                tables.Add(new TableConfig(entityName, tableNameLiteral.Token.ValueText, schema));
            }
        }

        return new ParseResult<(IReadOnlyList<TableConfig>, IReadOnlyList<TemporalConfig>)>((tables, temporal), diagnostics);
    }

    private static bool ContainsIsTemporalCall(AnonymousFunctionExpressionSyntax lambda)
    {
        return lambda.DescendantNodesAndSelf()
            .OfType<InvocationExpressionSyntax>()
            .Any(invocation => invocation.Expression is MemberAccessExpressionSyntax { Name.Identifier.Text: "IsTemporal" });
    }

    public ParseResult<IReadOnlyList<ViewConfig>> ParseViewMappings(string sourceCode)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var results = new List<ViewConfig>();
        var diagnostics = new List<Diagnostic>();

        foreach (var (entityName, scope) in FluentSyntaxHelpers.FindConfigurationScopes(root))
        {
            foreach (var toViewCall in FluentSyntaxHelpers.FindCallsNamed(scope, "ToView"))
            {
                var arguments = toViewCall.ArgumentList.Arguments;

                if (arguments.Count == 0
                    || arguments[0].Expression is not LiteralExpressionSyntax { } viewNameLiteral
                    || !viewNameLiteral.IsKind(SyntaxKind.StringLiteralExpression))
                {
                    diagnostics.Add(new Diagnostic(
                        DiagnosticCodes.UnreadableToViewArgument,
                        "ToView argument is not a string literal and could not be read.",
                        entityName,
                        PropertyName: null,
                        toViewCall.Span));
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
                            DiagnosticCodes.UnreadableToViewArgument,
                            "ToView schema argument is not a string literal and could not be read.",
                            entityName,
                            PropertyName: null,
                            toViewCall.Span));
                        continue;
                    }
                }

                results.Add(new ViewConfig(entityName, viewNameLiteral.Token.ValueText, schema));
            }
        }

        return new ParseResult<IReadOnlyList<ViewConfig>>(results, diagnostics);
    }

    public ParseResult<IReadOnlyList<SqlQueryConfig>> ParseSqlQueries(string sourceCode)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var results = new List<SqlQueryConfig>();
        var diagnostics = new List<Diagnostic>();

        foreach (var (entityName, scope) in FluentSyntaxHelpers.FindConfigurationScopes(root))
        {
            foreach (var toSqlQueryCall in FluentSyntaxHelpers.FindCallsNamed(scope, "ToSqlQuery"))
            {
                var arg = toSqlQueryCall.ArgumentList.Arguments.FirstOrDefault();

                if (arg?.Expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression))
                {
                    results.Add(new SqlQueryConfig(entityName, literal.Token.ValueText));
                }
                else
                {
                    diagnostics.Add(new Diagnostic(
                        DiagnosticCodes.UnreadableToSqlQueryArgument,
                        "ToSqlQuery argument is not a string literal and could not be read.",
                        entityName,
                        PropertyName: null,
                        (arg ?? (SyntaxNode)toSqlQueryCall).Span));
                }
            }
        }

        return new ParseResult<IReadOnlyList<SqlQueryConfig>>(results, diagnostics);
    }

    /// Reads bare `entity.HasNoKey()` calls (no arguments to misparse), so unlike every other
    /// `Parse*` method here there is nothing that can fail to read — no diagnostic/`ParseResult`
    /// wrapper is needed, matching `ParseIgnoredEntities`'s precedent for the same reason.
    public IReadOnlyList<string> ParseKeylessEntities(string sourceCode)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var results = new List<string>();

        foreach (var (entityName, scope) in FluentSyntaxHelpers.FindConfigurationScopes(root))
        {
            if (FluentSyntaxHelpers.FindCallsNamed(scope, "HasNoKey").Any())
            {
                results.Add(entityName);
            }
        }

        return results.Distinct().ToList();
    }

    /// Reads bare `entity.HasQueryFilter(expr)` calls — presence only. The predicate expression
    /// itself can't be meaningfully read or fail to read, so there's nothing an
    /// "unreadable argument" diagnostic could report; matches `ParseKeylessEntities`'s reasoning
    /// for the same no-`ParseResult`-wrapper shape, but returns the DTO (not a bare string list)
    /// to match every other `Parse*` method's return shape.
    public ParseResult<IReadOnlyList<QueryFilterConfig>> ParseQueryFilters(string sourceCode)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var results = new List<QueryFilterConfig>();

        foreach (var (entityName, scope) in FluentSyntaxHelpers.FindConfigurationScopes(root))
        {
            if (FluentSyntaxHelpers.FindCallsNamed(scope, "HasQueryFilter").Any())
            {
                results.Add(new QueryFilterConfig(entityName));
            }
        }

        return new ParseResult<IReadOnlyList<QueryFilterConfig>>(results, new List<Diagnostic>());
    }

    /// `HasComment` is legal chained directly onto the entity receiver (entity-level comment) or
    /// onto a `.Property(...)` call (property-level comment). `GetPropertyNameFor` returning null
    /// is the existing signal, used elsewhere, for "this call isn't property-scoped" — reused here
    /// to route each call to the right result list instead of guessing from call shape.
    public (ParseResult<IReadOnlyList<EntityCommentConfig>> Entities, ParseResult<IReadOnlyList<PropertyCommentConfig>> Properties)
        ParseComments(string sourceCode)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var entityResults = new List<EntityCommentConfig>();
        var entityDiagnostics = new List<Diagnostic>();
        var propertyResults = new List<PropertyCommentConfig>();
        var propertyDiagnostics = new List<Diagnostic>();

        foreach (var (entityName, scope) in FluentSyntaxHelpers.FindConfigurationScopes(root))
        {
            foreach (var call in FluentSyntaxHelpers.FindCallsNamed(scope, "HasComment"))
            {
                var propertyName = FluentSyntaxHelpers.GetPropertyNameFor(call);
                var arg = call.ArgumentList.Arguments.FirstOrDefault();
                var isReadableLiteral = arg?.Expression is LiteralExpressionSyntax literal
                    && literal.IsKind(SyntaxKind.StringLiteralExpression);

                if (propertyName is null)
                {
                    if (isReadableLiteral)
                    {
                        entityResults.Add(new EntityCommentConfig(
                            entityName, ((LiteralExpressionSyntax)arg!.Expression).Token.ValueText));
                    }
                    else
                    {
                        entityDiagnostics.Add(new Diagnostic(
                            DiagnosticCodes.UnreadableHasCommentArgument,
                            "HasComment argument is not a string literal and could not be read.",
                            entityName,
                            PropertyName: null,
                            (arg ?? (SyntaxNode)call).Span));
                    }
                }
                else
                {
                    if (isReadableLiteral)
                    {
                        propertyResults.Add(new PropertyCommentConfig(
                            entityName, propertyName, ((LiteralExpressionSyntax)arg!.Expression).Token.ValueText));
                    }
                    else
                    {
                        propertyDiagnostics.Add(new Diagnostic(
                            DiagnosticCodes.UnreadableHasCommentArgument,
                            "HasComment argument is not a string literal and could not be read.",
                            entityName,
                            propertyName,
                            (arg ?? (SyntaxNode)call).Span));
                    }
                }
            }
        }

        return (
            new ParseResult<IReadOnlyList<EntityCommentConfig>>(entityResults, entityDiagnostics),
            new ParseResult<IReadOnlyList<PropertyCommentConfig>>(propertyResults, propertyDiagnostics));
    }

    public ParseResult<IReadOnlyList<UnicodeConfig>> ParseUnicodeFlags(string sourceCode)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var results = new List<UnicodeConfig>();
        var diagnostics = new List<Diagnostic>();

        foreach (var (entityName, scope) in FluentSyntaxHelpers.FindConfigurationScopes(root))
        {
            foreach (var call in FluentSyntaxHelpers.FindCallsNamed(scope, "IsUnicode"))
            {
                var propertyName = FluentSyntaxHelpers.GetPropertyNameFor(call);

                if (propertyName is null)
                {
                    diagnostics.Add(new Diagnostic(
                        DiagnosticCodes.UnresolvablePropertyName,
                        "Could not determine which property this IsUnicode call configures.",
                        entityName,
                        PropertyName: null,
                        call.Span));
                    continue;
                }

                var arg = call.ArgumentList.Arguments.FirstOrDefault();

                if (arg is null)
                {
                    results.Add(new UnicodeConfig(entityName, propertyName, IsUnicode: true));
                    continue;
                }

                if (arg.Expression is LiteralExpressionSyntax literal
                    && (literal.IsKind(SyntaxKind.TrueLiteralExpression) || literal.IsKind(SyntaxKind.FalseLiteralExpression)))
                {
                    results.Add(new UnicodeConfig(entityName, propertyName, literal.IsKind(SyntaxKind.TrueLiteralExpression)));
                }
                else
                {
                    diagnostics.Add(new Diagnostic(
                        DiagnosticCodes.UnreadableIsUnicodeArgument,
                        "IsUnicode argument is not a boolean literal and could not be read.",
                        entityName,
                        propertyName,
                        arg.Span));
                }
            }
        }

        return new ParseResult<IReadOnlyList<UnicodeConfig>>(results, diagnostics);
    }

    public ParseResult<IReadOnlyList<FixedLengthConfig>> ParseFixedLengthFlags(string sourceCode)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var results = new List<FixedLengthConfig>();
        var diagnostics = new List<Diagnostic>();

        foreach (var (entityName, scope) in FluentSyntaxHelpers.FindConfigurationScopes(root))
        {
            foreach (var call in FluentSyntaxHelpers.FindCallsNamed(scope, "IsFixedLength"))
            {
                var propertyName = FluentSyntaxHelpers.GetPropertyNameFor(call);

                if (propertyName is null)
                {
                    diagnostics.Add(new Diagnostic(
                        DiagnosticCodes.UnresolvablePropertyName,
                        "Could not determine which property this IsFixedLength call configures.",
                        entityName,
                        PropertyName: null,
                        call.Span));
                    continue;
                }

                var arg = call.ArgumentList.Arguments.FirstOrDefault();

                if (arg is null)
                {
                    results.Add(new FixedLengthConfig(entityName, propertyName, IsFixedLength: true));
                    continue;
                }

                if (arg.Expression is LiteralExpressionSyntax literal
                    && (literal.IsKind(SyntaxKind.TrueLiteralExpression) || literal.IsKind(SyntaxKind.FalseLiteralExpression)))
                {
                    results.Add(new FixedLengthConfig(entityName, propertyName, literal.IsKind(SyntaxKind.TrueLiteralExpression)));
                }
                else
                {
                    diagnostics.Add(new Diagnostic(
                        DiagnosticCodes.UnreadableIsFixedLengthArgument,
                        "IsFixedLength argument is not a boolean literal and could not be read.",
                        entityName,
                        propertyName,
                        arg.Span));
                }
            }
        }

        return new ParseResult<IReadOnlyList<FixedLengthConfig>>(results, diagnostics);
    }

    public ParseResult<IReadOnlyList<CollationConfig>> ParseCollations(string sourceCode)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var results = new List<CollationConfig>();
        var diagnostics = new List<Diagnostic>();

        foreach (var (entityName, scope) in FluentSyntaxHelpers.FindConfigurationScopes(root))
        {
            foreach (var call in FluentSyntaxHelpers.FindCallsNamed(scope, "UseCollation"))
            {
                var propertyName = FluentSyntaxHelpers.GetPropertyNameFor(call);

                if (propertyName is null)
                {
                    diagnostics.Add(new Diagnostic(
                        DiagnosticCodes.UnresolvablePropertyName,
                        "Could not determine which property this UseCollation call configures.",
                        entityName,
                        PropertyName: null,
                        call.Span));
                    continue;
                }

                var arg = call.ArgumentList.Arguments.FirstOrDefault();

                if (arg?.Expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression))
                {
                    results.Add(new CollationConfig(entityName, propertyName, literal.Token.ValueText));
                }
                else
                {
                    diagnostics.Add(new Diagnostic(
                        DiagnosticCodes.UnreadableUseCollationArgument,
                        "UseCollation argument is not a string literal and could not be read.",
                        entityName,
                        propertyName,
                        (arg ?? (SyntaxNode)call).Span));
                }
            }
        }

        return new ParseResult<IReadOnlyList<CollationConfig>>(results, diagnostics);
    }

    public ParseResult<IReadOnlyList<JsonConfig>> ParseJsonMappings(string sourceCode)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var results = new List<JsonConfig>();
        var diagnostics = new List<Diagnostic>();

        foreach (var (entityName, scope) in FluentSyntaxHelpers.FindConfigurationScopes(root))
        {
            foreach (var call in FluentSyntaxHelpers.FindCallsNamed(scope, "ToJson"))
            {
                var arg = call.ArgumentList.Arguments.FirstOrDefault();

                if (arg is null)
                {
                    results.Add(new JsonConfig(entityName, null));
                }
                else if (arg.Expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression))
                {
                    results.Add(new JsonConfig(entityName, literal.Token.ValueText));
                }
                else
                {
                    diagnostics.Add(new Diagnostic(
                        DiagnosticCodes.UnreadableToJsonArgument,
                        "ToJson argument is not a string literal and could not be read.",
                        entityName,
                        PropertyName: null,
                        arg.Span));
                }
            }
        }

        return new ParseResult<IReadOnlyList<JsonConfig>>(results, diagnostics);
    }

    /// Only the secondary table name is read; the builder lambda's per-property table assignment
    /// is not modeled (same scope cut as `ToTable(b => b.IsTemporal())`'s config-lambda internals).
    /// `FindCallsNamed` finds every `SplitToTable` call in the scope regardless of how many are
    /// chained, so an entity split across three or more tables yields one config per call.
    public ParseResult<IReadOnlyList<SplitToTableConfig>> ParseSplitTables(string sourceCode)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var results = new List<SplitToTableConfig>();
        var diagnostics = new List<Diagnostic>();

        foreach (var (entityName, scope) in FluentSyntaxHelpers.FindConfigurationScopes(root))
        {
            foreach (var call in FluentSyntaxHelpers.FindCallsNamed(scope, "SplitToTable"))
            {
                var arg = call.ArgumentList.Arguments.FirstOrDefault();

                if (arg?.Expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression))
                {
                    results.Add(new SplitToTableConfig(entityName, literal.Token.ValueText));
                }
                else
                {
                    diagnostics.Add(new Diagnostic(
                        DiagnosticCodes.UnreadableSplitToTableArgument,
                        "SplitToTable table name argument is not a string literal and could not be read.",
                        entityName,
                        PropertyName: null,
                        (arg ?? (SyntaxNode)call).Span));
                }
            }
        }

        return new ParseResult<IReadOnlyList<SplitToTableConfig>>(results, diagnostics);
    }

    public ParseResult<IReadOnlyList<ColumnNameConfig>> ParseColumnNames(string sourceCode)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var results = new List<ColumnNameConfig>();
        var diagnostics = new List<Diagnostic>();

        foreach (var (entityName, scope) in FluentSyntaxHelpers.FindConfigurationScopes(root))
        {
            foreach (var call in FluentSyntaxHelpers.FindCallsNamed(scope, "HasColumnName"))
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
                    results.Add(new ColumnNameConfig(entityName, propertyName, literal.Token.ValueText));
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

        return new ParseResult<IReadOnlyList<ColumnNameConfig>>(results, diagnostics);
    }

    public ParseResult<IReadOnlyList<ColumnTypeConfig>> ParseColumnTypes(string sourceCode)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var results = new List<ColumnTypeConfig>();
        var diagnostics = new List<Diagnostic>();

        foreach (var (entityName, scope) in FluentSyntaxHelpers.FindConfigurationScopes(root))
        {
            foreach (var call in FluentSyntaxHelpers.FindCallsNamed(scope, "HasColumnType"))
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
                    results.Add(new ColumnTypeConfig(entityName, propertyName, literal.Token.ValueText));
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

        return new ParseResult<IReadOnlyList<ColumnTypeConfig>>(results, diagnostics);
    }

    public ParseResult<IReadOnlyList<DefaultValueConfig>> ParseDefaultValues(string sourceCode)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var results = new List<DefaultValueConfig>();
        var diagnostics = new List<Diagnostic>();

        foreach (var (entityName, scope) in FluentSyntaxHelpers.FindConfigurationScopes(root))
        {
            foreach (var call in FluentSyntaxHelpers.FindCallsNamed(scope, "HasDefaultValue"))
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
                    results.Add(new DefaultValueConfig(entityName, propertyName, literal.ToString()));
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

        return new ParseResult<IReadOnlyList<DefaultValueConfig>>(results, diagnostics);
    }

    public ParseResult<IReadOnlyList<DefaultValueSqlConfig>> ParseDefaultValueSqls(string sourceCode)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var results = new List<DefaultValueSqlConfig>();
        var diagnostics = new List<Diagnostic>();

        foreach (var (entityName, scope) in FluentSyntaxHelpers.FindConfigurationScopes(root))
        {
            foreach (var call in FluentSyntaxHelpers.FindCallsNamed(scope, "HasDefaultValueSql"))
            {
                var propertyName = FluentSyntaxHelpers.GetPropertyNameFor(call);

                if (propertyName is null)
                {
                    diagnostics.Add(new Diagnostic(
                        DiagnosticCodes.UnresolvablePropertyName,
                        "Could not determine which property this HasDefaultValueSql call configures.",
                        entityName,
                        PropertyName: null,
                        call.Span));
                    continue;
                }

                var arg = call.ArgumentList.Arguments.FirstOrDefault();

                if (arg?.Expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression))
                {
                    results.Add(new DefaultValueSqlConfig(entityName, propertyName, literal.Token.ValueText));
                }
                else
                {
                    diagnostics.Add(new Diagnostic(
                        DiagnosticCodes.UnreadableHasDefaultValueSqlArgument,
                        "HasDefaultValueSql argument is not a string literal and could not be read.",
                        entityName,
                        propertyName,
                        (arg ?? (SyntaxNode)call).Span));
                }
            }
        }

        return new ParseResult<IReadOnlyList<DefaultValueSqlConfig>>(results, diagnostics);
    }

    public ParseResult<IReadOnlyList<IndexConfig>> ParseIndexes(string sourceCode)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var results = new List<IndexConfig>();
        var diagnostics = new List<Diagnostic>();

        foreach (var (entityName, scope) in FluentSyntaxHelpers.FindConfigurationScopes(root))
        {
            foreach (var hasIndexCall in FluentSyntaxHelpers.FindCallsNamed(scope, "HasIndex"))
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

                var extras = ReadIndexExtras(hasIndexCall, entityName);
                diagnostics.AddRange(extras.Diagnostics);

                results.Add(new IndexConfig(
                    entityName,
                    indexArgs.Value.PropertyNames,
                    extras.IsUnique,
                    indexArgs.Value.Name,
                    extras.Filter,
                    extras.IsDescending,
                    extras.IncludeProperties));
            }
        }

        return new ParseResult<IReadOnlyList<IndexConfig>>(results, diagnostics);
    }

    public ParseResult<IReadOnlyList<IgnoreConfig>> ParseIgnoredProperties(string sourceCode)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var results = new List<IgnoreConfig>();
        var diagnostics = new List<Diagnostic>();

        foreach (var (entityName, scope) in FluentSyntaxHelpers.FindConfigurationScopes(root))
        {
            foreach (var call in FluentSyntaxHelpers.FindCallsNamed(scope, "Ignore"))
            {
                var propertyName = FluentSyntaxHelpers.TryReadSinglePropertyNameArgument(call);

                if (propertyName is null)
                {
                    diagnostics.Add(new Diagnostic(
                        DiagnosticCodes.UnreadableIgnoreArgument,
                        "Ignore argument could not be read as a property name.",
                        entityName,
                        PropertyName: null,
                        call.Span));
                    continue;
                }

                results.Add(new IgnoreConfig(entityName, propertyName));
            }
        }

        return new ParseResult<IReadOnlyList<IgnoreConfig>>(results, diagnostics);
    }

    private static readonly Dictionary<string, string> ValueGenerationCallModes = new()
    {
        ["ValueGeneratedOnAdd"] = "OnAdd",
        ["ValueGeneratedOnUpdate"] = "OnUpdate",
        ["ValueGeneratedOnAddOrUpdate"] = "OnAddOrUpdate",
        ["ValueGeneratedNever"] = "Never",
        ["UseIdentityColumn"] = "Identity",
    };

    public ParseResult<IReadOnlyList<ValueGenerationConfig>> ParseValueGeneration(string sourceCode)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var results = new List<ValueGenerationConfig>();
        var diagnostics = new List<Diagnostic>();

        foreach (var (entityName, scope) in FluentSyntaxHelpers.FindConfigurationScopes(root))
        {
            foreach (var (callName, mode) in ValueGenerationCallModes)
            {
                foreach (var call in FluentSyntaxHelpers.FindCallsNamed(scope, callName))
                {
                    var propertyName = FluentSyntaxHelpers.GetPropertyNameFor(call);

                    if (propertyName is null)
                    {
                        diagnostics.Add(new Diagnostic(
                            DiagnosticCodes.UnresolvablePropertyName,
                            $"Could not resolve the property configured by '{callName}'.",
                            entityName,
                            PropertyName: null,
                            call.Span));
                        continue;
                    }

                    results.Add(new ValueGenerationConfig(entityName, propertyName, mode));
                }
            }
        }

        return new ParseResult<IReadOnlyList<ValueGenerationConfig>>(results, diagnostics);
    }

    public ParseResult<IReadOnlyList<ConcurrencyTokenConfig>> ParseConcurrencyTokens(string sourceCode)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var diagnostics = new List<Diagnostic>();
        var flagsByProperty = new Dictionary<(string EntityName, string PropertyName), (bool IsRowVersion, bool IsConcurrencyToken)>();

        foreach (var (entityName, scope) in FluentSyntaxHelpers.FindConfigurationScopes(root))
        {
            foreach (var (callName, marksRowVersion) in new[] { ("IsRowVersion", true), ("IsConcurrencyToken", false) })
            {
                foreach (var call in FluentSyntaxHelpers.FindCallsNamed(scope, callName))
                {
                    var propertyName = FluentSyntaxHelpers.GetPropertyNameFor(call);

                    if (propertyName is null)
                    {
                        diagnostics.Add(new Diagnostic(
                            DiagnosticCodes.UnresolvablePropertyName,
                            $"Could not resolve the property configured by '{callName}'.",
                            entityName,
                            PropertyName: null,
                            call.Span));
                        continue;
                    }

                    var key = (entityName, propertyName);
                    var existing = flagsByProperty.GetValueOrDefault(key);
                    flagsByProperty[key] = marksRowVersion
                        ? (true, existing.IsConcurrencyToken)
                        : (existing.IsRowVersion, true);
                }
            }
        }

        var results = flagsByProperty
            .Select(kvp => new ConcurrencyTokenConfig(kvp.Key.EntityName, kvp.Key.PropertyName, kvp.Value.IsRowVersion, kvp.Value.IsConcurrencyToken))
            .ToList();

        return new ParseResult<IReadOnlyList<ConcurrencyTokenConfig>>(results, diagnostics);
    }

    public ParseResult<IReadOnlyList<ShadowPropertyConfig>> ParseShadowProperties(string sourceCode)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var results = new List<ShadowPropertyConfig>();

        foreach (var (entityName, scope) in FluentSyntaxHelpers.FindConfigurationScopes(root))
        {
            foreach (var propertyCall in FluentSyntaxHelpers.FindCallsNamed(scope, "Property"))
            {
                if (propertyCall.Expression is not MemberAccessExpressionSyntax
                    {
                        Name: GenericNameSyntax { TypeArgumentList.Arguments: [var typeArgNode] },
                    })
                {
                    continue;
                }

                if (propertyCall.ArgumentList.Arguments.FirstOrDefault()?.Expression is not LiteralExpressionSyntax literal
                    || !literal.IsKind(SyntaxKind.StringLiteralExpression))
                {
                    continue;
                }

                results.Add(new ShadowPropertyConfig(entityName, literal.Token.ValueText, typeArgNode.ToString()));
            }
        }

        return new ParseResult<IReadOnlyList<ShadowPropertyConfig>>(results, new List<Diagnostic>());
    }

    /// Reads bare `modelBuilder.Ignore<T>()` calls (whole-entity ignore). Distinguished from the
    /// property-level `entity.Ignore(e => e.X)` / `entity.Ignore("X")` overloads by shape alone:
    /// this one is always generic with zero arguments, the property-level one is always
    /// non-generic with exactly one argument, so no scope/receiver disambiguation is needed.
    public IReadOnlyList<string> ParseIgnoredEntities(string sourceCode)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var results = new List<string>();

        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax
                {
                    Name: GenericNameSyntax { Identifier.Text: "Ignore", TypeArgumentList.Arguments: [var typeArg] },
                })
            {
                continue;
            }

            if (invocation.ArgumentList.Arguments.Count != 0)
            {
                continue;
            }

            results.Add(typeArg.ToString());
        }

        return results.Distinct().ToList();
    }

    public ParseResult<IReadOnlyList<RelationshipConfig>> ParseRelationships(
        string sourceCode, IReadOnlyList<EntityModel> entities)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var results = new List<RelationshipConfig>();
        var diagnostics = new List<Diagnostic>();

        foreach (var (entityName, scope) in FluentSyntaxHelpers.FindConfigurationScopes(root))
        {
            var calls = FluentSyntaxHelpers.FindCallsNamed(scope, "HasOne")
                .Concat(FluentSyntaxHelpers.FindCallsNamed(scope, "HasMany"))
                .ToList();

            // The bare-chained style (`modelBuilder.Entity<Order>().HasOne(...)...`, with
            // no lambda block) only exists when the scope itself is the `Entity<T>()`
            // invocation. A `Configure` method declaration has nothing chained onto it.
            if (scope is InvocationExpressionSyntax entityInvocation)
            {
                if (FluentSyntaxHelpers.FindChainedCall(entityInvocation, "HasOne") is { } chainedHasOne)
                {
                    calls.Add(chainedHasOne);
                }

                if (FluentSyntaxHelpers.FindChainedCall(entityInvocation, "HasMany") is { } chainedHasMany)
                {
                    calls.Add(chainedHasMany);
                }
            }

            foreach (var call in calls)
            {
                ParseRelationshipChain(call, entityName, entities, results, diagnostics);
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

        FluentSyntaxHelpers.WalkChainedTail(withCall, invocation =>
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

    private sealed record IndexExtras(
        bool IsUnique,
        string? Filter,
        IReadOnlyList<bool>? IsDescending,
        IReadOnlyList<string>? IncludeProperties,
        IReadOnlyList<Diagnostic> Diagnostics);

    /// Walks every call chained onto a `HasIndex(...)` invocation (in any order — EF allows
    /// `IsUnique`/`HasFilter`/`IsDescending`/`IncludeProperties` in any sequence) and reads each
    /// recognized one. Unreadable arguments are reported as diagnostics but don't stop the walk.
    private static IndexExtras ReadIndexExtras(InvocationExpressionSyntax hasIndexCall, string entityName)
    {
        var isUnique = false;
        string? filter = null;
        IReadOnlyList<bool>? isDescending = null;
        IReadOnlyList<string>? includeProperties = null;
        var diagnostics = new List<Diagnostic>();

        FluentSyntaxHelpers.WalkChainedTail(hasIndexCall, chained =>
        {
            if (chained.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: var methodName })
                return;

            switch (methodName)
            {
                case "IsUnique":
                    {
                        var arg = chained.ArgumentList.Arguments.FirstOrDefault();
                        if (arg is null)
                        {
                            isUnique = true;
                            break;
                        }

                        if (arg.Expression is LiteralExpressionSyntax literal
                            && (literal.IsKind(SyntaxKind.TrueLiteralExpression) || literal.IsKind(SyntaxKind.FalseLiteralExpression)))
                        {
                            isUnique = literal.IsKind(SyntaxKind.TrueLiteralExpression);
                            break;
                        }

                        diagnostics.Add(new Diagnostic(
                            DiagnosticCodes.UnreadableIsUniqueArgument,
                            "IsUnique argument is not a boolean literal and could not be read.",
                            entityName,
                            PropertyName: null,
                            arg.Span));
                        break;
                    }

                case "HasFilter":
                    {
                        var arg = chained.ArgumentList.Arguments.FirstOrDefault();
                        if (arg?.Expression is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression } literal)
                        {
                            filter = literal.Token.ValueText;
                            break;
                        }

                        diagnostics.Add(new Diagnostic(
                            DiagnosticCodes.UnreadableHasFilterArgument,
                            "HasFilter argument is not a string literal and could not be read.",
                            entityName,
                            PropertyName: null,
                            (arg ?? (SyntaxNode)chained).Span));
                        break;
                    }

                case "IsDescending":
                    {
                        var arguments = chained.ArgumentList.Arguments;
                        if (arguments.Count == 0)
                        {
                            isDescending = Array.Empty<bool>();
                            break;
                        }

                        var values = new List<bool>();
                        var allLiteral = true;
                        foreach (var arg in arguments)
                        {
                            if (arg.Expression is LiteralExpressionSyntax literal
                                && (literal.IsKind(SyntaxKind.TrueLiteralExpression) || literal.IsKind(SyntaxKind.FalseLiteralExpression)))
                            {
                                values.Add(literal.IsKind(SyntaxKind.TrueLiteralExpression));
                            }
                            else
                            {
                                allLiteral = false;
                                break;
                            }
                        }

                        if (allLiteral)
                        {
                            isDescending = values;
                            break;
                        }

                        diagnostics.Add(new Diagnostic(
                            DiagnosticCodes.UnreadableIsDescendingArgument,
                            "IsDescending argument(s) are not boolean literals and could not be read.",
                            entityName,
                            PropertyName: null,
                            chained.ArgumentList.Span));
                        break;
                    }

                case "IncludeProperties":
                    {
                        var names = FluentSyntaxHelpers.TryReadPropertyNameList(chained);
                        if (names is not null)
                        {
                            includeProperties = names;
                            break;
                        }

                        diagnostics.Add(new Diagnostic(
                            DiagnosticCodes.UnreadableIncludePropertiesArgument,
                            "IncludeProperties argument(s) could not be read as property name(s).",
                            entityName,
                            PropertyName: null,
                            chained.ArgumentList.Span));
                        break;
                    }
            }
        });

        return new IndexExtras(isUnique, filter, isDescending, includeProperties, diagnostics);
    }
}
