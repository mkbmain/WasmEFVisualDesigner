using System.Collections.Generic;
using System.Linq;
using EfSchemaVisualizer.Core.Inference;
using EfSchemaVisualizer.Core.Model;
using Xunit;

namespace EfSchemaVisualizer.Core.Tests.Inference;

public class InheritanceInferenceTests
{
    private static PropertyModel Property(string name, string clrType) =>
        new(name, clrType, IsNullable: false, MaxLength: null);

    [Fact]
    public void Fold_NoEntityHasBaseEntityName_ReturnsEntitiesUnchangedAndNoRelationships()
    {
        var person = new EntityModel("Person", new[] { Property("Id", "int") }, KeyPropertyNames: new[] { "Id" });

        var result = InheritanceInference.Fold(new[] { person });

        Assert.Same(person, Assert.Single(result.Entities));
        Assert.Empty(result.Relationships);
    }

    [Fact]
    public void Fold_DerivedEntity_InheritsBaseProperties()
    {
        var person = new EntityModel(
            "Person",
            new[] { Property("Id", "int"), Property("Name", "string") },
            KeyPropertyNames: new[] { "Id" });
        var student = new EntityModel(
            "Student",
            new[] { Property("Course", "string") },
            BaseEntityName: "Person");

        var result = InheritanceInference.Fold(new[] { person, student });

        var foldedStudent = result.Entities.Single(e => e.Name == "Student");
        Assert.Equal(new[] { "Id", "Name", "Course" }, foldedStudent.Properties.Select(p => p.Name));
        Assert.Equal("Person", foldedStudent.Properties.Single(p => p.Name == "Id").DeclaringEntityName);
        Assert.Equal("Person", foldedStudent.Properties.Single(p => p.Name == "Name").DeclaringEntityName);
        Assert.Null(foldedStudent.Properties.Single(p => p.Name == "Course").DeclaringEntityName);
    }

    [Fact]
    public void Fold_DerivedEntity_InheritsBaseKeyAndMarksItInferred()
    {
        var person = new EntityModel(
            "Person",
            new[] { Property("Id", "int") },
            KeyPropertyNames: new[] { "Id" });
        var student = new EntityModel("Student", new[] { Property("Course", "string") }, BaseEntityName: "Person");

        var result = InheritanceInference.Fold(new[] { person, student });

        var foldedStudent = result.Entities.Single(e => e.Name == "Student");
        Assert.Equal(new[] { "Id" }, foldedStudent.KeyPropertyNames);
        Assert.True(foldedStudent.IsKeyInferred);
    }

    [Fact]
    public void Fold_DerivedEntityWithItsOwnExplicitKey_KeepsItsOwnKey()
    {
        var person = new EntityModel("Person", new[] { Property("Id", "int") }, KeyPropertyNames: new[] { "Id" });
        var student = new EntityModel(
            "Student",
            new[] { Property("StudentNumber", "string") },
            KeyPropertyNames: new[] { "StudentNumber" },
            BaseEntityName: "Person");

        var result = InheritanceInference.Fold(new[] { person, student });

        var foldedStudent = result.Entities.Single(e => e.Name == "Student");
        Assert.Equal(new[] { "StudentNumber" }, foldedStudent.KeyPropertyNames);
        Assert.False(foldedStudent.IsKeyInferred);
    }

    [Fact]
    public void Fold_OwnPropertyWithSameNameAsAncestorProperty_OwnPropertyWins()
    {
        var person = new EntityModel("Person", new[] { Property("Name", "string") });
        var student = new EntityModel(
            "Student",
            new[] { Property("Name", "string?") },
            BaseEntityName: "Person");

        var result = InheritanceInference.Fold(new[] { person, student });

        var foldedStudent = result.Entities.Single(e => e.Name == "Student");
        var name = Assert.Single(foldedStudent.Properties, p => p.Name == "Name");
        Assert.Equal("string?", name.ClrType);
        Assert.Null(name.DeclaringEntityName);
    }

