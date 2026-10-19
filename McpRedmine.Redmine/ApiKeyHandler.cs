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

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri;
        if (uri is null || !uri.IsAbsoluteUri || !IsSameOrigin(uri))
            throw new RedmineException(RedmineErrorKind.Other,
                $"Refusing to send credentials outside target '{target.Name}'.");

        var resolved = resolver.Resolve(target.Name, target.ApiKeyEnv)
            ?? throw new RedmineException(RedmineErrorKind.MissingApiKey,
                $"No API key is available for target '{target.Name}'. Run 'accounts add' to store one"
                + (target.ApiKeyEnv is null ? "." : $" or set the {target.ApiKeyEnv} environment variable."));

        var secret = resolved.Key.Reveal();

        // Last line of defence against an agent being tricked into posting the key into a ticket.
        var leaksInUrl = uri.ToString().Contains(secret, StringComparison.Ordinal);
        var leaksInBody = false;
        if (request.Content is not null)
        {
            await request.Content.LoadIntoBufferAsync(cancellationToken);
            leaksInBody = (await request.Content.ReadAsStringAsync(cancellationToken)).Contains(secret, StringComparison.Ordinal);
        }

        if (leaksInUrl || leaksInBody)
            throw new RedmineException(RedmineErrorKind.Other,
                $"Refused: the request to target '{target.Name}' contains the API key. Nothing was sent. Do not put credentials into issues, notes or comments.");

        request.Headers.Remove(HeaderName);
        request.Headers.TryAddWithoutValidation(HeaderName, secret);

        return await base.SendAsync(request, cancellationToken);
    }

    private bool IsSameOrigin(Uri uri) =>
        uri.Scheme == _origin.Scheme
        && string.Equals(uri.Host, _origin.Host, StringComparison.OrdinalIgnoreCase)
        && uri.Port == _origin.Port;
}
