namespace EfSchemaVisualizer.Web.Diagram;

public sealed class DiagramEditContext
{
    public required DiagramEditor Editor { get; init; }
    public required Func<Task> NotifyChangedAsync { get; init; }
}
