using System.Reflection;
using System.Text.Json;
using McpRedmine.Config;
using McpRedmine.Redmine;
using McpRedmine.Tests.TestSupport;
using McpRedmine.Tools;
using ModelContextProtocol.Server;

namespace McpRedmine.Tests;

public class ToolPipelineTests
{
    private static TargetResolver Resolver() => new(new RedmineMcpConfig
    {
        Targets = [Targets.Make("ro"), Targets.Make("rw", readOnly: false)],
    });

    [Fact]
    public void Write_tool_against_read_only_target_is_rejected()
    {
        var ex = Assert.Throws<UserFacingException>(() => ToolPipeline.Guard(Resolver(), "create_issue", "ro"));

        Assert.Contains("read-only", ex.Message);
        Assert.Contains("nothing was sent", ex.Message);
    }

    [Fact]
    public void Write_tool_against_writable_target_passes() =>
        ToolPipeline.Guard(Resolver(), "create_issue", "rw");

    [Fact]
    public void Guard_also_rejects_unknown_or_missing_targets()
    {
        Assert.Throws<UserFacingException>(() => ToolPipeline.Guard(Resolver(), "create_issue", "nope"));
        Assert.Throws<UserFacingException>(() => ToolPipeline.Guard(Resolver(), "create_issue", null));
    }

    [Fact]
    public void Every_tool_is_either_marked_as_a_write_tool_or_declared_read_only()
    {
        var tools = typeof(ToolPipeline).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            .Select(m => (Method: m, Attribute: m.GetCustomAttribute<McpServerToolAttribute>()))
            .Where(x => x.Attribute is not null)
            .ToList();

        Assert.NotEmpty(tools);
        foreach (var (method, attribute) in tools)
        {
            var isWrite = method.GetCustomAttribute<WriteToolAttribute>() is not null;
            Assert.False(string.IsNullOrWhiteSpace(attribute!.Name), $"{method.Name}: explicit tool name required");
            Assert.True(isWrite || attribute.ReadOnly, $"{attribute.Name}: must be [WriteTool] or ReadOnly = true");
            Assert.False(isWrite && attribute.ReadOnly, $"{attribute.Name}: cannot be both");
        }
    }

    [Fact]
    public void Every_tool_except_list_targets_takes_a_target_parameter()
    {
        var tools = typeof(ToolPipeline).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null);

        foreach (var method in tools)
        {
            var name = method.GetCustomAttribute<McpServerToolAttribute>()!.Name;
            if (name == "list_targets")
                continue;

            Assert.Contains(method.GetParameters(), p => p.Name == "target");
        }
    }
}

public class TargetToolsTests
{
    private sealed class FixedFactory(RedmineClient client) : IRedmineClientFactory
    {
        public RedmineClient Create(TargetConfig target) => client;
    }

    [Fact]
    public async Task Whoami_never_returns_the_api_key_that_redmine_includes_in_the_payload()
    {
        var handler = StubHandler.Always("""
            {"user":{"id":7,"login":"bot","firstname":"Build","lastname":"Bot","api_key":"LEAKED-KEY-IN-PAYLOAD"}}
            """);
        var target = Targets.Make("main");
        var tools = new TargetTools(new TargetResolver(new RedmineMcpConfig { Targets = [target] }),
            new FixedFactory(Targets.Client(handler, target)));

        var result = await tools.WhoAmI();

        Assert.DoesNotContain("LEAKED-KEY", result);
        using var json = JsonDocument.Parse(result);
        Assert.False(json.RootElement.TryGetProperty("login", out _), "login is hidden while people are redacted");
        Assert.Equal("B.B.", json.RootElement.GetProperty("name").GetString());
        Assert.DoesNotContain("Build", result);
        Assert.True(json.RootElement.GetProperty("read_only").GetBoolean());
    }

    [Fact]
    public async Task Whoami_shows_the_real_name_when_redaction_is_switched_off()
    {
        var handler = StubHandler.Always("""{"user":{"id":7,"login":"bot","firstname":"Build","lastname":"Bot"}}""");
        var target = Targets.Make("main") with { RedactPeople = false };
        var tools = new TargetTools(new TargetResolver(new RedmineMcpConfig { Targets = [target] }),
            new FixedFactory(Targets.Client(handler, target)));

        var json = JsonDocument.Parse(await tools.WhoAmI()).RootElement;

        Assert.Equal("bot", json.GetProperty("login").GetString());
        Assert.Equal("Build Bot", json.GetProperty("name").GetString());
    }

    [Fact]
    public void List_targets_reports_defaults_but_no_secrets()
    {
        var config = new RedmineMcpConfig
        {
            Targets = [Targets.Make("main") with { ApiKeyEnv = "SOME_ENV", ProjectAllowlist = ["p1"] }],
        };
        var tools = new TargetTools(new TargetResolver(config), new FixedFactory(Targets.Client(StubHandler.Always("{}"))));

        var result = tools.ListTargets();

        using var json = JsonDocument.Parse(result);
        Assert.Equal("main", json.RootElement.GetProperty("default_target").GetString());
        Assert.DoesNotContain("SOME_ENV", result);
        Assert.Contains("p1", result);
    }
}
