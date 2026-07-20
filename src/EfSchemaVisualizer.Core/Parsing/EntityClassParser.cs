using System.Collections.Generic;
using System.Linq;
using EfSchemaVisualizer.Core.Merging;
using EfSchemaVisualizer.Core.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EfSchemaVisualizer.Core.Parsing;

public sealed class EntityClassParser
{
    public ParseResult<IReadOnlyList<EntityModel>> Parse(string sourceCode)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var allTypeDeclarations = root.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .Where(t => t is ClassDeclarationSyntax or StructDeclarationSyntax or RecordDeclarationSyntax)
            .ToList();

        var typeDeclarations = allTypeDeclarations
            .Where(t => !t.Ancestors().OfType<TypeDeclarationSyntax>().Any())
            .ToList();

        var diagnostics = new List<Diagnostic>();

        foreach (var nested in allTypeDeclarations.Except(typeDeclarations))
        {
            var enclosing = nested.Ancestors().OfType<TypeDeclarationSyntax>().First();
            diagnostics.Add(new Diagnostic(
                DiagnosticCodes.NestedTypeDeclaration,
                $"'{nested.Identifier.Text}' is nested inside '{enclosing.Identifier.Text}' and was skipped; nested type declarations are not parsed as entities.",
                nested.Identifier.Text,
                PropertyName: null,
                nested.Span));
        }

