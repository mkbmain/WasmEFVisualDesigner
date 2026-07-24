using EfSchemaVisualizer.Core.Model;
using EfSchemaVisualizer.Web.Diagram;
using Xunit;

namespace EfSchemaVisualizer.Web.Tests.Diagram;

public class RelationshipLabelsTests
{
    [Fact]
    public void For_Inheritance_ReturnsDistinctLabel()
    {
        var label = RelationshipLabels.For(RelationshipKind.Inheritance);

        Assert.NotEqual("?", label);
        Assert.NotEqual(RelationshipLabels.For(RelationshipKind.OneToOne), label);
        Assert.NotEqual(RelationshipLabels.For(RelationshipKind.OneToMany), label);
        Assert.NotEqual(RelationshipLabels.For(RelationshipKind.ManyToMany), label);
    }
}
