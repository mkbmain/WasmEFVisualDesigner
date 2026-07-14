using Blazor.Diagrams;
using Blazor.Diagrams.Core.Geometry;
using Blazor.Diagrams.Core.Models;
using EfSchemaVisualizer.Core.Model;

namespace EfSchemaVisualizer.Web.Diagram;

public static class DiagramSync
{
    private const int Columns = 4;
    private const double XSpacing = 320;
    private const double YSpacing = 260;

    public static void Rebuild(BlazorDiagram diagram, DiagramModelResult result)
    {
        var previousPositions = diagram.Nodes
            .OfType<EntityNodeModel>()
            .Select(node => node.Position)
            .ToList();

        diagram.Nodes.Clear();
        diagram.Links.Clear();

        var nodesByEntityName = new Dictionary<string, EntityNodeModel>();

        for (var i = 0; i < result.Entities.Count; i++)
        {
            var entity = result.Entities[i];

            // Ordinal matching: safe while entity count/order can't change (Phase 1 is
            // rename/type-change only). Phase 2 (add/remove) will need identity-based
            // matching once the count and order can diverge from the previous render.
            var position = i < previousPositions.Count
                ? previousPositions[i]
                : new Point((i % Columns) * XSpacing, (i / Columns) * YSpacing);

            var node = new EntityNodeModel(entity, position);
            diagram.Nodes.Add(node);
            nodesByEntityName[entity.Name] = node;
        }

        foreach (var relationship in result.Relationships)
        {
            if (!nodesByEntityName.TryGetValue(relationship.PrincipalEntity, out var principalNode) ||
                !nodesByEntityName.TryGetValue(relationship.DependentEntity, out var dependentNode))
            {
                continue;
            }

            var link = new LinkModel(dependentNode, principalNode);
            link.AddLabel(RelationshipLabels.For(relationship.Kind));
            diagram.Links.Add(link);
        }
    }
}
