using System.Text.Json.Nodes;
using McpRedmine.Clients;
using McpRedmine.Config;
using McpRedmine.Secrets;

namespace McpRedmine.Tests;

public class ServerRegistrationTests
{
    [Fact]
    public void Non_windows_runs_dnx_directly() =>
        Assert.Equal("dnx Pkg.Id --version 1.2.3 --yes", ServerRegistration.ForDnx("Pkg.Id", "1.2.3", windows: false).DisplayCommand);

    [Fact]
    public void Windows_goes_through_cmd_because_dnx_is_a_cmd_shim()
    {
        var registration = ServerRegistration.ForDnx("Pkg.Id", "1.2.3", windows: true);

        Assert.Equal("cmd", registration.Command);
        Assert.Equal(["/c", "dnx", "Pkg.Id", "--version", "1.2.3", "--yes"], registration.Arguments);
    }

    [Fact]
    public void The_registration_pins_a_version_and_carries_no_secret()
    {
        var text = ServerRegistration.ForDnx("Pkg.Id", "1.2.3", windows: false).DisplayCommand;

        Assert.Contains("--version 1.2.3", text);
        Assert.DoesNotContain("key", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("env", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_package_id_in_code_matches_the_csproj()
    {
        var root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "McpRedmine.sln")))
            root = Path.GetDirectoryName(root);

        var csproj = File.ReadAllText(Path.Combine(root!, "McpRedmine", "McpRedmine.csproj"));

        Assert.Contains($"<PackageId>{McpRedmine.PackageInfo.PackageId}</PackageId>", csproj);
    }
}

public sealed class JsonConfigInstallerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rmcp-json-" + Guid.NewGuid().ToString("N"));
    private static readonly ServerRegistration Registration = ServerRegistration.ForDnx("Pkg.Id", "1.0.0", windows: false);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private string ConfigFile => Path.Combine(_dir, "client", "mcp.json");

    private JsonConfigInstaller Installer(DateTime? now = null) =>
        new("cursor", "Cursor", ConfigFile, Path.Combine(_dir, "client"), () => now ?? new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc));

    private void WriteClientConfig(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigFile)!);
        File.WriteAllText(ConfigFile, json);
    }

    private JsonObject Read() => (JsonObject)JsonNode.Parse(File.ReadAllText(ConfigFile))!;

    [Fact]
    public void Client_is_detected_by_its_directory()
    {
        Assert.NotNull(Installer().NotDetectedReason());
        Directory.CreateDirectory(Path.Combine(_dir, "client"));
        Assert.Null(Installer().NotDetectedReason());
    }

    [Fact]
    public void Missing_file_is_created_with_just_our_entry()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "client"));
        var installer = Installer();

        Assert.Equal(PlanAction.Add, installer.Plan(Registration).Action);
        installer.Apply(Registration);

        var entry = Read()["mcpServers"]!["redmine"]!;
        Assert.Equal("dnx", entry["command"]!.GetValue<string>());
        Assert.Equal(["Pkg.Id", "--version", "1.0.0", "--yes"], entry["args"]!.AsArray().Select(a => a!.GetValue<string>()));
        Assert.DoesNotContain("env", File.ReadAllText(ConfigFile));
    }

    [Fact]
    public void Existing_entries_and_unknown_settings_are_preserved_and_a_backup_is_made()
    {
        WriteClientConfig("""
            {
              "theme": "dark",
              "mcpServers": {
                "other": { "command": "npx", "args": ["-y", "something"], "env": { "TOKEN": "keep-me" } }
              }
            }
            """);
        var original = File.ReadAllText(ConfigFile);

        Installer().Apply(Registration);

        var root = Read();
        Assert.Equal("dark", root["theme"]!.GetValue<string>());
        Assert.Equal("keep-me", root["mcpServers"]!["other"]!["env"]!["TOKEN"]!.GetValue<string>());
        Assert.NotNull(root["mcpServers"]!["redmine"]);
        var backup = Directory.GetFiles(Path.GetDirectoryName(ConfigFile)!, "mcp.json.bak-*").Single();
        Assert.EndsWith("20250102030405", backup, StringComparison.Ordinal);
        Assert.Equal(original, File.ReadAllText(backup));
    }

    [Fact]
    public void Running_twice_changes_nothing_the_second_time()
    {
        WriteClientConfig("""{ "mcpServers": {} }""");
        var installer = Installer();
        installer.Apply(Registration);
        var afterFirst = File.ReadAllText(ConfigFile);
        var backupsAfterFirst = Directory.GetFiles(Path.GetDirectoryName(ConfigFile)!, "*.bak-*").Length;

        Assert.Equal(PlanAction.NoChange, installer.Plan(Registration).Action);
        installer.Apply(Registration);

        Assert.Equal(afterFirst, File.ReadAllText(ConfigFile));
        Assert.Equal(backupsAfterFirst, Directory.GetFiles(Path.GetDirectoryName(ConfigFile)!, "*.bak-*").Length);
    }

    [Fact]
    public void A_different_version_is_an_update_that_replaces_only_our_entry()
    {
        WriteClientConfig("""{ "mcpServers": { "redmine": { "command": "dnx", "args": ["Pkg.Id", "--version", "0.9.0", "--yes"] }, "other": { "command": "x" } } }""");
        var installer = Installer();

        Assert.Equal(PlanAction.Update, installer.Plan(Registration).Action);
        installer.Apply(Registration);

        var servers = Read()["mcpServers"]!;
        Assert.Equal("1.0.0", servers["redmine"]!["args"]![2]!.GetValue<string>());
        Assert.Equal("x", servers["other"]!["command"]!.GetValue<string>());
    }

    [Fact]
    public void An_unparseable_file_is_never_rewritten()
    {
        const string broken = "{ \"mcpServers\": { // a comment makes this invalid for strict JSON\n }";
        WriteClientConfig(broken);

        Assert.Throws<UserFacingException>(() => Installer().Plan(Registration));
        Assert.Throws<UserFacingException>(() => Installer().Apply(Registration));

        Assert.Equal(broken, File.ReadAllText(ConfigFile));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(ConfigFile)!, "*.bak-*"));
    }

    [Fact]
    public void A_mcp_servers_value_of_the_wrong_type_is_refused()
    {
        WriteClientConfig("""{ "mcpServers": [] }""");

        Assert.Throws<UserFacingException>(() => Installer().Apply(Registration));
    }

    [Fact]
    public void No_temp_files_are_left_behind()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "client"));

        Installer().Apply(Registration);

        Assert.DoesNotContain(Directory.GetFiles(Path.GetDirectoryName(ConfigFile)!), f => f.EndsWith(".tmp", StringComparison.Ordinal));
    }
}

