namespace StreamingAggregation.Core.Configuration;

public sealed class GroupWhitelist
{
    public IReadOnlyList<string> GroupIds { get; set; } = Array.Empty<string>();
}
