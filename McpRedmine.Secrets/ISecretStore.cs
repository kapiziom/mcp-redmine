namespace McpRedmine.Secrets;

/// <summary>Storage of API keys, addressed by account (the target name).</summary>
public interface ISecretStore
{
    /// <summary>Human-readable name shown by <c>doctor</c> / <c>accounts list</c>.</summary>
    string Description { get; }

    /// <summary>True for stores that keep the key as a plain file (doctor warns about these).</summary>
    bool StoresPlaintext { get; }

    ApiKey? Get(string account);

    void Set(string account, ApiKey key);

    bool Remove(string account);
}
