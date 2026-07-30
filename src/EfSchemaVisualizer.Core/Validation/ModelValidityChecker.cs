using System.Collections.Generic;
using System.Linq;
using EfSchemaVisualizer.Core.Model;
using EfSchemaVisualizer.Core.Parsing;
using Microsoft.CodeAnalysis.Text;

namespace EfSchemaVisualizer.Core.Validation;

/// Checks a fully-built model (all parsing/merging/inference stages already applied) for shapes
/// that EF itself would reject or warn about at model-build time — distinct from the parse
/// diagnostics elsewhere, which only mean "this source couldn't be read". These checks run last,
/// against the final resolved <see cref="EntityModel"/>/<see cref="RelationshipModel"/> lists, so
/// they see the same model the diagram renders.
public static class ModelValidityChecker
{
    /// CLR value types <c>HasPrecision</c> is valid for beyond <c>decimal</c> — EF also uses it to
    /// configure fractional-seconds precision on temporal types. <c>HasPrecision(precision, scale)</c>
    /// (the two-arg overload) is decimal-only; a <c>Scale</c> value on any of these means the model
    /// won't build.
    private static readonly HashSet<string> TemporalPrecisionTypes = new()
    {
        "DateTime", "DateTimeOffset", "TimeSpan", "TimeOnly",
    };

    /// Built-in CLR value types that can never be null at the CLR level, regardless of C# nullable
    /// reference type settings. Restricting the "IsRequired(false) is impossible" check to these
    /// avoids false positives on reference types (e.g. <c>string</c>), whose runtime nullability
    /// depends on project-wide nullable-reference-type settings this model doesn't have visibility into.
    private static readonly HashSet<string> NonNullableValueTypes = new()
    {
        "int", "long", "short", "byte", "sbyte", "uint", "ulong", "ushort",
        "bool", "decimal", "double", "float", "char",
        "DateTime", "DateTimeOffset", "DateOnly", "TimeOnly", "TimeSpan", "Guid",
    };

    public static IReadOnlyList<Diagnostic> Check(
        IReadOnlyList<EntityModel> entities,
        IReadOnlyList<RelationshipModel> relationships)
    {
        var diagnostics = new List<Diagnostic>();
        var entitiesByName = entities.ToDictionary(e => e.Name);

        foreach (var entity in entities)
        {
            CheckMissingKey(entity, diagnostics);
            CheckDuplicateColumnNames(entity, diagnostics);
            CheckIsRequiredFalseOnNonNullableProperty(entity, diagnostics);
            CheckPrecisionOrScaleOnUnsupportedType(entity, diagnostics);
            CheckIndexReferencesMissingProperty(entity, diagnostics);
        }

        foreach (var relationship in relationships)
        {
            CheckForeignKeyTargetsKeylessPrincipal(relationship, entitiesByName, diagnostics);
            CheckPrincipalKeyReferencesMissingProperty(relationship, entitiesByName, diagnostics);
        }

        return diagnostics;
    }

    /// An entity with no key, that hasn't opted into keyless via `HasNoKey()`/`[Keyless]`, and isn't
    /// an owned type (which shares its owner's key rather than declaring its own). EF refuses to
    /// build a model containing one.
    private static void CheckMissingKey(EntityModel entity, List<Diagnostic> diagnostics)
    {
        if (entity.IsKeyless || entity.IsOwned || entity.KeyPropertyNames.Count > 0)
        {
            return;
        }

        diagnostics.Add(new Diagnostic(
            DiagnosticCodes.EntityHasNoKey,
            $"Entity '{entity.Name}' has no primary key and isn't marked keyless (HasNoKey/[Keyless]). EF will refuse to build this model.",
            entity.Name,
            PropertyName: null,
            TextSpan.FromBounds(0, 0),
            DiagnosticCategory.ModelValidity));
    }

    /// Two properties on the same entity mapped to the same database column name (explicit
    /// `HasColumnName`/`[Column]`, or falling back to the property name) collide on the same table.
    private static void CheckDuplicateColumnNames(EntityModel entity, List<Diagnostic> diagnostics)
    {
        var duplicateGroups = entity.Properties
            .GroupBy(p => p.ColumnName ?? p.Name)
            .Where(g => g.Count() > 1);

        foreach (var group in duplicateGroups)
        {
            var propertyNames = string.Join(", ", group.Select(p => p.Name));
            diagnostics.Add(new Diagnostic(
                DiagnosticCodes.DuplicateColumnName,
                $"Properties {propertyNames} on entity '{entity.Name}' all map to column '{group.Key}'.",
                entity.Name,
                PropertyName: null,
                TextSpan.FromBounds(0, 0),
                DiagnosticCategory.ModelValidity));
        }
    }

    /// `IsRequired(false)` explicitly asks EF to make a property optional — impossible for a
    /// non-nullable CLR value type, which can never hold null. (The reverse, `IsRequired()`/
    /// `IsRequired(true)` on a nullable CLR type, is legal — it just makes the column stricter than
    /// the CLR type — so only this direction is a genuine model error.)
    private static void CheckIsRequiredFalseOnNonNullableProperty(EntityModel entity, List<Diagnostic> diagnostics)
    {
        foreach (var property in entity.Properties)
        {
            if (property.IsRequiredOverride != false || property.IsNullable || !NonNullableValueTypes.Contains(property.ClrType))
            {
                continue;
            }

            diagnostics.Add(new Diagnostic(
                DiagnosticCodes.IsRequiredFalseOnNonNullableProperty,
                $"'{entity.Name}.{property.Name}' is configured IsRequired(false), but its CLR type '{property.ClrType}' can never be null.",
                entity.Name,
                property.Name,
                TextSpan.FromBounds(0, 0),
                DiagnosticCategory.ModelValidity));
        }
    }

