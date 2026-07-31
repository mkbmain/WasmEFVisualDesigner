using System.Collections.Generic;
using System.Text;
using EfSchemaVisualizer.Core.Archive;
using EfSchemaVisualizer.Core.Model;

namespace EfSchemaVisualizer.Web.Diagram;

/// <summary>
/// Renders a <see cref="DiagramModelResult"/> as dialect-specific SQL DDL text (SQL Server /
/// PostgreSQL / SQLite). Pure string generation from the parsed model, same posture as
/// <see cref="MermaidExporter"/> — no dependency on the live <c>BlazorDiagram</c>.
/// </summary>
public static class SqlDdlExporter
{
    internal static string QuoteIdentifier(string name, ScaffoldProvider provider) => provider switch
    {
        ScaffoldProvider.SqlServer => $"[{name}]",
        _ => $"\"{name}\"",
    };

    internal static string PhysicalTableName(EntityModel entity) => entity.TableName ?? entity.Name;

    internal static string QualifiedTableName(EntityModel entity, ScaffoldProvider provider)
    {
        var table = QuoteIdentifier(PhysicalTableName(entity), provider);

        if (provider == ScaffoldProvider.Sqlite || entity.Schema is null)
        {
            return table;
        }

        return $"{QuoteIdentifier(entity.Schema, provider)}.{table}";
    }

    private static readonly HashSet<string> IntegerClrTypes = new() { "int", "long", "short", "byte" };

    internal static bool IsIdentityCandidate(PropertyModel property) =>
        property.ValueGenerated != "Never" && IntegerClrTypes.Contains(property.ClrType.TrimEnd('?'));

    internal static string RenderColumnDefinition(PropertyModel property, bool isSoleIntegerIdentityPrimaryKey, ScaffoldProvider provider)
    {
        var name = QuoteIdentifier(property.ColumnName ?? property.Name, provider);
        var sqlType = SqlColumnTypeMapper.MapType(property, provider);
        var nullability = property.IsNullable && property.IsRequiredOverride != true ? "NULL" : "NOT NULL";

        if (property.ComputedColumnSql is not null)
        {
            return RenderComputedColumn(name, sqlType, nullability, property, provider);
        }

        var identity = isSoleIntegerIdentityPrimaryKey ? IdentityClause(provider) : "";
        var defaultClause = RenderDefaultClause(property);

        return $"{name} {sqlType}{identity} {nullability}{defaultClause}";
    }

    private static string RenderComputedColumn(string name, string sqlType, string nullability, PropertyModel property, ScaffoldProvider provider)
    {
        return provider switch
        {
            ScaffoldProvider.SqlServer => property.ComputedColumnSqlIsStored != false
                ? $"{name} {sqlType} {nullability} AS ({property.ComputedColumnSql}) PERSISTED"
                : $"{name} {sqlType} {nullability} AS ({property.ComputedColumnSql})",
            _ => $"{name} {sqlType} GENERATED ALWAYS AS ({property.ComputedColumnSql}) STORED",
        };
    }

    private static string IdentityClause(ScaffoldProvider provider) => provider switch
    {
        ScaffoldProvider.SqlServer => " IDENTITY(1,1)",
        ScaffoldProvider.PostgreSql => " GENERATED ALWAYS AS IDENTITY",
        _ => "",
    };

    private static string RenderDefaultClause(PropertyModel property)
    {
        if (property.DefaultValueLiteral is not null)
        {
            return $" DEFAULT {property.DefaultValueLiteral}";
        }

        if (property.DefaultValueSql is not null)
        {
            return $" DEFAULT ({property.DefaultValueSql})";
        }

        return "";
    }

