using System.Net;
using System.Text;
using McpRedmine.Redmine;
using McpRedmine.Tests.TestSupport;

namespace McpRedmine.Tests;

public class RedmineQueryTests
{
    [Fact]
    public void Values_are_url_encoded_and_blank_values_skipped()
    {
        var query = new RedmineQuery().Add("subject", "a&b=c d").Add("tracker_id", (int?)3).Add("skip", "  ").Add("none", (int?)null);

        Assert.Equal("?subject=a%26b%3Dc%20d&tracker_id=3", query.ToString());
    }

    [Fact]
    public void Operator_filters_use_the_literal_equals_form_redmine_expects()
    {
        // ">=" must arrive as "updated_on=%3E%3D<date>"; a bare "updated_on>=..." is silently ignored by Redmine.
        var query = new RedmineQuery().Add("updated_on", ">=2024-01-02T03:04:05Z");

        Assert.Equal("?updated_on=%3E%3D2024-01-02T03%3A04%3A05Z", query.ToString());
    }

    [Theory]
    [InlineData("key")]
    [InlineData("API_KEY")]
    [InlineData("apikey")]
    [InlineData("token")]
    public void Credentials_can_never_be_put_in_the_query(string name) =>
        Assert.Throws<ArgumentException>(() => new RedmineQuery().Add(name, "secret"));

    [Fact]
    public void With_replaces_an_existing_parameter_without_mutating_the_original()
    {
        var original = new RedmineQuery().Add("offset", 0).Add("a", "1");
        var changed = original.With("offset", 50);

        Assert.Equal("?offset=0&a=1", original.ToString());
        Assert.Equal("?a=1&offset=50", changed.ToString());
    }
}

public class RedmineClientTests
{
    private const string UserJson = """
        {"user":{"id":7,"login":"bot","firstname":"Build","lastname":"Bot","mail":"bot@example.com",
                 "created_on":"2020-01-01T00:00:00Z","last_login_on":"2024-05-05T10:00:00Z","api_key":"LEAKED-KEY-IN-PAYLOAD"}}
        """;

    [Fact]
    public async Task Current_user_is_parsed_and_uses_a_relative_path_without_query()
    {
        var handler = StubHandler.Always(UserJson);
        var user = await Targets.Client(handler, Targets.Make(url: "https://host.example.com/redmine")).GetCurrentUserAsync();

        Assert.Equal(7, user.Id);
        Assert.Equal("Build Bot", user.DisplayName);
        Assert.Equal("https://host.example.com/redmine/users/current.json", handler.Requests.Single().RequestUri!.ToString());
        Assert.False(handler.Requests.Single().Headers.Contains("X-Redmine-API-Key"), "key header is the ApiKeyHandler's job");
    }

    [Fact]
    public void The_user_model_has_no_place_to_hold_the_api_key_from_the_payload() =>
        Assert.DoesNotContain(typeof(RedmineUser).GetProperties(), p => p.Name.Contains("Key", StringComparison.OrdinalIgnoreCase));

    private sealed record Item(int Id);

