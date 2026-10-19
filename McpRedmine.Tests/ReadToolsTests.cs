using System.Net;
using System.Text.Json;
using McpRedmine.Config;
using McpRedmine.Redmine;
using McpRedmine.Tests.TestSupport;
using McpRedmine.Tools;

namespace McpRedmine.Tests;

public class IssueSearchTests
{
    [Fact]
    public void Defaults_to_open_issues() =>
        Assert.Equal("?status_id=open", new IssueSearch().ToQuery().ToString());

    [Theory]
    [InlineData("closed", "closed")]
    [InlineData("all", "%2A")]
    [InlineData("*", "%2A")]
    [InlineData("5", "5")]
    [InlineData("OPEN", "open")]
    public void Status_values_map_to_redmine_filters(string input, string expected) =>
        Assert.Equal($"?status_id={expected}", new IssueSearch(Status: input).ToQuery().ToString());

    [Fact]
    public void Updated_since_uses_the_greater_or_equal_operator_in_utc()
    {
        var query = new IssueSearch(UpdatedSince: new DateTimeOffset(2025, 3, 4, 12, 0, 0, TimeSpan.FromHours(2))).ToQuery().ToString();

        Assert.Contains("updated_on=%3E%3D2025-03-04T10%3A00%3A00Z", query);
    }

    [Fact]
    public void Subject_search_uses_the_contains_operator() =>
        Assert.Contains("subject=~login%20bug", new IssueSearch(Text: "login bug").ToQuery().ToString().Replace("%7E", "~"));

    [Theory]
    [InlineData("status", "weird")]
    [InlineData("assigned_to", "bob")]
    [InlineData("project_id", "../x")]
    [InlineData("sort", "id; drop")]
    public void Invalid_input_is_explained_not_forwarded(string parameter, string value)
    {
        var search = parameter switch
        {
            "status" => new IssueSearch(Status: value),
            "assigned_to" => new IssueSearch(AssignedTo: value),
            "project_id" => new IssueSearch(ProjectId: value),
            _ => new IssueSearch(Sort: value),
        };

        var ex = Assert.Throws<UserFacingException>(search.ToQuery);
        Assert.Contains(parameter, ex.Message);
    }

    [Fact]
    public void Assigned_to_accepts_me_and_ids() =>
        Assert.Contains("assigned_to_id=me", new IssueSearch(AssignedTo: "ME").ToQuery().ToString());
}

public class ReadToolsTests
{
    private sealed class FixedFactory(RedmineClient client) : IRedmineClientFactory
    {
        public RedmineClient Create(TargetConfig target) => client;
    }

    private sealed class Rig
    {
        public required StubHandler Handler { get; init; }

        public required TargetConfig Target { get; init; }

        public required ProjectTools Projects { get; init; }

        public required EnumerationTools Enumerations { get; init; }

        public required IssueTools Issues { get; init; }

        public IEnumerable<string> Paths => Handler.Requests.Select(r => r.RequestUri!.PathAndQuery);
    }

    private static Rig MakeRig(Func<HttpRequestMessage, HttpResponseMessage> respond, TargetConfig? target = null)
    {
        target ??= Targets.Make("main", maxRows: 50);
        var handler = new StubHandler(respond);
        var resolver = new TargetResolver(new RedmineMcpConfig { Targets = [target] });
        var factory = new FixedFactory(Targets.Client(handler, target));
        var scope = new ProjectScope();
        return new Rig
        {
            Handler = handler,
            Target = target,
            Projects = new ProjectTools(resolver, factory, scope),
            Enumerations = new EnumerationTools(resolver, factory, scope),
            Issues = new IssueTools(resolver, factory, scope),
        };
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    private static HttpResponseMessage Ok(string json) => StubHandler.Json(json);

    private static string Query(HttpRequestMessage request, string name) =>
        System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query)[name] ?? string.Empty;

    // ---- list_projects --------------------------------------------------------------------------------------

    private const string ThreeProjects = """
        {"projects":[
          {"id":1,"identifier":"alpha","name":"Alpha Web","description":"first"},
          {"id":2,"identifier":"beta","name":"Beta API","parent":{"id":1,"name":"Alpha Web"}},
          {"id":3,"identifier":"gamma","name":"Gamma Tools"}],
         "total_count":3,"offset":0,"limit":1000}
        """;