    /// `HasPrecision`/`HasPrecision(precision, scale)` only apply to `decimal` (both args) and to
    /// temporal types (precision only, for fractional-seconds digits). Anything else — e.g. `int`,
    /// `string` — won't build.
    private static void CheckPrecisionOrScaleOnUnsupportedType(EntityModel entity, List<Diagnostic> diagnostics)
    {
        foreach (var property in entity.Properties)
        {
            var isDecimal = property.ClrType == "decimal";
            var invalid = (property.Scale.HasValue && !isDecimal)
                || (property.Precision.HasValue && !isDecimal && !TemporalPrecisionTypes.Contains(property.ClrType));

            if (!invalid)
            {
                continue;
            }

            diagnostics.Add(new Diagnostic(
                DiagnosticCodes.PrecisionOrScaleOnUnsupportedType,
                $"'{entity.Name}.{property.Name}' has HasPrecision/scale configured, but its CLR type '{property.ClrType}' doesn't support precision.",
                entity.Name,
                property.Name,
                TextSpan.FromBounds(0, 0),
                DiagnosticCategory.ModelValidity));
        }
    }

    /// An index (fluent `HasIndex` or `[Index]`) naming a property that no longer exists on the
    /// entity — typically left behind after the property was renamed or removed.
    private static void CheckIndexReferencesMissingProperty(EntityModel entity, List<Diagnostic> diagnostics)
    {
        var propertyNames = entity.Properties.Select(p => p.Name).ToHashSet();

        foreach (var index in entity.Indexes)
        {
            var missing = index.PropertyNames.Where(name => !propertyNames.Contains(name)).ToList();
            if (missing.Count == 0)
            {
                continue;
            }

            var missingList = string.Join(", ", missing);
            diagnostics.Add(new Diagnostic(
                DiagnosticCodes.IndexReferencesMissingProperty,
                $"Index on entity '{entity.Name}' references propert{(missing.Count == 1 ? "y" : "ies")} '{missingList}', which no longer exist on the entity.",
                entity.Name,
                PropertyName: null,
                TextSpan.FromBounds(0, 0),
                DiagnosticCategory.ModelValidity));
        }
    }

    /// A foreign key can't target a keyless principal (`HasNoKey()`/`[Keyless]`) — it has no PK or
    /// alternate key for the FK to reference. (Without `HasPrincipalKey` parsing (see backlog), a
    /// relationship's implicit target is always the principal's own key, so this is the only
    /// "targets a non-key property" case detectable from the current model shape.)
    private static void CheckForeignKeyTargetsKeylessPrincipal(
        RelationshipModel relationship,
        Dictionary<string, EntityModel> entitiesByName,
        List<Diagnostic> diagnostics)
    {
        if (relationship.Kind is RelationshipKind.Inheritance or RelationshipKind.Owned
            || relationship.ForeignKeyProperties.Count == 0)
        {
            return;
        }

        if (!entitiesByName.TryGetValue(relationship.PrincipalEntity, out var principal) || !principal.IsKeyless)
        {
            return;
        }

        diagnostics.Add(new Diagnostic(
            DiagnosticCodes.ForeignKeyTargetsKeylessPrincipal,
            $"'{relationship.DependentEntity}' has a foreign key to '{relationship.PrincipalEntity}', which is keyless (HasNoKey) and has no key for the foreign key to target.",
            relationship.DependentEntity,
            PropertyName: null,
            TextSpan.FromBounds(0, 0),
            DiagnosticCategory.ModelValidity));
    }

    /// `HasPrincipalKey` naming a property that no longer exists on the principal entity —
    /// typically left behind after the property was renamed or removed. Deliberately does
    /// NOT check whether the named properties already form a declared key (PK or
    /// `HasAlternateKey`): calling `HasPrincipalKey` on properties that aren't already a key
    /// implicitly creates the alternate key for you at EF model-build time, so that shape is
    /// valid, common usage — only a missing property is a genuine build-time failure.
    private static void CheckPrincipalKeyReferencesMissingProperty(
        RelationshipModel relationship,
        Dictionary<string, EntityModel> entitiesByName,
        List<Diagnostic> diagnostics)
    {
        if (relationship.PrincipalKeyProperties.Count == 0
            || relationship.Kind is RelationshipKind.Inheritance or RelationshipKind.Owned)
        {
            return;
        }

        if (!entitiesByName.TryGetValue(relationship.PrincipalEntity, out var principal))
        {
            return;
        }

        var propertyNames = principal.Properties.Select(p => p.Name).ToHashSet();
        var missing = relationship.PrincipalKeyProperties.Where(name => !propertyNames.Contains(name)).ToList();
        if (missing.Count == 0)
        {
            return;
        }

        var missingList = string.Join(", ", missing);
        diagnostics.Add(new Diagnostic(
            DiagnosticCodes.PrincipalKeyReferencesMissingProperty,
            $"Relationship from '{relationship.DependentEntity}' references principal key propert{(missing.Count == 1 ? "y" : "ies")} '{missingList}' on '{relationship.PrincipalEntity}', which no longer exist on the entity.",
            relationship.DependentEntity,
            PropertyName: null,
            TextSpan.FromBounds(0, 0),
            DiagnosticCategory.ModelValidity));
    }
}
