using EfSchemaVisualizer.Core.CodeGen;
using Microsoft.CodeAnalysis.CSharp;

namespace EfSchemaVisualizer.Web.Diagram;

public sealed class DiagramEditResult
{
    private DiagramEditResult(bool success, string? error)
    {
        Success = success;
        Error = error;
    }

    public bool Success { get; }
    public string? Error { get; }

    public static DiagramEditResult Ok() => new(true, null);
    public static DiagramEditResult Fail(string error) => new(false, error);
}

public sealed class DiagramEditor
{
    private readonly EntityClassRewriter _classRewriter = new();
    private readonly OnModelCreatingRewriter _configRewriter = new();

    public DiagramEditor(string classSource, string configSource)
    {
        ClassSource = classSource;
        ConfigSource = configSource;
        Current = DiagramModelBuilder.Build(classSource, configSource);
    }

    public string ClassSource { get; private set; }
    public string ConfigSource { get; private set; }
    public DiagramModelResult Current { get; private set; }

    public DiagramEditResult RenameEntity(string oldName, string newName)
    {
        if (!SyntaxFacts.IsValidIdentifier(newName))
        {
            return DiagramEditResult.Fail($"'{newName}' is not a valid entity name.");
        }

        if (newName == oldName)
        {
            return DiagramEditResult.Ok();
        }

        if (!Current.Entities.Any(e => e.Name == oldName))
        {
            return DiagramEditResult.Fail($"Entity '{oldName}' not found.");
        }

        if (Current.Entities.Any(e => e.Name == newName))
        {
            return DiagramEditResult.Fail($"An entity named '{newName}' already exists.");
        }

        var newClassSource = _classRewriter.RenameClass(ClassSource, oldName, newName);
        var newConfigSource = _configRewriter.RenameEntityReferences(ConfigSource, oldName, newName);
        Apply(newClassSource, newConfigSource);
        return DiagramEditResult.Ok();
    }

    public DiagramEditResult RenameProperty(string entityName, string oldPropertyName, string newPropertyName)
    {
        if (!SyntaxFacts.IsValidIdentifier(newPropertyName))
        {
            return DiagramEditResult.Fail($"'{newPropertyName}' is not a valid property name.");
        }

        var entity = Current.Entities.FirstOrDefault(e => e.Name == entityName);
        if (entity is null)
        {
            return DiagramEditResult.Fail($"Entity '{entityName}' not found.");
        }

        if (newPropertyName == oldPropertyName)
        {
            return DiagramEditResult.Ok();
        }

        if (!entity.Properties.Any(p => p.Name == oldPropertyName))
        {
            return DiagramEditResult.Fail($"Property '{oldPropertyName}' not found on '{entityName}'.");
        }

        if (entity.Properties.Any(p => p.Name == newPropertyName))
        {
            return DiagramEditResult.Fail($"'{entityName}' already has a property named '{newPropertyName}'.");
        }

        var newClassSource = _classRewriter.RenameProperty(ClassSource, entityName, oldPropertyName, newPropertyName);
        var newConfigSource = _configRewriter.RenamePropertyReferences(ConfigSource, entityName, oldPropertyName, newPropertyName);
        Apply(newClassSource, newConfigSource);
        return DiagramEditResult.Ok();
    }

    public DiagramEditResult ChangePropertyType(string entityName, string propertyName, string newClrType, bool newIsNullable)
    {
        if (!IsValidTypeToken(newClrType))
        {
            return DiagramEditResult.Fail($"'{newClrType}' is not a valid type.");
        }

        var entity = Current.Entities.FirstOrDefault(e => e.Name == entityName);
        if (entity is null)
        {
            return DiagramEditResult.Fail($"Entity '{entityName}' not found.");
        }

        if (!entity.Properties.Any(p => p.Name == propertyName))
        {
            return DiagramEditResult.Fail($"Property '{propertyName}' not found on '{entityName}'.");
        }

        var newClassSource = _classRewriter.ChangePropertyType(ClassSource, entityName, propertyName, newClrType, newIsNullable);
        Apply(newClassSource, ConfigSource);
        return DiagramEditResult.Ok();
    }

    private void Apply(string newClassSource, string newConfigSource)
    {
        ClassSource = newClassSource;
        ConfigSource = newConfigSource;
        Current = DiagramModelBuilder.Build(ClassSource, ConfigSource);
    }

    private static bool IsValidTypeToken(string typeText)
    {
        var trimmed = typeText.Trim();
        if (trimmed.Length == 0)
        {
            return false;
        }

        var typeSyntax = SyntaxFactory.ParseTypeName(trimmed);
        return typeSyntax.ToFullString() == trimmed && !typeSyntax.ContainsDiagnostics;
    }
}
