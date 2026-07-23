using Blazor.Diagrams;
using EfSchemaVisualizer.Core.Archive;
using EfSchemaVisualizer.Core.Model;
using EfSchemaVisualizer.Web.Diagram;

namespace EfSchemaVisualizer.Web.Tests.Diagram;

public class DiagramLayoutTests
{
    private static BlazorDiagram NewDiagram() => new();

    private static EntityModel Entity(string name) => new(name, new List<PropertyModel>());

    [Fact]
    public void Capture_ReturnsCurrentPositionOfEveryEntityNode()
    {
        var diagram = NewDiagram();
        var entityIds = new Dictionary<string, Guid> { ["Person"] = Guid.NewGuid(), ["Address"] = Guid.NewGuid() };
        DiagramSync.Rebuild(diagram, new DiagramModelResult(
            new[] { Entity("Person"), Entity("Address") },
            Array.Empty<RelationshipModel>(),
            Array.Empty<Core.Parsing.Diagnostic>()), entityIds);

        var personNode = diagram.Nodes.OfType<EntityNodeModel>().Single(n => n.Entity.Name == "Person");
        personNode.SetPosition(123, 456);

        var layout = DiagramLayout.Capture(diagram);

        Assert.Equal(2, layout.Count);
        Assert.Equal(new EntityPosition(123, 456), layout["Person"]);
    }

    [Fact]
    public void Apply_MovesMatchingNodesToTheGivenPositions()
    {
        var diagram = NewDiagram();
        var entityIds = new Dictionary<string, Guid> { ["Person"] = Guid.NewGuid() };
        DiagramSync.Rebuild(diagram, new DiagramModelResult(
            new[] { Entity("Person") },
            Array.Empty<RelationshipModel>(),
            Array.Empty<Core.Parsing.Diagnostic>()), entityIds);

        var layout = new Dictionary<string, EntityPosition> { ["Person"] = new(200, 300) };

        DiagramLayout.Apply(diagram, layout);

        var node = diagram.Nodes.OfType<EntityNodeModel>().Single();
        Assert.Equal(200, node.Position.X);
        Assert.Equal(300, node.Position.Y);
    }

    [Fact]
    public void Apply_EntityWithNoStoredPosition_IsLeftWhereItWas()
    {
        var diagram = NewDiagram();
        var entityIds = new Dictionary<string, Guid> { ["Person"] = Guid.NewGuid() };
        DiagramSync.Rebuild(diagram, new DiagramModelResult(
            new[] { Entity("Person") },
            Array.Empty<RelationshipModel>(),
            Array.Empty<Core.Parsing.Diagnostic>()), entityIds);

        var node = diagram.Nodes.OfType<EntityNodeModel>().Single();
        var originalPosition = node.Position;

        DiagramLayout.Apply(diagram, new Dictionary<string, EntityPosition> { ["SomeoneElse"] = new(1, 1) });

        Assert.Equal(originalPosition.X, node.Position.X);
        Assert.Equal(originalPosition.Y, node.Position.Y);
    }
}
