namespace EfSchemaVisualizer.Core.Model;

/// A generic `HasAnnotation("Name", value)` captured for display only — the value is the argument's
/// raw source text, since annotation values can be any expression shape (string/int/enum/etc.), not
/// just a literal. Read-only: there is no rewriter support for editing an annotation back into source.
public sealed record AnnotationModel(string Name, string ValueText);
