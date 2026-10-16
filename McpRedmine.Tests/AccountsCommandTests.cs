using McpRedmine.Cli;
using McpRedmine.Config;
using McpRedmine.Redmine;
using McpRedmine.Secrets;

namespace McpRedmine.Tests;

public sealed class FakeTerminal : ITerminal
{
    public Queue<string> Lines { get; } = new();

    public Queue<string> Secrets { get; } = new();

    public Queue<bool> Confirmations { get; } = new();

    public List<string> Output { get; } = [];

    public bool IsInteractive { get; init; } = true;

    public string Transcript => string.Join('\n', Output);

    public void WriteLine(string text = "") => Output.Add(text);

    public void WriteError(string text) => Output.Add("ERR: " + text);

    public string ReadLine(string prompt)
    {
        Output.Add("PROMPT: " + prompt);
        return Lines.Count > 0 ? Lines.Dequeue() : string.Empty;
    }

    public string ReadSecret(string prompt)
    {
        if (!IsInteractive)
            throw new UserFacingException("This needs an interactive terminal.");
        Output.Add("PROMPT: " + prompt);
        return Secrets.Count > 0 ? Secrets.Dequeue() : string.Empty;
    }

    public bool Confirm(string question, bool defaultYes)
    {
        Output.Add("CONFIRM: " + question);
        return Confirmations.Count > 0 ? Confirmations.Dequeue() : defaultYes;
    }
}

public sealed class AccountsCommandTests : IDisposable
{
    private const string Key = "TYPED-KEY-abc123";

    private sealed class FakeTester(Func<TargetConfig, ApiKeyResolver, RedmineUser> respond) : ITargetTester
    {
        public List<(TargetConfig Target, string? KeyUsed)> Calls { get; } = [];

        public Task<RedmineUser> TestAsync(TargetConfig target, ApiKeyResolver keys, CancellationToken cancellationToken)
        {
            Calls.Add((target, keys.Resolve(target.Name, target.ApiKeyEnv)?.Key.Reveal()));
            return Task.FromResult(respond(target, keys));
        }
    }

    private sealed class ThrowingStore : ISecretStore
    {
        public string Description => "broken store";

        public bool StoresPlaintext => false;

        public ApiKey? Get(string account) => throw new InvalidOperationException("locked");

        public void Set(string account, ApiKey key) => throw new InvalidOperationException("locked");

        public bool Remove(string account) => throw new InvalidOperationException("locked");
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rmcp-acc-" + Guid.NewGuid().ToString("N"));
    private readonly InMemorySecretStore _system = new();
    private readonly FakeTerminal _terminal = new();
    private FakeTester _tester = new((_, _) => new RedmineUser(1, "bot", "Build", "Bot", null, null));
    private readonly Dictionary<string, string> _env = [];

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private string ConfigPath => Path.Combine(_dir, "config.json");

    private FileSecretStore Files => new(Path.Combine(_dir, "keys"));

    private CliContext Context(ISecretStore? system = null, bool useSystem = true) => new()
    {
        Terminal = _terminal,
        ConfigPath = ConfigPath,
        SystemStore = useSystem ? system ?? _system : null,
        FileStore = Files,
        Tester = _tester,
        GetEnvironmentVariable = name => _env.GetValueOrDefault(name),
    };

    private string ConfigText => File.Exists(ConfigPath) ? File.ReadAllText(ConfigPath) : string.Empty;

    private static Task<int> Run(CliContext context, params string[] args) => AccountsCommand.RunAsync(args, context);

    [Fact]
    public async Task Add_stores_the_typed_key_in_the_os_store_and_never_in_config_or_output()
    {
        _terminal.Secrets.Enqueue(Key);

        var exit = await Run(Context(), "add", "--name", "main", "--url", "https://redmine.example.com");

        Assert.Equal(0, exit);
        Assert.Equal(Key, _system.Get("main")!.Reveal());
        Assert.Null(Files.Get("main"));
        Assert.DoesNotContain(Key, ConfigText);
        Assert.DoesNotContain(Key, _terminal.Transcript);
        var saved = ConfigStore.Load(ConfigPath).Targets.Single();
        Assert.Equal("main", saved.Name);
        Assert.True(saved.ReadOnly);
    }

