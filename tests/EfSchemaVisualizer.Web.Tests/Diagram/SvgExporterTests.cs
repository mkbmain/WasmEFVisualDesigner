using Blazor.Diagrams.Core.Geometry;
using EfSchemaVisualizer.Core.Model;
using EfSchemaVisualizer.Web.Diagram;

namespace EfSchemaVisualizer.Web.Tests.Diagram;

public class SvgExporterTests
{
    private static PropertyModel Property(string name, string clrType, bool isNullable = false) =>
        new(name, clrType, isNullable, MaxLength: null);

    private static EntityModel Entity(string name, params PropertyModel[] properties) =>
        new(name, properties);

    [Fact]
    public void Export_EmitsValidSvgRoot()
    {
        var result = new DiagramModelResult(
            Array.Empty<EntityModel>(), Array.Empty<RelationshipModel>(), Array.Empty<Core.Parsing.Diagnostic>());
        var layout = new Dictionary<string, (Point Position, Size Size)>();

        var svg = SvgExporter.Export(result, layout);

        Assert.StartsWith("<svg", svg);
        Assert.Contains("</svg>", svg);
    }

    [Fact]
    public void Export_EntityWithLayout_EmitsRectAndPropertyText()
    {
        var blog = Entity("Blog", Property("Id", "int"), Property("Title", "string")) with
        {
            KeyPropertyNames = new[] { "Id" },
        };
        var result = new DiagramModelResult(new[] { blog }, Array.Empty<RelationshipModel>(), Array.Empty<Core.Parsing.Diagnostic>());
        var layout = new Dictionary<string, (Point Position, Size Size)>
        {
            ["Blog"] = (new Point(0, 0), new Size(260, 160)),
        };

        var svg = SvgExporter.Export(result, layout);

        Assert.Contains("<rect", svg);
        Assert.Contains(">Blog<", svg);
        Assert.Contains("PK Id : int", svg);
        Assert.Contains("Title : string", svg);
    }

    [Fact]
    public void Export_NullableProperty_HasQuestionMarkSuffix()
    {
        var entity = Entity("Widget", Property("Note", "string", isNullable: true));
        var result = new DiagramModelResult(new[] { entity }, Array.Empty<RelationshipModel>(), Array.Empty<Core.Parsing.Diagnostic>());
        var layout = new Dictionary<string, (Point Position, Size Size)>
        {
            ["Widget"] = (new Point(0, 0), new Size(260, 160)),
        };

        var svg = SvgExporter.Export(result, layout);

        Assert.Contains("Note : string?", svg);
    }

    [Fact]
    public void Export_ForeignKeyProperty_IsMarkedFk()
    {
        var post = Entity("Post", Property("Id", "int"), Property("BlogId", "int")) with
        {
            KeyPropertyNames = new[] { "Id" },
        };
        var relationship = new RelationshipModel(
            "Blog", "Post", RelationshipKind.OneToMany, PrincipalNavigation: null, DependentNavigation: null,
            ForeignKeyProperties: new[] { "BlogId" });
        var result = new DiagramModelResult(new[] { Entity("Blog"), post }, new[] { relationship }, Array.Empty<Core.Parsing.Diagnostic>());
        var layout = new Dictionary<string, (Point Position, Size Size)>
        {
            ["Blog"] = (new Point(0, 0), new Size(260, 160)),
            ["Post"] = (new Point(400, 0), new Size(260, 160)),
        };

        var svg = SvgExporter.Export(result, layout);

        Assert.Contains("FK BlogId : int", svg);
    }

    [Fact]
    public void Export_Relationship_EmitsConnectingLineBetweenBothEntities()
    {
        var relationship = new RelationshipModel(
            "Blog", "Post", RelationshipKind.OneToMany, PrincipalNavigation: null, DependentNavigation: null);
        var result = new DiagramModelResult(
            new[] { Entity("Blog"), Entity("Post") }, new[] { relationship }, Array.Empty<Core.Parsing.Diagnostic>());
        var layout = new Dictionary<string, (Point Position, Size Size)>
        {
            ["Blog"] = (new Point(0, 0), new Size(260, 160)),
            ["Post"] = (new Point(400, 0), new Size(260, 160)),
        };

        var svg = SvgExporter.Export(result, layout);

        Assert.Contains("<line", svg);
    }

    [Fact]
    public void Export_RelationshipReferencingEntityWithNoLayoutEntry_IsSkippedWithoutThrowing()
    {
        var relationship = new RelationshipModel(
            "Blog", "Post", RelationshipKind.OneToMany, PrincipalNavigation: null, DependentNavigation: null);
        var result = new DiagramModelResult(
            new[] { Entity("Blog"), Entity("Post") }, new[] { relationship }, Array.Empty<Core.Parsing.Diagnostic>());
        var layout = new Dictionary<string, (Point Position, Size Size)>
        {
            ["Blog"] = (new Point(0, 0), new Size(260, 160)),
        };

        var svg = SvgExporter.Export(result, layout);

        Assert.DoesNotContain("<line", svg);
    }
}
