using System.ComponentModel;
using McpRedmine.Config;
using McpRedmine.Redmine;
using ModelContextProtocol.Server;

namespace McpRedmine.Tools;

public sealed class EnumerationTools(ITargetResolver targets, IRedmineClientFactory clients, ProjectScope scope)
{
    private static readonly string[] Kinds = ["trackers", "statuses", "priorities", "custom_fields", "time_entry_activities"];

    private const int MaxPossibleValues = 50;

    [McpServerTool(Name = "list_enumerations", ReadOnly = true)]
    [Description("List the ids used by one Redmine target: trackers, statuses, priorities, custom_fields or time_entry_activities. Ids differ between instances, so look them up per target before passing tracker_id/priority_id/activity_id.")]
    public async Task<string> ListEnumerations(
        [Description("One of: trackers, statuses, priorities, custom_fields, time_entry_activities.")] string kind,
        [Description(ToolSupport.TargetDescription)] string? target = null,
        [Description("Only for custom_fields: project id or identifier. Without it Redmine needs an administrator key.")] string? project = null,
        CancellationToken cancellationToken = default)
    {
        var resolved = targets.Resolve(target);
        var normalized = (kind ?? string.Empty).Trim().ToLowerInvariant();
        if (!Kinds.Contains(normalized))
            throw new UserFacingException($"Unknown kind '{kind}'. Use one of: {string.Join(", ", Kinds)}.");

        var client = clients.Create(resolved);

        object items = normalized switch
        {
            "trackers" => (await client.ListTrackersAsync(cancellationToken)).Select(t => new { t.Id, t.Name }).ToList(),
            "statuses" => (await client.ListIssueStatusesAsync(cancellationToken)).Select(s => new { s.Id, s.Name, is_closed = s.IsClosed }).ToList(),
            "priorities" => Enumerations(await client.ListIssuePrioritiesAsync(cancellationToken)),
            "time_entry_activities" => Enumerations(await client.ListTimeEntryActivitiesAsync(cancellationToken)),
            _ => await CustomFieldsAsync(client, project, cancellationToken),
        };

        return ToolJson.Serialize(new { target = resolved.Name, kind = normalized, items });
    }

    private static List<object> Enumerations(IEnumerable<RedmineEnumeration> values) =>
        values.Where(v => v.Active != false)
            .Select(v => (object)new { v.Id, v.Name, is_default = v.IsDefault == true ? true : (bool?)null })
            .ToList();

    private async Task<List<object>> CustomFieldsAsync(RedmineClient client, string? project, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(project))
        {
            await scope.EnsureProjectAllowedAsync(client, project, cancellationToken);
            var found = await client.GetProjectAsync(project, includeIssueCustomFields: true, cancellationToken);
            if (found.IssueCustomFields is null)
                throw new UserFacingException(
                    $"Target '{client.Target.Name}' does not report issue custom fields per project (older Redmine). "
                    + "Open an issue with get_issue to see the custom fields it carries.");

            return found.IssueCustomFields.Select(f => (object)new { f.Id, f.Name }).ToList();
        }

        try
        {
            var definitions = await client.ListCustomFieldsAsync(cancellationToken);
            return definitions
                .Where(f => f.CustomizedType is "issue" or "time_entry")
                .Select(f => (object)new
                {
                    f.Id,
                    f.Name,
                    applies_to = f.CustomizedType,
                    format = f.FieldFormat,
                    required = f.IsRequired,
                    multiple = f.Multiple,
                    possible_values = f.PossibleValues?.Take(MaxPossibleValues).Select(v => v.Value).ToList(),
                })
                .ToList();
        }
        catch (RedmineException ex) when (ex.Kind is RedmineErrorKind.Forbidden or RedmineErrorKind.NotFound)
        {
            throw new UserFacingException(
                $"Target '{client.Target.Name}' only lists all custom fields to administrators. "
                + "Pass 'project' to list the issue custom fields enabled for one project, or read them from get_issue.", ex);
        }
    }
}
