namespace EfSchemaVisualizer.Core.Merging;

public sealed record OwnedTypeConfig(string OwnerEntityName, string NavigationPropertyName, bool IsMany);