    [Fact]
    public async Task The_connection_test_uses_the_typed_key_before_anything_is_saved()
    {
        _terminal.Secrets.Enqueue(Key);
        string? seenAtTestTime = null;
        _tester = new FakeTester((target, keys) =>
        {
            seenAtTestTime = _system.Get("main")?.Reveal() ?? "not-yet-stored";
            return new RedmineUser(1, "bot", "B", "B", null, null);
        });

        await Run(Context(), "add", "--name", "main", "--url", "https://r.example.com");

        Assert.Equal("not-yet-stored", seenAtTestTime);
        Assert.Equal(Key, _tester.Calls.Single().KeyUsed);
    }

    [Fact]
    public async Task Without_an_os_store_the_key_goes_to_a_file_with_a_visible_warning()
    {
        _terminal.Secrets.Enqueue(Key);

        await Run(Context(useSystem: false), "add", "--name", "main", "--url", "https://r.example.com");

        Assert.Equal(Key, Files.Get("main")!.Reveal());
        Assert.Contains("WARNING", _terminal.Transcript);
        Assert.DoesNotContain(Key, _terminal.Transcript);
    }

    [Fact]
    public async Task A_failing_os_store_falls_back_to_the_file()
    {
        _terminal.Secrets.Enqueue(Key);

        var exit = await Run(Context(new ThrowingStore()), "add", "--name", "main", "--url", "https://r.example.com");

        Assert.Equal(0, exit);
        Assert.Equal(Key, Files.Get("main")!.Reveal());
        Assert.Contains("falling back", _terminal.Transcript);
    }

    [Fact]
    public async Task Moving_to_the_os_store_removes_a_stale_plaintext_copy()
    {
        Files.Set("main", new ApiKey("old-plaintext"));
        _terminal.Secrets.Enqueue(Key);
        _terminal.Confirmations.Enqueue(true); // replace? (target is new, so this is simply unused)

        await Run(Context(), "add", "--name", "main", "--url", "https://r.example.com");

        Assert.Null(Files.Get("main"));
        Assert.Equal(Key, _system.Get("main")!.Reveal());
    }

    [Fact]
    public async Task Api_key_env_stores_no_key_and_asks_for_none()
    {
        _env["MY_REDMINE_KEY"] = "from-env-value";

        var exit = await Run(Context(), "add", "--name", "main", "--url", "https://r.example.com", "--api-key-env", "MY_REDMINE_KEY");

        Assert.Equal(0, exit);
        Assert.Null(_system.Get("main"));
        Assert.Null(Files.Get("main"));
        Assert.Equal("MY_REDMINE_KEY", ConfigStore.Load(ConfigPath).Targets.Single().ApiKeyEnv);
        Assert.DoesNotContain("from-env-value", _terminal.Transcript + ConfigText);
        Assert.DoesNotContain("API key (input hidden)", _terminal.Transcript);
    }

    [Fact]
    public async Task Invalid_input_saves_nothing_and_asks_for_no_key()
    {
        var ex = await Assert.ThrowsAsync<UserFacingException>(() => Run(Context(), "add", "--name", "bad name", "--url", "http://plain.example.com"));

        Assert.Contains("name", ex.Message);
        Assert.Contains("http://", ex.Message);
        Assert.False(File.Exists(ConfigPath));
        Assert.DoesNotContain("API key", _terminal.Transcript);
    }

    [Fact]
    public async Task Non_interactive_add_cannot_receive_a_key()
    {
        var terminal = new FakeTerminal { IsInteractive = false };
        var context = new CliContext
        {
            Terminal = terminal,
            ConfigPath = ConfigPath,
            SystemStore = _system,
            FileStore = Files,
            Tester = _tester,
        };

        await Assert.ThrowsAsync<UserFacingException>(() => Run(context, "add", "--name", "main", "--url", "https://r.example.com"));

        Assert.False(File.Exists(ConfigPath));
        Assert.Null(_system.Get("main"));
    }

    [Fact]
    public async Task A_failed_connection_test_can_abort_the_save()
    {
        _terminal.Secrets.Enqueue(Key);
        _terminal.Confirmations.Enqueue(false); // "Save the target anyway?"
        _tester = new FakeTester((_, _) => throw new RedmineException(RedmineErrorKind.Unauthorized, "rejected the credentials"));

        var exit = await Run(Context(), "add", "--name", "main", "--url", "https://r.example.com");

        Assert.Equal(1, exit);
        Assert.False(File.Exists(ConfigPath));
        Assert.Null(_system.Get("main"));
        Assert.Contains("rejected the credentials", _terminal.Transcript);
    }

