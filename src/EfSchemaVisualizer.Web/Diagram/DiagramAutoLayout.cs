using Blazor.Diagrams;
using EfSchemaVisualizer.Core.Model;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("EfSchemaVisualizer.Web.Tests")]

namespace EfSchemaVisualizer.Web.Diagram;

/// <summary>
/// Arranges entities into left-to-right layers by relationship depth (principal entities
/// before their dependents), rather than the arrival-order grid <see cref="DiagramSync"/>
/// uses for brand-new nodes. Triggered on demand from the "Auto-layout" toolbar button;
/// existing node positions are otherwise left alone so manual dragging isn't fought.
/// </summary>
public static class DiagramAutoLayout
{
    private const double DefaultWidth = 260;
    private const double DefaultHeight = 160;
    private const double LayerSpacing = 80;
    private const double RowSpacing = 40;

    public static void Apply(BlazorDiagram diagram, DiagramModelResult result)
    {
        var nodesByName = new Dictionary<string, EntityNodeModel>();
        foreach (var node in diagram.Nodes.OfType<EntityNodeModel>())
        {
            nodesByName.TryAdd(node.Entity.Name, node);
        }

        if (nodesByName.Count == 0)
        {
            return;
        }

        var layers = ComputeLayers(result.Entities, result.Relationships);
        var orderedLayers = OrderWithinLayers(result.Entities, result.Relationships, layers);

        var layerWidths = new Dictionary<int, double>();
        foreach (var (name, layer) in layers)
        {
            var width = DefaultWidth;
            if (nodesByName.TryGetValue(name, out var node) && node.Size is { Width: > 0 } size)
            {
                width = size.Width;
            }

            layerWidths[layer] = Math.Max(layerWidths.GetValueOrDefault(layer), width);
        }

        var layerX = new Dictionary<int, double>();
        var x = 0.0;
        foreach (var layer in layerWidths.Keys.OrderBy(l => l))
        {
            layerX[layer] = x;
            x += layerWidths[layer] + LayerSpacing;
        }

        foreach (var (layer, namesInLayer) in orderedLayers)
        {
            var y = 0.0;
            foreach (var name in namesInLayer)
            {
                if (!nodesByName.TryGetValue(name, out var node))
                {
                    continue;
                }

                var height = node.Size is { Height: > 0 } size ? size.Height : DefaultHeight;
                node.SetPosition(layerX[layer], y);
                y += height + RowSpacing;
            }
        }
    }

    /// <summary>
    /// Assigns each entity a layer via longest-path-from-root layering over the
    /// dependent-to-principal edges (principals land in earlier layers than their
    /// dependents). Cycles (self-references, mutual FKs) are broken by ignoring any
    /// edge back to an entity still on the current DFS stack, so every entity still
    /// gets a finite layer.
    /// </summary>
    internal static Dictionary<string, int> ComputeLayers(
        IReadOnlyList<EntityModel> entities,
        IReadOnlyList<RelationshipModel> relationships)
    {
        var principalsOf = new Dictionary<string, List<string>>();
        foreach (var entity in entities)
        {
            principalsOf.TryAdd(entity.Name, new List<string>());
        }

        foreach (var relationship in relationships)
        {
            if (relationship.DependentEntity == relationship.PrincipalEntity)
            {
                continue;
            }

            if (principalsOf.TryGetValue(relationship.DependentEntity, out var principals) &&
                principalsOf.ContainsKey(relationship.PrincipalEntity))
            {
                principals.Add(relationship.PrincipalEntity);
            }
        }

        var layers = new Dictionary<string, int>();
        var onStack = new HashSet<string>();

        void Visit(string name)
        {
            if (layers.ContainsKey(name))
            {
                return;
            }

            onStack.Add(name);

            var layer = 0;
            foreach (var principal in principalsOf[name])
            {
                if (onStack.Contains(principal))
                {
                    continue;
                }

                Visit(principal);
                layer = Math.Max(layer, layers[principal] + 1);
            }

            layers[name] = layer;
            onStack.Remove(name);
        }

        foreach (var entity in entities)
        {
            Visit(entity.Name);
        }

        return layers;
    }

    /// <summary>
    /// Orders entities within each layer by the average position of their principals in
    /// the layer above (a single barycenter pass) to keep visually-related entities near
    /// each other and cut down on crossing relationship lines. Falls back to declaration
    /// order for entities with no principal in the layer above.
    /// </summary>
    private static List<(int Layer, List<string> Names)> OrderWithinLayers(
        IReadOnlyList<EntityModel> entities,
        IReadOnlyList<RelationshipModel> relationships,
        Dictionary<string, int> layers)
    {
        var declarationIndex = new Dictionary<string, int>();
        var groups = new Dictionary<int, List<string>>();
        for (var i = 0; i < entities.Count; i++)
        {
            var name = entities[i].Name;
            if (!declarationIndex.TryAdd(name, i))
            {
                continue;
            }

            var layer = layers[name];
            if (!groups.TryGetValue(layer, out var list))
            {
                list = new List<string>();
                groups[layer] = list;
            }

            list.Add(name);
        }

        var principalsOf = new Dictionary<string, List<string>>();
        foreach (var name in declarationIndex.Keys)
        {
            principalsOf[name] = new List<string>();
        }

        foreach (var relationship in relationships)
        {
            if (relationship.DependentEntity != relationship.PrincipalEntity &&
                principalsOf.TryGetValue(relationship.DependentEntity, out var principals) &&
                principalsOf.ContainsKey(relationship.PrincipalEntity))
            {
                principals.Add(relationship.PrincipalEntity);
            }
        }

        var orderedLayers = groups.Keys.OrderBy(l => l).ToList();
        foreach (var layer in orderedLayers)
        {
            if (layer == 0)
            {
                continue;
            }

            var positionAbove = groups[layer - 1]
                .Select((name, index) => (name, index))
                .ToDictionary(t => t.name, t => t.index);

            groups[layer] = groups[layer]
                .OrderBy(name =>
                {
                    var aboveIndices = principalsOf[name]
                        .Where(positionAbove.ContainsKey)
                        .Select(p => positionAbove[p])
                        .ToList();
                    return aboveIndices.Count == 0 ? double.MaxValue : aboveIndices.Average();
                })
                .ThenBy(name => declarationIndex[name])
                .ToList();
        }

        return orderedLayers.Select(layer => (layer, groups[layer])).ToList();
    }
}
