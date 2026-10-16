using System.Text.RegularExpressions;

namespace McpRedmine.Secrets;

/// <summary>
/// macOS Keychain through the <c>security</c> CLI. The secret is sent to <c>security -i</c> on stdin, so it
/// never appears in the argument list of a process. NOT VERIFIED on a real Mac yet.
/// </summary>
public sealed partial class MacKeychainStore(IProcessRunner runner) : ISecretStore
{
    private const string Service = "redmine-mcp";
    private const string Executable = "/usr/bin/security";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    // security -i parses its command line itself; restrict to printable ASCII without quote/backslash so a key can never break out.
    [GeneratedRegex("^[\\x21\\x23-\\x5B\\x5D-\\x7E]+$")]
    private static partial Regex SafeKey();

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex SafeAccount();

    public string Description => "macOS Keychain";

    public bool StoresPlaintext => false;

    public ApiKey? Get(string account)
    {
        var result = runner.Run(Executable, ["find-generic-password", "-a", CheckedAccount(account), "-s", Service, "-w"], null, Timeout);
        if (result.ExitCode != 0)
            return null; // 44 = item not found; anything else is treated the same way (caller falls back)

        var value = result.StandardOutput.Trim();
        return value.Length == 0 ? null : new ApiKey(value);
    }

    public void Set(string account, ApiKey key)
    {
        var secret = key.Reveal();
        if (!SafeKey().IsMatch(secret))
            throw new NotSupportedException("This key contains characters the Keychain helper cannot pass safely.");

        var command = $"add-generic-password -U -a \"{CheckedAccount(account)}\" -s \"{Service}\" -w \"{secret}\"\n";
        var result = runner.Run(Executable, ["-i"], command, Timeout);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Keychain write failed (exit code {result.ExitCode}).");
    }

    public bool Remove(string account)
    {
        var result = runner.Run(Executable, ["delete-generic-password", "-a", CheckedAccount(account), "-s", Service], null, Timeout);
        return result.ExitCode == 0;
    }

    private static string CheckedAccount(string account) =>
        SafeAccount().IsMatch(account) ? account : throw new ArgumentException("Invalid account name.", nameof(account));
}
