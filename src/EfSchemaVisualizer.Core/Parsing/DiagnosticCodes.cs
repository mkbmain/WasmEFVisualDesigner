namespace EfSchemaVisualizer.Core.Parsing;

public static class DiagnosticCodes
{
    public const string DuplicateEntityName = nameof(DuplicateEntityName);
    public const string NestedTypeDeclaration = nameof(NestedTypeDeclaration);
    public const string NoEntityDeclarations = nameof(NoEntityDeclarations);
    public const string UnresolvablePropertyName = nameof(UnresolvablePropertyName);
    public const string UnreadableMaxLengthArgument = nameof(UnreadableMaxLengthArgument);
    public const string UnreadableHasPrecisionArgument = nameof(UnreadableHasPrecisionArgument);
    public const string UnreadableIsRequiredArgument = nameof(UnreadableIsRequiredArgument);
    public const string UnreadableHasKeyArgument = nameof(UnreadableHasKeyArgument);
    public const string UnreadableToTableArgument = nameof(UnreadableToTableArgument);
    public const string UnreadableHasColumnNameArgument = nameof(UnreadableHasColumnNameArgument);
    public const string UnreadableHasColumnTypeArgument = nameof(UnreadableHasColumnTypeArgument);
    public const string UnreadableHasDefaultValueArgument = nameof(UnreadableHasDefaultValueArgument);
    public const string UnreadableHasIndexArgument = nameof(UnreadableHasIndexArgument);
    public const string UnreadableIsUniqueArgument = nameof(UnreadableIsUniqueArgument);
    public const string UnresolvableRelationshipTarget = nameof(UnresolvableRelationshipTarget);
    public const string UnreadableHasForeignKeyArgument = nameof(UnreadableHasForeignKeyArgument);
    public const string UnreadableOnDeleteArgument = nameof(UnreadableOnDeleteArgument);
    public const string UnrecognizedConfigCall = nameof(UnrecognizedConfigCall);
    public const string ArchiveNoContentFound = nameof(ArchiveNoContentFound);
}
