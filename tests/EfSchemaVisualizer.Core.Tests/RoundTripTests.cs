using System.Linq;
using EfSchemaVisualizer.Core.CodeGen;
using EfSchemaVisualizer.Core.Merging;
using EfSchemaVisualizer.Core.Parsing;
using Xunit;

namespace EfSchemaVisualizer.Core.Tests;

public class RoundTripTests
{
    private const string EntitySource = """
        public class Person
        {
            public int Id { get; set; }
            public string Name { get; set; }
            public string? Email { get; set; }
        }
        """;

    private const string ContextSource = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.Property(e => e.Name).HasMaxLength(100);
                });

                modelBuilder.Entity<Address>(entity =>
                {
                    entity.Property(e => e.Line1).HasMaxLength(200);
                });
            }
        }
        """;

    [Fact]
    public void Parse_Merge_NoEdit_RegeneratesConfigIdenticalToOriginal()
    {
        var baseEntity = new EntityClassParser().Parse(EntitySource).Value.Single();
        var configs = new FluentConfigParser().ParseMaxLengths(ContextSource).Value;
        var merged = ModelMerger.ApplyMaxLengths(baseEntity, configs);

        Assert.Equal(100, merged.Properties.Single(p => p.Name == "Name").MaxLength);

        // Regenerating with the *same* value the model already holds must
        // produce byte-identical output to the original source.
        var nameProperty = merged.Properties.Single(p => p.Name == "Name");
        var regenerated = new OnModelCreatingRewriter()
            .RewriteMaxLength(ContextSource, "Person", "Name", nameProperty.MaxLength!.Value);

        Assert.Equal(ContextSource, regenerated);
    }

    [Fact]
    public void Parse_Edit_Regenerate_ChangesOnlyTheEditedProperty()
    {
        var configs = new FluentConfigParser().ParseMaxLengths(ContextSource).Value;
        var addressLine1 = configs.Single(c => c is { EntityName: "Address", PropertyName: "Line1" });

        var regenerated = new OnModelCreatingRewriter()
            .RewriteMaxLength(ContextSource, "Person", "Name", newMaxLength: 150);

        // The edited entity's call changed...
        Assert.Contains("entity.Property(e => e.Name).HasMaxLength(150)", regenerated);

        // ...but the untouched entity's config, parsed fresh from the
        // regenerated source, still reports its original value.
        var configsAfter = new FluentConfigParser().ParseMaxLengths(regenerated).Value;
        var addressLine1After = configsAfter.Single(c => c is { EntityName: "Address", PropertyName: "Line1" });
        Assert.Equal(addressLine1.MaxLength, addressLine1After.MaxLength);
    }
}
