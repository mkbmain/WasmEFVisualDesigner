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

    private const string SourceWithoutMatchingClass = """
        public class Vehicle
        {
            public int Id { get; set; }
        }
        """;

    [Fact]
    public void AddProperty_ClassNameNotFound_Throws()
    {
        var rewriter = new EntityClassRewriter();

        Assert.Throws<InvalidOperationException>(() =>
            rewriter.AddProperty(SourceWithoutMatchingClass, className: "Person", property: new PropertyModel("Name", "string", IsNullable: false, MaxLength: null)));
    }

    private const string SourceWithMultipleTopLevelTypes = """
        public class Person
        {
            public int Id { get; set; }
        }

        public class Address
        {
            public string Line1 { get; set; }
        }
        """;

    [Fact]
    public void AddProperty_MultipleTopLevelTypes_OnlyModifiesTargetType()
    {
        var result = new EntityClassRewriter().AddProperty(
            SourceWithMultipleTopLevelTypes,
            className: "Person",
            property: new PropertyModel("Name", "string", IsNullable: false, MaxLength: null));

        Assert.Contains("public string Name { get; set; }", result);
        Assert.Contains("public string Line1 { get; set; }", result);

        // Name was added to Person, not Address.
        var addressBlockStart = result.IndexOf("class Address", StringComparison.Ordinal);
        var nameIndex = result.IndexOf("Name { get; set; }", StringComparison.Ordinal);
        Assert.True(nameIndex < addressBlockStart);
    }

    private const string SourceWithNestedTypeSameName = """
        public class Person
        {
            public int Id { get; set; }

            public class Address
            {
                public string Line1 { get; set; }
            }
        }
        """;

    [Fact]
    public void AddProperty_NameMatchesOnlyNestedType_ThrowsBecauseNestedTypesAreNotEligibleTargets()
    {
        var rewriter = new EntityClassRewriter();

        Assert.Throws<InvalidOperationException>(() =>
            rewriter.AddProperty(SourceWithNestedTypeSameName, className: "Address", property: new PropertyModel("Line2", "string", IsNullable: false, MaxLength: null)));
    }

    private const string SourceWithThreeProperties = """
        public class Person
        {
            // unrelated comment that must survive
            public int Id { get; set; }
            public string Name { get; set; }
            public string Email { get; set; }
        }
        """;

    [Fact]
    public void RemoveProperty_ExistingProperty_RemovesItAndLeavesSiblingsUntouched()
    {
        var result = new EntityClassRewriter().RemoveProperty(
            SourceWithThreeProperties, className: "Person", propertyName: "Email");

        Assert.DoesNotContain("Email", result);
        Assert.Contains("public int Id { get; set; }", result);
        Assert.Contains("public string Name { get; set; }", result);
        Assert.Contains("// unrelated comment that must survive", result);
    }

    private const string RecordWithTwoProperties = """
        public record Person
        {
            public int Id { get; set; }
            public string Name { get; set; }
        }
        """;

    [Fact]
    public void RemoveProperty_RecordBodyProperty_RemovesFromMemberList()
    {
        var result = new EntityClassRewriter().RemoveProperty(
            RecordWithTwoProperties, className: "Person", propertyName: "Name");

        Assert.DoesNotContain("Name", result);
        Assert.Contains("public int Id { get; set; }", result);
    }

    [Fact]
    public void RemoveProperty_PropertyNotFoundOnExistingClass_Throws()
    {
        var rewriter = new EntityClassRewriter();

        Assert.Throws<InvalidOperationException>(() =>
            rewriter.RemoveProperty(SourceWithThreeProperties, className: "Person", propertyName: "DoesNotExist"));
    }

    [Fact]
    public void RemoveProperty_ClassNotFound_Throws()
    {
        var rewriter = new EntityClassRewriter();

        Assert.Throws<InvalidOperationException>(() =>
            rewriter.RemoveProperty(SourceWithThreeProperties, className: "Vehicle", propertyName: "Name"));
    }

    private const string SourceWithMultipleTopLevelTypesForRemoval = """
        public class Person
        {
            public int Id { get; set; }
            public string Name { get; set; }
        }

        public class Address
        {
            public string Line1 { get; set; }
        }
        """;

    [Fact]
    public void RemoveProperty_MultipleTopLevelTypes_OnlyModifiesTargetType()
    {
        var result = new EntityClassRewriter().RemoveProperty(
            SourceWithMultipleTopLevelTypesForRemoval, className: "Person", propertyName: "Name");

        Assert.DoesNotContain("Name", result);
        Assert.Contains("public int Id { get; set; }", result);
        Assert.Contains("public string Line1 { get; set; }", result);
    }

    private const string ClassWithConstructor = """
        public class Person
        {
            public Person()
            {
            }

            public int Id { get; set; }
        }
        """;

    [Fact]
    public void RenameClass_ClassWithExplicitConstructor_RenamesConstructorToo()
    {
        var result = new EntityClassRewriter().RenameClass(
            ClassWithConstructor, oldClassName: "Person", newClassName: "Customer");

        Assert.Contains("class Customer", result);
        Assert.Contains("public Customer()", result);
        Assert.DoesNotContain("Person", result);
    }

    private const string RecordForRename = """
        public record Person
        {
            public int Id { get; set; }
        }
        """;

    [Fact]
    public void RenameClass_Record_RenamesIdentifier()
    {
        var result = new EntityClassRewriter().RenameClass(
            RecordForRename, oldClassName: "Person", newClassName: "Customer");

        Assert.Contains("record Customer", result);
        Assert.DoesNotContain("Person", result);
    }

    private const string StructForRename = """
        public struct Point
        {
            public int X { get; set; }
        }
        """;

    [Fact]
    public void RenameClass_Struct_RenamesIdentifier()
    {
        var result = new EntityClassRewriter().RenameClass(
            StructForRename, oldClassName: "Point", newClassName: "Coordinate");

        Assert.Contains("struct Coordinate", result);
        Assert.DoesNotContain("Point", result);
    }

    [Fact]
    public void RenameClass_ClassNotFound_Throws()
    {
        var rewriter = new EntityClassRewriter();

        Assert.Throws<InvalidOperationException>(() =>
            rewriter.RenameClass(SourceWithoutMatchingClass, oldClassName: "Person", newClassName: "Customer"));
    }

    [Fact]
    public void RenameClass_MultipleTopLevelTypes_OnlyModifiesTargetType()
    {
        var result = new EntityClassRewriter().RenameClass(
            SourceWithMultipleTopLevelTypes, oldClassName: "Person", newClassName: "Customer");

        Assert.Contains("class Customer", result);
        Assert.Contains("class Address", result);
        Assert.DoesNotContain("class Person", result);
    }

    [Fact]
    public void RenameProperty_ExistingProperty_RenamesItAndLeavesSiblingsUntouched()
    {
        var result = new EntityClassRewriter().RenameProperty(
            SourceWithThreeProperties, className: "Person", oldPropertyName: "Email", newPropertyName: "EmailAddress");

        Assert.Contains("public string EmailAddress { get; set; }", result);
        Assert.DoesNotContain("public string Email {", result);
        Assert.Contains("public int Id { get; set; }", result);
        Assert.Contains("public string Name { get; set; }", result);
        Assert.Contains("// unrelated comment that must survive", result);
    }

    [Fact]
    public void RenameProperty_RecordBodyProperty_RenamesInMemberList()
    {
        var result = new EntityClassRewriter().RenameProperty(
            RecordWithTwoProperties, className: "Person", oldPropertyName: "Name", newPropertyName: "FullName");

        Assert.Contains("public string FullName { get; set; }", result);
        Assert.Contains("public int Id { get; set; }", result);
    }

    [Fact]
    public void RenameProperty_PropertyNotFoundOnExistingClass_Throws()
    {
        var rewriter = new EntityClassRewriter();

        Assert.Throws<InvalidOperationException>(() =>
            rewriter.RenameProperty(SourceWithThreeProperties, className: "Person", oldPropertyName: "DoesNotExist", newPropertyName: "Whatever"));
    }

    [Fact]
    public void RenameProperty_ClassNotFound_Throws()
    {
        var rewriter = new EntityClassRewriter();

        Assert.Throws<InvalidOperationException>(() =>
            rewriter.RenameProperty(SourceWithThreeProperties, className: "Vehicle", oldPropertyName: "Name", newPropertyName: "Whatever"));
    }

    [Fact]
    public void RenameProperty_MultipleTopLevelTypes_OnlyModifiesTargetType()
    {
        var result = new EntityClassRewriter().RenameProperty(
            SourceWithMultipleTopLevelTypesForRemoval, className: "Person", oldPropertyName: "Name", newPropertyName: "FullName");

        Assert.Contains("public string FullName { get; set; }", result);
        Assert.Contains("public string Line1 { get; set; }", result);
        Assert.DoesNotContain("public string Name {", result);
    }

    [Fact]
    public void AddClass_FileWithExistingClasses_AppendsNewClassAsLastMember()
    {
        var result = new EntityClassRewriter().AddClass(SourceWithMultipleTopLevelTypes, className: "Order");

        Assert.Contains("public class Order", result);
        Assert.Contains("class Person", result);
        Assert.Contains("class Address", result);

        var addressIndex = result.IndexOf("class Address", StringComparison.Ordinal);
        var orderIndex = result.IndexOf("class Order", StringComparison.Ordinal);
        Assert.True(orderIndex > addressIndex);
    }

    private const string EmptyFile = "";

    [Fact]
    public void AddClass_FileWithNoTopLevelTypes_NewClassBecomesOnlyMember()
    {
        var result = new EntityClassRewriter().AddClass(EmptyFile, className: "Person");

        Assert.Contains("public class Person", result);
    }

    [Fact]
    public void RemoveClass_MultipleTopLevelTypes_OnlyRemovesTargetType()
    {
        var result = new EntityClassRewriter().RemoveClass(SourceWithMultipleTopLevelTypes, className: "Address");

        Assert.DoesNotContain("class Address", result);
        Assert.Contains("class Person", result);
    }

    [Fact]
    public void RemoveClass_ClassNotFound_Throws()
    {
        var rewriter = new EntityClassRewriter();

        Assert.Throws<InvalidOperationException>(() =>
            rewriter.RemoveClass(SourceWithoutMatchingClass, className: "Person"));
    }

    [Fact]
    public void RemoveClass_OnlyClassInFile_LeavesEmptyCompilationUnit()
    {
        var result = new EntityClassRewriter().RemoveClass(SourceWithEmptyClassBody, className: "Person");

        Assert.DoesNotContain("class Person", result);
        Assert.Equal(string.Empty, result.Trim());
    }

    private const string SourceForTypeChange = """
        public class Person
        {
            // unrelated comment that must survive
            public int Id { get; set; }
            public string Name { get; set; }
        }
        """;

    [Fact]
    public void ChangePropertyType_NonNullableToNonNullable_ChangesTypeOnly()
    {
        var result = new EntityClassRewriter().ChangePropertyType(
            SourceForTypeChange, className: "Person", propertyName: "Id",
            newClrType: "long", newIsNullable: false);

        Assert.Contains("public long Id { get; set; }", result);
        Assert.Contains("public string Name { get; set; }", result);
        Assert.Contains("// unrelated comment that must survive", result);
    }

    [Fact]
    public void ChangePropertyType_NonNullableToNullable_AddsQuestionMarkSuffix()
    {
        var result = new EntityClassRewriter().ChangePropertyType(
            SourceForTypeChange, className: "Person", propertyName: "Name",
            newClrType: "string", newIsNullable: true);

        Assert.Contains("public string? Name { get; set; }", result);
    }

    [Fact]
    public void ChangePropertyType_NullableToNonNullable_RemovesQuestionMarkSuffix()
    {
        const string sourceWithNullableProperty = """
            public class Person
            {
                public string? MiddleName { get; set; }
            }
            """;

        var result = new EntityClassRewriter().ChangePropertyType(
            sourceWithNullableProperty, className: "Person", propertyName: "MiddleName",
            newClrType: "string", newIsNullable: false);

        Assert.Contains("public string MiddleName { get; set; }", result);
        Assert.DoesNotContain("string?", result);
    }

    [Fact]
    public void ChangePropertyType_PropertyNotFound_Throws()
    {
        var rewriter = new EntityClassRewriter();

        Assert.Throws<InvalidOperationException>(() =>
            rewriter.ChangePropertyType(
                SourceForTypeChange, className: "Person", propertyName: "DoesNotExist",
                newClrType: "int", newIsNullable: false));
    }

    [Fact]
    public void ChangePropertyType_MultipleProperties_OnlyTargetChanges()
    {
        var result = new EntityClassRewriter().ChangePropertyType(
            SourceForTypeChange, className: "Person", propertyName: "Id",
            newClrType: "Guid", newIsNullable: false);

        Assert.Contains("public Guid Id { get; set; }", result);
        Assert.Contains("public string Name { get; set; }", result);
    }

    [Fact]
    public void ChangePropertyType_RecordBodyProperty_ChangesType()
    {
        const string recordSource = """
            public record Person
            {
                public int Id { get; set; }
            }
            """;

        var result = new EntityClassRewriter().ChangePropertyType(
            recordSource, className: "Person", propertyName: "Id",
            newClrType: "long", newIsNullable: false);

        Assert.Contains("public long Id { get; set; }", result);
    }
}
