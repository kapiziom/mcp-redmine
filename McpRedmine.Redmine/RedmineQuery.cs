using System.Globalization;

namespace McpRedmine.Redmine;

/// <summary>
/// Builds a query string with proper encoding. Refuses key-like parameter names: the API key goes
/// only in the <c>X-Redmine-API-Key</c> header, never in the URL.
/// </summary>
public sealed class RedmineQuery
{
    private static readonly string[] ForbiddenNames = ["key", "api_key", "apikey", "token"];

    private readonly List<KeyValuePair<string, string>> _parameters = [];

    public RedmineQuery Add(string name, string? value)
    {
        if (ForbiddenNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException($"Query parameter '{name}' is not allowed; credentials travel only in headers.", nameof(name));

        if (!string.IsNullOrWhiteSpace(value))
            _parameters.Add(new KeyValuePair<string, string>(name, value));

        return this;
    }

    public RedmineQuery Add(string name, int? value) =>
        Add(name, value?.ToString(CultureInfo.InvariantCulture));

    /// <summary>Returns a copy with <paramref name="name"/> set (replacing any earlier value).</summary>
    public RedmineQuery With(string name, int value)
    {
        var copy = new RedmineQuery();
        copy._parameters.AddRange(_parameters.Where(p => p.Key != name));
        return copy.Add(name, value);
    }

    public override string ToString() =>
        _parameters.Count == 0
            ? string.Empty
            : "?" + string.Join('&', _parameters.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));
}
