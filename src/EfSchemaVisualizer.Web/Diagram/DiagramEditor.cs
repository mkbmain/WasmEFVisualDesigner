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
    private readonly Stack<Snapshot> _undoStack = new();
    private readonly Stack<Snapshot> _redoStack = new();

    private sealed record Snapshot(string ClassSource, string ConfigSource, Dictionary<string, Guid> EntityIds);

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

    public DiagramEditResult ToggleKey(string entityName, string propertyName, bool isKey)
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

        var alreadyKey = entity.KeyPropertyNames.Contains(propertyName);
        if (isKey == alreadyKey)
        {
            return DiagramEditResult.Ok();
        }

        var newKeyPropertyNames = isKey
            ? entity.KeyPropertyNames.Append(propertyName).ToList()
            : entity.KeyPropertyNames.Where(name => name != propertyName).ToList();

        if (newKeyPropertyNames.Count == 0)
        {
            return DiagramEditResult.Fail($"'{entityName}' must have at least one key property.");
        }

        var newConfigSource = _configRewriter.SetKey(ConfigSource, entityName, newKeyPropertyNames);
        Apply(ClassSource, newConfigSource);
        return DiagramEditResult.Ok();
    }

    public DiagramEditResult AddIndex(string entityName, string propertyName)
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

        if (entity.Indexes.Any(i => i.PropertyNames.SequenceEqual(new[] { propertyName })))
        {
            return DiagramEditResult.Fail($"'{entityName}' already has an index on '{propertyName}'.");
        }

        var newConfigSource = _configRewriter.SetIndex(ConfigSource, entityName, new List<string> { propertyName }, isUnique: false);
        Apply(ClassSource, newConfigSource);
        return DiagramEditResult.Ok();
    }

    public DiagramEditResult ToggleIndexMembership(string entityName, IReadOnlyList<string> indexPropertyNames, string propertyName, bool include)
    {
        var entity = Current.Entities.FirstOrDefault(e => e.Name == entityName);
        if (entity is null)
        {
            return DiagramEditResult.Fail($"Entity '{entityName}' not found.");
        }

        var index = entity.Indexes.FirstOrDefault(i => i.PropertyNames.SequenceEqual(indexPropertyNames));
        if (index is null)
        {
            return DiagramEditResult.Fail($"Index not found on '{entityName}'.");
        }

        var alreadyIncluded = index.PropertyNames.Contains(propertyName);
        if (include == alreadyIncluded)
        {
            return DiagramEditResult.Ok();
        }

        var newPropertyNames = include
            ? index.PropertyNames.Append(propertyName).ToList()
            : index.PropertyNames.Where(name => name != propertyName).ToList();

        if (newPropertyNames.Count == 0)
        {
            var configAfterRemove = _configRewriter.RemoveIndex(ConfigSource, entityName, index.PropertyNames);
            Apply(ClassSource, configAfterRemove);
            return DiagramEditResult.Ok();
        }

        if (entity.Indexes.Any(i => !ReferenceEquals(i, index) && i.PropertyNames.SequenceEqual(newPropertyNames)))
        {
            return DiagramEditResult.Fail($"'{entityName}' already has an index on [{string.Join(", ", newPropertyNames)}].");
        }

        var withoutOldIndex = _configRewriter.RemoveIndex(ConfigSource, entityName, index.PropertyNames);
        var withNewIndex = _configRewriter.SetIndex(withoutOldIndex, entityName, newPropertyNames, index.IsUnique, index.Name);
        Apply(ClassSource, withNewIndex);
        return DiagramEditResult.Ok();
    }

    public DiagramEditResult SetIndexUnique(string entityName, IReadOnlyList<string> propertyNames, bool isUnique)
    {
        var entity = Current.Entities.FirstOrDefault(e => e.Name == entityName);
        if (entity is null)
        {
            return DiagramEditResult.Fail($"Entity '{entityName}' not found.");
        }

        var index = entity.Indexes.FirstOrDefault(i => i.PropertyNames.SequenceEqual(propertyNames));
        if (index is null)
        {
            return DiagramEditResult.Fail($"Index not found on '{entityName}'.");
        }

        if (index.IsUnique == isUnique)
        {
            return DiagramEditResult.Ok();
        }

        var newConfigSource = _configRewriter.SetIndex(ConfigSource, entityName, propertyNames, isUnique, index.Name);
        Apply(ClassSource, newConfigSource);
        return DiagramEditResult.Ok();
    }

    public DiagramEditResult RenameIndex(string entityName, IReadOnlyList<string> propertyNames, string? newName)
    {
        var entity = Current.Entities.FirstOrDefault(e => e.Name == entityName);
        if (entity is null)
        {
            return DiagramEditResult.Fail($"Entity '{entityName}' not found.");
        }

        var index = entity.Indexes.FirstOrDefault(i => i.PropertyNames.SequenceEqual(propertyNames));
        if (index is null)
        {
            return DiagramEditResult.Fail($"Index not found on '{entityName}'.");
        }

        var normalizedName = string.IsNullOrWhiteSpace(newName) ? null : newName.Trim();
        if (normalizedName == index.Name)
        {
            return DiagramEditResult.Ok();
        }

        var newConfigSource = _configRewriter.SetIndex(ConfigSource, entityName, propertyNames, index.IsUnique, normalizedName);
        Apply(ClassSource, newConfigSource);
        return DiagramEditResult.Ok();
    }

    public DiagramEditResult RemoveIndex(string entityName, IReadOnlyList<string> propertyNames)
    {
        var entity = Current.Entities.FirstOrDefault(e => e.Name == entityName);
        if (entity is null)
        {
            return DiagramEditResult.Fail($"Entity '{entityName}' not found.");
        }

        var index = entity.Indexes.FirstOrDefault(i => i.PropertyNames.SequenceEqual(propertyNames));
        if (index is null)
        {
            return DiagramEditResult.Fail($"Index not found on '{entityName}'.");
        }

        var newConfigSource = _configRewriter.RemoveIndex(ConfigSource, entityName, propertyNames);
        Apply(ClassSource, newConfigSource);
        return DiagramEditResult.Ok();
    }

    public DiagramEditResult SetTableMapping(string entityName, string? tableName, string? schema)
    {
        var entity = Current.Entities.FirstOrDefault(e => e.Name == entityName);
        if (entity is null)
        {
            return DiagramEditResult.Fail($"Entity '{entityName}' not found.");
        }

        var normalizedTableName = string.IsNullOrWhiteSpace(tableName) ? null : tableName.Trim();
        var normalizedSchema = string.IsNullOrWhiteSpace(schema) ? null : schema.Trim();

        if (normalizedTableName == entity.TableName && normalizedSchema == entity.Schema)
        {
            return DiagramEditResult.Ok();
        }

        if (normalizedTableName is null)
        {
            var clearedConfigSource = _configRewriter.RemoveTable(ConfigSource, entityName);
            Apply(ClassSource, clearedConfigSource);
            return DiagramEditResult.Ok();
        }

        var newConfigSource = _configRewriter.SetTable(ConfigSource, entityName, normalizedTableName, normalizedSchema);
        Apply(ClassSource, newConfigSource);
        return DiagramEditResult.Ok();
    }

    public DiagramEditResult SetColumnName(string entityName, string propertyName, string? columnName)
    {
        var entity = Current.Entities.FirstOrDefault(e => e.Name == entityName);
        if (entity is null)
        {
            return DiagramEditResult.Fail($"Entity '{entityName}' not found.");
        }

        var property = entity.Properties.FirstOrDefault(p => p.Name == propertyName);
        if (property is null)
        {
            return DiagramEditResult.Fail($"Property '{propertyName}' not found on '{entityName}'.");
        }

        var normalizedColumnName = string.IsNullOrWhiteSpace(columnName) ? null : columnName.Trim();
        if (normalizedColumnName == property.ColumnName)
        {
            return DiagramEditResult.Ok();
        }

        var newConfigSource = normalizedColumnName is null
            ? _configRewriter.RemoveColumnName(ConfigSource, entityName, propertyName)
            : _configRewriter.SetColumnName(ConfigSource, entityName, propertyName, normalizedColumnName);
        Apply(ClassSource, newConfigSource);
        return DiagramEditResult.Ok();
    }

    public DiagramEditResult SetColumnType(string entityName, string propertyName, string? columnType)
    {
        var entity = Current.Entities.FirstOrDefault(e => e.Name == entityName);
        if (entity is null)
        {
            return DiagramEditResult.Fail($"Entity '{entityName}' not found.");
        }

        var property = entity.Properties.FirstOrDefault(p => p.Name == propertyName);
        if (property is null)
        {
            return DiagramEditResult.Fail($"Property '{propertyName}' not found on '{entityName}'.");
        }

        var normalizedColumnType = string.IsNullOrWhiteSpace(columnType) ? null : columnType.Trim();
        if (normalizedColumnType == property.ColumnType)
        {
            return DiagramEditResult.Ok();
        }

        var newConfigSource = normalizedColumnType is null
            ? _configRewriter.RemoveColumnType(ConfigSource, entityName, propertyName)
            : _configRewriter.SetColumnType(ConfigSource, entityName, propertyName, normalizedColumnType);
        Apply(ClassSource, newConfigSource);
        return DiagramEditResult.Ok();
    }

    public DiagramEditResult SetPrecision(string entityName, string propertyName, int? precision, int? scale)
    {
        var entity = Current.Entities.FirstOrDefault(e => e.Name == entityName);
        if (entity is null)
        {
            return DiagramEditResult.Fail($"Entity '{entityName}' not found.");
        }

        var property = entity.Properties.FirstOrDefault(p => p.Name == propertyName);
        if (property is null)
        {
            return DiagramEditResult.Fail($"Property '{propertyName}' not found on '{entityName}'.");
        }

        if (precision is null)
        {
            if (property.Precision is null && property.Scale is null)
            {
                return DiagramEditResult.Ok();
            }

            var clearedConfigSource = _configRewriter.RemovePrecision(ConfigSource, entityName, propertyName);
            Apply(ClassSource, clearedConfigSource);
            return DiagramEditResult.Ok();
        }

        if (precision <= 0)
        {
            return DiagramEditResult.Fail("Precision must be a positive number.");
        }

        if (scale is not null && scale < 0)
        {
            return DiagramEditResult.Fail("Scale cannot be negative.");
        }

        if (precision == property.Precision && scale == property.Scale)
        {
            return DiagramEditResult.Ok();
        }

        var newConfigSource = _configRewriter.RewritePrecision(ConfigSource, entityName, propertyName, precision.Value, scale);
        Apply(ClassSource, newConfigSource);
        return DiagramEditResult.Ok();
    }

    public DiagramEditResult SetDefaultValue(string entityName, string propertyName, string? literalText)
    {
        var entity = Current.Entities.FirstOrDefault(e => e.Name == entityName);
        if (entity is null)
        {
            return DiagramEditResult.Fail($"Entity '{entityName}' not found.");
        }

        var property = entity.Properties.FirstOrDefault(p => p.Name == propertyName);
        if (property is null)
        {
            return DiagramEditResult.Fail($"Property '{propertyName}' not found on '{entityName}'.");
        }

        var normalizedLiteral = string.IsNullOrWhiteSpace(literalText) ? null : literalText.Trim();
        if (normalizedLiteral == property.DefaultValueLiteral)
        {
            return DiagramEditResult.Ok();
        }

        if (normalizedLiteral is not null && !IsValidExpressionText(normalizedLiteral))
        {
            return DiagramEditResult.Fail($"'{normalizedLiteral}' is not a valid default value expression.");
        }

        var newConfigSource = normalizedLiteral is null
            ? _configRewriter.RemoveDefaultValue(ConfigSource, entityName, propertyName)
            : _configRewriter.SetDefaultValue(ConfigSource, entityName, propertyName, normalizedLiteral);
        Apply(ClassSource, newConfigSource);
        return DiagramEditResult.Ok();
    }

    public DiagramEditResult AddRelationship(string dependentEntityName, string principalEntityName)
    {
        var dependent = Current.Entities.FirstOrDefault(e => e.Name == dependentEntityName);
        if (dependent is null)
        {
            return DiagramEditResult.Fail($"Entity '{dependentEntityName}' not found.");
        }

        var principal = Current.Entities.FirstOrDefault(e => e.Name == principalEntityName);
        if (principal is null)
        {
            return DiagramEditResult.Fail($"Entity '{principalEntityName}' not found.");
        }

        var relationship = new RelationshipModel(principalEntityName, dependentEntityName, RelationshipKind.OneToMany, null, null);

        var newConfigSource = _configRewriter.SetRelationship(ConfigSource, relationship);
        Apply(ClassSource, newConfigSource);
        return DiagramEditResult.Ok();
    }

    public DiagramEditResult SetRelationshipShape(RelationshipModel relationship, RelationshipKind newKind, IReadOnlyList<string> newForeignKeyProperties)
    {
        if (!Current.Relationships.Contains(relationship))
        {
            return DiagramEditResult.Fail("Relationship no longer exists.");
        }

        if (newKind == relationship.Kind && newForeignKeyProperties.SequenceEqual(relationship.ForeignKeyProperties))
        {
            return DiagramEditResult.Ok();
        }

        if (newKind == RelationshipKind.ManyToMany && newForeignKeyProperties.Count > 0)
        {
            return DiagramEditResult.Fail("Many-to-many relationships cannot have a foreign key.");
        }

        var dependent = Current.Entities.First(e => e.Name == relationship.DependentEntity);
        var missingProperty = newForeignKeyProperties.FirstOrDefault(name => !dependent.Properties.Any(p => p.Name == name));
        if (missingProperty is not null)
        {
            return DiagramEditResult.Fail($"'{missingProperty}' is not a property of '{relationship.DependentEntity}'.");
        }

        var updated = relationship with { Kind = newKind, ForeignKeyProperties = newForeignKeyProperties };

        var withoutOld = _configRewriter.RemoveRelationship(ConfigSource, relationship);
        if (withoutOld == ConfigSource)
        {
            return DiagramEditResult.Fail("Could not locate this relationship's existing configuration to update.");
        }

        var withNew = _configRewriter.SetRelationship(withoutOld, updated);
        Apply(ClassSource, withNew);
        return DiagramEditResult.Ok();
    }

    public DiagramEditResult RemoveRelationship(RelationshipModel relationship)
    {
        if (!Current.Relationships.Contains(relationship))
        {
            return DiagramEditResult.Fail("Relationship no longer exists.");
        }

        var newConfigSource = _configRewriter.RemoveRelationship(ConfigSource, relationship);
        if (newConfigSource == ConfigSource)
        {
            return DiagramEditResult.Fail("Could not locate this relationship in the source to remove.");
        }

        Apply(ClassSource, newConfigSource);
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

    public bool CanUndo => _undoStack.Count > 0;
    public bool CanRedo => _redoStack.Count > 0;

    public DiagramEditResult Undo()
    {
        if (_undoStack.Count == 0)
        {
            return DiagramEditResult.Fail("Nothing to undo.");
        }

        _redoStack.Push(CurrentSnapshot());
        Restore(_undoStack.Pop());
        return DiagramEditResult.Ok();
    }

    public DiagramEditResult Redo()
    {
        if (_redoStack.Count == 0)
        {
            return DiagramEditResult.Fail("Nothing to redo.");
        }

        _undoStack.Push(CurrentSnapshot());
        Restore(_redoStack.Pop());
        return DiagramEditResult.Ok();
    }

    private Snapshot CurrentSnapshot() => new(ClassSource, ConfigSource, new Dictionary<string, Guid>(_entityIds));

    private void Restore(Snapshot snapshot)
    {
        ClassSource = snapshot.ClassSource;
        ConfigSource = snapshot.ConfigSource;
        _entityIds.Clear();
        foreach (var (name, id) in snapshot.EntityIds)
        {
            _entityIds[name] = id;
        }

        Current = DiagramModelBuilder.Build(ClassSource, ConfigSource);
    }

    private void Apply(string newClassSource, string newConfigSource)
    {
        _undoStack.Push(CurrentSnapshot());
        _redoStack.Clear();

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

    private static bool IsValidExpressionText(string text)
    {
        var expression = SyntaxFactory.ParseExpression(text);
        return expression.ToFullString() == text && !expression.ContainsDiagnostics;
    }
}
