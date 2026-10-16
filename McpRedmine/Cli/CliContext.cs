using McpRedmine.Clients;
using McpRedmine.Config;
using McpRedmine.Secrets;

namespace McpRedmine.Cli;

/// <summary>Everything the subcommands touch outside of themselves; replaced piecemeal in tests.</summary>
public sealed class CliContext
{
    public required ITerminal Terminal { get; init; }

    public required string ConfigPath { get; init; }

    /// <summary>OS credential store, or <c>null</c> when this machine has none usable.</summary>
    public ISecretStore? SystemStore { get; init; }

    public required FileSecretStore FileStore { get; init; }

    public required ITargetTester Tester { get; init; }

    public IProcessRunner Processes { get; init; } = new ProcessRunner();

    /// <summary>MCP clients <c>setup</c> can register in.</summary>
    public IReadOnlyList<IMcpClientInstaller> Installers { get; init; } = [];

    public Func<string, string?> GetEnvironmentVariable { get; init; } = Environment.GetEnvironmentVariable;

    public ApiKeyResolver Keys => new(SystemStore, FileStore, GetEnvironmentVariable);

    public static CliContext CreateDefault()
    {
        var processes = new ProcessRunner();
        return new CliContext
        {
            Terminal = new ConsoleTerminal(),
            ConfigPath = ConfigPaths.ConfigFile(),
            SystemStore = SystemSecretStore.TryCreate(processes),
            FileStore = new FileSecretStore(ConfigPaths.KeysDirectory()),
            Tester = new TargetTester(),
            Processes = processes,
            Installers = ClientCatalog.CreateDefault(processes),
        };
    }

    public RedmineMcpConfig LoadConfig() => ConfigStore.Load(ConfigPath);
}
