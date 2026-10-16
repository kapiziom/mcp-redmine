using McpRedmine.Config;
using McpRedmine.Redmine;

namespace McpRedmine.Tests.TestSupport;

public static class Targets
{
    public static TargetConfig Make(string name = "main", string url = "https://redmine.example.com/", bool readOnly = true, int? maxRows = null) =>
        new() { Name = name, Url = url, ReadOnly = readOnly, MaxRows = maxRows };

    /// <summary>Client over a stub; the API-key handler is intentionally absent (tested separately).</summary>
    public static RedmineClient Client(StubHandler handler, TargetConfig? target = null)
    {
        target ??= Make();
        var http = new HttpClient(handler) { BaseAddress = new Uri(target.Url.TrimEnd('/') + "/") };
        return new RedmineClient(http, target);
    }
}
