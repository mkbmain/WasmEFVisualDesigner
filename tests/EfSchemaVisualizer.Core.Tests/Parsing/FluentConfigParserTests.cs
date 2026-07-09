using EfSchemaVisualizer.Core.Parsing;
using Xunit;

namespace EfSchemaVisualizer.Core.Tests.Parsing;

public class FluentConfigParserTests
{
    private const string Source = """
        public class AppDbContext : DbContext
        {
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
    public void ParseMaxLengths_ReadsEveryConfiguredProperty_AcrossMultipleEntities()
    {
        var result = new FluentConfigParser().ParseMaxLengths(Source);

        Assert.Empty(result.Diagnostics);
        Assert.Equal(3, result.Value.Count);
        Assert.Contains(result.Value, c => c is { EntityName: "Person", PropertyName: "Name", MaxLength: 100 });
        Assert.Contains(result.Value, c => c is { EntityName: "Person", PropertyName: "Email", MaxLength: 255 });
        Assert.Contains(result.Value, c => c is { EntityName: "Address", PropertyName: "Line1", MaxLength: 200 });
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
    public void ParseMaxLengths_NestedEntityConfig_DoesNotAttributeNestedCallsToOuterEntity()
    {
        var result = new FluentConfigParser().ParseMaxLengths(SourceWithNestedEntityConfig);

        Assert.Empty(result.Diagnostics);
        Assert.Equal(2, result.Value.Count);
        Assert.Contains(result.Value, c => c is { EntityName: "Person", PropertyName: "Name", MaxLength: 100 });
        Assert.Contains(result.Value, c => c is { EntityName: "Address", PropertyName: "Line1", MaxLength: 200 });
        Assert.DoesNotContain(result.Value, c => c.EntityName == "Person" && c.PropertyName == "Line1");
    }

    private const string SourceWithRenamedBuilder = """
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
    public void ParseMaxLengths_RenamedBuilderParameter_StillResolvesEntity()
    {
        var result = new FluentConfigParser().ParseMaxLengths(SourceWithRenamedBuilder);

        Assert.Empty(result.Diagnostics);
        Assert.Contains(result.Value, c => c is { EntityName: "Person", PropertyName: "Name", MaxLength: 100 });
    }

    private const string SourceWithStringPropertyOverload = """
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
    public void ParseMaxLengths_PropertyStringOverload_IsRead()
    {
        var result = new FluentConfigParser().ParseMaxLengths(SourceWithStringPropertyOverload);

        Assert.Empty(result.Diagnostics);
        Assert.Contains(result.Value, c => c is { EntityName: "Person", PropertyName: "Name", MaxLength: 100 });
    }

    private const string SourceWithBlockBodiedLambda = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.Property(e => { return e.Name; }).HasMaxLength(100);
                });
            }
        }
        """;

    [Fact]
    public void ParseMaxLengths_BlockBodiedLambdaWithSingleReturn_IsRead()
    {
        var result = new FluentConfigParser().ParseMaxLengths(SourceWithBlockBodiedLambda);

        Assert.Empty(result.Diagnostics);
        Assert.Contains(result.Value, c => c is { EntityName: "Person", PropertyName: "Name", MaxLength: 100 });
    }

    private const string SourceWithConstArgument = """
        public class AppDbContext : DbContext
        {
            private const int MaxNameLength = 100;

            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.Property(e => e.Name).HasMaxLength(MaxNameLength);
                });
            }
        }
        """;

    [Fact]
    public void ParseMaxLengths_ConstIdentifierArgument_EmitsUnreadableMaxLengthArgumentDiagnostic()
    {
        var result = new FluentConfigParser().ParseMaxLengths(SourceWithConstArgument);

        Assert.Empty(result.Value);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("UnreadableMaxLengthArgument", diagnostic.Code);
        Assert.Equal("Person", diagnostic.EntityName);
        Assert.Equal("Name", diagnostic.PropertyName);
    }

