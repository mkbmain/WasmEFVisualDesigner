using System.Linq;
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
        var tables = configParser.ParseTableMappings(configSource);
        var views = configParser.ParseViewMappings(configSource);
        var sqlQueries = configParser.ParseSqlQueries(configSource);
        var fluentKeylessNames = configParser.ParseKeylessEntities(configSource).ToHashSet();
        var columnNames = configParser.ParseColumnNames(configSource);
        var columnTypes = configParser.ParseColumnTypes(configSource);
        var defaultValues = configParser.ParseDefaultValues(configSource);
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
        diagnostics.AddRange(tables.Diagnostics);
        diagnostics.AddRange(views.Diagnostics);
        diagnostics.AddRange(sqlQueries.Diagnostics);
        diagnostics.AddRange(columnNames.Diagnostics);
        diagnostics.AddRange(columnTypes.Diagnostics);
        diagnostics.AddRange(defaultValues.Diagnostics);
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

        var entities = entityResult.Value
            .Where(entity => !ignoredEntityNames.Contains(entity.Name))
            .Select(entity => ModelMerger.ApplyMaxLengths(entity, maxLengths.Value))
            .Select(entity => ModelMerger.ApplyPrecisions(entity, precisions.Value))
            .Select(entity => ModelMerger.ApplyIsRequired(entity, isRequired.Value))
            .Select(entity => ModelMerger.ApplyKeys(entity, keys.Value))
            .Select(entity => ModelMerger.ApplyAlternateKeys(entity, alternateKeys.Value))
            .Select(entity => ModelMerger.ApplyTableMapping(entity, tables.Value))
            .Select(entity => ModelMerger.ApplyViewMapping(entity, views.Value))
            .Select(entity => ModelMerger.ApplySqlQuery(entity, sqlQueries.Value))
            .Select(entity => entity.IsKeyless || fluentKeylessNames.Contains(entity.Name)
                ? entity with { IsKeyless = true }
                : entity)
            .Select(entity => ModelMerger.ApplyColumnNames(entity, columnNames.Value))
            .Select(entity => ModelMerger.ApplyColumnTypes(entity, columnTypes.Value))
            .Select(entity => ModelMerger.ApplyDefaultValues(entity, defaultValues.Value))
            .Select(entity => ModelMerger.ApplyIndexes(entity, mergedIndexConfigs))
            .Select(entity => ModelMerger.ApplyValueGeneration(entity, valueGeneration.Value))
            .Select(entity => ModelMerger.ApplyConcurrencyTokens(entity, concurrencyTokens.Value))
            .Select(entity => ModelMerger.ApplyIgnoredProperties(entity, ignoredProperties.Value))
            .Select(entity => ModelMerger.ApplyShadowProperties(entity, shadowProperties.Value))
            .ToList();

        var fluentRelationshipKeys = fluentRelationships.Value
            .Select(RelationshipDedupeKey)
            .ToHashSet();

        var mergedRelationshipConfigs = fluentRelationships.Value
            .Concat(annotationRelationships.Value.Where(r => !fluentRelationshipKeys.Contains(RelationshipDedupeKey(r))))
            .Where(r => !ignoredEntityNames.Contains(r.PrincipalEntity) && !ignoredEntityNames.Contains(r.DependentEntity))
            .ToList();

        var relationshipModels = ModelMerger.ApplyRelationships(mergedRelationshipConfigs);

        return new DiagramModelResult(entities, relationshipModels, diagnostics);
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
}