        if (typeDeclarations.Count == 0)
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticCodes.NoEntityDeclarations,
                "No class, record, or struct declarations found in file; nothing to parse.",
                EntityName: null,
                PropertyName: null,
                root.Span));

            return new ParseResult<IReadOnlyList<EntityModel>>(new List<EntityModel>(), diagnostics);
        }

        var entities = typeDeclarations.Select(ParseEntity).ToList();

        foreach (var duplicateGroup in entities.GroupBy(e => e.Name).Where(g => g.Count() > 1))
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticCodes.DuplicateEntityName,
                $"{duplicateGroup.Count()} entity declarations share the name '{duplicateGroup.Key}'; only the first is used.",
                duplicateGroup.Key,
                PropertyName: null,
                root.Span));
        }

        var deduplicatedEntities = entities
            .GroupBy(e => e.Name)
            .Select(g => g.First())
            .ToList();

        return new ParseResult<IReadOnlyList<EntityModel>>(deduplicatedEntities, diagnostics);
    }

    private static EntityModel ParseEntity(TypeDeclarationSyntax typeDeclaration)
    {
        var positionalProperties = typeDeclaration is RecordDeclarationSyntax
            ? typeDeclaration.ParameterList?.Parameters.Select(ParseParameterProperty) ?? Enumerable.Empty<PropertyModel>()
            : Enumerable.Empty<PropertyModel>();

        var mappedProperties = typeDeclaration.Members
            .OfType<PropertyDeclarationSyntax>()
            .Where(IsMappedInstanceProperty)
            .ToList();

        var bodyProperties = mappedProperties.Select(ParseProperty);

        var properties = positionalProperties.Concat(bodyProperties).ToList();

        var keyPropertyNames = ResolveKeyPropertyNames(mappedProperties);
        var (tableName, schema) = ParseTableAttribute(typeDeclaration.AttributeLists);
        var isKeyless = FindAttribute(typeDeclaration.AttributeLists, "Keyless") is not null;

        return new EntityModel(
            typeDeclaration.Identifier.Text,
            properties,
            keyPropertyNames,
            TableName: tableName,
            Schema: schema,
            IsKeyless: isKeyless);
    }

    private static IReadOnlyList<string> ResolveKeyPropertyNames(List<PropertyDeclarationSyntax> mappedProperties)
    {
        var keyedProperties = mappedProperties
            .Where(p => FindAttribute(p.AttributeLists, "Key") is not null)
            .ToList();

        if (keyedProperties.Count == 0)
        {
            return new List<string>();
        }

        return keyedProperties
            .Select((p, index) => (Name: p.Identifier.Text, Order: GetColumnOrder(p), DeclarationIndex: index))
            .OrderBy(k => k.Order ?? int.MaxValue)
            .ThenBy(k => k.DeclarationIndex)
            .Select(k => k.Name)
            .ToList();
    }

    private static int? GetColumnOrder(PropertyDeclarationSyntax property)
    {
        return FindAttribute(property.AttributeLists, "Column") is { } columnAttr
            ? TryReadIntArg(GetNamedArg(columnAttr, "Order"))
            : null;
    }

    private static (string? TableName, string? Schema) ParseTableAttribute(SyntaxList<AttributeListSyntax> attributeLists)
    {
        if (FindAttribute(attributeLists, "Table") is not { } tableAttr)
        {
            return (null, null);
        }

        var tableName = TryReadStringArg(GetPositionalArg(tableAttr, 0));
        var schema = TryReadStringArg(GetNamedArg(tableAttr, "Schema"));
        return (tableName, schema);
    }

    public ParseResult<IReadOnlyList<IndexConfig>> ParseIndexAttributes(string sourceCode)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var typeDeclarations = root.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .Where(t => t is ClassDeclarationSyntax or StructDeclarationSyntax or RecordDeclarationSyntax)
            .Where(t => !t.Ancestors().OfType<TypeDeclarationSyntax>().Any());

        var results = new List<IndexConfig>();
        var diagnostics = new List<Diagnostic>();

        foreach (var typeDeclaration in typeDeclarations)
        {
            var entityName = typeDeclaration.Identifier.Text;

            foreach (var indexAttr in FindAttributes(typeDeclaration.AttributeLists, "Index"))
            {
                var positionalArgs = indexAttr.ArgumentList?.Arguments
                    .Where(a => a.NameEquals is null)
                    .ToList() ?? new List<AttributeArgumentSyntax>();

                if (positionalArgs.Count == 0)
                {
                    diagnostics.Add(new Diagnostic(
                        DiagnosticCodes.UnreadableHasIndexArgument,
                        "[Index] attribute has no property name arguments.",
                        entityName,
                        PropertyName: null,
                        indexAttr.Span));
                    continue;
                }

                var propertyNames = new List<string>();
                var unresolved = false;

                foreach (var arg in positionalArgs)
                {
                    var propertyName = TryReadIndexAttributePropertyName(arg);
                    if (propertyName is null)
                    {
                        unresolved = true;
                        break;
                    }

                    propertyNames.Add(propertyName);
                }

                if (unresolved)
                {
                    diagnostics.Add(new Diagnostic(
                        DiagnosticCodes.UnreadableHasIndexArgument,
                        "[Index] attribute argument(s) could not be read as property name(s).",
                        entityName,
                        PropertyName: null,
                        indexAttr.Span));
                    continue;
                }

                var isUnique = TryReadBoolArg(GetNamedArg(indexAttr, "IsUnique"));
                var name = TryReadStringArg(GetNamedArg(indexAttr, "Name"));

                results.Add(new IndexConfig(entityName, propertyNames, isUnique, name));
            }
        }

        return new ParseResult<IReadOnlyList<IndexConfig>>(results, diagnostics);
    }

    private static PropertyModel ParseParameterProperty(ParameterSyntax parameter)
    {
        var type = parameter.Type!;
        var isNullable = type is NullableTypeSyntax nullableType;
        var clrType = type is NullableTypeSyntax nullable
            ? nullable.ElementType.ToString()
            : type.ToString();

        return new PropertyModel(parameter.Identifier.Text, clrType, isNullable, MaxLength: null);
    }

    private static PropertyModel ParseProperty(PropertyDeclarationSyntax property)
    {
        var isNullable = property.Type is NullableTypeSyntax;
        var clrType = property.Type is NullableTypeSyntax nullableType
            ? nullableType.ElementType.ToString()
            : property.Type.ToString();

        var attributeLists = property.AttributeLists;

        bool? isRequiredOverride = FindAttribute(attributeLists, "Required") is not null ? true : null;

        int? maxLength = null;
        if (FindAttribute(attributeLists, "MaxLength") is { } maxLengthAttr)
        {
            maxLength = TryReadIntArg(GetPositionalArg(maxLengthAttr, 0));
        }
        else if (FindAttribute(attributeLists, "StringLength") is { } stringLengthAttr)
        {
            maxLength = TryReadIntArg(GetPositionalArg(stringLengthAttr, 0));
        }

        string? columnName = null;
        string? columnType = null;
        if (FindAttribute(attributeLists, "Column") is { } columnAttr)
        {
            columnName = TryReadStringArg(GetPositionalArg(columnAttr, 0))
                ?? TryReadStringArg(GetNamedArg(columnAttr, "Name"));
            columnType = TryReadStringArg(GetNamedArg(columnAttr, "TypeName"));
        }

        int? precision = null;
        int? scale = null;
        if (FindAttribute(attributeLists, "Precision") is { } precisionAttr)
        {
            precision = TryReadIntArg(GetPositionalArg(precisionAttr, 0));
            scale = TryReadIntArg(GetPositionalArg(precisionAttr, 1));
        }

        return new PropertyModel(
            property.Identifier.Text,
            clrType,
            isNullable,
            maxLength,
            isRequiredOverride,
            precision,
            scale,
            columnName,
            columnType);
    }

    private static bool IsMappedInstanceProperty(PropertyDeclarationSyntax property)
    {
        if (property.Modifiers.Any(SyntaxKind.StaticKeyword))
        {
            return false;
        }

        if (HasNotMappedAttribute(property.AttributeLists))
        {
            return false;
        }

        if (property.ExpressionBody is not null)
        {
            return false;
        }

        var hasSetter = property.AccessorList?.Accessors
            .Any(a => a.Kind() is SyntaxKind.SetAccessorDeclaration or SyntaxKind.InitAccessorDeclaration) ?? false;

        return hasSetter;
    }

    private static bool HasNotMappedAttribute(SyntaxList<AttributeListSyntax> attributeLists)
    {
        return FindAttribute(attributeLists, "NotMapped") is not null;
    }

    private static AttributeSyntax? FindAttribute(SyntaxList<AttributeListSyntax> attributeLists, string simpleName)
    {
        return attributeLists
            .SelectMany(list => list.Attributes)
            .FirstOrDefault(attribute => attribute.Name.ToString() is var name
                && (name == simpleName || name == simpleName + "Attribute"));
    }

    private static AttributeArgumentSyntax? GetPositionalArg(AttributeSyntax attribute, int index)
    {
        var positional = attribute.ArgumentList?.Arguments
            .Where(a => a.NameEquals is null)
            .ToList();

        return positional is not null && index < positional.Count ? positional[index] : null;
    }

    private static AttributeArgumentSyntax? GetNamedArg(AttributeSyntax attribute, string name)
    {
        return attribute.ArgumentList?.Arguments
            .FirstOrDefault(a => a.NameEquals?.Name.Identifier.Text == name);
    }

    private static string? TryReadStringArg(AttributeArgumentSyntax? arg)
    {
        return arg?.Expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression)
            ? literal.Token.ValueText
            : null;
    }

    private static int? TryReadIntArg(AttributeArgumentSyntax? arg)
    {
        return arg is not null && int.TryParse(arg.Expression.ToString(), out var value) ? value : null;
    }

    private static IEnumerable<AttributeSyntax> FindAttributes(SyntaxList<AttributeListSyntax> attributeLists, string simpleName)
    {
        return attributeLists
            .SelectMany(list => list.Attributes)
            .Where(attribute => attribute.Name.ToString() is var name
                && (name == simpleName || name == simpleName + "Attribute"));
    }

    private static string? TryReadIndexAttributePropertyName(AttributeArgumentSyntax arg)
    {
        return arg.Expression switch
        {
            LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.StringLiteralExpression) => literal.Token.ValueText,
            InvocationExpressionSyntax
            {
                Expression: IdentifierNameSyntax { Identifier.Text: "nameof" },
                ArgumentList.Arguments: [var nameofArg],
            } => nameofArg.Expression switch
            {
                IdentifierNameSyntax id => id.Identifier.Text,
                MemberAccessExpressionSyntax { Name.Identifier.Text: var memberName } => memberName,
                _ => null,
            },
            _ => null,
        };
    }

    private static bool TryReadBoolArg(AttributeArgumentSyntax? arg)
    {
        return arg?.Expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.TrueLiteralExpression);
    }

    public ParseResult<IReadOnlyList<RelationshipConfig>> ParseRelationships(
        string sourceCode, IReadOnlyList<EntityModel> entities)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var results = new List<RelationshipConfig>();

        var typeDeclarations = root.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .Where(t => t is ClassDeclarationSyntax or StructDeclarationSyntax or RecordDeclarationSyntax)
            .Where(t => !t.Ancestors().OfType<TypeDeclarationSyntax>().Any());

        foreach (var typeDeclaration in typeDeclarations)
        {
            var dependentEntityName = typeDeclaration.Identifier.Text;

            foreach (var property in typeDeclaration.Members.OfType<PropertyDeclarationSyntax>())
            {
                if (FindAttribute(property.AttributeLists, "ForeignKey") is not { } foreignKeyAttr)
                {
                    continue;
                }

                var pairedPropertyName = TryReadStringArg(GetPositionalArg(foreignKeyAttr, 0));
                if (pairedPropertyName is null)
                {
                    continue;
                }

                var pairedProperty = typeDeclaration.Members
                    .OfType<PropertyDeclarationSyntax>()
                    .FirstOrDefault(p => p.Identifier.Text == pairedPropertyName);

                if (pairedProperty is null)
                {
                    continue;
                }

                var relationship = TryResolveForeignKeyRelationship(dependentEntityName, property, pairedProperty, entities);
                if (relationship is not null)
                {
                    results.Add(relationship);
                }
            }
        }

        var deduplicated = results
            .GroupBy(r => (r.PrincipalEntity, r.DependentEntity, Fk: string.Join(",", r.ForeignKeyProperties)))
            .Select(g => g.First())
            .ToList();

        return new ParseResult<IReadOnlyList<RelationshipConfig>>(deduplicated, new List<Diagnostic>());
    }

    private static RelationshipConfig? TryResolveForeignKeyRelationship(
        string dependentEntityName,
        PropertyDeclarationSyntax annotatedProperty,
        PropertyDeclarationSyntax pairedProperty,
        IReadOnlyList<EntityModel> entities)
    {
        PropertyDeclarationSyntax navigationProperty;
        PropertyDeclarationSyntax fkProperty;

        if (TryGetNavigationTargetEntity(annotatedProperty, entities) is not null)
        {
            navigationProperty = annotatedProperty;
            fkProperty = pairedProperty;
        }
        else if (TryGetNavigationTargetEntity(pairedProperty, entities) is not null)
        {
            navigationProperty = pairedProperty;
            fkProperty = annotatedProperty;
        }
        else
        {
            return null;
        }

        var principalEntityName = TryGetNavigationTargetEntity(navigationProperty, entities)!;
        var principalEntity = entities.FirstOrDefault(e => e.Name == principalEntityName);
        if (principalEntity is null)
        {
            return null;
        }

        var (kind, principalNavigation) = FindPrincipalBackReference(principalEntity, dependentEntityName);

        return new RelationshipConfig(
            principalEntityName,
            dependentEntityName,
            kind,
            principalNavigation,
            navigationProperty.Identifier.Text,
            new List<string> { fkProperty.Identifier.Text });
    }

    private static string? TryGetNavigationTargetEntity(PropertyDeclarationSyntax property, IReadOnlyList<EntityModel> entities)
    {
        var typeText = property.Type is NullableTypeSyntax nullableType
            ? nullableType.ElementType.ToString()
            : property.Type.ToString();

        return entities.Any(e => e.Name == typeText) ? typeText : null;
    }

    private static (RelationshipKind Kind, string? PrincipalNavigation) FindPrincipalBackReference(
        EntityModel principalEntity, string dependentEntityName)
    {
        foreach (var property in principalEntity.Properties)
        {
            var elementTypeName = FluentSyntaxHelpers.TryGetElementTypeName(property.ClrType);

            if (elementTypeName != dependentEntityName)
            {
                continue;
            }

            var isCollection = elementTypeName != property.ClrType;
            return isCollection
                ? (RelationshipKind.OneToMany, property.Name)
                : (RelationshipKind.OneToOne, property.Name);
        }

        return (RelationshipKind.OneToMany, null);
    }
}
