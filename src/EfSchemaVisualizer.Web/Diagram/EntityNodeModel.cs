using Blazor.Diagrams.Core.Geometry;
using Blazor.Diagrams.Core.Models;
using EfSchemaVisualizer.Core.Model;

namespace EfSchemaVisualizer.Web.Diagram;

public sealed class EntityNodeModel : NodeModel
{
    public EntityNodeModel(EntityModel entity, Guid entityId, Point position) : base(position)
    {
        Entity = entity;
        EntityId = entityId;
        Title = entity.Name;
        AddPort(PortAlignment.Left);
        AddPort(PortAlignment.Right);
    }

    public EntityModel Entity { get; internal set; }
    public Guid EntityId { get; }
}
