using System.Text.Json;
using System.Text.Json.Nodes;
using McpRedmine.Config;

namespace McpRedmine.Clients;

/// <summary>
/// Registers the server in a client that keeps MCP servers in a JSON file (<c>{"mcpServers": {...}}</c>):
/// Claude Desktop and Cursor. Touches only its own entry; everything else in the file is kept as is.
/// A file that cannot be parsed is never rewritten.
/// </summary>
public sealed class JsonConfigInstaller(
    string id,
    string displayName,
    string configPath,
    string detectPath,
    Func<DateTime>? clock = null) : IMcpClientInstaller
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };
    private static readonly JsonDocumentOptions ReadOptions = new() { CommentHandling = JsonCommentHandling.Disallow };

    private readonly Func<DateTime> _clock = clock ?? (() => DateTime.UtcNow);

    public string Id => id;

    public string DisplayName => displayName;

    public string ConfigPath => configPath;

    public string? NotDetectedReason() =>
        File.Exists(detectPath) || Directory.Exists(detectPath)
            ? null
            : $"{detectPath} not found";

    public InstallPlan Plan(ServerRegistration registration)
    {
        var root = ReadRoot();
        var existing = (root?["mcpServers"] as JsonObject)?[registration.Name];

        if (existing is null)
            return new InstallPlan(PlanAction.Add,
                [File.Exists(configPath) ? $"back up {configPath}" : $"create {configPath}", $"add mcpServers.{registration.Name}: {registration.DisplayCommand}"]);

        return Matches(existing, registration)
            ? new InstallPlan(PlanAction.NoChange, [$"mcpServers.{registration.Name} in {configPath} is already up to date"])
            : new InstallPlan(PlanAction.Update, [$"back up {configPath}", $"replace mcpServers.{registration.Name} with: {registration.DisplayCommand}"]);
    }

    public void Apply(ServerRegistration registration)
    {
        var root = ReadRoot() ?? new JsonObject();
        var servers = root["mcpServers"] as JsonObject;
        if (root["mcpServers"] is not null && servers is null)
            throw new UserFacingException($"'mcpServers' in {configPath} is not an object; fix the file by hand and run setup again.");

        if (servers?[registration.Name] is { } current && Matches(current, registration))
            return; // idempotent: no backup, no write

        servers ??= [];
        servers[registration.Name] = new JsonObject
        {
            ["command"] = registration.Command,
            ["args"] = new JsonArray(registration.Arguments.Select(a => (JsonNode)JsonValue.Create(a)!).ToArray()),
        };
        root["mcpServers"] = servers;

        var directory = Path.GetDirectoryName(configPath)!;
        Directory.CreateDirectory(directory);

        if (File.Exists(configPath))
            File.Copy(configPath, $"{configPath}.bak-{_clock():yyyyMMddHHmmss}", overwrite: true);

        // Write next to the target and move over it, so a crash never leaves a half-written client config.
        var temp = Path.Combine(directory, $".{Path.GetFileName(configPath)}.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(temp, root.ToJsonString(WriteOptions));
        File.Move(temp, configPath, overwrite: true);
    }

    private JsonObject? ReadRoot()
    {
        if (!File.Exists(configPath))
            return null;

        var text = File.ReadAllText(configPath);
        if (string.IsNullOrWhiteSpace(text))
            return null;

        try
        {
            return JsonNode.Parse(text, documentOptions: ReadOptions) as JsonObject
                ?? throw new UserFacingException($"{configPath} does not contain a JSON object; not touching it.");
        }
        catch (JsonException ex)
        {
            throw new UserFacingException($"{configPath} is not valid JSON ({ex.Message}); not touching it. Fix it and run setup again.", ex);
        }
    }

    private static bool Matches(JsonNode existing, ServerRegistration registration)
    {
        if (existing is not JsonObject entry
            || entry["command"]?.GetValue<string>() != registration.Command
            || entry["args"] is not JsonArray args
            || args.Count != registration.Arguments.Count)
            return false;

        return args.Select(a => a?.GetValue<string>()).SequenceEqual(registration.Arguments);
    }
}
