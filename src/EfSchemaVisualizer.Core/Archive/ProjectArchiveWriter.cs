using System.IO.Compression;

namespace EfSchemaVisualizer.Core.Archive;

public static class ProjectArchiveWriter
{
    public static byte[] Write(string classSource, string configSource)
    {
        using var stream = new MemoryStream();

        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(zip, "Entities.cs", classSource);
            WriteEntry(zip, "DbContext.cs", configSource);
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
