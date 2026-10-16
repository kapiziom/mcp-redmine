namespace McpRedmine.Config;

public static class ConfigPaths
{
    public const string ConfigFileEnvVar = "REDMINE_MCP_CONFIG";

    public static string ConfigDirectory()
    {
        var overridePath = Environment.GetEnvironmentVariable(ConfigFileEnvVar);
        if (!string.IsNullOrWhiteSpace(overridePath))
            return Path.GetDirectoryName(Path.GetFullPath(overridePath))!;

        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RedmineMcp");

        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var root = string.IsNullOrWhiteSpace(xdg)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config")
            : xdg;
        return Path.Combine(root, "redmine-mcp");
    }

    public static string ConfigFile()
    {
        var overridePath = Environment.GetEnvironmentVariable(ConfigFileEnvVar);
        return string.IsNullOrWhiteSpace(overridePath)
            ? Path.Combine(ConfigDirectory(), "config.json")
            : Path.GetFullPath(overridePath);
    }

    /// <summary>Directory of the file-based secret fallback (permissions 600 on Unix).</summary>
    public static string KeysDirectory() => Path.Combine(ConfigDirectory(), "keys");
}
