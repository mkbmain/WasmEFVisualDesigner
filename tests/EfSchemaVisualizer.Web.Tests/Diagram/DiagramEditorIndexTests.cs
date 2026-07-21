using System.Linq;
using EfSchemaVisualizer.Web.Diagram;

namespace EfSchemaVisualizer.Web.Tests.Diagram;

public class DiagramEditorIndexTests
{
    private const string ClassSource = """
        public class Person
        {
            public int Id { get; set; }
            public string Email { get; set; } = "";
            public string FirstName { get; set; } = "";
        }
        """;

    private const string ConfigSourceWithIndexExtras = """
        modelBuilder.Entity<Person>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Email)
                .HasFilter("[Email] IS NOT NULL")
                .IncludeProperties(e => e.FirstName)
                .IsDescending(true);
        });
        """;

    [Fact]
    public void SetIndexUnique_PreservesFilterIsDescendingAndIncludeProperties()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSourceWithIndexExtras);

        var result = editor.SetIndexUnique("Person", new[] { "Email" }, isUnique: true);

        Assert.True(result.Success);
        var index = editor.Current.Entities.Single().Indexes.Single();
        Assert.True(index.IsUnique);
        Assert.Equal("[Email] IS NOT NULL", index.Filter);
        Assert.Equal(new[] { true }, index.IsDescending);
        Assert.Equal(new[] { "FirstName" }, index.IncludeProperties);
    }

    [Fact]
    public void RenameIndex_PreservesFilterIsDescendingAndIncludeProperties()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSourceWithIndexExtras);

        var result = editor.RenameIndex("Person", new[] { "Email" }, "IX_Person_Email");

        Assert.True(result.Success);
        var index = editor.Current.Entities.Single().Indexes.Single();
        Assert.Equal("IX_Person_Email", index.Name);
        Assert.Equal("[Email] IS NOT NULL", index.Filter);
        Assert.Equal(new[] { true }, index.IsDescending);
        Assert.Equal(new[] { "FirstName" }, index.IncludeProperties);
    }

    [Fact]
    public void ToggleIndexMembership_AddingColumn_PreservesFilterIsDescendingAndIncludeProperties()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSourceWithIndexExtras);

        var result = editor.ToggleIndexMembership("Person", new[] { "Email" }, "FirstName", include: true);

        Assert.True(result.Success);
        var index = editor.Current.Entities.Single().Indexes.Single();
        Assert.Equal(new[] { "Email", "FirstName" }, index.PropertyNames);
        Assert.Equal("[Email] IS NOT NULL", index.Filter);
        Assert.Equal(new[] { true }, index.IsDescending);
        Assert.Equal(new[] { "FirstName" }, index.IncludeProperties);
    }
}
