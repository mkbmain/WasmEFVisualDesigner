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
            .FirstOrDefault(p => p.Identifier.Text == propertyName);

        if (targetProperty is not null)
        {
            var newType = targetType.RemoveNode(targetProperty, SyntaxRemoveOptions.KeepNoTrivia)!;

            var newRoot = root.ReplaceNode(targetType, newType);
            return newRoot.NormalizeWhitespace().ToFullString();
        }

        var targetParameter = FindPositionalParameter(targetType, propertyName, className);

        var newParameterList = targetType.ParameterList!.WithParameters(
            targetType.ParameterList.Parameters.Remove(targetParameter));

        var newTypeWithParameterRemoved = targetType.WithParameterList(newParameterList);

        var newRootWithParameterRemoved = root.ReplaceNode(targetType, newTypeWithParameterRemoved);
        return newRootWithParameterRemoved.NormalizeWhitespace().ToFullString();
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

        var propertyTargets = root.DescendantNodes()
            .OfType<PropertyDeclarationSyntax>()
            .SelectMany(p => p.Type.DescendantNodesAndSelf())
            .OfType<IdentifierNameSyntax>();

        var positionalParameterTargets = root.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .Select(t => t.ParameterList)
            .Where(parameterList => parameterList is not null)
            .SelectMany(parameterList => parameterList!.Parameters)
            .SelectMany(p => p.Type!.DescendantNodesAndSelf())
            .OfType<IdentifierNameSyntax>();

        var targets = propertyTargets.Concat(positionalParameterTargets)
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
            .FirstOrDefault(p => p.Identifier.Text == oldPropertyName);

        if (targetProperty is not null)
        {
            var newProperty = targetProperty.WithIdentifier(SyntaxFactory.Identifier(newPropertyName));

            var newRoot = root.ReplaceNode(targetProperty, newProperty);
            return newRoot.NormalizeWhitespace().ToFullString();
        }

        var targetParameter = FindPositionalParameter(targetType, oldPropertyName, className);
        var newParameter = targetParameter.WithIdentifier(SyntaxFactory.Identifier(newPropertyName));

        var newRootWithParameterRenamed = root.ReplaceNode(targetParameter, newParameter);
        return newRootWithParameterRenamed.NormalizeWhitespace().ToFullString();
    }

    public string ChangePropertyType(string sourceCode, string className, string propertyName, string newClrType, bool newIsNullable)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var targetType = FindTopLevelType(root, className);

        var targetProperty = targetType.Members
            .OfType<PropertyDeclarationSyntax>()
            .FirstOrDefault(p => p.Identifier.Text == propertyName);

        TypeSyntax newTypeSyntax = SyntaxFactory.ParseTypeName(newClrType);
        if (newIsNullable)
        {
            newTypeSyntax = SyntaxFactory.NullableType(newTypeSyntax);
        }

        if (targetProperty is not null)
        {
            var newProperty = targetProperty.WithType(newTypeSyntax);

            var newRoot = root.ReplaceNode(targetProperty, newProperty);
            return newRoot.NormalizeWhitespace().ToFullString();
        }

        var targetParameter = FindPositionalParameter(targetType, propertyName, className);
        var newParameter = targetParameter.WithType(newTypeSyntax);

        var newRootWithParameterTypeChanged = root.ReplaceNode(targetParameter, newParameter);
        return newRootWithParameterTypeChanged.NormalizeWhitespace().ToFullString();
    }

    private static TypeDeclarationSyntax FindTopLevelType(CompilationUnitSyntax root, string className)
    {
        return root.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .Where(t => !t.Ancestors().OfType<TypeDeclarationSyntax>().Any())
            .FirstOrDefault(t => t.Identifier.Text == className)
            ?? throw new InvalidOperationException($"No top-level class, record, or struct named '{className}' found in source.");
    }

    private static ParameterSyntax FindPositionalParameter(TypeDeclarationSyntax targetType, string propertyName, string className)
    {
        return targetType.ParameterList?.Parameters
            .FirstOrDefault(p => p.Identifier.Text == propertyName)
            ?? throw new InvalidOperationException($"No property named '{propertyName}' found on type '{className}'.");
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
