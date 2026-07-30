using System.Collections.Generic;
using System.Linq;
using EfSchemaVisualizer.Core.Model;
using EfSchemaVisualizer.Core.Parsing;
using EfSchemaVisualizer.Web;
using Xunit;

namespace EfSchemaVisualizer.Web.Tests;

public class UsingEntitySharedTypeTests
{
    private const string ClassSource = """
        public class Post
        {
            public int Id { get; set; }
            public ICollection<Tag> Tags { get; set; }
        }

        public class Tag
        {
            public int Id { get; set; }
            public ICollection<Post> Posts { get; set; }
        }
        """;

    private const string ConfigSource = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Post>(entity =>
                {
                    entity.HasMany(p => p.Tags).WithMany(t => t.Posts).UsingEntity(
                        "PostTags",
                        right => right.HasOne<Tag>().WithMany().HasForeignKey("TagId"),
                        left => left.HasOne<Post>().WithMany().HasForeignKey("PostId"),
                        j => j.HasKey("PostId", "TagId"));
                });
            }
        }
        """;

    [Fact]
    public void Build_SharedTypeJoinEntity_IsSynthesizedWithKeyAndForeignKeyProperties()
    {
        var result = DiagramModelBuilder.Build(ClassSource, ConfigSource);

        var joinEntity = Assert.Single(result.Entities, e => e.Name == "PostTags");
        Assert.True(joinEntity.IsSharedType);
        Assert.Equal(new List<string> { "PostId", "TagId" }, joinEntity.KeyPropertyNames);
        Assert.Contains(joinEntity.Properties, p => p.Name == "PostId");
        Assert.Contains(joinEntity.Properties, p => p.Name == "TagId");

        Assert.DoesNotContain(result.Diagnostics, d => d.Code == DiagnosticCodes.EntityHasNoKey && d.EntityName == "PostTags");
    }

    [Fact]
    public void Build_SharedTypeJoinEntityWithNoExplicitKey_FiresEntityHasNoKey()
    {
        const string configWithoutKey = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Post>(entity =>
                    {
                        entity.HasMany(p => p.Tags).WithMany(t => t.Posts).UsingEntity("PostTags");
                    });
                }
            }
            """;

        var result = DiagramModelBuilder.Build(ClassSource, configWithoutKey);

        Assert.Contains(result.Diagnostics, d => d.Code == DiagnosticCodes.EntityHasNoKey && d.EntityName == "PostTags");
    }
}
