namespace StreamingAggregation.Core.Configuration;

public sealed class OneBotConnectionSettings
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 3001;
    public string Token { get; set; } = string.Empty;
    public bool UseSsl { get; set; } = false;
}