    [Fact]
    public async Task List_projects_passes_paging_through_and_reports_the_next_offset()
    {
        var rig = MakeRig(r => Ok($$"""{"projects":[{"id":1,"identifier":"a","name":"A"}],"total_count":7,"offset":{{Query(r, "offset")}},"limit":1}"""));

        var result = Parse(await rig.Projects.ListProjects(limit: 1, offset: 2));

        Assert.Equal(7, result.GetProperty("total_count").GetInt32());
        Assert.True(result.GetProperty("has_more").GetBoolean());
        Assert.Equal(3, result.GetProperty("next_offset").GetInt32());
        Assert.Contains("limit=1", rig.Paths.Single());
        Assert.Contains("offset=2", rig.Paths.Single());
    }

    [Fact]
    public async Task List_projects_never_asks_for_more_than_the_target_row_limit()
    {
        var rig = MakeRig(_ => Ok("""{"projects":[],"total_count":0}"""), Targets.Make("main", maxRows: 20));

        await rig.Projects.ListProjects(limit: 5000);

        Assert.Contains("limit=20", rig.Paths.Single());
    }

    [Fact]
    public async Task List_projects_filters_by_name_or_identifier()
    {
        var rig = MakeRig(_ => Ok(ThreeProjects));

        var byName = Parse(await rig.Projects.ListProjects(name_contains: "api"));
        var byIdentifier = Parse(await rig.Projects.ListProjects(name_contains: "GAMMA"));

        Assert.Equal(["beta"], byName.GetProperty("projects").EnumerateArray().Select(p => p.GetProperty("identifier").GetString()));
        Assert.Equal(["gamma"], byIdentifier.GetProperty("projects").EnumerateArray().Select(p => p.GetProperty("identifier").GetString()));
        Assert.Equal(1, byName.GetProperty("total_count").GetInt32());
    }

    [Fact]
    public async Task List_projects_shows_only_allowlisted_projects()
    {
        var target = Targets.Make("main") with { ProjectAllowlist = ["alpha", "3"] };
        var rig = MakeRig(r => r.RequestUri!.AbsolutePath.EndsWith("/projects/alpha.json", StringComparison.Ordinal)
            ? Ok("""{"project":{"id":1,"identifier":"alpha","name":"Alpha Web"}}""")
            : Ok(ThreeProjects), target);

        var result = Parse(await rig.Projects.ListProjects());

        Assert.Equal([1, 3], result.GetProperty("projects").EnumerateArray().Select(p => p.GetProperty("id").GetInt32()));
    }

    // ---- list_enumerations ----------------------------------------------------------------------------------

    [Fact]
    public async Task Enumerations_hit_the_stable_core_endpoints()
    {
        var rig = MakeRig(r => r.RequestUri!.AbsolutePath switch
        {
            var p when p.EndsWith("/trackers.json", StringComparison.Ordinal) => Ok("""{"trackers":[{"id":1,"name":"Bug"}]}"""),
            var p when p.EndsWith("/issue_statuses.json", StringComparison.Ordinal) => Ok("""{"issue_statuses":[{"id":1,"name":"New","is_closed":false},{"id":5,"name":"Closed","is_closed":true}]}"""),
            var p when p.EndsWith("/issue_priorities.json", StringComparison.Ordinal) => Ok("""{"issue_priorities":[{"id":4,"name":"Normal","is_default":true,"active":true},{"id":9,"name":"Old","active":false}]}"""),
            var p when p.EndsWith("/time_entry_activities.json", StringComparison.Ordinal) => Ok("""{"time_entry_activities":[{"id":8,"name":"Dev","is_default":true}]}"""),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });

        var trackers = Parse(await rig.Enumerations.ListEnumerations("trackers"));
        var statuses = Parse(await rig.Enumerations.ListEnumerations("Statuses"));
        var priorities = Parse(await rig.Enumerations.ListEnumerations("priorities"));
        var activities = Parse(await rig.Enumerations.ListEnumerations("time_entry_activities"));

        Assert.Equal("Bug", trackers.GetProperty("items")[0].GetProperty("name").GetString());
        Assert.True(statuses.GetProperty("items")[1].GetProperty("is_closed").GetBoolean());
        Assert.Single(priorities.GetProperty("items").EnumerateArray()); // inactive one is hidden
        Assert.Equal(8, activities.GetProperty("items")[0].GetProperty("id").GetInt32());
        Assert.Equal("main", trackers.GetProperty("target").GetString());
    }

