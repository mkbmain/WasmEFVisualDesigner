using System.Collections.Generic;

namespace EfSchemaVisualizer.Core.Model;

public sealed record RelationshipModel(
    string PrincipalEntity,
    string DependentEntity,
    RelationshipKind Kind,
    string? PrincipalNavigation,
    string? DependentNavigation,
    IReadOnlyList<string>? ForeignKeyProperties = null,
    string? OnDeleteBehavior = null,
    string? JoinEntityName = null,
    bool IsInferred = false,
    string? ConstraintName = null,
    IReadOnlyList<string>? PrincipalKeyProperties = null,
    bool JoinEntityIsSharedType = false,
    IReadOnlyList<string>? JoinEntityRightForeignKey = null,
    IReadOnlyList<string>? JoinEntityLeftForeignKey = null)
{
    public IReadOnlyList<string> ForeignKeyProperties { get; init; } = ForeignKeyProperties ?? new List<string>();
    public IReadOnlyList<string> PrincipalKeyProperties { get; init; } = PrincipalKeyProperties ?? new List<string>();
    public IReadOnlyList<string> JoinEntityRightForeignKey { get; init; } = JoinEntityRightForeignKey ?? new List<string>();
    public IReadOnlyList<string> JoinEntityLeftForeignKey { get; init; } = JoinEntityLeftForeignKey ?? new List<string>();
}
