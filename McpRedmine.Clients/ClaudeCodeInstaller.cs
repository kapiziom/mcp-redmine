using McpRedmine.Config;

namespace McpRedmine.Clients;

/// <summary>
/// Registers the server in Claude Code through its own CLI (<c>claude mcp add</c>), so Claude Code owns its config.
/// Re-running removes the user-scope entry and adds it again, which also moves the pinned version forward.
/// </summary>
public sealed class ClaudeCodeInstaller(ClaudeCli claude) : IMcpClientInstaller
{
    public string Id => "claude-code";

    public string DisplayName => "Claude Code";

    public string? NotDetectedReason() => claude.Locate() is null ? "'claude' not found on PATH" : null;

    public InstallPlan Plan(ServerRegistration registration)
    {
        var exists = Exists(registration.Name);
        var add = $"claude {string.Join(' ', AddArguments(registration))}";

        return exists
            ? new InstallPlan(PlanAction.Update, [$"claude mcp remove --scope user {registration.Name}", add])
            : new InstallPlan(PlanAction.Add, [add]);
    }

    public void Apply(ServerRegistration registration)
    {
        if (Exists(registration.Name))
        {
            var removed = claude.Run(["mcp", "remove", "--scope", "user", registration.Name]);
            if (removed.ExitCode != 0)
                throw new UserFacingException(
                    $"'claude mcp remove' failed (exit code {removed.ExitCode}): {Short(removed.StandardError)}. "
                    + "The server may be registered in another scope; remove it with 'claude mcp remove' and run setup again.");
        }

        var added = claude.Run(AddArguments(registration));
        if (added.ExitCode != 0)
            throw new UserFacingException($"'claude mcp add' failed (exit code {added.ExitCode}): {Short(added.StandardError)}");
    }

    internal static IReadOnlyList<string> AddArguments(ServerRegistration registration) =>
        ["mcp", "add", "--transport", "stdio", "--scope", "user", registration.Name, "--", registration.Command, .. registration.Arguments];

    // `claude mcp get` also health-checks (starts) the server, so give it room; any failure means "not registered".
    private bool Exists(string name) => claude.Run(["mcp", "get", name], TimeSpan.FromSeconds(120)).ExitCode == 0;

    private static string Short(string text)
    {
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "no details";
        return line.Length > 200 ? line[..200] : line;
    }
}
