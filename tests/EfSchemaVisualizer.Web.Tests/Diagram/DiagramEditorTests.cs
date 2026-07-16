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
}
