using McpRedmine.Config;
using McpRedmine.Secrets;

namespace McpRedmine.Redmine;

/// <summary>
/// The only place that reads the API key and puts it on a request. It also refuses to send the key
/// anywhere except the target's own origin (guards against e.g. an attachment URL pointing at another host).
/// </summary>
public sealed class ApiKeyHandler(TargetConfig target, ApiKeyResolver resolver) : DelegatingHandler
{
    public const string HeaderName = "X-Redmine-API-Key";

    private readonly Uri _origin = new(target.Url);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri;
        if (uri is null || !uri.IsAbsoluteUri || !IsSameOrigin(uri))
            throw new RedmineException(RedmineErrorKind.Other,
                $"Refusing to send credentials outside target '{target.Name}'.");

        var resolved = resolver.Resolve(target.Name, target.ApiKeyEnv)
            ?? throw new RedmineException(RedmineErrorKind.MissingApiKey,
                $"No API key is available for target '{target.Name}'. Run 'accounts add' to store one"
                + (target.ApiKeyEnv is null ? "." : $" or set the {target.ApiKeyEnv} environment variable."));

        request.Headers.Remove(HeaderName);
        request.Headers.TryAddWithoutValidation(HeaderName, resolved.Key.Reveal());

        return base.SendAsync(request, cancellationToken);
    }

    private bool IsSameOrigin(Uri uri) =>
        uri.Scheme == _origin.Scheme
        && string.Equals(uri.Host, _origin.Host, StringComparison.OrdinalIgnoreCase)
        && uri.Port == _origin.Port;
}
