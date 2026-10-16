using System.Net;
using System.Net.Http.Json;
using System.Security.Authentication;
using System.Text.Json;
using McpRedmine.Config;
using Microsoft.Extensions.Logging;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace McpRedmine.Redmine;

/// <summary>
/// Thin wrapper over one target's REST API. Uses only stable, core endpoints and relative paths
/// (no leading slash) so Redmine installed under a sub-URI such as <c>/redmine/</c> keeps working.
/// Every failure surfaces as a <see cref="RedmineException"/> that is safe to show to the model.
/// </summary>
public sealed partial class RedmineClient(HttpClient http, TargetConfig target, ILogger<RedmineClient>? logger = null)
{
    /// <summary>Redmine caps <c>limit</c> at 100 per request.</summary>
    public const int MaxPageSize = 100;

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public TargetConfig Target => target;

    public async Task<RedmineUser> GetCurrentUserAsync(CancellationToken cancellationToken = default)
    {
        var root = await GetJsonAsync("users/current.json", null, cancellationToken);
        if (!root.TryGetProperty("user", out var user))
            throw new RedmineException(RedmineErrorKind.InvalidResponse,
                $"Target '{target.Name}' answered, but not with a Redmine user document. Check that the URL points at Redmine.");

        return user.Deserialize<RedmineUser>(Json)!;
    }

    public async Task<T> GetAsync<T>(string path, string property, RedmineQuery? query = null, CancellationToken cancellationToken = default)
    {
        var root = await GetJsonAsync(path, query, cancellationToken);
        if (!root.TryGetProperty(property, out var element))
            throw new RedmineException(RedmineErrorKind.InvalidResponse,
                $"Unexpected response from target '{target.Name}': missing '{property}'.");

        return element.Deserialize<T>(Json)!;
    }

    /// <summary>
    /// Reads up to <paramref name="limit"/> items starting at <paramref name="offset"/>, following Redmine's
    /// <c>limit</c>/<c>offset</c> paging (max 100 per request) across as many requests as needed.
    /// </summary>
    public async Task<PagedResult<T>> GetPagedAsync<T>(
        string path,
        string collection,
        RedmineQuery? query,
        int limit,
        int offset,
        CancellationToken cancellationToken = default)
    {
        query ??= new RedmineQuery();
        var items = new List<T>();
        int? total = null;
        var cursor = offset;
        var hasMore = false;

        while (items.Count < limit)
        {
            var pageSize = Math.Min(MaxPageSize, limit - items.Count);
            var root = await GetJsonAsync(path, query.With("limit", pageSize).With("offset", cursor), cancellationToken);

            if (!root.TryGetProperty(collection, out var array) || array.ValueKind != JsonValueKind.Array)
                throw new RedmineException(RedmineErrorKind.InvalidResponse,
                    $"Unexpected response from target '{target.Name}': missing '{collection}' list.");

            if (root.TryGetProperty("total_count", out var totalElement) && totalElement.TryGetInt32(out var totalValue))
                total = totalValue;

            var received = 0;
            foreach (var element in array.EnumerateArray())
            {
                items.Add(element.Deserialize<T>(Json)!);
                received++;
            }

            cursor += received;

            // Without total_count (very old Redmine) a full page is the only hint that more may follow.
            hasMore = total is { } known ? cursor < known : received == pageSize;
            if (received == 0 || !hasMore)
                break;
        }

        return new PagedResult<T>(items, total, offset, hasMore);
    }