    [Fact]
    public void Fold_MultiLevelChain_FoldsAllAncestorsInRootFirstOrder()
    {
        var entity = new EntityModel("Entity", new[] { Property("Id", "int") }, KeyPropertyNames: new[] { "Id" });
        var person = new EntityModel("Person", new[] { Property("Name", "string") }, BaseEntityName: "Entity");
        var student = new EntityModel("Student", new[] { Property("Course", "string") }, BaseEntityName: "Person");

        var result = InheritanceInference.Fold(new[] { entity, person, student });

        var foldedStudent = result.Entities.Single(e => e.Name == "Student");
        Assert.Equal(new[] { "Id", "Name", "Course" }, foldedStudent.Properties.Select(p => p.Name));
        Assert.Equal("Entity", foldedStudent.Properties.Single(p => p.Name == "Id").DeclaringEntityName);
        Assert.Equal("Person", foldedStudent.Properties.Single(p => p.Name == "Name").DeclaringEntityName);
        Assert.Equal(new[] { "Id" }, foldedStudent.KeyPropertyNames);
    }

    [Fact]
    public void Fold_MultiLevelChain_NearerAncestorShadowsFurtherAncestorPropertyOfSameName()
    {
        var root = new EntityModel("Root", new[] { Property("Foo", "int") });
        var mid = new EntityModel("Mid", new[] { Property("Foo", "string") }, BaseEntityName: "Root");
        var leaf = new EntityModel("Leaf", System.Array.Empty<PropertyModel>(), BaseEntityName: "Mid");

        var result = InheritanceInference.Fold(new[] { root, mid, leaf });

        var foldedLeaf = result.Entities.Single(e => e.Name == "Leaf");
        var foo = Assert.Single(foldedLeaf.Properties, p => p.Name == "Foo");
        Assert.Equal("string", foo.ClrType);
        Assert.Equal("Mid", foo.DeclaringEntityName);
    }

    [Fact]
    public void Fold_MalformedCycle_DoesNotThrowAndStopsAtCycle()
    {
        var a = new EntityModel("A", new[] { Property("X", "int") }, BaseEntityName: "B");
        var b = new EntityModel("B", new[] { Property("Y", "int") }, BaseEntityName: "A");

        var result = InheritanceInference.Fold(new[] { a, b });

        Assert.Equal(2, result.Entities.Count);
    }

    [Fact]
    public void Fold_BaseEntityNameDoesNotResolve_TreatedAsRootEntity()
    {
        var student = new EntityModel("Student", new[] { Property("Course", "string") }, BaseEntityName: "Ignored");

        var result = InheritanceInference.Fold(new[] { student });

        var folded = Assert.Single(result.Entities);
        Assert.Equal(new[] { "Course" }, folded.Properties.Select(p => p.Name));
        Assert.Empty(result.Relationships);
    }

    [Fact]
    public void Fold_DerivedEntity_ProducesInheritanceRelationshipToDirectBase()
    {
        var person = new EntityModel("Person", new[] { Property("Id", "int") }, KeyPropertyNames: new[] { "Id" });
        var student = new EntityModel("Student", new[] { Property("Course", "string") }, BaseEntityName: "Person");

        var result = InheritanceInference.Fold(new[] { person, student });

        var relationship = Assert.Single(result.Relationships);
        Assert.Equal("Person", relationship.PrincipalEntity);
        Assert.Equal("Student", relationship.DependentEntity);
        Assert.Equal(RelationshipKind.Inheritance, relationship.Kind);
        Assert.False(relationship.IsInferred);
        Assert.Empty(relationship.ForeignKeyProperties);
    }

    [Fact]
    public void Fold_TwoSiblingsSharingOneBase_ProducesOneRelationshipPerSibling()
    {
        var person = new EntityModel("Person", new[] { Property("Id", "int") }, KeyPropertyNames: new[] { "Id" });
        var student = new EntityModel("Student", new[] { Property("Course", "string") }, BaseEntityName: "Person");
        var teacher = new EntityModel("Teacher", new[] { Property("Salary", "decimal") }, BaseEntityName: "Person");

        var result = InheritanceInference.Fold(new[] { person, student, teacher });

        Assert.Equal(2, result.Relationships.Count);
        Assert.Contains(result.Relationships, r => r.DependentEntity == "Student" && r.PrincipalEntity == "Person");
        Assert.Contains(result.Relationships, r => r.DependentEntity == "Teacher" && r.PrincipalEntity == "Person");
    }
}
