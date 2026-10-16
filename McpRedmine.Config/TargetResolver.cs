namespace McpRedmine.Config;

public interface ITargetResolver
{
    IReadOnlyList<TargetConfig> Targets { get; }

    /// <summary>
    /// <paramref name="name"/> is required, except when exactly one target is configured.
    /// </summary>
    TargetConfig Resolve(string? name);
}

public sealed class TargetResolver(RedmineMcpConfig config) : ITargetResolver
{
    public IReadOnlyList<TargetConfig> Targets => config.Targets;

    public TargetConfig Resolve(string? name)
    {
        if (config.Targets.Count == 0)
            throw new UserFacingException(
                "No Redmine targets are configured. Run 'accounts add' (see 'doctor') to add one.");

        if (string.IsNullOrWhiteSpace(name))
        {
            if (config.Targets.Count == 1)
                return config.Targets[0];

            throw new UserFacingException(
                $"Parameter 'target' is required because several targets are configured. Available: {Names()}.");
        }

        var match = config.Targets.FirstOrDefault(t => t.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
        return match ?? throw new UserFacingException($"Unknown target '{name}'. Available: {Names()}.");
    }

    private string Names() => string.Join(", ", config.Targets.Select(t => t.Name));
}
