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
}
