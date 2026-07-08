using EfSchemaVisualizer.Core.CodeGen;
using EfSchemaVisualizer.Core.Model;
using Xunit;

namespace EfSchemaVisualizer.Core.Tests.CodeGen;

public class EntityClassRewriterTests
{
    private const string SourceWithExistingProperties = """
        public class Person
        {
            // unrelated comment that must survive
            public int Id { get; set; }
            public string Name { get; set; }
        }
        """;

    [Fact]
    public void AddProperty_ClassWithExistingProperties_AppendsAsLastMember()
    {
        var result = new EntityClassRewriter().AddProperty(
            SourceWithExistingProperties,
            className: "Person",
            property: new PropertyModel("Email", "string", IsNullable: false, MaxLength: null));

        Assert.Contains("public string Email { get; set; }", result);
        Assert.Contains("public int Id { get; set; }", result);
        Assert.Contains("public string Name { get; set; }", result);
        Assert.Contains("// unrelated comment that must survive", result);

        // Appended after Name, not before it or interleaved.
        var nameIndex = result.IndexOf("Name { get; set; }", StringComparison.Ordinal);
        var emailIndex = result.IndexOf("Email { get; set; }", StringComparison.Ordinal);
        Assert.True(emailIndex > nameIndex);
    }

    private const string SourceWithEmptyClassBody = """
        public class Person
        {
        }
        """;

    [Fact]
    public void AddProperty_ClassWithNoExistingProperties_InsertsSingleProperty()
    {
        var result = new EntityClassRewriter().AddProperty(
            SourceWithEmptyClassBody,
            className: "Person",
            property: new PropertyModel("Name", "string", IsNullable: false, MaxLength: null));

        Assert.Contains("public string Name { get; set; }", result);
    }

    [Fact]
    public void AddProperty_NullablePropertyModel_AppendsQuestionMarkSuffix()
    {
        var result = new EntityClassRewriter().AddProperty(
            SourceWithEmptyClassBody,
            className: "Person",
            property: new PropertyModel("MiddleName", "string", IsNullable: true, MaxLength: null));

        Assert.Contains("public string? MiddleName { get; set; }", result);
    }

    private const string SourceWithRecord = """
        public record Person
        {
            public int Id { get; set; }
        }
        """;

    [Fact]
    public void AddProperty_RecordWithBodyProperties_AppendsToMemberList()
    {
        var result = new EntityClassRewriter().AddProperty(
            SourceWithRecord,
            className: "Person",
            property: new PropertyModel("Name", "string", IsNullable: false, MaxLength: null));

        Assert.Contains("public string Name { get; set; }", result);
        Assert.Contains("public int Id { get; set; }", result);
    }
}
