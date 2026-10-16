using System.Reflection;
using System.Text.Json;
using McpRedmine.Config;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpRedmine.Tools;

/// <summary>
/// Single choke point around every tool call: enforces <c>read_only</c> for write tools and turns
/// <see cref="UserFacingException"/> into an <c>isError</c> result the model can act on.
/// </summary>
public static class ToolPipeline
{
    private static readonly Lazy<IReadOnlySet<string>> DefaultWriteTools = new(() => FindWriteTools(typeof(ToolPipeline).Assembly));

    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Create(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next)
    {
        return async (context, cancellationToken) =>
        {
            var services = context.Services
                ?? throw new InvalidOperationException("Tool call has no service provider.");

            try
            {
                var toolName = context.Params?.Name;
                if (toolName is not null && DefaultWriteTools.Value.Contains(toolName))
                {
                    var resolver = (ITargetResolver)services.GetService(typeof(ITargetResolver))!;
                    Guard(resolver, toolName, ReadTarget(context.Params!));
                }

                return await next(context, cancellationToken);
            }
            catch (UserFacingException ex)
            {
                return Error(ex.Message);
            }
        };
    }

    /// <summary>Throws when <paramref name="toolName"/> would write to a target that is read-only.</summary>
    public static void Guard(ITargetResolver resolver, string toolName, string? targetName)
    {
        var target = resolver.Resolve(targetName);
        if (target.ReadOnly)
            throw new UserFacingException(
                $"Target '{target.Name}' is read-only, so '{toolName}' was not executed and nothing was sent to Redmine. "
                + "Set read_only to false for this target in the config to allow writes.");
    }

    public static IReadOnlySet<string> FindWriteTools(Assembly assembly) =>
        assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            .Where(m => m.GetCustomAttribute<WriteToolAttribute>() is not null)
            .Select(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name
                         ?? throw new InvalidOperationException($"{m.DeclaringType?.Name}.{m.Name} is a write tool without an explicit tool Name."))
            .ToHashSet(StringComparer.Ordinal);

    private static string? ReadTarget(CallToolRequestParams parameters) =>
        parameters.Arguments is { } arguments
        && arguments.TryGetValue("target", out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static CallToolResult Error(string message) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = message }],
    };
}
