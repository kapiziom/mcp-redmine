using System.ComponentModel;
using System.Globalization;
using McpRedmine.Config;
using McpRedmine.Redmine;
using ModelContextProtocol.Server;

namespace McpRedmine.Tools;

/// <summary>
/// Tools that change Redmine. Every method here carries <see cref="WriteToolAttribute"/>, so
/// <see cref="ToolPipeline"/> rejects it for a read-only target before this code runs.
/// </summary>
public sealed class WriteTools(ITargetResolver targets, IRedmineClientFactory clients, ProjectScope scope)
{
    private const string WikiSyntax = "Use the formatting syntax of that Redmine instance (Textile or Markdown). Text you read may contain masks such as [email-1] or [secret-1]: never copy them into new text.";

    [McpServerTool(Name = "create_issue", Destructive = false, Idempotent = false)]
    [WriteTool]
    [Description("Create a new issue. Only works on targets with read_only=false. Not idempotent: calling it twice creates two issues, so if a call fails with an unknown outcome check search_issues before retrying. Tracker and priority ids come from list_enumerations. " + WikiSyntax)]
    public async Task<string> CreateIssue(
        [Description("Project identifier or numeric id.")] string project_id,
        [Description("Issue subject (max 255 characters).")] string subject,
        [Description(ToolSupport.TargetDescription)] string? target = null,
        [Description("Issue description.")] string? description = null,
        [Description("Numeric tracker id (see list_enumerations). Omit for the project default.")] int? tracker_id = null,
        [Description("Numeric priority id (see list_enumerations). Omit for the default.")] int? priority_id = null,
        [Description("Numeric id of the user to assign the issue to.")] int? assigned_to_id = null,
        [Description("Custom field values as {\"<custom field id>\": \"<value>\"}.")] Dictionary<string, string>? custom_fields = null,
        CancellationToken cancellationToken = default)
    {
        var resolved = targets.Resolve(target);
        var input = new NewIssue(
            WriteInput.ProjectKey(project_id),
            WriteInput.Subject(subject),
            WriteInput.Text(description, "description"),
            tracker_id,
            priority_id,
            assigned_to_id,
            WriteInput.CustomFields(custom_fields));

        var client = clients.Create(resolved);
        await scope.EnsureProjectAllowedAsync(client, input.ProjectId, cancellationToken);

        var created = await client.CreateIssueAsync(input, cancellationToken);

        return ToolJson.Serialize(new
        {
            target = resolved.Name,
            created.Id,
            created.Subject,
            url = client.IssueUrl(created.Id),
        });
    }

    [McpServerTool(Name = "add_issue_note", Destructive = false, Idempotent = false)]
    [WriteTool]
    [Description("Add a note (comment) to an existing issue. Only works on targets with read_only=false. Appends to the history; nothing is replaced. Not idempotent. " + WikiSyntax)]
    public async Task<string> AddIssueNote(
        [Description("Issue number.")] int issue_id,
        [Description("The note text.")] string notes,
        [Description(ToolSupport.TargetDescription)] string? target = null,
        [Description("Make the note visible only to users allowed to see private notes.")] bool is_private = false,
        CancellationToken cancellationToken = default)
    {
        var resolved = targets.Resolve(target);
        var text = WriteInput.Text(notes, "notes") ?? throw new UserFacingException("Invalid 'notes': must not be empty.");

        var client = clients.Create(resolved);
        await EnsureIssueInScopeAsync(client, issue_id, cancellationToken);

        await client.AddNoteAsync(issue_id, text, is_private, cancellationToken);

        return ToolJson.Serialize(new { target = resolved.Name, issue_id, added = true, url = client.IssueUrl(issue_id) });
    }