    private static StubHandler PagedServer(int total, List<(int Limit, int Offset)> seen) => new(request =>
    {
        var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query);
        var limit = int.Parse(query["limit"]!);
        var offset = int.Parse(query["offset"]!);
        seen.Add((limit, offset));

        var serverLimit = Math.Min(limit, 100);
        var ids = Enumerable.Range(offset + 1, Math.Max(0, Math.Min(serverLimit, total - offset)));
        var items = string.Join(',', ids.Select(i => $$"""{"id":{{i}}}"""));
        return StubHandler.Json($$"""{"things":[{{items}}],"total_count":{{total}},"offset":{{offset}},"limit":{{serverLimit}}}""");
    });

    [Fact]
    public async Task Paging_spans_several_requests_of_at_most_100_rows()
    {
        var seen = new List<(int, int)>();
        var client = Targets.Client(PagedServer(total: 500, seen));

        var page = await client.GetPagedAsync<Item>("things.json", "things", null, limit: 250, offset: 0);

        Assert.Equal(250, page.Items.Count);
        Assert.Equal(500, page.TotalCount);
        Assert.True(page.HasMore);
        Assert.Equal([(100, 0), (100, 100), (50, 200)], seen);
        Assert.Equal(Enumerable.Range(1, 250), page.Items.Select(i => i.Id));
    }

    [Fact]
    public async Task Paging_stops_at_the_end_and_reports_no_more()
    {
        var seen = new List<(int, int)>();
        var page = await Targets.Client(PagedServer(total: 30, seen)).GetPagedAsync<Item>("things.json", "things", null, limit: 100, offset: 10);

        Assert.Equal(20, page.Items.Count);
        Assert.False(page.HasMore);
        Assert.Single(seen);
    }

    [Fact]
    public async Task Paging_without_total_count_relies_on_a_full_page()
    {
        var handler = StubHandler.Always("""{"things":[{"id":1},{"id":2}]}""");

        var page = await Targets.Client(handler).GetPagedAsync<Item>("things.json", "things", null, limit: 2, offset: 0);

        Assert.Null(page.TotalCount);
        Assert.True(page.HasMore);
    }

    public static TheoryData<HttpStatusCode, RedmineErrorKind> StatusMapping => new()
    {
        { HttpStatusCode.Unauthorized, RedmineErrorKind.Unauthorized },
        { HttpStatusCode.Forbidden, RedmineErrorKind.Forbidden },
        { HttpStatusCode.NotFound, RedmineErrorKind.NotFound },
        { HttpStatusCode.Found, RedmineErrorKind.Redirected },
        { HttpStatusCode.MovedPermanently, RedmineErrorKind.Redirected },
        { HttpStatusCode.TooManyRequests, RedmineErrorKind.Unavailable },
        { HttpStatusCode.InternalServerError, RedmineErrorKind.Unavailable },
        { HttpStatusCode.BadGateway, RedmineErrorKind.Unavailable },
        { HttpStatusCode.Conflict, RedmineErrorKind.Other },
    };

    [Theory]
    [MemberData(nameof(StatusMapping))]
    public async Task Http_statuses_become_model_friendly_errors(HttpStatusCode status, RedmineErrorKind expected)
    {
        var handler = StubHandler.Always("""<html>secret internal stack trace api_key=ABC123</html>""", status);

        var ex = await Assert.ThrowsAsync<RedmineException>(() => Targets.Client(handler).GetCurrentUserAsync());

        Assert.Equal(expected, ex.Kind);
        Assert.DoesNotContain("ABC123", ex.Message);
        Assert.DoesNotContain("stack trace", ex.Message);
    }

    [Fact]
    public async Task Validation_errors_from_redmine_are_passed_on()
    {
        var handler = StubHandler.Always("""{"errors":["Subject cannot be blank","Project is invalid"]}""", HttpStatusCode.UnprocessableEntity);

        var ex = await Assert.ThrowsAsync<RedmineException>(() => Targets.Client(handler).GetCurrentUserAsync());

        Assert.Equal(RedmineErrorKind.Validation, ex.Kind);
        Assert.Contains("Subject cannot be blank; Project is invalid", ex.Message);
    }

    [Fact]
    public async Task Html_with_status_200_is_reported_as_invalid_response()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>login</html>", Encoding.UTF8, "text/html"),
        });

        var ex = await Assert.ThrowsAsync<RedmineException>(() => Targets.Client(handler).GetCurrentUserAsync());

        Assert.Equal(RedmineErrorKind.InvalidResponse, ex.Kind);
        Assert.Contains("REST API", ex.Message);
    }

    [Fact]
    public async Task Connection_failures_become_unavailable_without_raw_exception_text()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException(HttpRequestError.ConnectionError, "connect failed to https://10.0.0.5/?api_key=ABC123"));

        var ex = await Assert.ThrowsAsync<RedmineException>(() => Targets.Client(handler).GetCurrentUserAsync());

        Assert.Equal(RedmineErrorKind.Unavailable, ex.Kind);
        Assert.DoesNotContain("ABC123", ex.Message);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_masked_as_a_timeout()
    {
        using var cts = new CancellationTokenSource();
        var handler = new StubHandler(_ =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Targets.Client(handler).GetCurrentUserAsync(cts.Token));
    }
}