    private const string SourceWithArithmeticArgument = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.Property(e => e.Name).HasMaxLength(50 * 2);
                });
            }
        }
        """;

    [Fact]
    public void ParseMaxLengths_ArithmeticArgument_EmitsUnreadableMaxLengthArgumentDiagnostic()
    {
        var result = new FluentConfigParser().ParseMaxLengths(SourceWithArithmeticArgument);

        Assert.Empty(result.Value);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("UnreadableMaxLengthArgument", diagnostic.Code);
        Assert.Equal("Person", diagnostic.EntityName);
        Assert.Equal("Name", diagnostic.PropertyName);
    }

    private const string SourceWithUnresolvablePropertyLambda = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.Property(e =>
                    {
                        var name = e.Name;
                        return name;
                    }).HasMaxLength(100);
                });
            }
        }
        """;

    [Fact]
    public void ParseMaxLengths_UnresolvablePropertyLambda_EmitsUnresolvablePropertyNameDiagnostic()
    {
        var result = new FluentConfigParser().ParseMaxLengths(SourceWithUnresolvablePropertyLambda);

        Assert.Empty(result.Value);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("UnresolvablePropertyName", diagnostic.Code);
        Assert.Equal("Person", diagnostic.EntityName);
        Assert.Null(diagnostic.PropertyName);
    }

    private const string SourceWithIsRequiredCalls = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.Property(e => e.Name).IsRequired();
                    entity.Property(e => e.Email).IsRequired(false);
                });

                modelBuilder.Entity<Address>(entity =>
                {
                    entity.Property(e => e.Line1).IsRequired(true);
                });
            }
        }
        """;

    [Fact]
    public void ParseIsRequired_ReadsBareAndExplicitCalls_AcrossMultipleEntities()
    {
        var result = new FluentConfigParser().ParseIsRequired(SourceWithIsRequiredCalls);

        Assert.Empty(result.Diagnostics);
        Assert.Equal(3, result.Value.Count);
        Assert.Contains(result.Value, c => c is { EntityName: "Person", PropertyName: "Name", IsRequired: true });
        Assert.Contains(result.Value, c => c is { EntityName: "Person", PropertyName: "Email", IsRequired: false });
        Assert.Contains(result.Value, c => c is { EntityName: "Address", PropertyName: "Line1", IsRequired: true });
    }

    private const string SourceWithNoIsRequiredCalls = """
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
    public void ParseIsRequired_NoCallsPresent_ReturnsEmpty()
    {
        var result = new FluentConfigParser().ParseIsRequired(SourceWithNoIsRequiredCalls);

        Assert.Empty(result.Diagnostics);
        Assert.Empty(result.Value);
    }

    private const string SourceWithNonLiteralIsRequiredArgument = """
        public class AppDbContext : DbContext
        {
            private const bool NameIsRequired = true;

            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.Property(e => e.Name).IsRequired(NameIsRequired);
                });
            }
        }
        """;

    [Fact]
    public void ParseIsRequired_NonLiteralArgument_EmitsUnreadableIsRequiredArgumentDiagnostic()
    {
        var result = new FluentConfigParser().ParseIsRequired(SourceWithNonLiteralIsRequiredArgument);

        Assert.Empty(result.Value);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("UnreadableIsRequiredArgument", diagnostic.Code);
        Assert.Equal("Person", diagnostic.EntityName);
        Assert.Equal("Name", diagnostic.PropertyName);
    }

    private const string SourceWithUnresolvableIsRequiredPropertyLambda = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.Property(e =>
                    {
                        var name = e.Name;
                        return name;
                    }).IsRequired();
                });
            }
        }
        """;

    [Fact]
    public void ParseIsRequired_UnresolvablePropertyLambda_EmitsUnresolvablePropertyNameDiagnostic()
    {
        var result = new FluentConfigParser().ParseIsRequired(SourceWithUnresolvableIsRequiredPropertyLambda);

        Assert.Empty(result.Value);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("UnresolvablePropertyName", diagnostic.Code);
        Assert.Equal("Person", diagnostic.EntityName);
        Assert.Null(diagnostic.PropertyName);
    }

    private const string SourceWithBothConfigsChained = """
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
    public void ParseMaxLengths_HasMaxLengthChainedAfterIsRequired_StillResolvesPropertyName()
    {
        var result = new FluentConfigParser().ParseMaxLengths(SourceWithBothConfigsChained);

        Assert.Empty(result.Diagnostics);
        Assert.Contains(result.Value, c => c is { EntityName: "Person", PropertyName: "Name", MaxLength: 100 });
    }

    [Fact]
    public void ParseIsRequired_IsRequiredFollowedByHasMaxLength_StillResolvesPropertyName()
    {
        var result = new FluentConfigParser().ParseIsRequired(SourceWithBothConfigsChained);

        Assert.Empty(result.Diagnostics);
        Assert.Contains(result.Value, c => c is { EntityName: "Person", PropertyName: "Name", IsRequired: true });
    }

    private const string SourceWithSingleAndCompositeKeys = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.HasKey(e => e.Id);
                });

                modelBuilder.Entity<OrderLine>(entity =>
                {
                    entity.HasKey(e => new { e.OrderId, e.LineNumber });
                });
            }
        }
        """;

    [Fact]
    public void ParseKeys_ReadsSingleAndCompositeLambdaKeys_AcrossMultipleEntities()
    {
        var result = new FluentConfigParser().ParseKeys(SourceWithSingleAndCompositeKeys);

        Assert.Empty(result.Diagnostics);
        Assert.Equal(2, result.Value.Count);
        Assert.Contains(result.Value, c => c.EntityName == "Person" && c.PropertyNames.SequenceEqual(new[] { "Id" }));
        Assert.Contains(result.Value, c => c.EntityName == "OrderLine" && c.PropertyNames.SequenceEqual(new[] { "OrderId", "LineNumber" }));
    }

    private const string SourceWithStringKey = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.HasKey("Id");
                });
            }
        }
        """;

    [Fact]
    public void ParseKeys_StringOverload_IsRead()
    {
        var result = new FluentConfigParser().ParseKeys(SourceWithStringKey);

        Assert.Empty(result.Diagnostics);
        Assert.Contains(result.Value, c => c.EntityName == "Person" && c.PropertyNames.SequenceEqual(new[] { "Id" }));
    }

    private const string SourceWithStringArrayKey = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<OrderLine>(entity =>
                {
                    entity.HasKey("OrderId", "LineNumber");
                });
            }
        }
        """;

    [Fact]
    public void ParseKeys_StringParamsOverload_IsReadInOrder()
    {
        var result = new FluentConfigParser().ParseKeys(SourceWithStringArrayKey);

        Assert.Empty(result.Diagnostics);
        Assert.Contains(result.Value, c => c.EntityName == "OrderLine" && c.PropertyNames.SequenceEqual(new[] { "OrderId", "LineNumber" }));
    }

    private const string SourceWithExplicitNameAnonymousMember = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.HasKey(e => new { Key = e.Id });
                });
            }
        }
        """;

    [Fact]
    public void ParseKeys_ExplicitNameAnonymousMember_EmitsUnreadableHasKeyArgumentDiagnostic()
    {
        var result = new FluentConfigParser().ParseKeys(SourceWithExplicitNameAnonymousMember);

        Assert.Empty(result.Value);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("UnreadableHasKeyArgument", diagnostic.Code);
        Assert.Equal("Person", diagnostic.EntityName);
        Assert.Null(diagnostic.PropertyName);
    }

    private const string SourceWithMethodCallKeyArgument = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.HasKey(GetKeySelector());
                });
            }
        }
        """;

    [Fact]
    public void ParseKeys_MethodCallArgument_EmitsUnreadableHasKeyArgumentDiagnostic()
    {
        var result = new FluentConfigParser().ParseKeys(SourceWithMethodCallKeyArgument);

        Assert.Empty(result.Value);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("UnreadableHasKeyArgument", diagnostic.Code);
        Assert.Equal("Person", diagnostic.EntityName);
        Assert.Null(diagnostic.PropertyName);
    }

    private const string SourceWithNoHasKeyCall = """
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
    public void ParseKeys_NoCallPresent_ReturnsEmpty()
    {
        var result = new FluentConfigParser().ParseKeys(SourceWithNoHasKeyCall);

        Assert.Empty(result.Diagnostics);
        Assert.Empty(result.Value);
    }
}
