using System.Globalization;
using System.Text.Json;
using McpRedmine.Config;

namespace McpRedmine.Redmine;

public sealed record NewIssue(
    string ProjectId,
    string Subject,
    string? Description = null,
    int? TrackerId = null,
    int? PriorityId = null,
    int? AssignedToId = null,
    IReadOnlyDictionary<int, string>? CustomFields = null);

public sealed record NewTimeEntry(
    int? IssueId,
    string? ProjectId,
    double Hours,
    int? ActivityId = null,
    DateOnly? SpentOn = null,
    string? Comments = null);

public sealed record RedmineTimeEntry(
    int Id,
    RedmineIdName? Project,
    RedmineIdName? Issue,
    RedmineIdName? User,
    RedmineIdName? Activity,
    double? Hours,
    string? Comments,
    DateOnly? SpentOn);

public sealed partial class RedmineClient
{
    /// <summary>Web address of an issue on this target, for tool results.</summary>
    public string IssueUrl(int id) => $"{target.Url.TrimEnd('/')}/issues/{id}";

    public async Task<RedmineIssue> CreateIssueAsync(NewIssue issue, CancellationToken cancellationToken = default)
    {
        var fields = new Dictionary<string, object>
        {
            ["project_id"] = int.TryParse(issue.ProjectId, CultureInfo.InvariantCulture, out var numeric) ? numeric : issue.ProjectId,
            ["subject"] = issue.Subject,
        };
        AddIfSet(fields, "description", issue.Description);
        AddIfSet(fields, "tracker_id", issue.TrackerId);
        AddIfSet(fields, "priority_id", issue.PriorityId);
        AddIfSet(fields, "assigned_to_id", issue.AssignedToId);
        if (issue.CustomFields is { Count: > 0 })
            fields["custom_fields"] = issue.CustomFields.Select(f => new { id = f.Key, value = f.Value }).ToList();

        var root = await SendJsonAsync(HttpMethod.Post, "issues.json", new { issue = fields }, cancellationToken);
        return Require<RedmineIssue>(root, "issue");
    }

    /// <summary>Redmine has no separate comment endpoint: a note is a PUT with <c>notes</c>, which appends a journal entry.</summary>
    public async Task AddNoteAsync(int issueId, string notes, bool isPrivate, CancellationToken cancellationToken = default)
    {
        var fields = new Dictionary<string, object> { ["notes"] = notes };
        if (isPrivate)
            fields["private_notes"] = true;

        await SendJsonAsync(HttpMethod.Put, $"issues/{issueId}.json", new { issue = fields }, cancellationToken);
    }

    public async Task<RedmineTimeEntry> LogTimeAsync(NewTimeEntry entry, CancellationToken cancellationToken = default)
    {
        var fields = new Dictionary<string, object> { ["hours"] = entry.Hours };
        AddIfSet(fields, "issue_id", entry.IssueId);
        if (entry.IssueId is null && entry.ProjectId is not null)
            fields["project_id"] = int.TryParse(entry.ProjectId, CultureInfo.InvariantCulture, out var numeric) ? numeric : entry.ProjectId;
        AddIfSet(fields, "activity_id", entry.ActivityId);
        AddIfSet(fields, "comments", entry.Comments);
        if (entry.SpentOn is { } day)
            fields["spent_on"] = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var root = await SendJsonAsync(HttpMethod.Post, "time_entries.json", new { time_entry = fields }, cancellationToken);
        return Require<RedmineTimeEntry>(root, "time_entry");
    }

    private T Require<T>(JsonElement root, string property)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(property, out var element))
            throw new RedmineException(RedmineErrorKind.InvalidResponse,
                $"Target '{target.Name}' accepted the request but returned no '{property}'." + OutcomeUnknown);

        return element.Deserialize<T>(Json)!;
    }

    private static void AddIfSet(Dictionary<string, object> fields, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            fields[name] = value;
    }

    private static void AddIfSet(Dictionary<string, object> fields, string name, int? value)
    {
        if (value is { } v)
            fields[name] = v;
    }
}
