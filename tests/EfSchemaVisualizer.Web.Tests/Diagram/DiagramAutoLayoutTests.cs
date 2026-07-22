using Blazor.Diagrams;
using Blazor.Diagrams.Core.Geometry;
using EfSchemaVisualizer.Core.Model;
using EfSchemaVisualizer.Web.Diagram;

namespace EfSchemaVisualizer.Web.Tests.Diagram;

public class DiagramAutoLayoutTests
{
    private static EntityModel Entity(string name) => new(name, Array.Empty<PropertyModel>());

    private static RelationshipModel Relationship(string principal, string dependent) =>
        new(principal, dependent, RelationshipKind.OneToMany, PrincipalNavigation: null, DependentNavigation: null);

    [Fact]
    public void ComputeLayers_ChainOfThree_AssignsIncreasingLayers()
    {
        var entities = new[] { Entity("Blog"), Entity("Post"), Entity("Comment") };
        var relationships = new[] { Relationship("Blog", "Post"), Relationship("Post", "Comment") };

        var layers = DiagramAutoLayout.ComputeLayers(entities, relationships);

        Assert.Equal(0, layers["Blog"]);
        Assert.Equal(1, layers["Post"]);
        Assert.Equal(2, layers["Comment"]);
    }

    [Fact]
    public void ComputeLayers_IsolatedEntity_IsLayerZero()
    {
        var entities = new[] { Entity("Blog"), Entity("Post"), Entity("Standalone") };
        var relationships = new[] { Relationship("Blog", "Post") };

        var layers = DiagramAutoLayout.ComputeLayers(entities, relationships);

        Assert.Equal(0, layers["Standalone"]);
    }

    [Fact]
    public void ComputeLayers_SelfReferencingRelationship_DoesNotAffectItsOwnLayer()
    {
        var entities = new[] { Entity("Employee") };
        var relationships = new[] { Relationship("Employee", "Employee") };

        var layers = DiagramAutoLayout.ComputeLayers(entities, relationships);

        Assert.Equal(0, layers["Employee"]);
    }

    [Fact]
    public void ComputeLayers_MutualCycle_BreaksCycleAndAssignsFiniteLayers()
    {
        // A depends on B and B depends on A: a genuine cycle with no acyclic root.
        var entities = new[] { Entity("A"), Entity("B") };
        var relationships = new[] { Relationship("B", "A"), Relationship("A", "B") };

        var layers = DiagramAutoLayout.ComputeLayers(entities, relationships);

        Assert.True(layers["A"] >= 0);
        Assert.True(layers["B"] >= 0);
    }

    [Fact]
    public void ComputeLayers_RelationshipToUnknownEntity_IsIgnored()
    {
        var entities = new[] { Entity("Post") };
        var relationships = new[] { Relationship("Blog", "Post") };

        var layers = DiagramAutoLayout.ComputeLayers(entities, relationships);

        Assert.Equal(0, layers["Post"]);
    }

    private static BlazorDiagram DiagramWithNodes(params (EntityModel Entity, Size? Size)[] nodes)
    {
        var diagram = new BlazorDiagram();
        foreach (var (entity, size) in nodes)
        {
            var node = new EntityNodeModel(entity, Guid.NewGuid(), new Point(0, 0));
            if (size is not null)
            {
                node.Size = size;
            }

            diagram.Nodes.Add(node);
        }

        return diagram;
    }

    [Fact]
    public void Apply_PrincipalsLandInEarlierLayerThanDependents()
    {
        var blog = Entity("Blog");
        var post = Entity("Post");
        var diagram = DiagramWithNodes((blog, new Size(260, 120)), (post, new Size(260, 160)));
        var result = new DiagramModelResult(
            new[] { blog, post },
            new[] { Relationship("Blog", "Post") },
            Array.Empty<Core.Parsing.Diagnostic>());

        DiagramAutoLayout.Apply(diagram, result);

        var blogNode = diagram.Nodes.OfType<EntityNodeModel>().Single(n => n.Entity.Name == "Blog");
        var postNode = diagram.Nodes.OfType<EntityNodeModel>().Single(n => n.Entity.Name == "Post");

        Assert.True(blogNode.Position.X < postNode.Position.X);
    }

    [Fact]
    public void Apply_SameLayerEntities_AreStackedVerticallyWithoutOverlap()
    {
        var blog = Entity("Blog");
        var author = Entity("Author");
        var diagram = DiagramWithNodes((blog, new Size(260, 120)), (author, new Size(260, 100)));
        var result = new DiagramModelResult(
            new[] { blog, author },
            Array.Empty<RelationshipModel>(),
            Array.Empty<Core.Parsing.Diagnostic>());

        DiagramAutoLayout.Apply(diagram, result);

        var nodes = diagram.Nodes.OfType<EntityNodeModel>().ToList();
        Assert.Equal(nodes[0].Position.X, nodes[1].Position.X);
        Assert.NotEqual(nodes[0].Position.Y, nodes[1].Position.Y);
    }

    [Fact]
    public void Apply_NodeWithNoMeasuredSizeYet_FallsBackToDefaultSizeWithoutThrowing()
    {
        var blog = Entity("Blog");
        var post = Entity("Post");
        var diagram = DiagramWithNodes((blog, null), (post, null));
        var result = new DiagramModelResult(
            new[] { blog, post },
            new[] { Relationship("Blog", "Post") },
            Array.Empty<Core.Parsing.Diagnostic>());

        DiagramAutoLayout.Apply(diagram, result);

        var blogNode = diagram.Nodes.OfType<EntityNodeModel>().Single(n => n.Entity.Name == "Blog");
        var postNode = diagram.Nodes.OfType<EntityNodeModel>().Single(n => n.Entity.Name == "Post");
        Assert.True(blogNode.Position.X < postNode.Position.X);
    }

    [Fact]
    public void Apply_NoNodesInDiagram_DoesNothing()
    {
        var diagram = new BlazorDiagram();
        var result = new DiagramModelResult(
            Array.Empty<EntityModel>(), Array.Empty<RelationshipModel>(), Array.Empty<Core.Parsing.Diagnostic>());

        DiagramAutoLayout.Apply(diagram, result);

        Assert.Empty(diagram.Nodes);
    }
}