    [Fact]
    public async Task Replacing_an_existing_target_requires_confirmation()
    {
        ConfigStore.Save(ConfigPath, new RedmineMcpConfig { Targets = [new TargetConfig { Name = "main", Url = "https://old.example.com" }] });
        _terminal.Confirmations.Enqueue(false);

        var exit = await Run(Context(), "add", "--name", "main", "--url", "https://new.example.com");

        Assert.Equal(1, exit);
        Assert.Equal("https://old.example.com", ConfigStore.Load(ConfigPath).Targets.Single().Url);
    }

    [Fact]
    public async Task List_shows_where_each_key_lives_but_never_the_key()
    {
        ConfigStore.Save(ConfigPath, new RedmineMcpConfig
        {
            Targets =
            [
                new TargetConfig { Name = "os", Url = "https://a.example.com" },
                new TargetConfig { Name = "file", Url = "https://b.example.com" },
                new TargetConfig { Name = "env", Url = "https://c.example.com", ApiKeyEnv = "ENV_KEY" },
                new TargetConfig { Name = "none", Url = "https://d.example.com", ReadOnly = false },
            ],
        });
        _system.Set("os", new ApiKey("os-secret-value"));
        Files.Set("file", new ApiKey("file-secret-value"));
        _env["ENV_KEY"] = "env-secret-value";

        var exit = await Run(Context(), "list");

        Assert.Equal(0, exit);
        var text = _terminal.Transcript;
        Assert.DoesNotContain("secret-value", text);
        Assert.Contains("MISSING", text);
        Assert.Contains("plaintext", text);
        Assert.Contains("environment variable ENV_KEY", text);
        Assert.Contains("read-write", text);
    }

    [Fact]
    public async Task Remove_deletes_the_config_entry_and_every_stored_copy_of_the_key()
    {
        ConfigStore.Save(ConfigPath, new RedmineMcpConfig
        {
            Targets = [new TargetConfig { Name = "main", Url = "https://a.example.com" }, new TargetConfig { Name = "keep", Url = "https://b.example.com" }],
        });
        _system.Set("main", new ApiKey("k1"));
        Files.Set("main", new ApiKey("k2"));

        var exit = await Run(Context(), "remove", "main", "--yes");

        Assert.Equal(0, exit);
        Assert.Equal(["keep"], ConfigStore.Load(ConfigPath).Targets.Select(t => t.Name));
        Assert.Null(_system.Get("main"));
        Assert.Null(Files.Get("main"));
    }

    [Fact]
    public async Task Remove_without_confirmation_changes_nothing()
    {
        ConfigStore.Save(ConfigPath, new RedmineMcpConfig { Targets = [new TargetConfig { Name = "main", Url = "https://a.example.com" }] });
        _system.Set("main", new ApiKey("k1"));
        _terminal.Confirmations.Enqueue(false);

        Assert.Equal(1, await Run(Context(), "remove", "main"));
        Assert.Single(ConfigStore.Load(ConfigPath).Targets);
        Assert.NotNull(_system.Get("main"));
    }

    [Fact]
    public async Task Test_reports_each_target_and_fails_the_exit_code_when_any_fails()
    {
        ConfigStore.Save(ConfigPath, new RedmineMcpConfig
        {
            Targets = [new TargetConfig { Name = "good", Url = "https://a.example.com" }, new TargetConfig { Name = "bad", Url = "https://b.example.com" }],
        });
        _tester = new FakeTester((target, _) => target.Name == "bad"
            ? throw new RedmineException(RedmineErrorKind.Unauthorized, "wrong key")
            : new RedmineUser(1, "bot", "B", "B", null, null));

        var exit = await Run(Context(), "test");

        Assert.Equal(1, exit);
        Assert.Contains("good: OK", _terminal.Transcript);
        Assert.Contains("bad: FAILED - wrong key", _terminal.Transcript);
    }

    [Fact]
    public async Task Unknown_subcommands_and_options_are_rejected()
    {
        Assert.Equal(2, await Run(Context(), "frobnicate"));
        await Assert.ThrowsAsync<UserFacingException>(() => Run(Context(), "add", "--api-key", "sneaky"));
    }

    [Fact]
    public void The_key_cannot_be_passed_as_an_option()
    {
        // There is deliberately no --api-key / --key option: keys come only from the hidden prompt.
        Assert.Throws<UserFacingException>(() => new ArgParser(["--api-key", "x"], ["name"], []));
        Assert.Throws<UserFacingException>(() => new ArgParser(["--key", "x"], ["name"], []));
    }
}
