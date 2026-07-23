namespace EfSchemaVisualizer.Web.Tests.Diagram;

/// Markup-source assertions for Home.razor's fullscreen diagram toggle, following the same
/// technique as EntityNodeMarkupTests/EntityNodeAccessibilityTests: Home.razor's PortRenderer
/// descendants (via DiagramCanvas) depend on real browser layout APIs that bUnit's headless
/// render tree doesn't provide, so these assert directly against the component's markup/@code
/// source instead of rendering it.
public class HomeMarkupTests
{
    [Fact]
    public void FullscreenToolbar_ContainsExactlyFiveActions()
    {
        var markup = ReadHomeRazorSource();

        Assert.Contains("fullscreen-toolbar", markup);

        var fullscreenBlock = ExtractFullscreenBlock(markup);
        Assert.Contains("UndoAsync", fullscreenBlock);
        Assert.Contains("RedoAsync", fullscreenBlock);
        Assert.Contains("AutoLayout", fullscreenBlock);
        Assert.Contains("ZoomToFit", fullscreenBlock);
        Assert.Contains("ToggleFullscreen", fullscreenBlock);
    }

    [Fact]
    public void FullscreenToolbar_DoesNotExposeFileOrExportActions()
    {
        var markup = ReadHomeRazorSource();
        var fullscreenBlock = ExtractFullscreenBlock(markup);

        Assert.DoesNotContain("AddEntity", fullscreenBlock);
        Assert.DoesNotContain("DownloadZip", fullscreenBlock);
        Assert.DoesNotContain("ExportSvgAsync", fullscreenBlock);
        Assert.DoesNotContain("ExportMermaidAsync", fullscreenBlock);
        Assert.DoesNotContain("OnZipSelected", fullscreenBlock);
    }

    [Fact]
    public void ErrorText_IsRenderedInBothNormalAndFullscreenBranches()
    {
        var markup = ReadHomeRazorSource();

        var errorGuardCount = CountOccurrences(markup, "@if (_error is not null)");
        var errorRenderCount = CountOccurrences(markup, "@_error");

        Assert.Equal(2, errorGuardCount);
        Assert.Equal(2, errorRenderCount);

        var nonFullscreenBlock = ExtractNonFullscreenBlock(markup);
        var fullscreenBlock = ExtractFullscreenBlock(markup);

        Assert.Contains("@_error", nonFullscreenBlock);
        Assert.Contains("@_error", fullscreenBlock);
    }

    [Fact]
    public void PageBody_HidesEditorsAndToolbarAndInstructions_BehindIsFullscreenGuard()
    {
        var markup = ReadHomeRazorSource();

        Assert.Contains("@if (!_isFullscreen)", markup);

        var nonFullscreenBlock = ExtractNonFullscreenBlock(markup);

        Assert.Equal(1, CountOccurrences(markup, "id=\"class-source\""));
        Assert.Equal(1, CountOccurrences(markup, "id=\"config-source\""));
        Assert.Equal(1, CountOccurrences(markup, "id=\"render-diagram\""));

        Assert.Contains("id=\"class-source\"", nonFullscreenBlock);
        Assert.Contains("id=\"config-source\"", nonFullscreenBlock);
        Assert.Contains("id=\"render-diagram\"", nonFullscreenBlock);
    }

    [Fact]
    public void OnEscapeShortcut_OnlyActsWhenFullscreen()
    {
        var markup = ReadHomeRazorSource();

        Assert.Contains("[JSInvokable]", markup);
        Assert.Contains("public void OnEscapeShortcut()", markup);

        var methodIndex = markup.IndexOf("public void OnEscapeShortcut()", StringComparison.Ordinal);
        Assert.True(methodIndex >= 0);

        var methodBody = markup.Substring(methodIndex, 200);
        Assert.Contains("if (!_isFullscreen)", methodBody);
        Assert.Contains("_isFullscreen = false;", methodBody);
        Assert.Contains("StateHasChanged();", methodBody);
    }

    [Fact]
    public void KeyboardShortcuts_Escape_InvokesOnEscapeShortcut()
    {
        var script = ReadKeyboardShortcutsJsSource();

        Assert.Contains("'Escape'", script);
        Assert.Contains("'OnEscapeShortcut'", script);

        var escapeIndex = script.IndexOf("'Escape'", StringComparison.Ordinal);
        var isEditableTargetCheckIndex = script.IndexOf("isEditableTarget(event.target)", StringComparison.Ordinal);

        Assert.True(escapeIndex >= 0);
        Assert.True(isEditableTargetCheckIndex >= 0);
        Assert.True(escapeIndex < isEditableTargetCheckIndex,
            "The Escape check must appear before the isEditableTarget/ctrl-key check in handleUndoRedoKeydown, so Escape can't fall through into the undo/redo logic.");
    }

    private static string ExtractFullscreenBlock(string markup)
    {
        const string start = "@if (_isFullscreen)";
        const string end = "<CascadingValue Value=\"_diagram\">";

        var startIndex = markup.IndexOf(start, StringComparison.Ordinal);
        var endIndex = markup.IndexOf(end, StringComparison.Ordinal);

        Assert.True(startIndex >= 0, "Could not find the fullscreen-branch start marker in Home.razor.");
        Assert.True(endIndex > startIndex, "Could not find the fullscreen-branch end marker in Home.razor.");

        return markup.Substring(startIndex, endIndex - startIndex);
    }

    private static string ExtractNonFullscreenBlock(string markup)
    {
        const string start = "@if (!_isFullscreen)";
        const string end = "\n@if (_diagram is not null && _editContext is not null)\n{\n    <div class=\"diagram-panel";

        var startIndex = markup.IndexOf(start, StringComparison.Ordinal);
        var endIndex = markup.IndexOf(end, StringComparison.Ordinal);

        Assert.True(startIndex >= 0, "Could not find the non-fullscreen-branch start marker in Home.razor.");
        Assert.True(endIndex > startIndex, "Could not find the non-fullscreen-branch end marker in Home.razor.");

        return markup.Substring(startIndex, endIndex - startIndex);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    private static string ReadHomeRazorSource()
    {
        var path = Path.Combine(FindRepoRoot(), "src", "EfSchemaVisualizer.Web", "Pages", "Home.razor");
        return File.ReadAllText(path);
    }

    private static string ReadKeyboardShortcutsJsSource()
    {
        var path = Path.Combine(FindRepoRoot(), "src", "EfSchemaVisualizer.Web", "wwwroot", "js", "keyboardShortcuts.js");
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