    public async Task<JsonElement> GetJsonAsync(string path, RedmineQuery? query, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path + query);
        return await SendAsync(request, path, allowEmptyBody: false, cancellationToken);
    }

    /// <summary>
    /// POST/PUT a JSON document. Writes are never retried (see the resilience setup), and a failure that
    /// leaves the outcome unknown says so. Returns an <c>Undefined</c> element for an empty (204) answer.
    /// </summary>
    public async Task<JsonElement> SendJsonAsync(HttpMethod method, string path, object body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path)
        {
            Content = JsonContent.Create(body, options: Json),
        };
        return await SendAsync(request, path, allowEmptyBody: true, cancellationToken);
    }

    private const string OutcomeUnknown =
        " The change may or may not have been applied - check with search_issues/get_issue before retrying.";

    /// <summary>
    /// Downloads a file (e.g. an attachment) fully into memory, refusing anything larger than
    /// <paramref name="maxBytes"/> - both by the announced length and while reading.
    /// </summary>
    public async Task<byte[]> GetBytesAsync(string path, long maxBytes, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        using var response = await SendRawAsync(request, path, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (response.Content.Headers.ContentType?.MediaType is "text/html" or "application/xhtml+xml")
            throw new RedmineException(RedmineErrorKind.InvalidResponse,
                $"Target '{target.Name}' answered the download with a web page instead of the file (login required or no permission).");

        if (response.Content.Headers.ContentLength > maxBytes)
            throw TooLarge(maxBytes);

        try
        {
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await source.ReadAsync(chunk, cancellationToken)) > 0)
            {
                if (buffer.Length + read > maxBytes)
                    throw TooLarge(maxBytes);
                buffer.Write(chunk, 0, read);
            }

            return buffer.ToArray();
        }
        catch (IOException ex)
        {
            throw new RedmineException(RedmineErrorKind.Unavailable,
                $"The download from target '{target.Name}' was interrupted. Try again.", ex);
        }
    }

    private static RedmineException TooLarge(long maxBytes) =>
        new(RedmineErrorKind.TooLarge, $"The file is larger than the {maxBytes / (1024 * 1024)} MB limit and was not downloaded.");

    private async Task<JsonElement> SendAsync(HttpRequestMessage request, string path, bool allowEmptyBody, CancellationToken cancellationToken)
    {
        using var response = await SendRawAsync(request, path, HttpCompletionOption.ResponseContentRead, cancellationToken);
        return await ReadJsonAsync(response, allowEmptyBody, cancellationToken);
    }

    /// <summary>Sends the request; returns the response only for a success status, otherwise throws a mapped error.</summary>
    private async Task<HttpResponseMessage> SendRawAsync(
        HttpRequestMessage request, string path, HttpCompletionOption completion, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, completion, cancellationToken);
        }
        catch (RedmineException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutRejectedException)
        {
            throw Annotate(request, new RedmineException(RedmineErrorKind.Timeout,
                $"Target '{target.Name}' did not answer in time. It may be slow or unreachable (VPN?). Try again.", ex));
        }
        catch (BrokenCircuitException ex)
        {
            throw new RedmineException(RedmineErrorKind.Unavailable,
                $"Target '{target.Name}' failed repeatedly; requests are paused briefly. Try again in a minute.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw Annotate(request, MapTransportError(ex));
        }

        // Path only - never the query (may hold user text) and never headers.
        logger?.LogDebug("{Method} {Target} {Path} -> {Status}", request.Method, target.Name, path, (int)response.StatusCode);

        if (response.IsSuccessStatusCode)
            return response;

        try
        {
            throw Annotate(request, await MapStatusAsync(response, cancellationToken));
        }
        finally
        {
            response.Dispose();
        }
    }

    /// <summary>For writes, an ambiguous failure (timeout, 5xx, dropped connection) must not invite a blind retry.</summary>
    private static RedmineException Annotate(HttpRequestMessage request, RedmineException error) =>
        request.Method == HttpMethod.Get || error.Kind is not (RedmineErrorKind.Timeout or RedmineErrorKind.Unavailable)
            ? error
            : new RedmineException(error.Kind, error.Message + OutcomeUnknown, error.InnerException);

    private async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response, bool allowEmptyBody, CancellationToken cancellationToken)
    {
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (allowEmptyBody && (response.StatusCode == HttpStatusCode.NoContent || bytes.Length == 0))
            return default;

        if (response.Content.Headers.ContentType?.MediaType is not { } mediaType
            || !mediaType.EndsWith("json", StringComparison.OrdinalIgnoreCase))
        {
            throw new RedmineException(RedmineErrorKind.InvalidResponse,
                $"Target '{target.Name}' returned a non-JSON response. The URL may not be Redmine, "
                + "or the REST API is disabled (Administration -> Settings -> API).");
        }

        try
        {
            using var document = JsonDocument.Parse(bytes);
            return document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new RedmineException(RedmineErrorKind.InvalidResponse,
                $"Target '{target.Name}' returned malformed JSON.", ex);
        }
    }

    private async Task<RedmineException> MapStatusAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var status = response.StatusCode;
        var name = target.Name;

        if ((int)status is >= 300 and < 400)
            return new RedmineException(RedmineErrorKind.Redirected,
                $"Target '{name}' redirected the request (HTTP {(int)status}). Redirects are not followed. "
                + "Check the target URL (http vs https, missing sub-path such as /redmine) or whether it requires SSO login.");

        return status switch
        {
            HttpStatusCode.Unauthorized => new RedmineException(RedmineErrorKind.Unauthorized,
                $"Target '{name}' rejected the credentials (HTTP 401): wrong or revoked API key, or the REST API is disabled. "
                + "Run 'doctor' to check, or 'accounts add' to store a new key."),
            HttpStatusCode.Forbidden => new RedmineException(RedmineErrorKind.Forbidden,
                $"Target '{name}' denied access (HTTP 403): the account lacks permission for this resource, "
                + "or the REST API is disabled (Administration -> Settings -> API)."),
            HttpStatusCode.NotFound => new RedmineException(RedmineErrorKind.NotFound,
                $"Not found on target '{name}' (HTTP 404). The id may be wrong, the object may be private to this account, "
                + "or this Redmine version does not offer the endpoint."),
            HttpStatusCode.UnprocessableEntity => new RedmineException(RedmineErrorKind.Validation,
                $"Target '{name}' rejected the data: {await ReadValidationErrorsAsync(response, cancellationToken)}"),
            HttpStatusCode.TooManyRequests => new RedmineException(RedmineErrorKind.Unavailable,
                $"Target '{name}' is rate limiting requests (HTTP 429). Try again shortly."),
            _ when (int)status >= 500 => new RedmineException(RedmineErrorKind.Unavailable,
                $"Target '{name}' reported a server error (HTTP {(int)status}). Try again later."),
            _ => new RedmineException(RedmineErrorKind.Other,
                $"Target '{name}' answered with HTTP {(int)status}."),
        };
    }

    /// <summary>Redmine's validation messages (<c>{"errors":[...]}</c>) describe user input and are safe to show.</summary>
    private static async Task<string> ReadValidationErrorsAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (document.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
            {
                var messages = errors.EnumerateArray()
                    .Select(e => e.GetString())
                    .Where(m => !string.IsNullOrWhiteSpace(m))
                    .Take(10);
                var joined = string.Join("; ", messages);
                if (joined.Length > 0)
                    return joined;
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // Fall through to the generic text.
        }

        return "validation failed (HTTP 422).";
    }

    private RedmineException MapTransportError(HttpRequestException ex)
    {
        var name = target.Name;

        if (ex.InnerException is AuthenticationException)
            return new RedmineException(RedmineErrorKind.Tls,
                $"TLS handshake with target '{name}' failed: the certificate is not trusted or does not match the host. "
                + "For a private CA set ca_path on this target; certificate validation is never disabled.", ex);

        return ex.HttpRequestError switch
        {
            HttpRequestError.NameResolutionError => new RedmineException(RedmineErrorKind.Unavailable,
                $"Cannot resolve the host of target '{name}'. Check the URL and whether the VPN is connected.", ex),
            HttpRequestError.ConnectionError => new RedmineException(RedmineErrorKind.Unavailable,
                $"Cannot connect to target '{name}'. Check the URL, port and whether the VPN is connected.", ex),
            HttpRequestError.SecureConnectionError => new RedmineException(RedmineErrorKind.Tls,
                $"Secure connection to target '{name}' failed (certificate or protocol problem).", ex),
            _ => new RedmineException(RedmineErrorKind.Unavailable,
                $"Request to target '{name}' failed before a response arrived.", ex),
        };
    }
}
