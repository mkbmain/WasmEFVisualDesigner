using System.Linq;
using EfSchemaVisualizer.Core.Parsing;
using EfSchemaVisualizer.Web;
using Xunit;

namespace EfSchemaVisualizer.Web.Tests;

public class DiagramModelBuilderValidityTests
{
    [Fact]
    public void Build_EntityWithNoKeyAndNotMarkedKeyless_EmitsEntityHasNoKey()
    {
        const string classSource = """
            public class AuditLog
            {
                public string Message { get; set; }
            }
            """;

        const string configSource = """
            public class AppDbContext : DbContext
            {
            }
            """;

        var result = DiagramModelBuilder.Build(classSource, configSource);

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.EntityHasNoKey, diagnostic.Code);
        Assert.Equal(DiagnosticCategory.ModelValidity, diagnostic.Category);
        Assert.Equal("AuditLog", diagnostic.EntityName);
    }

    [Fact]
    public void Build_EntityMarkedHasNoKey_NoEntityHasNoKeyDiagnostic()
    {
        const string classSource = """
            public class AuditLog
            {
                public string Message { get; set; }
            }
            """;

        const string configSource = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<AuditLog>(entity =>
                    {
                        entity.HasNoKey();
                    });
                }
            }
            """;

        var result = DiagramModelBuilder.Build(classSource, configSource);

        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Build_TwoPropertiesMapToSameColumnName_EmitsDuplicateColumnName()
    {
        const string classSource = """
            public class Customer
            {
                public int Id { get; set; }

                [Column("Label")]
                public string Name { get; set; }

                [Column("Label")]
                public string Nickname { get; set; }
            }
            """;

        const string configSource = """
            public class AppDbContext : DbContext
            {
            }
            """;

        var result = DiagramModelBuilder.Build(classSource, configSource);

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.DuplicateColumnName, diagnostic.Code);
        Assert.Equal(DiagnosticCategory.ModelValidity, diagnostic.Category);
        Assert.Equal("Customer", diagnostic.EntityName);
    }

    [Fact]
    public void Build_IsRequiredFalseOnNonNullableValueType_EmitsDiagnostic()
    {
        const string classSource = """
            public class Order
            {
                public int Id { get; set; }
                public int Total { get; set; }
            }
            """;

        const string configSource = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Order>(entity =>
                    {
                        entity.Property(e => e.Total).IsRequired(false);
                    });
                }
            }
            """;

        var result = DiagramModelBuilder.Build(classSource, configSource);

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.IsRequiredFalseOnNonNullableProperty, diagnostic.Code);
        Assert.Equal(DiagnosticCategory.ModelValidity, diagnostic.Category);
        Assert.Equal("Order", diagnostic.EntityName);
        Assert.Equal("Total", diagnostic.PropertyName);
    }

    [Fact]
    public void Build_IsRequiredTrueOnNullableValueType_NoDiagnostic()
    {
        const string classSource = """
            public class Order
            {
                public int Id { get; set; }
                public int? Total { get; set; }
            }
            """;

        const string configSource = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Order>(entity =>
                    {
                        entity.Property(e => e.Total).IsRequired();
                    });
                }
            }
            """;

        var result = DiagramModelBuilder.Build(classSource, configSource);

        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Build_IsRequiredFalseOnNullableValueType_NoDiagnostic()
    {
        const string classSource = """
            public class Order
            {
                public int Id { get; set; }
                public int? Total { get; set; }
            }
            """;

        const string configSource = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Order>(entity =>
                    {
                        entity.Property(e => e.Total).IsRequired(false);
                    });
                }
            }
            """;

        var result = DiagramModelBuilder.Build(classSource, configSource);

        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Build_PrecisionOnNonDecimalNonTemporalProperty_EmitsDiagnostic()
    {
        const string classSource = """
            public class Order
            {
                public int Id { get; set; }
                public int Quantity { get; set; }
            }
            """;

        const string configSource = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Order>(entity =>
                    {
                        entity.Property(e => e.Quantity).HasPrecision(5);
                    });
                }
            }
            """;

        var result = DiagramModelBuilder.Build(classSource, configSource);

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.PrecisionOrScaleOnUnsupportedType, diagnostic.Code);
        Assert.Equal(DiagnosticCategory.ModelValidity, diagnostic.Category);
    }

    [Fact]
    public void Build_PrecisionOnDateTimeProperty_NoDiagnostic()
    {
        const string classSource = """
            public class Order
            {
                public int Id { get; set; }
                public DateTime PlacedAt { get; set; }
            }
            """;

        const string configSource = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Order>(entity =>
                    {
                        entity.Property(e => e.PlacedAt).HasPrecision(3);
                    });
                }
            }
            """;

        var result = DiagramModelBuilder.Build(classSource, configSource);

        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Build_ScaleOnDateTimeProperty_EmitsDiagnostic()
    {
        const string classSource = """
            public class Order
            {
                public int Id { get; set; }
                public DateTime PlacedAt { get; set; }
            }
            """;

        const string configSource = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Order>(entity =>
                    {
                        entity.Property(e => e.PlacedAt).HasPrecision(3, 1);
                    });
                }
            }
            """;

        var result = DiagramModelBuilder.Build(classSource, configSource);

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.PrecisionOrScaleOnUnsupportedType, diagnostic.Code);
    }

    [Fact]
    public void Build_PrecisionAndScaleOnDecimalProperty_NoDiagnostic()
    {
        const string classSource = """
            public class Order
            {
                public int Id { get; set; }
                public decimal Total { get; set; }
            }
            """;

        const string configSource = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Order>(entity =>
                    {
                        entity.Property(e => e.Total).HasPrecision(18, 2);
                    });
                }
            }
            """;

        var result = DiagramModelBuilder.Build(classSource, configSource);

        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Build_PrincipalKeyReferencesRemovedProperty_EmitsDiagnostic()
    {
        const string classSource = """
            public class Customer
            {
                public int Id { get; set; }
                public string Email { get; set; }
                public ICollection<Order> Orders { get; set; }
            }

            public class Order
            {
                public int Id { get; set; }
                public string CustomerCode { get; set; }
                public Customer Customer { get; set; }
            }
            """;

        const string configSource = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Order>(entity =>
                    {
                        entity.HasOne(o => o.Customer)
                            .WithMany(c => c.Orders)
                            .HasForeignKey(o => o.CustomerCode)
                            .HasPrincipalKey(c => c.Code);
                    });
                }
            }
            """;

        var result = DiagramModelBuilder.Build(classSource, configSource);

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.PrincipalKeyReferencesMissingProperty, diagnostic.Code);
    }

    [Fact]
    public void Build_PrincipalKeyReferencesExistingNonKeyProperty_NoDiagnostic()
    {
        const string classSource = """
            public class Customer
            {
                public int Id { get; set; }
                public string Code { get; set; }
                public ICollection<Order> Orders { get; set; }
            }

            public class Order
            {
                public int Id { get; set; }
                public string CustomerCode { get; set; }
                public Customer Customer { get; set; }
            }
            """;

        const string configSource = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Order>(entity =>
                    {
                        entity.HasOne(o => o.Customer)
                            .WithMany(c => c.Orders)
                            .HasForeignKey(o => o.CustomerCode)
                            .HasPrincipalKey(c => c.Code);
                    });
                }
            }
            """;

        var result = DiagramModelBuilder.Build(classSource, configSource);

        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Build_IndexReferencesRemovedProperty_EmitsDiagnostic()
    {
        const string classSource = """
            public class Customer
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
                    modelBuilder.Entity<Customer>(entity =>
                    {
                        entity.HasIndex(e => e.Phone);
                    });
                }
            }
            """;

        var result = DiagramModelBuilder.Build(classSource, configSource);

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.IndexReferencesMissingProperty, diagnostic.Code);
        Assert.Equal(DiagnosticCategory.ModelValidity, diagnostic.Category);
        Assert.Equal("Customer", diagnostic.EntityName);
    }

    [Fact]
    public void Build_IndexReferencesExistingProperty_NoDiagnostic()
    {
        const string classSource = """
            public class Customer
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
                    modelBuilder.Entity<Customer>(entity =>
                    {
                        entity.HasIndex(e => e.Email);
                    });
                }
            }
            """;

        var result = DiagramModelBuilder.Build(classSource, configSource);

        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Build_ForeignKeyTargetsKeylessPrincipal_EmitsDiagnostic()
    {
        const string classSource = """
            public class Blog
            {
                public string Title { get; set; }
                public ICollection<Post> Posts { get; set; }
            }

            public class Post
            {
                public int Id { get; set; }
                public int BlogId { get; set; }
                public Blog Blog { get; set; }
            }
            """;

        const string configSource = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Blog>(entity =>
                    {
                        entity.HasNoKey();
                    });

                    modelBuilder.Entity<Post>(entity =>
                    {
                        entity.HasOne(p => p.Blog)
                            .WithMany(b => b.Posts)
                            .HasForeignKey(p => p.BlogId);
                    });
                }
            }
            """;

        var result = DiagramModelBuilder.Build(classSource, configSource);

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.ForeignKeyTargetsKeylessPrincipal, diagnostic.Code);
        Assert.Equal(DiagnosticCategory.ModelValidity, diagnostic.Category);
        Assert.Equal("Post", diagnostic.EntityName);
    }

    [Fact]
    public void Build_ForeignKeyTargetsKeyedPrincipal_NoDiagnostic()
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
                            .HasForeignKey(p => p.BlogId);
                    });
                }
            }
            """;

        var result = DiagramModelBuilder.Build(classSource, configSource);

        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Build_OwnedEntityWithNoOwnKey_NoEntityHasNoKeyDiagnostic()
    {
        const string classSource = """
            public class Order
            {
                public int Id { get; set; }
                public Address ShippingAddress { get; set; }
            }

            public class Address
            {
                public string City { get; set; }
            }
            """;

        const string configSource = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Order>(entity =>
                    {
                        entity.OwnsOne(e => e.ShippingAddress);
                    });
                }
            }
            """;

        var result = DiagramModelBuilder.Build(classSource, configSource);

        Assert.Empty(result.Diagnostics);
    }
}
