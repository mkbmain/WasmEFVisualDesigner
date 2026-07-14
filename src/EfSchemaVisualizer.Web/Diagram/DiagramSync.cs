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

    public static void Rebuild(BlazorDiagram diagram, DiagramModelResult result, IReadOnlyDictionary<string, Guid> entityIds)
    {
        var previousPositionsById = diagram.Nodes
            .OfType<EntityNodeModel>()
            .ToDictionary(node => node.EntityId, node => node.Position);

        diagram.Nodes.Clear();
        diagram.Links.Clear();

        var nodesByEntityName = new Dictionary<string, EntityNodeModel>();
        var newEntityIndex = previousPositionsById.Count;

        foreach (var entity in result.Entities)
        {
            var entityId = entityIds[entity.Name];

            Point position;
            if (previousPositionsById.TryGetValue(entityId, out var existingPosition))
            {
                position = existingPosition;
            }
            else
            {
                position = new Point((newEntityIndex % Columns) * XSpacing, (newEntityIndex / Columns) * YSpacing);
                newEntityIndex++;
            }

            var node = new EntityNodeModel(entity, entityId, position);
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
            link.Labels.Add(new RelationshipLinkLabelModel(link, relationship));
            diagram.Links.Add(link);
        }
    }
}