    internal static string RenderCreateTable(EntityModel entity, IReadOnlyList<string> primaryKeyColumnNames, ScaffoldProvider provider)
    {
        var sb = new StringBuilder();
        sb.Append("CREATE TABLE ").Append(QualifiedTableName(entity, provider)).Append(" (\n");

        var sqliteInlineAutoIncrement =
            provider == ScaffoldProvider.Sqlite &&
            primaryKeyColumnNames.Count == 1 &&
            entity.Properties.FirstOrDefault(p => p.Name == primaryKeyColumnNames[0]) is { } solePk &&
            IsIdentityCandidate(solePk);

        var lines = new List<string>();

        foreach (var property in entity.Properties)
        {
            var isSolePk = primaryKeyColumnNames.Count == 1 && property.Name == primaryKeyColumnNames[0];

            if (sqliteInlineAutoIncrement && isSolePk)
            {
                var name = QuoteIdentifier(property.ColumnName ?? property.Name, provider);
                lines.Add($"    {name} INTEGER PRIMARY KEY AUTOINCREMENT");
                continue;
            }

            var isIdentityColumn = isSolePk && provider != ScaffoldProvider.Sqlite && IsIdentityCandidate(property);
            lines.Add("    " + RenderColumnDefinition(property, isIdentityColumn, provider));
        }

        if (!entity.IsKeyless && primaryKeyColumnNames.Count > 0 && !sqliteInlineAutoIncrement)
        {
            var keyName = entity.KeyName ?? $"PK_{PhysicalTableName(entity)}";
            var columns = string.Join(", ", primaryKeyColumnNames.Select(c => QuoteIdentifier(c, provider)));
            lines.Add($"    CONSTRAINT {QuoteIdentifier(keyName, provider)} PRIMARY KEY ({columns})");
        }

        foreach (var check in entity.CheckConstraints)
        {
            lines.Add($"    CONSTRAINT {QuoteIdentifier(check.Name, provider)} CHECK ({check.Sql})");
        }

        foreach (var alternateKey in entity.AlternateKeys)
        {
            var akName = $"AK_{PhysicalTableName(entity)}_{string.Join("_", alternateKey)}";
            var columns = string.Join(", ", alternateKey.Select(c => QuoteIdentifier(c, provider)));
            lines.Add($"    CONSTRAINT {QuoteIdentifier(akName, provider)} UNIQUE ({columns})");
        }

        sb.Append(string.Join(",\n", lines));
        sb.Append("\n);\n");
        return sb.ToString();
    }

    internal static List<EntityModel> SelectPhysicalEntities(IReadOnlyList<EntityModel> entities) =>
        entities.Where(e => e.ViewName is null && e.FunctionName is null).ToList();

    internal static List<EntityModel> CollectDescendants(EntityModel root, IReadOnlyList<EntityModel> allEntities)
    {
        var result = new List<EntityModel>();
        var visited = new HashSet<string> { root.Name };
        var frontier = new Queue<string>();
        frontier.Enqueue(root.Name);

        while (frontier.Count > 0)
        {
            var current = frontier.Dequeue();
            foreach (var child in allEntities.Where(e => e.BaseEntityName == current))
            {
                if (visited.Add(child.Name))
                {
                    result.Add(child);
                    frontier.Enqueue(child.Name);
                }
            }
        }

        return result;
    }

    internal static EntityModel BuildTphMergedEntity(EntityModel root, IReadOnlyList<EntityModel> allEntities)
    {
        var columns = new List<PropertyModel>(root.Properties);
        var seenNames = new HashSet<string>(columns.Select(c => c.Name));

        foreach (var descendant in CollectDescendants(root, allEntities))
        {
            foreach (var property in descendant.Properties.Where(p => p.DeclaringEntityName is null))
            {
                if (seenNames.Add(property.Name))
                {
                    columns.Add(property with { IsNullable = true });
                }
            }
        }

        var discriminatorName = root.DiscriminatorPropertyName ?? "Discriminator";
        var discriminatorClrType = root.DiscriminatorClrType ?? "string";
        columns.Add(new PropertyModel(discriminatorName, discriminatorClrType, IsNullable: false, MaxLength: null));

        return root with { Properties = columns };
    }

    internal static List<EntityModel> OrderTablesByDependency(
        IReadOnlyList<EntityModel> physicalEntities, IReadOnlyList<RelationshipModel> relationships)
    {
        var layers = DiagramAutoLayout.ComputeLayers(physicalEntities, relationships);

        return physicalEntities
            .Select((entity, index) => (entity, index))
            .OrderBy(t => layers.GetValueOrDefault(t.entity.Name, 0))
            .ThenBy(t => t.index)
            .Select(t => t.entity)
            .ToList();
    }

    internal static string RenderCreateIndex(EntityModel entity, IndexModel index, ScaffoldProvider provider)
    {
        var indexName = index.Name ?? $"IX_{PhysicalTableName(entity)}_{string.Join("_", index.PropertyNames)}";
        var uniqueKeyword = index.IsUnique ? "UNIQUE " : "";
        var columns = string.Join(", ", index.PropertyNames.Select(c => QuoteIdentifier(c, provider)));

        return $"CREATE {uniqueKeyword}INDEX {QuoteIdentifier(indexName, provider)} ON {QualifiedTableName(entity, provider)} ({columns});\n";
    }