    [Fact]
    public async Task Unknown_kind_lists_the_valid_ones()
    {
        var rig = MakeRig(_ => Ok("{}"));

        var ex = await Assert.ThrowsAsync<UserFacingException>(() => rig.Enumerations.ListEnumerations("users"));

        Assert.Contains("custom_fields", ex.Message);
        Assert.Empty(rig.Handler.Requests);
    }

    [Fact]
    public async Task Old_redmine_without_enumeration_endpoints_gets_a_clear_message()
    {
        var rig = MakeRig(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var ex = await Assert.ThrowsAsync<RedmineException>(() => rig.Enumerations.ListEnumerations("priorities"));

        Assert.Equal(RedmineErrorKind.NotSupported, ex.Kind);
    }

    [Fact]
    public async Task Custom_fields_without_admin_rights_point_to_the_project_option()
    {
        var rig = MakeRig(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));

        var ex = await Assert.ThrowsAsync<UserFacingException>(() => rig.Enumerations.ListEnumerations("custom_fields"));

        Assert.Contains("project", ex.Message);
    }

    [Fact]
    public async Task Custom_fields_for_a_project_use_the_project_endpoint()
    {
        var rig = MakeRig(_ => Ok("""{"project":{"id":1,"identifier":"alpha","name":"A","issue_custom_fields":[{"id":3,"name":"Customer"}]}}"""));

        var result = Parse(await rig.Enumerations.ListEnumerations("custom_fields", project: "alpha"));

        Assert.Equal("Customer", result.GetProperty("items")[0].GetProperty("name").GetString());
        Assert.Contains("include=issue_custom_fields", rig.Paths.Single());
    }

    // ---- search_issues --------------------------------------------------------------------------------------

    private static string IssueRows(params (int Id, int ProjectId)[] rows) =>
        "{\"issues\":[" + string.Join(',', rows.Select(r =>
            $$"""{"id":{{r.Id}},"subject":"S{{r.Id}}","status":{"id":1,"name":"New"},"project":{"id":{{r.ProjectId}},"name":"P{{r.ProjectId}}"},"updated_on":"2025-01-01T00:00:00Z"}""")) + $"],\"total_count\":{rows.Length}}}";

