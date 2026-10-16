using McpRedmine.Secrets;

namespace McpRedmine.Clients;

public static class ClientCatalog
{
    public static IReadOnlyList<IMcpClientInstaller> CreateDefault(IProcessRunner runner)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var desktopDirectory = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude")
            : OperatingSystem.IsMacOS()
                ? Path.Combine(home, "Library", "Application Support", "Claude")
                : Path.Combine(home, ".config", "Claude");

        var cursorDirectory = Path.Combine(home, ".cursor");

        return
        [
            new ClaudeCodeInstaller(new ClaudeCli(runner)),
            new JsonConfigInstaller("claude-desktop", "Claude Desktop", Path.Combine(desktopDirectory, "claude_desktop_config.json"), desktopDirectory),
            new JsonConfigInstaller("cursor", "Cursor", Path.Combine(cursorDirectory, "mcp.json"), cursorDirectory),
        ];
    }
}
