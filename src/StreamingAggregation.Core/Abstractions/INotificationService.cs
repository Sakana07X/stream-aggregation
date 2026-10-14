namespace StreamingAggregation.Core.Abstractions;

public interface INotificationService
{
    void NotifyUrgent(string title, string message);
    void NotifyDeadline(string title, string message);
}
