using System.IO.Compression;
using System.Text.Json;

namespace EfSchemaVisualizer.Core.Archive;

public static class ProjectArchiveWriter
{
    public const string LayoutFileName = "diagram-layout.json";

    public static byte[] Write(
        string classSource,
        string configSource,
        IReadOnlyDictionary<string, EntityPosition>? layout = null,
        IReadOnlyDictionary<string, string>? entityFileOrigins = null,
        IReadOnlyDictionary<string, string>? configFileOrigins = null,
        IReadOnlyDictionary<string, byte[]>? passthroughFiles = null)
    {
        using var stream = new MemoryStream();

        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var writtenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var classFilePath = SingleOriginPathOrDefault(entityFileOrigins) ?? "Entities.cs";
            WriteEntry(zip, classFilePath, classSource);
            writtenPaths.Add(classFilePath);

            var configFilePath = SingleOriginPathOrDefault(configFileOrigins) ?? "DbContext.cs";
            WriteEntry(zip, configFilePath, configSource);
            writtenPaths.Add(configFilePath);

            if (passthroughFiles is not null)
            {
                foreach (var (path, bytes) in passthroughFiles)
                {
                    // The chosen class/config paths always carry the current (possibly edited)
                    // source; a stale passthrough entry at the same path must not overwrite it.
                    if (writtenPaths.Add(path))
                    {
                        WriteEntry(zip, path, bytes);
                    }
                }
            }

            if (layout is { Count: > 0 } && writtenPaths.Add(LayoutFileName))
            {
                WriteEntry(zip, LayoutFileName, JsonSerializer.Serialize(layout));
            }
        }

        return stream.ToArray();
    }

    /// A project may have uploaded more than one class (or config) file; when it did, there is
    /// no single correct original path to write the merged, currently-edited source back to, so
    /// the caller falls back to the fixed default name instead of guessing.
    private static string? SingleOriginPathOrDefault(IReadOnlyDictionary<string, string>? origins)
    {
        if (origins is null || origins.Count == 0)
        {
            return null;
        }

        var distinctPaths = origins.Values.Distinct().ToList();
        return distinctPaths.Count == 1 ? distinctPaths[0] : null;
    }

    private static void WriteEntry(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }

    private static void WriteEntry(ZipArchive zip, string name, byte[] content)
    {
        var entry = zip.CreateEntry(name);
        using var entryStream = entry.Open();
        entryStream.Write(content, 0, content.Length);
    }
}
