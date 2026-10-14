namespace StreamingAggregation.Core.Abstractions;

public enum ConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Reconnecting
}
