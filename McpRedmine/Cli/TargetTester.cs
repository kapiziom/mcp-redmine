using McpRedmine.Config;
using McpRedmine.Redmine;
using McpRedmine.Secrets;
using Microsoft.Extensions.DependencyInjection;

namespace McpRedmine.Cli;

public interface ITargetTester
{
    /// <summary>Calls <c>/users/current.json</c> with the real handler chain (key header, custom CA, no redirects).</summary>
    Task<RedmineUser> TestAsync(TargetConfig target, ApiKeyResolver keys, CancellationToken cancellationToken);
}

public sealed class TargetTester : ITargetTester
{
    public async Task<RedmineUser> TestAsync(TargetConfig target, ApiKeyResolver keys, CancellationToken cancellationToken)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRedmine(new RedmineMcpConfig { Targets = [target] }, keys);

        await using var provider = services.BuildServiceProvider();
        return await provider.GetRequiredService<IRedmineClientFactory>().Create(target).GetCurrentUserAsync(cancellationToken);
    }
}