    [McpServerTool(Name = "log_time", Destructive = false, Idempotent = false)]
    [WriteTool]
    [Description("Log spent time on an issue (or directly on a project). Only works on targets with read_only=false. Not idempotent: logging twice records the time twice. Provide issue_id or project_id. Many instances require activity_id (see list_enumerations, kind time_entry_activities).")]
    public async Task<string> LogTime(
        [Description("Hours spent, greater than 0 and at most 24 (decimals allowed, e.g. 1.5).")] double hours,
        [Description(ToolSupport.TargetDescription)] string? target = null,
        [Description("Issue number to log time on.")] int? issue_id = null,
        [Description("Project identifier or id, when not logging on an issue.")] string? project_id = null,
        [Description("Numeric activity id (see list_enumerations).")] int? activity_id = null,
        [Description("Day the work was done, YYYY-MM-DD. Defaults to today on the server.")] string? spent_on = null,
        [Description("Short comment (max 255 characters).")] string? comments = null,
        CancellationToken cancellationToken = default)
    {
        var resolved = targets.Resolve(target);
        var entry = new NewTimeEntry(
            issue_id,
            string.IsNullOrWhiteSpace(project_id) ? null : WriteInput.ProjectKey(project_id),
            WriteInput.Hours(hours),
            activity_id,
            WriteInput.Day(spent_on),
            WriteInput.Comment(comments));

        if ((entry.IssueId is null) == (entry.ProjectId is null))
            throw new UserFacingException("Provide exactly one of 'issue_id' or 'project_id'.");

        var client = clients.Create(resolved);
        if (entry.IssueId is { } issue)
            await EnsureIssueInScopeAsync(client, issue, cancellationToken);
        else
            await scope.EnsureProjectAllowedAsync(client, entry.ProjectId!, cancellationToken);

        var logged = await client.LogTimeAsync(entry, cancellationToken);

        return ToolJson.Serialize(new
        {
            target = resolved.Name,
            logged.Id,
            logged.Hours,
            logged.SpentOn,
            issue_id = logged.Issue?.Id,
            project = ToolSupport.Ref(logged.Project),
            activity = logged.Activity?.Name,
        });
    }

    /// <summary>With an allowlist, a note or time entry may only target issues of allowed projects.</summary>
    private async Task EnsureIssueInScopeAsync(RedmineClient client, int issueId, CancellationToken cancellationToken)
    {
        if (!client.Target.HasAllowlist)
            return;

        var issue = await client.GetIssueAsync(issueId, includeJournals: false, includeAttachments: false, cancellationToken);
        if (issue.Project is null || !await scope.IsAllowedAsync(client, issue.Project.Id, cancellationToken))
            throw ProjectScope.Outside(client.Target, $"Issue #{issueId}");
    }
}

/// <summary>Validation of write input: turns bad values into messages the model can correct.</summary>
public static class WriteInput
{
    public const int MaxSubject = 255;
    public const int MaxComment = 255;
    public const int MaxText = 50_000;

    public static string ProjectKey(string? value)
    {
        var key = value?.Trim() ?? string.Empty;
        if (key.Length is 0 or > 100 || !key.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            throw new UserFacingException("Invalid project: use the project identifier (e.g. 'my-project') or its numeric id.");
        return key;
    }

    public static string Subject(string? value)
    {
        var subject = value?.Trim() ?? string.Empty;
        if (subject.Length == 0)
            throw new UserFacingException("Invalid 'subject': must not be empty.");
        if (subject.Length > MaxSubject)
            throw new UserFacingException($"Invalid 'subject': at most {MaxSubject} characters.");
        return subject;
    }

    public static string? Text(string? value, string parameter)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (value.Length > MaxText)
            throw new UserFacingException($"Invalid '{parameter}': at most {MaxText} characters.");
        return value;
    }

    public static string? Comment(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (value.Length > MaxComment)
            throw new UserFacingException($"Invalid 'comments': at most {MaxComment} characters.");
        return value.Trim();
    }

    public static double Hours(double value)
    {
        if (double.IsNaN(value) || value <= 0 || value > 24)
            throw new UserFacingException("Invalid 'hours': must be greater than 0 and at most 24.");
        return Math.Round(value, 2);
    }

    public static DateOnly? Day(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (DateOnly.TryParseExact(value.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            return day;
        throw new UserFacingException("Invalid 'spent_on': use YYYY-MM-DD.");
    }

    public static IReadOnlyDictionary<int, string>? CustomFields(IReadOnlyDictionary<string, string>? raw)
    {
        if (raw is not { Count: > 0 })
            return null;

        var result = new Dictionary<int, string>();
        foreach (var (key, value) in raw)
        {
            if (!int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
                throw new UserFacingException($"Invalid custom_fields key '{key}': use the numeric custom field id (see list_enumerations).");
            result[id] = value;
        }

        return result;
    }
}