    [Fact]
    public async Task Search_defaults_to_open_issues_sorted_by_recent_update()
    {
        var rig = MakeRig(_ => Ok(IssueRows((1, 1))));

        var result = Parse(await rig.Issues.SearchIssues());

        var request = rig.Handler.Requests.Single();
        Assert.Equal("open", Query(request, "status_id"));
        Assert.Equal("updated_on:desc", Query(request, "sort"));
        Assert.Equal("New", result.GetProperty("issues")[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task Search_rejects_bad_input_before_any_request()
    {
        var rig = MakeRig(_ => Ok(IssueRows()));

        await Assert.ThrowsAsync<UserFacingException>(() => rig.Issues.SearchIssues(status: "weird"));
        await Assert.ThrowsAsync<UserFacingException>(() => rig.Issues.SearchIssues(updated_since: "yesterday-ish"));

        Assert.Empty(rig.Handler.Requests);
    }

    [Fact]
    public async Task Search_with_an_allowlist_requires_a_project()
    {
        var rig = MakeRig(_ => Ok(IssueRows()), Targets.Make("main") with { ProjectAllowlist = ["alpha"] });

        var ex = await Assert.ThrowsAsync<UserFacingException>(() => rig.Issues.SearchIssues());

        Assert.Contains("alpha", ex.Message);
        Assert.Empty(rig.Handler.Requests);
    }

    [Fact]
    public async Task Search_refuses_a_project_outside_the_allowlist_and_hides_subproject_rows()
    {
        var target = Targets.Make("main") with { ProjectAllowlist = ["1"] };
        var rig = MakeRig(_ => Ok(IssueRows((10, 1), (11, 2), (12, 1))), target);

        await Assert.ThrowsAsync<UserFacingException>(() => rig.Issues.SearchIssues(project_id: "2"));
        var result = Parse(await rig.Issues.SearchIssues(project_id: "1"));

        Assert.Equal([10, 12], result.GetProperty("issues").EnumerateArray().Select(i => i.GetProperty("id").GetInt32()));
        Assert.Contains("1 issue(s)", result.GetProperty("note").GetString());
    }

    [Fact]
    public async Task Search_paging_advances_by_what_the_server_returned()
    {
        var rig = MakeRig(r => Ok($$"""{"issues":[{"id":1,"subject":"a"},{"id":2,"subject":"b"}],"total_count":9,"offset":{{Query(r, "offset")}},"limit":2}"""));

        var result = Parse(await rig.Issues.SearchIssues(limit: 2, offset: 4));

        Assert.True(result.GetProperty("has_more").GetBoolean());
        Assert.Equal(6, result.GetProperty("next_offset").GetInt32());
    }

    // ---- get_issue ------------------------------------------------------------------------------------------

    private const string FullIssue = """
        {"issue":{"id":42,"subject":"Crash on save","description":"DESCR","project":{"id":1,"name":"Alpha"},
          "tracker":{"id":1,"name":"Bug"},"status":{"id":2,"name":"In progress"},"priority":{"id":4,"name":"High"},
          "author":{"id":5,"name":"Ann"},"assigned_to":{"id":6,"name":"Bob"},"start_date":"2025-01-02","done_ratio":30,
          "custom_fields":[{"id":1,"name":"Customer","value":"ACME"},{"id":2,"name":"Empty","value":""},{"id":3,"name":"Multi","value":["a","b"]}],
          "journals":[
            {"id":1,"user":{"id":5,"name":"Ann"},"notes":"first","created_on":"2025-01-03T10:00:00Z","details":[]},
            {"id":2,"user":{"id":6,"name":"Bob"},"notes":"","created_on":"2025-01-04T10:00:00Z","details":[{"property":"attr","name":"status_id","old_value":"1","new_value":"2"}]}],
          "attachments":[{"id":9,"filename":"log.txt","filesize":120,"content_type":"text/plain","author":{"id":5,"name":"Ann"}}]}}
        """;

    [Fact]
    public async Task Get_issue_is_lean_by_default()
    {
        var rig = MakeRig(_ => Ok(FullIssue));

        var result = Parse(await rig.Issues.GetIssue(42));

        Assert.Equal("Crash on save", result.GetProperty("subject").GetString());
        Assert.Equal("B. (#6)", result.GetProperty("assignee").GetString());
        Assert.Equal("A. (#5)", result.GetProperty("author").GetString());
        Assert.False(result.TryGetProperty("journals", out _));
        Assert.False(result.TryGetProperty("attachments", out _));
        Assert.DoesNotContain("include", rig.Paths.Single());
        Assert.Equal(["Customer", "Multi"], result.GetProperty("custom_fields").EnumerateArray().Select(f => f.GetProperty("name").GetString()));
    }

    [Fact]
    public async Task Get_issue_can_include_journals_with_notes_and_changes_and_attachments()
    {
        var rig = MakeRig(_ => Ok(FullIssue));

        var result = Parse(await rig.Issues.GetIssue(42, include_journals: true, include_attachments: true));

        Assert.Contains("include=journals%2Cattachments", rig.Paths.Single());
        var journals = result.GetProperty("journals");
        Assert.Equal("first", Untrusted.Unwrap(journals[0].GetProperty("notes").GetString()));
        Assert.False(journals[1].TryGetProperty("notes", out _));
        Assert.Equal("status_id: 1 -> 2", journals[1].GetProperty("changes")[0].GetString());
        Assert.Equal("log.txt", result.GetProperty("attachments")[0].GetProperty("filename").GetString());
    }

    [Fact]
    public async Task Get_issue_truncates_very_long_descriptions()
    {
        var rig = MakeRig(_ => Ok($$$"""{"issue":{"id":1,"subject":"x","description":"{{{new string('a', 20000)}}}"}}"""));

        var description = Parse(await rig.Issues.GetIssue(1)).GetProperty("description").GetString()!;

        Assert.True(description.Length < 8200);
        Assert.Contains("truncated", description);
    }

    [Fact]
    public async Task Get_issue_from_a_project_outside_the_allowlist_returns_no_content()
    {
        var rig = MakeRig(_ => Ok(FullIssue), Targets.Make("main") with { ProjectAllowlist = ["2"] });

        var ex = await Assert.ThrowsAsync<UserFacingException>(() => rig.Issues.GetIssue(42));

        Assert.Contains("allowlist", ex.Message);
        Assert.DoesNotContain("Crash on save", ex.Message);
    }

    [Fact]
    public async Task Get_issue_not_found_is_explained()
    {
        var rig = MakeRig(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var ex = await Assert.ThrowsAsync<RedmineException>(() => rig.Issues.GetIssue(404));

        Assert.Equal(RedmineErrorKind.NotFound, ex.Kind);
    }
}
