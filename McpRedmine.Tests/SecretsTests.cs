using McpRedmine.Secrets;

namespace McpRedmine.Tests;

public class ApiKeyTests
{
    [Fact]
    public void ToString_and_interpolation_never_reveal_the_value()
    {
        var key = new ApiKey("super-secret-value");

        Assert.DoesNotContain("super-secret", key.ToString());
        Assert.DoesNotContain("super-secret", $"{key}");
        Assert.Equal("super-secret-value", key.Reveal());
    }

    [Fact]
    public void Surrounding_whitespace_is_trimmed() => Assert.Equal("abc", new ApiKey("  abc\n").Reveal());

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_keys_are_rejected(string value) => Assert.ThrowsAny<ArgumentException>(() => new ApiKey(value));
}

public class FileSecretStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rmcp-keys-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Set_get_remove_round_trip()
    {
        var store = new FileSecretStore(_dir);

        Assert.Null(store.Get("main"));
        store.Set("main", new ApiKey("k1"));
        Assert.Equal("k1", store.Get("main")!.Reveal());
        Assert.True(store.Remove("main"));
        Assert.Null(store.Get("main"));
        Assert.False(store.Remove("main"));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("a/b")]
    [InlineData("a.b")]
    [InlineData("")]
    public void Account_names_cannot_traverse_paths(string account) =>
        Assert.Throws<ArgumentException>(() => new FileSecretStore(_dir).Set(account, new ApiKey("k")));

    [Fact]
    public void Key_file_is_owner_only_on_unix()
    {
        if (OperatingSystem.IsWindows())
            return;

        var store = new FileSecretStore(_dir);
        store.Set("main", new ApiKey("k"));
        Assert.False(store.HasLoosePermissions("main"));
    }
}

public class ApiKeyResolverTests
{
    private sealed class MemoryStore(string description, bool plaintext = false) : ISecretStore
    {
        public Dictionary<string, ApiKey> Keys { get; } = [];

        public string Description => description;

        public bool StoresPlaintext => plaintext;

        public ApiKey? Get(string account) => Keys.GetValueOrDefault(account);

        public void Set(string account, ApiKey key) => Keys[account] = key;

        public bool Remove(string account) => Keys.Remove(account);
    }

    [Fact]
    public void System_store_wins_over_environment_and_file()
    {
        var system = new MemoryStore("system");
        var file = new MemoryStore("file", plaintext: true);
        system.Set("a", new ApiKey("from-system"));
        file.Set("a", new ApiKey("from-file"));
        var resolver = new ApiKeyResolver(system, file, _ => "from-env");

        var resolved = resolver.Resolve("a", "ENV_A")!;

        Assert.Equal("from-system", resolved.Key.Reveal());
        Assert.Equal("system", resolved.Source);
    }

    [Fact]
    public void Environment_wins_over_file()
    {
        var file = new MemoryStore("file", plaintext: true);
        file.Set("a", new ApiKey("from-file"));
        var resolver = new ApiKeyResolver(null, file, name => name == "ENV_A" ? "from-env" : null);

        Assert.Equal("from-env", resolver.Resolve("a", "ENV_A")!.Key.Reveal());
    }

    [Fact]
    public void File_is_the_last_resort_and_none_means_null()
    {
        var file = new MemoryStore("file", plaintext: true);
        var resolver = new ApiKeyResolver(null, file, _ => null);

        Assert.Null(resolver.Resolve("a", "ENV_A"));
        file.Set("a", new ApiKey("from-file"));
        Assert.Equal("from-file", resolver.Resolve("a", "ENV_A")!.Key.Reveal());
    }

    [Fact]
    public void Environment_is_ignored_when_config_names_no_variable()
    {
        var resolver = new ApiKeyResolver(null, new MemoryStore("file"), _ => "from-env");

        Assert.Null(resolver.Resolve("a", null));
    }
}
