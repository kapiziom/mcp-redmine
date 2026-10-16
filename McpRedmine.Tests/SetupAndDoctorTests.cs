using McpRedmine;
using McpRedmine.Clients;
using McpRedmine.Cli;
using McpRedmine.Config;
using McpRedmine.Redmine;
using McpRedmine.Secrets;

namespace McpRedmine.Tests;

public sealed class SetupCommandTests
{
    private sealed class FakeInstaller(string id, string? notDetected = null, PlanAction plan = PlanAction.Add, bool failOnApply = false) : IMcpClientInstaller
    {
        public int Applied { get; private set; }

        public string Id => id;

        public string DisplayName => "Fake " + id;

        public string? NotDetectedReason() => notDetected;

        public InstallPlan Plan(ServerRegistration registration) => new(plan, [$"do something for {registration.Name}"]);

        public void Apply(ServerRegistration registration)
        {
            if (failOnApply)
                throw new UserFacingException("could not write");
            Applied++;
        }
    }

    private static (CliContext Context, FakeTerminal Terminal) Make(params IMcpClientInstaller[] installers)
    {
        var terminal = new FakeTerminal();
        var context = new CliContext
        {
            Terminal = terminal,
            ConfigPath = Path.Combine(Path.GetTempPath(), "none.json"),
            FileStore = new FileSecretStore(Path.Combine(Path.GetTempPath(), "none-keys")),
            Tester = new TargetTester(),
            Installers = installers,
        };
        return (context, terminal);
    }

    [Fact]
    public void Dry_run_prints_the_plan_and_changes_nothing()
    {
        var installer = new FakeInstaller("a");
        var (context, terminal) = Make(installer);

        var exit = SetupCommand.Run(["--dry-run"], context);

        Assert.Equal(0, exit);
        Assert.Equal(0, installer.Applied);
        Assert.Contains("Plan", terminal.Transcript);
        Assert.Contains($"pinned to {PackageInfo.PackageId} {PackageInfo.Version}", terminal.Transcript);
        Assert.Contains("Dry run", terminal.Transcript);
        Assert.DoesNotContain("CONFIRM", terminal.Transcript);
    }

    [Fact]
    public void Yes_applies_without_asking()
    {
        var installer = new FakeInstaller("a");
        var (context, terminal) = Make(installer);

        var exit = SetupCommand.Run(["--yes"], context);

        Assert.Equal(0, exit);
        Assert.Equal(1, installer.Applied);
        Assert.DoesNotContain("CONFIRM", terminal.Transcript);
        Assert.Contains("accounts add", terminal.Transcript);
    }

    [Fact]
    public void Declining_the_confirmation_applies_nothing()
    {
        var installer = new FakeInstaller("a");
        var (context, terminal) = Make(installer);
        terminal.Confirmations.Enqueue(false);

        Assert.Equal(1, SetupCommand.Run([], context));
        Assert.Equal(0, installer.Applied);
    }

    [Fact]
    public void Already_current_clients_are_left_alone()
    {
        var installer = new FakeInstaller("a", plan: PlanAction.NoChange);
        var (context, terminal) = Make(installer);

        var exit = SetupCommand.Run([], context);

        Assert.Equal(0, exit);
        Assert.Equal(0, installer.Applied);
        Assert.Contains("up to date", terminal.Transcript);
        Assert.DoesNotContain("CONFIRM", terminal.Transcript);
    }

    [Fact]
    public void Client_filter_limits_what_is_touched_and_rejects_unknown_ids()
    {
        var a = new FakeInstaller("a");
        var b = new FakeInstaller("b");
        var (context, _) = Make(a, b);

        SetupCommand.Run(["--yes", "--client", "b"], context);
        Assert.Equal((0, 1), (a.Applied, b.Applied));

        var ex = Assert.Throws<UserFacingException>(() => SetupCommand.Run(["--client", "emacs"], context));
        Assert.Contains("a, b", ex.Message);
    }

