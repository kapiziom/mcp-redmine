using System.Text.RegularExpressions;
using McpRedmine.Config;

namespace McpRedmine.Redmine;

public sealed partial class RedmineClient
{
    [GeneratedRegex("^[A-Za-z0-9_-]{1,100}$")]
    private static partial Regex ProjectKeyPattern();

    /// <summary>Redmine's own listing order, one page-range of projects.</summary>
    public Task<PagedResult<RedmineProject>> ListProjectsAsync(int limit, int offset, CancellationToken cancellationToken = default) =>
        GetPagedAsync<RedmineProject>("projects.json", "projects", null, limit, offset, cancellationToken);

    /// <param name="idOrIdentifier">Numeric id or the project identifier.</param>
    /// <param name="includeIssueCustomFields">Adds the issue custom fields enabled for the project (Redmine 4.x+).</param>
    public Task<RedmineProject> GetProjectAsync(string idOrIdentifier, bool includeIssueCustomFields = false, CancellationToken cancellationToken = default)
    {
        if (!ProjectKeyPattern().IsMatch(idOrIdentifier))
            throw new UserFacingException("Invalid project: use the project identifier (e.g. 'my-project') or its numeric id.");

        var query = new RedmineQuery().Add("include", includeIssueCustomFields ? "issue_custom_fields" : null);
        return GetAsync<RedmineProject>($"projects/{idOrIdentifier}.json", "project", query, cancellationToken);
    }

    public Task<List<RedmineTracker>> ListTrackersAsync(CancellationToken cancellationToken = default) =>
        GetAsync<List<RedmineTracker>>("trackers.json", "trackers", null, cancellationToken);

    public Task<List<RedmineIssueStatus>> ListIssueStatusesAsync(CancellationToken cancellationToken = default) =>
        GetAsync<List<RedmineIssueStatus>>("issue_statuses.json", "issue_statuses", null, cancellationToken);

    public Task<List<RedmineEnumeration>> ListIssuePrioritiesAsync(CancellationToken cancellationToken = default) =>
        GetEnumerationAsync("enumerations/issue_priorities.json", "issue_priorities", "issue priorities", cancellationToken);

    public Task<List<RedmineEnumeration>> ListTimeEntryActivitiesAsync(CancellationToken cancellationToken = default) =>
        GetEnumerationAsync("enumerations/time_entry_activities.json", "time_entry_activities", "time entry activities", cancellationToken);

    /// <summary>Admin-only on stock Redmine: a non-admin key gets 403, which callers should explain.</summary>
    public Task<List<RedmineCustomFieldDefinition>> ListCustomFieldsAsync(CancellationToken cancellationToken = default) =>
        GetAsync<List<RedmineCustomFieldDefinition>>("custom_fields.json", "custom_fields", null, cancellationToken);

    public Task<PagedResult<RedmineIssue>> SearchIssuesAsync(IssueSearch search, int limit, int offset, CancellationToken cancellationToken = default) =>
        GetPagedAsync<RedmineIssue>("issues.json", "issues", search.ToQuery(), limit, offset, cancellationToken);

    /// <summary>Unknown <c>include</c> values are ignored by Redmine, so older versions simply omit what they lack.</summary>
    public Task<RedmineIssue> GetIssueAsync(int id, bool includeJournals, bool includeAttachments, CancellationToken cancellationToken = default)
    {
        var includes = new List<string>(2);
        if (includeJournals)
            includes.Add("journals");
        if (includeAttachments)
            includes.Add("attachments");

        var query = new RedmineQuery().Add("include", includes.Count == 0 ? null : string.Join(',', includes));
        return GetAsync<RedmineIssue>($"issues/{id}.json", "issue", query, cancellationToken);
    }

    private async Task<List<RedmineEnumeration>> GetEnumerationAsync(string path, string property, string label, CancellationToken cancellationToken)
    {
        try
        {
            return await GetAsync<List<RedmineEnumeration>>(path, property, null, cancellationToken);
        }
        catch (RedmineException ex) when (ex.Kind == RedmineErrorKind.NotFound)
        {
            throw new RedmineException(RedmineErrorKind.NotSupported,
                $"Target '{Target.Name}' does not expose {label} through the REST API (older Redmine version).", ex);
        }
    }
}
