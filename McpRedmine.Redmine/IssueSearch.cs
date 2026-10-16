using System.Globalization;
using System.Text.RegularExpressions;
using McpRedmine.Config;

namespace McpRedmine.Redmine;

/// <summary>Validated search criteria for <c>/issues.json</c>. Invalid input becomes a message the model can fix.</summary>
public sealed partial record IssueSearch(
    string? ProjectId = null,
    string? Status = null,
    string? AssignedTo = null,
    int? TrackerId = null,
    string? Text = null,
    DateTimeOffset? UpdatedSince = null,
    string? Sort = null)
{
    public const int MaxTextLength = 200;

    [GeneratedRegex("^[A-Za-z0-9_-]{1,100}$")]
    private static partial Regex ProjectPattern();

    [GeneratedRegex("^[a-z_.]+(:(asc|desc))?(,[a-z_.]+(:(asc|desc))?)*$")]
    private static partial Regex SortPattern();

    [GeneratedRegex("^[0-9]{1,9}$")]
    private static partial Regex DigitsPattern();

    public RedmineQuery ToQuery()
    {
        var query = new RedmineQuery();

        if (!string.IsNullOrWhiteSpace(ProjectId))
        {
            if (!ProjectPattern().IsMatch(ProjectId))
                throw Invalid("project_id", "use the project identifier (e.g. 'my-project') or its numeric id");
            query.Add("project_id", ProjectId);
        }

        query.Add("status_id", StatusFilter());

        if (!string.IsNullOrWhiteSpace(AssignedTo))
        {
            if (!AssignedTo.Equals("me", StringComparison.OrdinalIgnoreCase) && !DigitsPattern().IsMatch(AssignedTo))
                throw Invalid("assigned_to", "use 'me' or a numeric user id");
            query.Add("assigned_to_id", AssignedTo.ToLowerInvariant());
        }

        query.Add("tracker_id", TrackerId);

        if (!string.IsNullOrWhiteSpace(Text))
        {
            if (Text.Length > MaxTextLength)
                throw Invalid("text", $"at most {MaxTextLength} characters");
            // "~" is Redmine's "contains" operator; it needs the literal "=" form like every operator filter.
            query.Add("subject", "~" + Text.Trim());
        }

        if (UpdatedSince is { } since)
            query.Add("updated_on", ">=" + since.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture));

        if (!string.IsNullOrWhiteSpace(Sort))
        {
            if (!SortPattern().IsMatch(Sort))
                throw Invalid("sort", "use field[:asc|:desc], e.g. 'updated_on:desc'");
            query.Add("sort", Sort);
        }

        return query;
    }

    private string StatusFilter()
    {
        var status = string.IsNullOrWhiteSpace(Status) ? "open" : Status.Trim().ToLowerInvariant();
        return status switch
        {
            "open" => "open",
            "closed" => "closed",
            "all" or "*" => "*",
            _ when DigitsPattern().IsMatch(status) => status,
            _ => throw Invalid("status", "use open, closed, all or a numeric status id (see list_enumerations)"),
        };
    }

    private static UserFacingException Invalid(string parameter, string hint) =>
        new($"Invalid '{parameter}': {hint}.");
}
