using McpRedmine.Secrets;

namespace McpRedmine.Tests;

public class WindowsCredentialStoreTests
{
    [Fact]
    public void Round_trip_against_the_real_credential_manager()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var store = new WindowsCredentialStore();
        var account = "rmcp-test-" + Guid.NewGuid().ToString("N")[..12];
        try
        {
            Assert.Null(store.Get(account));

            store.Set(account, new ApiKey("0123456789abcdef-ąę"));
            Assert.Equal("0123456789abcdef-ąę", store.Get(account)!.Reveal());

            store.Set(account, new ApiKey("replaced"));
            Assert.Equal("replaced", store.Get(account)!.Reveal());

            Assert.True(store.Remove(account));
            Assert.Null(store.Get(account));
            Assert.False(store.Remove(account));
        }
        finally
        {
            store.Remove(account);
        }
    }
}

public class CliSecretStoreTests
{
    private sealed class RecordingRunner(Func<string, IReadOnlyList<string>, ProcessResult> respond) : IProcessRunner
    {
        public List<(string File, IReadOnlyList<string> Args, string? Stdin)> Calls { get; } = [];

        public ProcessResult Run(string fileName, IReadOnlyList<string> arguments, string? standardInput, TimeSpan timeout)
        {
            Calls.Add((fileName, arguments, standardInput));
            return respond(fileName, arguments);
        }
    }

    private static ProcessResult Ok(string stdout = "") => new(0, stdout, string.Empty);

    [Fact]
    public void Keychain_gets_the_secret_on_stdin_and_never_in_arguments()
    {
        var runner = new RecordingRunner((_, _) => Ok());

        new MacKeychainStore(runner).Set("main", new ApiKey("TOPSECRET123"));

        var call = Assert.Single(runner.Calls);
        Assert.Equal(["-i"], call.Args);
        Assert.Contains("TOPSECRET123", call.Stdin);
        Assert.DoesNotContain(call.Args, a => a.Contains("TOPSECRET", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("quo\"te")]
    [InlineData("back\\slash")]
    [InlineData("new\nline")]
    public void Keychain_refuses_keys_that_could_break_out_of_the_command(string key)
    {
        var runner = new RecordingRunner((_, _) => Ok());

        Assert.Throws<NotSupportedException>(() => new MacKeychainStore(runner).Set("main", new ApiKey(key)));
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public void Keychain_reads_the_password_from_stdout_and_maps_failure_to_null()
    {
        var found = new MacKeychainStore(new RecordingRunner((_, _) => Ok("stored-key\n")));
        var missing = new MacKeychainStore(new RecordingRunner((_, _) => new ProcessResult(44, string.Empty, "not found")));

        Assert.Equal("stored-key", found.Get("main")!.Reveal());
        Assert.Null(missing.Get("main"));
    }

    [Fact]
    public void Libsecret_gets_the_secret_on_stdin_and_never_in_arguments()
    {
        var runner = new RecordingRunner((_, _) => Ok());

        new LibSecretStore(runner).Set("main", new ApiKey("TOPSECRET123"));

        var call = Assert.Single(runner.Calls);
        Assert.Equal("secret-tool", call.File);
        Assert.Equal("TOPSECRET123", call.Stdin);
        Assert.DoesNotContain(call.Args, a => a.Contains("TOPSECRET", StringComparison.Ordinal));
        Assert.Contains("store", call.Args);
    }

    [Fact]
    public void Libsecret_lookup_and_remove()
    {
        var runner = new RecordingRunner((_, args) => args[0] == "lookup" ? Ok("the-key\n") : Ok());
        var store = new LibSecretStore(runner);

        Assert.Equal("the-key", store.Get("main")!.Reveal());
        Assert.True(store.Remove("main"));
        Assert.Contains(runner.Calls, c => c.Args[0] == "clear");
    }

    [Fact]
    public void Libsecret_availability_follows_whether_the_tool_can_be_started()
    {
        Assert.True(LibSecretStore.IsAvailable(new RecordingRunner((_, _) => new ProcessResult(1, string.Empty, string.Empty))));
        Assert.False(LibSecretStore.IsAvailable(new RecordingRunner((_, _) => throw new System.ComponentModel.Win32Exception("not found"))));
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("a b")]
    [InlineData("")]
    public void Account_names_are_validated_before_any_process_is_started(string account)
    {
        var runner = new RecordingRunner((_, _) => Ok());

        Assert.Throws<ArgumentException>(() => new LibSecretStore(runner).Get(account));
        Assert.Throws<ArgumentException>(() => new MacKeychainStore(runner).Get(account));
        Assert.Empty(runner.Calls);
    }
}
