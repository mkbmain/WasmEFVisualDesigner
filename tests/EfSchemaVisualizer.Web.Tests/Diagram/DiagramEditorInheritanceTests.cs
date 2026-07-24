using EfSchemaVisualizer.Web.Diagram;
using Xunit;

namespace EfSchemaVisualizer.Web.Tests.Diagram;

public class DiagramEditorInheritanceTests
{
    private const string ClassSource = """
        public class Person
        {
            public int Id { get; set; }
            public string Name { get; set; }
        }

        public class Student : Person
        {
            public string Course { get; set; }
        }
        """;

    private const string ConfigSource = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
            }
        }
        """;

    [Fact]
    public void RenameProperty_InheritedPropertyViewedFromDerivedEntity_RenamesItOnTheBaseClass()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);

        var result = editor.RenameProperty("Student", "Name", "FullName");

        Assert.True(result.Success);
        Assert.Contains("public string FullName { get; set; }", editor.ClassSource);
        Assert.DoesNotContain("public string Name { get; set; }", editor.ClassSource);
        Assert.Contains(editor.Current.Entities.Single(e => e.Name == "Student").Properties, p => p.Name == "FullName");
    }

    [Fact]
    public void ChangePropertyType_InheritedPropertyViewedFromDerivedEntity_ChangesItOnTheBaseClass()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);

        var result = editor.ChangePropertyType("Student", "Name", "string", newIsNullable: true);

        Assert.True(result.Success);
        Assert.Contains("public string? Name { get; set; }", editor.ClassSource);
    }

    [Fact]
    public void RemoveProperty_InheritedPropertyViewedFromDerivedEntity_RemovesItFromTheBaseClass()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);

        var result = editor.RemoveProperty("Student", "Name");

        Assert.True(result.Success);
        Assert.DoesNotContain("public string Name { get; set; }", editor.ClassSource);
        Assert.DoesNotContain(editor.Current.Entities.Single(e => e.Name == "Student").Properties, p => p.Name == "Name");
        Assert.DoesNotContain(editor.Current.Entities.Single(e => e.Name == "Person").Properties, p => p.Name == "Name");
    }

    [Fact]
    public void RenameProperty_OwnPropertyOnDerivedEntity_StillWorksUnaffected()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);

        var result = editor.RenameProperty("Student", "Course", "Class");

        Assert.True(result.Success);
        Assert.Contains("public string Class { get; set; }", editor.ClassSource);
    }
}
