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
        var tables = configParser.ParseTableMappings(configSource);
        var columnNames = configParser.ParseColumnNames(configSource);
        var columnTypes = configParser.ParseColumnTypes(configSource);
        var defaultValues = configParser.ParseDefaultValues(configSource);
        var indexes = configParser.ParseIndexes(configSource);
        var relationships = configParser.ParseRelationships(configSource, entityResult.Value);

        diagnostics.AddRange(maxLengths.Diagnostics);
        diagnostics.AddRange(precisions.Diagnostics);
        diagnostics.AddRange(isRequired.Diagnostics);
        diagnostics.AddRange(keys.Diagnostics);
        diagnostics.AddRange(tables.Diagnostics);
        diagnostics.AddRange(columnNames.Diagnostics);
        diagnostics.AddRange(columnTypes.Diagnostics);
        diagnostics.AddRange(defaultValues.Diagnostics);
        diagnostics.AddRange(indexes.Diagnostics);
        diagnostics.AddRange(relationships.Diagnostics);

        var entities = entityResult.Value
            .Select(entity => ModelMerger.ApplyMaxLengths(entity, maxLengths.Value))
            .Select(entity => ModelMerger.ApplyPrecisions(entity, precisions.Value))
            .Select(entity => ModelMerger.ApplyIsRequired(entity, isRequired.Value))
            .Select(entity => ModelMerger.ApplyKeys(entity, keys.Value))
            .Select(entity => ModelMerger.ApplyTableMapping(entity, tables.Value))
            .Select(entity => ModelMerger.ApplyColumnNames(entity, columnNames.Value))
            .Select(entity => ModelMerger.ApplyColumnTypes(entity, columnTypes.Value))
            .Select(entity => ModelMerger.ApplyDefaultValues(entity, defaultValues.Value))
            .Select(entity => ModelMerger.ApplyIndexes(entity, indexes.Value))
            .ToList();

        var relationshipModels = ModelMerger.ApplyRelationships(relationships.Value);

        return new DiagramModelResult(entities, relationshipModels, diagnostics);
    }
}
