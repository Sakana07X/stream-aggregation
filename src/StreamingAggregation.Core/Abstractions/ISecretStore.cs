namespace StreamingAggregation.Core.Abstractions;

public interface ISecretStore
{
    string? GetSecret(string key);
    void SetSecret(string key, string value);
    void RemoveSecret(string key);
}
