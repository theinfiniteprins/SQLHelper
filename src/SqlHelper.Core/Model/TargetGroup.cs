namespace SqlHelper.Core.Model;

/// <summary>A named, reusable selection of targets ("All Production", "EU clients", "Pilot").</summary>
public sealed class TargetGroup
{
    public required string Name { get; set; }

    public List<Guid> TargetIds { get; init; } = [];

    public string? Description { get; set; }
}
