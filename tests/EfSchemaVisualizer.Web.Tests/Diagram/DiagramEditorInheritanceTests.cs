using EfSchemaVisualizer.Core.Model;
using EfSchemaVisualizer.Web.Diagram;
using Xunit;

namespace EfSchemaVisualizer.Web.Tests.Diagram;

public class DiagramEditorInheritanceTests
{
    private const string ClassSource = """
        public class Person
        {
            public int Id { get; set; }
            public string Name { get; set; }
        }

        public class Student : Person
        {
            public string Course { get; set; }
        }
        """;

    private const string ConfigSource = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
            }
        }
        """;

    [Fact]
    public void RenameProperty_InheritedPropertyViewedFromDerivedEntity_RenamesItOnTheBaseClass()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);

        var result = editor.RenameProperty("Student", "Name", "FullName");

        Assert.True(result.Success);
        Assert.Contains("public string FullName { get; set; }", editor.ClassSource);
        Assert.DoesNotContain("public string Name { get; set; }", editor.ClassSource);
        Assert.Contains(editor.Current.Entities.Single(e => e.Name == "Student").Properties, p => p.Name == "FullName");
    }

    [Fact]
    public void ChangePropertyType_InheritedPropertyViewedFromDerivedEntity_ChangesItOnTheBaseClass()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);

        var result = editor.ChangePropertyType("Student", "Name", "string", newIsNullable: true);

        Assert.True(result.Success);
        Assert.Contains("public string? Name { get; set; }", editor.ClassSource);
    }

    [Fact]
    public void RemoveProperty_InheritedPropertyViewedFromDerivedEntity_RemovesItFromTheBaseClass()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);

        var result = editor.RemoveProperty("Student", "Name");

        Assert.True(result.Success);
        Assert.DoesNotContain("public string Name { get; set; }", editor.ClassSource);
        Assert.DoesNotContain(editor.Current.Entities.Single(e => e.Name == "Student").Properties, p => p.Name == "Name");
        Assert.DoesNotContain(editor.Current.Entities.Single(e => e.Name == "Person").Properties, p => p.Name == "Name");
    }

    [Fact]
    public void RenameProperty_OwnPropertyOnDerivedEntity_StillWorksUnaffected()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);

        var result = editor.RenameProperty("Student", "Course", "Class");

        Assert.True(result.Success);
        Assert.Contains("public string Class { get; set; }", editor.ClassSource);
    }

    [Fact]
    public void SetMaxLength_InheritedPropertyViewedFromDerivedEntity_WritesConfigUnderTheBaseEntityScope()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);

        var result = editor.SetMaxLength("Student", "Name", 50);

        Assert.True(result.Success);
        Assert.Contains("modelBuilder.Entity<Person>", editor.ConfigSource);
        Assert.Contains("HasMaxLength(50)", editor.ConfigSource);
        Assert.DoesNotContain("modelBuilder.Entity<Student>", editor.ConfigSource);
    }

    [Fact]
    public void ToggleKey_InheritedPropertyViewedFromDerivedEntity_WritesHasKeyUnderTheBaseEntityScope()
    {
        const string classSourceNoOwnKey = """
            public class Person
            {
                public string Ssn { get; set; }
            }

            public class Student : Person
            {
                public string Course { get; set; }
            }
            """;

        var editor = new DiagramEditor(classSourceNoOwnKey, ConfigSource);

        var result = editor.ToggleKey("Student", "Ssn", isKey: true);

        Assert.True(result.Success);
        Assert.Contains("modelBuilder.Entity<Person>", editor.ConfigSource);
        Assert.Contains("HasKey", editor.ConfigSource);
    }

    [Fact]
    public void SetColumnName_OwnPropertyOnDerivedEntity_StillWritesUnderTheDerivedEntityScope()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);

        var result = editor.SetColumnName("Student", "Course", "class_name");

        Assert.True(result.Success);
        Assert.Contains("modelBuilder.Entity<Student>", editor.ConfigSource);
        Assert.Contains("class_name", editor.ConfigSource);
    }

    private const string TptClassSource = """
        public class Person
        {
            public int Id { get; set; }
            public string Name { get; set; } = null!;
        }

        public class Student : Person
        {
            public string Course { get; set; } = null!;
        }
        """;
    private const string TptConfigSource = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.HasKey(e => e.Id);
                });
            }
        }
        """;

    [Fact]
    public void SetMappingStrategy_Tpt_UpdatesEveryEntityInHierarchy()
    {
        var editor = new DiagramEditor(TptClassSource, TptConfigSource);

        var result = editor.SetMappingStrategy("Student", MappingStrategy.Tpt);

        Assert.True(result.Success);
        Assert.Equal(MappingStrategy.Tpt, editor.Current.Entities.Single(e => e.Name == "Person").MappingStrategy);
        Assert.Equal(MappingStrategy.Tpt, editor.Current.Entities.Single(e => e.Name == "Student").MappingStrategy);
    }

    [Fact]
    public void SetMappingStrategy_BlockedWhenDiscriminatorConfigured()
    {
        const string configWithDiscriminator = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Person>(entity =>
                    {
                        entity.HasKey(e => e.Id);
                        entity.HasDiscriminator<string>("Type").HasValue<Student>("S");
                    });
                }
            }
            """;
        var editor = new DiagramEditor(TptClassSource, configWithDiscriminator);

        var result = editor.SetMappingStrategy("Person", MappingStrategy.Tpt);

        Assert.False(result.Success);
        Assert.Equal(MappingStrategy.Tph, editor.Current.Entities.Single(e => e.Name == "Person").MappingStrategy);
    }

    [Fact]
    public void SetDiscriminatorColumn_ThenSetDiscriminatorValue_RoundTrips()
    {
        var editor = new DiagramEditor(TptClassSource, TptConfigSource);

        var columnResult = editor.SetDiscriminatorColumn("Person", "Type", null);
        Assert.True(columnResult.Success);
        Assert.Equal("Type", editor.Current.Entities.Single(e => e.Name == "Person").DiscriminatorPropertyName);
        Assert.Equal("string", editor.Current.Entities.Single(e => e.Name == "Person").DiscriminatorClrType);

        var valueResult = editor.SetDiscriminatorValue("Student", "S");
        Assert.True(valueResult.Success);
        Assert.Equal("\"S\"", editor.Current.Entities.Single(e => e.Name == "Student").DiscriminatorValue);
    }

    [Fact]
    public void SetDiscriminatorColumn_BlockedWhenStrategyIsNotTph()
    {
        var editor = new DiagramEditor(TptClassSource, TptConfigSource);
        editor.SetMappingStrategy("Person", MappingStrategy.Tpt);

        var result = editor.SetDiscriminatorColumn("Person", "Type", null);

        Assert.False(result.Success);
    }

    [Fact]
    public void SetDiscriminatorValue_NoColumnConfiguredYet_Fails()
    {
        var editor = new DiagramEditor(TptClassSource, TptConfigSource);

        var result = editor.SetDiscriminatorValue("Student", "S");

        Assert.False(result.Success);
    }

    // A UseTptMappingStrategy() call declared on a DERIVED entity's own Entity<Student>(...) scope is
    // technically valid EF configuration today (InheritanceInference.Fold tolerates it, resolving the
    // strategy from wherever it's found in the hierarchy). SetMappingStrategy must still normalize it
    // away from every hierarchy member -- not just the root -- when switching strategies, or it either
    // silently no-ops (switching to Tph) or leaves both an old and a new call present across the
    // hierarchy (switching to Tpc), which would trip InconsistentMappingStrategyInHierarchy on reparse.
    private const string TptConfigSourceStrategyOnDerivedEntity = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.HasKey(e => e.Id);
                });
                modelBuilder.Entity<Student>(entity =>
                {
                    entity.UseTptMappingStrategy();
                });
            }
        }
        """;

    [Fact]
    public void SetMappingStrategy_ToTpc_RemovesStaleStrategyCallDeclaredOnDerivedEntity()
    {
        var editor = new DiagramEditor(TptClassSource, TptConfigSourceStrategyOnDerivedEntity);

        var result = editor.SetMappingStrategy("Person", MappingStrategy.Tpc);

        Assert.True(result.Success);
        Assert.DoesNotContain("UseTptMappingStrategy", editor.ConfigSource);
        Assert.Equal(MappingStrategy.Tpc, editor.Current.Entities.Single(e => e.Name == "Person").MappingStrategy);
        Assert.Equal(MappingStrategy.Tpc, editor.Current.Entities.Single(e => e.Name == "Student").MappingStrategy);
    }

    [Fact]
    public void SetMappingStrategy_ToTph_RemovesStaleStrategyCallDeclaredOnDerivedEntity()
    {
        var editor = new DiagramEditor(TptClassSource, TptConfigSourceStrategyOnDerivedEntity);

        var result = editor.SetMappingStrategy("Person", MappingStrategy.Tph);

        Assert.True(result.Success);
        Assert.DoesNotContain("UseTptMappingStrategy", editor.ConfigSource);
        Assert.Equal(MappingStrategy.Tph, editor.Current.Entities.Single(e => e.Name == "Person").MappingStrategy);
        Assert.Equal(MappingStrategy.Tph, editor.Current.Entities.Single(e => e.Name == "Student").MappingStrategy);
    }

    [Fact]
    public void RemoveDiscriminatorValue_ClearsJustThatEntity_LeavesOthersIntact()
    {
        const string configWithTwoValues = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Person>(entity =>
                    {
                        entity.HasKey(e => e.Id);
                        entity.HasDiscriminator<string>("Type").HasValue<Student>("S").HasValue<Teacher>("T");
                    });
                }
            }
            """;
        const string threeLevelClassSource = """
            public class Person
            {
                public int Id { get; set; }
            }

            public class Student : Person
            {
                public string Course { get; set; } = null!;
            }

            public class Teacher : Person
            {
                public string Salary { get; set; } = null!;
            }
            """;
        var editor = new DiagramEditor(threeLevelClassSource, configWithTwoValues);

        var result = editor.RemoveDiscriminatorValue("Student");

        Assert.True(result.Success);
        Assert.Null(editor.Current.Entities.Single(e => e.Name == "Student").DiscriminatorValue);
        Assert.Equal("\"T\"", editor.Current.Entities.Single(e => e.Name == "Teacher").DiscriminatorValue);
    }
}
