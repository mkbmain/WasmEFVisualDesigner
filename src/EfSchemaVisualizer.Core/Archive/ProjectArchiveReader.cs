using System.IO.Compression;
using EfSchemaVisualizer.Core.Parsing;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace EfSchemaVisualizer.Core.Archive;

public sealed record ProjectArchiveResult(
    string ClassSource,
    string ConfigSource,
    IReadOnlyList<Diagnostic> Diagnostics);

public static class ProjectArchiveReader
{
    public static ProjectArchiveResult Read(Stream zipStream)
    {
        using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);

        var classFiles = new List<string>();
        var configFiles = new List<string>();

        foreach (var entry in zip.Entries)
        {
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

        return new ProjectArchiveResult(classSource, configSource, diagnostics);
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

        var hasOnModelCreating = root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Any(m => m.Identifier.Text == "OnModelCreating");

        var hasEntityTypeConfigurationBase = root.DescendantNodes()
            .OfType<BaseListSyntax>()
            .Any(baseList => baseList.Types.Any(t => t.Type.ToString().Contains("IEntityTypeConfiguration")));

        if (hasOnModelCreating || hasEntityTypeConfigurationBase)
        {
            return FileKind.Config;
        }

        var hasTypeDeclaration = root.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .Any(t => t is ClassDeclarationSyntax or RecordDeclarationSyntax or StructDeclarationSyntax);

        return hasTypeDeclaration ? FileKind.Class : FileKind.Ignored;
    }
}
