using System.Text;
using McpRedmine.Config;

namespace McpRedmine.Cli;

/// <summary>Everything the interactive commands need from a console, so tests can script a session.</summary>
public interface ITerminal
{
    bool IsInteractive { get; }

    void WriteLine(string text = "");

    void WriteError(string text);

    /// <summary>Returns the entered text (trimmed), or an empty string.</summary>
    string ReadLine(string prompt);

    /// <summary>Reads a secret without echoing it.</summary>
    string ReadSecret(string prompt);

    bool Confirm(string question, bool defaultYes);
}

public sealed class ConsoleTerminal : ITerminal
{
    public bool IsInteractive => !Console.IsInputRedirected;

    public void WriteLine(string text = "") => Console.Out.WriteLine(text);

    public void WriteError(string text) => Console.Error.WriteLine(text);

    public string ReadLine(string prompt)
    {
        EnsureInteractive();
        Console.Out.Write(prompt);
        return Console.In.ReadLine()?.Trim() ?? string.Empty;
    }

    public string ReadSecret(string prompt)
    {
        EnsureInteractive();
        Console.Out.Write(prompt);

        var buffer = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
                break;

            if (key.Key == ConsoleKey.Backspace)
            {
                if (buffer.Length > 0)
                    buffer.Length--;
            }
            else if (!char.IsControl(key.KeyChar))
            {
                buffer.Append(key.KeyChar);
            }
        }

        Console.Out.WriteLine();
        return buffer.ToString().Trim();
    }

    public bool Confirm(string question, bool defaultYes)
    {
        var answer = ReadLine($"{question} [{(defaultYes ? "Y/n" : "y/N")}] ").ToLowerInvariant();
        return answer.Length == 0 ? defaultYes : answer is "y" or "yes" or "t" or "tak";
    }

    private void EnsureInteractive()
    {
        if (!IsInteractive)
            throw new UserFacingException("This needs an interactive terminal (input is redirected). Run it directly in a terminal window.");
    }
}
