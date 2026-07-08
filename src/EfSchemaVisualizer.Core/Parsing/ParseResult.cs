using System.Collections.Generic;

namespace EfSchemaVisualizer.Core.Parsing;

public sealed record ParseResult<T>(T Value, IReadOnlyList<Diagnostic> Diagnostics);
