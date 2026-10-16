using McpRedmine.Config;
using Microsoft.Extensions.Logging;

namespace McpRedmine.Redmine;

public interface IRedmineClientFactory
{
    RedmineClient Create(TargetConfig target);
}

public sealed class RedmineClientFactory(
    IHttpClientFactory httpClientFactory,
    IEnumerable<string> registeredTargets,
    ILoggerFactory? loggerFactory = null) : IRedmineClientFactory
{
    private readonly HashSet<string> _registered =
        new(registeredTargets, StringComparer.OrdinalIgnoreCase);

    public RedmineClient Create(TargetConfig target)
    {
        // An unregistered name would yield a bare HttpClient without the key handler or CA settings.
        if (!_registered.Contains(target.Name))
            throw new InvalidOperationException($"Target '{target.Name}' has no registered HTTP client.");

        var http = httpClientFactory.CreateClient(RedmineServiceCollectionExtensions.ClientName(target.Name));
        return new RedmineClient(http, target, loggerFactory?.CreateLogger<RedmineClient>());
    }
}
