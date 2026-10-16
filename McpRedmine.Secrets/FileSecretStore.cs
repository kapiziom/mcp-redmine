using System.Text.RegularExpressions;

namespace McpRedmine.Secrets;

/// <summary>
/// Last-resort fallback: one file per account, mode 600 on Unix. On Windows the file inherits the
/// user-profile ACL. Prefer the OS credential store; <c>doctor</c> warns when this one is in use.
/// </summary>
public sealed partial class FileSecretStore(string directory) : ISecretStore
{
    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex AccountPattern();

    public string Description => $"key file in {directory}";

    public bool StoresPlaintext => true;

    public ApiKey? Get(string account)
    {
        var path = PathFor(account);
        if (!File.Exists(path))
            return null;

        var text = File.ReadAllText(path).Trim();
        return text.Length == 0 ? null : new ApiKey(text);
    }

    public void Set(string account, ApiKey key)
    {
        var path = PathFor(account);
        Directory.CreateDirectory(directory);

        var options = new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        using var stream = new FileStream(path, options);
        using var writer = new StreamWriter(stream);
        writer.Write(key.Reveal());
    }

    public bool Remove(string account)
    {
        var path = PathFor(account);
        if (!File.Exists(path))
            return false;

        File.Delete(path);
        return true;
    }

    /// <summary>True when the file is readable by anyone but its owner (Unix only; always false on Windows).</summary>
    public bool HasLoosePermissions(string account)
    {
        var path = PathFor(account);
        if (OperatingSystem.IsWindows() || !File.Exists(path))
            return false;

        return (File.GetUnixFileMode(path) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                                              | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0;
    }

    private string PathFor(string account)
    {
        // The pattern is also the path-traversal guard: no separators, no dots.
        if (!AccountPattern().IsMatch(account))
            throw new ArgumentException("Invalid account name.", nameof(account));

        return Path.Combine(directory, account + ".key");
    }
}
