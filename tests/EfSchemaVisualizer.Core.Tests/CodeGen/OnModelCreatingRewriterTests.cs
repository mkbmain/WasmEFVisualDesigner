using System;
using System.Collections.Generic;
using System.Linq;
using EfSchemaVisualizer.Core.CodeGen;
using EfSchemaVisualizer.Core.Merging;
using EfSchemaVisualizer.Core.Model;
using EfSchemaVisualizer.Core.Parsing;
using Xunit;

namespace EfSchemaVisualizer.Core.Tests.CodeGen;

public class OnModelCreatingRewriterTests
{
    private const string Source = """
        public class AppDbContext : DbContext
        {
            // unrelated comment that must survive untouched
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.Property(e => e.Name).HasMaxLength(100);
                    entity.Property(e => e.Email).HasMaxLength(255);
                });

                modelBuilder.Entity<Address>(entity =>
                {
                    entity.Property(e => e.Line1).HasMaxLength(200);
                });
            }
        }
        """;

    [Fact]
    public void RewriteMaxLength_ChangesOnlyTargetedCall_LeavesEverythingElseIdentical()
    {
        var result = new OnModelCreatingRewriter()
            .RewriteMaxLength(Source, entityName: "Person", propertyName: "Name", newMaxLength: 150);

        Assert.Contains("entity.Property(e => e.Name).HasMaxLength(150)", result);

        // Untouched: Person.Email, Address.Line1, and the unrelated comment.
        Assert.Contains("entity.Property(e => e.Email).HasMaxLength(255)", result);
        Assert.Contains("entity.Property(e => e.Line1).HasMaxLength(200)", result);
        Assert.Contains("// unrelated comment that must survive untouched", result);

        Assert.DoesNotContain("HasMaxLength(100)", result);
    }

    [Fact]
    public void RewriteMaxLength_UnknownEntity_InsertsNewEntityBlock()
    {
        var result = new OnModelCreatingRewriter()
            .RewriteMaxLength(Source, entityName: "Vehicle", propertyName: "Name", newMaxLength: 10);

        Assert.Contains("modelBuilder.Entity<Vehicle>(entity =>", result);
        Assert.Contains("entity.Property(e => e.Name).HasMaxLength(10)", result);

        var configs = new FluentConfigParser().ParseMaxLengths(result).Value;
        Assert.Contains(configs, c => c is { EntityName: "Vehicle", PropertyName: "Name", MaxLength: 10 });
        Assert.Contains(configs, c => c is { EntityName: "Person", PropertyName: "Name", MaxLength: 100 });
        Assert.Contains(configs, c => c is { EntityName: "Address", PropertyName: "Line1", MaxLength: 200 });
    }