    [Fact]
    public void Undetected_clients_are_skipped_and_when_none_remain_the_manual_command_is_shown()
    {
        var (context, terminal) = Make(new FakeInstaller("a", notDetected: "not installed"));

        var exit = SetupCommand.Run(["--yes"], context);

        Assert.Equal(1, exit);
        Assert.Contains("skipped (not installed)", terminal.Transcript);
        Assert.Contains("dnx", terminal.Transcript);
    }

    [Fact]
    public void Explicitly_requested_but_missing_client_is_a_failure_even_if_others_succeed()
    {
        var present = new FakeInstaller("a");
        var (context, _) = Make(present, new FakeInstaller("b", notDetected: "not installed"));

        Assert.Equal(1, SetupCommand.Run(["--yes", "--client", "a", "--client", "b"], context));
        Assert.Equal(1, present.Applied);
    }

    [Fact]
    public void One_failing_client_does_not_stop_the_others_but_fails_the_exit_code()
    {
        var good = new FakeInstaller("good");
        var (context, terminal) = Make(new FakeInstaller("bad", failOnApply: true), good);

        var exit = SetupCommand.Run(["--yes"], context);

        Assert.Equal(1, exit);
        Assert.Equal(1, good.Applied);
        Assert.Contains("FAILED - could not write", terminal.Transcript);
    }

    [Fact]
    public void The_version_can_be_overridden_for_the_pin()
    {
        var (context, terminal) = Make(new FakeInstaller("a"));

        SetupCommand.Run(["--dry-run", "--version", "9.9.9"], context);

        Assert.Contains("9.9.9", terminal.Transcript);
    }
}

public sealed class DoctorCommandTests : IDisposable
{
    private const string Key = "DOCTOR-SECRET-KEY-77";

    private sealed class FakeRunner(string dotnetSdks, bool dotnetMissing = false) : IProcessRunner
    {
        public ProcessResult Run(string fileName, IReadOnlyList<string> arguments, string? standardInput, TimeSpan timeout) =>
            dotnetMissing ? throw new System.ComponentModel.Win32Exception("not found") : new ProcessResult(0, dotnetSdks, string.Empty);
    }

    private sealed class FakeTester(Func<TargetConfig, RedmineUser> respond) : ITargetTester
    {
        public Task<RedmineUser> TestAsync(TargetConfig target, ApiKeyResolver keys, CancellationToken cancellationToken) =>
            Task.FromResult(respond(target));
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rmcp-doc-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTerminal _terminal = new();
    private readonly InMemorySecretStore _system = new();

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private string ConfigPath => Path.Combine(_dir, "config.json");

    private void WriteConfig(params TargetConfig[] targets) =>
        ConfigStore.Save(ConfigPath, new RedmineMcpConfig { Targets = targets });

    private Task<int> Run(ITargetTester? tester = null, IProcessRunner? processes = null, ISecretStore? system = null, bool useSystem = true) =>
        DoctorCommand.RunAsync(new CliContext
        {
            Terminal = _terminal,
            ConfigPath = ConfigPath,
            SystemStore = useSystem ? system ?? _system : null,
            FileStore = new FileSecretStore(Path.Combine(_dir, "keys")),
            Tester = tester ?? new FakeTester(_ => new RedmineUser(1, "bot", "Build", "Bot", null, null)),
            Processes = processes ?? new FakeRunner("10.0.401 [C:\\dotnet\\sdk]\n9.0.100 [C:\\dotnet\\sdk]"),
        });

    private static TargetConfig Target(string name = "main") => new() { Name = name, Url = "https://redmine.example.com" };

