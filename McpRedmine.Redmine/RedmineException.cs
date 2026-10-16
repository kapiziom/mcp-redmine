using McpRedmine.Config;

namespace McpRedmine.Redmine;

public enum RedmineErrorKind
{
    MissingApiKey,
    Unauthorized,
    Forbidden,
    NotFound,
    Validation,
    Redirected,
    Unavailable,
    Timeout,
    Tls,
    InvalidResponse,
    NotSupported,
    TooLarge,
    Other,
}

/// <summary>
/// A Redmine failure described for the model: what happened and what to do next.
/// Never carries raw HTTP bodies, headers or the API key.
/// </summary>
public sealed class RedmineException(RedmineErrorKind kind, string message, Exception? inner = null)
    : UserFacingException(message, inner!)
{
    public RedmineErrorKind Kind { get; } = kind;
}
