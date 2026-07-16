using System.Linq;
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
}
