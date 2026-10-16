using System.Text.RegularExpressions;

namespace McpRedmine.Config;

public static partial class ConfigValidator
{
    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex NamePattern();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex EnvVarPattern();

    /// <param name="config">Config to check.</param>
    /// <param name="fileExists">Injectable for tests; defaults to <see cref="File.Exists(string?)"/>.</param>
    public static IReadOnlyList<string> Validate(RedmineMcpConfig config, Func<string, bool>? fileExists = null)
    {
        fileExists ??= File.Exists;
        var errors = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var target in config.Targets)
        {
            var label = string.IsNullOrWhiteSpace(target.Name) ? "<unnamed>" : target.Name;

            if (!NamePattern().IsMatch(target.Name ?? string.Empty))
                errors.Add($"Target '{label}': name must be 1-64 characters: letters, digits, '-' or '_'.");
            else if (!seen.Add(target.Name!))
                errors.Add($"Target '{label}': duplicate name (names are case-insensitive).");

            ValidateUrl(target, label, errors);

            if (target.CaPath is not null && !fileExists(target.CaPath))
                errors.Add($"Target '{label}': ca_path '{target.CaPath}' does not exist.");

            if (target.MaxRows is { } maxRows && (maxRows < 1 || maxRows > RowLimits.HardMaxRows))
                errors.Add($"Target '{label}': max_rows must be between 1 and {RowLimits.HardMaxRows}.");

            if (target.ProjectAllowlist is { } allowlist && allowlist.Any(string.IsNullOrWhiteSpace))
                errors.Add($"Target '{label}': project_allowlist must not contain empty entries.");

            if (target.ApiKeyEnv is not null && !EnvVarPattern().IsMatch(target.ApiKeyEnv))
                errors.Add($"Target '{label}': api_key_env '{target.ApiKeyEnv}' is not a valid environment variable name.");
        }

        return errors;
    }

    private static void ValidateUrl(TargetConfig target, string label, List<string> errors)
    {
        if (!Uri.TryCreate(target.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
        {
            errors.Add($"Target '{label}': url must be an absolute http(s) URL.");
            return;
        }

        if (uri.Scheme == "http" && !target.AllowInsecureHttp)
            errors.Add($"Target '{label}': url uses http://. Use https:// or set allow_insecure_http (the API key would be sent unencrypted).");

        if (!string.IsNullOrEmpty(uri.UserInfo))
            errors.Add($"Target '{label}': url must not contain credentials.");

        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            errors.Add($"Target '{label}': url must not contain a query string or fragment.");
    }
}
