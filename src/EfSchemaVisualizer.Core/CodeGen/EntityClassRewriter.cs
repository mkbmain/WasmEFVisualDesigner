using System;
using System.Linq;
using EfSchemaVisualizer.Core.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EfSchemaVisualizer.Core.CodeGen;

public sealed class EntityClassRewriter
{
    public string AddProperty(string sourceCode, string className, PropertyModel property)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var targetType = FindTopLevelType(root, className);

        var newProperty = BuildPropertyDeclaration(property);
        var newType = targetType.AddMembers(newProperty);

        var newRoot = root.ReplaceNode(targetType, newType);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    public string AddClass(string sourceCode, string className)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var newClass = SyntaxFactory.ClassDeclaration(className)
            .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword));

        var newRoot = root.AddMembers(newClass);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    public string RemoveClass(string sourceCode, string className)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var targetType = FindTopLevelType(root, className);

        var newRoot = root.RemoveNode(targetType, SyntaxRemoveOptions.KeepNoTrivia)!;
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    public string RemoveProperty(string sourceCode, string className, string propertyName)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var targetType = FindTopLevelType(root, className);

        var targetProperty = targetType.Members
            .OfType<PropertyDeclarationSyntax>()
            .FirstOrDefault(p => p.Identifier.Text == propertyName)
            ?? throw new InvalidOperationException($"No property named '{propertyName}' found on type '{className}'.");

        var newType = targetType.RemoveNode(targetProperty, SyntaxRemoveOptions.KeepNoTrivia)!;

        var newRoot = root.ReplaceNode(targetType, newType);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    public string RenameClass(string sourceCode, string oldClassName, string newClassName)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var targetType = FindTopLevelType(root, oldClassName);

        var newType = targetType.WithIdentifier(SyntaxFactory.Identifier(newClassName));

        newType = newType.ReplaceNodes(
            newType.Members.OfType<ConstructorDeclarationSyntax>().Where(c => c.Identifier.Text == oldClassName),
            (ctor, _) => ctor.WithIdentifier(SyntaxFactory.Identifier(newClassName)));

        var newRoot = root.ReplaceNode(targetType, newType);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    public string RenamePropertyTypeReferences(string sourceCode, string oldTypeName, string newTypeName)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var targets = root.DescendantNodes()
            .OfType<PropertyDeclarationSyntax>()
            .SelectMany(p => p.Type.DescendantNodesAndSelf())
            .OfType<IdentifierNameSyntax>()
            .Where(id => id.Identifier.Text == oldTypeName)
            .ToList();

        if (targets.Count == 0)
        {
            return sourceCode;
        }

        var newRoot = root.ReplaceNodes(
            targets,
            (original, _) => original.WithIdentifier(SyntaxFactory.Identifier(newTypeName)));

        return newRoot.NormalizeWhitespace().ToFullString();
    }

    public string RenameProperty(string sourceCode, string className, string oldPropertyName, string newPropertyName)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var targetType = FindTopLevelType(root, className);

        var targetProperty = targetType.Members
            .OfType<PropertyDeclarationSyntax>()
            .FirstOrDefault(p => p.Identifier.Text == oldPropertyName)
            ?? throw new InvalidOperationException($"No property named '{oldPropertyName}' found on type '{className}'.");

        var newProperty = targetProperty.WithIdentifier(SyntaxFactory.Identifier(newPropertyName));

        var newRoot = root.ReplaceNode(targetProperty, newProperty);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    public string ChangePropertyType(string sourceCode, string className, string propertyName, string newClrType, bool newIsNullable)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var targetType = FindTopLevelType(root, className);

        var targetProperty = targetType.Members
            .OfType<PropertyDeclarationSyntax>()
            .FirstOrDefault(p => p.Identifier.Text == propertyName)
            ?? throw new InvalidOperationException($"No property named '{propertyName}' found on type '{className}'.");

        TypeSyntax newTypeSyntax = SyntaxFactory.ParseTypeName(newClrType);
        if (newIsNullable)
        {
            newTypeSyntax = SyntaxFactory.NullableType(newTypeSyntax);
        }

        var newProperty = targetProperty.WithType(newTypeSyntax);

        var newRoot = root.ReplaceNode(targetProperty, newProperty);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    private static TypeDeclarationSyntax FindTopLevelType(CompilationUnitSyntax root, string className)
    {
        return root.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .Where(t => !t.Ancestors().OfType<TypeDeclarationSyntax>().Any())
            .FirstOrDefault(t => t.Identifier.Text == className)
            ?? throw new InvalidOperationException($"No top-level class, record, or struct named '{className}' found in source.");
    }

    private static PropertyDeclarationSyntax BuildPropertyDeclaration(PropertyModel property)
    {
        TypeSyntax typeSyntax = SyntaxFactory.ParseTypeName(property.ClrType);

        if (property.IsNullable)
        {
            typeSyntax = SyntaxFactory.NullableType(typeSyntax);
        }

        return SyntaxFactory.PropertyDeclaration(typeSyntax, property.Name)
            .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword))
            .AddAccessorListAccessors(
                SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                    .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)),
                SyntaxFactory.AccessorDeclaration(SyntaxKind.SetAccessorDeclaration)
                    .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)));
    }
}
