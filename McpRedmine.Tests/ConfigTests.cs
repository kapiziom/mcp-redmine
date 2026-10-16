using McpRedmine.Config;
using McpRedmine.Tests.TestSupport;

namespace McpRedmine.Tests;

public class ConfigValidatorTests
{
    private static RedmineMcpConfig Config(params TargetConfig[] targets) => new() { Targets = targets };

    [Fact]
    public void Valid_config_has_no_errors() =>
        Assert.Empty(ConfigValidator.Validate(Config(Targets.Make("a"), Targets.Make("b", "https://other.example.com/redmine"))));

    [Fact]
    public void Http_is_rejected_unless_explicitly_allowed()
    {
        var plain = Targets.Make("a", "http://redmine.local/");
        Assert.Contains(ConfigValidator.Validate(Config(plain)), e => e.Contains("http://"));
        Assert.Empty(ConfigValidator.Validate(Config(plain with { AllowInsecureHttp = true })));
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("../evil")]
    [InlineData("a/b")]
    public void Bad_names_are_rejected(string name) =>
        Assert.NotEmpty(ConfigValidator.Validate(Config(Targets.Make(name))));

    [Fact]
    public void Duplicate_names_are_rejected_case_insensitively() =>
        Assert.Contains(ConfigValidator.Validate(Config(Targets.Make("Prod"), Targets.Make("prod"))), e => e.Contains("duplicate"));

    [Theory]
    [InlineData("https://user:pass@redmine.example.com/")]
    [InlineData("https://redmine.example.com/?key=abc")]
    [InlineData("ftp://redmine.example.com/")]
    [InlineData("not a url")]
    public void Dangerous_or_invalid_urls_are_rejected(string url) =>
        Assert.NotEmpty(ConfigValidator.Validate(Config(Targets.Make("a", url))));

    [Fact]
    public void Missing_ca_file_is_reported()
    {
        var target = Targets.Make("a") with { CaPath = "/nope/ca.pem" };
        Assert.Contains(ConfigValidator.Validate(Config(target), _ => false), e => e.Contains("ca_path"));
        Assert.Empty(ConfigValidator.Validate(Config(target), _ => true));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(201)]
    public void Max_rows_out_of_range_is_rejected(int maxRows) =>
        Assert.Contains(ConfigValidator.Validate(Config(Targets.Make("a", maxRows: maxRows))), e => e.Contains("max_rows"));

    [Fact]
    public void Api_key_env_must_be_a_variable_name() =>
        Assert.NotEmpty(ConfigValidator.Validate(Config(Targets.Make("a") with { ApiKeyEnv = "NOT VALID; rm -rf" })));
}

public class TargetResolverTests
{
    private static TargetResolver Resolver(params TargetConfig[] targets) => new(new RedmineMcpConfig { Targets = targets });

    [Fact]
    public void Single_target_is_the_default() =>
        Assert.Equal("only", Resolver(Targets.Make("only")).Resolve(null).Name);

    [Fact]
    public void Several_targets_require_an_explicit_name()
    {
        var ex = Assert.Throws<UserFacingException>(() => Resolver(Targets.Make("a"), Targets.Make("b")).Resolve(null));
        Assert.Contains("required", ex.Message);
        Assert.Contains("a, b", ex.Message);
    }

    [Fact]
    public void Lookup_ignores_case_and_surrounding_whitespace() =>
        Assert.Equal("Prod", Resolver(Targets.Make("Prod"), Targets.Make("Test")).Resolve(" prod ").Name);

    [Fact]
    public void Unknown_target_lists_the_available_ones()
    {
        var ex = Assert.Throws<UserFacingException>(() => Resolver(Targets.Make("a")).Resolve("zzz"));
        Assert.Contains("Available: a", ex.Message);
    }

    [Fact]
    public void No_targets_explains_how_to_add_one() =>
        Assert.Contains("accounts add", Assert.Throws<UserFacingException>(() => Resolver().Resolve(null)).Message);
}

public class ConfigStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rmcp-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Missing_file_is_an_empty_config() =>
        Assert.Empty(ConfigStore.Load(Path.Combine(_dir, "none.json")).Targets);

    [Fact]
    public void Read_only_defaults_to_true_when_absent()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "c.json");
        File.WriteAllText(path, """{ "targets": [ { "name": "a", "url": "https://r.example.com" } ] }""");

        Assert.True(ConfigStore.Load(path).Targets[0].ReadOnly);
    }

    [Fact]
    public void Round_trip_uses_snake_case_and_keeps_values()
    {
        var path = Path.Combine(_dir, "c.json");
        var config = new RedmineMcpConfig
        {
            Targets = [Targets.Make("a", readOnly: false) with { ApiKeyEnv = "KEY_A", ProjectAllowlist = ["p1"] }],
        };

        ConfigStore.Save(path, config);

        var text = File.ReadAllText(path);
        Assert.Contains("\"read_only\": false", text);
        Assert.Contains("\"api_key_env\"", text);

        var loaded = ConfigStore.Load(path).Targets[0];
        Assert.False(loaded.ReadOnly);
        Assert.Equal("KEY_A", loaded.ApiKeyEnv);
        Assert.Equal(["p1"], loaded.ProjectAllowlist);
    }

    [Fact]
    public void Malformed_json_names_the_file()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "bad.json");
        File.WriteAllText(path, "{ not json");

        Assert.Contains(path, Assert.Throws<UserFacingException>(() => ConfigStore.Load(path)).Message);
    }

    [Fact]
    public void Max_rows_never_exceeds_the_hard_ceiling()
    {
        Assert.Equal(RowLimits.DefaultMaxRows, Targets.Make().EffectiveMaxRows);
        Assert.Equal(RowLimits.HardMaxRows, (Targets.Make() with { MaxRows = 5000 }).EffectiveMaxRows);
    }
}
