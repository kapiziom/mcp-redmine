using System.Net;
using McpRedmine.Config;
using McpRedmine.Redmine;
using McpRedmine.Secrets;
using McpRedmine.Tests.TestSupport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace McpRedmine.Tests;

/// <summary>The real handler chain (primary handler, key handler, resilience) against an in-process server.</summary>
public sealed class RedmineHttpPipelineTests : IDisposable
{
    private const string UserJson = """{"user":{"id":1,"login":"bot","firstname":"B","lastname":"B","api_key":"x"}}""";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rmcp-pipe-" + Guid.NewGuid().ToString("N"));

    public RedmineHttpPipelineTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static RedmineClient ClientFor(TargetConfig target)
    {
        var resolver = new ApiKeyResolver(null, new FileSecretStore(Path.Combine(Path.GetTempPath(), "rmcp-none")),
            name => name == "RMCP_PIPE_KEY" ? "PIPE-KEY-1" : null);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRedmine(new RedmineMcpConfig { Targets = [target] }, resolver);
        return services.BuildServiceProvider().GetRequiredService<IRedmineClientFactory>().Create(target);
    }

    private static TargetConfig HttpTarget(string url) =>
        Targets.Make("t", url) with { AllowInsecureHttp = true, ApiKeyEnv = "RMCP_PIPE_KEY" };

    [Fact]
    public async Task Key_arrives_as_header_and_not_in_the_query()
    {
        string? header = null, query = null;
        await using var server = await FakeRedmineServer.StartAsync(app => app.MapGet("/users/current.json", (HttpContext ctx) =>
        {
            header = ctx.Request.Headers["X-Redmine-API-Key"];
            query = ctx.Request.QueryString.Value;
            return Results.Text(UserJson, "application/json");
        }));

        var user = await ClientFor(HttpTarget(server.BaseUrl)).GetCurrentUserAsync();

        Assert.Equal("bot", user.Login);
        Assert.Equal("PIPE-KEY-1", header);
        Assert.True(string.IsNullOrEmpty(query));
    }

    [Fact]
    public async Task Redirects_are_not_followed_so_the_key_cannot_leak_to_another_host()
    {
        var otherServerHits = 0;
        await using var other = await FakeRedmineServer.StartAsync(app => app.MapGet("/{**path}", () =>
        {
            Interlocked.Increment(ref otherServerHits);
            return Results.Text(UserJson, "application/json");
        }));
        await using var server = await FakeRedmineServer.StartAsync(app =>
            app.MapGet("/users/current.json", () => Results.Redirect(other.BaseUrl + "users/current.json")));

        var ex = await Assert.ThrowsAsync<RedmineException>(() => ClientFor(HttpTarget(server.BaseUrl)).GetCurrentUserAsync());

        Assert.Equal(RedmineErrorKind.Redirected, ex.Kind);
        Assert.Equal(0, otherServerHits);
    }

    [Fact]
    public async Task Transient_server_errors_on_reads_are_retried()
    {
        var calls = 0;
        await using var server = await FakeRedmineServer.StartAsync(app => app.MapGet("/users/current.json", () =>
            Interlocked.Increment(ref calls) < 3
                ? Results.StatusCode(503)
                : Results.Text(UserJson, "application/json")));

        var user = await ClientFor(HttpTarget(server.BaseUrl)).GetCurrentUserAsync();

        Assert.Equal("bot", user.Login);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task Writes_are_never_retried_so_a_flaky_server_cannot_cause_duplicates()
    {
        var posts = 0;
        await using var server = await FakeRedmineServer.StartAsync(app => app.MapPost("/issues.json", () =>
        {
            Interlocked.Increment(ref posts);
            return Results.StatusCode(503);
        }));

        var ex = await Assert.ThrowsAsync<RedmineException>(() =>
            ClientFor(HttpTarget(server.BaseUrl)).CreateIssueAsync(new NewIssue("p", "s")));

        Assert.Equal(1, posts);
        Assert.Contains("may or may not have been applied", ex.Message);
    }

    [Fact]
    public async Task Custom_ca_makes_a_privately_signed_server_trusted()
    {
        using var pki = new TestPki();
        await using var server = await FakeRedmineServer.StartAsync(
            app => app.MapGet("/users/current.json", () => Results.Text(UserJson, "application/json")), pki.ServerCertificate);
        var target = Targets.Make("t", server.BaseUrl) with { ApiKeyEnv = "RMCP_PIPE_KEY", CaPath = pki.WriteCaPem(Path.Combine(_dir, "ca.pem")) };

        var user = await ClientFor(target).GetCurrentUserAsync();

        Assert.Equal("bot", user.Login);
    }

    [Fact]
    public async Task Without_the_custom_ca_the_certificate_is_rejected_not_ignored()
    {
        using var pki = new TestPki();
        await using var server = await FakeRedmineServer.StartAsync(
            app => app.MapGet("/users/current.json", () => Results.Text(UserJson, "application/json")), pki.ServerCertificate);
        var target = Targets.Make("t", server.BaseUrl) with { ApiKeyEnv = "RMCP_PIPE_KEY" };

        var ex = await Assert.ThrowsAsync<RedmineException>(() => ClientFor(target).GetCurrentUserAsync());

        Assert.Equal(RedmineErrorKind.Tls, ex.Kind);
    }

    [Fact]
    public async Task A_different_ca_does_not_validate_the_server()
    {
        using var serverPki = new TestPki("Server CA");
        using var unrelatedPki = new TestPki("Unrelated CA");
        await using var server = await FakeRedmineServer.StartAsync(
            app => app.MapGet("/users/current.json", () => Results.Text(UserJson, "application/json")), serverPki.ServerCertificate);
        var target = Targets.Make("t", server.BaseUrl) with { ApiKeyEnv = "RMCP_PIPE_KEY", CaPath = unrelatedPki.WriteCaPem(Path.Combine(_dir, "other.pem")) };

        var ex = await Assert.ThrowsAsync<RedmineException>(() => ClientFor(target).GetCurrentUserAsync());

        Assert.Equal(RedmineErrorKind.Tls, ex.Kind);
    }

    [Fact]
    public async Task Missing_key_surfaces_before_the_server_is_contacted()
    {
        var hits = 0;
        await using var server = await FakeRedmineServer.StartAsync(app => app.MapGet("/users/current.json", () =>
        {
            Interlocked.Increment(ref hits);
            return Results.Text(UserJson, "application/json");
        }));
        var target = Targets.Make("t", server.BaseUrl) with { AllowInsecureHttp = true, ApiKeyEnv = "RMCP_UNSET_VAR" };

        var ex = await Assert.ThrowsAsync<RedmineException>(() => ClientFor(target).GetCurrentUserAsync());

        Assert.Equal(RedmineErrorKind.MissingApiKey, ex.Kind);
        Assert.Equal(0, hits);
    }
}
