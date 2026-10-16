using System.Net.Security;
using McpRedmine.Config;

namespace McpRedmine.Redmine;

public static class RedmineHttpHandlers
{
    public static SocketsHttpHandler CreatePrimaryHandler(TargetConfig target)
    {
        var handler = new SocketsHttpHandler
        {
            // The key travels in a custom header, which .NET would forward on a redirect to another host
            // (or a downgrade to http). Redirects are therefore never followed; see RedmineClient.
            AllowAutoRedirect = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(10),
        };

        if (target.CaPath is not null)
        {
            var cas = CustomCaValidator.LoadCas(target.CaPath);
            handler.SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
                    CustomCaValidator.Validate(certificate, errors, cas),
            };
        }

        return handler;
    }
}
