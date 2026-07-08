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

        var classDeclarations = root.DescendantNodes()
            .OfType<ClassDeclarationSyntax>()
            .ToList();

        if (classDeclarations.Count == 0)
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

        var entities = classDeclarations.Select(ParseEntity).ToList();

        return new ParseResult<IReadOnlyList<EntityModel>>(entities, new List<Diagnostic>());
    }

    private static EntityModel ParseEntity(ClassDeclarationSyntax classDeclaration)
    {
        var properties = classDeclaration.Members
            .OfType<PropertyDeclarationSyntax>()
            .Select(ParseProperty)
            .ToList();

        return new EntityModel(classDeclaration.Identifier.Text, properties);
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
