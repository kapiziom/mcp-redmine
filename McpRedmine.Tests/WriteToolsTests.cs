using System.Net;
using System.Text.Json;
using McpRedmine.Config;
using McpRedmine.Redmine;
using McpRedmine.Tests.TestSupport;
using McpRedmine.Tools;
using Polly.Timeout;

namespace McpRedmine.Tests;

public class WriteInputTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(24.5)]
    [InlineData(double.NaN)]
    public void Hours_must_be_between_zero_and_a_day(double hours) =>
        Assert.Throws<UserFacingException>(() => WriteInput.Hours(hours));

    [Fact]
    public void Hours_are_rounded_to_two_decimals() => Assert.Equal(1.33, WriteInput.Hours(1.3333333));

    [Theory]
    [InlineData("2025-13-01")]
    [InlineData("yesterday")]
    [InlineData("01/02/2025")]
    public void Dates_must_be_iso(string value) => Assert.Throws<UserFacingException>(() => WriteInput.Day(value));

    [Fact]
    public void Valid_date_parses() => Assert.Equal(new DateOnly(2025, 2, 1), WriteInput.Day("2025-02-01"));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Subject_is_required(string value) => Assert.Throws<UserFacingException>(() => WriteInput.Subject(value));

    [Fact]
    public void Subject_has_a_length_limit() =>
        Assert.Throws<UserFacingException>(() => WriteInput.Subject(new string('x', 256)));

    [Theory]
    [InlineData("a b")]
    [InlineData("../x")]
    [InlineData("")]
    public void Project_keys_are_restricted(string value) => Assert.Throws<UserFacingException>(() => WriteInput.ProjectKey(value));

    [Fact]
    public void Custom_field_keys_must_be_numeric()
    {
        Assert.Equal("v", WriteInput.CustomFields(new Dictionary<string, string> { ["12"] = "v" })![12]);
        Assert.Throws<UserFacingException>(() => WriteInput.CustomFields(new Dictionary<string, string> { ["name"] = "v" }));
    }

    [Fact]
    public void Write_tools_are_discovered_by_attribute() =>
        Assert.Equal(["add_issue_note", "create_issue", "log_time"], ToolPipeline.FindWriteTools(typeof(ToolPipeline).Assembly).Order());
}

public class WriteToolsTests
{
    private sealed class FixedFactory(RedmineClient client) : IRedmineClientFactory
    {
        public RedmineClient Create(TargetConfig target) => client;
    }

    private static (WriteTools Tools, StubHandler Handler) Make(Func<HttpRequestMessage, HttpResponseMessage> respond, TargetConfig? target = null)
    {
        target ??= Targets.Make("main", readOnly: false);
        var handler = new StubHandler(respond);
        var tools = new WriteTools(
            new TargetResolver(new RedmineMcpConfig { Targets = [target] }),
            new FixedFactory(Targets.Client(handler, target)),
            new ProjectScope());
        return (tools, handler);
    }

    private static JsonElement BodyOf(StubHandler handler, int index = 0) =>
        JsonDocument.Parse(handler.Bodies[index]!).RootElement;

    private static HttpResponseMessage Created(string json) => StubHandler.Json(json, HttpStatusCode.Created);

    [Fact]
    public async Task Create_issue_posts_only_the_fields_that_were_given_and_returns_the_url()
    {
        var (tools, handler) = Make(_ => Created("""{"issue":{"id":77,"subject":"Fix login"}}"""));

        var result = JsonDocument.Parse(await tools.CreateIssue("my-project", "Fix login", tracker_id: 2,
            custom_fields: new Dictionary<string, string> { ["5"] = "ACME" })).RootElement;

        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.EndsWith("/issues.json", request.RequestUri!.AbsolutePath, StringComparison.Ordinal);
        var issue = BodyOf(handler).GetProperty("issue");
        Assert.Equal("my-project", issue.GetProperty("project_id").GetString());
        Assert.Equal(2, issue.GetProperty("tracker_id").GetInt32());
        Assert.Equal(5, issue.GetProperty("custom_fields")[0].GetProperty("id").GetInt32());
        Assert.False(issue.TryGetProperty("description", out _));
        Assert.False(issue.TryGetProperty("priority_id", out _));
        Assert.Equal(77, result.GetProperty("id").GetInt32());
        Assert.Equal("https://redmine.example.com/issues/77", result.GetProperty("url").GetString());
    }

    [Fact]
    public async Task Numeric_project_ids_are_sent_as_numbers()
    {
        var (tools, handler) = Make(_ => Created("""{"issue":{"id":1,"subject":"s"}}"""));

        await tools.CreateIssue("12", "s");

        Assert.Equal(12, BodyOf(handler).GetProperty("issue").GetProperty("project_id").GetInt32());
    }

