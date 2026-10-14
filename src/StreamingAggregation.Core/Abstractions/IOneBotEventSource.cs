using StreamingAggregation.Core.Models;

namespace StreamingAggregation.Core.Abstractions;

public interface IOneBotEventSource
{
    IAsyncEnumerable<GroupMessageEvent> StreamAsync(CancellationToken cancellationToken);
    ConnectionState State { get; }
}