    internal static bool IsSkippedTphMember(EntityModel entity, IReadOnlyList<EntityModel> allEntities) =>
        entity.MappingStrategy == MappingStrategy.Tph &&
        entity.BaseEntityName is not null &&
        allEntities.Any(e => e.Name == entity.BaseEntityName);

    /// <summary>
    /// Walks from a (possibly TPH-derived) entity up its base-entity chain until it reaches the
    /// entity whose merged table is actually emitted by <see cref="Export"/> — i.e. the first
    /// entity that is not itself a skipped TPH member. Guards against cycles the same way
    /// <see cref="CollectDescendants"/> does.
    /// </summary>
    internal static EntityModel ResolveTphTableEntity(EntityModel entity, IReadOnlyList<EntityModel> allEntities)
    {
        var current = entity;
        var visited = new HashSet<string> { current.Name };

        while (IsSkippedTphMember(current, allEntities) && current.BaseEntityName is { } baseName)
        {
            var baseEntity = allEntities.FirstOrDefault(e => e.Name == baseName);
            if (baseEntity is null || !visited.Add(baseEntity.Name))
            {
                break;
            }

            current = baseEntity;
        }

        return current;
    }

    internal static string RenderSequence(SequenceModel sequence, ScaffoldProvider provider)
    {
        if (provider == ScaffoldProvider.Sqlite)
        {
            return $"-- SQLite has no CREATE SEQUENCE equivalent; skipped sequence \"{sequence.Name}\".\n";
        }

        var name = sequence.Schema is not null
            ? $"{QuoteIdentifier(sequence.Schema, provider)}.{QuoteIdentifier(sequence.Name, provider)}"
            : QuoteIdentifier(sequence.Name, provider);

        var clauses = new List<string>();
        if (sequence.StartsAt is long start)
        {
            clauses.Add($"START WITH {start}");
        }

        if (sequence.IncrementsBy is int increment)
        {
            clauses.Add($"INCREMENT BY {increment}");
        }

        var suffix = clauses.Count > 0 ? " " + string.Join(" ", clauses) : "";
        return $"CREATE SEQUENCE {name}{suffix};\n";
    }

    public static string Export(DiagramModelResult result, ScaffoldProvider provider)
    {
        var sb = new StringBuilder();

        foreach (var sequence in result.Sequences)
        {
            sb.Append(RenderSequence(sequence, provider));
        }

        var physicalEntities = SelectPhysicalEntities(result.Entities);
        var orderedEntities = OrderTablesByDependency(physicalEntities, result.Relationships);
        var byName = physicalEntities.ToDictionary(e => e.Name);

        foreach (var entity in orderedEntities)
        {
            if (IsSkippedTphMember(entity, physicalEntities))
            {
                continue;
            }

            var isTphRootWithDescendants =
                entity.MappingStrategy == MappingStrategy.Tph &&
                CollectDescendants(entity, physicalEntities).Count > 0;

            if (isTphRootWithDescendants)
            {
                var merged = BuildTphMergedEntity(entity, physicalEntities);
                sb.Append(RenderCreateTable(merged, merged.KeyPropertyNames, provider));
                continue;
            }

            var primaryKeyColumns = entity.IsSharedType && entity.KeyPropertyNames.Count == 0
                ? ResolveJoinEntityKeyColumns(entity, result.Relationships)
                : entity.KeyPropertyNames;

            sb.Append(RenderCreateTable(entity, primaryKeyColumns, provider));
        }

        foreach (var entity in orderedEntities)
        {
            if (IsSkippedTphMember(entity, physicalEntities))
            {
                continue;
            }

            foreach (var index in entity.Indexes)
            {
                sb.Append(RenderCreateIndex(entity, index, provider));
            }
        }

        foreach (var relationship in result.Relationships)
        {
            AppendForeignKey(sb, relationship, byName, physicalEntities, provider);
        }

        return sb.ToString();
    }

