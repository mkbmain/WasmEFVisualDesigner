namespace EfSchemaVisualizer.Web.Tests.Diagram;

/// Markup-source assertions for EntityNode.razor features that can't be exercised via full bUnit
/// rendering (see EntityNodeAccessibilityTests for why). Each test pins down a specific rendering
/// invariant the component's @code and markup must uphold.
public class EntityNodeMarkupTests
{
    [Fact]
    public void PropertyRow_RendersValueGeneratedBadge_WhenValueGeneratedIsSet()
    {
        var markup = ReadEntityNodeRazorSource();

        Assert.Contains("property.ValueGenerated is not null", markup);
        Assert.Contains("value-generated-badge", markup);
    }

    [Fact]
    public void PropertyRow_RendersReadOnlyShadowRow_WhenIsShadowIsSet()
    {
        var markup = ReadEntityNodeRazorSource();

        Assert.Contains("property.IsShadow", markup);
        Assert.Contains("shadow-property", markup);
    }

    [Fact]
    public void EntityHeader_RendersViewAndSqlQueryAndKeylessFields()
    {
        var markup = ReadEntityNodeRazorSource();

        Assert.Contains("CommitViewName", markup);
        Assert.Contains("CommitSqlQuery", markup);
        Assert.Contains("CommitKeyless", markup);
    }

    [Fact]
    public void PrimaryKeyCheckbox_IsDisabled_WhenEntityIsKeyless()
    {
        var markup = ReadEntityNodeRazorSource();

        Assert.Contains("disabled=\"@Node.Entity.IsKeyless\"", markup);
    }

    private static string ReadEntityNodeRazorSource()
    {
        var path = Path.Combine(FindRepoRoot(), "src", "EfSchemaVisualizer.Web", "Diagram", "EntityNode.razor");
        return File.ReadAllText(path);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "EfSchemaVisualizer.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
            ?? throw new InvalidOperationException("Could not locate repo root (EfSchemaVisualizer.slnx) above " + AppContext.BaseDirectory);
    }
}
