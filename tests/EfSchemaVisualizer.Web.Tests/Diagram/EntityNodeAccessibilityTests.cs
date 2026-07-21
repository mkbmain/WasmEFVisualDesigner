using System.Text.RegularExpressions;

namespace EfSchemaVisualizer.Web.Tests.Diagram;

/// Regression coverage for the aria-label gap the 2026-07-15 browser-verification pass found and
/// fixed in EntityNode.razor: an icon-only button with a `title` but no `aria-label` is invisible
/// to screen readers even though sighted users get a tooltip. Full component rendering via bUnit
/// isn't viable here — EntityNode's PortRenderer children depend on real browser layout APIs
/// (getBoundingClientRect/ResizeObserver) that bUnit's headless render tree doesn't provide and
/// throw a NullReferenceException during OnAfterRenderAsync — so this asserts directly against the
/// component's markup source instead.
public class EntityNodeAccessibilityTests
{
    private static readonly Regex ButtonTagRegex = new("<button\\b[^>]*>", RegexOptions.Compiled);
    private static readonly Regex TitleAttributeRegex = new("title=\"([^\"]*)\"", RegexOptions.Compiled);
    private static readonly Regex AriaLabelAttributeRegex = new("aria-label=\"([^\"]*)\"", RegexOptions.Compiled);

    [Fact]
    public void EveryTitledButton_HasAMatchingAriaLabel()
    {
        var markup = ReadEntityNodeRazorSource();
        var buttonTags = ButtonTagRegex.Matches(markup).Select(m => m.Value).ToList();

        Assert.NotEmpty(buttonTags);

        var titledButtonsMissingAriaLabel = buttonTags
            .Where(tag => TitleAttributeRegex.IsMatch(tag))
            .Where(tag => TitleAttributeRegex.Match(tag).Groups[1].Value
                != (AriaLabelAttributeRegex.Match(tag) is { Success: true } m ? m.Groups[1].Value : null))
            .ToList();

        Assert.Empty(titledButtonsMissingAriaLabel);
    }

    [Fact]
    public void PropertyExpandPanel_HasRowVersionAndConcurrencyTokenCheckboxes()
    {
        var markup = ReadEntityNodeRazorSource();

        Assert.Contains("CommitRowVersion", markup);
        Assert.Contains("CommitConcurrencyToken", markup);
        Assert.Contains("Row version", markup);
        Assert.Contains("Concurrency token", markup);
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
