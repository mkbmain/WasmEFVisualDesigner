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
    }

    public EntityModel Entity { get; }
    public Guid EntityId { get; }
}
