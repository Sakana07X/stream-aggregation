namespace StreamingAggregation.Core.Configuration;

public sealed class AggregationSettings
{
    public int WindowMinutes { get; set; } = 3;
    public int RetentionDays { get; set; } = 30;
}
