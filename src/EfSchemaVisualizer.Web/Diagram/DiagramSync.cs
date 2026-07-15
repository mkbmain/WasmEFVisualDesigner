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
        // Reuse existing EntityNodeModel instances for entities that persist across this rebuild
        // (matched by their stable EntityId) instead of always creating fresh objects. Blazor.Diagrams
        // keys its per-node component by the NodeModel instance itself, so replacing the instance on
        // every single edit destroys and recreates the EntityNode component for every card, wiping any
        // in-progress local UI state (expanded "more options" panels, in-place renames) each time -
        // even for entities unrelated to the edit that just happened.
        var previousNodesById = diagram.Nodes
            .OfType<EntityNodeModel>()
            .ToDictionary(node => node.EntityId);

        diagram.Links.Clear();

        var nodesByEntityName = new Dictionary<string, EntityNodeModel>();
        var keptEntityIds = new HashSet<Guid>();
        var newEntityIndex = previousNodesById.Count;

        foreach (var entity in result.Entities)
        {
            var entityId = entityIds[entity.Name];
            keptEntityIds.Add(entityId);

            if (previousNodesById.TryGetValue(entityId, out var existingNode))
            {
                existingNode.Entity = entity;
                existingNode.Title = entity.Name;
                existingNode.Refresh();
                nodesByEntityName[entity.Name] = existingNode;
                continue;
            }

            var position = new Point((newEntityIndex % Columns) * XSpacing, (newEntityIndex / Columns) * YSpacing);
            newEntityIndex++;

            var node = new EntityNodeModel(entity, entityId, position);
            diagram.Nodes.Add(node);
            nodesByEntityName[entity.Name] = node;
        }

        var staleNodes = previousNodesById.Values.Where(node => !keptEntityIds.Contains(node.EntityId)).ToList();
        foreach (var staleNode in staleNodes)
        {
            diagram.Nodes.Remove(staleNode);
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
