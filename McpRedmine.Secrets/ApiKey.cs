namespace McpRedmine.Secrets;

/// <summary>
/// A Redmine API key. Deliberately not a record: <see cref="ToString"/> must never reveal the value,
/// so accidental interpolation into a log line or exception message is harmless.
/// </summary>
public sealed class ApiKey
{
    private readonly string _value;

    public ApiKey(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        _value = value.Trim();
    }

    /// <summary>The only way to read the key. Call it at the last moment, when setting the request header.</summary>
    public string Reveal() => _value;

    public override string ToString() => "********";
}