    [Fact]
    public async Task A_healthy_setup_passes_and_never_prints_the_key()
    {
        WriteConfig(Target());
        _system.Set("main", new ApiKey(Key));

        var exit = await Run();

        Assert.Equal(0, exit);
        Assert.Contains("[OK", _terminal.Transcript);
        Assert.Contains("key accepted: bot", _terminal.Transcript);
        Assert.DoesNotContain(Key, _terminal.Transcript);
        Assert.DoesNotContain("[FAIL", _terminal.Transcript);
    }

    [Fact]
    public async Task A_missing_key_fails_with_the_command_that_fixes_it()
    {
        WriteConfig(Target());

        var exit = await Run();

        Assert.Equal(1, exit);
        Assert.Contains("No API key", _terminal.Transcript);
        Assert.Contains("accounts add", _terminal.Transcript);
    }

    [Fact]
    public async Task A_rejected_key_points_at_the_rest_api_setting()
    {
        WriteConfig(Target());
        _system.Set("main", new ApiKey(Key));
        var tester = new FakeTester(_ => throw new RedmineException(RedmineErrorKind.Unauthorized, "Target 'main' rejected the credentials (HTTP 401)."));

        var exit = await Run(tester);

        Assert.Equal(1, exit);
        Assert.Contains("Enable REST web service", _terminal.Transcript);
        Assert.DoesNotContain(Key, _terminal.Transcript);
    }

    [Fact]
    public async Task A_key_in_a_plain_file_warns_but_does_not_fail()
    {
        WriteConfig(Target());
        new FileSecretStore(Path.Combine(_dir, "keys")).Set("main", new ApiKey(Key));

        var exit = await Run(useSystem: false);

        Assert.Equal(0, exit);
        Assert.Contains("[WARN] The key is kept in a plain file", _terminal.Transcript);
    }

    [Fact]
    public async Task No_config_is_a_warning_with_the_next_step_not_a_failure()
    {
        var exit = await Run();

        Assert.Equal(0, exit);
        Assert.Contains("No config yet", _terminal.Transcript);
    }

    [Fact]
    public async Task An_invalid_config_fails_and_lists_the_problems()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(ConfigPath, """{"targets":[{"name":"a","url":"http://plain.example.com"}]}""");

        var exit = await Run();

        Assert.Equal(1, exit);
        Assert.Contains("http://", _terminal.Transcript);
    }

    [Fact]
    public async Task A_broken_config_file_fails_cleanly()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(ConfigPath, "{ nope");

        Assert.Equal(1, await Run());
        Assert.Contains("[FAIL", _terminal.Transcript);
    }

    [Fact]
    public async Task Missing_dotnet_10_is_a_warning_because_dnx_needs_it()
    {
        WriteConfig(Target());
        _system.Set("main", new ApiKey(Key));

        var old = await Run(processes: new FakeRunner("8.0.100 [x]"));
        var none = await Run(processes: new FakeRunner(string.Empty, dotnetMissing: true));

        Assert.Equal(0, old);
        Assert.Equal(0, none);
        Assert.Contains(".NET 10 SDK", _terminal.Transcript);
        Assert.Contains("not on PATH", _terminal.Transcript);
    }

    [Fact]
    public async Task An_unreadable_ca_file_fails()
    {
        var caPath = Path.Combine(_dir, "bad.pem");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(caPath, "this is not a certificate");
        WriteConfig(Target() with { CaPath = caPath });
        _system.Set("main", new ApiKey(Key));

        var exit = await Run();

        Assert.Equal(1, exit);
        Assert.Contains("not a valid certificate", _terminal.Transcript);
    }

    [Fact]
    public async Task Every_target_is_checked_independently()
    {
        WriteConfig(Target("good"), Target("bad"));
        _system.Set("good", new ApiKey(Key));

        var exit = await Run();

        Assert.Equal(1, exit);
        Assert.Contains("Target 'good'", _terminal.Transcript);
        Assert.Contains("Target 'bad'", _terminal.Transcript);
        Assert.Contains("key accepted", _terminal.Transcript);
        Assert.Contains("No API key", _terminal.Transcript);
    }
}
