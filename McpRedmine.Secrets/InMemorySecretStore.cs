namespace McpRedmine.Secrets;

/// <summary>Holds a key only for the lifetime of the process, e.g. to test a key before it is saved anywhere.</summary>
public sealed class InMemorySecretStore : ISecretStore
{
    private readonly Dictionary<string, ApiKey> _keys = new(StringComparer.OrdinalIgnoreCase);

    public string Description => "memory";

    public bool StoresPlaintext => false;

    public ApiKey? Get(string account) => _keys.GetValueOrDefault(account);

    public void Set(string account, ApiKey key) => _keys[account] = key;

    public bool Remove(string account) => _keys.Remove(account);
}
