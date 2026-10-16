using McpRedmine.Clients;
using McpRedmine.Config;

namespace McpRedmine.Cli;

/// <summary>
/// Registers this server in the user's MCP clients, pinned to one package version. Shows the plan first,
/// supports <c>--dry-run</c>, and is idempotent. No API key is ever written to a client config.
/// </summary>
public static class SetupCommand
{
    public static int Run(string[] args, CliContext context)
    {
        var options = new ArgParser(args, ["client", "version"], ["dry-run", "yes"]);
        var terminal = context.Terminal;

        var version = options.Get("version") ?? PackageInfo.Version;
        var registration = ServerRegistration.ForDnx(PackageInfo.PackageId, version, OperatingSystem.IsWindows());

        var requested = options.GetAll("client").Select(c => c.ToLowerInvariant()).ToList();
        var known = context.Installers.Select(i => i.Id).ToList();
        if (requested.Contains("all"))
            requested.Clear();

        var unknown = requested.Where(r => !known.Contains(r)).ToList();
        if (unknown.Count > 0)
            throw new UserFacingException($"Unknown client '{unknown[0]}'. Use one of: {string.Join(", ", known)}, all.");

        var selected = requested.Count == 0 ? context.Installers : context.Installers.Where(i => requested.Contains(i.Id)).ToList();

        var detected = new List<(IMcpClientInstaller Installer, InstallPlan Plan)>();
        var failed = false;
        foreach (var installer in selected)
        {
            if (installer.NotDetectedReason() is { } reason)
            {
                terminal.WriteLine($"  {installer.DisplayName}: skipped ({reason})");
                failed |= requested.Count > 0; // asked for it explicitly, so a miss is a failure
                continue;
            }

            detected.Add((installer, installer.Plan(registration)));
        }

        if (detected.Count == 0)
        {
            terminal.WriteError("No supported MCP client found. Register the server by hand with:");
            terminal.WriteError($"  {registration.DisplayCommand}");
            return 1;
        }

        terminal.WriteLine($"Plan (server '{registration.Name}' pinned to {PackageInfo.PackageId} {version}):");
        foreach (var (installer, plan) in detected)
        {
            terminal.WriteLine($"  {installer.DisplayName}: {plan.Action.ToString().ToLowerInvariant()}");
            foreach (var step in plan.Steps)
                terminal.WriteLine($"    - {step}");
        }

        if (options.Has("dry-run"))
        {
            terminal.WriteLine("Dry run: nothing was changed.");
            return failed ? 1 : 0;
        }

        var pending = detected.Where(d => d.Plan.Action != PlanAction.NoChange).ToList();
        if (pending.Count == 0)
        {
            terminal.WriteLine("Everything is already up to date.");
            return failed ? 1 : 0;
        }

        if (!options.Has("yes") && !terminal.Confirm("Apply these changes?", defaultYes: true))
        {
            terminal.WriteLine("Nothing changed.");
            return 1;
        }

        foreach (var (installer, _) in pending)
        {
            try
            {
                installer.Apply(registration);
                terminal.WriteLine($"  {installer.DisplayName}: done");
            }
            catch (UserFacingException ex)
            {
                failed = true;
                terminal.WriteError($"  {installer.DisplayName}: FAILED - {ex.Message}");
            }
        }

        terminal.WriteLine();
        terminal.WriteLine("Next: add a Redmine target and its API key (asked for with hidden input):");
        terminal.WriteLine($"  {PackageInfo.RunHint("accounts add")}");
        terminal.WriteLine("Then restart your MCP client.");
        return failed ? 1 : 0;
    }
}
