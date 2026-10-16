using McpRedmine.Config;
using McpRedmine.Secrets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;

namespace McpRedmine.Redmine;

public static class RedmineServiceCollectionExtensions
{
    public static string ClientName(string targetName) => $"redmine:{targetName.ToLowerInvariant()}";

    /// <summary>Registers one fully configured <see cref="HttpClient"/> per target plus <see cref="IRedmineClientFactory"/>.</summary>
    public static IServiceCollection AddRedmine(this IServiceCollection services, RedmineMcpConfig config, ApiKeyResolver apiKeys)
    {
        foreach (var target in config.Targets)
        {
            var captured = target;
            services
                .AddHttpClient(ClientName(captured.Name), client =>
                {
                    client.BaseAddress = new Uri(captured.Url.TrimEnd('/') + "/");
                    client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
                })
                .ConfigurePrimaryHttpMessageHandler(() => RedmineHttpHandlers.CreatePrimaryHandler(captured))
                .AddHttpMessageHandler(() => new ApiKeyHandler(captured, apiKeys))
                .AddStandardResilienceHandler(options =>
                {
                    // Only reads are retried: repeating a POST/PUT could duplicate issues or time entries.
                    options.Retry.DisableForUnsafeHttpMethods();
                    options.Retry.MaxRetryAttempts = 2;
                    options.Retry.Delay = TimeSpan.FromSeconds(1);
                    options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(20);
                    options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(60);
                    options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(60);
                });
        }

        services.AddSingleton<IRedmineClientFactory>(sp => new RedmineClientFactory(
            sp.GetRequiredService<IHttpClientFactory>(),
            config.Targets.Select(t => t.Name).ToList(),
            sp.GetService<Microsoft.Extensions.Logging.ILoggerFactory>()));

        return services;
    }
}