    [Fact]
    public async Task Invalid_input_never_reaches_redmine()
    {
        var (tools, handler) = Make(_ => Created("{}"));

        await Assert.ThrowsAsync<UserFacingException>(() => tools.CreateIssue("p", "  "));
        await Assert.ThrowsAsync<UserFacingException>(() => tools.AddIssueNote(1, " "));
        await Assert.ThrowsAsync<UserFacingException>(() => tools.LogTime(0, issue_id: 1));
        await Assert.ThrowsAsync<UserFacingException>(() => tools.LogTime(1)); // neither issue nor project
        await Assert.ThrowsAsync<UserFacingException>(() => tools.LogTime(1, issue_id: 1, project_id: "p")); // both

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Create_issue_in_a_project_outside_the_allowlist_sends_nothing_to_write()
    {
        var target = Targets.Make("main", readOnly: false) with { ProjectAllowlist = ["1"] };
        var (tools, handler) = Make(_ => StubHandler.Json("""{"project":{"id":2,"identifier":"other","name":"Other"}}"""), target);

        await Assert.ThrowsAsync<UserFacingException>(() => tools.CreateIssue("other", "s"));

        Assert.DoesNotContain(handler.Requests, r => r.Method != HttpMethod.Get);
    }

    [Fact]
    public async Task Add_note_puts_notes_and_accepts_an_empty_204()
    {
        var (tools, handler) = Make(_ => new HttpResponseMessage(HttpStatusCode.NoContent));

        var result = JsonDocument.Parse(await tools.AddIssueNote(5, "Deployed to staging", is_private: true)).RootElement;

        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.EndsWith("/issues/5.json", request.RequestUri!.AbsolutePath, StringComparison.Ordinal);
        var issue = BodyOf(handler).GetProperty("issue");
        Assert.Equal("Deployed to staging", issue.GetProperty("notes").GetString());
        Assert.True(issue.GetProperty("private_notes").GetBoolean());
        Assert.True(result.GetProperty("added").GetBoolean());
    }

    [Fact]
    public async Task Add_note_checks_the_issue_project_against_the_allowlist_first()
    {
        var target = Targets.Make("main", readOnly: false) with { ProjectAllowlist = ["1"] };
        var (tools, handler) = Make(_ => StubHandler.Json("""{"issue":{"id":5,"project":{"id":2,"name":"Other"}}}"""), target);

        var ex = await Assert.ThrowsAsync<UserFacingException>(() => tools.AddIssueNote(5, "hi"));

        Assert.Contains("allowlist", ex.Message);
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Put);
    }

    [Fact]
    public async Task Log_time_on_an_issue_posts_a_time_entry()
    {
        var (tools, handler) = Make(_ => Created("""{"time_entry":{"id":9,"hours":1.5,"spent_on":"2025-02-01","issue":{"id":5},"project":{"id":1,"name":"P"},"activity":{"id":8,"name":"Dev"}}}"""));

        var result = JsonDocument.Parse(await tools.LogTime(1.5, issue_id: 5, activity_id: 8, spent_on: "2025-02-01", comments: "pairing")).RootElement;

        var entry = BodyOf(handler).GetProperty("time_entry");
        Assert.Equal(1.5, entry.GetProperty("hours").GetDouble());
        Assert.Equal(5, entry.GetProperty("issue_id").GetInt32());
        Assert.Equal("2025-02-01", entry.GetProperty("spent_on").GetString());
        Assert.False(entry.TryGetProperty("project_id", out _));
        Assert.Equal(9, result.GetProperty("id").GetInt32());
        Assert.Equal("Dev", result.GetProperty("activity").GetString());
    }

    [Fact]
    public async Task Log_time_on_a_project_sends_project_id()
    {
        var (tools, handler) = Make(_ => Created("""{"time_entry":{"id":9,"hours":2}}"""));

        await tools.LogTime(2, project_id: "alpha");

        Assert.Equal("alpha", BodyOf(handler).GetProperty("time_entry").GetProperty("project_id").GetString());
    }

    [Fact]
    public async Task Redmine_validation_errors_are_relayed_for_correction()
    {
        var (tools, _) = Make(_ => StubHandler.Json("""{"errors":["Activity cannot be blank"]}""", HttpStatusCode.UnprocessableEntity));

        var ex = await Assert.ThrowsAsync<RedmineException>(() => tools.LogTime(1, issue_id: 5));

        Assert.Contains("Activity cannot be blank", ex.Message);
    }

    [Fact]
    public async Task A_timed_out_write_warns_that_the_outcome_is_unknown_but_a_read_does_not()
    {
        var client = Targets.Client(new StubHandler(_ => throw new TimeoutRejectedException()));

        var write = await Assert.ThrowsAsync<RedmineException>(() => client.CreateIssueAsync(new NewIssue("p", "s")));
        var read = await Assert.ThrowsAsync<RedmineException>(() => client.GetCurrentUserAsync());

        Assert.Contains("may or may not have been applied", write.Message);
        Assert.DoesNotContain("may or may not", read.Message);
    }

    [Fact]
    public async Task A_5xx_on_a_write_also_warns_about_the_unknown_outcome()
    {
        var client = Targets.Client(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.GatewayTimeout)));

        var ex = await Assert.ThrowsAsync<RedmineException>(() => client.AddNoteAsync(1, "x", false));

        Assert.Contains("may or may not have been applied", ex.Message);
    }
}
