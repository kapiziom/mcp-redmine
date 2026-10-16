using McpRedmine.Config;

namespace McpRedmine.Cli;

/// <summary>Minimal option parser: <c>--name value</c> options, <c>--flag</c> switches, and positional words.</summary>
public sealed class ArgParser
{
    private readonly Dictionary<string, List<string>> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _flags = new(StringComparer.OrdinalIgnoreCase);

    public ArgParser(IEnumerable<string> args, IReadOnlyCollection<string> valueOptions, IReadOnlyCollection<string> flagOptions)
    {
        var queue = new Queue<string>(args);
        var positionals = new List<string>();

        while (queue.Count > 0)
        {
            var arg = queue.Dequeue();
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                positionals.Add(arg);
                continue;
            }

            var name = arg[2..];
            if (flagOptions.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                _flags.Add(name);
            }
            else if (valueOptions.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                if (queue.Count == 0 || queue.Peek().StartsWith("--", StringComparison.Ordinal))
                    throw new UserFacingException($"Option --{name} needs a value.");

                if (!_values.TryGetValue(name, out var list))
                    _values[name] = list = [];
                list.Add(queue.Dequeue());
            }
            else
            {
                throw new UserFacingException($"Unknown option --{name}.");
            }
        }

        Positionals = positionals;
    }

    public IReadOnlyList<string> Positionals { get; }

    public bool Has(string flag) => _flags.Contains(flag);

    public string? Get(string name) => _values.TryGetValue(name, out var list) ? list[^1] : null;

    public IReadOnlyList<string> GetAll(string name) => _values.TryGetValue(name, out var list) ? list : [];
}
