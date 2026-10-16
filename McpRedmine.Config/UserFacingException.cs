namespace McpRedmine.Config;

/// <summary>
/// An error whose message is safe and useful to show to the model/user as-is
/// (no secrets, no raw HTTP bodies). Anything that is not this type is treated as an internal error.
/// </summary>
public class UserFacingException : Exception
{
    public UserFacingException(string message) : base(message)
    {
    }

    public UserFacingException(string message, Exception inner) : base(message, inner)
    {
    }
}
