using System.Linq;
using System.Text;
using EfSchemaVisualizer.Core.Model;

namespace EfSchemaVisualizer.Web.Diagram;

/// <summary>
/// Renders a <see cref="DiagramModelResult"/> as Mermaid <c>erDiagram</c> text. Pure string
/// generation from the parsed model — no dependency on the live <c>BlazorDiagram</c> or its
/// node positions, unlike <see cref="SvgExporter"/>.
/// </summary>
public static class MermaidExporter
{
    public static string Export(DiagramModelResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine("erDiagram");

        foreach (var relationship in result.Relationships)
        {
            var (left, right) = Cardinality(relationship.Kind);
            var label = relationship.DependentNavigation ?? relationship.PrincipalNavigation ?? "relates to";
            sb.AppendLine(
                $"    {Identifier(relationship.PrincipalEntity)} {left}--{right} {Identifier(relationship.DependentEntity)} : \"{EscapeLabel(label)}\"");
        }

        foreach (var entity in result.Entities)
        {
            if (entity.Properties.Count == 0)
            {
                continue;
            }

            sb.AppendLine($"    {Identifier(entity.Name)} {{");

            foreach (var property in entity.Properties)
            {
                var isPrimaryKey = entity.KeyPropertyNames.Contains(property.Name);
                var isForeignKey = result.Relationships.Any(r =>
                    r.DependentEntity == entity.Name && r.ForeignKeyProperties.Contains(property.Name));

                var keyToken = (isPrimaryKey, isForeignKey) switch
                {
                    (true, true) => " PK,FK",
                    (true, false) => " PK",
                    (false, true) => " FK",
                    _ => "",
                };

                sb.AppendLine($"        {SanitizeType(property.ClrType)} {Identifier(property.Name)}{keyToken}");
            }

            sb.AppendLine("    }");
        }

        return sb.ToString();
    }

    private static (string Left, string Right) Cardinality(RelationshipKind kind) => kind switch
    {
        RelationshipKind.OneToOne => ("||", "||"),
        RelationshipKind.OneToMany => ("||", "o{"),
        RelationshipKind.ManyToMany => ("}o", "o{"),
        _ => ("||", "o{"),
    };

    private static string Identifier(string name) => name.Replace(' ', '_');

    private static string EscapeLabel(string label) => label.Replace("\"", "'");

    private static string SanitizeType(string clrType)
    {
        var sanitized = clrType
            .Replace("?", "")
            .Replace("<", "_")
            .Replace(">", "")
            .Replace(", ", "_")
            .Replace(",", "_")
            .Replace("[]", "Array")
            .Replace(" ", "");

        return sanitized.Length == 0 ? "object" : sanitized;
    }
}
