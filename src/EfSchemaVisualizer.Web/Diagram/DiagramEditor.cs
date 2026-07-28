using EfSchemaVisualizer.Core.CodeGen;
using EfSchemaVisualizer.Core.Model;
using EfSchemaVisualizer.Core.Parsing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

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
    private readonly Dictionary<string, string> _entityFileOrigins;
    private readonly Dictionary<string, string> _configFileOrigins;
    private readonly Stack<Snapshot> _undoStack = new();
    private readonly Stack<Snapshot> _redoStack = new();

    private sealed record Snapshot(
        string ClassSource,
        string ConfigSource,
        Dictionary<string, Guid> EntityIds,
        Dictionary<string, string> EntityFileOrigins,
        Dictionary<string, string> ConfigFileOrigins);

    public DiagramEditor(
        string classSource,
        string configSource,
        IReadOnlyDictionary<string, string>? entityFileOrigins = null,
        IReadOnlyDictionary<string, string>? configFileOrigins = null)
    {
        ClassSource = classSource;
        ConfigSource = configSource;
        Current = DiagramModelBuilder.Build(classSource, configSource);
        _entityFileOrigins = entityFileOrigins is null ? new() : new(entityFileOrigins);
        _configFileOrigins = configFileOrigins is null ? new() : new(configFileOrigins);

        foreach (var entity in Current.Entities)
        {
            _entityIds[entity.Name] = Guid.NewGuid();
        }
    }

    public string ClassSource { get; private set; }
    public string ConfigSource { get; private set; }
    public DiagramModelResult Current { get; private set; }
    public IReadOnlyDictionary<string, Guid> EntityIds => _entityIds;
    public IReadOnlyDictionary<string, string> EntityFileOrigins => _entityFileOrigins;
    public IReadOnlyDictionary<string, string> ConfigFileOrigins => _configFileOrigins;

    public DiagramEditResult RenameEntity(string oldName, string newName)
    {
        if (!SyntaxFacts.IsValidIdentifier(newName) || IsReservedKeyword(newName))
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

        // Snapshot for undo before re-keying below, so the undo entry captures the
        // pre-rename id/origin maps (oldName still present) rather than the
        // already-renamed ones. Apply() itself can't take this snapshot for us here
        // because by the time it runs, the re-keying below has already mutated these
        // dictionaries in place.
        _undoStack.Push(CurrentSnapshot());
        _redoStack.Clear();

        // Re-key before ApplyCore so its entity-id reconciliation (which only fills in
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

        if (_entityFileOrigins.Remove(oldName, out var entityFileOrigin))
        {
            _entityFileOrigins[newName] = entityFileOrigin;
        }

        if (_configFileOrigins.Remove(oldName, out var configFileOrigin))
        {
            _configFileOrigins[newName] = configFileOrigin;
        }

        ApplyCore(newClassSource, newConfigSource);

        return DiagramEditResult.Ok();
    }

    public DiagramEditResult RenameProperty(string entityName, string oldPropertyName, string newPropertyName)
    {
        if (!SyntaxFacts.IsValidIdentifier(newPropertyName) || IsReservedKeyword(newPropertyName))
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

        if (newPropertyName == entityName)
        {
            return DiagramEditResult.Fail($"A property cannot have the same name as its entity '{entityName}'.");
        }

        // A folded-away owner nav property (e.g. Order.ShippingAddress once OwnedTypeInference.Fold or
        // ComplexTypeInference.Fold splices the target type's properties into Order) never appears in
        // entity.Properties by name — it's replaced there, not merely hidden. But every property folded
        // in that way is stamped with OwnerNavigationProperty = the nav property's own name (see
        // OwnedTypeInference.Fold/ComplexTypeInference.Fold), so a folded-in property whose
        // OwnerNavigationProperty matches oldPropertyName proves oldPropertyName is a real nav property
        // that owns something, without re-parsing the class source. This deliberately does NOT admit an
        // Ignore()d/[NotMapped] property — ModelMerger.ApplyIgnoredProperties also removes those from
        // Properties, but they never stamp any OwnerNavigationProperty, so they still correctly fail
        // here (renaming one would otherwise leave a dangling Ignore(e => e.OldName) reference).
        if (!entity.Properties.Any(p => p.Name == oldPropertyName) && !entity.Properties.Any(p => p.OwnerNavigationProperty == oldPropertyName))
        {
            return DiagramEditResult.Fail($"Property '{oldPropertyName}' not found on '{entityName}'.");
        }

        if (entity.Properties.Any(p => p.Name == newPropertyName))
        {
            return DiagramEditResult.Fail($"'{entityName}' already has a property named '{newPropertyName}'.");
        }

        var owningEntityName = ResolveDeclaringEntity(entityName, oldPropertyName);
        var newClassSource = _classRewriter.RenameProperty(ClassSource, owningEntityName, oldPropertyName, newPropertyName);
        var newConfigSource = _configRewriter.RenamePropertyReferences(ConfigSource, owningEntityName, oldPropertyName, newPropertyName);

        // If oldPropertyName is itself an owner-side nav property with an OwnsOne/OwnsMany/ComplexProperty
        // call (owningEntityName == entityName in that case, since a nav property's own DeclaringEntityName
        // is never stamped by the fold — only the properties folded IN from the target get stamped), also
        // patch the outer call's lambda parameter.
        newConfigSource = _configRewriter.RenameOwnedNavigationReference(newConfigSource, entityName, oldPropertyName, newPropertyName);

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

        var owningEntityName = ResolveDeclaringEntity(entityName, propertyName);
        var newClassSource = _classRewriter.ChangePropertyType(ClassSource, owningEntityName, propertyName, newClrType, newIsNullable);
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
        _entityFileOrigins.Remove(entityName);
        _configFileOrigins.Remove(entityName);
        return DiagramEditResult.Ok();
    }

    public DiagramEditResult AddProperty(string entityName, string clrType = "string")
    {
        var entity = Current.Entities.FirstOrDefault(e => e.Name == entityName);
        if (entity is null)
        {
            return DiagramEditResult.Fail($"Entity '{entityName}' not found.");
        }

        var propertyName = GenerateUniquePropertyName(entity);
        var newClassSource = _classRewriter.AddProperty(
            ClassSource, entityName, new PropertyModel(propertyName, clrType, IsNullable: false, MaxLength: null));
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

        var owningEntityName = ResolveDeclaringEntity(entityName, propertyName);
        var newClassSource = _classRewriter.RemoveProperty(ClassSource, owningEntityName, propertyName);
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

        var owningEntityName = ResolveDeclaringEntity(entityName, propertyName);
        var newConfigSource = _configRewriter.SetKey(ConfigSource, owningEntityName, newKeyPropertyNames);
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
        var withNewIndex = _configRewriter.SetIndex(
            withoutOldIndex, entityName, newPropertyNames, index.IsUnique, index.Name,
            index.Filter, index.IsDescending, index.IncludeProperties);
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

        var newConfigSource = _configRewriter.SetIndex(
            ConfigSource, entityName, propertyNames, isUnique, index.Name,
            index.Filter, index.IsDescending, index.IncludeProperties);
        Apply(ClassSource, newConfigSource);
        return DiagramEditResult.Ok();
    }

    public DiagramEditResult SetKeyName(string entityName, string? newName)
    {
        var entity = Current.Entities.FirstOrDefault(e => e.Name == entityName);
        if (entity is null)
        {
            return DiagramEditResult.Fail($"Entity '{entityName}' not found.");
        }

        var normalizedName = string.IsNullOrWhiteSpace(newName) ? null : newName.Trim();
        if (normalizedName is not null && entity.KeyPropertyNames.Count == 0)
        {
            return DiagramEditResult.Fail($"'{entityName}' has no primary key to name.");
        }

        if (normalizedName == entity.KeyName)
        {
            return DiagramEditResult.Ok();
        }

        var newConfigSource = _configRewriter.SetKey(ConfigSource, entityName, entity.KeyPropertyNames, normalizedName);
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

        var newConfigSource = _configRewriter.SetIndex(
            ConfigSource, entityName, propertyNames, index.IsUnique, normalizedName,
            index.Filter, index.IsDescending, index.IncludeProperties);
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

    public DiagramEditResult AddAlternateKey(string entityName, string propertyName)
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

        if (entity.AlternateKeys.Any(k => k.SequenceEqual(new[] { propertyName })))
        {
            return DiagramEditResult.Fail($"'{entityName}' already has an alternate key on '{propertyName}'.");
        }

        var newConfigSource = _configRewriter.AddAlternateKey(ConfigSource, entityName, new List<string> { propertyName });
        Apply(ClassSource, newConfigSource);
        return DiagramEditResult.Ok();
    }

    public DiagramEditResult ToggleAlternateKeyMembership(string entityName, IReadOnlyList<string> alternateKeyPropertyNames, string propertyName, bool include)
    {
        var entity = Current.Entities.FirstOrDefault(e => e.Name == entityName);
        if (entity is null)
        {
            return DiagramEditResult.Fail($"Entity '{entityName}' not found.");
        }

        var alternateKey = entity.AlternateKeys.FirstOrDefault(k => k.SequenceEqual(alternateKeyPropertyNames));
        if (alternateKey is null)
        {
            return DiagramEditResult.Fail($"Alternate key not found on '{entityName}'.");
        }

        var alreadyIncluded = alternateKey.Contains(propertyName);
        if (include == alreadyIncluded)
        {
            return DiagramEditResult.Ok();
        }

        var newPropertyNames = include
            ? alternateKey.Append(propertyName).ToList()
            : alternateKey.Where(name => name != propertyName).ToList();

        if (newPropertyNames.Count == 0)
        {
            var configAfterRemove = _configRewriter.RemoveAlternateKey(ConfigSource, entityName, alternateKey);
            Apply(ClassSource, configAfterRemove);
            return DiagramEditResult.Ok();
        }

        if (entity.AlternateKeys.Any(k => !ReferenceEquals(k, alternateKey) && k.SequenceEqual(newPropertyNames)))
        {
            return DiagramEditResult.Fail($"'{entityName}' already has an alternate key on [{string.Join(", ", newPropertyNames)}].");
        }

        var withoutOldAlternateKey = _configRewriter.RemoveAlternateKey(ConfigSource, entityName, alternateKey);
        var withNewAlternateKey = _configRewriter.AddAlternateKey(withoutOldAlternateKey, entityName, newPropertyNames);
        Apply(ClassSource, withNewAlternateKey);
        return DiagramEditResult.Ok();
    }

    public DiagramEditResult RemoveAlternateKey(string entityName, IReadOnlyList<string> propertyNames)
    {
        var entity = Current.Entities.FirstOrDefault(e => e.Name == entityName);
        if (entity is null)
        {
            return DiagramEditResult.Fail($"Entity '{entityName}' not found.");
        }

        var alternateKey = entity.AlternateKeys.FirstOrDefault(k => k.SequenceEqual(propertyNames));
        if (alternateKey is null)
        {
            return DiagramEditResult.Fail($"Alternate key not found on '{entityName}'.");
        }

        var newConfigSource = _configRewriter.RemoveAlternateKey(ConfigSource, entityName, propertyNames);
        Apply(ClassSource, newConfigSource);
        return DiagramEditResult.Ok();
    }

    public DiagramEditResult AddCheckConstraint(string entityName, string name, string sql)
    {
        var entity = Current.Entities.FirstOrDefault(e => e.Name == entityName);
        if (entity is null)
        {
            return DiagramEditResult.Fail($"Entity '{entityName}' not found.");
        }

        var normalizedName = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        var normalizedSql = string.IsNullOrWhiteSpace(sql) ? null : sql.Trim();

        if (normalizedName is null)
        {
            return DiagramEditResult.Fail("Check constraint name cannot be empty.");
        }

        if (entity.CheckConstraints.Any(c => c.Name == normalizedName))
        {
            return DiagramEditResult.Fail($"'{entityName}' already has a check constraint named '{normalizedName}'.");
        }

        var newConfigSource = _configRewriter.AddCheckConstraint(ConfigSource, entityName, normalizedName, normalizedSql ?? sql);
        Apply(ClassSource, newConfigSource);
        return DiagramEditResult.Ok();
    }

    public DiagramEditResult SetCheckConstraint(string entityName, string oldName, string newName, string newSql)
    {
        var entity = Current.Entities.FirstOrDefault(e => e.Name == entityName);
        if (entity is null)
        {
            return DiagramEditResult.Fail($"Entity '{entityName}' not found.");
        }

        var existing = entity.CheckConstraints.FirstOrDefault(c => c.Name == oldName);
        if (existing is null)
        {
            return DiagramEditResult.Fail($"'{entityName}' has no check constraint named '{oldName}'.");
        }

        var normalizedNewName = string.IsNullOrWhiteSpace(newName) ? null : newName.Trim();
        var normalizedNewSql = string.IsNullOrWhiteSpace(newSql) ? null : newSql.Trim();

        if (normalizedNewName is null)
        {
            return DiagramEditResult.Fail("Check constraint name cannot be empty.");
        }

        if (normalizedNewName != oldName && entity.CheckConstraints.Any(c => c.Name == normalizedNewName))
        {
            return DiagramEditResult.Fail($"'{entityName}' already has a check constraint named '{normalizedNewName}'.");
        }

        if (normalizedNewName == existing.Name && normalizedNewSql == existing.Sql)
        {
            return DiagramEditResult.Ok();
        }

        var newConfigSource = _configRewriter.SetCheckConstraint(ConfigSource, entityName, oldName, normalizedNewName, normalizedNewSql ?? newSql);
        Apply(ClassSource, newConfigSource);
        return DiagramEditResult.Ok();
    }

    public DiagramEditResult RemoveCheckConstraint(string entityName, string name)
    {
        var entity = Current.Entities.FirstOrDefault(e => e.Name == entityName);
        if (entity is null)
        {
            return DiagramEditResult.Fail($"Entity '{entityName}' not found.");
        }

        if (!entity.CheckConstraints.Any(c => c.Name == name))
        {
            return DiagramEditResult.Fail($"'{entityName}' has no check constraint named '{name}'.");
        }

        var newConfigSource = _configRewriter.RemoveCheckConstraint(ConfigSource, entityName, name);
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

    public DiagramEditResult SetViewMapping(string entityName, string? viewName, string? schema)
    {
        var entity = Current.Entities.FirstOrDefault(e => e.Name == entityName);
        if (entity is null)
        {
            return DiagramEditResult.Fail($"Entity '{entityName}' not found.");
        }

        var normalizedViewName = string.IsNullOrWhiteSpace(viewName) ? null : viewName.Trim();
        var normalizedSchema = string.IsNullOrWhiteSpace(schema) ? null : schema.Trim();

        if (normalizedViewName == entity.ViewName && normalizedSchema == entity.Schema)
        {
            return DiagramEditResult.Ok();
        }

        if (normalizedViewName is null)
        {
            var clearedConfigSource = _configRewriter.RemoveView(ConfigSource, entityName);
            Apply(ClassSource, clearedConfigSource);
            return DiagramEditResult.Ok();
        }

        var newConfigSource = _configRewriter.SetView(ConfigSource, entityName, normalizedViewName, normalizedSchema);
        Apply(ClassSource, newConfigSource);
        return DiagramEditResult.Ok();
    }

    public DiagramEditResult SetSqlQuery(string entityName, string? sql)
    {
        var entity = Current.Entities.FirstOrDefault(e => e.Name == entityName);
        if (entity is null)
        {
            return DiagramEditResult.Fail($"Entity '{entityName}' not found.");
        }

        var normalizedSql = string.IsNullOrWhiteSpace(sql) ? null : sql.Trim();

        if (normalizedSql == entity.SqlQuery)
        {
            return DiagramEditResult.Ok();
        }

        if (normalizedSql is null)
        {
            var clearedConfigSource = _configRewriter.RemoveSqlQuery(ConfigSource, entityName);
            Apply(ClassSource, clearedConfigSource);
            return DiagramEditResult.Ok();
        }

        var newConfigSource = _configRewriter.SetSqlQuery(ConfigSource, entityName, normalizedSql);
        Apply(ClassSource, newConfigSource);
        return DiagramEditResult.Ok();
    }

    public DiagramEditResult SetKeyless(string entityName, bool isKeyless)
    {
        var entity = Current.Entities.FirstOrDefault(e => e.Name == entityName);
        if (entity is null)
        {
            return DiagramEditResult.Fail($"Entity '{entityName}' not found.");
        }

        if (isKeyless == entity.IsKeyless)
        {
            return DiagramEditResult.Ok();
        }

        var newConfigSource = isKeyless
            ? _configRewriter.SetKeyless(ConfigSource, entityName)
            : _configRewriter.RemoveKeyless(ConfigSource, entityName);
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

        string newConfigSource;
        if (property.FoldKind != FoldKind.None && property.OwnerNavigationProperty is { } columnNameNav)
        {
            if (ValidateOwnedEditDepth(entityName, property, columnNameNav) is { } foldFailure)
            {
                return foldFailure;
            }

            newConfigSource = normalizedColumnName is null
                ? _configRewriter.RemoveColumnNameOnOwnedProperty(ConfigSource, entityName, columnNameNav, propertyName)
                : _configRewriter.SetColumnNameOnOwnedProperty(ConfigSource, entityName, columnNameNav, propertyName, normalizedColumnName);
        }
        else
        {
            newConfigSource = normalizedColumnName is null
                ? _configRewriter.RemoveColumnName(ConfigSource, ResolveDeclaringEntity(entityName, propertyName), propertyName)
                : _configRewriter.SetColumnName(ConfigSource, ResolveDeclaringEntity(entityName, propertyName), propertyName, normalizedColumnName);
        }

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

        string newConfigSource;
        if (property.FoldKind != FoldKind.None && property.OwnerNavigationProperty is { } columnTypeNav)
        {
            if (ValidateOwnedEditDepth(entityName, property, columnTypeNav) is { } foldFailure)
            {
                return foldFailure;
            }

            newConfigSource = normalizedColumnType is null
                ? _configRewriter.RemoveColumnTypeOnOwnedProperty(ConfigSource, entityName, columnTypeNav, propertyName)
                : _configRewriter.SetColumnTypeOnOwnedProperty(ConfigSource, entityName, columnTypeNav, propertyName, normalizedColumnType);
        }
        else
        {
            newConfigSource = normalizedColumnType is null
                ? _configRewriter.RemoveColumnType(ConfigSource, ResolveDeclaringEntity(entityName, propertyName), propertyName)
                : _configRewriter.SetColumnType(ConfigSource, ResolveDeclaringEntity(entityName, propertyName), propertyName, normalizedColumnType);
        }

        Apply(ClassSource, newConfigSource);
        return DiagramEditResult.Ok();
    }

    public DiagramEditResult SetMaxLength(string entityName, string propertyName, int? maxLength)
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

        if (maxLength == property.MaxLength)
        {
            return DiagramEditResult.Ok();
        }

        var foldedNav = property.FoldKind != FoldKind.None ? property.OwnerNavigationProperty : null;
        if (foldedNav is not null && ValidateOwnedEditDepth(entityName, property, foldedNav) is { } depthFailure)
        {
            return depthFailure;
        }

        var owningEntityName = ResolveDeclaringEntity(entityName, propertyName);

        if (maxLength is null)
        {
            var clearedConfigSource = foldedNav is { } clearNav
                ? _configRewriter.RemoveMaxLengthOnOwnedProperty(ConfigSource, entityName, clearNav, propertyName)
                : _configRewriter.RemoveMaxLength(ConfigSource, owningEntityName, propertyName);
            Apply(ClassSource, clearedConfigSource);
            return DiagramEditResult.Ok();
        }

        if (maxLength <= 0)
        {
            return DiagramEditResult.Fail("Max length must be a positive number.");
        }

        var newConfigSource = foldedNav is { } setNav
            ? _configRewriter.SetMaxLengthOnOwnedProperty(ConfigSource, entityName, setNav, propertyName, maxLength.Value)
            : _configRewriter.RewriteMaxLength(ConfigSource, owningEntityName, propertyName, maxLength.Value);
        Apply(ClassSource, newConfigSource);
        return DiagramEditResult.Ok();
    }

    public DiagramEditResult SetRequiredOverride(string entityName, string propertyName, bool? isRequired)
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

        if (isRequired == property.IsRequiredOverride)
        {
            return DiagramEditResult.Ok();
        }

        var foldedNav = property.FoldKind != FoldKind.None ? property.OwnerNavigationProperty : null;
        if (foldedNav is not null && ValidateOwnedEditDepth(entityName, property, foldedNav) is { } depthFailure)
        {
            return depthFailure;
        }

        var owningEntityName = ResolveDeclaringEntity(entityName, propertyName);

        if (isRequired is null)
        {
            var clearedConfigSource = foldedNav is { } clearNav
                ? _configRewriter.RemoveIsRequiredOnOwnedProperty(ConfigSource, entityName, clearNav, propertyName)
                : _configRewriter.RemoveIsRequired(ConfigSource, owningEntityName, propertyName);
            Apply(ClassSource, clearedConfigSource);
            return DiagramEditResult.Ok();
        }

        var newConfigSource = foldedNav is { } setNav
            ? _configRewriter.SetIsRequiredOnOwnedProperty(ConfigSource, entityName, setNav, propertyName, isRequired.Value)
            : _configRewriter.RewriteIsRequired(ConfigSource, owningEntityName, propertyName, isRequired.Value);
        Apply(ClassSource, newConfigSource);
        return DiagramEditResult.Ok();
    }

    public DiagramEditResult SetRowVersion(string entityName, string propertyName, bool isRowVersion)
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

        if (isRowVersion == property.IsRowVersion)
        {
            return DiagramEditResult.Ok();
        }

        var foldedNav = property.FoldKind != FoldKind.None ? property.OwnerNavigationProperty : null;
        if (foldedNav is not null && ValidateOwnedEditDepth(entityName, property, foldedNav) is { } depthFailure)
        {
            return depthFailure;
        }

        var owningEntityName = ResolveDeclaringEntity(entityName, propertyName);

        if (!isRowVersion)
        {
            var clearedConfigSource = foldedNav is { } clearNav
                ? _configRewriter.RemoveRowVersionOnOwnedProperty(ConfigSource, entityName, clearNav, propertyName)
                : _configRewriter.RemoveRowVersion(ConfigSource, owningEntityName, propertyName);
            if (clearedConfigSource == ConfigSource)
            {
                return DiagramEditResult.Fail(
                    $"'{propertyName}' is marked as a row version by a [Timestamp] attribute on the class; remove the attribute to clear it.");
            }

            Apply(ClassSource, clearedConfigSource);
            return DiagramEditResult.Ok();
        }

        var newConfigSource = foldedNav is { } setNav
            ? _configRewriter.SetRowVersionOnOwnedProperty(ConfigSource, entityName, setNav, propertyName)
            : _configRewriter.SetRowVersion(ConfigSource, owningEntityName, propertyName);
        Apply(ClassSource, newConfigSource);
        return DiagramEditResult.Ok();
    }

    public DiagramEditResult SetConcurrencyToken(string entityName, string propertyName, bool isConcurrencyToken)
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

        if (isConcurrencyToken == property.IsConcurrencyToken)
        {
            return DiagramEditResult.Ok();
        }

        var foldedNav = property.FoldKind != FoldKind.None ? property.OwnerNavigationProperty : null;
        if (foldedNav is not null && ValidateOwnedEditDepth(entityName, property, foldedNav) is { } depthFailure)
        {
            return depthFailure;
        }

        var owningEntityName = ResolveDeclaringEntity(entityName, propertyName);

        if (!isConcurrencyToken)
        {
            var clearedConfigSource = foldedNav is { } clearNav
                ? _configRewriter.RemoveConcurrencyTokenOnOwnedProperty(ConfigSource, entityName, clearNav, propertyName)
                : _configRewriter.RemoveConcurrencyToken(ConfigSource, owningEntityName, propertyName);
            if (clearedConfigSource == ConfigSource)
            {
                return DiagramEditResult.Fail(
                    $"'{propertyName}' is marked as a concurrency token by a [ConcurrencyCheck] attribute on the class; remove the attribute to clear it.");
            }

            Apply(ClassSource, clearedConfigSource);
            return DiagramEditResult.Ok();
        }

        var newConfigSource = foldedNav is { } setNav
            ? _configRewriter.SetConcurrencyTokenOnOwnedProperty(ConfigSource, entityName, setNav, propertyName)
            : _configRewriter.SetConcurrencyToken(ConfigSource, owningEntityName, propertyName);
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

        var foldedNav = property.FoldKind != FoldKind.None ? property.OwnerNavigationProperty : null;
        if (foldedNav is not null && ValidateOwnedEditDepth(entityName, property, foldedNav) is { } depthFailure)
        {
            return depthFailure;
        }

        var owningEntityName = ResolveDeclaringEntity(entityName, propertyName);

        if (precision is null)
        {
            if (property.Precision is null && property.Scale is null)
            {
                return DiagramEditResult.Ok();
            }

            var clearedConfigSource = foldedNav is { } clearNav
                ? _configRewriter.RemovePrecisionOnOwnedProperty(ConfigSource, entityName, clearNav, propertyName)
                : _configRewriter.RemovePrecision(ConfigSource, owningEntityName, propertyName);
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

        var newConfigSource = foldedNav is { } setNav
            ? _configRewriter.SetPrecisionOnOwnedProperty(ConfigSource, entityName, setNav, propertyName, precision.Value, scale)
            : _configRewriter.RewritePrecision(ConfigSource, owningEntityName, propertyName, precision.Value, scale);
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

        var trimmedInput = string.IsNullOrWhiteSpace(literalText) ? null : literalText.Trim();

        string? normalizedLiteral = null;
        if (trimmedInput is not null)
        {
            normalizedLiteral = FormatDefaultValueLiteral(trimmedInput, property.ClrType);
            if (normalizedLiteral is null)
            {
                return DiagramEditResult.Fail(
                    $"'{trimmedInput}' is not a valid default value for '{propertyName}'. For SQL expressions like GETDATE() or NEWID(), use the Default value SQL field instead.");
            }
        }

        if (normalizedLiteral == property.DefaultValueLiteral)
        {
            return DiagramEditResult.Ok();
        }

        string newConfigSource;
        if (property.FoldKind != FoldKind.None && property.OwnerNavigationProperty is { } defaultValueNav)
        {
            if (ValidateOwnedEditDepth(entityName, property, defaultValueNav) is { } foldFailure)
            {
                return foldFailure;
            }

            newConfigSource = normalizedLiteral is null
                ? _configRewriter.RemoveDefaultValueOnOwnedProperty(ConfigSource, entityName, defaultValueNav, propertyName)
                : _configRewriter.SetDefaultValueOnOwnedProperty(ConfigSource, entityName, defaultValueNav, propertyName, normalizedLiteral);
        }
        else
        {
            newConfigSource = normalizedLiteral is null
                ? _configRewriter.RemoveDefaultValue(ConfigSource, ResolveDeclaringEntity(entityName, propertyName), propertyName)
                : _configRewriter.SetDefaultValue(ConfigSource, ResolveDeclaringEntity(entityName, propertyName), propertyName, normalizedLiteral);
        }

        Apply(ClassSource, newConfigSource);
        return DiagramEditResult.Ok();
    }

    public DiagramEditResult SetDefaultValueSql(string entityName, string propertyName, string? sql)
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

        var normalizedSql = string.IsNullOrWhiteSpace(sql) ? null : sql.Trim();
        if (normalizedSql == property.DefaultValueSql)
        {
            return DiagramEditResult.Ok();
        }

        string newConfigSource;
        if (property.FoldKind != FoldKind.None && property.OwnerNavigationProperty is { } defaultValueSqlNav)
        {
            if (ValidateOwnedEditDepth(entityName, property, defaultValueSqlNav) is { } foldFailure)
            {
                return foldFailure;
            }

            newConfigSource = normalizedSql is null
                ? _configRewriter.RemoveDefaultValueSqlOnOwnedProperty(ConfigSource, entityName, defaultValueSqlNav, propertyName)
                : _configRewriter.SetDefaultValueSqlOnOwnedProperty(ConfigSource, entityName, defaultValueSqlNav, propertyName, normalizedSql);
        }
        else
        {
            newConfigSource = normalizedSql is null
                ? _configRewriter.RemoveDefaultValueSql(ConfigSource, ResolveDeclaringEntity(entityName, propertyName), propertyName)
                : _configRewriter.SetDefaultValueSql(ConfigSource, ResolveDeclaringEntity(entityName, propertyName), propertyName, normalizedSql);
        }

        Apply(ClassSource, newConfigSource);
        return DiagramEditResult.Ok();
    }

    public DiagramEditResult SetComputedColumnSql(string entityName, string propertyName, string? sql, bool? isStored)
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

        var normalizedSql = string.IsNullOrWhiteSpace(sql) ? null : sql.Trim();
        if (normalizedSql == property.ComputedColumnSql && isStored == property.ComputedColumnSqlIsStored)
        {
            return DiagramEditResult.Ok();
        }

        string newConfigSource;
        if (property.FoldKind != FoldKind.None && property.OwnerNavigationProperty is { } computedColumnSqlNav)
        {
            if (ValidateOwnedEditDepth(entityName, property, computedColumnSqlNav) is { } foldFailure)
            {
                return foldFailure;
            }

            newConfigSource = normalizedSql is null
                ? _configRewriter.RemoveComputedColumnSqlOnOwnedProperty(ConfigSource, entityName, computedColumnSqlNav, propertyName)
                : _configRewriter.SetComputedColumnSqlOnOwnedProperty(ConfigSource, entityName, computedColumnSqlNav, propertyName, normalizedSql, isStored);
        }
        else
        {
            newConfigSource = normalizedSql is null
                ? _configRewriter.RemoveComputedColumnSql(ConfigSource, ResolveDeclaringEntity(entityName, propertyName), propertyName)
                : _configRewriter.SetComputedColumnSql(ConfigSource, ResolveDeclaringEntity(entityName, propertyName), propertyName, normalizedSql, isStored);
        }

        Apply(ClassSource, newConfigSource);
        return DiagramEditResult.Ok();
    }

    private static readonly HashSet<string> QuotedDefaultValueClrTypes = new(StringComparer.Ordinal)
    {
        "string", "Guid", "DateTime", "DateTimeOffset", "DateOnly", "TimeOnly",
    };

    private static string? FormatDefaultValueLiteral(string rawText, string clrType)
    {
        var baseType = clrType.TrimEnd('?');

        if (!QuotedDefaultValueClrTypes.Contains(baseType))
        {
            return IsLiteralExpressionText(rawText) ? rawText : null;
        }

        if (IsLiteralExpressionText(rawText) && SyntaxFactory.ParseExpression(rawText).IsKind(SyntaxKind.StringLiteralExpression))
        {
            return rawText;
        }

        var quoted = SyntaxFactory.Literal(rawText).ToString();
        return IsValidExpressionText(quoted) ? quoted : null;
    }

    private static bool IsLiteralExpressionText(string text)
    {
        return IsValidExpressionText(text) && SyntaxFactory.ParseExpression(text) is LiteralExpressionSyntax;
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

    public DiagramEditResult SetRelationshipShape(
        RelationshipModel relationship,
        RelationshipKind newKind,
        IReadOnlyList<string> newForeignKeyProperties,
        string? newOnDeleteBehavior,
        string? newConstraintName = null)
    {
        if (!Current.Relationships.Contains(relationship))
        {
            return DiagramEditResult.Fail("Relationship no longer exists.");
        }

        if (newKind == relationship.Kind
            && newForeignKeyProperties.SequenceEqual(relationship.ForeignKeyProperties)
            && newOnDeleteBehavior == relationship.OnDeleteBehavior
            && newConstraintName == relationship.ConstraintName)
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

        var updated = relationship with
        {
            Kind = newKind,
            ForeignKeyProperties = newForeignKeyProperties,
            OnDeleteBehavior = newOnDeleteBehavior,
            ConstraintName = newConstraintName,
        };

        var withoutOld = relationship.IsInferred
            ? ConfigSource
            : _configRewriter.RemoveRelationship(ConfigSource, relationship);

        if (!relationship.IsInferred && withoutOld == ConfigSource)
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

        if (relationship.IsInferred)
        {
            return DiagramEditResult.Fail(
                "This relationship is inferred from naming convention and isn't backed by explicit " +
                "configuration yet — change its kind or foreign key first to make it explicit.");
        }

        var newConfigSource = _configRewriter.RemoveRelationship(ConfigSource, relationship);
        if (newConfigSource == ConfigSource)
        {
            return DiagramEditResult.Fail("Could not locate this relationship in the source to remove.");
        }

        Apply(ClassSource, newConfigSource);
        return DiagramEditResult.Ok();
    }

    private string ResolveDeclaringEntity(string entityName, string propertyName)
    {
        var property = Current.Entities
            .FirstOrDefault(e => e.Name == entityName)?.Properties
            .FirstOrDefault(p => p.Name == propertyName);

        return property?.DeclaringEntityName ?? entityName;
    }

    /// Guards against writing a fluent-attribute edit into the WRONG owner's builder lambda for a
    /// property folded through a multi-level owned/complex chain (e.g. Order owns Address owns
    /// Country: Country.Code gets re-stamped with OwnerNavigationProperty = "ShippingAddress" — the
    /// OUTERMOST nav, purely for UI grouping — while DeclaringEntityName stays "Country"; see
    /// OwnedTypeInference.Fold's re-stamping comment). Routing straight to navPropertyName's
    /// OwnsOne/OwnsMany/ComplexProperty scope in that case would write e.g.
    /// `b.Property(e => e.Code).HasColumnName(...)` inside Address's builder lambda — but Address has
    /// no Code property (Country does) — producing bogus config that silently fails to round-trip.
    /// Detects the mismatch by resolving navPropertyName's CLR type from the pre-fold class source
    /// (the type OwnerNavigationProperty actually targets) and comparing it against
    /// property.DeclaringEntityName (the type that actually declares the property). For a
    /// single-level fold these always agree (OwnedTypeInference.Fold only sets DeclaringEntityName via
    /// `?? targetName`, so it's exactly the immediate owned/complex type), so this only fires for
    /// genuine multi-level chains. Returns null when the edit is safe to proceed, or a failure result
    /// to surface instead. Editing through multi-level owned/complex chains remains an explicit
    /// non-goal for this plan (see the design spec's Non-goals section) — this only makes that
    /// unsupported case fail safely and visibly instead of corrupting or silently losing the edit.
    private DiagramEditResult? ValidateOwnedEditDepth(string entityName, PropertyModel property, string navPropertyName)
    {
        var rawOwner = new EntityClassParser().Parse(ClassSource).Value.FirstOrDefault(e => e.Name == entityName);
        var navProperty = rawOwner?.Properties.FirstOrDefault(p => p.Name == navPropertyName);
        var targetTypeName = navProperty?.ClrType.TrimEnd('?');

        if (targetTypeName is not null && targetTypeName != property.DeclaringEntityName)
        {
            return DiagramEditResult.Fail(
                $"'{property.Name}' was folded through a multi-level owned/complex chain (via " +
                $"'{entityName}.{navPropertyName}' → '{targetTypeName}', which itself owns the type that " +
                $"actually declares '{property.Name}'). Editing properties folded through multi-level " +
                "owned/complex chains is not yet supported.");
        }

        return null;
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

    private Snapshot CurrentSnapshot() => new(
        ClassSource,
        ConfigSource,
        new Dictionary<string, Guid>(_entityIds),
        new Dictionary<string, string>(_entityFileOrigins),
        new Dictionary<string, string>(_configFileOrigins));

    private void Restore(Snapshot snapshot)
    {
        ClassSource = snapshot.ClassSource;
        ConfigSource = snapshot.ConfigSource;
        _entityIds.Clear();
        foreach (var (name, id) in snapshot.EntityIds)
        {
            _entityIds[name] = id;
        }

        _entityFileOrigins.Clear();
        foreach (var (name, path) in snapshot.EntityFileOrigins)
        {
            _entityFileOrigins[name] = path;
        }

        _configFileOrigins.Clear();
        foreach (var (name, path) in snapshot.ConfigFileOrigins)
        {
            _configFileOrigins[name] = path;
        }

        Current = DiagramModelBuilder.Build(ClassSource, ConfigSource);
    }

    private void Apply(string newClassSource, string newConfigSource)
    {
        _undoStack.Push(CurrentSnapshot());
        _redoStack.Clear();

        ApplyCore(newClassSource, newConfigSource);
    }

    private void ApplyCore(string newClassSource, string newConfigSource)
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

        foreach (var staleName in _entityFileOrigins.Keys.Where(name => !currentNames.Contains(name)).ToList())
        {
            _entityFileOrigins.Remove(staleName);
        }

        foreach (var staleName in _configFileOrigins.Keys.Where(name => !currentNames.Contains(name)).ToList())
        {
            _configFileOrigins.Remove(staleName);
        }
    }

    private static bool IsReservedKeyword(string name) => SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None;

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

    public DiagramEditResult AddSequence(
        string name, string? schema, string? clrType,
        long? startsAt, int? incrementsBy, long? minValue, long? maxValue, bool? isCyclic)
    {
        var normalizedName = string.IsNullOrWhiteSpace(name) ? null : name.Trim();

        if (normalizedName is null)
        {
            return DiagramEditResult.Fail("Sequence name cannot be empty.");
        }

        if (Current.Sequences.Any(s => s.Name == normalizedName))
        {
            return DiagramEditResult.Fail($"A sequence named '{normalizedName}' already exists.");
        }

        var normalizedSchema = string.IsNullOrWhiteSpace(schema) ? null : schema.Trim();
        var normalizedClrType = string.IsNullOrWhiteSpace(clrType) ? null : clrType.Trim();
        var newConfigSource = _configRewriter.SetSequence(ConfigSource, normalizedName, normalizedSchema, normalizedClrType, startsAt, incrementsBy, minValue, maxValue, isCyclic);
        Apply(ClassSource, newConfigSource);
        return DiagramEditResult.Ok();
    }

    public DiagramEditResult SetSequence(
        string name, string? schema, string? clrType,
        long? startsAt, int? incrementsBy, long? minValue, long? maxValue, bool? isCyclic)
    {
        var existing = Current.Sequences.FirstOrDefault(s => s.Name == name);
        if (existing is null)
        {
            return DiagramEditResult.Fail($"No sequence named '{name}' exists.");
        }

        var normalizedSchema = string.IsNullOrWhiteSpace(schema) ? null : schema.Trim();
        var normalizedClrType = string.IsNullOrWhiteSpace(clrType) ? null : clrType.Trim();

        if (normalizedSchema == existing.Schema
            && normalizedClrType == existing.ClrType
            && startsAt == existing.StartsAt
            && incrementsBy == existing.IncrementsBy
            && minValue == existing.MinValue
            && maxValue == existing.MaxValue
            && isCyclic == existing.IsCyclic)
        {
            return DiagramEditResult.Ok();
        }

        var newConfigSource = _configRewriter.SetSequence(ConfigSource, name, normalizedSchema, normalizedClrType, startsAt, incrementsBy, minValue, maxValue, isCyclic);
        Apply(ClassSource, newConfigSource);
        return DiagramEditResult.Ok();
    }

    public DiagramEditResult RemoveSequence(string name)
    {
        if (!Current.Sequences.Any(s => s.Name == name))
        {
            return DiagramEditResult.Fail($"No sequence named '{name}' exists.");
        }

        var newConfigSource = _configRewriter.RemoveSequence(ConfigSource, name);
        Apply(ClassSource, newConfigSource);
        return DiagramEditResult.Ok();
    }

    public DiagramEditResult SetUseSequence(string entityName, string propertyName, string? sequenceName, string? schema)
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

        var normalizedSequenceName = string.IsNullOrWhiteSpace(sequenceName) ? null : sequenceName.Trim();
        var normalizedSchema = string.IsNullOrWhiteSpace(schema) ? null : schema.Trim();
        if (normalizedSequenceName == property.SequenceName && normalizedSchema == property.SequenceSchema)
        {
            return DiagramEditResult.Ok();
        }

        string newConfigSource;
        if (property.FoldKind != FoldKind.None && property.OwnerNavigationProperty is { } useSequenceNav)
        {
            if (ValidateOwnedEditDepth(entityName, property, useSequenceNav) is { } foldFailure)
            {
                return foldFailure;
            }

            newConfigSource = normalizedSequenceName is null
                ? _configRewriter.RemoveUseSequenceOnOwnedProperty(ConfigSource, entityName, useSequenceNav, propertyName)
                : _configRewriter.SetUseSequenceOnOwnedProperty(ConfigSource, entityName, useSequenceNav, propertyName, normalizedSequenceName, normalizedSchema);
        }
        else
        {
            newConfigSource = normalizedSequenceName is null
                ? _configRewriter.RemoveUseSequence(ConfigSource, ResolveDeclaringEntity(entityName, propertyName), propertyName)
                : _configRewriter.SetUseSequence(ConfigSource, ResolveDeclaringEntity(entityName, propertyName), propertyName, normalizedSequenceName, normalizedSchema);
        }

        Apply(ClassSource, newConfigSource);
        return DiagramEditResult.Ok();
    }
}