    private static IReadOnlyList<string> ResolveJoinEntityKeyColumns(EntityModel joinEntity, IReadOnlyList<RelationshipModel> relationships)
    {
        var owning = relationships.FirstOrDefault(r => r.JoinEntityName == joinEntity.Name);
        return owning is null
            ? Array.Empty<string>()
            : owning.JoinEntityLeftForeignKey.Concat(owning.JoinEntityRightForeignKey).ToList();
    }

    private static void AppendForeignKey(
        StringBuilder sb, RelationshipModel relationship, Dictionary<string, EntityModel> byName,
        IReadOnlyList<EntityModel> allEntities, ScaffoldProvider provider)
    {
        switch (relationship.Kind)
        {
            case RelationshipKind.OneToOne or RelationshipKind.OneToMany when relationship.ForeignKeyProperties.Count > 0:
            {
                if (!byName.TryGetValue(relationship.DependentEntity, out var dependentRaw) ||
                    !byName.TryGetValue(relationship.PrincipalEntity, out var principalRaw))
                {
                    return;
                }

                var dependent = ResolveTphTableEntity(dependentRaw, allEntities);
                var principal = ResolveTphTableEntity(principalRaw, allEntities);

                var principalKeyColumns = relationship.PrincipalKeyProperties.Count > 0
                    ? relationship.PrincipalKeyProperties
                    : principal.KeyPropertyNames;

                var constraintName = relationship.ConstraintName
                    ?? $"FK_{PhysicalTableName(dependent)}_{PhysicalTableName(principal)}_{string.Join("_", relationship.ForeignKeyProperties)}";

                AppendAlterTableForeignKey(sb, dependent, principal, relationship.ForeignKeyProperties, principalKeyColumns, constraintName, provider);
                return;
            }

            case RelationshipKind.Inheritance:
            {
                if (!byName.TryGetValue(relationship.DependentEntity, out var child) ||
                    !byName.TryGetValue(relationship.PrincipalEntity, out var parent) ||
                    child.MappingStrategy != MappingStrategy.Tpt)
                {
                    return;
                }

                var keyColumns = child.KeyPropertyNames;
                var constraintName = $"FK_{PhysicalTableName(child)}_{PhysicalTableName(parent)}";
                AppendAlterTableForeignKey(sb, child, parent, keyColumns, keyColumns, constraintName, provider);
                return;
            }

            case RelationshipKind.ManyToMany when relationship.JoinEntityName is not null:
            {
                if (!byName.TryGetValue(relationship.JoinEntityName, out var join) ||
                    !byName.TryGetValue(relationship.PrincipalEntity, out var leftRaw) ||
                    !byName.TryGetValue(relationship.DependentEntity, out var rightRaw))
                {
                    return;
                }

                var left = ResolveTphTableEntity(leftRaw, allEntities);
                var right = ResolveTphTableEntity(rightRaw, allEntities);

                AppendAlterTableForeignKey(
                    sb, join, left, relationship.JoinEntityLeftForeignKey, left.KeyPropertyNames,
                    $"FK_{PhysicalTableName(join)}_{PhysicalTableName(left)}_{string.Join("_", relationship.JoinEntityLeftForeignKey)}", provider);
                AppendAlterTableForeignKey(
                    sb, join, right, relationship.JoinEntityRightForeignKey, right.KeyPropertyNames,
                    $"FK_{PhysicalTableName(join)}_{PhysicalTableName(right)}_{string.Join("_", relationship.JoinEntityRightForeignKey)}", provider);
                return;
            }
        }
    }

    private static void AppendAlterTableForeignKey(
        StringBuilder sb, EntityModel dependent, EntityModel principal,
        IReadOnlyList<string> foreignKeyColumns, IReadOnlyList<string> principalKeyColumns,
        string constraintName, ScaffoldProvider provider)
    {
        var fkColumns = string.Join(", ", foreignKeyColumns.Select(c => QuoteIdentifier(c, provider)));
        var pkColumns = string.Join(", ", principalKeyColumns.Select(c => QuoteIdentifier(c, provider)));

        sb.Append("ALTER TABLE ").Append(QualifiedTableName(dependent, provider))
          .Append(" ADD CONSTRAINT ").Append(QuoteIdentifier(constraintName, provider))
          .Append(" FOREIGN KEY (").Append(fkColumns).Append(") REFERENCES ")
          .Append(QualifiedTableName(principal, provider)).Append(" (").Append(pkColumns).Append(");\n");
    }
}
