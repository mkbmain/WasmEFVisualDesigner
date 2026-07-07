using EfSchemaVisualizer.Core.Model;
using Xunit;

namespace EfSchemaVisualizer.Core.Tests.Model;

public class PropertyModelTests
{
    [Fact]
    public void WithMaxLength_ProducesUpdatedCopy_LeavingOriginalUnchanged()
    {
        var original = new PropertyModel("Name", "string", IsNullable: true, MaxLength: null);

        var updated = original with { MaxLength = 100 };

        Assert.Null(original.MaxLength);
        Assert.Equal(100, updated.MaxLength);
        Assert.Equal(original.Name, updated.Name);
    }

    [Fact]
    public void EntityModel_ExposesNameAndProperties()
    {
        var properties = new List<PropertyModel>
        {
            new("Id", "int", IsNullable: false, MaxLength: null),
            new("Name", "string", IsNullable: true, MaxLength: null),
        };

        var entity = new EntityModel("Person", properties);

        Assert.Equal("Person", entity.Name);
        Assert.Equal(2, entity.Properties.Count);
    }
}
