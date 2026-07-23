using Blazor.Diagrams;
using EfSchemaVisualizer.Core.Archive;

namespace EfSchemaVisualizer.Web.Diagram;

/// <summary>
/// Captures/restores entity node positions independently of <see cref="DiagramSync"/>'s
/// model rebuild, so a layout can be round-tripped through localStorage or a zip sidecar
/// file across a full diagram re-render (which otherwise resets nodes to the arrival grid).
/// </summary>
public static class DiagramLayout
{
    public static Dictionary<string, EntityPosition> Capture(BlazorDiagram diagram) =>
        diagram.Nodes
            .OfType<EntityNodeModel>()
            .ToDictionary(node => node.Entity.Name, node => new EntityPosition(node.Position.X, node.Position.Y));

    public static void Apply(BlazorDiagram diagram, IReadOnlyDictionary<string, EntityPosition> layout)
    {
        foreach (var node in diagram.Nodes.OfType<EntityNodeModel>())
        {
            if (layout.TryGetValue(node.Entity.Name, out var position))
            {
                node.SetPosition(position.X, position.Y);
            }
        }
    }
}
