using System.ComponentModel;
using McpRedmine.Config;
using McpRedmine.Redmine;
using McpRedmine.Redmine.Redaction;
using ModelContextProtocol.Server;

namespace McpRedmine.Tools;

public sealed class ProjectTools(ITargetResolver targets, IRedmineClientFactory clients, ProjectScope scope)
{
    /// <summary>Most projects scanned when a name filter or allowlist forces client-side filtering.</summary>
    private const int ScanCap = 1000;

    [McpServerTool(Name = "list_projects", ReadOnly = true)]
    [Description("List projects visible to the API key on a target (id, identifier, name, parent). Use the identifier or id as project_id in other tools.")]
    public async Task<string> ListProjects(
        [Description(ToolSupport.TargetDescription)] string? target = null,
        [Description("Only projects whose name or identifier contains this text (case-insensitive).")] string? name_contains = null,
        [Description("Max rows to return; capped by the target's row limit.")] int? limit = null,
        [Description("Rows to skip, for paging (use next_offset from the previous result).")] int? offset = null,
        CancellationToken cancellationToken = default)
    {
        var resolved = targets.Resolve(target);
        var client = clients.Create(resolved);
        var (pageLimit, pageOffset) = ToolSupport.Page(resolved, limit, offset);

        IReadOnlyList<RedmineProject> rows;
        int? total;
        bool hasMore;
        string? note = null;

        if (string.IsNullOrWhiteSpace(name_contains) && !resolved.HasAllowlist)
        {
            var page = await client.ListProjectsAsync(pageLimit, pageOffset, cancellationToken);
            (rows, total, hasMore) = (page.Items, page.TotalCount, page.HasMore);
        }
        else
        {
            // Redmine cannot filter projects portably, so scan (bounded) and filter here.
            var all = await client.ListProjectsAsync(ScanCap, 0, cancellationToken);
            IEnumerable<RedmineProject> filtered = all.Items;

            if (resolved.HasAllowlist)
            {
                var allowed = await scope.AllowedIdsAsync(client, cancellationToken);
                filtered = filtered.Where(p => allowed.Contains(p.Id));
            }

            if (!string.IsNullOrWhiteSpace(name_contains))
            {
                var needle = name_contains.Trim();
                filtered = filtered.Where(p =>
                    (p.Name?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false)
                    || (p.Identifier?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false));
            }

            var matching = filtered.ToList();
            rows = matching.Skip(pageOffset).Take(pageLimit).ToList();
            total = matching.Count;
            hasMore = pageOffset + rows.Count < matching.Count;
            if (all.HasMore)
                note = $"Only the first {ScanCap} projects were scanned; more exist on the server.";
        }

        var redactor = TextRedactor.For(resolved);
        var guard = InjectionGuard.For(resolved);
        var items = rows.Select(p => new
        {
            p.Id,
            p.Identifier,
            name = guard.Scan(redactor.Redact(p.Name), $"project {p.Id} name"),
            parent = ToolSupport.Ref(p.Parent),
            description = guard.Scan(ToolSupport.Truncate(redactor.Redact(p.Description), 200), $"project {p.Id} description"),
        }).ToList();

        var envelope = ToolSupport.List("projects", resolved, items, pageOffset, total, hasMore, note);
        ToolSupport.AddSecurity(envelope, guard);
        return ToolJson.Serialize(envelope);
    }
}
