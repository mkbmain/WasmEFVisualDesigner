using System.IO.Compression;
using System.Text;
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
    IReadOnlyDictionary<string, EntityPosition> Layout,
    IReadOnlyDictionary<string, string> EntityFileOrigins,
    IReadOnlyDictionary<string, string> ConfigFileOrigins,
    IReadOnlyDictionary<string, byte[]> PassthroughFiles);

public static class ProjectArchiveReader
{
    public static ProjectArchiveResult Read(Stream zipStream)
    {
        using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);

        var classFiles = new List<string>();
        var configFiles = new List<string>();
        var layout = new Dictionary<string, EntityPosition>();
        var entityFileOrigins = new Dictionary<string, string>();
        var entityStyleConfigOrigins = new Dictionary<string, string>();
        var classStyleConfigOrigins = new Dictionary<string, string>();
        var passthroughFiles = new Dictionary<string, byte[]>();
        var skippedBuildArtifactCount = 0;
        var excludedGeneratedFiles = new List<string>();

        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.EndsWith('/'))
            {
                continue;
            }

            if (entry.FullName.Equals(ProjectArchiveWriter.LayoutFileName, StringComparison.OrdinalIgnoreCase))
            {
                layout = ReadLayout(entry);
                continue;
            }

            if (ArchivePathFilter.IsBuildArtifact(entry.FullName))
            {
                skippedBuildArtifactCount++;
                continue;
            }

            var isGeneratedOrMigration = ArchivePathFilter.IsGeneratedOrMigration(entry.FullName);

            if (!entry.FullName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                passthroughFiles[entry.FullName] = ReadAllBytes(entry);
                continue;
            }

            if (isGeneratedOrMigration)
            {
                passthroughFiles[entry.FullName] = ReadAllBytes(entry);
                excludedGeneratedFiles.Add(entry.FullName);
                continue;
            }

            using var reader = new StreamReader(entry.Open());
            var text = reader.ReadToEnd();

            switch (Classify(text))
            {
                case FileKind.Config:
                    configFiles.Add(text);
                    RecordConfigOrigins(text, entry.FullName, entityStyleConfigOrigins, classStyleConfigOrigins);
                    break;
                case FileKind.Class:
                    classFiles.Add(text);
                    RecordClassOrigins(text, entry.FullName, entityFileOrigins);
                    break;
                case FileKind.Ignored:
                    passthroughFiles[entry.FullName] = Encoding.UTF8.GetBytes(text);
                    break;
            }
        }

        // Entity<T>() style always wins over IEntityTypeConfiguration<T> style when an
        // entity is (unusually) configured both ways, matching the existing rewriter
        // precedent that the Entity<T>() block is authoritative for that entity.
        var configFileOrigins = new Dictionary<string, string>(classStyleConfigOrigins);
        foreach (var (entityName, fileName) in entityStyleConfigOrigins)
        {
            configFileOrigins[entityName] = fileName;
        }

        var classSource = MultiFileSourceMerger.Merge(classFiles);
        var configSource = MultiFileSourceMerger.Merge(configFiles);

        var diagnostics = new List<Diagnostic>();
        if (skippedBuildArtifactCount > 0)
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticCodes.ArchiveBuildArtifactSkipped,
                $"Skipped {skippedBuildArtifactCount} build-artifact file(s) under bin/ or obj/ — "
                    + "not part of the schema and not included in the download.",
                EntityName: null,
                PropertyName: null,
                new TextSpan(0, 0)));
        }

        if (excludedGeneratedFiles.Count > 0)
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticCodes.ArchiveGeneratedFileExcluded,
                $"Excluded {excludedGeneratedFiles.Count} migration/generated file(s) from the diagram "
                    + $"(preserved unchanged for download): {string.Join(", ", excludedGeneratedFiles)}.",
                EntityName: null,
                PropertyName: null,
                new TextSpan(0, 0)));
        }

        if (classFiles.Count == 0 && configFiles.Count == 0)
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticCodes.ArchiveNoContentFound,
                "No entity classes or configuration found in the uploaded zip.",
                EntityName: null,
                PropertyName: null,
                new TextSpan(0, 0)));
        }

        return new ProjectArchiveResult(
            classSource, configSource, diagnostics, layout, entityFileOrigins, configFileOrigins, passthroughFiles);
    }

    private static byte[] ReadAllBytes(ZipArchiveEntry entry)
    {
        using var entryStream = entry.Open();
        using var memory = new MemoryStream();
        entryStream.CopyTo(memory);
        return memory.ToArray();
    }

    private static void RecordClassOrigins(string sourceText, string fileName, Dictionary<string, string> entityFileOrigins)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceText);
        var root = tree.GetCompilationUnitRoot();

        var typeDeclarations = root.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .Where(t => t is ClassDeclarationSyntax or RecordDeclarationSyntax or StructDeclarationSyntax)
            .Where(t => !t.Ancestors().OfType<TypeDeclarationSyntax>().Any());

        foreach (var typeDeclaration in typeDeclarations)
        {
            entityFileOrigins[typeDeclaration.Identifier.Text] = fileName;
        }
    }

    private static void RecordConfigOrigins(
        string sourceText,
        string fileName,
        Dictionary<string, string> entityStyleOrigins,
        Dictionary<string, string> classStyleOrigins)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceText);
        var root = tree.GetCompilationUnitRoot();

        foreach (var (entityName, scope) in FluentSyntaxHelpers.FindConfigurationScopes(root))
        {
            if (scope is InvocationExpressionSyntax)
            {
                entityStyleOrigins[entityName] = fileName;
            }
            else
            {
                classStyleOrigins[entityName] = fileName;
            }
        }
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
