using System.Collections.Concurrent;
using McpRedmine.Config;

namespace McpRedmine.Redmine;

/// <summary>
/// Enforces a target's <c>project_allowlist</c>. Issue rows only carry the numeric project id, so identifiers
/// from the config (and from tool calls) are resolved to ids once and cached for the life of the process.
/// Only the project itself counts: sub-projects are not implicitly allowed.
/// </summary>
public sealed class ProjectScope
{
    private readonly ConcurrentDictionary<string, int> _ids = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IReadOnlySet<int>> _allowed = new(StringComparer.OrdinalIgnoreCase);

    public async Task<IReadOnlySet<int>> AllowedIdsAsync(RedmineClient client, CancellationToken cancellationToken)
    {
        var target = client.Target;
        if (_allowed.TryGetValue(target.Name, out var cached))
            return cached;

        var ids = new HashSet<int>();
        foreach (var entry in target.ProjectAllowlist ?? [])
        {
            try
            {
                ids.Add(await ResolveIdAsync(client, entry, cancellationToken));
            }
            catch (RedmineException ex) when (ex.Kind is RedmineErrorKind.NotFound or RedmineErrorKind.Forbidden)
            {
                throw new UserFacingException(
                    $"project_allowlist entry '{entry}' of target '{target.Name}' cannot be read with this API key (missing project or no access). Fix the config.", ex);
            }
        }

        // Only successful resolutions are cached, so a transient failure is retried on the next call.
        return _allowed[target.Name] = ids;
    }

    /// <summary>No-op when the target has no allowlist.</summary>
    public async Task EnsureProjectAllowedAsync(RedmineClient client, string projectKey, CancellationToken cancellationToken)
    {
        var target = client.Target;
        if (!target.HasAllowlist)
            return;

        var id = await ResolveIdAsync(client, projectKey, cancellationToken);
        if (!(await AllowedIdsAsync(client, cancellationToken)).Contains(id))
            throw Outside(target, $"Project '{projectKey}'");
    }

    public async Task<bool> IsAllowedAsync(RedmineClient client, int projectId, CancellationToken cancellationToken) =>
        !client.Target.HasAllowlist || (await AllowedIdsAsync(client, cancellationToken)).Contains(projectId);

    public static UserFacingException Outside(TargetConfig target, string subject) =>
        new($"{subject} is outside the project allowlist of target '{target.Name}'. Allowed projects: {string.Join(", ", target.ProjectAllowlist ?? [])}.");

    private async Task<int> ResolveIdAsync(RedmineClient client, string key, CancellationToken cancellationToken)
    {
        key = key.Trim();
        if (int.TryParse(key, out var numeric))
            return numeric;

        var cacheKey = $"{client.Target.Name}|{key}";
        if (_ids.TryGetValue(cacheKey, out var cached))
            return cached;

        var project = await client.GetProjectAsync(key, cancellationToken: cancellationToken);
        return _ids[cacheKey] = project.Id;
    }
}