    private const string SourceWithRenamedBuilderAndNoConfig = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder builder)
            {
                builder.Entity<Person>(entity =>
                {
                    entity.Property(e => e.Name).HasMaxLength(100);
                });
            }
        }
        """;

    [Fact]
    public void RewriteMaxLength_UnknownEntity_RenamedModelBuilderParameter_UsesSameReceiverName()
    {
        var result = new OnModelCreatingRewriter()
            .RewriteMaxLength(SourceWithRenamedBuilderAndNoConfig, entityName: "Vehicle", propertyName: "Name", newMaxLength: 10);

        Assert.Contains("builder.Entity<Vehicle>(entity =>", result);
        Assert.DoesNotContain("modelBuilder.Entity<Vehicle>", result);
    }

    private const string SourceWithoutOnModelCreating = """
        public class AppDbContext : DbContext
        {
        }
        """;

    [Fact]
    public void RewriteMaxLength_OnModelCreatingMissing_Throws()
    {
        var rewriter = new OnModelCreatingRewriter();

        Assert.Throws<InvalidOperationException>(() =>
            rewriter.RewriteMaxLength(SourceWithoutOnModelCreating, entityName: "Vehicle", propertyName: "Name", newMaxLength: 10));
    }

    private const string SourceWithUnconfiguredProperty = """
        public class AppDbContext : DbContext
        {
            // unrelated comment that must survive untouched
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.Property(e => e.Name).HasMaxLength(100);
                    entity.Property(e => e.Email);
                });

                modelBuilder.Entity<Address>(entity =>
                {
                    entity.Property(e => e.Line1).HasMaxLength(200);
                });
            }
        }
        """;

    [Fact]
    public void RewriteMaxLength_PropertyExistsWithoutHasMaxLength_AppendsHasMaxLengthCall()
    {
        var result = new OnModelCreatingRewriter()
            .RewriteMaxLength(SourceWithUnconfiguredProperty, entityName: "Person", propertyName: "Email", newMaxLength: 50);

        Assert.Contains("entity.Property(e => e.Email).HasMaxLength(50)", result);
        Assert.Contains("// unrelated comment that must survive untouched", result);

        var configs = new FluentConfigParser().ParseMaxLengths(result).Value;
        Assert.Contains(configs, c => c is { EntityName: "Person", PropertyName: "Name", MaxLength: 100 });
        Assert.Contains(configs, c => c is { EntityName: "Person", PropertyName: "Email", MaxLength: 50 });
        Assert.Contains(configs, c => c is { EntityName: "Address", PropertyName: "Line1", MaxLength: 200 });
    }

    private const string SourceWithNestedEntityConfig = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.Property(e => e.Name).HasMaxLength(100);

                    modelBuilder.Entity<Address>(nested =>
                    {
                        nested.Property(e => e.Line1).HasMaxLength(200);
                    });
                });
            }
        }
        """;

    [Fact]
    public void RewriteMaxLength_PropertyOnlyPresentInNestedConfig_InsertsNewStatementIntoOuterScope()
    {
        var rewriter = new OnModelCreatingRewriter();

        // Person has no Line1 property in this shape - Line1 belongs to the nested Address config.
        // RewriteMaxLength is purely syntactic (it doesn't cross-check property names against a
        // parsed EntityModel), so it inserts a new statement into Person's own scope rather than
        // reaching into the nested Address block.
        var result = rewriter.RewriteMaxLength(
            SourceWithNestedEntityConfig, entityName: "Person", propertyName: "Line1", newMaxLength: 999);

        Assert.Contains("entity.Property(e => e.Line1).HasMaxLength(999)", result);

        var configs = new FluentConfigParser().ParseMaxLengths(result).Value;
        Assert.Contains(configs, c => c is { EntityName: "Person", PropertyName: "Line1", MaxLength: 999 });
        Assert.Contains(configs, c => c is { EntityName: "Address", PropertyName: "Line1", MaxLength: 200 });
    }

    private const string SourceWithMissingPropertyMention = """
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

    [Fact]
    public void RewriteMaxLength_PropertyNeverMentioned_InsertsNewStatementAtEndOfBlock()
    {
        var result = new OnModelCreatingRewriter()
            .RewriteMaxLength(SourceWithMissingPropertyMention, entityName: "Person", propertyName: "Email", newMaxLength: 75);

        Assert.Contains("entity.Property(e => e.Email).HasMaxLength(75)", result);

        var configs = new FluentConfigParser().ParseMaxLengths(result).Value;
        Assert.Contains(configs, c => c is { EntityName: "Person", PropertyName: "Name", MaxLength: 100 });
        Assert.Contains(configs, c => c is { EntityName: "Person", PropertyName: "Email", MaxLength: 75 });
    }

    private const string SourceWithNonDefaultLambdaParam = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.Property(x => x.Name).HasMaxLength(100);
                });
            }
        }
        """;

    [Fact]
    public void RewriteMaxLength_PropertyNeverMentioned_MatchesSiblingLambdaParameterName()
    {
        var result = new OnModelCreatingRewriter()
            .RewriteMaxLength(SourceWithNonDefaultLambdaParam, entityName: "Person", propertyName: "Email", newMaxLength: 75);

        Assert.Contains("entity.Property(x => x.Email).HasMaxLength(75)", result);
    }

    private const string SourceWithEmptyEntityBlock = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                });
            }
        }
        """;

    [Fact]
    public void RewriteMaxLength_EmptyEntityBlock_FallsBackToDefaultLambdaParameterName()
    {
        var result = new OnModelCreatingRewriter()
            .RewriteMaxLength(SourceWithEmptyEntityBlock, entityName: "Person", propertyName: "Name", newMaxLength: 40);

        Assert.Contains("entity.Property(e => e.Name).HasMaxLength(40)", result);
    }

    private const string SourceWithIsRequiredCalls = """
        public class AppDbContext : DbContext
        {
            // unrelated comment that must survive untouched
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.Property(e => e.Name).IsRequired();
                    entity.Property(e => e.Email).IsRequired(false);
                });

                modelBuilder.Entity<Address>(entity =>
                {
                    entity.Property(e => e.Line1).IsRequired();
                });
            }
        }
        """;

    [Fact]
    public void RewriteIsRequired_ExistingBareCall_MutatesToFalse()
    {
        var result = new OnModelCreatingRewriter()
            .RewriteIsRequired(SourceWithIsRequiredCalls, entityName: "Person", propertyName: "Name", newIsRequired: false);

        Assert.Contains("entity.Property(e => e.Name).IsRequired(false)", result);

        // Untouched: Person.Email, Address.Line1, and the unrelated comment.
        Assert.Contains("entity.Property(e => e.Email).IsRequired(false)", result);
        Assert.Contains("entity.Property(e => e.Line1).IsRequired()", result);
        Assert.Contains("// unrelated comment that must survive untouched", result);
    }

    [Fact]
    public void RewriteIsRequired_ExistingExplicitFalseCall_MutatesToBareTrue()
    {
        var result = new OnModelCreatingRewriter()
            .RewriteIsRequired(SourceWithIsRequiredCalls, entityName: "Person", propertyName: "Email", newIsRequired: true);

        Assert.Contains("entity.Property(e => e.Email).IsRequired()", result);
        Assert.DoesNotContain("IsRequired(false)", result);
    }

    private const string SourceWithUnconfiguredIsRequiredProperty = """
        public class AppDbContext : DbContext
        {
            // unrelated comment that must survive untouched
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.Property(e => e.Name).IsRequired();
                    entity.Property(e => e.Email);
                });
            }
        }
        """;

    [Fact]
    public void RewriteIsRequired_PropertyExistsWithoutIsRequired_AppendsBareIsRequiredCall()
    {
        var result = new OnModelCreatingRewriter()
            .RewriteIsRequired(SourceWithUnconfiguredIsRequiredProperty, entityName: "Person", propertyName: "Email", newIsRequired: true);

        Assert.Contains("entity.Property(e => e.Email).IsRequired()", result);
        Assert.Contains("// unrelated comment that must survive untouched", result);

        var configs = new FluentConfigParser().ParseIsRequired(result).Value;
        Assert.Contains(configs, c => c is { EntityName: "Person", PropertyName: "Name", IsRequired: true });
        Assert.Contains(configs, c => c is { EntityName: "Person", PropertyName: "Email", IsRequired: true });
    }

    [Fact]
    public void RewriteIsRequired_PropertyExistsWithoutIsRequired_AppendsExplicitFalseCall()
    {
        var result = new OnModelCreatingRewriter()
            .RewriteIsRequired(SourceWithUnconfiguredIsRequiredProperty, entityName: "Person", propertyName: "Email", newIsRequired: false);

        Assert.Contains("entity.Property(e => e.Email).IsRequired(false)", result);
    }

    [Fact]
    public void RewriteIsRequired_UnknownEntity_InsertsNewEntityBlock()
    {
        var result = new OnModelCreatingRewriter()
            .RewriteIsRequired(SourceWithIsRequiredCalls, entityName: "Vehicle", propertyName: "Name", newIsRequired: true);

        Assert.Contains("modelBuilder.Entity<Vehicle>(entity =>", result);
        Assert.Contains("entity.Property(e => e.Name).IsRequired()", result);

        var configs = new FluentConfigParser().ParseIsRequired(result).Value;
        Assert.Contains(configs, c => c is { EntityName: "Vehicle", PropertyName: "Name", IsRequired: true });
        Assert.Contains(configs, c => c is { EntityName: "Person", PropertyName: "Name", IsRequired: true });
    }

    private const string SourceWithMissingIsRequiredPropertyMention = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.Property(e => e.Name).IsRequired();
                });
            }
        }
        """;

    [Fact]
    public void RewriteIsRequired_PropertyNeverMentioned_InsertsNewStatementAtEndOfBlock()
    {
        var result = new OnModelCreatingRewriter()
            .RewriteIsRequired(SourceWithMissingIsRequiredPropertyMention, entityName: "Person", propertyName: "Email", newIsRequired: false);

        Assert.Contains("entity.Property(e => e.Email).IsRequired(false)", result);

        var configs = new FluentConfigParser().ParseIsRequired(result).Value;
        Assert.Contains(configs, c => c is { EntityName: "Person", PropertyName: "Name", IsRequired: true });
        Assert.Contains(configs, c => c is { EntityName: "Person", PropertyName: "Email", IsRequired: false });
    }

    [Fact]
    public void RemoveIsRequired_ExistingCall_StripsIsRequiredLeavesBarePropertyCall()
    {
        var result = new OnModelCreatingRewriter()
            .RemoveIsRequired(SourceWithIsRequiredCalls, entityName: "Person", propertyName: "Name");

        Assert.Contains("entity.Property(e => e.Name);", result);
        Assert.DoesNotContain("e.Name).IsRequired()", result);

        // Untouched: Person.Email, Address.Line1.
        Assert.Contains("entity.Property(e => e.Email).IsRequired(false)", result);
        Assert.Contains("entity.Property(e => e.Line1).IsRequired()", result);
    }

    [Fact]
    public void RemoveIsRequired_NoMatchingCall_ReturnsSourceUnchanged()
    {
        var result = new OnModelCreatingRewriter()
            .RemoveIsRequired(SourceWithIsRequiredCalls, entityName: "Person", propertyName: "DoesNotExist");

        Assert.Equal(SourceWithIsRequiredCalls, result);
    }

    [Fact]
    public void RemoveIsRequired_EntityHasNoConfigAtAll_ReturnsSourceUnchanged()
    {
        var result = new OnModelCreatingRewriter()
            .RemoveIsRequired(SourceWithIsRequiredCalls, entityName: "Vehicle", propertyName: "Name");

        Assert.Equal(SourceWithIsRequiredCalls, result);
    }

    private const string SourceWithSingleKey = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.HasKey(e => e.Id);
                    entity.Property(e => e.Name).HasMaxLength(100);
                });
            }
        }
        """;

    [Fact]
    public void SetKey_ExistingSingleKey_MutatesToComposite()
    {
        var result = new OnModelCreatingRewriter()
            .SetKey(SourceWithSingleKey, entityName: "Person", propertyNames: new List<string> { "TenantId", "Id" });

        Assert.Contains("entity.HasKey(e => new { e.TenantId, e.Id })", result);
        Assert.DoesNotContain("entity.HasKey(e => e.Id)", result);
        Assert.Contains("entity.Property(e => e.Name).HasMaxLength(100)", result);
    }

    [Fact]
    public void SetKey_ExistingSingleKey_MutatesToDifferentSingleProperty()
    {
        var result = new OnModelCreatingRewriter()
            .SetKey(SourceWithSingleKey, entityName: "Person", propertyNames: new List<string> { "Guid" });

        Assert.Contains("entity.HasKey(e => e.Guid)", result);
        Assert.DoesNotContain("entity.HasKey(e => e.Id)", result);
    }

    private const string SourceWithEntityConfiguredNoKey = """
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

    [Fact]
    public void SetKey_EntityConfiguredWithoutHasKey_InsertsStatementAtEndOfBlock()
    {
        var result = new OnModelCreatingRewriter()
            .SetKey(SourceWithEntityConfiguredNoKey, entityName: "Person", propertyNames: new List<string> { "Id" });

        Assert.Contains("entity.HasKey(e => e.Id)", result);
        Assert.Contains("entity.Property(e => e.Name).HasMaxLength(100)", result);

        var configs = new FluentConfigParser().ParseKeys(result).Value;
        Assert.Contains(configs, c => c.EntityName == "Person" && c.PropertyNames.SequenceEqual(new[] { "Id" }));
    }

    [Fact]
    public void SetKey_UnknownEntity_InsertsNewEntityBlock()
    {
        var result = new OnModelCreatingRewriter()
            .SetKey(SourceWithSingleKey, entityName: "Vehicle", propertyNames: new List<string> { "Vin" });

        Assert.Contains("modelBuilder.Entity<Vehicle>", result);
        Assert.Contains("entity.HasKey(e => e.Vin)", result);

        var configs = new FluentConfigParser().ParseKeys(result).Value;
        Assert.Contains(configs, c => c.EntityName == "Vehicle" && c.PropertyNames.SequenceEqual(new[] { "Vin" }));
        Assert.Contains(configs, c => c.EntityName == "Person" && c.PropertyNames.SequenceEqual(new[] { "Id" }));
    }

    [Fact]
    public void RemoveKey_ExistingCall_RemovesHasKeyStatementEntirely()
    {
        var result = new OnModelCreatingRewriter()
            .RemoveKey(SourceWithSingleKey, entityName: "Person");

        Assert.DoesNotContain("HasKey", result);
        Assert.Contains("entity.Property(e => e.Name).HasMaxLength(100)", result);
    }

    [Fact]
    public void RemoveKey_NoMatchingCall_ReturnsSourceUnchanged()
    {
        var result = new OnModelCreatingRewriter()
            .RemoveKey(SourceWithEntityConfiguredNoKey, entityName: "Person");

        Assert.Equal(SourceWithEntityConfiguredNoKey, result);
    }

    [Fact]
    public void RemoveKey_EntityHasNoConfigAtAll_ReturnsSourceUnchanged()
    {
        var result = new OnModelCreatingRewriter()
            .RemoveKey(SourceWithSingleKey, entityName: "Vehicle");

        Assert.Equal(SourceWithSingleKey, result);
    }

    [Fact]
    public void RemoveMaxLength_ExistingCall_StripsHasMaxLengthLeavesBarePropertyCall()
    {
        var result = new OnModelCreatingRewriter()
            .RemoveMaxLength(Source, entityName: "Person", propertyName: "Name");

        Assert.Contains("entity.Property(e => e.Name);", result);
        Assert.DoesNotContain("HasMaxLength(100)", result);

        // Untouched: Person.Email, Address.Line1.
        Assert.Contains("entity.Property(e => e.Email).HasMaxLength(255)", result);
        Assert.Contains("entity.Property(e => e.Line1).HasMaxLength(200)", result);
    }

    [Fact]
    public void RemoveMaxLength_NoMatchingCall_ReturnsSourceUnchanged()
    {
        var result = new OnModelCreatingRewriter()
            .RemoveMaxLength(Source, entityName: "Person", propertyName: "DoesNotExist");

        Assert.Equal(Source, result);
    }

    [Fact]
    public void RemoveMaxLength_EntityHasNoConfigAtAll_ReturnsSourceUnchanged()
    {
        var result = new OnModelCreatingRewriter()
            .RemoveMaxLength(Source, entityName: "Vehicle", propertyName: "Name");

        Assert.Equal(Source, result);
    }

    [Fact]
    public void RemoveMaxLength_MultiEntitySource_OnlyStripsTargetEntitysCall()
    {
        var result = new OnModelCreatingRewriter()
            .RemoveMaxLength(Source, entityName: "Address", propertyName: "Line1");

        Assert.Contains("entity.Property(e => e.Line1);", result);
        Assert.DoesNotContain("HasMaxLength(200)", result);

        // Person's calls are untouched.
        Assert.Contains("entity.Property(e => e.Name).HasMaxLength(100)", result);
        Assert.Contains("entity.Property(e => e.Email).HasMaxLength(255)", result);
    }

    private const string SourceWithEntityConfigOnly = """
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

    [Fact]
    public void RenameEntityReferences_EntityConfigOnly_RenamesGenericTypeArgument()
    {
        var result = new OnModelCreatingRewriter()
            .RenameEntityReferences(SourceWithEntityConfigOnly, oldEntityName: "Person", newEntityName: "Customer");

        Assert.Contains("modelBuilder.Entity<Customer>(entity =>", result);
        Assert.DoesNotContain("Entity<Person>", result);
    }

    private const string SourceWithDbSetOnly = """
        public class AppDbContext : DbContext
        {
            public DbSet<Person> People { get; set; }
        }
        """;

    [Fact]
    public void RenameEntityReferences_DbSetOnly_RenamesGenericTypeArgument()
    {
        var result = new OnModelCreatingRewriter()
            .RenameEntityReferences(SourceWithDbSetOnly, oldEntityName: "Person", newEntityName: "Customer");

        Assert.Contains("public DbSet<Customer> People { get; set; }", result);
    }

    private const string SourceWithDbSetAndEntityConfig = """
        public class AppDbContext : DbContext
        {
            public DbSet<Person> People { get; set; }
            public DbSet<Address> Addresses { get; set; }

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
    public void RenameEntityReferences_BothEntityConfigAndDbSetPresent_RenamesBothInOnePass()
    {
        var result = new OnModelCreatingRewriter()
            .RenameEntityReferences(SourceWithDbSetAndEntityConfig, oldEntityName: "Person", newEntityName: "Customer");

        Assert.Contains("public DbSet<Customer> People { get; set; }", result);
        Assert.Contains("modelBuilder.Entity<Customer>(entity =>", result);
    }

    [Fact]
    public void RenameEntityReferences_NoMatchingReferences_ReturnsSourceUnchanged()
    {
        var result = new OnModelCreatingRewriter()
            .RenameEntityReferences(SourceWithDbSetAndEntityConfig, oldEntityName: "Vehicle", newEntityName: "Car");

        Assert.Equal(SourceWithDbSetAndEntityConfig, result);
    }

    [Fact]
    public void RenameEntityReferences_MultiEntitySource_SiblingEntityReferencesUntouched()
    {
        var result = new OnModelCreatingRewriter()
            .RenameEntityReferences(SourceWithDbSetAndEntityConfig, oldEntityName: "Person", newEntityName: "Customer");

        Assert.Contains("public DbSet<Address> Addresses { get; set; }", result);
        Assert.Contains("modelBuilder.Entity<Address>(entity =>", result);
        Assert.Contains("entity.Property(e => e.Line1).HasMaxLength(200)", result);
    }

    [Fact]
    public void RenamePropertyReferences_ExpressionBodiedLambda_RenamesMemberAccess()
    {
        var result = new OnModelCreatingRewriter()
            .RenamePropertyReferences(Source, entityName: "Person", oldPropertyName: "Name", newPropertyName: "FullName");

        Assert.Contains("entity.Property(e => e.FullName).HasMaxLength(100)", result);
        Assert.Contains("entity.Property(e => e.Email).HasMaxLength(255)", result);
    }

    private const string SourceWithBlockBodiedPropertyLambda = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.Property(e =>
                    {
                        return e.Name;
                    }).HasMaxLength(100);
                });
            }
        }
        """;

    [Fact]
    public void RenamePropertyReferences_BlockBodiedLambda_RenamesReturnedMemberAccess()
    {
        var result = new OnModelCreatingRewriter()
            .RenamePropertyReferences(SourceWithBlockBodiedPropertyLambda, entityName: "Person", oldPropertyName: "Name", newPropertyName: "FullName");

        Assert.Contains("return e.FullName;", result);
        Assert.DoesNotContain("return e.Name;", result);
    }

    private const string SourceWithStringOverload = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.Property("Name").HasMaxLength(100);
                });
            }
        }
        """;

    [Fact]
    public void RenamePropertyReferences_StringOverload_RenamesLiteral()
    {
        var result = new OnModelCreatingRewriter()
            .RenamePropertyReferences(SourceWithStringOverload, entityName: "Person", oldPropertyName: "Name", newPropertyName: "FullName");

        Assert.Contains("entity.Property(\"FullName\").HasMaxLength(100)", result);
    }

    [Fact]
    public void RenamePropertyReferences_NoMatchingCall_ReturnsSourceUnchanged()
    {
        var result = new OnModelCreatingRewriter()
            .RenamePropertyReferences(Source, entityName: "Person", oldPropertyName: "DoesNotExist", newPropertyName: "Whatever");

        Assert.Equal(Source, result);
    }

    [Fact]
    public void RenamePropertyReferences_MultiEntitySource_OnlyRenamesTargetEntitysProperty()
    {
        var result = new OnModelCreatingRewriter()
            .RenamePropertyReferences(Source, entityName: "Person", oldPropertyName: "Name", newPropertyName: "FullName");

        Assert.Contains("entity.Property(e => e.FullName).HasMaxLength(100)", result);
        Assert.Contains("entity.Property(e => e.Line1).HasMaxLength(200)", result);
    }

    private const string SourceWithExistingEntityForAddEntity = """
        public class AppDbContext : DbContext
        {
            public DbSet<Person> People { get; set; }

            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.Property(e => e.Name).HasMaxLength(100);
                });
            }
        }
        """;

    [Fact]
    public void AddEntity_ExistingEntityPresent_AppendsNewDbSetAndEmptyEntityBlock()
    {
        var result = new OnModelCreatingRewriter()
            .AddEntity(SourceWithExistingEntityForAddEntity, entityName: "Address", dbSetPropertyName: "Addresses");

        Assert.Contains("public DbSet<Address> Addresses { get; set; }", result);
        Assert.Contains("modelBuilder.Entity<Address>(entity =>", result);

        // Existing entity untouched.
        Assert.Contains("public DbSet<Person> People { get; set; }", result);
        Assert.Contains("entity.Property(e => e.Name).HasMaxLength(100)", result);
    }

    [Fact]
    public void AddEntity_NoOnModelCreatingMethod_Throws()
    {
        var rewriter = new OnModelCreatingRewriter();

        Assert.Throws<InvalidOperationException>(() =>
            rewriter.AddEntity(SourceWithDbSetOnly, entityName: "Address", dbSetPropertyName: "Addresses"));
    }

    private const string BareFluentConfigSourceWithNonDefaultReceiver = """
        builder.Entity<Blog>(entity =>
        {
            entity.Property(e => e.Title).HasMaxLength(100);
        });
        """;

    [Fact]
    public void AddEntity_BareFluentConfigSourceWithNonDefaultReceiverName_UsesExistingReceiverNotModelBuilder()
    {
        var result = new OnModelCreatingRewriter()
            .AddEntity(BareFluentConfigSourceWithNonDefaultReceiver, entityName: "Address", dbSetPropertyName: "Addresses");

        Assert.Contains("builder.Entity<Address>(entity =>", result);
        Assert.DoesNotContain("modelBuilder.Entity<Address>", result);

        // Existing statement untouched.
        Assert.Contains("builder.Entity<Blog>(entity =>", result);
    }

    [Fact]
    public void RemoveEntity_DbSetOnly_RemovesProperty()
    {
        var result = new OnModelCreatingRewriter().RemoveEntity(SourceWithDbSetOnly, entityName: "Person");

        Assert.DoesNotContain("DbSet<Person>", result);
    }

    [Fact]
    public void RemoveEntity_EntityConfigOnly_RemovesStatement()
    {
        var result = new OnModelCreatingRewriter().RemoveEntity(SourceWithEntityConfigOnly, entityName: "Person");

        Assert.DoesNotContain("Entity<Person>", result);
    }

    [Fact]
    public void RemoveEntity_BothDbSetAndEntityConfigPresent_RemovesBothInOnePass()
    {
        var result = new OnModelCreatingRewriter().RemoveEntity(SourceWithDbSetAndEntityConfig, entityName: "Person");

        Assert.DoesNotContain("DbSet<Person>", result);
        Assert.DoesNotContain("Entity<Person>", result);

        // Sibling untouched.
        Assert.Contains("public DbSet<Address> Addresses { get; set; }", result);
        Assert.Contains("modelBuilder.Entity<Address>(entity =>", result);
    }

    [Fact]
    public void RemoveEntity_NoMatchingReferences_ReturnsSourceUnchanged()
    {
        var result = new OnModelCreatingRewriter().RemoveEntity(SourceWithDbSetAndEntityConfig, entityName: "Vehicle");

        Assert.Equal(SourceWithDbSetAndEntityConfig, result);
    }

    [Fact]
    public void RemoveEntity_MultiEntitySource_SiblingEntityDbSetAndConfigUntouched()
    {
        var result = new OnModelCreatingRewriter().RemoveEntity(SourceWithDbSetAndEntityConfig, entityName: "Address");

        Assert.DoesNotContain("DbSet<Address>", result);
        Assert.DoesNotContain("Entity<Address>", result);

        Assert.Contains("public DbSet<Person> People { get; set; }", result);
        Assert.Contains("modelBuilder.Entity<Person>(entity =>", result);
        Assert.Contains("entity.Property(e => e.Name).HasMaxLength(100)", result);
    }

    private const string SourceWithBothConfigsChainedForRewrite = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.Property(e => e.Name).IsRequired().HasMaxLength(100);
                });
            }
        }
        """;

    [Fact]
    public void RewriteMaxLength_HasMaxLengthChainedAfterIsRequired_MutatesInPlaceWithoutDuplicating()
    {
        var result = new OnModelCreatingRewriter()
            .RewriteMaxLength(SourceWithBothConfigsChainedForRewrite, entityName: "Person", propertyName: "Name", newMaxLength: 200);

        Assert.Contains("entity.Property(e => e.Name).IsRequired().HasMaxLength(200)", result);
        Assert.DoesNotContain("HasMaxLength(100)", result);

        // Exactly one HasMaxLength call must remain — not two.
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(result, "HasMaxLength"));
    }

    [Fact]
    public void RewriteIsRequired_IsRequiredPrecedingHasMaxLength_MutatesInPlaceWithoutDuplicating()
    {
        var result = new OnModelCreatingRewriter()
            .RewriteIsRequired(SourceWithBothConfigsChainedForRewrite, entityName: "Person", propertyName: "Name", newIsRequired: false);

        Assert.Contains("entity.Property(e => e.Name).IsRequired(false).HasMaxLength(100)", result);

        // Exactly one IsRequired call must remain — not two.
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(result, "IsRequired"));
    }

    [Fact]
    public void RemoveMaxLength_HasMaxLengthChainedAfterIsRequired_RemovesCallLeavingIsRequiredIntact()
    {
        var result = new OnModelCreatingRewriter()
            .RemoveMaxLength(SourceWithBothConfigsChainedForRewrite, entityName: "Person", propertyName: "Name");

        Assert.Contains("entity.Property(e => e.Name).IsRequired();", result);
        Assert.DoesNotContain("HasMaxLength", result);
    }

    [Fact]
    public void RemoveIsRequired_IsRequiredPrecedingHasMaxLength_RemovesCallLeavingHasMaxLengthIntact()
    {
        var result = new OnModelCreatingRewriter()
            .RemoveIsRequired(SourceWithBothConfigsChainedForRewrite, entityName: "Person", propertyName: "Name");

        Assert.Contains("entity.Property(e => e.Name).HasMaxLength(100);", result);
        Assert.DoesNotContain("IsRequired", result);
    }

    // ─── SetIndex / RemoveIndex ──────────────────────────────────────────────────

    [Fact]
    public void SetIndex_MutatesExistingHasIndex_ToAddIsUnique()
    {
        const string source = """
            class Ctx : DbContext {
                protected override void OnModelCreating(ModelBuilder modelBuilder) {
                    modelBuilder.Entity<Person>(entity => {
                        entity.HasIndex(e => e.Email);
                    });
                }
            }
            """;

        var result = new OnModelCreatingRewriter().SetIndex(source, "Person", new[] { "Email" }, isUnique: true);

        var configs = new FluentConfigParser().ParseIndexes(result);
        var config = Assert.Single(configs.Value);
        Assert.Equal(new[] { "Email" }, config.PropertyNames);
        Assert.True(config.IsUnique);
        Assert.Null(config.Name);
    }

    [Fact]
    public void SetIndex_MutatesExistingHasIndex_ToRemoveUniqueness()
    {
        const string source = """
            class Ctx : DbContext {
                protected override void OnModelCreating(ModelBuilder modelBuilder) {
                    modelBuilder.Entity<Person>(entity => {
                        entity.HasIndex(e => e.Email).IsUnique();
                    });
                }
            }
            """;

        var result = new OnModelCreatingRewriter().SetIndex(source, "Person", new[] { "Email" }, isUnique: false);

        Assert.DoesNotContain("IsUnique", result);
        var configs = new FluentConfigParser().ParseIndexes(result);
        var config = Assert.Single(configs.Value);
        Assert.False(config.IsUnique);
    }

    [Fact]
    public void SetIndex_MutatesExistingHasIndex_ToChangeName()
    {
        const string source = """
            class Ctx : DbContext {
                protected override void OnModelCreating(ModelBuilder modelBuilder) {
                    modelBuilder.Entity<Person>(entity => {
                        entity.HasIndex(e => e.Email, "IX_Old");
                    });
                }
            }
            """;

        var result = new OnModelCreatingRewriter().SetIndex(source, "Person", new[] { "Email" }, isUnique: false, name: "IX_New");

        var configs = new FluentConfigParser().ParseIndexes(result);
        var config = Assert.Single(configs.Value);
        Assert.Equal("IX_New", config.Name);
        Assert.False(config.IsUnique);
    }

    [Fact]
    public void SetIndex_InsertsStatementIntoExistingEntityBlock()
    {
        const string source = """
            class Ctx : DbContext {
                protected override void OnModelCreating(ModelBuilder modelBuilder) {
                    modelBuilder.Entity<Person>(entity => {
                        entity.Property(e => e.Name).HasMaxLength(100);
                    });
                }
            }
            """;

        var result = new OnModelCreatingRewriter().SetIndex(source, "Person", new[] { "Email" }, isUnique: true);

        var configs = new FluentConfigParser().ParseIndexes(result);
        var config = Assert.Single(configs.Value);
        Assert.Equal(new[] { "Email" }, config.PropertyNames);
        Assert.True(config.IsUnique);
        Assert.Contains("HasMaxLength", result);
    }

    [Fact]
    public void SetIndex_SynthesizesNewEntityBlock_WhenEntityNotConfigured()
    {
        const string source = """
            class Ctx : DbContext {
                protected override void OnModelCreating(ModelBuilder modelBuilder) {
                }
            }
            """;

        var result = new OnModelCreatingRewriter().SetIndex(source, "Person", new[] { "Email" }, isUnique: false, name: "IX_Person_Email");

        var configs = new FluentConfigParser().ParseIndexes(result);
        var config = Assert.Single(configs.Value);
        Assert.Equal("Person", config.EntityName);
        Assert.Equal(new[] { "Email" }, config.PropertyNames);
        Assert.Equal("IX_Person_Email", config.Name);
        Assert.False(config.IsUnique);
    }

    [Fact]
    public void SetIndex_CompositeColumns_WrittenInOrder()
    {
        const string source = """
            class Ctx : DbContext {
                protected override void OnModelCreating(ModelBuilder modelBuilder) {
                }
            }
            """;

        var result = new OnModelCreatingRewriter().SetIndex(source, "Person", new[] { "LastName", "FirstName" }, isUnique: false);

        var configs = new FluentConfigParser().ParseIndexes(result);
        var config = Assert.Single(configs.Value);
        Assert.Equal(new[] { "LastName", "FirstName" }, config.PropertyNames);
    }

    [Fact]
    public void RemoveIndex_RemovesStatementIncludingIsUniqueChain()
    {
        const string source = """
            class Ctx : DbContext {
                protected override void OnModelCreating(ModelBuilder modelBuilder) {
                    modelBuilder.Entity<Person>(entity => {
                        entity.HasIndex(e => e.Email).IsUnique();
                    });
                }
            }
            """;

        var result = new OnModelCreatingRewriter().RemoveIndex(source, "Person", new[] { "Email" });

        Assert.DoesNotContain("HasIndex", result);
        Assert.DoesNotContain("IsUnique", result);
    }

    [Fact]
    public void RemoveIndex_LeavesOtherIndexesUntouched()
    {
        const string source = """
            class Ctx : DbContext {
                protected override void OnModelCreating(ModelBuilder modelBuilder) {
                    modelBuilder.Entity<Person>(entity => {
                        entity.HasIndex(e => e.Email).IsUnique();
                        entity.HasIndex(e => new { e.LastName, e.FirstName });
                    });
                }
            }
            """;

        var result = new OnModelCreatingRewriter().RemoveIndex(source, "Person", new[] { "Email" });

        var configs = new FluentConfigParser().ParseIndexes(result);
        var config = Assert.Single(configs.Value);
        Assert.Equal(new[] { "LastName", "FirstName" }, config.PropertyNames);
    }

    [Fact]
    public void RemoveIndex_IsNoop_WhenNoMatchingIndex()
    {
        const string source = """
            class Ctx : DbContext {
                protected override void OnModelCreating(ModelBuilder modelBuilder) {
                    modelBuilder.Entity<Person>(entity => {
                        entity.HasIndex(e => e.Email);
                    });
                }
            }
            """;

        var result = new OnModelCreatingRewriter().RemoveIndex(source, "Person", new[] { "PhoneNumber" });

        Assert.Equal(source, result);
    }

    private const string PrecisionSource = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Order>(entity =>
                {
                    entity.Property(e => e.Total).HasPrecision(18, 2);
                    entity.Property(e => e.Rate).HasPrecision(5);
                });
            }
        }
        """;

    [Fact]
    public void RewritePrecision_ExistingCall_MutatesArguments()
    {
        var result = new OnModelCreatingRewriter()
            .RewritePrecision(PrecisionSource, entityName: "Order", propertyName: "Total", precision: 20, scale: 4);

        Assert.Contains("entity.Property(e => e.Total).HasPrecision(20, 4)", result);
        Assert.Contains("entity.Property(e => e.Rate).HasPrecision(5)", result);
        Assert.DoesNotContain("HasPrecision(18, 2)", result);
    }

    // Deliberately irregular formatting (mismatched indentation on the untouched sibling
    // statement, a blank line before it, and non-canonical spacing before its trailing
    // comment) that Roslyn's NormalizeWhitespace() would rewrite away but a surgical,
    // non-reformatting edit must leave completely untouched.
    private const string PrecisionSourceWithIrregularFormatting = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Order>(entity =>
                {
                        entity.Property(e => e.Total).HasPrecision(18, 2);

                    entity.Property(e => e.Rate).HasPrecision(5);   // rate note
                });
            }
        }
        """;

    [Fact]
    public void RewritePrecision_ExistingCall_LeavesEverythingElseIdentical()
    {
        var result = new OnModelCreatingRewriter()
            .RewritePrecision(PrecisionSourceWithIrregularFormatting, entityName: "Order", propertyName: "Total", precision: 20, scale: 4);

        // The mutated call's arguments change, but every other byte of the file - including
        // the sibling statement's mismatched indentation, the blank line above it, and the
        // irregular spacing before its trailing comment - must survive untouched. A whole-file
        // NormalizeWhitespace() pass would silently reformat all of these away.
        var expected = PrecisionSourceWithIrregularFormatting.Replace(
            "entity.Property(e => e.Total).HasPrecision(18, 2);",
            "entity.Property(e => e.Total).HasPrecision(20, 4);");

        Assert.Equal(expected, result);
    }

    [Fact]
    public void RewritePrecision_ExistingCall_MutatesFromPrecisionScaleToPrecisionOnly()
    {
        var result = new OnModelCreatingRewriter()
            .RewritePrecision(PrecisionSource, entityName: "Order", propertyName: "Total", precision: 10, scale: null);

        Assert.Contains("entity.Property(e => e.Total).HasPrecision(10)", result);
        Assert.DoesNotContain("HasPrecision(18, 2)", result);
    }

    private const string SourceWithPropertyButNoPrecision = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Order>(entity =>
                {
                    entity.Property(e => e.Total);
                });
            }
        }
        """;

    [Fact]
    public void RewritePrecision_BarePropertyCall_AppendsHasPrecision()
    {
        var result = new OnModelCreatingRewriter()
            .RewritePrecision(SourceWithPropertyButNoPrecision, entityName: "Order", propertyName: "Total", precision: 18, scale: 2);

        Assert.Contains("entity.Property(e => e.Total).HasPrecision(18, 2)", result);
    }

    private const string SourceWithEntityConfiguredNoPrecisionProperty = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Order>(entity =>
                {
                    entity.Property(e => e.Rate).HasPrecision(5);
                });
            }
        }
        """;

    [Fact]
    public void RewritePrecision_EntityConfiguredWithoutTargetProperty_InsertsNewStatement()
    {
        var result = new OnModelCreatingRewriter()
            .RewritePrecision(SourceWithEntityConfiguredNoPrecisionProperty, entityName: "Order", propertyName: "Total", precision: 18, scale: 2);

        Assert.Contains("entity.Property(e => e.Total).HasPrecision(18, 2)", result);
        Assert.Contains("entity.Property(e => e.Rate).HasPrecision(5)", result);
    }

    [Fact]
    public void RewritePrecision_UnknownEntity_InsertsNewEntityBlock()
    {
        var result = new OnModelCreatingRewriter()
            .RewritePrecision(PrecisionSource, entityName: "Vehicle", propertyName: "Weight", precision: 8, scale: 1);

        Assert.Contains("modelBuilder.Entity<Vehicle>(entity =>", result);
        Assert.Contains("entity.Property(e => e.Weight).HasPrecision(8, 1)", result);

        var configs = new FluentConfigParser().ParsePrecisions(result).Value;
        Assert.Contains(configs, c => c is { EntityName: "Vehicle", PropertyName: "Weight", Precision: 8, Scale: 1 });
        Assert.Contains(configs, c => c is { EntityName: "Order", PropertyName: "Total", Precision: 18, Scale: 2 });
    }

    [Fact]
    public void RemovePrecision_ExistingCall_RemovesHasPrecisionCall_LeavesBarePropertyCall()
    {
        var result = new OnModelCreatingRewriter()
            .RemovePrecision(PrecisionSource, entityName: "Order", propertyName: "Total");

        Assert.Contains("entity.Property(e => e.Total);", result);
        Assert.DoesNotContain("HasPrecision(18, 2)", result);
        Assert.Contains("entity.Property(e => e.Rate).HasPrecision(5)", result);
    }

    [Fact]
    public void RemovePrecision_NoMatchingCall_ReturnsSourceUnchanged()
    {
        var result = new OnModelCreatingRewriter()
            .RemovePrecision(SourceWithPropertyButNoPrecision, entityName: "Order", propertyName: "Total");

        Assert.Equal(SourceWithPropertyButNoPrecision, result);
    }

    private const string TableMappingSource = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.ToTable("People", "dbo");
                });
            }
        }
        """;

    [Fact]
    public void SetTable_ExistingCall_MutatesArguments()
    {
        var result = new OnModelCreatingRewriter()
            .SetTable(TableMappingSource, entityName: "Person", tableName: "Persons", schema: "sales");

        Assert.Contains("entity.ToTable(\"Persons\", \"sales\")", result);
        Assert.DoesNotContain("ToTable(\"People\", \"dbo\")", result);
    }

    [Fact]
    public void SetTable_ExistingCall_MutatesFromSchemaToNoSchema()
    {
        var result = new OnModelCreatingRewriter()
            .SetTable(TableMappingSource, entityName: "Person", tableName: "Persons", schema: null);

        Assert.Contains("entity.ToTable(\"Persons\")", result);
        Assert.DoesNotContain("ToTable(\"People\", \"dbo\")", result);
    }

    private const string SourceWithEntityConfiguredNoTable = """
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

    [Fact]
    public void SetTable_EntityConfiguredWithoutToTable_InsertsStatementAtEndOfBlock()
    {
        var result = new OnModelCreatingRewriter()
            .SetTable(SourceWithEntityConfiguredNoTable, entityName: "Person", tableName: "People", schema: "dbo");

        Assert.Contains("entity.ToTable(\"People\", \"dbo\")", result);
        Assert.Contains("entity.Property(e => e.Name).HasMaxLength(100)", result);
    }

    [Fact]
    public void SetTable_UnknownEntity_InsertsNewEntityBlock()
    {
        var result = new OnModelCreatingRewriter()
            .SetTable(TableMappingSource, entityName: "Vehicle", tableName: "Vehicles", schema: null);

        Assert.Contains("modelBuilder.Entity<Vehicle>(entity =>", result);
        Assert.Contains("entity.ToTable(\"Vehicles\")", result);

        var configs = new FluentConfigParser().ParseTableMappings(result).Value;
        Assert.Contains(configs, c => c is { EntityName: "Vehicle", TableName: "Vehicles", Schema: null });
        Assert.Contains(configs, c => c is { EntityName: "Person", TableName: "People", Schema: "dbo" });
    }

    [Fact]
    public void RemoveTable_ExistingCall_RemovesStatementEntirely()
    {
        var result = new OnModelCreatingRewriter()
            .RemoveTable(TableMappingSource, entityName: "Person");

        Assert.DoesNotContain("ToTable", result);
    }

    [Fact]
    public void RemoveTable_NoMatchingCall_ReturnsSourceUnchanged()
    {
        var result = new OnModelCreatingRewriter()
            .RemoveTable(SourceWithEntityConfiguredNoTable, entityName: "Person");

        Assert.Equal(SourceWithEntityConfiguredNoTable, result);
    }

    private const string ColumnNameSource = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.Property(e => e.Name).HasColumnName("full_name");
                    entity.Property(e => e.Email).HasColumnName("email_address");
                });
            }
        }
        """;

    [Fact]
    public void SetColumnName_ExistingCall_MutatesArgument()
    {
        var result = new OnModelCreatingRewriter()
            .SetColumnName(ColumnNameSource, entityName: "Person", propertyName: "Name", columnName: "display_name");

        Assert.Contains("entity.Property(e => e.Name).HasColumnName(\"display_name\")", result);
        Assert.Contains("entity.Property(e => e.Email).HasColumnName(\"email_address\")", result);
        Assert.DoesNotContain("HasColumnName(\"full_name\")", result);
    }

    private const string SourceWithPropertyButNoColumnName = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.Property(e => e.Name);
                });
            }
        }
        """;

    [Fact]
    public void SetColumnName_BarePropertyCall_AppendsHasColumnName()
    {
        var result = new OnModelCreatingRewriter()
            .SetColumnName(SourceWithPropertyButNoColumnName, entityName: "Person", propertyName: "Name", columnName: "full_name");

        Assert.Contains("entity.Property(e => e.Name).HasColumnName(\"full_name\")", result);
    }

    [Fact]
    public void SetColumnName_UnknownEntity_InsertsNewEntityBlock()
    {
        var result = new OnModelCreatingRewriter()
            .SetColumnName(ColumnNameSource, entityName: "Vehicle", propertyName: "Vin", columnName: "vin_number");

        Assert.Contains("modelBuilder.Entity<Vehicle>(entity =>", result);
        Assert.Contains("entity.Property(e => e.Vin).HasColumnName(\"vin_number\")", result);
    }

    [Fact]
    public void RemoveColumnName_ExistingCall_RemovesCall_LeavesBarePropertyCall()
    {
        var result = new OnModelCreatingRewriter()
            .RemoveColumnName(ColumnNameSource, entityName: "Person", propertyName: "Name");

        Assert.Contains("entity.Property(e => e.Name);", result);
        Assert.DoesNotContain("HasColumnName(\"full_name\")", result);
        Assert.Contains("entity.Property(e => e.Email).HasColumnName(\"email_address\")", result);
    }

    [Fact]
    public void RemoveColumnName_NoMatchingCall_ReturnsSourceUnchanged()
    {
        var result = new OnModelCreatingRewriter()
            .RemoveColumnName(SourceWithPropertyButNoColumnName, entityName: "Person", propertyName: "Name");

        Assert.Equal(SourceWithPropertyButNoColumnName, result);
    }

    private const string ColumnTypeSource = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Order>(entity =>
                {
                    entity.Property(e => e.Total).HasColumnType("decimal(18,2)");
                });
            }
        }
        """;

    [Fact]
    public void SetColumnType_ExistingCall_MutatesArgument()
    {
        var result = new OnModelCreatingRewriter()
            .SetColumnType(ColumnTypeSource, entityName: "Order", propertyName: "Total", columnType: "money");

        Assert.Contains("entity.Property(e => e.Total).HasColumnType(\"money\")", result);
        Assert.DoesNotContain("HasColumnType(\"decimal(18,2)\")", result);
    }

    private const string SourceWithPropertyButNoColumnType = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Order>(entity =>
                {
                    entity.Property(e => e.Total);
                });
            }
        }
        """;

    [Fact]
    public void SetColumnType_BarePropertyCall_AppendsHasColumnType()
    {
        var result = new OnModelCreatingRewriter()
            .SetColumnType(SourceWithPropertyButNoColumnType, entityName: "Order", propertyName: "Total", columnType: "decimal(18,2)");

        Assert.Contains("entity.Property(e => e.Total).HasColumnType(\"decimal(18,2)\")", result);
    }

    [Fact]
    public void SetColumnType_UnknownEntity_InsertsNewEntityBlock()
    {
        var result = new OnModelCreatingRewriter()
            .SetColumnType(ColumnTypeSource, entityName: "Vehicle", propertyName: "Price", columnType: "money");

        Assert.Contains("modelBuilder.Entity<Vehicle>(entity =>", result);
        Assert.Contains("entity.Property(e => e.Price).HasColumnType(\"money\")", result);
    }

    [Fact]
    public void RemoveColumnType_ExistingCall_RemovesCall_LeavesBarePropertyCall()
    {
        var result = new OnModelCreatingRewriter()
            .RemoveColumnType(ColumnTypeSource, entityName: "Order", propertyName: "Total");

        Assert.Contains("entity.Property(e => e.Total);", result);
        Assert.DoesNotContain("HasColumnType(\"decimal(18,2)\")", result);
    }

    [Fact]
    public void RemoveColumnType_NoMatchingCall_ReturnsSourceUnchanged()
    {
        var result = new OnModelCreatingRewriter()
            .RemoveColumnType(SourceWithPropertyButNoColumnType, entityName: "Order", propertyName: "Total");

        Assert.Equal(SourceWithPropertyButNoColumnType, result);
    }

    private const string DefaultValueSource = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Order>(entity =>
                {
                    entity.Property(e => e.Quantity).HasDefaultValue(1);
                    entity.Property(e => e.Status).HasDefaultValue("pending");
                });
            }
        }
        """;

    [Fact]
    public void SetDefaultValue_ExistingCall_MutatesArgument()
    {
        var result = new OnModelCreatingRewriter()
            .SetDefaultValue(DefaultValueSource, entityName: "Order", propertyName: "Quantity", literalText: "5");

        Assert.Contains("entity.Property(e => e.Quantity).HasDefaultValue(5)", result);
        Assert.Contains("entity.Property(e => e.Status).HasDefaultValue(\"pending\")", result);
        Assert.DoesNotContain("HasDefaultValue(1)", result);
    }

    [Fact]
    public void SetDefaultValue_ExistingCall_MutatesStringLiteralArgument()
    {
        var result = new OnModelCreatingRewriter()
            .SetDefaultValue(DefaultValueSource, entityName: "Order", propertyName: "Status", literalText: "\"active\"");

        Assert.Contains("entity.Property(e => e.Status).HasDefaultValue(\"active\")", result);
        Assert.DoesNotContain("HasDefaultValue(\"pending\")", result);
    }

    private const string SourceWithPropertyButNoDefaultValue = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Order>(entity =>
                {
                    entity.Property(e => e.Quantity);
                });
            }
        }
        """;

    [Fact]
    public void SetDefaultValue_BarePropertyCall_AppendsHasDefaultValue()
    {
        var result = new OnModelCreatingRewriter()
            .SetDefaultValue(SourceWithPropertyButNoDefaultValue, entityName: "Order", propertyName: "Quantity", literalText: "1");

        Assert.Contains("entity.Property(e => e.Quantity).HasDefaultValue(1)", result);
    }

    [Fact]
    public void SetDefaultValue_UnknownEntity_InsertsNewEntityBlock()
    {
        var result = new OnModelCreatingRewriter()
            .SetDefaultValue(DefaultValueSource, entityName: "Vehicle", propertyName: "Wheels", literalText: "4");

        Assert.Contains("modelBuilder.Entity<Vehicle>(entity =>", result);
        Assert.Contains("entity.Property(e => e.Wheels).HasDefaultValue(4)", result);
    }

    [Fact]
    public void RemoveDefaultValue_ExistingCall_RemovesCall_LeavesBarePropertyCall()
    {
        var result = new OnModelCreatingRewriter()
            .RemoveDefaultValue(DefaultValueSource, entityName: "Order", propertyName: "Quantity");

        Assert.Contains("entity.Property(e => e.Quantity);", result);
        Assert.DoesNotContain("HasDefaultValue(1)", result);
        Assert.Contains("entity.Property(e => e.Status).HasDefaultValue(\"pending\")", result);
    }

    [Fact]
    public void RemoveDefaultValue_NoMatchingCall_ReturnsSourceUnchanged()
    {
        var result = new OnModelCreatingRewriter()
            .RemoveDefaultValue(SourceWithPropertyButNoDefaultValue, entityName: "Order", propertyName: "Quantity");

        Assert.Equal(SourceWithPropertyButNoDefaultValue, result);
    }

    private const string SourceWithNoRelationshipConfig = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Blog>(entity =>
                {
                    entity.HasKey(e => e.Id);
                });
                modelBuilder.Entity<Post>(entity =>
                {
                    entity.HasKey(e => e.Id);
                });
            }
        }
        """;

    private const string SourceWithNoEntityConfigAtAll = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
            }
        }
        """;

    [Fact]
    public void SetRelationship_OneToMany_ExistingDependentBlock_AppendsChain()
    {
        var relationship = new RelationshipModel("Blog", "Post", RelationshipKind.OneToMany, null, null);

        var result = new OnModelCreatingRewriter()
            .SetRelationship(SourceWithNoRelationshipConfig, relationship);

        Assert.Contains("entity.HasOne<Blog>().WithMany()", result);
        Assert.Contains("entity.HasKey(e => e.Id)", result);

        var entities = new List<EntityModel>
        {
            new("Blog", new List<PropertyModel> { new("Id", "int", false, null) }),
            new("Post", new List<PropertyModel> { new("Id", "int", false, null), new("BlogId", "int", false, null) }),
        };
        var configs = new FluentConfigParser().ParseRelationships(result, entities).Value;
        Assert.Contains(configs, c => c.PrincipalEntity == "Blog" && c.DependentEntity == "Post" && c.Kind == RelationshipKind.OneToMany);
    }

    [Fact]
    public void SetRelationship_OneToMany_WithForeignKey_EmitsHasForeignKey()
    {
        var relationship = new RelationshipModel(
            "Blog", "Post", RelationshipKind.OneToMany, null, null,
            ForeignKeyProperties: new List<string> { "BlogId" });

        var result = new OnModelCreatingRewriter()
            .SetRelationship(SourceWithNoRelationshipConfig, relationship);

        Assert.Contains("entity.HasOne<Blog>().WithMany().HasForeignKey(d => d.BlogId)", result);
    }

    [Fact]
    public void SetRelationship_OneToMany_WithCompositeForeignKey_EmitsAnonymousObject()
    {
        var relationship = new RelationshipModel(
            "Blog", "Post", RelationshipKind.OneToMany, null, null,
            ForeignKeyProperties: new List<string> { "BlogId", "TenantId" });

        var result = new OnModelCreatingRewriter()
            .SetRelationship(SourceWithNoRelationshipConfig, relationship);

        Assert.Contains("entity.HasOne<Blog>().WithMany().HasForeignKey(d => new { d.BlogId, d.TenantId })", result);
    }

    [Fact]
    public void SetRelationship_OneToMany_WithNavigationNames_EmitsLambdas()
    {
        var relationship = new RelationshipModel("Blog", "Post", RelationshipKind.OneToMany, "Posts", "Blog");

        var result = new OnModelCreatingRewriter()
            .SetRelationship(SourceWithNoRelationshipConfig, relationship);

        Assert.Contains("entity.HasOne<Blog>(x => x.Blog).WithMany(x => x.Posts)", result);
    }

    [Fact]
    public void SetRelationship_OneToOne_EmitsGenericHasForeignKey()
    {
        var relationship = new RelationshipModel(
            "Blog", "Post", RelationshipKind.OneToOne, null, null,
            ForeignKeyProperties: new List<string> { "BlogId" });

        var result = new OnModelCreatingRewriter()
            .SetRelationship(SourceWithNoRelationshipConfig, relationship);

        Assert.Contains("entity.HasOne<Blog>().WithOne().HasForeignKey<Post>(d => d.BlogId)", result);
    }

    [Fact]
    public void SetRelationship_ManyToMany_InsertsIntoPrincipalScope()
    {
        var relationship = new RelationshipModel("Blog", "Post", RelationshipKind.ManyToMany, null, null);

        var result = new OnModelCreatingRewriter()
            .SetRelationship(SourceWithNoRelationshipConfig, relationship);

        var blogBlockStart = result.IndexOf("modelBuilder.Entity<Blog>", StringComparison.Ordinal);
        var postBlockStart = result.IndexOf("modelBuilder.Entity<Post>", StringComparison.Ordinal);
        var hasManyIndex = result.IndexOf("entity.HasMany<Post>().WithMany()", StringComparison.Ordinal);

        Assert.True(hasManyIndex > blogBlockStart && hasManyIndex < postBlockStart);
    }

    [Fact]
    public void SetRelationship_ManyToMany_WithJoinEntity_EmitsUsingEntity()
    {
        var relationship = new RelationshipModel(
            "Blog", "Post", RelationshipKind.ManyToMany, null, null,
            JoinEntityName: "BlogPost");

        var result = new OnModelCreatingRewriter()
            .SetRelationship(SourceWithNoRelationshipConfig, relationship);

        Assert.Contains("entity.HasMany<Post>().WithMany().UsingEntity<BlogPost>()", result);
    }

    [Fact]
    public void SetRelationship_WithOnDelete_EmitsOnDeleteCall()
    {
        var relationship = new RelationshipModel(
            "Blog", "Post", RelationshipKind.OneToMany, null, null,
            OnDeleteBehavior: "Cascade");

        var result = new OnModelCreatingRewriter()
            .SetRelationship(SourceWithNoRelationshipConfig, relationship);

        Assert.Contains("entity.HasOne<Blog>().WithMany().OnDelete(DeleteBehavior.Cascade)", result);
    }

    [Fact]
    public void SetRelationship_DependentEntityHasNoConfigBlockYet_InsertsWholeEntityBlock()
    {
        var relationship = new RelationshipModel("Blog", "Post", RelationshipKind.OneToMany, null, null);

        var result = new OnModelCreatingRewriter()
            .SetRelationship(SourceWithNoEntityConfigAtAll, relationship);

        Assert.Contains("modelBuilder.Entity<Post>(entity =>", result);
        Assert.Contains("entity.HasOne<Blog>().WithMany()", result);
    }

    private const string SourceWithOneToManyRelationship = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Blog>(entity =>
                {
                    entity.HasKey(e => e.Id);
                });
                modelBuilder.Entity<Post>(entity =>
                {
                    entity.HasKey(e => e.Id);
                    entity.HasOne<Blog>().WithMany().HasForeignKey(d => d.BlogId);
                });
            }
        }
        """;

    private const string SourceWithManyToManyRelationship = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Blog>(entity =>
                {
                    entity.HasKey(e => e.Id);
                    entity.HasMany<Post>().WithMany();
                });
                modelBuilder.Entity<Post>(entity =>
                {
                    entity.HasKey(e => e.Id);
                });
            }
        }
        """;

    [Fact]
    public void RemoveRelationship_OneToMany_RemovesWholeStatementFromDependentScope()
    {
        var relationship = new RelationshipModel(
            "Blog", "Post", RelationshipKind.OneToMany, null, null,
            ForeignKeyProperties: new List<string> { "BlogId" });

        var result = new OnModelCreatingRewriter()
            .RemoveRelationship(SourceWithOneToManyRelationship, relationship);

        Assert.DoesNotContain("HasOne<Blog>()", result);
        Assert.Contains("entity.HasKey(e => e.Id)", result);
    }

    [Fact]
    public void RemoveRelationship_ManyToMany_RemovesWholeStatementFromPrincipalScope()
    {
        var relationship = new RelationshipModel("Blog", "Post", RelationshipKind.ManyToMany, null, null);

        var result = new OnModelCreatingRewriter()
            .RemoveRelationship(SourceWithManyToManyRelationship, relationship);

        Assert.DoesNotContain("HasMany<Post>()", result);
    }

    [Fact]
    public void RemoveRelationship_NoMatch_ReturnsSourceUnchanged()
    {
        var relationship = new RelationshipModel("Blog", "Comment", RelationshipKind.OneToMany, null, null);

        var result = new OnModelCreatingRewriter()
            .RemoveRelationship(SourceWithOneToManyRelationship, relationship);

        Assert.Equal(SourceWithOneToManyRelationship, result);
    }

    private const string SourceWithOneToManyRelationshipViaNavigation = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Blog>(entity =>
                {
                    entity.HasKey(e => e.Id);
                });
                modelBuilder.Entity<Post>(entity =>
                {
                    entity.HasKey(e => e.Id);
                    entity.HasOne(d => d.Blog).WithMany().HasForeignKey(d => d.BlogId);
                });
            }
        }
        """;

    private const string SourceWithManyToManyRelationshipViaNavigation = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Blog>(entity =>
                {
                    entity.HasKey(e => e.Id);
                });
                modelBuilder.Entity<Post>(entity =>
                {
                    entity.HasKey(e => e.Id);
                    entity.HasMany(p => p.Tags).WithMany();
                });
            }
        }
        """;

    [Fact]
    public void RemoveRelationship_OneToManyViaNavigationProperty_RemovesWholeStatementFromDependentScope()
    {
        var relationship = new RelationshipModel(
            "Blog", "Post", RelationshipKind.OneToMany, null, "Blog",
            ForeignKeyProperties: new List<string> { "BlogId" });

        var result = new OnModelCreatingRewriter()
            .RemoveRelationship(SourceWithOneToManyRelationshipViaNavigation, relationship);

        Assert.DoesNotContain("HasOne(d => d.Blog)", result);
        Assert.Contains("entity.HasKey(e => e.Id)", result);
    }

    [Fact]
    public void RemoveRelationship_ManyToManyViaNavigationProperty_RemovesWholeStatementFromPrincipalScope()
    {
        var relationship = new RelationshipModel("Post", "Tag", RelationshipKind.ManyToMany, "Tags", null);

        var result = new OnModelCreatingRewriter()
            .RemoveRelationship(SourceWithManyToManyRelationshipViaNavigation, relationship);

        Assert.DoesNotContain("HasMany(p => p.Tags)", result);
    }

    private const string SourceUsingEntityTypeConfiguration = """
        public class PersonConfiguration : IEntityTypeConfiguration<Person>
        {
            public void Configure(EntityTypeBuilder<Person> builder)
            {
                builder.Property(e => e.Name).HasMaxLength(100);
            }
        }
        """;

    [Fact]
    public void RewriteMaxLength_EntityTypeConfigurationStyle_MutatesExistingCall()
    {
        var result = new OnModelCreatingRewriter()
            .RewriteMaxLength(SourceUsingEntityTypeConfiguration, entityName: "Person", propertyName: "Name", newMaxLength: 150);

        Assert.Contains("builder.Property(e => e.Name).HasMaxLength(150)", result);
        Assert.DoesNotContain("HasMaxLength(100)", result);
    }

    private const string SourceUsingEntityTypeConfigurationNoMaxLengthYet = """
        public class PersonConfiguration : IEntityTypeConfiguration<Person>
        {
            public void Configure(EntityTypeBuilder<Person> builder)
            {
                builder.Property(e => e.Name);
            }
        }
        """;

    [Fact]
    public void RewriteMaxLength_EntityTypeConfigurationStyle_AppendsOntoExistingPropertyCall()
    {
        var result = new OnModelCreatingRewriter()
            .RewriteMaxLength(SourceUsingEntityTypeConfigurationNoMaxLengthYet, entityName: "Person", propertyName: "Name", newMaxLength: 50);

        Assert.Contains("builder.Property(e => e.Name).HasMaxLength(50)", result);
    }

    private const string SourceUsingEntityTypeConfigurationEmptyConfigure = """
        public class PersonConfiguration : IEntityTypeConfiguration<Person>
        {
            public void Configure(EntityTypeBuilder<Person> builder)
            {
            }
        }
        """;

    [Fact]
    public void RewriteMaxLength_EntityTypeConfigurationStyle_InsertsNewStatementIntoConfigureBody()
    {
        var result = new OnModelCreatingRewriter()
            .RewriteMaxLength(SourceUsingEntityTypeConfigurationEmptyConfigure, entityName: "Person", propertyName: "Email", newMaxLength: 255);

        Assert.Contains("builder.Property(e => e.Email).HasMaxLength(255)", result);
    }

    [Fact]
    public void RemoveMaxLength_EntityTypeConfigurationStyle_RemovesCallButKeepsPropertyCall()
    {
        var result = new OnModelCreatingRewriter()
            .RemoveMaxLength(SourceUsingEntityTypeConfiguration, entityName: "Person", propertyName: "Name");

        Assert.Contains("builder.Property(e => e.Name);", result);
        Assert.DoesNotContain("HasMaxLength", result);
    }

    private const string SourceUsingEntityTypeConfigurationForPrecisionAndRequired = """
        public class PersonConfiguration : IEntityTypeConfiguration<Person>
        {
            public void Configure(EntityTypeBuilder<Person> builder)
            {
                builder.Property(e => e.Balance).HasPrecision(18, 2);
                builder.Property(e => e.Name).IsRequired();
            }
        }
        """;

    [Fact]
    public void RewritePrecision_EntityTypeConfigurationStyle_MutatesExistingCall()
    {
        var result = new OnModelCreatingRewriter()
            .RewritePrecision(SourceUsingEntityTypeConfigurationForPrecisionAndRequired, entityName: "Person", propertyName: "Balance", precision: 10, scale: 4);

        Assert.Contains("builder.Property(e => e.Balance).HasPrecision(10, 4)", result);
    }

    [Fact]
    public void RemovePrecision_EntityTypeConfigurationStyle_RemovesCall()
    {
        var result = new OnModelCreatingRewriter()
            .RemovePrecision(SourceUsingEntityTypeConfigurationForPrecisionAndRequired, entityName: "Person", propertyName: "Balance");

        Assert.DoesNotContain("HasPrecision", result);
    }

    [Fact]
    public void RewriteIsRequired_EntityTypeConfigurationStyle_MutatesExistingCall()
    {
        var result = new OnModelCreatingRewriter()
            .RewriteIsRequired(SourceUsingEntityTypeConfigurationForPrecisionAndRequired, entityName: "Person", propertyName: "Name", newIsRequired: false);

        Assert.Contains("builder.Property(e => e.Name).IsRequired(false)", result);
    }

    [Fact]
    public void RemoveIsRequired_EntityTypeConfigurationStyle_RemovesCall()
    {
        var result = new OnModelCreatingRewriter()
            .RemoveIsRequired(SourceUsingEntityTypeConfigurationForPrecisionAndRequired, entityName: "Person", propertyName: "Name");

        Assert.DoesNotContain("IsRequired", result);
    }

    private const string SourceUsingEntityTypeConfigurationNoPrecisionYet = """
        public class PersonConfiguration : IEntityTypeConfiguration<Person>
        {
            public void Configure(EntityTypeBuilder<Person> builder)
            {
            }
        }
        """;

    [Fact]
    public void RewritePrecision_EntityTypeConfigurationStyle_InsertsNewStatement()
    {
        var result = new OnModelCreatingRewriter()
            .RewritePrecision(SourceUsingEntityTypeConfigurationNoPrecisionYet, entityName: "Person", propertyName: "Balance", precision: 12, scale: null);

        Assert.Contains("builder.Property(e => e.Balance).HasPrecision(12)", result);
    }

    private const string SourceUsingEntityTypeConfigurationForKeyAndTable = """
        public class PersonConfiguration : IEntityTypeConfiguration<Person>
        {
            public void Configure(EntityTypeBuilder<Person> builder)
            {
                builder.HasKey(e => e.Id);
                builder.ToTable("People");
            }
        }
        """;

    [Fact]
    public void SetKey_EntityTypeConfigurationStyle_MutatesExistingCall()
    {
        var result = new OnModelCreatingRewriter()
            .SetKey(SourceUsingEntityTypeConfigurationForKeyAndTable, entityName: "Person", propertyNames: new[] { "PersonId" });

        Assert.Contains("builder.HasKey(e => e.PersonId)", result);
    }

    [Fact]
    public void RemoveKey_EntityTypeConfigurationStyle_RemovesStatement()
    {
        var result = new OnModelCreatingRewriter()
            .RemoveKey(SourceUsingEntityTypeConfigurationForKeyAndTable, entityName: "Person");

        Assert.DoesNotContain("HasKey", result);
    }

    [Fact]
    public void SetTable_EntityTypeConfigurationStyle_MutatesExistingCall()
    {
        var result = new OnModelCreatingRewriter()
            .SetTable(SourceUsingEntityTypeConfigurationForKeyAndTable, entityName: "Person", tableName: "Persons", schema: null);

        Assert.Contains("builder.ToTable(\"Persons\")", result);
    }

    [Fact]
    public void RemoveTable_EntityTypeConfigurationStyle_RemovesStatement()
    {
        var result = new OnModelCreatingRewriter()
            .RemoveTable(SourceUsingEntityTypeConfigurationForKeyAndTable, entityName: "Person");

        Assert.DoesNotContain("ToTable", result);
    }

    private const string SourceUsingEntityTypeConfigurationEmptyForKeyAndTable = """
        public class PersonConfiguration : IEntityTypeConfiguration<Person>
        {
            public void Configure(EntityTypeBuilder<Person> builder)
            {
            }
        }
        """;

    [Fact]
    public void SetKey_EntityTypeConfigurationStyle_InsertsNewStatement()
    {
        var result = new OnModelCreatingRewriter()
            .SetKey(SourceUsingEntityTypeConfigurationEmptyForKeyAndTable, entityName: "Person", propertyNames: new[] { "Id" });

        Assert.Contains("builder.HasKey(e => e.Id)", result);
    }
}
