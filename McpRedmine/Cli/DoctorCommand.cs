using System.Diagnostics;
using McpRedmine.Clients;
using McpRedmine.Config;
using McpRedmine.Redmine;

namespace McpRedmine.Cli;

/// <summary>Checks the installation end to end and says what to fix. Never prints a key.</summary>
public static class DoctorCommand
{
    private enum Level
    {
        Ok,
        Warn,
        Fail,
    }

    public static async Task<int> RunAsync(CliContext context, CancellationToken cancellationToken = default)
    {
        var terminal = context.Terminal;
        var worst = Level.Ok;

        void Report(Level level, string text)
        {
            worst = (Level)Math.Max((int)worst, (int)level);
            terminal.WriteLine($"[{level.ToString().ToUpperInvariant(),-4}] {text}");
        }

        terminal.WriteLine($"{PackageInfo.PackageId} {PackageInfo.Version} on {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");

        CheckDotnet(context, Report);
        CheckClaude(context, Report);

        RedmineMcpConfig config;
        try
        {
            config = context.LoadConfig();
        }
        catch (UserFacingException ex)
        {
            Report(Level.Fail, ex.Message);
            return 1;
        }

        if (!File.Exists(context.ConfigPath))
        {
            Report(Level.Warn, $"No config yet ({context.ConfigPath}). Add a target with: {PackageInfo.RunHint("accounts add")}");
            return worst == Level.Fail ? 1 : 0;
        }

        var problems = ConfigValidator.Validate(config);
        foreach (var problem in problems)
            Report(Level.Fail, problem);

        if (config.Targets.Count == 0)
            Report(Level.Warn, "Config has no targets.");
        else if (problems.Count == 0)
            Report(Level.Ok, $"Config {context.ConfigPath}: {config.Targets.Count} target(s)");

        foreach (var target in config.Targets)
        {
            terminal.WriteLine();
            terminal.WriteLine($"Target '{target.Name}' ({target.Url}, {(target.ReadOnly ? "read-only" : "read-write")})");
            await CheckTargetAsync(context, target, Report, cancellationToken);
        }

        terminal.WriteLine();
        terminal.WriteLine(worst switch
        {
            Level.Ok => "All checks passed.",
            Level.Warn => "Passed with warnings.",
            _ => "Some checks failed.",
        });
        return worst == Level.Fail ? 1 : 0;
    }

    private static void CheckDotnet(CliContext context, Action<Level, string> report)
    {
        try
        {
            var result = context.Processes.Run("dotnet", ["--list-sdks"], null, TimeSpan.FromSeconds(30));
            var majors = result.StandardOutput
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(line => line.Split(' ')[0])
                .Where(v => Version.TryParse(v.Split('-')[0], out _))
                .Select(v => Version.Parse(v.Split('-')[0]).Major)
                .ToList();

            if (majors.Contains(10) || majors.Any(m => m > 10))
                report(Level.Ok, $".NET SDK {majors.Max()}.x found (dnx needs the .NET 10 SDK)");
            else
                report(Level.Warn, "No .NET 10 SDK found. MCP clients start this server through 'dnx', which needs the .NET 10 SDK: https://dotnet.microsoft.com/download");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or TimeoutException or InvalidOperationException)
        {
            report(Level.Warn, "'dotnet' is not on PATH. MCP clients start this server through 'dnx', which needs the .NET 10 SDK.");
        }
    }

    private static void CheckClaude(CliContext context, Action<Level, string> report)
    {
        var claude = new ClaudeCli(context.Processes);
        if (claude.Locate() is { } path)
            report(Level.Ok, $"'claude' CLI found ({path})");
        else
            report(Level.Warn, "'claude' CLI not found on PATH. 'setup' cannot register Claude Code (Claude Desktop and Cursor are unaffected).");
    }

    private static async Task CheckTargetAsync(CliContext context, TargetConfig target, Action<Level, string> report, CancellationToken cancellationToken)
    {
        var keys = context.Keys;
        var resolved = keys.Resolve(target.Name, target.ApiKeyEnv);
        if (resolved is null)
        {
            report(Level.Fail, "No API key. " + (target.ApiKeyEnv is null
                ? $"Store one with: {PackageInfo.RunHint("accounts add")}"
                : $"Set the {target.ApiKeyEnv} environment variable for the MCP client, or store a key with 'accounts add'."));
            return;
        }

        report(Level.Ok, $"API key found ({resolved.Source})");
        if (resolved.Plaintext)
            report(Level.Warn, "The key is kept in a plain file. Use an OS credential store (Credential Manager, Keychain, libsecret) where possible.");
        if (context.FileStore.HasLoosePermissions(target.Name))
            report(Level.Warn, $"The key file for '{target.Name}' is readable by other users; run: chmod 600 on it.");

        if (target.CaPath is not null)
        {
            try
            {
                var cas = CustomCaValidator.LoadCas(target.CaPath);
                report(Level.Ok, $"Custom CA loaded ({cas.Count} certificate(s))");
            }
            catch (UserFacingException ex)
            {
                report(Level.Fail, ex.Message);
                return;
            }
        }

        var clock = Stopwatch.StartNew();
        try
        {
            var user = await context.Tester.TestAsync(target, keys, cancellationToken);
            report(Level.Ok, $"REST API reachable and key accepted: {user.Login} ({user.DisplayName}) in {clock.ElapsedMilliseconds} ms");
            if (user.Login is null)
                report(Level.Warn, "Redmine returned no login for this key.");
        }
        catch (RedmineException ex)
        {
            report(Level.Fail, ex.Kind switch
            {
                RedmineErrorKind.Unauthorized or RedmineErrorKind.Forbidden =>
                    ex.Message + " Check the key, and that the REST API is enabled: Administration -> Settings -> API -> \"Enable REST web service\".",
                RedmineErrorKind.InvalidResponse =>
                    ex.Message,
                _ => ex.Message,
            });
        }
    }
}
