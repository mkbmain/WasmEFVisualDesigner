using System.IO.Compression;
using System.Text.Json;

namespace EfSchemaVisualizer.Core.Archive;

public static class ProjectArchiveWriter
{
    public const string LayoutFileName = "diagram-layout.json";

    public static byte[] Write(string classSource, string configSource, IReadOnlyDictionary<string, EntityPosition>? layout = null)
    {
        using var stream = new MemoryStream();

        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(zip, "Entities.cs", classSource);
            WriteEntry(zip, "DbContext.cs", configSource);

            if (layout is { Count: > 0 })
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
}
