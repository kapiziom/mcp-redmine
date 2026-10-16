using System.Text.RegularExpressions;

namespace McpRedmine.Secrets;

/// <summary>
/// Linux Secret Service (GNOME Keyring, KWallet, ...) through <c>secret-tool</c> from libsecret. The secret is
/// written on stdin, never in the argument list. NOT VERIFIED on a real desktop yet.
/// </summary>
public sealed partial class LibSecretStore(IProcessRunner runner) : ISecretStore
{
    private const string Executable = "secret-tool";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60); // an unlock prompt may be waiting for the user

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex SafeAccount();

    public string Description => "Secret Service (libsecret)";

    public bool StoresPlaintext => false;

    /// <summary>True when <c>secret-tool</c> exists and answers; false on headless boxes without it.</summary>
    public static bool IsAvailable(IProcessRunner runner)
    {
        try
        {
            // Exit code 1 ("not found") still proves the tool and the bus are reachable; a missing binary throws.
            runner.Run(Executable, ["lookup", "service", "redmine-mcp", "account", "__probe__"], null, TimeSpan.FromSeconds(10));
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or TimeoutException or InvalidOperationException)
        {
            return false;
        }
    }

    public ApiKey? Get(string account)
    {
        var result = runner.Run(Executable, ["lookup", "service", "redmine-mcp", "account", CheckedAccount(account)], null, Timeout);
        if (result.ExitCode != 0)
            return null;

        var value = result.StandardOutput.Trim();
        return value.Length == 0 ? null : new ApiKey(value);
    }

    public void Set(string account, ApiKey key)
    {
        var result = runner.Run(
            Executable,
            ["store", "--label=RedmineMcp API key", "service", "redmine-mcp", "account", CheckedAccount(account)],
            key.Reveal(),
            Timeout);

        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Secret Service write failed (exit code {result.ExitCode}).");
    }

    public bool Remove(string account)
    {
        var existed = Get(account) is not null;
        runner.Run(Executable, ["clear", "service", "redmine-mcp", "account", CheckedAccount(account)], null, Timeout);
        return existed;
    }

    private static string CheckedAccount(string account) =>
        SafeAccount().IsMatch(account) ? account : throw new ArgumentException("Invalid account name.", nameof(account));
}
