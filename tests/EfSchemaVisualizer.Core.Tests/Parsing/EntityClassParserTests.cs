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
        Assert.Equal("NoEntityDeclarations", diagnostic.Code);
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
}
