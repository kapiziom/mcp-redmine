using System.ComponentModel;
using McpRedmine.Config;
using McpRedmine.Redmine;
using ModelContextProtocol.Server;

namespace McpRedmine.Tools;

public sealed class TargetTools(ITargetResolver targets, IRedmineClientFactory clients)
{
    [McpServerTool(Name = "list_targets", ReadOnly = true, OpenWorld = false)]
    [Description("List the configured Redmine targets (name, URL, whether writes are allowed, project allowlist). Call this first to learn valid values for the 'target' parameter of other tools.")]
    public string ListTargets()
    {
        var rows = targets.Targets.Select(t => new
        {
            name = t.Name,
            url = t.Url,
            read_only = t.ReadOnly,
            redact_people = t.RedactPeople,
            project_allowlist = t.HasAllowlist ? t.ProjectAllowlist : null,
            max_rows = t.EffectiveMaxRows,
        });

        return ToolJson.Serialize(new { targets = rows, default_target = targets.Targets.Count == 1 ? targets.Targets[0].Name : null });
    }

    [McpServerTool(Name = "whoami", ReadOnly = true)]
    [Description("Show which Redmine account the configured API key belongs to on a target. Use it to check that a target works.")]
    public async Task<string> WhoAmI(
        [Description("Target name from list_targets. Optional only when exactly one target is configured.")]
        string? target = null,
        CancellationToken cancellationToken = default)
    {
        var resolved = targets.Resolve(target);
        var user = await clients.Create(resolved).GetCurrentUserAsync(cancellationToken);

        // With redaction on, neither the name nor the login (usually derived from it) is shown.
        return ToolJson.Serialize(new
        {
            target = resolved.Name,
            user.Id,
            login = resolved.RedactPeople ? null : user.Login,
            name = resolved.RedactPeople ? PersonRedactor.Initials(user.DisplayName) : user.DisplayName,
            read_only = resolved.ReadOnly,
        });
    }
}
