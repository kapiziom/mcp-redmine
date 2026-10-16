using System.Net;
using System.Text;

namespace McpRedmine.Tests.TestSupport;

/// <summary>Hand-rolled HTTP stub: records requests (and their bodies), answers from a delegate. No real network.</summary>
public sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];

    /// <summary>Request bodies, captured while the content is still alive (index matches <see cref="Requests"/>).</summary>
    public List<string?> Bodies { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        Bodies.Add(request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken));
        return respond(request);
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    public static StubHandler Always(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(_ => Json(json, status));
}
