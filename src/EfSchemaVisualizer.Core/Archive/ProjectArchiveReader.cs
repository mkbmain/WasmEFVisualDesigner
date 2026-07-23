using System.IO.Compression;
using System.Text.Json;
using EfSchemaVisualizer.Core.Parsing;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace EfSchemaVisualizer.Core.Archive;

public sealed record ProjectArchiveResult(
    string ClassSource,
    string ConfigSource,
    IReadOnlyList<Diagnostic> Diagnostics,
    IReadOnlyDictionary<string, EntityPosition> Layout);

public static class ProjectArchiveReader
{
    public static ProjectArchiveResult Read(Stream zipStream)
    {
        using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);

        var classFiles = new List<string>();
        var configFiles = new List<string>();
        var layout = new Dictionary<string, EntityPosition>();

        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.Equals(ProjectArchiveWriter.LayoutFileName, StringComparison.OrdinalIgnoreCase))
            {
                layout = ReadLayout(entry);
                continue;
            }

            if (!entry.FullName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            using var reader = new StreamReader(entry.Open());
            var text = reader.ReadToEnd();

            switch (Classify(text))
            {
                case FileKind.Config:
                    configFiles.Add(text);
                    break;
                case FileKind.Class:
                    classFiles.Add(text);
                    break;
                case FileKind.Ignored:
                    break;
            }
        }

        var classSource = string.Join(Environment.NewLine + Environment.NewLine, classFiles);
        var configSource = string.Join(Environment.NewLine + Environment.NewLine, configFiles);

        var diagnostics = new List<Diagnostic>();
        if (classFiles.Count == 0 && configFiles.Count == 0)
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticCodes.ArchiveNoContentFound,
                "No entity classes or configuration found in the uploaded zip.",
                EntityName: null,
                PropertyName: null,
                new TextSpan(0, 0)));
        }

        return new ProjectArchiveResult(classSource, configSource, diagnostics, layout);
    }

    private static Dictionary<string, EntityPosition> ReadLayout(ZipArchiveEntry entry)
    {
        try
        {
            using var reader = new StreamReader(entry.Open());
            var json = reader.ReadToEnd();
            return JsonSerializer.Deserialize<Dictionary<string, EntityPosition>>(json) ?? new Dictionary<string, EntityPosition>();
        }
        catch (JsonException)
        {
            // A malformed layout sidecar only affects where entities are drawn, not the
            // parsed model, so it's dropped rather than surfaced as a data-loss diagnostic.
            return new Dictionary<string, EntityPosition>();
        }
    }

    private enum FileKind
    {
        Ignored,
        Class,
        Config,
    }

    private static FileKind Classify(string sourceText)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceText);
        var root = tree.GetCompilationUnitRoot();

        if (FluentSyntaxHelpers.FindConfigurationScopes(root).Any())
        {
            return FileKind.Config;
        }

        var hasTypeDeclaration = root.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .Any(t => t is ClassDeclarationSyntax or RecordDeclarationSyntax or StructDeclarationSyntax);

        return hasTypeDeclaration ? FileKind.Class : FileKind.Ignored;
    }
}
