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

        var classDeclaration = root.DescendantNodes()
            .OfType<ClassDeclarationSyntax>()
            .First();

        var properties = classDeclaration.Members
            .OfType<PropertyDeclarationSyntax>()
            .Select(ParseProperty)
            .ToList();

        var entity = new EntityModel(classDeclaration.Identifier.Text, properties);

        return new ParseResult<IReadOnlyList<EntityModel>>(
            new List<EntityModel> { entity },
            new List<Diagnostic>());
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
