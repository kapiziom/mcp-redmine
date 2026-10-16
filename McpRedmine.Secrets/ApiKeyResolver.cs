namespace McpRedmine.Secrets;

public sealed record ResolvedApiKey(ApiKey Key, string Source, bool Plaintext);

/// <summary>Lookup order: OS credential store, then the environment variable named in config, then the key file.</summary>
public sealed class ApiKeyResolver(
    ISecretStore? systemStore,
    ISecretStore fileStore,
    Func<string, string?>? getEnvironmentVariable = null)
{
    private readonly Func<string, string?> _getEnv = getEnvironmentVariable ?? Environment.GetEnvironmentVariable;

    public ResolvedApiKey? Resolve(string account, string? apiKeyEnv)
    {
        if (TryGet(systemStore, account) is { } fromSystem)
            return new ResolvedApiKey(fromSystem, systemStore!.Description, systemStore.StoresPlaintext);

        if (!string.IsNullOrWhiteSpace(apiKeyEnv) && _getEnv(apiKeyEnv) is { } fromEnv && !string.IsNullOrWhiteSpace(fromEnv))
            return new ResolvedApiKey(new ApiKey(fromEnv), $"environment variable {apiKeyEnv}", Plaintext: false);

        if (TryGet(fileStore, account) is { } fromFile)
            return new ResolvedApiKey(fromFile, fileStore.Description, fileStore.StoresPlaintext);

        return null;
    }

    // An OS store that is locked, missing its helper or timing out must not hide the other sources.
    private static ApiKey? TryGet(ISecretStore? store, string account)
    {
        try
        {
            return store?.Get(account);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return null;
        }
    }
}
