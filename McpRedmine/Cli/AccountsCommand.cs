using System.Globalization;
using McpRedmine.Config;
using McpRedmine.Redmine;
using McpRedmine.Secrets;

namespace McpRedmine.Cli;

/// <summary>
/// <c>accounts add|list|remove|test</c>. The API key is only ever typed into the hidden prompt of
/// <c>add</c>; it is not an option, not read from stdin and never echoed or printed back.
/// </summary>
public static class AccountsCommand
{
    private static readonly string[] AddValueOptions = ["name", "url", "ca", "max-rows", "project", "api-key-env"];
    private static readonly string[] AddFlags = ["read-write", "allow-insecure-http", "yes"];

    public static async Task<int> RunAsync(string[] args, CliContext context, CancellationToken cancellationToken = default)
    {
        if (args.Length == 0)
            return Usage(context);

        var rest = args[1..];
        return args[0].ToLowerInvariant() switch
        {
            "add" => await AddAsync(rest, context, cancellationToken),
            "list" => List(context),
            "remove" => Remove(rest, context),
            "test" => await TestAsync(rest, context, cancellationToken),
            _ => Usage(context),
        };
    }

    private static int Usage(CliContext context)
    {
        context.Terminal.WriteError("""
            Usage: accounts <command>

              add [--name N] [--url U] [--read-write] [--ca FILE] [--max-rows N] [--project ID]... [--api-key-env VAR] [--allow-insecure-http] [--yes]
                  Add or replace a target. The API key is asked for interactively (hidden input).
                  With --api-key-env no key is stored: the server reads it from that environment variable.
              list                 Show targets and where each key comes from (never the key itself)
              remove <name> [--yes]  Delete a target and its stored key
              test [name]          Call each target's API with its stored key
            """);
        return 2;
    }

