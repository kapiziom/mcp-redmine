namespace McpRedmine.Secrets;

public static class SystemSecretStore
{
    /// <summary>The OS credential store for this platform, or <c>null</c> when none is usable (headless Linux, unknown OS).</summary>
    public static ISecretStore? TryCreate(IProcessRunner? runner = null)
    {
        runner ??= new ProcessRunner();

        if (OperatingSystem.IsWindows())
            return new WindowsCredentialStore();
        if (OperatingSystem.IsMacOS())
            return new MacKeychainStore(runner);
        if (OperatingSystem.IsLinux() && LibSecretStore.IsAvailable(runner))
            return new LibSecretStore(runner);

        return null;
    }
}
