namespace EfSchemaVisualizer.Core.Archive;

/// Path-based triage for zip entries that are never diagram-worthy source: build output
/// (regenerable, safe to drop outright) versus EF-generated/migration files (must survive
/// download for `dotnet ef` to keep working, but must not render as diagram entities).
public static class ArchivePathFilter
{
    public static bool IsBuildArtifact(string path)
    {
        var segments = path.Split('/');
        return segments.Any(s => s.Equals("bin", StringComparison.OrdinalIgnoreCase)
            || s.Equals("obj", StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsGeneratedOrMigration(string path)
    {
        var segments = path.Split('/');
        if (segments.Any(s => s.Equals("Migrations", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var fileName = segments[^1];
        return fileName.EndsWith("ModelSnapshot.cs", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase);
    }
}
