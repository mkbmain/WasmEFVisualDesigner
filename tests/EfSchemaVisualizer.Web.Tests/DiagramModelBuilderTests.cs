using System.Linq;
using EfSchemaVisualizer.Web;

namespace EfSchemaVisualizer.Web.Tests;

public class DiagramModelBuilderTests
{
    [Fact]
    public void Build_FluentMaxLengthAndAnnotationMaxLengthOnSameProperty_FluentWins()
    {
        const string classSource = """
            public class Person
            {
                public int Id { get; set; }

                [MaxLength(50)]
                public string Name { get; set; }
            }
            """;

        const string configSource = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Person>(entity =>
                    {
                        entity.Property(e => e.Name).HasMaxLength(100);
                    });
                }
            }
            """;

        var result = DiagramModelBuilder.Build(classSource, configSource);

        var name = result.Entities.Single().Properties.Single(p => p.Name == "Name");
        Assert.Equal(100, name.MaxLength);
    }

    [Fact]
    public void Build_AnnotationOnlyMaxLength_NoFluentConfig_AnnotationValueUsed()
    {
        const string classSource = """
            public class Person
            {
                public int Id { get; set; }

                [MaxLength(50)]
                public string Name { get; set; }
            }
            """;

        const string configSource = """
            public class AppDbContext : DbContext
            {
            }
            """;

        var result = DiagramModelBuilder.Build(classSource, configSource);

        var name = result.Entities.Single().Properties.Single(p => p.Name == "Name");
        Assert.Equal(50, name.MaxLength);
    }

    [Fact]
    public void Build_FluentRelationshipAndAnnotationForeignKeyForSamePair_FluentWins()
    {
        const string classSource = """
            public class Blog
            {
                public int Id { get; set; }
                public ICollection<Post> Posts { get; set; }
            }

            public class Post
            {
                public int Id { get; set; }
                public int BlogId { get; set; }

                [ForeignKey("BlogId")]
                public Blog Blog { get; set; }
            }
            """;

        const string configSource = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Post>(entity =>
                    {
                        entity.HasOne(p => p.Blog)
                            .WithMany(b => b.Posts)
                            .HasForeignKey(p => p.BlogId)
                            .OnDelete(DeleteBehavior.Cascade);
                    });
                }
            }
            """;

        var result = DiagramModelBuilder.Build(classSource, configSource);

        var relationship = Assert.Single(result.Relationships);
        Assert.Equal("Cascade", relationship.OnDeleteBehavior); // only the fluent parse reads OnDelete; proves fluent's config survived, not the annotation's
    }

    [Fact]
    public void Build_AnnotationOnlyForeignKey_NoFluentConfig_RelationshipStillProduced()
    {
        const string classSource = """
            public class Blog
            {
                public int Id { get; set; }
            }

            public class Post
            {
                public int Id { get; set; }
                public int BlogId { get; set; }

                [ForeignKey("BlogId")]
                public Blog Blog { get; set; }
            }
            """;

        const string configSource = """
            public class AppDbContext : DbContext
            {
            }
            """;

        var result = DiagramModelBuilder.Build(classSource, configSource);

        var relationship = Assert.Single(result.Relationships);
        Assert.Equal("Blog", relationship.PrincipalEntity);
        Assert.Equal("Post", relationship.DependentEntity);
    }
}
