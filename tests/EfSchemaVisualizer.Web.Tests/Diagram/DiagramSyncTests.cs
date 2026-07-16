using Blazor.Diagrams;
using Blazor.Diagrams.Core.Models;
using EfSchemaVisualizer.Core.Model;
using EfSchemaVisualizer.Web.Diagram;

namespace EfSchemaVisualizer.Web.Tests.Diagram;

public class DiagramSyncTests
{
    private static BlazorDiagram NewDiagram() => new();

    private static EntityModel Entity(string name, params string[] propertyNames) =>
        new(name, propertyNames.Select(p => new PropertyModel(p, "string", IsNullable: true, MaxLength: null)).ToList());

    [Fact]
    public void Rebuild_NewEntities_CreatesOneNodePerEntity()
    {
        var diagram = NewDiagram();
        var result = new DiagramModelResult(
            new[] { Entity("Person"), Entity("Address") },
            Array.Empty<RelationshipModel>(),
            Array.Empty<Core.Parsing.Diagnostic>());
        var entityIds = new Dictionary<string, Guid> { ["Person"] = Guid.NewGuid(), ["Address"] = Guid.NewGuid() };

        DiagramSync.Rebuild(diagram, result, entityIds);

        var nodes = diagram.Nodes.OfType<EntityNodeModel>().ToList();
        Assert.Equal(2, nodes.Count);
        Assert.Contains(nodes, n => n.Entity.Name == "Person");
        Assert.Contains(nodes, n => n.Entity.Name == "Address");
    }

    [Fact]
    public void Rebuild_SameEntityIdAcrossRebuilds_ReusesExistingNodeInstance()
    {
        var diagram = NewDiagram();
        var entityId = Guid.NewGuid();
        var entityIds = new Dictionary<string, Guid> { ["Person"] = entityId };

        DiagramSync.Rebuild(diagram, new DiagramModelResult(
            new[] { Entity("Person", "Id", "Name") },
            Array.Empty<RelationshipModel>(),
            Array.Empty<Core.Parsing.Diagnostic>()), entityIds);

        var originalNode = diagram.Nodes.OfType<EntityNodeModel>().Single();

        // Simulate an unrelated edit: same entity, same id, different property list.
        DiagramSync.Rebuild(diagram, new DiagramModelResult(
            new[] { Entity("Person", "Id", "Name", "Email") },
            Array.Empty<RelationshipModel>(),
            Array.Empty<Core.Parsing.Diagnostic>()), entityIds);

        var rebuiltNode = diagram.Nodes.OfType<EntityNodeModel>().Single();

        Assert.Same(originalNode, rebuiltNode);
        Assert.Equal(3, rebuiltNode.Entity.Properties.Count);
    }

    [Fact]
    public void Rebuild_EntityRemoved_RemovesItsNodeButKeepsOthers()
    {
        var diagram = NewDiagram();
        var entityIds = new Dictionary<string, Guid> { ["Person"] = Guid.NewGuid(), ["Address"] = Guid.NewGuid() };

        DiagramSync.Rebuild(diagram, new DiagramModelResult(
            new[] { Entity("Person"), Entity("Address") },
            Array.Empty<RelationshipModel>(),
            Array.Empty<Core.Parsing.Diagnostic>()), entityIds);

        var addressNode = diagram.Nodes.OfType<EntityNodeModel>().Single(n => n.Entity.Name == "Address");

        DiagramSync.Rebuild(diagram, new DiagramModelResult(
            new[] { Entity("Person") },
            Array.Empty<RelationshipModel>(),
            Array.Empty<Core.Parsing.Diagnostic>()), entityIds);

        var nodes = diagram.Nodes.OfType<EntityNodeModel>().ToList();
        Assert.Single(nodes);
        Assert.Equal("Person", nodes[0].Entity.Name);
        Assert.DoesNotContain(addressNode, diagram.Nodes);
    }

