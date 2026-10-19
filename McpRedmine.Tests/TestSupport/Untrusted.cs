using System.Text.RegularExpressions;

namespace McpRedmine.Tests.TestSupport;

public static partial class Untrusted
{
    [GeneratedRegex(@"^\[\[UNTRUSTED-(?<n>[0-9a-f]{8}) [^\]]*\]\]\n(?<text>[\s\S]*)\n\[\[/UNTRUSTED-\k<n>\]\]$")]
    private static partial Regex Wrapper();

    public static bool IsWrapped(string? text) => text is not null && Wrapper().IsMatch(text);

    /// <summary>The text inside the delimiters; fails the test if there are none.</summary>
    public static string Unwrap(string? text)
    {
        var match = Wrapper().Match(text ?? string.Empty);
        Assert.True(match.Success, "expected text wrapped in UNTRUSTED markers, got: " + text);
        return match.Groups["text"].Value;
    }
}
