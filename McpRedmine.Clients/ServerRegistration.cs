namespace McpRedmine.Clients;

/// <summary>How an MCP client should launch this server. Never contains secrets (the key lives in the OS store).</summary>
public sealed record ServerRegistration(string Name, string Command, IReadOnlyList<string> Arguments)
{
    public const string DefaultName = "redmine";

    /// <summary>
    /// <c>dnx &lt;package&gt; --version X --yes</c>, pinned to one version. On Windows <c>dnx</c> is a <c>.cmd</c> shim,
    /// which clients that start servers without a shell cannot run directly, so it goes through <c>cmd /c</c>.
    /// </summary>
    public static ServerRegistration ForDnx(string packageId, string version, bool windows, string name = DefaultName)
    {
        string[] dnx = ["dnx", packageId, "--version", version, "--yes"];
        return windows
            ? new ServerRegistration(name, "cmd", ["/c", .. dnx])
            : new ServerRegistration(name, dnx[0], dnx[1..]);
    }

    public string DisplayCommand => string.Join(' ', new[] { Command }.Concat(Arguments));
}
