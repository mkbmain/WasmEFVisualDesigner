using Microsoft.CodeAnalysis.Text;

namespace EfSchemaVisualizer.Core.Parsing;

public sealed record Diagnostic(
    string Code,
    string Message,
    string? EntityName,
    string? PropertyName,
    TextSpan Span);
