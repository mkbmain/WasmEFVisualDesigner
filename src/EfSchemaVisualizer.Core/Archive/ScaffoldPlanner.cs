using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EfSchemaVisualizer.Core.Archive;

public enum ScaffoldProvider
{
    SqlServer,
    PostgreSql,
    Sqlite,
}

public sealed record ScaffoldPlan(
    bool NeedsCsproj,
    bool NeedsProgram,
    bool NeedsAppSettings,
    bool NeedsReadme,
    bool NeedsDbContextWrapper,
    ScaffoldProvider? DetectedProvider,
    bool NeedsDbContextFactory);

/// Determines which scaffold pieces (.csproj, Program.cs, appsettings.json, README.md, a real
/// DbContext class) are missing from a project about to be downloaded, so ScaffoldGenerator can
/// fill in only what's absent and never overwrite a file the user already has.
public static class ScaffoldPlanner
{
    public static ScaffoldPlan Plan(
        string configSource,
        IReadOnlyDictionary<string, byte[]>? passthroughFiles)
    {
        var files = passthroughFiles ?? new Dictionary<string, byte[]>();

        var csprojEntry = files.FirstOrDefault(kvp =>
            kvp.Key.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase));
        var needsCsproj = csprojEntry.Key is null;

        var needsProgram = !files.Keys.Any(path =>
            FileNameOf(path).Equals("Program.cs", StringComparison.OrdinalIgnoreCase));
        var needsAppSettings = !files.Keys.Any(path =>
            FileNameOf(path).Equals("appsettings.json", StringComparison.OrdinalIgnoreCase));
        var needsReadme = !files.Keys.Any(path =>
            FileNameOf(path).Equals("README.md", StringComparison.OrdinalIgnoreCase));

        var needsDbContextWrapper = !HasAnyTypeDeclaration(configSource);

        var needsDbContextFactory = !files.Keys.Any(path =>
            FileNameOf(path).Equals("AppDbContextFactory.cs", StringComparison.OrdinalIgnoreCase));

        ScaffoldProvider? detectedProvider = null;
        if (!needsCsproj)
        {
            var csprojText = Encoding.UTF8.GetString(csprojEntry.Value);
            detectedProvider = DetectProvider(csprojText);
        }

        return new ScaffoldPlan(
            needsCsproj, needsProgram, needsAppSettings, needsReadme, needsDbContextWrapper, detectedProvider,
            needsDbContextFactory);
    }

    private static string FileNameOf(string path)
    {
        var slash = path.LastIndexOfAny(new[] { '/', '\\' });
        return slash >= 0 ? path[(slash + 1)..] : path;
    }

    /// True when configSource already contains ANY type declaration (a real DbContext, an
    /// IEntityTypeConfiguration class, anything) — meaning it's already structured C# and must
    /// not be indented into a synthesized OnModelCreating body. Only bare top-level statements
    /// (the "paste fluent config directly" shape) need wrapping.
    private static bool HasAnyTypeDeclaration(string configSource)
    {
        if (string.IsNullOrWhiteSpace(configSource))
        {
            return false;
        }

        var root = CSharpSyntaxTree.ParseText(configSource).GetCompilationUnitRoot();

        return root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>().Any();
    }

    private static ScaffoldProvider? DetectProvider(string csprojText)
    {
        if (csprojText.Contains("Microsoft.EntityFrameworkCore.SqlServer", StringComparison.OrdinalIgnoreCase))
        {
            return ScaffoldProvider.SqlServer;
        }

        if (csprojText.Contains("Npgsql.EntityFrameworkCore.PostgreSQL", StringComparison.OrdinalIgnoreCase))
        {
            return ScaffoldProvider.PostgreSql;
        }

        if (csprojText.Contains("Microsoft.EntityFrameworkCore.Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            return ScaffoldProvider.Sqlite;
        }

        return null;
    }
}
