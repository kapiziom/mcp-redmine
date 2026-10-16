using System.Reflection;

namespace McpRedmine;

public static class PackageInfo
{
    /// <summary>Must match <c>PackageId</c> in the csproj (a test enforces it).</summary>
    public const string PackageId = "Kapiziom.RedmineMcp";

    /// <summary>Version of the running build, without the source-control suffix ("0.1.0-beta+abc" -> "0.1.0-beta").</summary>
    public static string Version
    {
        get
        {
            var informational = typeof(PackageInfo).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? typeof(PackageInfo).Assembly.GetName().Version?.ToString()
                ?? "0.0.0";
            var plus = informational.IndexOf('+', StringComparison.Ordinal);
            return plus < 0 ? informational : informational[..plus];
        }
    }

    /// <summary>How a user starts a subcommand without having installed anything (what MCP clients do too).</summary>
    public static string RunHint(string subcommand) =>
        $"dnx {PackageId} --version {Version} --yes -- {subcommand}";
}
