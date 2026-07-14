using Blazor.Diagrams.Core.Models;
using Blazor.Diagrams.Core.Models.Base;
using EfSchemaVisualizer.Core.Model;

namespace EfSchemaVisualizer.Web.Diagram;

public sealed class RelationshipLinkLabelModel : LinkLabelModel
{
    public RelationshipLinkLabelModel(BaseLinkModel parent, RelationshipModel relationship)
        : base(parent, RelationshipLabels.For(relationship.Kind))
    {
        Relationship = relationship;
    }

    public RelationshipModel Relationship { get; }
}
