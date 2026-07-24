using System.Linq;
using EfSchemaVisualizer.Web.Diagram;

namespace EfSchemaVisualizer.Web.Tests.Diagram;

public class DiagramEditorPropertyPanelTests
{
    private const string ClassSource = """
        public class Person
        {
            public int Id { get; set; }
            public string Name { get; set; } = "";
        }
        """;

    private const string ConfigSource = """
        modelBuilder.Entity<Person>(entity =>
        {
            entity.HasKey(e => e.Id);
        });
        """;

    [Fact]
    public void SetMaxLength_NoExistingConfig_InsertsHasMaxLength()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);

        var result = editor.SetMaxLength("Person", "Name", 100);

        Assert.True(result.Success);
        Assert.Equal(100, editor.Current.Entities.Single().Properties.Single(p => p.Name == "Name").MaxLength);
        Assert.Contains("HasMaxLength(100)", editor.ConfigSource);
    }

    [Fact]
    public void SetMaxLength_ClearingExistingConfig_RemovesHasMaxLength()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);
        editor.SetMaxLength("Person", "Name", 100);

        var result = editor.SetMaxLength("Person", "Name", null);

        Assert.True(result.Success);
        Assert.Null(editor.Current.Entities.Single().Properties.Single(p => p.Name == "Name").MaxLength);
        Assert.DoesNotContain("HasMaxLength", editor.ConfigSource);
    }

    [Fact]
    public void SetMaxLength_NonPositiveValue_Fails()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);

        var result = editor.SetMaxLength("Person", "Name", 0);

        Assert.False(result.Success);
    }

    [Fact]
    public void SetDefaultValue_StringPropertyWithUnquotedText_AutoQuotesTheLiteral()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);

        var result = editor.SetDefaultValue("Person", "Name", "Unknown");

        Assert.True(result.Success);
        Assert.Equal("\"Unknown\"", editor.Current.Entities.Single().Properties.Single(p => p.Name == "Name").DefaultValueLiteral);
        Assert.Contains("HasDefaultValue(\"Unknown\")", editor.ConfigSource);
    }

    [Fact]
    public void SetDefaultValue_StringPropertyWithAlreadyQuotedText_DoesNotDoubleQuote()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);

        var result = editor.SetDefaultValue("Person", "Name", "\"Unknown\"");

        Assert.True(result.Success);
        Assert.Contains("HasDefaultValue(\"Unknown\")", editor.ConfigSource);
        Assert.DoesNotContain("\"\\\"Unknown\\\"\"", editor.ConfigSource);
    }

    [Fact]
    public void SetDefaultValue_NumericPropertyWithPlainNumber_PassesThroughUnquoted()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);

        var result = editor.SetDefaultValue("Person", "Id", "1");

        Assert.True(result.Success);
        Assert.Equal("1", editor.Current.Entities.Single().Properties.Single(p => p.Name == "Id").DefaultValueLiteral);
        Assert.Contains("HasDefaultValue(1)", editor.ConfigSource);
    }

    [Fact]
    public void SetDefaultValue_NumericPropertyWithNonLiteralExpression_FailsWithGuidanceTowardSqlField()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);

        var result = editor.SetDefaultValue("Person", "Id", "GetNextId()");

        Assert.False(result.Success);
        Assert.Contains("Default value SQL", result.Error);
    }

    [Fact]
    public void SetDefaultValueSql_NoExistingConfig_InsertsHasDefaultValueSql()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);

        var result = editor.SetDefaultValueSql("Person", "Name", "GETDATE()");

        Assert.True(result.Success);
        Assert.Equal("GETDATE()", editor.Current.Entities.Single().Properties.Single(p => p.Name == "Name").DefaultValueSql);
        Assert.Contains("HasDefaultValueSql(\"GETDATE()\")", editor.ConfigSource);
    }

    [Fact]
    public void SetDefaultValueSql_ClearingExistingConfig_RemovesHasDefaultValueSql()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);
        editor.SetDefaultValueSql("Person", "Name", "GETDATE()");

        var result = editor.SetDefaultValueSql("Person", "Name", null);

        Assert.True(result.Success);
        Assert.Null(editor.Current.Entities.Single().Properties.Single(p => p.Name == "Name").DefaultValueSql);
        Assert.DoesNotContain("HasDefaultValueSql", editor.ConfigSource);
    }

    [Fact]
    public void SetRequiredOverride_SetToTrue_InsertsIsRequired()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);

        var result = editor.SetRequiredOverride("Person", "Name", true);

        Assert.True(result.Success);
        Assert.True(editor.Current.Entities.Single().Properties.Single(p => p.Name == "Name").IsRequiredOverride);
        Assert.Contains("IsRequired()", editor.ConfigSource);
    }

    [Fact]
    public void SetRequiredOverride_ClearingExistingOverride_RemovesIsRequired()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);
        editor.SetRequiredOverride("Person", "Name", false);

        var result = editor.SetRequiredOverride("Person", "Name", null);

        Assert.True(result.Success);
        Assert.Null(editor.Current.Entities.Single().Properties.Single(p => p.Name == "Name").IsRequiredOverride);
        Assert.DoesNotContain("IsRequired", editor.ConfigSource);
    }

    [Fact]
    public void SetRowVersion_SetToTrue_InsertsIsRowVersion()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);

        var result = editor.SetRowVersion("Person", "Name", true);

        Assert.True(result.Success);
        Assert.True(editor.Current.Entities.Single().Properties.Single(p => p.Name == "Name").IsRowVersion);
        Assert.Contains("IsRowVersion()", editor.ConfigSource);
    }

    [Fact]
    public void SetRowVersion_SetToFalse_WhenAlreadyFalse_IsNoOp()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);

        var result = editor.SetRowVersion("Person", "Name", false);

        Assert.True(result.Success);
        Assert.DoesNotContain("IsRowVersion", editor.ConfigSource);
    }

    [Fact]
    public void SetRowVersion_ClearingExistingFlag_RemovesIsRowVersion()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);
        editor.SetRowVersion("Person", "Name", true);

        var result = editor.SetRowVersion("Person", "Name", false);

        Assert.True(result.Success);
        Assert.False(editor.Current.Entities.Single().Properties.Single(p => p.Name == "Name").IsRowVersion);
        Assert.DoesNotContain("IsRowVersion", editor.ConfigSource);
    }

    [Fact]
    public void SetRowVersion_ClearingAttributeSourcedFlag_FailsWithClearMessage()
    {
        const string classSourceWithTimestamp = """
            public class Person
            {
                public int Id { get; set; }
                [Timestamp]
                public byte[] Name { get; set; } = System.Array.Empty<byte>();
            }
            """;

        var editor = new DiagramEditor(classSourceWithTimestamp, ConfigSource);

        var result = editor.SetRowVersion("Person", "Name", false);

        Assert.False(result.Success);
        Assert.True(editor.Current.Entities.Single().Properties.Single(p => p.Name == "Name").IsRowVersion);
    }

    [Fact]
    public void SetRowVersion_UnknownEntity_Fails()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);

        var result = editor.SetRowVersion("DoesNotExist", "Name", true);

        Assert.False(result.Success);
    }

    [Fact]
    public void SetConcurrencyToken_SetToTrue_InsertsIsConcurrencyToken()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);

        var result = editor.SetConcurrencyToken("Person", "Name", true);

        Assert.True(result.Success);
        Assert.True(editor.Current.Entities.Single().Properties.Single(p => p.Name == "Name").IsConcurrencyToken);
        Assert.Contains("IsConcurrencyToken()", editor.ConfigSource);
    }

    [Fact]
    public void SetConcurrencyToken_ClearingExistingFlag_RemovesIsConcurrencyToken()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);
        editor.SetConcurrencyToken("Person", "Name", true);

        var result = editor.SetConcurrencyToken("Person", "Name", false);

        Assert.True(result.Success);
        Assert.False(editor.Current.Entities.Single().Properties.Single(p => p.Name == "Name").IsConcurrencyToken);
        Assert.DoesNotContain("IsConcurrencyToken", editor.ConfigSource);
    }

    [Fact]
    public void SetConcurrencyToken_ClearingAttributeSourcedFlag_FailsWithClearMessage()
    {
        const string classSourceWithConcurrencyCheck = """
            public class Person
            {
                public int Id { get; set; }
                [ConcurrencyCheck]
                public string Name { get; set; } = "";
            }
            """;

        var editor = new DiagramEditor(classSourceWithConcurrencyCheck, ConfigSource);

        var result = editor.SetConcurrencyToken("Person", "Name", false);

        Assert.False(result.Success);
        Assert.True(editor.Current.Entities.Single().Properties.Single(p => p.Name == "Name").IsConcurrencyToken);
    }

    [Fact]
    public void SetConcurrencyToken_UnknownProperty_Fails()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);

        var result = editor.SetConcurrencyToken("Person", "DoesNotExist", true);

        Assert.False(result.Success);
    }

    private const string RelationshipClassSource = """
        public class Blog
        {
            public int Id { get; set; }
            public ICollection<Post> Posts { get; set; } = new List<Post>();
        }

        public class Post
        {
            public int Id { get; set; }
            public int BlogId { get; set; }
            public Blog Blog { get; set; } = null!;
        }
        """;

    private const string RelationshipConfigSource = """
        modelBuilder.Entity<Post>(entity =>
        {
            entity.HasOne(p => p.Blog)
                .WithMany(b => b.Posts)
                .HasForeignKey(p => p.BlogId);
        });
        """;

    [Fact]
    public void SetRelationshipShape_SettingOnDeleteBehavior_WritesOnDeleteCall()
    {
        var editor = new DiagramEditor(RelationshipClassSource, RelationshipConfigSource);
        var relationship = editor.Current.Relationships.Single();

        var result = editor.SetRelationshipShape(relationship, relationship.Kind, relationship.ForeignKeyProperties, "Cascade");

        Assert.True(result.Success);
        Assert.Equal("Cascade", editor.Current.Relationships.Single().OnDeleteBehavior);
        Assert.Contains("OnDelete(DeleteBehavior.Cascade)", editor.ConfigSource);
    }

    [Fact]
    public void SetRelationshipShape_SameKindFkAndOnDelete_IsNoOp()
    {
        var editor = new DiagramEditor(RelationshipClassSource, RelationshipConfigSource);
        var relationship = editor.Current.Relationships.Single();
        var configSourceBefore = editor.ConfigSource;

        var result = editor.SetRelationshipShape(relationship, relationship.Kind, relationship.ForeignKeyProperties, relationship.OnDeleteBehavior);

        Assert.True(result.Success);
        Assert.Equal(configSourceBefore, editor.ConfigSource);
        Assert.False(editor.CanUndo);
    }

    private const string InferredRelationshipClassSource = """
        public class Blog
        {
            public int Id { get; set; }
            public ICollection<Post> Posts { get; set; } = new List<Post>();
        }

        public class Post
        {
            public int Id { get; set; }
            public int BlogId { get; set; }
            public Blog Blog { get; set; } = null!;
        }
        """;

    private const string EmptyConfigSource = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
            }
        }
        """;

    [Fact]
    public void SetRelationshipShape_OnInferredRelationship_MaterializesExplicitConfig()
    {
        var editor = new DiagramEditor(InferredRelationshipClassSource, EmptyConfigSource);
        var relationship = editor.Current.Relationships.Single();
        Assert.True(relationship.IsInferred);

        var result = editor.SetRelationshipShape(relationship, relationship.Kind, relationship.ForeignKeyProperties, "Cascade");

        Assert.True(result.Success);
        var updated = editor.Current.Relationships.Single();
        Assert.False(updated.IsInferred);
        Assert.Equal("Cascade", updated.OnDeleteBehavior);
        Assert.Contains("OnDelete(DeleteBehavior.Cascade)", editor.ConfigSource);
    }

    [Fact]
    public void RemoveRelationship_OnInferredRelationship_FailsWithClearMessage()
    {
        var editor = new DiagramEditor(InferredRelationshipClassSource, EmptyConfigSource);
        var relationship = editor.Current.Relationships.Single();

        var result = editor.RemoveRelationship(relationship);

        Assert.False(result.Success);
        Assert.Contains("inferred from naming convention", result.Error);
    }

    [Fact]
    public void AddAlternateKey_NewProperty_InsertsHasAlternateKey()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);

        var result = editor.AddAlternateKey("Person", "Name");

        Assert.True(result.Success);
        var alternateKey = Assert.Single(editor.Current.Entities.Single().AlternateKeys);
        Assert.Equal(new[] { "Name" }, alternateKey);
        Assert.Contains("HasAlternateKey(e => e.Name)", editor.ConfigSource);
    }

    [Fact]
    public void AddAlternateKey_AlreadyExists_Fails()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);
        editor.AddAlternateKey("Person", "Name");

        var result = editor.AddAlternateKey("Person", "Name");

        Assert.False(result.Success);
    }

    [Fact]
    public void AddAlternateKey_UnknownEntity_Fails()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);

        var result = editor.AddAlternateKey("Unknown", "Name");

        Assert.False(result.Success);
    }

    [Fact]
    public void AddAlternateKey_UnknownProperty_Fails()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);

        var result = editor.AddAlternateKey("Person", "Unknown");

        Assert.False(result.Success);
    }

    [Fact]
    public void ToggleAlternateKeyMembership_AddSecondPropertyToExistingKey_MakesItComposite()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);
        editor.AddAlternateKey("Person", "Name");

        var result = editor.ToggleAlternateKeyMembership("Person", new[] { "Name" }, "Id", include: true);

        Assert.True(result.Success);
        var alternateKey = Assert.Single(editor.Current.Entities.Single().AlternateKeys);
        Assert.Equal(new[] { "Name", "Id" }, alternateKey);
    }

    [Fact]
    public void ToggleAlternateKeyMembership_RemoveOnlyMemberProperty_RemovesTheAlternateKeyEntirely()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);
        editor.AddAlternateKey("Person", "Name");

        var result = editor.ToggleAlternateKeyMembership("Person", new[] { "Name" }, "Name", include: false);

        Assert.True(result.Success);
        Assert.Empty(editor.Current.Entities.Single().AlternateKeys);
    }

    [Fact]
    public void RemoveAlternateKey_Existing_RemovesIt()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);
        editor.AddAlternateKey("Person", "Name");

        var result = editor.RemoveAlternateKey("Person", new[] { "Name" });

        Assert.True(result.Success);
        Assert.Empty(editor.Current.Entities.Single().AlternateKeys);
    }

    [Fact]
    public void RemoveAlternateKey_NotFound_Fails()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);

        var result = editor.RemoveAlternateKey("Person", new[] { "Name" });

        Assert.False(result.Success);
    }
}
