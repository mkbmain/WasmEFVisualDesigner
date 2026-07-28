using EfSchemaVisualizer.Core.Model;
using Xunit;

namespace EfSchemaVisualizer.Core.Tests.Model;

public class OwnedTypeModelFieldsTests
{
    [Fact]
    public void PropertyModel_DefaultsLeaveFoldKindNone()
    {
        var property = new PropertyModel("Street", "string", IsNullable: false, MaxLength: null);

        Assert.Equal(FoldKind.None, property.FoldKind);
        Assert.Null(property.OwnerNavigationProperty);
    }

    [Fact]
    public void EntityModel_DefaultLeavesIsOwnedFalse()
    {
        var entity = new EntityModel("Address", new List<PropertyModel>());

        Assert.False(entity.IsOwned);
    }

    [Fact]
    public void RelationshipKind_HasOwnedMember()
    {
        Assert.True(Enum.IsDefined(typeof(RelationshipKind), RelationshipKind.Owned));
    }
}
