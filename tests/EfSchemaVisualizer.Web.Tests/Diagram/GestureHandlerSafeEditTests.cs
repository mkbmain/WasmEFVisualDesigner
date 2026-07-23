namespace EfSchemaVisualizer.Web.Tests.Diagram;

/// F1 hardening: a rewriter bug can throw instead of returning a failed DiagramEditResult (as
/// RemoveEntity did on bare-statement config), and an uncaught exception from a gesture handler
/// crashes the whole Blazor app rather than showing an inline error. EntityNode.razor and
/// RelationshipLinkLabel.razor route every DiagramEditResult-returning Editor call through a
/// SafeEdit(...) wrapper for this reason. Full component rendering via bUnit isn't viable for these
/// components (see EntityNodeAccessibilityTests), so this asserts directly against markup source,
/// matching this codebase's existing test style for the file.
public class GestureHandlerSafeEditTests
{
    private const string EditorMarker = "EditContext.Editor.";
    private const string ReadOnlyEditorMarker = "EditContext.Editor.Current.";
    private const string SafeEditMarker = "SafeEdit(";

    [Theory]
    [InlineData("EntityNode.razor")]
    [InlineData("RelationshipLinkLabel.razor")]
    public void EveryEditorMutationCall_IsWrappedInSafeEdit(string fileName)
    {
        var source = ReadRazorSource(fileName);

        Assert.Contains("private static DiagramEditResult SafeEdit(Func<DiagramEditResult> operation)", source);

        // Strip line comments so a comment merely mentioning "SafeEdit(" can't mask a genuinely
        // unwrapped call, then collapse whitespace so multi-line statements (e.g. a ternary
        // choosing between two Editor calls) read as one line for statement-boundary scanning.
        var stripped = StripLineComments(source);
        var normalized = string.Join(' ', stripped.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        var unwrappedCallOffsets = FindUnwrappedEditorMutationCalls(normalized);

        Assert.Empty(unwrappedCallOffsets);
    }

    /// Finds every "EditContext.Editor." mutation call (excluding the read-only
    /// "EditContext.Editor.Current." property access) that is not lexically nested inside a
    /// SafeEdit(...) call's argument — i.e. verifies the call site sits inside the parentheses
    /// opened by the nearest preceding SafeEdit( within the same statement, not just that the
    /// substring "SafeEdit(" happens to appear somewhere earlier in the statement.
    private static List<int> FindUnwrappedEditorMutationCalls(string normalized)
    {
        var unwrapped = new List<int>();
        var searchStart = 0;

        while (true)
        {
            var idx = normalized.IndexOf(EditorMarker, searchStart, StringComparison.Ordinal);
            if (idx < 0)
            {
                break;
            }

            searchStart = idx + EditorMarker.Length;

            var isReadOnlyAccess = idx + ReadOnlyEditorMarker.Length <= normalized.Length
                && normalized.Substring(idx, ReadOnlyEditorMarker.Length) == ReadOnlyEditorMarker;
            if (isReadOnlyAccess)
            {
                continue;
            }

            var statementStart = normalized.LastIndexOf(';', Math.Max(idx - 1, 0)) + 1;
            var safeEditIdx = normalized.LastIndexOf(SafeEditMarker, idx, idx - statementStart, StringComparison.Ordinal);

            if (safeEditIdx < 0 || !IsInsideBalancedCall(normalized, safeEditIdx + SafeEditMarker.Length, idx))
            {
                unwrapped.Add(idx);
            }
        }

        return unwrapped;
    }

    /// Given the position right after a call's opening '(', verifies that paren depth never
    /// returns to zero before reaching targetIdx — i.e. targetIdx is still inside that call.
    private static bool IsInsideBalancedCall(string text, int afterOpenParenIdx, int targetIdx)
    {
        var depth = 1;
        for (var i = afterOpenParenIdx; i < targetIdx; i++)
        {
            if (text[i] == '(')
            {
                depth++;
            }
            else if (text[i] == ')')
            {
                depth--;
                if (depth == 0)
                {
                    return false;
                }
            }
        }

        return depth > 0;
    }

    private static string StripLineComments(string source)
    {
        var lines = source.Split('\n').Select(line =>
        {
            var idx = line.IndexOf("//", StringComparison.Ordinal);
            return idx >= 0 ? line[..idx] : line;
        });
        return string.Join('\n', lines);
    }

    private static string ReadRazorSource(string fileName)
    {
        var path = Path.Combine(FindRepoRoot(), "src", "EfSchemaVisualizer.Web", "Diagram", fileName);
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
