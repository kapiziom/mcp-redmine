using McpRedmine.Redmine;
using McpRedmine.Secrets;
using McpRedmine.Tests.TestSupport;

namespace McpRedmine.Tests;

public class ApiKeyHandlerTests
{
    private static HttpClient ClientWithKey(StubHandler stub, string? key, string baseUrl = "https://redmine.example.com/")
    {
        var target = Targets.Make(url: baseUrl) with { ApiKeyEnv = "RMCP_TEST_KEY" };
        var resolver = new ApiKeyResolver(null, new FileSecretStore(Path.Combine(Path.GetTempPath(), "rmcp-none-" + Guid.NewGuid().ToString("N"))),
            name => name == "RMCP_TEST_KEY" ? key : null);
        return new HttpClient(new ApiKeyHandler(target, resolver) { InnerHandler = stub }) { BaseAddress = new Uri(baseUrl) };
    }

    [Fact]
    public async Task Key_is_sent_in_the_header_and_never_in_the_url()
    {
        var stub = StubHandler.Always("{}");

        await ClientWithKey(stub, "KEY-123").GetAsync("issues.json?limit=5");

        var request = stub.Requests.Single();
        Assert.Equal("KEY-123", request.Headers.GetValues("X-Redmine-API-Key").Single());
        Assert.DoesNotContain("KEY-123", request.RequestUri!.ToString());
    }

    [Fact]
    public async Task Missing_key_gives_actionable_error_before_any_request()
    {
        var stub = StubHandler.Always("{}");

        var ex = await Assert.ThrowsAsync<RedmineException>(() => ClientWithKey(stub, key: null).GetAsync("issues.json"));

        Assert.Equal(RedmineErrorKind.MissingApiKey, ex.Kind);
        Assert.Contains("accounts add", ex.Message);
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public async Task Key_is_never_sent_to_another_host()
    {
        var stub = StubHandler.Always("{}");
        var client = ClientWithKey(stub, "KEY-123");

        await Assert.ThrowsAsync<RedmineException>(() => client.GetAsync("https://evil.example.net/steal"));
        await Assert.ThrowsAsync<RedmineException>(() => client.GetAsync("http://redmine.example.com/downgrade"));
        await Assert.ThrowsAsync<RedmineException>(() => client.GetAsync("https://redmine.example.com:8443/other-port"));

        Assert.Empty(stub.Requests);
    }

    [Fact]
    public async Task A_caller_supplied_key_header_is_overwritten()
    {
        var stub = StubHandler.Always("{}");
        using var request = new HttpRequestMessage(HttpMethod.Get, "issues.json");
        request.Headers.Add("X-Redmine-API-Key", "caller-value");

        await ClientWithKey(stub, "KEY-123").SendAsync(request);

        Assert.Equal(["KEY-123"], stub.Requests.Single().Headers.GetValues("X-Redmine-API-Key"));
    }
}
