using System.ComponentModel;
using System.Text.Json;
using McpRedmine.Config;
using McpRedmine.Redmine;
using McpRedmine.Redmine.Attachments;
using ModelContextProtocol.Server;

namespace McpRedmine.Tools;

public sealed class IssueTools(ITargetResolver targets, IRedmineClientFactory clients, ProjectScope scope)
{
    private const int MaxDescription = 8000;
    private const int MaxNote = 4000;
    private const int MaxJournals = 50;

    [McpServerTool(Name = "search_issues", ReadOnly = true)]
    [Description("Search issues on a target. Defaults to open issues, any project. Returns compact rows; use get_issue for the full text. Filter ids (status, tracker) differ per target - see list_enumerations.")]
    public async Task<string> SearchIssues(
        [Description(ToolSupport.TargetDescription)] string? target = null,
        [Description("Project identifier or numeric id. Required when the target has a project allowlist.")] string? project_id = null,
        [Description("open (default), closed, all, or a numeric status id.")] string? status = null,
        [Description("'me' or a numeric user id.")] string? assigned_to = null,
        [Description("Numeric tracker id (see list_enumerations).")] int? tracker_id = null,
        [Description("Text contained in the subject.")] string? text = null,
        [Description("Only issues updated on or after this date/time (ISO 8601, e.g. 2025-01-31 or 2025-01-31T09:00:00Z).")] string? updated_since = null,
        [Description("Sort, e.g. 'updated_on:desc' (default) or 'id:asc', 'priority:desc'.")] string? sort = null,
        [Description("Max rows to return; capped by the target's row limit.")] int? limit = null,
        [Description("Rows to skip, for paging (use next_offset from the previous result).")] int? offset = null,
        CancellationToken cancellationToken = default)
    {
        var resolved = targets.Resolve(target);
        var client = clients.Create(resolved);
        var (pageLimit, pageOffset) = ToolSupport.Page(resolved, limit, offset);

        var search = new IssueSearch(
            project_id, status, assigned_to, tracker_id, text, ParseSince(updated_since), string.IsNullOrWhiteSpace(sort) ? "updated_on:desc" : sort);
        var query = search.ToQuery(); // validates before any request is made

        if (resolved.HasAllowlist)
        {
            if (string.IsNullOrWhiteSpace(project_id))
                throw new UserFacingException(
                    $"Target '{resolved.Name}' is restricted to projects: {string.Join(", ", resolved.ProjectAllowlist!)}. Pass project_id.");
            await scope.EnsureProjectAllowedAsync(client, project_id, cancellationToken);
        }

        var page = await client.SearchIssuesAsync(search, pageLimit, pageOffset, cancellationToken);

        // A project filter in Redmine includes sub-projects, which the allowlist does not cover.
        var rows = page.Items;
        string? note = null;
        if (resolved.HasAllowlist)
        {
            var allowed = await scope.AllowedIdsAsync(client, cancellationToken);
            rows = page.Items.Where(i => i.Project is not null && allowed.Contains(i.Project.Id)).ToList();
            if (rows.Count < page.Items.Count)
                note = $"{page.Items.Count - rows.Count} issue(s) from projects outside the allowlist were hidden.";
        }

        var items = rows.Select(i => new
        {
            i.Id,
            i.Subject,
            status = i.Status?.Name,
            tracker = i.Tracker?.Name,
            priority = i.Priority?.Name,
            project = ToolSupport.Ref(i.Project),
            assignee = PersonRedactor.Format(i.AssignedTo, resolved.RedactPeople),
            updated_on = i.UpdatedOn,
        }).ToList();

        // next_offset must advance by what the server returned, not by what was hidden.
        var envelope = ToolSupport.List("issues", resolved, items, pageOffset, page.TotalCount, page.HasMore, note);
        if (page.HasMore)
            envelope["next_offset"] = pageOffset + page.Items.Count;

        return ToolJson.Serialize(envelope);
    }

