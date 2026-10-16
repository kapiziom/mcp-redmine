using McpRedmine.Secrets;

namespace McpRedmine.Clients;

/// <summary>Finds and runs the <c>claude</c> CLI, including npm's <c>claude.cmd</c> shim on Windows.</summary>
public sealed class ClaudeCli(IProcessRunner runner, string? pathVariable = null, bool? windows = null)
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);
    private static readonly char[] BatchMetaCharacters = ['&', '|', '<', '>', '^', '%', '"', '\r', '\n'];

    private readonly string _path = pathVariable ?? Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
    private readonly bool _windows = windows ?? OperatingSystem.IsWindows();

    /// <summary>Full path of the first <c>claude</c> found on PATH (preferring a real executable over a shim), or null.</summary>
    public string? Locate()
    {
        string[] names = _windows ? ["claude.exe", "claude.cmd", "claude.bat"] : ["claude"];
        foreach (var directory in _path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var name in names)
            {
                string candidate;
                try
                {
                    candidate = Path.Combine(directory, name);
                }
                catch (ArgumentException)
                {
                    continue; // a malformed PATH entry
                }

                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return null;
    }

    /// <summary>Arguments are passed as a list, never as one concatenated string.</summary>
    public ProcessResult Run(IReadOnlyList<string> arguments, TimeSpan? timeout = null)
    {
        var executable = Locate() ?? throw new InvalidOperationException("The 'claude' CLI was not found on PATH.");

        // Batch shims go through cmd.exe, whose metacharacters .NET argument quoting does not neutralise.
        if (_windows && !executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            && arguments.Any(a => a.IndexOfAny(BatchMetaCharacters) >= 0))
            throw new ArgumentException("Argument contains characters that are unsafe for a batch shim.", nameof(arguments));

        return runner.Run(executable, arguments, null, timeout ?? DefaultTimeout);
    }
}
