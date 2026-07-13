using EfSchemaVisualizer.Core.Model;
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
        Assert.Equal(DiagnosticCodes.UnreadableMaxLengthArgument, diagnostic.Code);
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
        Assert.Equal(DiagnosticCodes.UnreadableMaxLengthArgument, diagnostic.Code);
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
        Assert.Equal(DiagnosticCodes.UnresolvablePropertyName, diagnostic.Code);
        Assert.Equal("Person", diagnostic.EntityName);
        Assert.Null(diagnostic.PropertyName);
    }

    private const string SourceWithParenthesizedLambda = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.Property((Person e) => e.Name).HasMaxLength(100);
                });
            }
        }
        """;

    [Fact]
    public void ParseMaxLengths_ParenthesizedLambda_ResolvesPropertyName()
    {
        var result = new FluentConfigParser().ParseMaxLengths(SourceWithParenthesizedLambda);

        Assert.Empty(result.Diagnostics);
        var config = Assert.Single(result.Value);
        Assert.Equal("Person", config.EntityName);
        Assert.Equal("Name", config.PropertyName);
        Assert.Equal(100, config.MaxLength);
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
        Assert.Equal(DiagnosticCodes.UnreadableIsRequiredArgument, diagnostic.Code);
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
        Assert.Equal(DiagnosticCodes.UnresolvablePropertyName, diagnostic.Code);
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
        Assert.Equal(DiagnosticCodes.UnreadableHasKeyArgument, diagnostic.Code);
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
        Assert.Equal(DiagnosticCodes.UnreadableHasKeyArgument, diagnostic.Code);
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

    // ─── ParseIndexes ────────────────────────────────────────────────────────────

    [Fact]
    public void ParseIndexes_SinglePropertyLambda_NoUniqueNoName()
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

        var result = new FluentConfigParser().ParseIndexes(source);

        var config = Assert.Single(result.Value);
        Assert.Equal("Person", config.EntityName);
        Assert.Equal(new[] { "Email" }, config.PropertyNames);
        Assert.False(config.IsUnique);
        Assert.Null(config.Name);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void ParseIndexes_CompositeLambda_PreservesColumnOrder()
    {
        const string source = """
            class Ctx : DbContext {
                protected override void OnModelCreating(ModelBuilder modelBuilder) {
                    modelBuilder.Entity<Person>(entity => {
                        entity.HasIndex(e => new { e.LastName, e.FirstName });
                    });
                }
            }
            """;

        var result = new FluentConfigParser().ParseIndexes(source);

        var config = Assert.Single(result.Value);
        Assert.Equal(new[] { "LastName", "FirstName" }, config.PropertyNames);
        Assert.False(config.IsUnique);
        Assert.Null(config.Name);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void ParseIndexes_SingleStringParam_ReadsColumnName()
    {
        const string source = """
            class Ctx : DbContext {
                protected override void OnModelCreating(ModelBuilder modelBuilder) {
                    modelBuilder.Entity<Person>(entity => {
                        entity.HasIndex("Email");
                    });
                }
            }
            """;

        var result = new FluentConfigParser().ParseIndexes(source);

        var config = Assert.Single(result.Value);
        Assert.Equal(new[] { "Email" }, config.PropertyNames);
        Assert.Null(config.Name);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void ParseIndexes_BareStringParamsComposite_ReadsAllColumnNames()
    {
        const string source = """
            class Ctx : DbContext {
                protected override void OnModelCreating(ModelBuilder modelBuilder) {
                    modelBuilder.Entity<Person>(entity => {
                        entity.HasIndex("LastName", "FirstName");
                    });
                }
            }
            """;

        var result = new FluentConfigParser().ParseIndexes(source);

        var config = Assert.Single(result.Value);
        Assert.Equal(new[] { "LastName", "FirstName" }, config.PropertyNames);
        Assert.Null(config.Name);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void ParseIndexes_LambdaWithIndexName_ReadsNameFromSecondArg()
    {
        const string source = """
            class Ctx : DbContext {
                protected override void OnModelCreating(ModelBuilder modelBuilder) {
                    modelBuilder.Entity<Person>(entity => {
                        entity.HasIndex(e => e.Email, "IX_Person_Email");
                    });
                }
            }
            """;

        var result = new FluentConfigParser().ParseIndexes(source);

        var config = Assert.Single(result.Value);
        Assert.Equal(new[] { "Email" }, config.PropertyNames);
        Assert.Equal("IX_Person_Email", config.Name);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void ParseIndexes_StringArrayWithIndexName_ReadsColumnsAndName()
    {
        const string source = """
            class Ctx : DbContext {
                protected override void OnModelCreating(ModelBuilder modelBuilder) {
                    modelBuilder.Entity<Person>(entity => {
                        entity.HasIndex(new[] { "LastName", "FirstName" }, "IX_Person_Name");
                    });
                }
            }
            """;

        var result = new FluentConfigParser().ParseIndexes(source);

        var config = Assert.Single(result.Value);
        Assert.Equal(new[] { "LastName", "FirstName" }, config.PropertyNames);
        Assert.Equal("IX_Person_Name", config.Name);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void ParseIndexes_IsUnique_Bare_SetsFlagTrue()
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

        var result = new FluentConfigParser().ParseIndexes(source);

        var config = Assert.Single(result.Value);
        Assert.Equal(new[] { "Email" }, config.PropertyNames);
        Assert.True(config.IsUnique);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void ParseIndexes_IsUnique_ExplicitFalse_SetsFlagFalse()
    {
        const string source = """
            class Ctx : DbContext {
                protected override void OnModelCreating(ModelBuilder modelBuilder) {
                    modelBuilder.Entity<Person>(entity => {
                        entity.HasIndex(e => e.Email).IsUnique(false);
                    });
                }
            }
            """;

        var result = new FluentConfigParser().ParseIndexes(source);

        var config = Assert.Single(result.Value);
        Assert.False(config.IsUnique);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void ParseIndexes_IsUnique_NonBoolLiteralArg_EmitsDiagnosticAndDefaultsFalse()
    {
        const string source = """
            class Ctx : DbContext {
                protected override void OnModelCreating(ModelBuilder modelBuilder) {
                    modelBuilder.Entity<Person>(entity => {
                        entity.HasIndex(e => e.Email).IsUnique(someVariable);
                    });
                }
            }
            """;

        var result = new FluentConfigParser().ParseIndexes(source);

        var config = Assert.Single(result.Value);
        Assert.False(config.IsUnique);
        var diag = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.UnreadableIsUniqueArgument, diag.Code);
        Assert.Equal("Person", diag.EntityName);
        Assert.Null(diag.PropertyName);
    }

    [Fact]
    public void ParseIndexes_ExplicitNameAnonymousMember_EmitsDiagnosticAndSkipsIndex()
    {
        const string source = """
            class Ctx : DbContext {
                protected override void OnModelCreating(ModelBuilder modelBuilder) {
                    modelBuilder.Entity<Person>(entity => {
                        entity.HasIndex(e => new { Key = e.Email });
                    });
                }
            }
            """;

        var result = new FluentConfigParser().ParseIndexes(source);

        Assert.Empty(result.Value);
        var diag = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.UnreadableHasIndexArgument, diag.Code);
        Assert.Equal("Person", diag.EntityName);
        Assert.Null(diag.PropertyName);
    }

    [Fact]
    public void ParseIndexes_MultipleHasIndexCalls_AllAppearInResult()
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

        var result = new FluentConfigParser().ParseIndexes(source);

        Assert.Equal(2, result.Value.Count);
        Assert.Equal(new[] { "Email" }, result.Value[0].PropertyNames);
        Assert.True(result.Value[0].IsUnique);
        Assert.Equal(new[] { "LastName", "FirstName" }, result.Value[1].PropertyNames);
        Assert.False(result.Value[1].IsUnique);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void ParseIndexes_NoHasIndexCalls_ReturnsEmpty()
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

        var result = new FluentConfigParser().ParseIndexes(source);

        Assert.Empty(result.Value);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void ParseIndexes_StringArrayWithoutIndexName_ReadsColumnsOnly()
    {
        const string source = """
            class Ctx : DbContext {
                protected override void OnModelCreating(ModelBuilder modelBuilder) {
                    modelBuilder.Entity<Person>(entity => {
                        entity.HasIndex(new[] { "LastName", "FirstName" });
                    });
                }
            }
            """;

        var result = new FluentConfigParser().ParseIndexes(source);

        var config = Assert.Single(result.Value);
        Assert.Equal(new[] { "LastName", "FirstName" }, config.PropertyNames);
        Assert.Null(config.Name);
        Assert.False(config.IsUnique);
        Assert.Empty(result.Diagnostics);
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
    public void ParsePrecisions_ReadsPrecisionOnlyAndPrecisionWithScale()
    {
        var result = new FluentConfigParser().ParsePrecisions(PrecisionSource);

        Assert.Empty(result.Diagnostics);
        Assert.Equal(2, result.Value.Count);
        Assert.Contains(result.Value, c => c is { EntityName: "Order", PropertyName: "Total", Precision: 18, Scale: 2 });
        Assert.Contains(result.Value, c => c is { EntityName: "Order", PropertyName: "Rate", Precision: 5, Scale: null });
    }

    private const string PrecisionSourceWithNonLiteralFirstArg = """
        public class AppDbContext : DbContext
        {
            private const int DefaultPrecision = 18;

            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Order>(entity =>
                {
                    entity.Property(e => e.Total).HasPrecision(DefaultPrecision, 2);
                });
            }
        }
        """;

    [Fact]
    public void ParsePrecisions_NonLiteralFirstArgument_EmitsUnreadableHasPrecisionArgumentDiagnostic()
    {
        var result = new FluentConfigParser().ParsePrecisions(PrecisionSourceWithNonLiteralFirstArg);

        Assert.Empty(result.Value);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.UnreadableHasPrecisionArgument, diagnostic.Code);
        Assert.Equal("Order", diagnostic.EntityName);
        Assert.Equal("Total", diagnostic.PropertyName);
    }

    private const string PrecisionSourceWithNonLiteralSecondArg = """
        public class AppDbContext : DbContext
        {
            private const int DefaultScale = 2;

            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Order>(entity =>
                {
                    entity.Property(e => e.Total).HasPrecision(18, DefaultScale);
                });
            }
        }
        """;

    [Fact]
    public void ParsePrecisions_NonLiteralSecondArgument_EmitsUnreadableHasPrecisionArgumentDiagnostic()
    {
        var result = new FluentConfigParser().ParsePrecisions(PrecisionSourceWithNonLiteralSecondArg);

        Assert.Empty(result.Value);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.UnreadableHasPrecisionArgument, diagnostic.Code);
        Assert.Equal("Order", diagnostic.EntityName);
        Assert.Equal("Total", diagnostic.PropertyName);
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

                modelBuilder.Entity<Address>(entity =>
                {
                    entity.ToTable("Addresses");
                });
            }
        }
        """;

    [Fact]
    public void ParseTableMappings_ReadsTableNameOnly_AndTableNameWithSchema()
    {
        var result = new FluentConfigParser().ParseTableMappings(TableMappingSource);

        Assert.Empty(result.Diagnostics);
        Assert.Equal(2, result.Value.Count);
        Assert.Contains(result.Value, c => c is { EntityName: "Person", TableName: "People", Schema: "dbo" });
        Assert.Contains(result.Value, c => c is { EntityName: "Address", TableName: "Addresses", Schema: null });
    }

    private const string TableMappingSourceWithNonLiteralArg = """
        public class AppDbContext : DbContext
        {
            private const string TableName = "People";

            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.ToTable(TableName);
                });
            }
        }
        """;

    [Fact]
    public void ParseTableMappings_NonLiteralArgument_EmitsUnreadableToTableArgumentDiagnostic()
    {
        var result = new FluentConfigParser().ParseTableMappings(TableMappingSourceWithNonLiteralArg);

        Assert.Empty(result.Value);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.UnreadableToTableArgument, diagnostic.Code);
        Assert.Equal("Person", diagnostic.EntityName);
        Assert.Null(diagnostic.PropertyName);
    }

    private const string ColumnNameSource = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.Property(e => e.Name).HasColumnName("full_name");
                });
            }
        }
        """;

    [Fact]
    public void ParseColumnNames_ReadsStringLiteralArgument()
    {
        var result = new FluentConfigParser().ParseColumnNames(ColumnNameSource);

        Assert.Empty(result.Diagnostics);
        var config = Assert.Single(result.Value);
        Assert.Equal("Person", config.EntityName);
        Assert.Equal("Name", config.PropertyName);
        Assert.Equal("full_name", config.ColumnName);
    }

    private const string ColumnNameSourceWithNonLiteralArg = """
        public class AppDbContext : DbContext
        {
            private const string ColumnName = "full_name";

            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.Property(e => e.Name).HasColumnName(ColumnName);
                });
            }
        }
        """;

    [Fact]
    public void ParseColumnNames_NonLiteralArgument_EmitsUnreadableHasColumnNameArgumentDiagnostic()
    {
        var result = new FluentConfigParser().ParseColumnNames(ColumnNameSourceWithNonLiteralArg);

        Assert.Empty(result.Value);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.UnreadableHasColumnNameArgument, diagnostic.Code);
        Assert.Equal("Person", diagnostic.EntityName);
        Assert.Equal("Name", diagnostic.PropertyName);
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
    public void ParseColumnTypes_ReadsStringLiteralArgument()
    {
        var result = new FluentConfigParser().ParseColumnTypes(ColumnTypeSource);

        Assert.Empty(result.Diagnostics);
        var config = Assert.Single(result.Value);
        Assert.Equal("Order", config.EntityName);
        Assert.Equal("Total", config.PropertyName);
        Assert.Equal("decimal(18,2)", config.ColumnType);
    }

    private const string ColumnTypeSourceWithNonLiteralArg = """
        public class AppDbContext : DbContext
        {
            private const string ColumnType = "decimal(18,2)";

            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Order>(entity =>
                {
                    entity.Property(e => e.Total).HasColumnType(ColumnType);
                });
            }
        }
        """;

    [Fact]
    public void ParseColumnTypes_NonLiteralArgument_EmitsUnreadableHasColumnTypeArgumentDiagnostic()
    {
        var result = new FluentConfigParser().ParseColumnTypes(ColumnTypeSourceWithNonLiteralArg);

        Assert.Empty(result.Value);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.UnreadableHasColumnTypeArgument, diagnostic.Code);
        Assert.Equal("Order", diagnostic.EntityName);
        Assert.Equal("Total", diagnostic.PropertyName);
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
                    entity.Property(e => e.IsArchived).HasDefaultValue(false);
                    entity.Property(e => e.CanceledAt).HasDefaultValue(null);
                });
            }
        }
        """;

    [Fact]
    public void ParseDefaultValues_ReadsNumericStringBoolAndNullLiterals()
    {
        var result = new FluentConfigParser().ParseDefaultValues(DefaultValueSource);

        Assert.Empty(result.Diagnostics);
        Assert.Equal(4, result.Value.Count);
        Assert.Contains(result.Value, c => c is { EntityName: "Order", PropertyName: "Quantity", LiteralText: "1" });
        Assert.Contains(result.Value, c => c is { EntityName: "Order", PropertyName: "Status", LiteralText: "\"pending\"" });
        Assert.Contains(result.Value, c => c is { EntityName: "Order", PropertyName: "IsArchived", LiteralText: "false" });
        Assert.Contains(result.Value, c => c is { EntityName: "Order", PropertyName: "CanceledAt", LiteralText: "null" });
    }

    private const string DefaultValueSourceWithMemberAccessArg = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Order>(entity =>
                {
                    entity.Property(e => e.CreatedAt).HasDefaultValue(DateTime.UtcNow);
                });
            }
        }
        """;

    [Fact]
    public void ParseDefaultValues_MemberAccessArgument_EmitsUnreadableHasDefaultValueArgumentDiagnostic()
    {
        var result = new FluentConfigParser().ParseDefaultValues(DefaultValueSourceWithMemberAccessArg);

        Assert.Empty(result.Value);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.UnreadableHasDefaultValueArgument, diagnostic.Code);
        Assert.Equal("Order", diagnostic.EntityName);
        Assert.Equal("CreatedAt", diagnostic.PropertyName);
    }

    // ─── ParseRelationships ─────────────────────────────────────────────────────

    private static readonly IReadOnlyList<EntityModel> OrderCustomerEntities = new List<EntityModel>
    {
        new("Customer", new List<PropertyModel>
        {
            new("Id", "int", IsNullable: false, MaxLength: null),
            new("Orders", "ICollection<Order>", IsNullable: false, MaxLength: null),
        }),
        new("Order", new List<PropertyModel>
        {
            new("Id", "int", IsNullable: false, MaxLength: null),
            new("CustomerId", "int", IsNullable: false, MaxLength: null),
            new("Customer", "Customer", IsNullable: false, MaxLength: null),
        }),
    };

    private const string SourceWithHasOneWithManyBlockNested = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Order>(entity =>
                {
                    entity.HasOne(d => d.Customer)
                          .WithMany(p => p.Orders)
                          .HasForeignKey(d => d.CustomerId);
                });
            }
        }
        """;

    [Fact]
    public void ParseRelationships_HasOneWithMany_BlockNested_ResolvesOneToMany()
    {
        var result = new FluentConfigParser().ParseRelationships(SourceWithHasOneWithManyBlockNested, OrderCustomerEntities);

        Assert.Empty(result.Diagnostics);
        var relationship = Assert.Single(result.Value);
        Assert.Equal(RelationshipKind.OneToMany, relationship.Kind);
        Assert.Equal("Customer", relationship.PrincipalEntity);
        Assert.Equal("Order", relationship.DependentEntity);
        Assert.Equal("Orders", relationship.PrincipalNavigation);
        Assert.Equal("Customer", relationship.DependentNavigation);
        Assert.Equal(new[] { "CustomerId" }, relationship.ForeignKeyProperties);
    }

    private const string SourceWithHasOneWithManyChained = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Order>()
                    .HasOne(d => d.Customer)
                    .WithMany(p => p.Orders)
                    .HasForeignKey(d => d.CustomerId);
            }
        }
        """;

    [Fact]
    public void ParseRelationships_HasOneWithMany_ChainedOffBareEntity_ResolvesOneToMany()
    {
        var result = new FluentConfigParser().ParseRelationships(SourceWithHasOneWithManyChained, OrderCustomerEntities);

        Assert.Empty(result.Diagnostics);
        var relationship = Assert.Single(result.Value);
        Assert.Equal(RelationshipKind.OneToMany, relationship.Kind);
        Assert.Equal("Customer", relationship.PrincipalEntity);
        Assert.Equal("Order", relationship.DependentEntity);
        Assert.Equal(new[] { "CustomerId" }, relationship.ForeignKeyProperties);
    }

    [Fact]
    public void ParseRelationships_HasManyWithOne_ResolvesOneToMany_PrincipalIsConfiguringEntity()
    {
        const string source = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Customer>(entity =>
                    {
                        entity.HasMany(p => p.Orders)
                              .WithOne(d => d.Customer)
                              .HasForeignKey(d => d.CustomerId);
                    });
                }
            }
            """;

        var result = new FluentConfigParser().ParseRelationships(source, OrderCustomerEntities);

        Assert.Empty(result.Diagnostics);
        var relationship = Assert.Single(result.Value);
        Assert.Equal(RelationshipKind.OneToMany, relationship.Kind);
        Assert.Equal("Customer", relationship.PrincipalEntity);
        Assert.Equal("Order", relationship.DependentEntity);
        Assert.Equal("Orders", relationship.PrincipalNavigation);
        Assert.Equal("Customer", relationship.DependentNavigation);
        Assert.Equal(new[] { "CustomerId" }, relationship.ForeignKeyProperties);
    }

    [Fact]
    public void ParseRelationships_BareWithMany_NoInverseNavigation_PrincipalNavigationIsNull()
    {
        const string source = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Order>(entity =>
                    {
                        entity.HasOne(d => d.Customer).WithMany();
                    });
                }
            }
            """;

        var result = new FluentConfigParser().ParseRelationships(source, OrderCustomerEntities);

        Assert.Empty(result.Diagnostics);
        var relationship = Assert.Single(result.Value);
        Assert.Null(relationship.PrincipalNavigation);
        Assert.Equal("Customer", relationship.DependentNavigation);
        Assert.Empty(relationship.ForeignKeyProperties);
    }

    [Fact]
    public void ParseRelationships_NoHasForeignKeyCall_ForeignKeyPropertiesEmpty_NoDiagnostic()
    {
        const string source = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Order>(entity =>
                    {
                        entity.HasOne(d => d.Customer).WithMany(p => p.Orders);
                    });
                }
            }
            """;

        var result = new FluentConfigParser().ParseRelationships(source, OrderCustomerEntities);

        Assert.Empty(result.Diagnostics);
        var relationship = Assert.Single(result.Value);
        Assert.Empty(relationship.ForeignKeyProperties);
    }

    [Fact]
    public void ParseRelationships_ExplicitGenericTarget_NoNavLambda_ResolvesEntity()
    {
        const string source = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Order>(entity =>
                    {
                        entity.HasOne<Customer>().WithMany();
                    });
                }
            }
            """;

        var result = new FluentConfigParser().ParseRelationships(source, OrderCustomerEntities);

        Assert.Empty(result.Diagnostics);
        var relationship = Assert.Single(result.Value);
        Assert.Equal("Customer", relationship.PrincipalEntity);
        Assert.Equal("Order", relationship.DependentEntity);
    }

    [Fact]
    public void ParseRelationships_MalformedChain_NoWithCall_SkippedSilently()
    {
        const string source = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Order>(entity =>
                    {
                        entity.HasOne(d => d.Customer);
                    });
                }
            }
            """;

        var result = new FluentConfigParser().ParseRelationships(source, OrderCustomerEntities);

        Assert.Empty(result.Diagnostics);
        Assert.Empty(result.Value);
    }

    [Fact]
    public void ParseRelationships_UnresolvableNavigation_EmitsUnresolvableRelationshipTargetDiagnostic()
    {
        const string source = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Order>(entity =>
                    {
                        entity.HasOne(d => d.NoSuchProperty).WithMany(p => p.Orders);
                    });
                }
            }
            """;

        var result = new FluentConfigParser().ParseRelationships(source, OrderCustomerEntities);

        Assert.Empty(result.Value);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.UnresolvableRelationshipTarget, diagnostic.Code);
        Assert.Equal("Order", diagnostic.EntityName);
    }

    [Fact]
    public void ParseRelationships_UnrecognizedCollectionWrapper_EmitsUnresolvableRelationshipTargetDiagnostic()
    {
        var entities = new List<EntityModel>
        {
            new("Customer", new List<PropertyModel>
            {
                new("Orders", "IQueryable<Order>", IsNullable: false, MaxLength: null),
            }),
            new("Order", new List<PropertyModel>()),
        };
        const string source = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Customer>(entity =>
                    {
                        entity.HasMany(p => p.Orders).WithOne();
                    });
                }
            }
            """;

        var result = new FluentConfigParser().ParseRelationships(source, entities);

        Assert.Empty(result.Value);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.UnresolvableRelationshipTarget, diagnostic.Code);
    }

    [Fact]
    public void ParseRelationships_UnreadableHasForeignKeyArgument_EmitsDiagnostic_RelationshipStillRecorded()
    {
        const string source = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Order>(entity =>
                    {
                        entity.HasOne(d => d.Customer).WithMany(p => p.Orders).HasForeignKey(GetFkExpression());
                    });
                }
            }
            """;

        var result = new FluentConfigParser().ParseRelationships(source, OrderCustomerEntities);

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.UnreadableHasForeignKeyArgument, diagnostic.Code);
        Assert.Equal("Order", diagnostic.EntityName);
        var relationship = Assert.Single(result.Value);
        Assert.Empty(relationship.ForeignKeyProperties);
    }

    [Fact]
    public void ParseRelationships_OnDelete_Present_IsRead()
    {
        const string source = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Order>(entity =>
                    {
                        entity.HasOne(d => d.Customer)
                              .WithMany(p => p.Orders)
                              .HasForeignKey(d => d.CustomerId)
                              .OnDelete(DeleteBehavior.Cascade);
                    });
                }
            }
            """;

        var result = new FluentConfigParser().ParseRelationships(source, OrderCustomerEntities);

        Assert.Empty(result.Diagnostics);
        var relationship = Assert.Single(result.Value);
        Assert.Equal("Cascade", relationship.OnDeleteBehavior);
    }

    [Fact]
    public void ParseRelationships_OnDelete_UnreadableArgument_EmitsDiagnostic()
    {
        const string source = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Order>(entity =>
                    {
                        entity.HasOne(d => d.Customer)
                              .WithMany(p => p.Orders)
                              .OnDelete(GetBehavior());
                    });
                }
            }
            """;

        var result = new FluentConfigParser().ParseRelationships(source, OrderCustomerEntities);

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.UnreadableOnDeleteArgument, diagnostic.Code);
        var relationship = Assert.Single(result.Value);
        Assert.Null(relationship.OnDeleteBehavior);
    }

    [Fact]
    public void ParseRelationships_HasForeignKeyAndOnDelete_OrderReversed_BothStillRead()
    {
        const string source = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Order>(entity =>
                    {
                        entity.HasOne(d => d.Customer)
                              .WithMany(p => p.Orders)
                              .OnDelete(DeleteBehavior.Restrict)
                              .HasForeignKey(d => d.CustomerId);
                    });
                }
            }
            """;

        var result = new FluentConfigParser().ParseRelationships(source, OrderCustomerEntities);

        Assert.Empty(result.Diagnostics);
        var relationship = Assert.Single(result.Value);
        Assert.Equal(new[] { "CustomerId" }, relationship.ForeignKeyProperties);
        Assert.Equal("Restrict", relationship.OnDeleteBehavior);
    }

    private static readonly IReadOnlyList<EntityModel> PersonAddressEntities = new List<EntityModel>
    {
        new("Person", new List<PropertyModel>
        {
            new("Id", "int", IsNullable: false, MaxLength: null),
            new("Address", "Address", IsNullable: true, MaxLength: null),
        }),
        new("Address", new List<PropertyModel>
        {
            new("Id", "int", IsNullable: false, MaxLength: null),
            new("PersonId", "int", IsNullable: false, MaxLength: null),
            new("Person", "Person", IsNullable: false, MaxLength: null),
        }),
    };

    [Fact]
    public void ParseRelationships_HasOneWithOne_ExplicitForeignKeyGeneric_ResolvesDependent()
    {
        const string source = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Person>(entity =>
                    {
                        entity.HasOne(p => p.Address)
                              .WithOne(a => a.Person)
                              .HasForeignKey<Address>(a => a.PersonId);
                    });
                }
            }
            """;

        var result = new FluentConfigParser().ParseRelationships(source, PersonAddressEntities);

        Assert.Empty(result.Diagnostics);
        var relationship = Assert.Single(result.Value);
        Assert.Equal(RelationshipKind.OneToOne, relationship.Kind);
        Assert.Equal("Person", relationship.PrincipalEntity);
        Assert.Equal("Address", relationship.DependentEntity);
        Assert.Equal("Address", relationship.PrincipalNavigation);
        Assert.Equal("Person", relationship.DependentNavigation);
        Assert.Equal(new[] { "PersonId" }, relationship.ForeignKeyProperties);
    }

    [Fact]
    public void ParseRelationships_HasOneWithOne_NoExplicitGeneric_DefaultsDependentToConfiguringEntity()
    {
        const string source = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Address>(entity =>
                    {
                        entity.HasOne(a => a.Person)
                              .WithOne(p => p.Address)
                              .HasForeignKey(a => a.PersonId);
                    });
                }
            }
            """;

        var result = new FluentConfigParser().ParseRelationships(source, PersonAddressEntities);

        Assert.Empty(result.Diagnostics);
        var relationship = Assert.Single(result.Value);
        Assert.Equal(RelationshipKind.OneToOne, relationship.Kind);
        Assert.Equal("Address", relationship.DependentEntity);
        Assert.Equal("Person", relationship.PrincipalEntity);
        Assert.Equal("Person", relationship.DependentNavigation);
        Assert.Equal("Address", relationship.PrincipalNavigation);
    }

    private static readonly IReadOnlyList<EntityModel> PostTagEntities = new List<EntityModel>
    {
        new("Post", new List<PropertyModel>
        {
            new("Id", "int", IsNullable: false, MaxLength: null),
            new("Tags", "ICollection<Tag>", IsNullable: false, MaxLength: null),
        }),
        new("Tag", new List<PropertyModel>
        {
            new("Id", "int", IsNullable: false, MaxLength: null),
            new("Posts", "ICollection<Post>", IsNullable: false, MaxLength: null),
        }),
    };

    [Fact]
    public void ParseRelationships_HasManyWithMany_ResolvesManyToMany()
    {
        const string source = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Post>(entity =>
                    {
                        entity.HasMany(p => p.Tags).WithMany(t => t.Posts);
                    });
                }
            }
            """;

        var result = new FluentConfigParser().ParseRelationships(source, PostTagEntities);

        Assert.Empty(result.Diagnostics);
        var relationship = Assert.Single(result.Value);
        Assert.Equal(RelationshipKind.ManyToMany, relationship.Kind);
        Assert.Equal("Post", relationship.PrincipalEntity);
        Assert.Equal("Tag", relationship.DependentEntity);
        Assert.Equal("Tags", relationship.PrincipalNavigation);
        Assert.Equal("Posts", relationship.DependentNavigation);
        Assert.Empty(relationship.ForeignKeyProperties);
        Assert.Null(relationship.OnDeleteBehavior);
        Assert.Null(relationship.JoinEntityName);
    }

    [Fact]
    public void ParseRelationships_HasManyWithMany_ExplicitUsingEntityGeneric_SetsJoinEntityName()
    {
        const string source = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Post>(entity =>
                    {
                        entity.HasMany(p => p.Tags).WithMany(t => t.Posts).UsingEntity<PostTag>();
                    });
                }
            }
            """;

        var result = new FluentConfigParser().ParseRelationships(source, PostTagEntities);

        Assert.Empty(result.Diagnostics);
        var relationship = Assert.Single(result.Value);
        Assert.Equal("PostTag", relationship.JoinEntityName);
    }

    [Fact]
    public void ParseRelationships_HasManyWithMany_BareUsingEntity_JoinEntityNameNull_NoDiagnostic()
    {
        const string source = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Post>(entity =>
                    {
                        entity.HasMany(p => p.Tags).WithMany(t => t.Posts).UsingEntity(j => j.ToTable("PostTags"));
                    });
                }
            }
            """;

        var result = new FluentConfigParser().ParseRelationships(source, PostTagEntities);

        Assert.Empty(result.Diagnostics);
        var relationship = Assert.Single(result.Value);
        Assert.Null(relationship.JoinEntityName);
    }
}
