using System.Collections.Generic;
using System.Linq;
using EfSchemaVisualizer.Core.Model;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EfSchemaVisualizer.Core.Parsing;

public sealed class EntityClassParser
{
    public ParseResult<IReadOnlyList<EntityModel>> Parse(string sourceCode)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var typeDeclarations = root.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .Where(t => t is ClassDeclarationSyntax or StructDeclarationSyntax or RecordDeclarationSyntax)
            .ToList();

        if (typeDeclarations.Count == 0)
        {
            var diagnostic = new Diagnostic(
                "NoEntityDeclarations",
                "No class, record, or struct declarations found in file; nothing to parse.",
                EntityName: null,
                PropertyName: null,
                root.Span);

            return new ParseResult<IReadOnlyList<EntityModel>>(
                new List<EntityModel>(),
                new List<Diagnostic> { diagnostic });
        }

        var entities = typeDeclarations.Select(ParseEntity).ToList();

        return new ParseResult<IReadOnlyList<EntityModel>>(entities, new List<Diagnostic>());
    }

    private static EntityModel ParseEntity(TypeDeclarationSyntax typeDeclaration)
    {
        var positionalProperties = typeDeclaration is RecordDeclarationSyntax
            ? typeDeclaration.ParameterList?.Parameters.Select(ParseParameterProperty) ?? Enumerable.Empty<PropertyModel>()
            : Enumerable.Empty<PropertyModel>();

        var bodyProperties = typeDeclaration.Members
            .OfType<PropertyDeclarationSyntax>()
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

        return new PropertyModel(property.Identifier.Text, clrType, isNullable, MaxLength: null);
    }
}