    [Fact]
    public void Rebuild_EntityAddedAlongsideExisting_KeepsExistingNodeInstanceAndAddsNewOne()
    {
        var diagram = NewDiagram();
        var personId = Guid.NewGuid();
        var entityIds = new Dictionary<string, Guid> { ["Person"] = personId };

        DiagramSync.Rebuild(diagram, new DiagramModelResult(
            new[] { Entity("Person") },
            Array.Empty<RelationshipModel>(),
            Array.Empty<Core.Parsing.Diagnostic>()), entityIds);

        var originalPersonNode = diagram.Nodes.OfType<EntityNodeModel>().Single();

        entityIds["Address"] = Guid.NewGuid();

        DiagramSync.Rebuild(diagram, new DiagramModelResult(
            new[] { Entity("Person"), Entity("Address") },
            Array.Empty<RelationshipModel>(),
            Array.Empty<Core.Parsing.Diagnostic>()), entityIds);

        var nodes = diagram.Nodes.OfType<EntityNodeModel>().ToList();
        Assert.Equal(2, nodes.Count);
        Assert.Same(originalPersonNode, nodes.Single(n => n.Entity.Name == "Person"));
    }

    [Fact]
    public void Rebuild_ClearsAndRecreatesLinksEveryTime()
    {
        var diagram = NewDiagram();
        var entityIds = new Dictionary<string, Guid> { ["Customer"] = Guid.NewGuid(), ["Order"] = Guid.NewGuid() };
        var relationship = new RelationshipModel(
            "Customer", "Order", RelationshipKind.OneToMany,
            PrincipalNavigation: "Orders", DependentNavigation: "Customer",
            ForeignKeyProperties: new[] { "CustomerId" });

        DiagramSync.Rebuild(diagram, new DiagramModelResult(
            new[] { Entity("Customer"), Entity("Order") },
            new[] { relationship },
            Array.Empty<Core.Parsing.Diagnostic>()), entityIds);

        Assert.Single(diagram.Links);

        // Rebuild with the relationship gone: the stale link must not survive.
        DiagramSync.Rebuild(diagram, new DiagramModelResult(
            new[] { Entity("Customer"), Entity("Order") },
            Array.Empty<RelationshipModel>(),
            Array.Empty<Core.Parsing.Diagnostic>()), entityIds);

        Assert.Empty(diagram.Links);
    }

    [Fact]
    public void Rebuild_RelationshipReferencingUnknownEntity_IsSkippedWithoutThrowing()
    {
        var diagram = NewDiagram();
        var entityIds = new Dictionary<string, Guid> { ["Order"] = Guid.NewGuid() };
        var relationship = new RelationshipModel(
            "Customer", "Order", RelationshipKind.OneToMany,
            PrincipalNavigation: null, DependentNavigation: null);

        DiagramSync.Rebuild(diagram, new DiagramModelResult(
            new[] { Entity("Order") },
            new[] { relationship },
            Array.Empty<Core.Parsing.Diagnostic>()), entityIds);

        Assert.Empty(diagram.Links);
    }

    [Fact]
    public void Rebuild_NewNode_IsPlacedOnAGridAvoidingExistingPositions()
    {
        var diagram = NewDiagram();
        var entityIds = new Dictionary<string, Guid>();
        var entities = new List<EntityModel>();

        for (var i = 0; i < 5; i++)
        {
            var name = $"Entity{i}";
            entityIds[name] = Guid.NewGuid();
            entities.Add(Entity(name));

            DiagramSync.Rebuild(diagram, new DiagramModelResult(
                entities, Array.Empty<RelationshipModel>(), Array.Empty<Core.Parsing.Diagnostic>()), entityIds);
        }

        var nodes = diagram.Nodes.OfType<EntityNodeModel>().ToList();
        var distinctPositions = nodes.Select(n => (n.Position.X, n.Position.Y)).Distinct().Count();

        Assert.Equal(5, nodes.Count);
        Assert.Equal(5, distinctPositions);
    }
}
