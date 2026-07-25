using Microsoft.CodeAnalysis.Text;

namespace EfSchemaVisualizer.Core.Parsing;

public enum DiagnosticCategory
{
    /// The source couldn't be read/understood as intended.
    Parse,

    /// The source parsed fine, but the resulting model would fail (or behave unexpectedly)
    /// at EF's own model-build time.
    ModelValidity,
}

public sealed record Diagnostic(
    string Code,
    string Message,
    string? EntityName,
    string? PropertyName,
    TextSpan Span,
    DiagnosticCategory Category = DiagnosticCategory.Parse);
