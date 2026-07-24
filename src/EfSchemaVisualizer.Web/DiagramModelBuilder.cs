using System.Linq;
using EfSchemaVisualizer.Core.Inference;
using EfSchemaVisualizer.Core.Merging;
using EfSchemaVisualizer.Core.Model;
using EfSchemaVisualizer.Core.Parsing;

namespace EfSchemaVisualizer.Web;

public sealed record DiagramModelResult(
    IReadOnlyList<EntityModel> Entities,
    IReadOnlyList<RelationshipModel> Relationships,
    IReadOnlyList<Diagnostic> Diagnostics);

public static class DiagramModelBuilder
{
    public static DiagramModelResult Build(string classSource, string configSource)
    {
        var entityParser = new EntityClassParser();
        var configParser = new FluentConfigParser();

        var entityResult = entityParser.Parse(classSource);
        var diagnostics = new List<Diagnostic>(entityResult.Diagnostics);

        var maxLengths = configParser.ParseMaxLengths(configSource);
        var precisions = configParser.ParsePrecisions(configSource);
        var isRequired = configParser.ParseIsRequired(configSource);
        var keys = configParser.ParseKeys(configSource);
        var alternateKeys = configParser.ParseAlternateKeys(configSource);
        var queryFilters = configParser.ParseQueryFilters(configSource);
        var comments = configParser.ParseComments(configSource);
        var unicodeFlags = configParser.ParseUnicodeFlags(configSource);
        var fixedLengthFlags = configParser.ParseFixedLengthFlags(configSource);
        var collations = configParser.ParseCollations(configSource);
        var jsonMappings = configParser.ParseJsonMappings(configSource);
        var splitTables = configParser.ParseSplitTables(configSource);
        var tables = configParser.ParseTableMappings(configSource);
        var views = configParser.ParseViewMappings(configSource);
        var sqlQueries = configParser.ParseSqlQueries(configSource);
        var fluentKeylessNames = configParser.ParseKeylessEntities(configSource).ToHashSet();
        var columnNames = configParser.ParseColumnNames(configSource);
        var columnTypes = configParser.ParseColumnTypes(configSource);
        var defaultValues = configParser.ParseDefaultValues(configSource);
        var defaultValueSqls = configParser.ParseDefaultValueSqls(configSource);
        var indexes = configParser.ParseIndexes(configSource);
        var indexAttributes = entityParser.ParseIndexAttributes(classSource);
        var ignoredProperties = configParser.ParseIgnoredProperties(configSource);
        var valueGeneration = configParser.ParseValueGeneration(configSource);
        var concurrencyTokens = configParser.ParseConcurrencyTokens(configSource);
        var shadowProperties = configParser.ParseShadowProperties(configSource);
        var ignoredEntityNames = configParser.ParseIgnoredEntities(configSource).ToHashSet();
        var fluentRelationships = configParser.ParseRelationships(configSource, entityResult.Value);
        var annotationRelationships = entityParser.ParseRelationships(classSource, entityResult.Value);
        var unrecognizedCalls = configParser.ParseUnrecognizedCalls(configSource);

        diagnostics.AddRange(maxLengths.Diagnostics);
        diagnostics.AddRange(precisions.Diagnostics);
        diagnostics.AddRange(isRequired.Diagnostics);
        diagnostics.AddRange(keys.Diagnostics);
        diagnostics.AddRange(alternateKeys.Diagnostics);
        diagnostics.AddRange(queryFilters.Diagnostics);
        diagnostics.AddRange(comments.Entities.Diagnostics);
        diagnostics.AddRange(comments.Properties.Diagnostics);
        diagnostics.AddRange(unicodeFlags.Diagnostics);
        diagnostics.AddRange(fixedLengthFlags.Diagnostics);
        diagnostics.AddRange(collations.Diagnostics);
        diagnostics.AddRange(jsonMappings.Diagnostics);
        diagnostics.AddRange(splitTables.Diagnostics);
        diagnostics.AddRange(tables.Diagnostics);
        diagnostics.AddRange(views.Diagnostics);
        diagnostics.AddRange(sqlQueries.Diagnostics);
        diagnostics.AddRange(columnNames.Diagnostics);
        diagnostics.AddRange(columnTypes.Diagnostics);
        diagnostics.AddRange(defaultValues.Diagnostics);
        diagnostics.AddRange(defaultValueSqls.Diagnostics);
        diagnostics.AddRange(indexes.Diagnostics);
        diagnostics.AddRange(indexAttributes.Diagnostics);
        diagnostics.AddRange(ignoredProperties.Diagnostics);
        diagnostics.AddRange(valueGeneration.Diagnostics);
        diagnostics.AddRange(concurrencyTokens.Diagnostics);
        diagnostics.AddRange(shadowProperties.Diagnostics);
        diagnostics.AddRange(fluentRelationships.Diagnostics);
        diagnostics.AddRange(annotationRelationships.Diagnostics);
        diagnostics.AddRange(unrecognizedCalls);

        var fluentIndexKeys = indexes.Value.Select(IndexDedupeKey).ToHashSet();
        var mergedIndexConfigs = indexAttributes.Value
            .Where(c => !fluentIndexKeys.Contains(IndexDedupeKey(c)))
            .Concat(indexes.Value)
            .ToList();

        IReadOnlyList<EntityModel> entities = entityResult.Value
            .Where(entity => !ignoredEntityNames.Contains(entity.Name))
            .Select(entity => ModelMerger.ApplyMaxLengths(entity, maxLengths.Value))
            .Select(entity => ModelMerger.ApplyPrecisions(entity, precisions.Value))
            .Select(entity => ModelMerger.ApplyIsRequired(entity, isRequired.Value))
            .Select(entity => ModelMerger.ApplyKeys(entity, keys.Value))
            .Select(entity => ModelMerger.ApplyAlternateKeys(entity, alternateKeys.Value))
            .Select(entity => ModelMerger.ApplyQueryFilters(entity, queryFilters.Value))
            .Select(entity => ModelMerger.ApplyEntityComments(entity, comments.Entities.Value))
            .Select(entity => ModelMerger.ApplyPropertyComments(entity, comments.Properties.Value))
            .Select(entity => ModelMerger.ApplyUnicodeFlags(entity, unicodeFlags.Value))
            .Select(entity => ModelMerger.ApplyFixedLengthFlags(entity, fixedLengthFlags.Value))
            .Select(entity => ModelMerger.ApplyCollations(entity, collations.Value))
            .Select(entity => ModelMerger.ApplyJsonMappings(entity, jsonMappings.Value))
            .Select(entity => ModelMerger.ApplySplitTables(entity, splitTables.Value))
            .Select(entity => ModelMerger.ApplyTableMapping(entity, tables.Value.Tables))
            .Select(entity => ModelMerger.ApplyTemporal(entity, tables.Value.Temporal))
            .Select(entity => ModelMerger.ApplyViewMapping(entity, views.Value))
            .Select(entity => ModelMerger.ApplySqlQuery(entity, sqlQueries.Value))
            .Select(entity => entity.IsKeyless || fluentKeylessNames.Contains(entity.Name)
                ? entity with { IsKeyless = true }
                : entity)
            .Select(entity => ModelMerger.ApplyColumnNames(entity, columnNames.Value))
            .Select(entity => ModelMerger.ApplyColumnTypes(entity, columnTypes.Value))
            .Select(entity => ModelMerger.ApplyDefaultValues(entity, defaultValues.Value))
            .Select(entity => ModelMerger.ApplyDefaultValueSqls(entity, defaultValueSqls.Value))
            .Select(entity => ModelMerger.ApplyIndexes(entity, mergedIndexConfigs))
            .Select(entity => ModelMerger.ApplyValueGeneration(entity, valueGeneration.Value))
            .Select(entity => ModelMerger.ApplyConcurrencyTokens(entity, concurrencyTokens.Value))
            .Select(entity => ModelMerger.ApplyIgnoredProperties(entity, ignoredProperties.Value))
            .Select(entity => ModelMerger.ApplyShadowProperties(entity, shadowProperties.Value))
            .Select(ConventionInference.InferKey)
            .ToList();

        var inheritanceFold = InheritanceInference.Fold(entities);
        entities = inheritanceFold.Entities;

        var fluentRelationshipKeys = fluentRelationships.Value
            .Select(RelationshipDedupeKey)
            .ToHashSet();

        var mergedRelationshipConfigs = fluentRelationships.Value
            .Concat(annotationRelationships.Value.Where(r => !fluentRelationshipKeys.Contains(RelationshipDedupeKey(r))))
            .Where(r => !ignoredEntityNames.Contains(r.PrincipalEntity) && !ignoredEntityNames.Contains(r.DependentEntity))
            .ToList();

        var relationshipModels = ModelMerger.ApplyRelationships(mergedRelationshipConfigs);

        var explicitRelationshipKeys = relationshipModels
            .Select(RelationshipModelDedupeKey)
            .ToHashSet();

        var explicitNavigationKeys = relationshipModels
            .Where(r => r.DependentNavigation is not null)
            .Select(r => (r.DependentEntity, r.DependentNavigation))
            .ToHashSet();

        var inferredRelationships = ConventionInference.InferRelationships(entities)
            .Where(r => !explicitRelationshipKeys.Contains(RelationshipModelDedupeKey(r))
                && !explicitNavigationKeys.Contains((r.DependentEntity, r.DependentNavigation)))
            .ToList();

        var allRelationships = relationshipModels
            .Concat(inferredRelationships)
            .Concat(inheritanceFold.Relationships)
            .ToList();

        return new DiagramModelResult(entities, allRelationships, diagnostics);
    }

    private static (string PrincipalEntity, string DependentEntity, string ForeignKeyProperties) RelationshipDedupeKey(
        RelationshipConfig config)
    {
        return (config.PrincipalEntity, config.DependentEntity, string.Join(",", config.ForeignKeyProperties));
    }

    private static (string EntityName, string PropertyNames) IndexDedupeKey(IndexConfig config)
    {
        return (config.EntityName, string.Join(",", config.PropertyNames));
    }

    private static (string DependentEntity, string ForeignKeyProperties) RelationshipModelDedupeKey(RelationshipModel relationship)
    {
        return (relationship.DependentEntity, string.Join(",", relationship.ForeignKeyProperties));
    }
}
