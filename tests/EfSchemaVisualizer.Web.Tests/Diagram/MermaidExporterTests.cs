using EfSchemaVisualizer.Core.Model;
using EfSchemaVisualizer.Web.Diagram;

namespace EfSchemaVisualizer.Web.Tests.Diagram;

public class MermaidExporterTests
{
    private static PropertyModel Property(string name, string clrType, bool isNullable = false) =>
        new(name, clrType, isNullable, MaxLength: null);

    private static EntityModel Entity(string name, params PropertyModel[] properties) =>
        new(name, properties);

    [Fact]
    public void Export_StartsWithErDiagramHeader()
    {
        var result = new DiagramModelResult(
            Array.Empty<EntityModel>(), Array.Empty<RelationshipModel>(), Array.Empty<Core.Parsing.Diagnostic>());

        var mermaid = MermaidExporter.Export(result);

        Assert.StartsWith("erDiagram", mermaid);
    }

    [Fact]
    public void Export_EntityWithProperties_EmitsAttributeBlockWithTypesAndKeys()
    {
        var blog = Entity("Blog", Property("Id", "int"), Property("Title", "string")) with
        {
            KeyPropertyNames = new[] { "Id" },
        };
        var result = new DiagramModelResult(new[] { blog }, Array.Empty<RelationshipModel>(), Array.Empty<Core.Parsing.Diagnostic>());

        var mermaid = MermaidExporter.Export(result);

        Assert.Contains("Blog {", mermaid);
        Assert.Contains("int Id PK", mermaid);
        Assert.Contains("string Title", mermaid);
        Assert.DoesNotContain("string Title PK", mermaid);
    }

    [Fact]
    public void Export_EntityWithNoProperties_EmitsNoAttributeBlock()
    {
        var empty = Entity("Empty");
        var result = new DiagramModelResult(new[] { empty }, Array.Empty<RelationshipModel>(), Array.Empty<Core.Parsing.Diagnostic>());

        var mermaid = MermaidExporter.Export(result);

        Assert.DoesNotContain("Empty {", mermaid);
    }

    [Theory]
    [InlineData(RelationshipKind.OneToOne, "||--||")]
    [InlineData(RelationshipKind.OneToMany, "||--o{")]
    [InlineData(RelationshipKind.ManyToMany, "}o--o{")]
    public void Export_Relationship_EmitsExpectedCardinalityTokens(RelationshipKind kind, string expectedToken)
    {
        var relationship = new RelationshipModel("Blog", "Post", kind, PrincipalNavigation: "Posts", DependentNavigation: "Blog");
        var result = new DiagramModelResult(
            new[] { Entity("Blog"), Entity("Post") }, new[] { relationship }, Array.Empty<Core.Parsing.Diagnostic>());

        var mermaid = MermaidExporter.Export(result);

        Assert.Contains($"Blog {expectedToken} Post", mermaid);
    }

    [Fact]
    public void Export_RelationshipLabel_PrefersDependentNavigation()
    {
        var relationship = new RelationshipModel(
            "Blog", "Post", RelationshipKind.OneToMany, PrincipalNavigation: "Posts", DependentNavigation: "Blog");
        var result = new DiagramModelResult(
            new[] { Entity("Blog"), Entity("Post") }, new[] { relationship }, Array.Empty<Core.Parsing.Diagnostic>());

        var mermaid = MermaidExporter.Export(result);

        Assert.Contains(": \"Blog\"", mermaid);
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

        var mermaid = MermaidExporter.Export(result);

        Assert.Contains("int BlogId FK", mermaid);
    }

    [Theory]
    [InlineData("int?", "int")]
    [InlineData("List<string>", "List_string")]
    [InlineData("Dictionary<string, int>", "Dictionary_string_int")]
    public void Export_SanitizesTypesForMermaidAttributeSyntax(string clrType, string expectedToken)
    {
        var entity = Entity("Widget", Property("Value", clrType));
        var result = new DiagramModelResult(new[] { entity }, Array.Empty<RelationshipModel>(), Array.Empty<Core.Parsing.Diagnostic>());

        var mermaid = MermaidExporter.Export(result);

        Assert.Contains($"{expectedToken} Value", mermaid);
    }
}
