using System.Linq;
using EfSchemaVisualizer.Web.Diagram;

namespace EfSchemaVisualizer.Web.Tests.Diagram;

public class DiagramEditorPropertyPanelTests
{
    private const string ClassSource = """
        public class Person
        {
            public int Id { get; set; }
            public string Name { get; set; } = "";
        }
        """;

    private const string ConfigSource = """
        modelBuilder.Entity<Person>(entity =>
        {
            entity.HasKey(e => e.Id);
        });
        """;

    [Fact]
    public void SetMaxLength_NoExistingConfig_InsertsHasMaxLength()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);

        var result = editor.SetMaxLength("Person", "Name", 100);

        Assert.True(result.Success);
        Assert.Equal(100, editor.Current.Entities.Single().Properties.Single(p => p.Name == "Name").MaxLength);
        Assert.Contains("HasMaxLength(100)", editor.ConfigSource);
    }

    [Fact]
    public void SetMaxLength_ClearingExistingConfig_RemovesHasMaxLength()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);
        editor.SetMaxLength("Person", "Name", 100);

        var result = editor.SetMaxLength("Person", "Name", null);

        Assert.True(result.Success);
        Assert.Null(editor.Current.Entities.Single().Properties.Single(p => p.Name == "Name").MaxLength);
        Assert.DoesNotContain("HasMaxLength", editor.ConfigSource);
    }

    [Fact]
    public void SetMaxLength_NonPositiveValue_Fails()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);

        var result = editor.SetMaxLength("Person", "Name", 0);

        Assert.False(result.Success);
    }

    [Fact]
    public void SetRequiredOverride_SetToTrue_InsertsIsRequired()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);

        var result = editor.SetRequiredOverride("Person", "Name", true);

        Assert.True(result.Success);
        Assert.True(editor.Current.Entities.Single().Properties.Single(p => p.Name == "Name").IsRequiredOverride);
        Assert.Contains("IsRequired()", editor.ConfigSource);
    }

    [Fact]
    public void SetRequiredOverride_ClearingExistingOverride_RemovesIsRequired()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);
        editor.SetRequiredOverride("Person", "Name", false);

        var result = editor.SetRequiredOverride("Person", "Name", null);

        Assert.True(result.Success);
        Assert.Null(editor.Current.Entities.Single().Properties.Single(p => p.Name == "Name").IsRequiredOverride);
        Assert.DoesNotContain("IsRequired", editor.ConfigSource);
    }

    private const string RelationshipClassSource = """
        public class Blog
        {
            public int Id { get; set; }
            public ICollection<Post> Posts { get; set; } = new List<Post>();
        }

        public class Post
        {
            public int Id { get; set; }
            public int BlogId { get; set; }
            public Blog Blog { get; set; } = null!;
        }
        """;

    private const string RelationshipConfigSource = """
        modelBuilder.Entity<Post>(entity =>
        {
            entity.HasOne(p => p.Blog)
                .WithMany(b => b.Posts)
                .HasForeignKey(p => p.BlogId);
        });
        """;

    [Fact]
    public void SetRelationshipShape_SettingOnDeleteBehavior_WritesOnDeleteCall()
    {
        var editor = new DiagramEditor(RelationshipClassSource, RelationshipConfigSource);
        var relationship = editor.Current.Relationships.Single();

        var result = editor.SetRelationshipShape(relationship, relationship.Kind, relationship.ForeignKeyProperties, "Cascade");

        Assert.True(result.Success);
        Assert.Equal("Cascade", editor.Current.Relationships.Single().OnDeleteBehavior);
        Assert.Contains("OnDelete(DeleteBehavior.Cascade)", editor.ConfigSource);
    }

    [Fact]
    public void SetRelationshipShape_SameKindFkAndOnDelete_IsNoOp()
    {
        var editor = new DiagramEditor(RelationshipClassSource, RelationshipConfigSource);
        var relationship = editor.Current.Relationships.Single();
        var configSourceBefore = editor.ConfigSource;

        var result = editor.SetRelationshipShape(relationship, relationship.Kind, relationship.ForeignKeyProperties, relationship.OnDeleteBehavior);

        Assert.True(result.Success);
        Assert.Equal(configSourceBefore, editor.ConfigSource);
        Assert.False(editor.CanUndo);
    }
}
