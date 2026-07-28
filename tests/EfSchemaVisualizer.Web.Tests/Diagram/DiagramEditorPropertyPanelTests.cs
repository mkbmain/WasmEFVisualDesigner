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

    private const string KeylessClassSource = """
        public class Person
        {
            public int Id { get; set; }
        }
        """;

    private const string KeylessConfigSource = """
        modelBuilder.Entity<Person>(entity =>
        {
            entity.HasNoKey();
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

    [Fact]
    public void SetRelationshipShape_SettingConstraintName_WritesHasConstraintNameCall()
    {
        var editor = new DiagramEditor(RelationshipClassSource, RelationshipConfigSource);
        var relationship = editor.Current.Relationships.Single();

        var result = editor.SetRelationshipShape(
            relationship, relationship.Kind, relationship.ForeignKeyProperties, relationship.OnDeleteBehavior, "FK_Post_Blog");

        Assert.True(result.Success);
        Assert.Equal("FK_Post_Blog", editor.Current.Relationships.Single().ConstraintName);
        Assert.Contains("HasConstraintName(\"FK_Post_Blog\")", editor.ConfigSource);
    }

    [Fact]
    public void SetRelationshipShape_SameConstraintName_IsNoOp()
    {
        var editor = new DiagramEditor(RelationshipClassSource, RelationshipConfigSource);
        var relationship = editor.Current.Relationships.Single();
        editor.SetRelationshipShape(relationship, relationship.Kind, relationship.ForeignKeyProperties, relationship.OnDeleteBehavior, "FK_Post_Blog");
        var updated = editor.Current.Relationships.Single();
        var configSourceBefore = editor.ConfigSource;

        var result = editor.SetRelationshipShape(updated, updated.Kind, updated.ForeignKeyProperties, updated.OnDeleteBehavior, updated.ConstraintName);

        Assert.True(result.Success);
        Assert.Equal(configSourceBefore, editor.ConfigSource);
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

    [Fact]
    public void SetKeyName_NoExistingName_WritesHasNameCall()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);

        var result = editor.SetKeyName("Person", "PK_Person");

        Assert.True(result.Success);
        Assert.Equal("PK_Person", editor.Current.Entities.Single().KeyName);
        Assert.Contains("HasName(\"PK_Person\")", editor.ConfigSource);
    }

    [Fact]
    public void SetKeyName_ClearingExistingName_RemovesHasNameCall()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);
        editor.SetKeyName("Person", "PK_Person");

        var result = editor.SetKeyName("Person", null);

        Assert.True(result.Success);
        Assert.Null(editor.Current.Entities.Single().KeyName);
        Assert.DoesNotContain("HasName", editor.ConfigSource);
    }

    [Fact]
    public void SetKeyName_SameName_IsNoOp()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);
        editor.SetKeyName("Person", "PK_Person");
        var configSourceBefore = editor.ConfigSource;

        var result = editor.SetKeyName("Person", "PK_Person");

        Assert.True(result.Success);
        Assert.Equal(configSourceBefore, editor.ConfigSource);
    }

    [Fact]
    public void SetKeyName_UnknownEntity_Fails()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);

        var result = editor.SetKeyName("DoesNotExist", "PK_Foo");

        Assert.False(result.Success);
    }

    [Fact]
    public void SetKeyName_EntityHasNoKey_Fails()
    {
        var editor = new DiagramEditor(KeylessClassSource, KeylessConfigSource);

        var result = editor.SetKeyName("Person", "PK_Person");

        Assert.False(result.Success);
    }

    [Fact]
    public void SetComputedColumnSql_NoExistingConfig_InsertsHasComputedColumnSql()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);

        var result = editor.SetComputedColumnSql("Person", "Name", "UPPER([Name])", true);

        Assert.True(result.Success);
        var property = editor.Current.Entities.Single().Properties.Single(p => p.Name == "Name");
        Assert.Equal("UPPER([Name])", property.ComputedColumnSql);
        Assert.True(property.ComputedColumnSqlIsStored);
        Assert.Contains("HasComputedColumnSql(\"UPPER([Name])\", true)", editor.ConfigSource);
    }

    [Fact]
    public void SetComputedColumnSql_ClearingExistingConfig_RemovesHasComputedColumnSql()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);
        editor.SetComputedColumnSql("Person", "Name", "UPPER([Name])", true);

        var result = editor.SetComputedColumnSql("Person", "Name", null, null);

        Assert.True(result.Success);
        Assert.Null(editor.Current.Entities.Single().Properties.Single(p => p.Name == "Name").ComputedColumnSql);
        Assert.DoesNotContain("HasComputedColumnSql", editor.ConfigSource);
    }

    [Fact]
    public void AddCheckConstraint_NewName_AddsToEntity()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);

        var result = editor.AddCheckConstraint("Person", "CK_Person_Name", "LEN([Name]) > 0");

        Assert.True(result.Success);
        var constraint = editor.Current.Entities.Single().CheckConstraints.Single();
        Assert.Equal("CK_Person_Name", constraint.Name);
        Assert.Equal("LEN([Name]) > 0", constraint.Sql);
    }

    [Fact]
    public void AddCheckConstraint_DuplicateName_Fails()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);
        editor.AddCheckConstraint("Person", "CK_Person_Name", "LEN([Name]) > 0");

        var result = editor.AddCheckConstraint("Person", "CK_Person_Name", "1 = 1");

        Assert.False(result.Success);
        Assert.Single(editor.Current.Entities.Single().CheckConstraints);
    }

    [Fact]
    public void RemoveCheckConstraint_ExistingName_RemovesIt()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);
        editor.AddCheckConstraint("Person", "CK_Person_Name", "LEN([Name]) > 0");

        var result = editor.RemoveCheckConstraint("Person", "CK_Person_Name");

        Assert.True(result.Success);
        Assert.Empty(editor.Current.Entities.Single().CheckConstraints);
    }

    [Fact]
    public void SetCheckConstraint_RenamesAndUpdatesSql()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);
        editor.AddCheckConstraint("Person", "CK_Person_Name", "LEN([Name]) > 0");

        var result = editor.SetCheckConstraint("Person", "CK_Person_Name", "CK_Person_NonEmptyName", "LEN([Name]) >= 1");

        Assert.True(result.Success);
        var constraint = editor.Current.Entities.Single().CheckConstraints.Single();
        Assert.Equal("CK_Person_NonEmptyName", constraint.Name);
        Assert.Equal("LEN([Name]) >= 1", constraint.Sql);
    }

    [Fact]
    public void SetCheckConstraint_DuplicateNewName_Fails()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);
        editor.AddCheckConstraint("Person", "CK_Person_Name", "LEN([Name]) > 0");
        editor.AddCheckConstraint("Person", "CK_Person_Email", "LEN([Email]) > 0");

        var result = editor.SetCheckConstraint("Person", "CK_Person_Name", "CK_Person_Email", "LEN([Name]) > 0");

        Assert.False(result.Success);
        var constraints = editor.Current.Entities.Single().CheckConstraints;
        Assert.Equal(2, constraints.Count);
        Assert.Single(constraints, c => c.Name == "CK_Person_Name");
        Assert.Single(constraints, c => c.Name == "CK_Person_Email");
    }

    [Fact]
    public void AddSequence_NewName_AddsToModel()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);

        var result = editor.AddSequence("PersonIds", schema: null, clrType: "int", startsAt: 1, incrementsBy: null, minValue: null, maxValue: null, isCyclic: null);

        Assert.True(result.Success);
        var sequence = editor.Current.Sequences.Single();
        Assert.Equal("PersonIds", sequence.Name);
        Assert.Equal(1, sequence.StartsAt);
    }

    [Fact]
    public void AddSequence_DuplicateName_Fails()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);
        editor.AddSequence("PersonIds", null, "int", 1, null, null, null, null);

        var result = editor.AddSequence("PersonIds", null, "int", 2, null, null, null, null);

        Assert.False(result.Success);
        Assert.Single(editor.Current.Sequences);
    }

    [Fact]
    public void RemoveSequence_ExistingName_RemovesIt()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);
        editor.AddSequence("PersonIds", null, "int", 1, null, null, null, null);

        var result = editor.RemoveSequence("PersonIds");

        Assert.True(result.Success);
        Assert.Empty(editor.Current.Sequences);
    }

    [Fact]
    public void SetUseSequence_NoExistingConfig_LinksPropertyToSequence()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);
        editor.AddSequence("PersonIds", "shared", "int", null, null, null, null, null);

        var result = editor.SetUseSequence("Person", "Id", "PersonIds", "shared");

        Assert.True(result.Success);
        var property = editor.Current.Entities.Single().Properties.Single(p => p.Name == "Id");
        Assert.Equal("PersonIds", property.SequenceName);
        Assert.Equal("shared", property.SequenceSchema);
    }

    [Fact]
    public void SetUseSequence_ClearingExistingConfig_RemovesUseSequence()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);
        editor.AddSequence("PersonIds", "shared", "int", null, null, null, null, null);
        editor.SetUseSequence("Person", "Id", "PersonIds", "shared");

        var result = editor.SetUseSequence("Person", "Id", null, null);

        Assert.True(result.Success);
        Assert.Null(editor.Current.Entities.Single().Properties.Single(p => p.Name == "Id").SequenceName);
    }

    [Fact]
    public void SetUseSequence_SameSequenceWithEmptyStringSchema_WhenSchemaIsNull_IsNoOp()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);
        editor.AddSequence("PersonIds", null, "int", null, null, null, null, null);
        editor.SetUseSequence("Person", "Id", "PersonIds", null);
        var configSourceBefore = editor.ConfigSource;

        var result = editor.SetUseSequence("Person", "Id", "PersonIds", "");

        Assert.True(result.Success);
        Assert.Equal(configSourceBefore, editor.ConfigSource);
    }
}
