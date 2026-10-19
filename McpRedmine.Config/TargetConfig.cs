namespace McpRedmine.Config;

/// <summary>One Redmine instance. Contains only non-sensitive data - the API key lives in <c>McpRedmine.Secrets</c>.</summary>
public sealed record TargetConfig
{
    public required string Name { get; init; }

    public required string Url { get; init; }

    /// <summary>Write tools are rejected server-side unless this is explicitly <c>false</c>.</summary>
    public bool ReadOnly { get; init; } = true;

    /// <summary>Optional PEM file with the CA (or bundle) used to validate this target's certificate.</summary>
    public string? CaPath { get; init; }

    public int? MaxRows { get; init; }

    public IReadOnlyList<string>? ProjectAllowlist { get; init; }

    /// <summary>Name of an environment variable holding the API key (fallback when the OS store has none).</summary>
    public string? ApiKeyEnv { get; init; }

    /// <summary>Permits plain <c>http://</c>. Off by default; the API key would travel unencrypted.</summary>
    public bool AllowInsecureHttp { get; init; }

    /// <summary>Show people as initials plus id (e.g. "J.K. (#12)") instead of names in tool results. On by default.</summary>
    public bool RedactPeople { get; init; } = true;

    /// <summary>
    /// Mask e-mail addresses, keys/tokens/passwords and checksum-valid PESEL/NIP/IBAN/card numbers in free text
    /// (descriptions, comments, subjects, attachment text) before it reaches the model. On by default.
    /// </summary>
    public bool RedactText { get; init; } = true;

    /// <summary>E-mail domains (and their subdomains) that stay visible, e.g. your company or test domain.</summary>
    public IReadOnlyList<string>? KeepEmailDomains { get; init; }

    /// <summary>
    /// Treat Redmine text as untrusted: strip hidden characters, flag known prompt-injection phrases and wrap long texts
    /// in delimiters. Does not affect what is sent to Redmine. On by default.
    /// </summary>
    public bool InjectionGuard { get; init; } = true;

    public int EffectiveMaxRows => RowLimits.Clamp(MaxRows ?? RowLimits.DefaultMaxRows);

    public bool HasAllowlist => ProjectAllowlist is { Count: > 0 };
}

public sealed record RedmineMcpConfig
{
    public IReadOnlyList<TargetConfig> Targets { get; init; } = [];
}

public static class RowLimits
{
    public const int DefaultMaxRows = 50;

    /// <summary>Server-side ceiling regardless of what a target or a tool call asks for.</summary>
    public const int HardMaxRows = 200;

    public static int Clamp(int value) => Math.Clamp(value, 1, HardMaxRows);
}
