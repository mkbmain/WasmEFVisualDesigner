using EfSchemaVisualizer.Web.Diagram;

namespace EfSchemaVisualizer.Web.Tests.Diagram;

public class DiagramEditorEntityMappingTests
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
    public void SetViewMapping_NoExistingConfig_InsertsToView()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);

        var result = editor.SetViewMapping("Person", "vPeople", "dbo");

        Assert.True(result.Success);
        Assert.Equal("vPeople", editor.Current.Entities.Single().ViewName);
        Assert.Equal("dbo", editor.Current.Entities.Single().Schema);
        Assert.Contains("ToView(\"vPeople\", \"dbo\")", editor.ConfigSource);
    }

    [Fact]
    public void SetViewMapping_ClearingExistingConfig_RemovesToView()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);
        editor.SetViewMapping("Person", "vPeople", "dbo");

        var result = editor.SetViewMapping("Person", null, null);

        Assert.True(result.Success);
        Assert.Null(editor.Current.Entities.Single().ViewName);
        Assert.DoesNotContain("ToView", editor.ConfigSource);
    }

    [Fact]
    public void SetViewMapping_UnknownEntity_Fails()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);

        var result = editor.SetViewMapping("DoesNotExist", "vX", null);

        Assert.False(result.Success);
    }

    [Fact]
    public void SetSqlQuery_NoExistingConfig_InsertsToSqlQuery()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);

        var result = editor.SetSqlQuery("Person", "SELECT * FROM People");

        Assert.True(result.Success);
        Assert.Equal("SELECT * FROM People", editor.Current.Entities.Single().SqlQuery);
        Assert.Contains("ToSqlQuery(\"SELECT * FROM People\")", editor.ConfigSource);
    }

    [Fact]
    public void SetSqlQuery_ClearingExistingConfig_RemovesToSqlQuery()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);
        editor.SetSqlQuery("Person", "SELECT * FROM People");

        var result = editor.SetSqlQuery("Person", null);

        Assert.True(result.Success);
        Assert.Null(editor.Current.Entities.Single().SqlQuery);
        Assert.DoesNotContain("ToSqlQuery", editor.ConfigSource);
    }

    [Fact]
    public void SetKeyless_SetToTrue_InsertsHasNoKeyAndRemovesHasKey()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);

        var result = editor.SetKeyless("Person", true);

        Assert.True(result.Success);
        Assert.True(editor.Current.Entities.Single().IsKeyless);
        Assert.Contains("HasNoKey()", editor.ConfigSource);
        Assert.DoesNotContain("HasKey", editor.ConfigSource);
    }

    [Fact]
    public void SetKeyless_SetToFalse_WhenAlreadyNotKeyless_IsNoOp()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);

        var result = editor.SetKeyless("Person", false);

        Assert.True(result.Success);
        Assert.Equal(ConfigSource, editor.ConfigSource);
    }

    [Fact]
    public void SetKeyless_UnknownEntity_Fails()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);

        var result = editor.SetKeyless("DoesNotExist", true);

        Assert.False(result.Success);
    }
}
