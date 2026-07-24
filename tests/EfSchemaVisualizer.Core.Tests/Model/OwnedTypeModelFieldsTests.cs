using EfSchemaVisualizer.Core.Model;
using Xunit;

namespace EfSchemaVisualizer.Core.Tests.Model;

public class OwnedTypeModelFieldsTests
{
    [Fact]
    public void PropertyModel_DefaultsLeaveOwnedFieldsUnset()
    {
        var property = new PropertyModel("Street", "string", IsNullable: false, MaxLength: null);

        Assert.False(property.IsOwned);
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
