using EfSchemaVisualizer.Core.CodeGen;
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
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(result, "HasMaxLength").Count);
    }

    [Fact]
    public void RewriteIsRequired_IsRequiredPrecedingHasMaxLength_MutatesInPlaceWithoutDuplicating()
    {
        var result = new OnModelCreatingRewriter()
            .RewriteIsRequired(SourceWithBothConfigsChainedForRewrite, entityName: "Person", propertyName: "Name", newIsRequired: false);

        Assert.Contains("entity.Property(e => e.Name).IsRequired(false).HasMaxLength(100)", result);

        // Exactly one IsRequired call must remain — not two.
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(result, "IsRequired").Count);
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
}
