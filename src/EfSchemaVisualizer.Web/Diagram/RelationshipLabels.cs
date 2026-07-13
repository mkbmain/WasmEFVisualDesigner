using EfSchemaVisualizer.Core.Model;

namespace EfSchemaVisualizer.Web.Diagram;

public static class RelationshipLabels
{
    public static string For(RelationshipKind kind) => kind switch
    {
        RelationshipKind.OneToOne => "1—1",
        RelationshipKind.OneToMany => "1—*",
        RelationshipKind.ManyToMany => "*—*",
        _ => "?",
    };
}
