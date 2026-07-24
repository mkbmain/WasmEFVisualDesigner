using EfSchemaVisualizer.Web.Diagram;

namespace EfSchemaVisualizer.Web.Tests.Diagram;

public class DiagramEditorTests
{
    private const string ClassSource = """
        public class Blog
        {
            public int Id { get; set; }
            public string Title { get; set; } = "";
        }
        """;

    private const string ConfigSource = """
        modelBuilder.Entity<Blog>(entity =>
        {
            entity.HasKey(e => e.Id);
        });
        """;

    [Fact]
    public void CanUndo_NoEditsYet_IsFalse()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);

        Assert.False(editor.CanUndo);
        Assert.False(editor.CanRedo);
    }

    [Fact]
    public void Undo_AfterRename_RestoresPreviousSource()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);
        var classSourceBeforeRename = editor.ClassSource;

        editor.RenameEntity("Blog", "Post");
        Assert.True(editor.CanUndo);
        Assert.Contains("Post", editor.ClassSource);

        var result = editor.Undo();

        Assert.True(result.Success);
        Assert.Equal(classSourceBeforeRename, editor.ClassSource);
        Assert.False(editor.CanUndo);
        Assert.True(editor.CanRedo);
        Assert.Contains(editor.Current.Entities, e => e.Name == "Blog");
    }

    [Fact]
    public void RenameEntity_ToReservedKeyword_Fails()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);

        var result = editor.RenameEntity("Blog", "class");

        Assert.False(result.Success);
        Assert.Contains("Blog", editor.ClassSource);
        Assert.DoesNotContain("public class class", editor.ClassSource);
    }

    [Fact]
    public void RenameProperty_ToReservedKeyword_Fails()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);

        var result = editor.RenameProperty("Blog", "Title", "class");

        Assert.False(result.Success);
        Assert.Contains("Title", editor.ClassSource);
    }

    [Fact]
    public void RenameProperty_ToSameNameAsEnclosingEntity_Fails()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);

        var result = editor.RenameProperty("Blog", "Title", "Blog");

        Assert.False(result.Success);
        Assert.Contains("Title", editor.ClassSource);
    }

    [Fact]
    public void AddProperty_WithClrType_UsesRequestedType()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);

        var result = editor.AddProperty("Blog", "int");

        Assert.True(result.Success);
        var property = Assert.Single(editor.Current.Entities.Single(e => e.Name == "Blog").Properties, p => p.Name == "NewProperty");
        Assert.Equal("int", property.ClrType);
    }

    [Fact]
    public void AddProperty_NoClrTypeSpecified_DefaultsToString()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);

        var result = editor.AddProperty("Blog");

        Assert.True(result.Success);
        var property = Assert.Single(editor.Current.Entities.Single(e => e.Name == "Blog").Properties, p => p.Name == "NewProperty");
        Assert.Equal("string", property.ClrType);
    }

    private const string ConfigSourceWithHasDataSeed = """
        modelBuilder.Entity<Blog>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasData(new Blog { Id = 1, Title = "First" });
        });
        """;

    [Fact]
    public void RenameEntity_WithHasDataSeedRows_RenamesObjectCreationType()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSourceWithHasDataSeed);

        var result = editor.RenameEntity("Blog", "Post");

        Assert.True(result.Success);
        Assert.Contains("new Post { Id = 1, Title = \"First\" }", editor.ConfigSource);
        Assert.DoesNotContain("new Blog", editor.ConfigSource);
    }

    [Fact]
    public void Redo_AfterUndo_ReappliesTheEdit()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);
        editor.RenameEntity("Blog", "Post");
        var classSourceAfterRename = editor.ClassSource;

        editor.Undo();
        var result = editor.Redo();

        Assert.True(result.Success);
        Assert.Equal(classSourceAfterRename, editor.ClassSource);
        Assert.False(editor.CanRedo);
        Assert.True(editor.CanUndo);
        Assert.Contains(editor.Current.Entities, e => e.Name == "Post");
    }

    [Fact]
    public void Apply_AfterUndo_ClearsRedoStack()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);
        editor.RenameEntity("Blog", "Post");
        editor.Undo();
        Assert.True(editor.CanRedo);

        editor.AddEntity();

        Assert.False(editor.CanRedo);
    }

    [Fact]
    public void Undo_NoEditsYet_FailsWithoutChangingSource()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);
        var classSource = editor.ClassSource;

        var result = editor.Undo();

        Assert.False(result.Success);
        Assert.Equal(classSource, editor.ClassSource);
    }

    [Fact]
    public void Redo_NothingUndone_Fails()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);

        var result = editor.Redo();

        Assert.False(result.Success);
    }

    [Fact]
    public void Undo_NoOpEditThatReturnsOkWithoutApplying_DoesNotPushUndoState()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);

        var result = editor.RenameEntity("Blog", "Blog");

        Assert.True(result.Success);
        Assert.False(editor.CanUndo);
    }

    [Fact]
    public void Constructor_WithFileOrigins_ExposesThemUnchanged()
    {
        var entityFileOrigins = new Dictionary<string, string> { ["Blog"] = "Models/Blog.cs" };
        var configFileOrigins = new Dictionary<string, string> { ["Blog"] = "Data/AppDbContext.cs" };

        var editor = new DiagramEditor(ClassSource, ConfigSource, entityFileOrigins, configFileOrigins);

        Assert.Equal("Models/Blog.cs", editor.EntityFileOrigins["Blog"]);
        Assert.Equal("Data/AppDbContext.cs", editor.ConfigFileOrigins["Blog"]);
    }

    [Fact]
    public void Constructor_WithoutFileOrigins_ExposesEmptyMaps()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);

        Assert.Empty(editor.EntityFileOrigins);
        Assert.Empty(editor.ConfigFileOrigins);
    }

    [Fact]
    public void RenameEntity_MovesFileOriginToTheNewName()
    {
        var entityFileOrigins = new Dictionary<string, string> { ["Blog"] = "Models/Blog.cs" };
        var configFileOrigins = new Dictionary<string, string> { ["Blog"] = "Data/AppDbContext.cs" };
        var editor = new DiagramEditor(ClassSource, ConfigSource, entityFileOrigins, configFileOrigins);

        editor.RenameEntity("Blog", "Post");

        Assert.False(editor.EntityFileOrigins.ContainsKey("Blog"));
        Assert.Equal("Models/Blog.cs", editor.EntityFileOrigins["Post"]);
        Assert.False(editor.ConfigFileOrigins.ContainsKey("Blog"));
        Assert.Equal("Data/AppDbContext.cs", editor.ConfigFileOrigins["Post"]);
    }

    [Fact]
    public void RemoveEntity_DropsItsFileOrigins()
    {
        const string classSource = """
            public class Blog
            {
                public int Id { get; set; }
            }

            public class Post
            {
                public int Id { get; set; }
                public int BlogId { get; set; }
            }
            """;
        const string configSource = """
            modelBuilder.Entity<Blog>(entity => entity.HasKey(e => e.Id));
            modelBuilder.Entity<Post>(entity => entity.HasKey(e => e.Id));
            """;
        var entityFileOrigins = new Dictionary<string, string> { ["Post"] = "Models/Post.cs" };
        var editor = new DiagramEditor(classSource, configSource, entityFileOrigins);

        editor.RemoveEntity("Post");

        Assert.False(editor.EntityFileOrigins.ContainsKey("Post"));
    }

    [Fact]
    public void Undo_AfterRename_RestoresPreviousFileOrigins()
    {
        var entityFileOrigins = new Dictionary<string, string> { ["Blog"] = "Models/Blog.cs" };
        var editor = new DiagramEditor(ClassSource, ConfigSource, entityFileOrigins);

        editor.RenameEntity("Blog", "Post");
        editor.Undo();

        Assert.Equal("Models/Blog.cs", editor.EntityFileOrigins["Blog"]);
        Assert.False(editor.EntityFileOrigins.ContainsKey("Post"));
    }
}
