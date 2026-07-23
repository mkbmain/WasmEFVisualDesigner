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

            var classFiles = MultiFileSourceMerger.Split(
                classSource, entityFileOrigins ?? new Dictionary<string, string>(), "Entities.cs");
            foreach (var (path, content) in classFiles)
            {
                WriteEntry(zip, path, content);
                writtenPaths.Add(path);
            }

            var configFiles = MultiFileSourceMerger.Split(
                configSource, configFileOrigins ?? new Dictionary<string, string>(), "DbContext.cs");
            foreach (var (path, content) in configFiles)
            {
                WriteEntry(zip, path, content);
                writtenPaths.Add(path);
            }

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