    private static async Task<int> AddAsync(string[] args, CliContext context, CancellationToken cancellationToken)
    {
        var options = new ArgParser(args, AddValueOptions, AddFlags);
        var terminal = context.Terminal;
        var assumeYes = options.Has("yes");

        var name = options.Get("name") ?? terminal.ReadLine("Target name (letters, digits, - _): ");
        var url = options.Get("url") ?? terminal.ReadLine("Redmine URL (https://...): ");

        var readOnly = !options.Has("read-write");
        if (readOnly && options.Get("name") is null && terminal.IsInteractive)
            readOnly = !terminal.Confirm("Allow write tools (create issues, add notes, log time) on this target?", defaultYes: false);

        int? maxRows = null;
        if (options.Get("max-rows") is { } rawRows)
            maxRows = int.TryParse(rawRows, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : throw new UserFacingException("--max-rows must be a number.");

        var target = new TargetConfig
        {
            Name = name,
            Url = url,
            ReadOnly = readOnly,
            CaPath = options.Get("ca") is { } ca ? Path.GetFullPath(ca) : null,
            MaxRows = maxRows,
            ProjectAllowlist = options.GetAll("project") is { Count: > 0 } projects ? projects.ToList() : null,
            ApiKeyEnv = options.Get("api-key-env"),
            AllowInsecureHttp = options.Has("allow-insecure-http"),
        };

        var config = context.LoadConfig();
        var candidate = new RedmineMcpConfig { Targets = [.. config.Targets.Where(t => !t.Name.Equals(name, StringComparison.OrdinalIgnoreCase)), target] };
        var problems = ConfigValidator.Validate(candidate);
        if (problems.Count > 0)
            throw new UserFacingException("Cannot save this target:\n  - " + string.Join("\n  - ", problems));

        var existing = config.Targets.Any(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing && !assumeYes && !terminal.Confirm($"Target '{name}' already exists. Replace it?", defaultYes: false))
        {
            terminal.WriteLine("Nothing changed.");
            return 1;
        }

        ApiKey? key = null;
        var keys = context.Keys;
        if (target.ApiKeyEnv is null)
        {
            var typed = terminal.ReadSecret("API key (input hidden): ");
            if (typed.Length == 0)
                throw new UserFacingException("No API key entered.");
            key = new ApiKey(typed);
        }
        else if (string.IsNullOrWhiteSpace(context.GetEnvironmentVariable(target.ApiKeyEnv)))
        {
            terminal.WriteLine($"Note: environment variable {target.ApiKeyEnv} is not set in this shell; it must be set for the MCP client that starts the server.");
        }

        // Test with the key held in memory only, before anything is written anywhere.
        var tester = key is null
            ? keys
            : new ApiKeyResolver(systemStore: null, new InMemoryFile(key), context.GetEnvironmentVariable);
        if (key is not null || !string.IsNullOrWhiteSpace(context.GetEnvironmentVariable(target.ApiKeyEnv!)))
        {
            try
            {
                var user = await context.Tester.TestAsync(target, tester, cancellationToken);
                terminal.WriteLine($"Connected to '{name}' as {user.Login} ({user.DisplayName}).");
            }
            catch (UserFacingException ex)
            {
                terminal.WriteError($"Connection test failed: {ex.Message}");
                if (!assumeYes && !terminal.Confirm("Save the target anyway?", defaultYes: false))
                    return 1;
            }
        }

        if (key is not null)
            terminal.WriteLine(StoreKey(context, name, key));

        ConfigStore.Save(context.ConfigPath, candidate);
        terminal.WriteLine($"Saved target '{name}' ({(readOnly ? "read-only" : "read-write")}) in {context.ConfigPath}");
        return 0;
    }

    /// <summary>Prefers the OS credential store; falls back to a mode-600 file and says so.</summary>
    private static string StoreKey(CliContext context, string name, ApiKey key)
    {
        if (context.SystemStore is { } system)
        {
            try
            {
                system.Set(name, key);
                // A leftover plaintext copy from an earlier fallback would be both stale and unnecessary.
                context.FileStore.Remove(name);
                return $"API key stored in {system.Description}.";
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or TimeoutException or System.ComponentModel.Win32Exception)
            {
                context.Terminal.WriteError($"Could not use {system.Description} ({ex.GetType().Name}); falling back to a key file.");
            }
        }

        context.FileStore.Set(name, key);
        return $"WARNING: no OS credential store available - API key stored in a plain file ({context.FileStore.Description}). 'doctor' will keep reminding you.";
    }

    private static int List(CliContext context)
    {
        var config = context.LoadConfig();
        var terminal = context.Terminal;
        if (config.Targets.Count == 0)
        {
            terminal.WriteLine($"No targets configured ({context.ConfigPath}). Add one with: accounts add");
            return 0;
        }

        var keys = context.Keys;
        foreach (var target in config.Targets)
        {
            var resolved = keys.Resolve(target.Name, target.ApiKeyEnv);
            var keyInfo = resolved is null
                ? "key: MISSING" + (target.ApiKeyEnv is null ? string.Empty : $" (env {target.ApiKeyEnv} not set)")
                : $"key: set via {resolved.Source}{(resolved.Plaintext ? " [plaintext file]" : string.Empty)}";

            terminal.WriteLine(
                $"{target.Name}  {target.Url}  {(target.ReadOnly ? "read-only" : "read-write")}  {keyInfo}"
                + (target.HasAllowlist ? $"  projects: {string.Join(",", target.ProjectAllowlist!)}" : string.Empty));
        }

        return 0;
    }

    private static int Remove(string[] args, CliContext context)
    {
        var options = new ArgParser(args, [], ["yes"]);
        var terminal = context.Terminal;
        var name = options.Positionals.Count > 0 ? options.Positionals[0] : throw new UserFacingException("Usage: accounts remove <name> [--yes]");

        var config = context.LoadConfig();
        var target = config.Targets.FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? throw new UserFacingException($"Unknown target '{name}'. Known: {string.Join(", ", config.Targets.Select(t => t.Name))}.");

        if (!options.Has("yes") && !terminal.Confirm($"Delete target '{target.Name}' and its stored API key?", defaultYes: false))
        {
            terminal.WriteLine("Nothing changed.");
            return 1;
        }

        ConfigStore.Save(context.ConfigPath, new RedmineMcpConfig { Targets = config.Targets.Where(t => t != target).ToList() });

        foreach (var store in new[] { context.SystemStore, context.FileStore })
        {
            try
            {
                store?.Remove(target.Name);
            }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
            {
                terminal.WriteError($"Could not clear the key from {store!.Description} ({ex.GetType().Name}); remove it manually.");
            }
        }

        terminal.WriteLine($"Removed target '{target.Name}'.");
        return 0;
    }

    private static async Task<int> TestAsync(string[] args, CliContext context, CancellationToken cancellationToken)
    {
        var options = new ArgParser(args, [], []);
        var config = context.LoadConfig();
        var terminal = context.Terminal;

        var selected = options.Positionals.Count == 0
            ? config.Targets
            : [config.Targets.FirstOrDefault(t => t.Name.Equals(options.Positionals[0], StringComparison.OrdinalIgnoreCase))
               ?? throw new UserFacingException($"Unknown target '{options.Positionals[0]}'.")];

        if (selected.Count == 0)
        {
            terminal.WriteLine("No targets configured. Add one with: accounts add");
            return 1;
        }

        var failures = 0;
        foreach (var target in selected)
        {
            try
            {
                var user = await context.Tester.TestAsync(target, context.Keys, cancellationToken);
                terminal.WriteLine($"{target.Name}: OK - {user.Login} ({user.DisplayName})");
            }
            catch (UserFacingException ex)
            {
                failures++;
                terminal.WriteError($"{target.Name}: FAILED - {ex.Message}");
            }
        }

        return failures == 0 ? 0 : 1;
    }

    /// <summary>A throw-away "file store" that serves one in-memory key, used only for the connection test.</summary>
    private sealed class InMemoryFile(ApiKey key) : ISecretStore
    {
        public string Description => "typed key (memory)";

        public bool StoresPlaintext => false;

        public ApiKey? Get(string account) => key;

        public void Set(string account, ApiKey value) => throw new NotSupportedException();

        public bool Remove(string account) => false;
    }
}
