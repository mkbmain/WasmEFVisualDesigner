using System.Collections.Generic;
using System.Linq;
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

        var bodyProperties = typeDeclaration.Members
            .OfType<PropertyDeclarationSyntax>()
            .Where(IsMappedInstanceProperty)
            .Select(ParseProperty);

        var properties = positionalProperties.Concat(bodyProperties).ToList();

        return new EntityModel(typeDeclaration.Identifier.Text, properties);
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
}
