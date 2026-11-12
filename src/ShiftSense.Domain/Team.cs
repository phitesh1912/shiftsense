namespace ShiftSense.Domain;

/// <summary>A store/site team. Coverage is evaluated per team per shift slot.</summary>
public sealed class Team
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required Region Region { get; init; }

    /// <summary>Minimum staffed shifts required in any slot for the team to be "covered".</summary>
    public required int MinHeadcountPerSlot { get; init; }
}
