using System.Text.Json;

namespace McpRedmine.Redmine;

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int? TotalCount, int Offset, bool HasMore);

public sealed record RedmineIdName(int Id, string? Name);

/// <summary>
/// Deliberately has no API key property: <c>/users/current.json</c> returns the caller's <c>api_key</c>,
/// and it must never reach a tool result or a log line.
/// </summary>
public sealed record RedmineUser(
    int Id,
    string? Login,
    string? Firstname,
    string? Lastname,
    string? Mail,
    DateTimeOffset? LastLoginOn)
{
    public string DisplayName => $"{Firstname} {Lastname}".Trim();
}

public sealed record RedmineProject(
    int Id,
    string? Identifier,
    string? Name,
    string? Description,
    int? Status,
    RedmineIdName? Parent,
    List<RedmineIdName>? IssueCustomFields);

public sealed record RedmineTracker(int Id, string? Name);

public sealed record RedmineIssueStatus(int Id, string? Name, bool? IsClosed);

/// <summary>Issue priorities and time-entry activities share this shape.</summary>
public sealed record RedmineEnumeration(int Id, string? Name, bool? IsDefault, bool? Active);

public sealed record RedmineCustomFieldDefinition(
    int Id,
    string? Name,
    string? CustomizedType,
    string? FieldFormat,
    bool? IsRequired,
    bool? Multiple,
    List<RedmineCustomFieldValue>? PossibleValues);

public sealed record RedmineCustomFieldValue(string? Value, string? Label);

public sealed record RedmineCustomFieldEntry(int Id, string? Name, JsonElement Value);

public sealed record RedmineJournalDetail(string? Property, string? Name, JsonElement? OldValue, JsonElement? NewValue);

public sealed record RedmineJournal(
    int Id,
    RedmineIdName? User,
    string? Notes,
    DateTimeOffset? CreatedOn,
    bool? PrivateNotes,
    List<RedmineJournalDetail>? Details);

public sealed record RedmineAttachment(
    int Id,
    string? Filename,
    long? Filesize,
    string? ContentType,
    RedmineIdName? Author,
    DateTimeOffset? CreatedOn);

public sealed record RedmineIssue(
    int Id,
    string? Subject,
    string? Description,
    RedmineIdName? Project,
    RedmineIdName? Tracker,
    RedmineIdName? Status,
    RedmineIdName? Priority,
    RedmineIdName? Author,
    RedmineIdName? AssignedTo,
    RedmineIdName? FixedVersion,
    RedmineIdName? Category,
    RedmineIdName? Parent,
    DateOnly? StartDate,
    DateOnly? DueDate,
    int? DoneRatio,
    double? EstimatedHours,
    DateTimeOffset? CreatedOn,
    DateTimeOffset? UpdatedOn,
    List<RedmineCustomFieldEntry>? CustomFields,
    List<RedmineJournal>? Journals,
    List<RedmineAttachment>? Attachments);
