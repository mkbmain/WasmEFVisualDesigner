using System.Linq;
using EfSchemaVisualizer.Core.Model;
using EfSchemaVisualizer.Core.Parsing;
using Xunit;

namespace EfSchemaVisualizer.Core.Tests.Parsing;

public class EntityClassParserTests
{
    [Fact]
    public void Parse_ReadsClassNameAndProperties_WithNullability()
    {
        const string source = """
            public class Person
            {
                public int Id { get; set; }
                public string? Name { get; set; }
                public string Email { get; set; }
            }
            """;

        var result = new EntityClassParser().Parse(source);

        Assert.Empty(result.Diagnostics);

        var entity = result.Value.Single();
        Assert.Equal("Person", entity.Name);
        Assert.Equal(3, entity.Properties.Count);

        var id = entity.Properties.Single(p => p.Name == "Id");
        Assert.Equal("int", id.ClrType);
        Assert.False(id.IsNullable);

        var name = entity.Properties.Single(p => p.Name == "Name");
        Assert.Equal("string", name.ClrType);
        Assert.True(name.IsNullable);

        var email = entity.Properties.Single(p => p.Name == "Email");
        Assert.Equal("string", email.ClrType);
        Assert.False(email.IsNullable);
    }

    [Fact]
    public void Parse_ClassLessFile_ReturnsEmptyListAndDiagnostic_NoException()
    {
        const string source = """
            public enum Status
            {
                Active,
                Inactive
            }
            """;

        var result = new EntityClassParser().Parse(source);

        Assert.Empty(result.Value);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.NoEntityDeclarations, diagnostic.Code);
    }

    [Fact]
    public void Parse_MultipleClassesInOneFile_ReturnsOneEntityPerClass()
    {
        const string source = """
            public class Person
            {
                public int Id { get; set; }
            }

            public class Address
            {
                public int Id { get; set; }
                public string Line1 { get; set; }
            }
            """;

        var result = new EntityClassParser().Parse(source);

        Assert.Empty(result.Diagnostics);
        Assert.Equal(2, result.Value.Count);
        Assert.Contains(result.Value, e => e.Name == "Person" && e.Properties.Count == 1);
        Assert.Contains(result.Value, e => e.Name == "Address" && e.Properties.Count == 2);
    }

    [Fact]
    public void Parse_PositionalRecord_ReadsParametersAsProperties()
    {
        const string source = """
            public record Product(int Id, string Name);
            """;

        var result = new EntityClassParser().Parse(source);

        var entity = result.Value.Single();
        Assert.Equal("Product", entity.Name);
        Assert.Equal(2, entity.Properties.Count);
        Assert.Equal("int", entity.Properties.Single(p => p.Name == "Id").ClrType);
        Assert.Equal("string", entity.Properties.Single(p => p.Name == "Name").ClrType);
    }

    [Fact]
    public void Parse_RecordWithPositionalAndBodyProperties_MergesBothInOrder()
    {
        const string source = """
            public record Product(int Id, string Name)
            {
                public decimal Price { get; set; }
            }
            """;

        var result = new EntityClassParser().Parse(source);

        var entity = result.Value.Single();
        Assert.Equal(new[] { "Id", "Name", "Price" }, entity.Properties.Select(p => p.Name));
    }

    [Fact]
    public void Parse_ClassWithPrimaryConstructor_DoesNotTreatParametersAsProperties()
    {
        const string source = """
            public class Person(int id, string name)
            {
                public int Id { get; set; } = id;
            }
            """;

        var result = new EntityClassParser().Parse(source);

        var entity = result.Value.Single();
        Assert.Equal("Person", entity.Name);
        // "name"/"Name" must NOT appear as a phantom property synthesized from the
        // primary constructor parameter, and "Id" must not be double-counted (once
        // from the parameter, once from the body property).
        Assert.DoesNotContain(entity.Properties, p => p.Name == "name" || p.Name == "Name");
        Assert.Single(entity.Properties, p => p.Name == "Id");
    }

    [Fact]
    public void Parse_StructEntity_IsRead()
    {
        const string source = """
            public struct Point
            {
                public int X { get; set; }
                public int Y { get; set; }
            }
            """;

        var result = new EntityClassParser().Parse(source);

        var entity = result.Value.Single();
        Assert.Equal("Point", entity.Name);
        Assert.Equal(2, entity.Properties.Count);
    }

    [Fact]
    public void Parse_InterfaceAlongsideClass_OnlyClassBecomesEntity_NoDiagnostic()
    {
        const string source = """
            public interface IAudited
            {
                DateTime CreatedAt { get; }
            }

            public class Person
            {
                public int Id { get; set; }
            }
            """;

        var result = new EntityClassParser().Parse(source);

        Assert.Empty(result.Diagnostics);
        var entity = Assert.Single(result.Value);
        Assert.Equal("Person", entity.Name);
    }

    [Fact]
    public void Parse_NotMappedProperty_IsExcluded()
    {
        const string source = """
            using System.ComponentModel.DataAnnotations.Schema;

            public class Person
            {
                public int Id { get; set; }

                [NotMapped]
                public string ScratchNote { get; set; }
            }
            """;

        var result = new EntityClassParser().Parse(source);

        var entity = result.Value.Single();
        Assert.Single(entity.Properties);
        Assert.Equal("Id", entity.Properties[0].Name);
    }

    [Fact]
    public void Parse_StaticProperty_IsExcluded()
    {
        const string source = """
            public class Person
            {
                public int Id { get; set; }
                public static int InstanceCount { get; set; }
            }
            """;

        var result = new EntityClassParser().Parse(source);

        var entity = result.Value.Single();
        Assert.Single(entity.Properties);
        Assert.Equal("Id", entity.Properties[0].Name);
    }

    [Fact]
    public void Parse_GetOnlyProperty_IsExcluded()
    {
        const string source = """
            public class Person
            {
                public int Id { get; set; }
                public string ReadOnlyLabel { get; }
            }
            """;

        var result = new EntityClassParser().Parse(source);

        var entity = result.Value.Single();
        Assert.Single(entity.Properties);
        Assert.Equal("Id", entity.Properties[0].Name);
    }

    [Fact]
    public void Parse_ExpressionBodiedProperty_IsExcluded()
    {
        const string source = """
            public class Person
            {
                public int Id { get; set; }
                public string First { get; set; }
                public string Last { get; set; }
                public string FullName => First + " " + Last;
            }
            """;

        var result = new EntityClassParser().Parse(source);

        var entity = result.Value.Single();
        Assert.Equal(new[] { "Id", "First", "Last" }, entity.Properties.Select(p => p.Name));
    }

    [Fact]
    public void Parse_InitOnlyProperty_IsIncluded()
    {
        const string source = """
            public class Person
            {
                public int Id { get; init; }
            }
            """;

        var result = new EntityClassParser().Parse(source);

        var entity = result.Value.Single();
        Assert.Single(entity.Properties);
        Assert.Equal("Id", entity.Properties[0].Name);
    }

    [Fact]
    public void Parse_NestedTypeDeclaration_IsNotTreatedAsATopLevelEntity()
    {
        const string source = """
            public class Order
            {
                public int Id { get; set; }

                private class LineItemComparer
                {
                    public int Threshold { get; set; }
                }
            }
            """;

        var result = new EntityClassParser().Parse(source);

        var entity = Assert.Single(result.Value);
        Assert.Equal("Order", entity.Name);
        Assert.Single(entity.Properties, p => p.Name == "Id");
    }

    [Fact]
    public void Parse_ClassInsideNamespace_IsStillReadAsAnEntity()
    {
        const string source = """
            namespace MyApp.Models;

            public class Person
            {
                public int Id { get; set; }
            }
            """;

        var result = new EntityClassParser().Parse(source);

        var entity = Assert.Single(result.Value);
        Assert.Equal("Person", entity.Name);
    }

    [Fact]
    public void Parse_DuplicateEntityNames_EmitsDiagnostic_AndKeepsFirst()
    {
        const string source = """
            public class Person
            {
                public int Id { get; set; }
            }

            public class Person
            {
                public int Id { get; set; }
                public string? Nickname { get; set; }
            }
            """;

        var result = new EntityClassParser().Parse(source);

        var entity = Assert.Single(result.Value);
        Assert.Equal("Person", entity.Name);
        Assert.Single(entity.Properties); // the first declaration's shape, not the second's

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.DuplicateEntityName, diagnostic.Code);
        Assert.Equal("Person", diagnostic.EntityName);
    }

    [Fact]
    public void Parse_NestedTypeDeclaration_EmitsDiagnostic_AndIsExcluded()
    {
        const string source = """
            public class Outer
            {
                public int Id { get; set; }

                public class Inner
                {
                    public int Id { get; set; }
                }
            }
            """;

        var result = new EntityClassParser().Parse(source);

        var entity = Assert.Single(result.Value);
        Assert.Equal("Outer", entity.Name);

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.NestedTypeDeclaration, diagnostic.Code);
        Assert.Equal("Inner", diagnostic.EntityName);
    }

    [Fact]
    public void Parse_RequiredAttribute_SetsIsRequiredOverride()
    {
        const string source = """
            public class Person
            {
                public int Id { get; set; }
                [Required]
                public string? Name { get; set; }
            }
            """;

        var result = new EntityClassParser().Parse(source);

        var name = result.Value.Single().Properties.Single(p => p.Name == "Name");
        Assert.True(name.IsRequiredOverride);
    }

    [Fact]
    public void Parse_TimestampAttribute_SetsIsRowVersion()
    {
        const string source = """
            public class Person
            {
                public int Id { get; set; }
                [Timestamp]
                public byte[] RowVersion { get; set; } = System.Array.Empty<byte>();
            }
            """;

        var result = new EntityClassParser().Parse(source);

        var property = result.Value.Single().Properties.Single(p => p.Name == "RowVersion");
        Assert.True(property.IsRowVersion);
        Assert.False(property.IsConcurrencyToken);
    }

    [Fact]
    public void Parse_ConcurrencyCheckAttribute_SetsIsConcurrencyToken()
    {
        const string source = """
            public class Person
            {
                public int Id { get; set; }
                [ConcurrencyCheck]
                public int Version { get; set; }
            }
            """;

        var result = new EntityClassParser().Parse(source);

        var property = result.Value.Single().Properties.Single(p => p.Name == "Version");
        Assert.False(property.IsRowVersion);
        Assert.True(property.IsConcurrencyToken);
    }

    [Fact]
    public void Parse_NoConcurrencyAttributes_LeavesBothFlagsFalse()
    {
        const string source = """
            public class Person
            {
                public int Id { get; set; }
                public string Name { get; set; } = "";
            }
            """;

        var result = new EntityClassParser().Parse(source);

        var property = result.Value.Single().Properties.Single(p => p.Name == "Name");
        Assert.False(property.IsRowVersion);
        Assert.False(property.IsConcurrencyToken);
    }

    [Fact]
    public void Parse_MaxLengthAttribute_SetsMaxLength()
    {
        const string source = """
            public class Person
            {
                public int Id { get; set; }
                [MaxLength(50)]
                public string Name { get; set; }
            }
            """;

        var result = new EntityClassParser().Parse(source);

        var name = result.Value.Single().Properties.Single(p => p.Name == "Name");
        Assert.Equal(50, name.MaxLength);
    }

    [Fact]
    public void Parse_StringLengthAttribute_SetsMaxLength()
    {
        const string source = """
            public class Person
            {
                public int Id { get; set; }
                [StringLength(80)]
                public string Name { get; set; }
            }
            """;

        var result = new EntityClassParser().Parse(source);

        var name = result.Value.Single().Properties.Single(p => p.Name == "Name");
        Assert.Equal(80, name.MaxLength);
    }

    [Fact]
    public void Parse_MaxLengthAndStringLengthBothPresent_MaxLengthWins()
    {
        const string source = """
            public class Person
            {
                public int Id { get; set; }
                [MaxLength(50)]
                [StringLength(80)]
                public string Name { get; set; }
            }
            """;

        var result = new EntityClassParser().Parse(source);

        var name = result.Value.Single().Properties.Single(p => p.Name == "Name");
        Assert.Equal(50, name.MaxLength);
    }

    [Fact]
    public void Parse_MaxLengthWithNonLiteralArgument_SkipsSilently_NoException()
    {
        const string source = """
            public class Person
            {
                public int Id { get; set; }
                [MaxLength(MaxNameLength)]
                public string Name { get; set; }
            }
            """;

        var result = new EntityClassParser().Parse(source);

        var name = result.Value.Single().Properties.Single(p => p.Name == "Name");
        Assert.Null(name.MaxLength);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Parse_ColumnAttribute_SetsColumnNameAndType()
    {
        const string source = """
            public class Person
            {
                public int Id { get; set; }
                [Column(Name = "full_name", TypeName = "varchar(80)")]
                public string Name { get; set; }
            }
            """;

        var result = new EntityClassParser().Parse(source);

        var name = result.Value.Single().Properties.Single(p => p.Name == "Name");
        Assert.Equal("full_name", name.ColumnName);
        Assert.Equal("varchar(80)", name.ColumnType);
    }

    [Fact]
    public void Parse_ColumnAttributePositionalName_SetsColumnName()
    {
        const string source = """
            public class Person
            {
                public int Id { get; set; }
                [Column("full_name")]
                public string Name { get; set; }
            }
            """;

        var result = new EntityClassParser().Parse(source);

        var name = result.Value.Single().Properties.Single(p => p.Name == "Name");
        Assert.Equal("full_name", name.ColumnName);
    }

    [Fact]
    public void Parse_PrecisionAttribute_SetsPrecisionAndScale()
    {
        const string source = """
            public class Invoice
            {
                public int Id { get; set; }
                [Precision(18, 2)]
                public decimal Total { get; set; }
            }
            """;

        var result = new EntityClassParser().Parse(source);

        var total = result.Value.Single().Properties.Single(p => p.Name == "Total");
        Assert.Equal(18, total.Precision);
        Assert.Equal(2, total.Scale);
    }

    [Fact]
    public void Parse_PrecisionAttributeNoScale_SetsPrecisionOnly()
    {
        const string source = """
            public class Invoice
            {
                public int Id { get; set; }
                [Precision(18)]
                public decimal Total { get; set; }
            }
            """;

        var result = new EntityClassParser().Parse(source);

        var total = result.Value.Single().Properties.Single(p => p.Name == "Total");
        Assert.Equal(18, total.Precision);
        Assert.Null(total.Scale);
    }

    [Fact]
    public void Parse_TableAttribute_SetsTableNameAndSchema()
    {
        const string source = """
            [Table("people", Schema = "dbo")]
            public class Person
            {
                public int Id { get; set; }
            }
            """;

        var result = new EntityClassParser().Parse(source);

        var entity = result.Value.Single();
        Assert.Equal("people", entity.TableName);
        Assert.Equal("dbo", entity.Schema);
    }

    [Fact]
    public void Parse_KeylessAttribute_SetsIsKeylessTrue()
    {
        const string source = """
            [Keyless]
            public class PersonView
            {
                public string Name { get; set; }
            }
            """;

        var result = new EntityClassParser().Parse(source);

        var entity = result.Value.Single();
        Assert.True(entity.IsKeyless);
    }

    [Fact]
    public void Parse_NoKeylessAttribute_IsKeylessFalse()
    {
        const string source = """
            public class Person
            {
                public int Id { get; set; }
            }
            """;

        var result = new EntityClassParser().Parse(source);

        var entity = result.Value.Single();
        Assert.False(entity.IsKeyless);
    }

    [Fact]
    public void Parse_KeyAttribute_SetsSinglePropertyKey()
    {
        const string source = """
            public class Person
            {
                [Key]
                public int PersonId { get; set; }
                public string? Name { get; set; }
            }
            """;

        var result = new EntityClassParser().Parse(source);

        var entity = result.Value.Single();
        Assert.Equal(new[] { "PersonId" }, entity.KeyPropertyNames);
    }

    [Fact]
    public void Parse_CompositeKeyAttributes_OrderedByColumnOrder()
    {
        const string source = """
            public class OrderLine
            {
                [Key]
                [Column(Order = 2)]
                public int ProductId { get; set; }

                [Key]
                [Column(Order = 1)]
                public int OrderId { get; set; }
            }
            """;

        var result = new EntityClassParser().Parse(source);

        var entity = result.Value.Single();
        Assert.Equal(new[] { "OrderId", "ProductId" }, entity.KeyPropertyNames);
    }

    [Fact]
    public void Parse_CompositeKeyAttributesNoOrder_UsesDeclarationOrder()
    {
        const string source = """
            public class OrderLine
            {
                [Key]
                public int OrderId { get; set; }

                [Key]
                public int ProductId { get; set; }
            }
            """;

        var result = new EntityClassParser().Parse(source);

        var entity = result.Value.Single();
        Assert.Equal(new[] { "OrderId", "ProductId" }, entity.KeyPropertyNames);
    }

    [Fact]
    public void Parse_NoKeyAttribute_KeyPropertyNamesEmpty()
    {
        const string source = """
            public class Person
            {
                public int Id { get; set; }
            }
            """;

        var result = new EntityClassParser().Parse(source);

        Assert.Empty(result.Value.Single().KeyPropertyNames);
    }

    [Fact]
    public void ParseRelationships_ForeignKeyOnNavigationProperty_ResolvesOneToMany()
    {
        const string source = """
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

        var parser = new EntityClassParser();
        var entityResult = parser.Parse(source);
        var relationshipResult = parser.ParseRelationships(source, entityResult.Value);

        var relationship = Assert.Single(relationshipResult.Value);
        Assert.Equal("Blog", relationship.PrincipalEntity);
        Assert.Equal("Post", relationship.DependentEntity);
        Assert.Equal(RelationshipKind.OneToMany, relationship.Kind);
        Assert.Null(relationship.PrincipalNavigation);
        Assert.Equal("Blog", relationship.DependentNavigation);
        Assert.Equal(new[] { "BlogId" }, relationship.ForeignKeyProperties);
    }

    [Fact]
    public void ParseRelationships_ForeignKeyOnScalarProperty_ResolvesSameAsNavigationPlacement()
    {
        const string source = """
            public class Blog
            {
                public int Id { get; set; }
            }

            public class Post
            {
                public int Id { get; set; }

                [ForeignKey("Blog")]
                public int BlogId { get; set; }

                public Blog Blog { get; set; }
            }
            """;

        var parser = new EntityClassParser();
        var entityResult = parser.Parse(source);
        var relationshipResult = parser.ParseRelationships(source, entityResult.Value);

        var relationship = Assert.Single(relationshipResult.Value);
        Assert.Equal("Blog", relationship.PrincipalEntity);
        Assert.Equal("Post", relationship.DependentEntity);
        Assert.Equal(new[] { "BlogId" }, relationship.ForeignKeyProperties);
    }

    [Fact]
    public void ParseRelationships_PrincipalHasCollectionBackReference_ResolvesOneToManyWithPrincipalNavigation()
    {
        const string source = """
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

        var parser = new EntityClassParser();
        var entityResult = parser.Parse(source);
        var relationshipResult = parser.ParseRelationships(source, entityResult.Value);

        var relationship = Assert.Single(relationshipResult.Value);
        Assert.Equal(RelationshipKind.OneToMany, relationship.Kind);
        Assert.Equal("Posts", relationship.PrincipalNavigation);
    }

    [Fact]
    public void ParseRelationships_PrincipalHasScalarBackReference_ResolvesOneToOne()
    {
        const string source = """
            public class Blog
            {
                public int Id { get; set; }
                public Post FeaturedPost { get; set; }
            }

            public class Post
            {
                public int Id { get; set; }
                public int BlogId { get; set; }

                [ForeignKey("BlogId")]
                public Blog Blog { get; set; }
            }
            """;

        var parser = new EntityClassParser();
        var entityResult = parser.Parse(source);
        var relationshipResult = parser.ParseRelationships(source, entityResult.Value);

        var relationship = Assert.Single(relationshipResult.Value);
        Assert.Equal(RelationshipKind.OneToOne, relationship.Kind);
        Assert.Equal("FeaturedPost", relationship.PrincipalNavigation);
    }

    [Fact]
    public void ParseRelationships_ForeignKeyNamesNonexistentProperty_SkipsSilently_NoException()
    {
        const string source = """
            public class Blog
            {
                public int Id { get; set; }
            }

            public class Post
            {
                public int Id { get; set; }

                [ForeignKey("DoesNotExist")]
                public Blog Blog { get; set; }
            }
            """;

        var parser = new EntityClassParser();
        var entityResult = parser.Parse(source);
        var relationshipResult = parser.ParseRelationships(source, entityResult.Value);

        Assert.Empty(relationshipResult.Value);
        Assert.Empty(relationshipResult.Diagnostics);
    }

    [Fact]
    public void ParseRelationships_NeitherSideIsKnownEntity_SkipsSilently()
    {
        const string source = """
            public class Post
            {
                public int Id { get; set; }
                public int PublisherId { get; set; }

                [ForeignKey("PublisherId")]
                public string Publisher { get; set; }
            }
            """;

        var parser = new EntityClassParser();
        var entityResult = parser.Parse(source);
        var relationshipResult = parser.ParseRelationships(source, entityResult.Value);

        Assert.Empty(relationshipResult.Value);
    }

    [Fact]
    public void ParseRelationships_BothSidesAnnotated_ProducesOneRelationshipNotTwo()
    {
        const string source = """
            public class Blog
            {
                public int Id { get; set; }
            }

            public class Post
            {
                public int Id { get; set; }

                [ForeignKey("Blog")]
                public int BlogId { get; set; }

                [ForeignKey("BlogId")]
                public Blog Blog { get; set; }
            }
            """;

        var parser = new EntityClassParser();
        var entityResult = parser.Parse(source);
        var relationshipResult = parser.ParseRelationships(source, entityResult.Value);

        Assert.Single(relationshipResult.Value);
    }

    // ─── ParseIndexAttributes ──────────────────────────────────────────────────────

    [Fact]
    public void ParseIndexAttributes_SinglePropertyViaNameof_ReadsPropertyName()
    {
        const string source = """
            [Index(nameof(Email))]
            public class Person
            {
                public int Id { get; set; }
                public string Email { get; set; }
            }
            """;

        var result = new EntityClassParser().ParseIndexAttributes(source);

        var config = Assert.Single(result.Value);
        Assert.Equal("Person", config.EntityName);
        Assert.Equal(new[] { "Email" }, config.PropertyNames);
        Assert.False(config.IsUnique);
        Assert.Null(config.Name);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void ParseIndexAttributes_BareStringLiteral_ReadsPropertyName()
    {
        const string source = """
            [Index("Email")]
            public class Person
            {
                public int Id { get; set; }
                public string Email { get; set; }
            }
            """;

        var result = new EntityClassParser().ParseIndexAttributes(source);

        var config = Assert.Single(result.Value);
        Assert.Equal(new[] { "Email" }, config.PropertyNames);
    }

    [Fact]
    public void ParseIndexAttributes_CompositeProperties_PreservesOrder()
    {
        const string source = """
            [Index(nameof(LastName), nameof(FirstName))]
            public class Person
            {
                public int Id { get; set; }
                public string FirstName { get; set; }
                public string LastName { get; set; }
            }
            """;

        var result = new EntityClassParser().ParseIndexAttributes(source);

        var config = Assert.Single(result.Value);
        Assert.Equal(new[] { "LastName", "FirstName" }, config.PropertyNames);
    }

    [Fact]
    public void ParseIndexAttributes_NamedArgsIsUniqueAndName_AreRead()
    {
        const string source = """
            [Index(nameof(Email), IsUnique = true, Name = "IX_Person_Email")]
            public class Person
            {
                public int Id { get; set; }
                public string Email { get; set; }
            }
            """;

        var result = new EntityClassParser().ParseIndexAttributes(source);

        var config = Assert.Single(result.Value);
        Assert.True(config.IsUnique);
        Assert.Equal("IX_Person_Email", config.Name);
    }

    [Fact]
    public void ParseIndexAttributes_MultipleAttributesOnSameClass_AllRead()
    {
        const string source = """
            [Index(nameof(Email))]
            [Index(nameof(LastName))]
            public class Person
            {
                public int Id { get; set; }
                public string Email { get; set; }
                public string LastName { get; set; }
            }
            """;

        var result = new EntityClassParser().ParseIndexAttributes(source);

        Assert.Equal(2, result.Value.Count);
        Assert.Contains(result.Value, c => c.PropertyNames.SequenceEqual(new[] { "Email" }));
        Assert.Contains(result.Value, c => c.PropertyNames.SequenceEqual(new[] { "LastName" }));
    }

    [Fact]
    public void ParseIndexAttributes_NoIndexAttribute_ReturnsEmpty()
    {
        const string source = """
            public class Person
            {
                public int Id { get; set; }
            }
            """;

        var result = new EntityClassParser().ParseIndexAttributes(source);

        Assert.Empty(result.Value);
        Assert.Empty(result.Diagnostics);
    }
}
