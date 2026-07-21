using System.Linq;
using EfSchemaVisualizer.Core.Parsing;
using EfSchemaVisualizer.Web;
using Xunit;

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

    [Fact]
    public void Build_IgnoredProperty_IsDroppedFromDiagram()
    {
        const string classSource = """
            public class Person
            {
                public int Id { get; set; }
                public string Notes { get; set; }
            }
            """;

        const string configSource = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Person>(entity =>
                    {
                        entity.Ignore(e => e.Notes);
                    });
                }
            }
            """;

        var result = DiagramModelBuilder.Build(classSource, configSource);

        var person = result.Entities.Single();
        Assert.DoesNotContain(person.Properties, p => p.Name == "Notes");
        Assert.Contains(person.Properties, p => p.Name == "Id");
    }

    [Fact]
    public void Build_IgnoredEntity_IsDroppedFromDiagram()
    {
        const string classSource = """
            public class Person
            {
                public int Id { get; set; }
            }

            public class AuditLog
            {
                public int Id { get; set; }
            }
            """;

        const string configSource = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Ignore<AuditLog>();
                }
            }
            """;

        var result = DiagramModelBuilder.Build(classSource, configSource);

        Assert.DoesNotContain(result.Entities, e => e.Name == "AuditLog");
        Assert.Contains(result.Entities, e => e.Name == "Person");
    }

    [Fact]
    public void Build_IgnoredEntity_DropsRelationshipsReferencingIt()
    {
        const string classSource = """
            public class Person
            {
                public int Id { get; set; }
                public List<AuditLog> Logs { get; set; }
            }

            public class AuditLog
            {
                public int Id { get; set; }
                public int PersonId { get; set; }
                public Person Person { get; set; }
            }
            """;

        const string configSource = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Person>(entity =>
                    {
                        entity.HasMany(p => p.Logs).WithOne(a => a.Person).HasForeignKey(a => a.PersonId);
                    });
                    modelBuilder.Ignore<AuditLog>();
                }
            }
            """;

        var result = DiagramModelBuilder.Build(classSource, configSource);

        Assert.Empty(result.Relationships);
    }

    [Fact]
    public void Build_IndexAttributeOnly_NoFluentConfig_AttributeIndexUsed()
    {
        const string classSource = """
            [Index(nameof(Email))]
            public class Person
            {
                public int Id { get; set; }
                public string Email { get; set; }
            }
            """;

        const string configSource = """
            public class AppDbContext : DbContext
            {
            }
            """;

        var result = DiagramModelBuilder.Build(classSource, configSource);

        var index = Assert.Single(result.Entities.Single().Indexes);
        Assert.Equal(new[] { "Email" }, index.PropertyNames);
    }

    [Fact]
    public void Build_FluentIndexAndAttributeIndexOnSameProperties_FluentWins()
    {
        const string classSource = """
            [Index(nameof(Email))]
            public class Person
            {
                public int Id { get; set; }
                public string Email { get; set; }
            }
            """;

        const string configSource = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Person>(entity =>
                    {
                        entity.HasIndex(e => e.Email).IsUnique();
                    });
                }
            }
            """;

        var result = DiagramModelBuilder.Build(classSource, configSource);

        var index = Assert.Single(result.Entities.Single().Indexes);
        Assert.True(index.IsUnique);
    }

    [Fact]
    public void Build_FluentIndexAndAttributeIndexOnDifferentProperties_BothPresent()
    {
        const string classSource = """
            [Index(nameof(LastName))]
            public class Person
            {
                public int Id { get; set; }
                public string Email { get; set; }
                public string LastName { get; set; }
            }
            """;

        const string configSource = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Person>(entity =>
                    {
                        entity.HasIndex(e => e.Email);
                    });
                }
            }
            """;

        var result = DiagramModelBuilder.Build(classSource, configSource);

        Assert.Equal(2, result.Entities.Single().Indexes.Count);
    }

    [Fact]
    public void Build_UseIdentityColumn_SetsValueGeneratedOnProperty()
    {
        const string classSource = """
            public class Person
            {
                public int Id { get; set; }
            }
            """;

        const string configSource = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Person>(entity =>
                    {
                        entity.Property(e => e.Id).UseIdentityColumn();
                    });
                }
            }
            """;

        var result = DiagramModelBuilder.Build(classSource, configSource);

        var id = result.Entities.Single().Properties.Single(p => p.Name == "Id");
        Assert.Equal("Identity", id.ValueGenerated);
    }

    [Fact]
    public void Build_IsRowVersionCall_SetsIsRowVersionOnProperty()
    {
        const string classSource = """
            public class Person
            {
                public int Id { get; set; }
                public byte[] RowVersion { get; set; } = System.Array.Empty<byte>();
            }
            """;

        const string configSource = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Person>(entity =>
                    {
                        entity.Property(e => e.RowVersion).IsRowVersion();
                    });
                }
            }
            """;

        var result = DiagramModelBuilder.Build(classSource, configSource);

        var rowVersion = result.Entities.Single().Properties.Single(p => p.Name == "RowVersion");
        Assert.True(rowVersion.IsRowVersion);
        Assert.False(rowVersion.IsConcurrencyToken);
    }

    [Fact]
    public void Build_TimestampAttribute_SetsIsRowVersionOnProperty()
    {
        const string classSource = """
            public class Person
            {
                public int Id { get; set; }
                [Timestamp]
                public byte[] RowVersion { get; set; } = System.Array.Empty<byte>();
            }
            """;

        const string configSource = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                }
            }
            """;

        var result = DiagramModelBuilder.Build(classSource, configSource);

        Assert.True(result.Entities.Single().Properties.Single(p => p.Name == "RowVersion").IsRowVersion);
    }

    [Fact]
    public void Build_IsRowVersionCallNotFlaggedAsUnrecognized()
    {
        const string classSource = """
            public class Person
            {
                public int Id { get; set; }
                public byte[] RowVersion { get; set; } = System.Array.Empty<byte>();
            }
            """;

        const string configSource = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Person>(entity =>
                    {
                        entity.Property(e => e.RowVersion).IsRowVersion();
                    });
                }
            }
            """;

        var result = DiagramModelBuilder.Build(classSource, configSource);

        Assert.DoesNotContain(result.Diagnostics, d => d.Code == DiagnosticCodes.UnrecognizedConfigCall);
    }

    [Fact]
    public void Build_ShadowProperty_AppearsAsIsShadowProperty()
    {
        const string classSource = """
            public class Person
            {
                public int Id { get; set; }
            }
            """;

        const string configSource = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Person>(entity =>
                    {
                        entity.Property<string>("CreatedBy");
                    });
                }
            }
            """;

        var result = DiagramModelBuilder.Build(classSource, configSource);

        var shadow = result.Entities.Single().Properties.Single(p => p.Name == "CreatedBy");
        Assert.True(shadow.IsShadow);
        Assert.Equal("string", shadow.ClrType);
    }

    [Fact]
    public void Build_ToViewCall_SetsViewNameAndSchema()
    {
        const string classSource = """
            public class PersonView
            {
                public string Name { get; set; }
            }
            """;

        const string configSource = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<PersonView>(entity =>
                    {
                        entity.ToView("vPeople", "dbo");
                    });
                }
            }
            """;

        var result = DiagramModelBuilder.Build(classSource, configSource);

        var entity = result.Entities.Single();
        Assert.Equal("vPeople", entity.ViewName);
        Assert.Equal("dbo", entity.Schema);
    }

    [Fact]
    public void Build_ToSqlQueryCall_SetsSqlQuery()
    {
        const string classSource = """
            public class PersonView
            {
                public string Name { get; set; }
            }
            """;

        const string configSource = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<PersonView>(entity =>
                    {
                        entity.ToSqlQuery("SELECT Name FROM People");
                    });
                }
            }
            """;

        var result = DiagramModelBuilder.Build(classSource, configSource);

        Assert.Equal("SELECT Name FROM People", result.Entities.Single().SqlQuery);
    }

    [Fact]
    public void Build_KeylessViaFluentHasNoKey_SetsIsKeylessTrue()
    {
        const string classSource = """
            public class PersonView
            {
                public string Name { get; set; }
            }
            """;

        const string configSource = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<PersonView>(entity =>
                    {
                        entity.HasNoKey();
                    });
                }
            }
            """;

        var result = DiagramModelBuilder.Build(classSource, configSource);

        Assert.True(result.Entities.Single().IsKeyless);
    }

    [Fact]
    public void Build_KeylessViaAttributeOnly_SetsIsKeylessTrue()
    {
        const string classSource = """
            [Keyless]
            public class PersonView
            {
                public string Name { get; set; }
            }
            """;

        const string configSource = """
            public class AppDbContext : DbContext
            {
            }
            """;

        var result = DiagramModelBuilder.Build(classSource, configSource);

        Assert.True(result.Entities.Single().IsKeyless);
    }

    [Fact]
    public void Build_KeylessViaBothAttributeAndFluent_StillJustTrue()
    {
        const string classSource = """
            [Keyless]
            public class PersonView
            {
                public string Name { get; set; }
            }
            """;

        const string configSource = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<PersonView>(entity =>
                    {
                        entity.HasNoKey();
                    });
                }
            }
            """;

        var result = DiagramModelBuilder.Build(classSource, configSource);

        Assert.True(result.Entities.Single().IsKeyless);
    }
}
