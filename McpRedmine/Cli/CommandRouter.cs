using McpRedmine.Config;

namespace McpRedmine.Cli;

/// <summary>
/// One binary, several subcommands. <c>serve</c> (the MCP stdio server) is the default so that
/// <c>dnx Pkg --yes</c> with no arguments starts the server.
/// </summary>
public static class CommandRouter
{
    public static async Task<int> RunAsync(string[] args)
    {
        var (command, rest) = Split(args);

        try
        {
            return command switch
            {
                "serve" => await ServeCommand.RunAsync(rest),
                "accounts" => await AccountsCommand.RunAsync(rest, CliContext.CreateDefault()),
                "setup" => SetupCommand.Run(rest, CliContext.CreateDefault()),
                "doctor" => await DoctorCommand.RunAsync(CliContext.CreateDefault()),
                "help" or "--help" or "-h" => PrintUsage(0),
                _ => PrintUsage(2, $"Unknown command '{command}'."),
            };
        }
        catch (UserFacingException ex)
        {
            await Console.Error.WriteLineAsync(ex.Message);
            return 1;
        }
    }

    internal static (string Command, string[] Remaining) Split(string[] args)
    {
        // No arguments, or only host options (e.g. --verbosity): behave as `serve`.
        if (args.Length == 0 || args[0].StartsWith('-') && args[0] is not ("--help" or "-h"))
            return ("serve", args);

        return (args[0].ToLowerInvariant(), args[1..]);
    }

    private static int PrintUsage(int exitCode, string? problem = null)
    {
        // Usage goes to stderr: in `serve` mode stdout belongs to the MCP protocol, and a CLI that
        // is mistakenly launched by an MCP client must not corrupt it.
        if (problem is not null)
            Console.Error.WriteLine(problem);

        Console.Error.WriteLine("""
            Usage: redmine-mcp [command] [options]

              serve                          Run the MCP server over stdio (default)
              setup                          Register the server in MCP clients
              doctor                         Diagnose the installation and targets
              accounts add|list|remove|test  Manage Redmine targets and API keys
            """);
        return exitCode;
    }
}