public sealed class ClaudeCodeInstallerTests : IDisposable
{
    private static readonly ServerRegistration Registration = ServerRegistration.ForDnx("Pkg.Id", "1.0.0", windows: false);
    private readonly string _bin = Path.Combine(Path.GetTempPath(), "rmcp-bin-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_bin))
            Directory.Delete(_bin, recursive: true);
    }

    private sealed class FakeRunner(Func<IReadOnlyList<string>, int> exitCode) : IProcessRunner
    {
        public List<string[]> Calls { get; } = [];

        public ProcessResult Run(string fileName, IReadOnlyList<string> arguments, string? standardInput, TimeSpan timeout)
        {
            Calls.Add([.. arguments]);
            var code = exitCode(arguments);
            return new ProcessResult(code, string.Empty, code == 0 ? string.Empty : "boom: something failed");
        }
    }

    private ClaudeCodeInstaller Installer(FakeRunner runner)
    {
        Directory.CreateDirectory(_bin);
        File.WriteAllText(Path.Combine(_bin, "claude"), string.Empty);
        return new ClaudeCodeInstaller(new ClaudeCli(runner, _bin, windows: false));
    }

    [Fact]
    public void Detection_follows_the_path()
    {
        Assert.NotNull(new ClaudeCodeInstaller(new ClaudeCli(new FakeRunner(_ => 0), _bin, windows: false)).NotDetectedReason());
        Assert.Null(Installer(new FakeRunner(_ => 0)).NotDetectedReason());
    }

    [Fact]
    public void A_fresh_registration_is_one_add_with_arguments_as_a_list()
    {
        var runner = new FakeRunner(args => args[1] == "get" ? 1 : 0);

        Installer(runner).Apply(Registration);

        Assert.Equal(["mcp", "get", "redmine"], runner.Calls[0]);
        Assert.Equal(
            ["mcp", "add", "--transport", "stdio", "--scope", "user", "redmine", "--", "dnx", "Pkg.Id", "--version", "1.0.0", "--yes"],
            runner.Calls[1]);
        Assert.Equal(2, runner.Calls.Count);
    }

    [Fact]
    public void Re_running_removes_the_old_entry_then_adds_a_new_one()
    {
        var runner = new FakeRunner(_ => 0); // `get` succeeds => already registered

        Installer(runner).Apply(Registration);

        Assert.Equal(["mcp", "remove", "--scope", "user", "redmine"], runner.Calls[1]);
        Assert.Equal("add", runner.Calls[2][1]);
    }

    [Fact]
    public void Plan_describes_the_commands_without_running_changes()
    {
        var runner = new FakeRunner(args => args[1] == "get" ? 0 : 99);

        var plan = Installer(runner).Plan(Registration);

        Assert.Equal(PlanAction.Update, plan.Action);
        Assert.Contains(plan.Steps, s => s.Contains("claude mcp remove", StringComparison.Ordinal));
        Assert.All(runner.Calls, call => Assert.Equal("get", call[1]));
    }

    [Fact]
    public void A_failing_add_reports_the_cause()
    {
        var runner = new FakeRunner(args => args[1] == "get" ? 1 : args[1] == "add" ? 3 : 0);

        var ex = Assert.Throws<UserFacingException>(() => Installer(runner).Apply(Registration));

        Assert.Contains("boom", ex.Message);
    }

    [Fact]
    public void Windows_batch_shims_reject_shell_metacharacters_in_arguments()
    {
        Directory.CreateDirectory(_bin);
        File.WriteAllText(Path.Combine(_bin, "claude.cmd"), string.Empty);
        var claude = new ClaudeCli(new FakeRunner(_ => 0), _bin, windows: true);

        Assert.EndsWith("claude.cmd", claude.Locate(), StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => claude.Run(["mcp", "add", "x & calc"]));
    }

    [Fact]
    public void A_real_executable_is_preferred_over_a_shim_on_windows()
    {
        Directory.CreateDirectory(_bin);
        File.WriteAllText(Path.Combine(_bin, "claude.cmd"), string.Empty);
        File.WriteAllText(Path.Combine(_bin, "claude.exe"), string.Empty);

        Assert.EndsWith("claude.exe", new ClaudeCli(new FakeRunner(_ => 0), _bin, windows: true).Locate(), StringComparison.Ordinal);
    }
}
