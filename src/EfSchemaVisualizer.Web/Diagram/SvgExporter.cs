using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Blazor.Diagrams;
using Blazor.Diagrams.Core.Geometry;
using EfSchemaVisualizer.Core.Model;

namespace EfSchemaVisualizer.Web.Diagram;

/// <summary>
/// Renders a <see cref="DiagramModelResult"/> as a standalone SVG document, using each entity's
/// live <c>Position</c>/<c>Size</c> from the current <see cref="BlazorDiagram"/> — the same node
/// state <see cref="DiagramAutoLayout"/> reads — rather than the browser-only HTML/SVG hybrid the
/// on-screen canvas actually uses (entity cards are HTML overlaid next to, not inside, the
/// canvas's link &lt;svg&gt;, so there's no single live element that's already valid standalone SVG).
/// </summary>
public static class SvgExporter
{
    private const double DefaultWidth = 260;
    private const double DefaultHeight = 160;
    private const double Padding = 40;
    private const double HeaderHeight = 26;
    private const double RowHeight = 18;

    public static string Export(BlazorDiagram diagram, DiagramModelResult result)
    {
        var layout = new Dictionary<string, (Point Position, Size Size)>();
        foreach (var node in diagram.Nodes.OfType<EntityNodeModel>())
        {
            var size = node.Size is { Width: > 0, Height: > 0 } measured ? measured : new Size(DefaultWidth, DefaultHeight);
            layout[node.Entity.Name] = (node.Position, size);
        }

        return Export(result, layout);
    }

    internal static string Export(DiagramModelResult result, IReadOnlyDictionary<string, (Point Position, Size Size)> layout)
    {
        var maxX = 0.0;
        var maxY = 0.0;
        foreach (var (position, size) in layout.Values)
        {
            maxX = Math.Max(maxX, position.X + size.Width);
            maxY = Math.Max(maxY, position.Y + size.Height);
        }

        var width = maxX + Padding * 2;
        var height = maxY + Padding * 2;

        var sb = new StringBuilder();
        sb.AppendLine(FormattableString.Invariant(
            $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {width} {height}\" font-family=\"sans-serif\" font-size=\"12\">"));
        sb.AppendLine("<rect x=\"0\" y=\"0\" width=\"100%\" height=\"100%\" fill=\"white\" />");

        // Relationships first so their lines render underneath the entity cards.
        foreach (var relationship in result.Relationships)
        {
            AppendRelationship(sb, relationship, layout);
        }

        foreach (var entity in result.Entities)
        {
            AppendEntity(sb, entity, result.Relationships, layout);
        }

        sb.AppendLine("</svg>");
        return sb.ToString();
    }

    private static void AppendEntity(
        StringBuilder sb,
        EntityModel entity,
        IReadOnlyList<RelationshipModel> relationships,
        IReadOnlyDictionary<string, (Point Position, Size Size)> layout)
    {
        if (!layout.TryGetValue(entity.Name, out var placement))
        {
            return;
        }

        var (position, size) = placement;
        var x = position.X + Padding;
        var y = position.Y + Padding;

        sb.AppendLine("<g>");
        sb.AppendLine(FormattableString.Invariant(
            $"<rect x=\"{x}\" y=\"{y}\" width=\"{size.Width}\" height=\"{size.Height}\" fill=\"#f8f9fa\" stroke=\"#333\" stroke-width=\"1\" rx=\"4\" />"));
        sb.AppendLine(FormattableString.Invariant(
            $"<rect x=\"{x}\" y=\"{y}\" width=\"{size.Width}\" height=\"{HeaderHeight}\" fill=\"#343a40\" rx=\"4\" />"));
        sb.AppendLine(FormattableString.Invariant(
            $"<text x=\"{x + 8}\" y=\"{y + (HeaderHeight / 2) + 4}\" fill=\"white\" font-weight=\"bold\">{Escape(entity.Name)}</text>"));

        var rowY = y + HeaderHeight + RowHeight - 4;
        foreach (var property in entity.Properties)
        {
            var isPrimaryKey = entity.KeyPropertyNames.Contains(property.Name);
            var isForeignKey = relationships.Any(r =>
                r.DependentEntity == entity.Name && r.ForeignKeyProperties.Contains(property.Name));

            var marker = isPrimaryKey ? "PK " : isForeignKey ? "FK " : "";
            var nullSuffix = property.IsNullable ? "?" : "";

            sb.AppendLine(FormattableString.Invariant(
                $"<text x=\"{x + 8}\" y=\"{rowY}\">{Escape(marker)}{Escape(property.Name)} : {Escape(property.ClrType)}{nullSuffix}</text>"));

            rowY += RowHeight;
        }

        sb.AppendLine("</g>");
    }

    private static void AppendRelationship(
        StringBuilder sb,
        RelationshipModel relationship,
        IReadOnlyDictionary<string, (Point Position, Size Size)> layout)
    {
        if (!layout.TryGetValue(relationship.PrincipalEntity, out var principal) ||
            !layout.TryGetValue(relationship.DependentEntity, out var dependent))
        {
            return;
        }

        var principalCenter = CenterOf(principal);
        var dependentCenter = CenterOf(dependent);

        var (px, py) = AnchorPoint(principal, principalCenter, dependentCenter);
        var (dx, dy) = AnchorPoint(dependent, dependentCenter, principalCenter);

        sb.AppendLine(FormattableString.Invariant(
            $"<line x1=\"{px}\" y1=\"{py}\" x2=\"{dx}\" y2=\"{dy}\" stroke=\"#555\" stroke-width=\"1.5\" />"));

        var midX = (px + dx) / 2;
        var midY = (py + dy) / 2;
        var label = RelationshipLabels.For(relationship.Kind);
        sb.AppendLine(FormattableString.Invariant(
            $"<text x=\"{midX}\" y=\"{midY}\" fill=\"#555\" text-anchor=\"middle\">{Escape(label)}</text>"));
    }

    private static (double X, double Y) CenterOf((Point Position, Size Size) box) =>
        (box.Position.X + Padding + (box.Size.Width / 2), box.Position.Y + Padding + (box.Size.Height / 2));

    private static (double X, double Y) AnchorPoint(
        (Point Position, Size Size) box, (double X, double Y) center, (double X, double Y) towards)
    {
        var left = box.Position.X + Padding;
        var right = left + box.Size.Width;
        var x = towards.X >= center.X ? right : left;
        return (x, center.Y);
    }

    private static string Escape(string value) => value
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;");
}