    [McpServerTool(Name = "get_issue", ReadOnly = true)]
    [Description("Get one issue in full: description, fields, custom fields and optionally the notes/history and attachment list. Long texts are truncated.")]
    public async Task<string> GetIssue(
        [Description("Issue number.")] int id,
        [Description(ToolSupport.TargetDescription)] string? target = null,
        [Description("Include the journal: notes and field changes (latest 50 entries).")] bool include_journals = false,
        [Description("Include the list of attachments (id, name, size, kind). Read their content with get_attachment.")] bool include_attachments = false,
        CancellationToken cancellationToken = default)
    {
        var resolved = targets.Resolve(target);
        var client = clients.Create(resolved);

        var issue = await client.GetIssueAsync(id, include_journals, include_attachments, cancellationToken);

        if (resolved.HasAllowlist && (issue.Project is null || !await scope.IsAllowedAsync(client, issue.Project.Id, cancellationToken)))
            throw ProjectScope.Outside(resolved, $"Issue #{id}");

        var journals = (issue.Journals ?? []).ToList();
        var shownJournals = journals.Skip(Math.Max(0, journals.Count - MaxJournals)).Select(j => MapJournal(j, resolved.RedactPeople)).ToList();

        return ToolJson.Serialize(new
        {
            target = resolved.Name,
            issue.Id,
            issue.Subject,
            status = issue.Status?.Name,
            tracker = issue.Tracker?.Name,
            priority = issue.Priority?.Name,
            project = ToolSupport.Ref(issue.Project),
            author = PersonRedactor.Format(issue.Author, resolved.RedactPeople),
            assignee = PersonRedactor.Format(issue.AssignedTo, resolved.RedactPeople),
            version = issue.FixedVersion?.Name,
            category = issue.Category?.Name,
            parent_id = issue.Parent?.Id,
            issue.StartDate,
            issue.DueDate,
            issue.DoneRatio,
            issue.EstimatedHours,
            issue.CreatedOn,
            issue.UpdatedOn,
            description = ToolSupport.Truncate(issue.Description, MaxDescription),
            custom_fields = (issue.CustomFields ?? [])
                .Where(f => HasValue(f.Value))
                .Select(f => new { f.Id, f.Name, f.Value })
                .ToList(),
            journals = include_journals ? shownJournals : null,
            journals_total = include_journals && journals.Count > MaxJournals ? journals.Count : (int?)null,
            attachments = include_attachments
                ? (issue.Attachments ?? []).Select(a => new { a.Id, a.Filename, size = a.Filesize, a.ContentType, kind = AttachmentKinds.Name(AttachmentKinds.Classify(a.Filename, a.ContentType)), author = PersonRedactor.Format(a.Author, resolved.RedactPeople), a.CreatedOn }).ToList()
                : null,
        });
    }

    private static object MapJournal(RedmineJournal journal, bool redact) => new
    {
        journal.Id,
        user = PersonRedactor.Format(journal.User, redact),
        created_on = journal.CreatedOn,
        notes = ToolSupport.Truncate(string.IsNullOrWhiteSpace(journal.Notes) ? null : journal.Notes, MaxNote),
        @private = journal.PrivateNotes == true ? true : (bool?)null,
        changes = journal.Details is { Count: > 0 }
            ? journal.Details.Select(d => $"{(d.Property == "attr" ? d.Name : $"{d.Property}:{d.Name}")}: {AsText(d.OldValue)} -> {AsText(d.NewValue)}").ToList()
            : null,
    };

    private static string AsText(JsonElement? value) =>
        value is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } element
            ? element.ValueKind == JsonValueKind.String ? element.GetString() ?? string.Empty : element.GetRawText()
            : "(none)";

    private static bool HasValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Undefined or JsonValueKind.Null => false,
        JsonValueKind.String => !string.IsNullOrWhiteSpace(value.GetString()),
        JsonValueKind.Array => value.GetArrayLength() > 0,
        _ => true,
    };

    private static DateTimeOffset? ParseSince(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed))
            return parsed;

        throw new UserFacingException("Invalid 'updated_since': use an ISO 8601 date or date-time, e.g. 2025-01-31 or 2025-01-31T09:00:00Z.");
    }
}
