using McpRedmine.Config;
using McpRedmine.Redmine;
using McpRedmine.Redmine.Attachments;
using McpRedmine.Secrets;
using McpRedmine.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace McpRedmine.Cli;

public static class ServeCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        var configPath = ConfigPaths.ConfigFile();
        var config = ConfigStore.Load(configPath);

        var problems = ConfigValidator.Validate(config);
        if (problems.Count > 0)
        {
            await Console.Error.WriteLineAsync($"Invalid config '{configPath}':");
            foreach (var problem in problems)
                await Console.Error.WriteLineAsync($"  - {problem}");
            return 1;
        }

        var builder = Host.CreateApplicationBuilder(args);

        // stdout is reserved for MCP protocol messages; every log line goes to stderr.
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

        var apiKeys = new ApiKeyResolver(SystemSecretStore.TryCreate(), new FileSecretStore(ConfigPaths.KeysDirectory()));

        builder.Services.AddSingleton(config);
        builder.Services.AddSingleton<ITargetResolver, TargetResolver>();
        builder.Services.AddSingleton<ProjectScope>();
        builder.Services.AddSingleton(new AttachmentStore(Path.Combine(ConfigPaths.ConfigDirectory(), "cache", "attachments")));
        builder.Services.AddSingleton<AttachmentBytesCache>();
        builder.Services.AddRedmine(config, apiKeys);

        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithTools<TargetTools>()
            .WithTools<ProjectTools>()
            .WithTools<EnumerationTools>()
            .WithTools<IssueTools>()
            .WithTools<AttachmentTools>()
            .WithTools<WriteTools>()
            .WithRequestFilters(filters => filters.AddCallToolFilter(ToolPipeline.Create));

        await builder.Build().RunAsync();
        return 0;
    }
}
