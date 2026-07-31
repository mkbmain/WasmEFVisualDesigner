using System.Collections.Generic;
using System.Linq;
using System.Text;
using EfSchemaVisualizer.Core.Model;

namespace EfSchemaVisualizer.Core.Archive;

/// Fills in whatever scaffold pieces a ScaffoldPlan marked missing. Never regenerates or modifies
/// a file that already exists — see ScaffoldPlanner for what "missing" means.
public static class ScaffoldGenerator
{
    public static string Pluralize(string name)
    {
        if (name.Length == 0)
        {
            return name;
        }

        var lower = name.ToLowerInvariant();
        if (lower.EndsWith("s") || lower.EndsWith("x") || lower.EndsWith("z")
            || lower.EndsWith("ch") || lower.EndsWith("sh"))
        {
            return name + "es";
        }

        if (lower.EndsWith("y") && name.Length > 1 && !IsVowel(lower[^2]))
        {
            return name[..^1] + "ies";
        }

        return name + "s";
    }

    private static bool IsVowel(char c) => "aeiou".IndexOf(c) >= 0;

    public static string BuildDbContextWrapper(
        string configSource,
        IReadOnlyList<EntityModel> entities,
        string projectName)
    {
        var dbSets = new StringBuilder();
        foreach (var entity in entities.Where(e => !e.IsOwned))
        {
            dbSets.AppendLine(
                $"    public DbSet<{entity.Name}> {Pluralize(entity.Name)} => Set<{entity.Name}>();");
        }

        var indentedConfig = IndentBody(configSource, "        ");

        return $$"""
            using Microsoft.EntityFrameworkCore;

            namespace {{projectName}};

            public class AppDbContext : DbContext
            {
                public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
                {
                }

            {{dbSets.ToString().TrimEnd('\r', '\n')}}

                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
            {{indentedConfig}}
                }
            }
            """;
    }

    private static string IndentBody(string source, string indent)
    {
        var lines = source.Replace("\r\n", "\n").Split('\n');
        return string.Join("\n", lines.Select(line => line.Length == 0 ? line : indent + line));
    }
}
