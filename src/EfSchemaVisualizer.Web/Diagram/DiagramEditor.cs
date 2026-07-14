using EfSchemaVisualizer.Core.CodeGen;
using EfSchemaVisualizer.Core.Model;
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
    private readonly Dictionary<string, Guid> _entityIds = new();

    public DiagramEditor(string classSource, string configSource)
    {
        ClassSource = classSource;
        ConfigSource = configSource;
        Current = DiagramModelBuilder.Build(classSource, configSource);

        foreach (var entity in Current.Entities)
        {
            _entityIds[entity.Name] = Guid.NewGuid();
        }
    }

    public string ClassSource { get; private set; }
    public string ConfigSource { get; private set; }
    public DiagramModelResult Current { get; private set; }
    public IReadOnlyDictionary<string, Guid> EntityIds => _entityIds;

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
        newClassSource = _classRewriter.RenamePropertyTypeReferences(newClassSource, oldName, newName);
        var newConfigSource = _configRewriter.RenameEntityReferences(ConfigSource, oldName, newName);

        // Re-key before Apply so its entity-id reconciliation (which only fills in
        // missing names and drops stale ones) sees the rename as already accounted
        // for, instead of dropping oldName and minting a fresh Guid for newName.
        if (_entityIds.Remove(oldName, out var entityId))
        {
            _entityIds[newName] = entityId;
        }
        else
        {
            _entityIds[newName] = Guid.NewGuid();
        }

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

    public DiagramEditResult AddEntity()
    {
        var name = GenerateUniqueEntityName();
        var dbSetPropertyName = name + "s";

        var newClassSource = _classRewriter.AddClass(ClassSource, name);
        var newConfigSource = _configRewriter.AddEntity(ConfigSource, name, dbSetPropertyName);
        Apply(newClassSource, newConfigSource);
        _entityIds[name] = Guid.NewGuid();
        return DiagramEditResult.Ok();
    }

    public DiagramEditResult RemoveEntity(string entityName)
    {
        if (!Current.Entities.Any(e => e.Name == entityName))
        {
            return DiagramEditResult.Fail($"Entity '{entityName}' not found.");
        }

        var blockingRelationship = Current.Relationships.FirstOrDefault(r =>
            r.PrincipalEntity == entityName || r.DependentEntity == entityName);
        if (blockingRelationship is not null)
        {
            var otherEntity = blockingRelationship.PrincipalEntity == entityName
                ? blockingRelationship.DependentEntity
                : blockingRelationship.PrincipalEntity;
            return DiagramEditResult.Fail(
                $"Cannot remove '{entityName}': it has a relationship with '{otherEntity}'. Remove the relationship first.");
        }

        var newClassSource = _classRewriter.RemoveClass(ClassSource, entityName);
        var newConfigSource = _configRewriter.RemoveEntity(ConfigSource, entityName);
        Apply(newClassSource, newConfigSource);
        _entityIds.Remove(entityName);
        return DiagramEditResult.Ok();
    }

    public DiagramEditResult AddProperty(string entityName)
    {
        var entity = Current.Entities.FirstOrDefault(e => e.Name == entityName);
        if (entity is null)
        {
            return DiagramEditResult.Fail($"Entity '{entityName}' not found.");
        }

        var propertyName = GenerateUniquePropertyName(entity);
        var newClassSource = _classRewriter.AddProperty(
            ClassSource, entityName, new PropertyModel(propertyName, "string", IsNullable: false, MaxLength: null));
        Apply(newClassSource, ConfigSource);
        return DiagramEditResult.Ok();
    }

    public DiagramEditResult RemoveProperty(string entityName, string propertyName)
    {
        var entity = Current.Entities.FirstOrDefault(e => e.Name == entityName);
        if (entity is null)
        {
            return DiagramEditResult.Fail($"Entity '{entityName}' not found.");
        }

        if (!entity.Properties.Any(p => p.Name == propertyName))
        {
            return DiagramEditResult.Fail($"Property '{propertyName}' not found on '{entityName}'.");
        }

        if (entity.KeyPropertyNames.Contains(propertyName))
        {
            return DiagramEditResult.Fail($"Cannot remove '{propertyName}': it is part of '{entityName}''s key.");
        }

        if (entity.Indexes.Any(i => i.PropertyNames.Contains(propertyName)))
        {
            return DiagramEditResult.Fail($"Cannot remove '{propertyName}': it is used in an index.");
        }

        var blockingRelationship = Current.Relationships.FirstOrDefault(r =>
            (r.DependentEntity == entityName && (r.ForeignKeyProperties.Contains(propertyName) || r.DependentNavigation == propertyName)) ||
            (r.PrincipalEntity == entityName && r.PrincipalNavigation == propertyName));
        if (blockingRelationship is not null)
        {
            return DiagramEditResult.Fail($"Cannot remove '{propertyName}': it is used by a relationship.");
        }

        var newClassSource = _classRewriter.RemoveProperty(ClassSource, entityName, propertyName);
        Apply(newClassSource, ConfigSource);
        return DiagramEditResult.Ok();
    }

    private static string GenerateUniquePropertyName(EntityModel entity)
    {
        if (!entity.Properties.Any(p => p.Name == "NewProperty"))
        {
            return "NewProperty";
        }

        var suffix = 2;
        while (entity.Properties.Any(p => p.Name == $"NewProperty{suffix}"))
        {
            suffix++;
        }

        return $"NewProperty{suffix}";
    }

    private string GenerateUniqueEntityName()
    {
        if (!Current.Entities.Any(e => e.Name == "NewEntity"))
        {
            return "NewEntity";
        }

        var suffix = 2;
        while (Current.Entities.Any(e => e.Name == $"NewEntity{suffix}"))
        {
            suffix++;
        }

        return $"NewEntity{suffix}";
    }

    public void SyncSource(string classSource, string configSource)
    {
        ClassSource = classSource;
        ConfigSource = configSource;
    }

    private void Apply(string newClassSource, string newConfigSource)
    {
        ClassSource = newClassSource;
        ConfigSource = newConfigSource;
        Current = DiagramModelBuilder.Build(ClassSource, ConfigSource);

        // Hand-edited source (via SyncSource) can introduce or delete classes without
        // going through AddEntity/RemoveEntity, so _entityIds would otherwise drift out
        // of sync with Current.Entities. Reconcile here, the one place every mutation
        // (rename, add, remove, type change, hand-edit reparse) funnels through.
        var currentNames = Current.Entities.Select(e => e.Name).ToHashSet();
        foreach (var name in currentNames)
        {
            if (!_entityIds.ContainsKey(name))
            {
                _entityIds[name] = Guid.NewGuid();
            }
        }

        foreach (var staleName in _entityIds.Keys.Where(name => !currentNames.Contains(name)).ToList())
        {
            _entityIds.Remove(staleName);
        }
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
