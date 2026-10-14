namespace StreamingAggregation.Core.Configuration;

public sealed class AppSettings
{
    public OneBotConnectionSettings OneBotConnection { get; set; } = new();
    public ModelSettings Model { get; set; } = new();
    public AggregationSettings Aggregation { get; set; } = new();
    public GroupWhitelist GroupWhitelist { get; set; } = new();
}
