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
        var configs = new FluentConfigParser().ParseMaxLengths(Source);

        Assert.Equal(3, configs.Count);
        Assert.Contains(configs, c => c is { EntityName: "Person", PropertyName: "Name", MaxLength: 100 });
        Assert.Contains(configs, c => c is { EntityName: "Person", PropertyName: "Email", MaxLength: 255 });
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
    public void ParseMaxLengths_NestedEntityConfig_DoesNotAttributeNestedCallsToOuterEntity()
    {
        var configs = new FluentConfigParser().ParseMaxLengths(SourceWithNestedEntityConfig);

        Assert.Equal(2, configs.Count);
        Assert.Contains(configs, c => c is { EntityName: "Person", PropertyName: "Name", MaxLength: 100 });
        Assert.Contains(configs, c => c is { EntityName: "Address", PropertyName: "Line1", MaxLength: 200 });
        Assert.DoesNotContain(configs, c => c.EntityName == "Person" && c.PropertyName == "Line1");
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
}
