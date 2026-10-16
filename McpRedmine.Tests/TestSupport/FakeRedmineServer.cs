using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace McpRedmine.Tests.TestSupport;

/// <summary>In-process Kestrel on a random loopback port, standing in for a real Redmine instance.</summary>
public sealed class FakeRedmineServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    private FakeRedmineServer(WebApplication app, string baseUrl)
    {
        _app = app;
        BaseUrl = baseUrl;
    }

    public string BaseUrl { get; }

    public static async Task<FakeRedmineServer> StartAsync(Action<WebApplication> map, X509Certificate2? certificate = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, listen =>
        {
            listen.Protocols = HttpProtocols.Http1;
            if (certificate is not null)
                listen.UseHttps(certificate);
        }));

        var app = builder.Build();
        map(app);
        await app.StartAsync();

        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new FakeRedmineServer(app, address.Replace("[::]", "127.0.0.1").TrimEnd('/') + "/");
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

/// <summary>A throw-away CA and a server certificate for 127.0.0.1 signed by it.</summary>
public sealed class TestPki : IDisposable
{
    private readonly X509Certificate2 _ca;

    public TestPki(string caName = "Test CA")
    {
        using var caKey = RSA.Create(2048);
        var caRequest = new CertificateRequest($"CN={caName}", caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        var now = DateTimeOffset.UtcNow;
        _ca = caRequest.CreateSelfSigned(now.AddDays(-1), now.AddDays(1));

        using var serverKey = RSA.Create(2048);
        var serverRequest = new CertificateRequest("CN=127.0.0.1", serverKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(IPAddress.Loopback);
        serverRequest.CertificateExtensions.Add(san.Build());
        serverRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));

        using var signed = serverRequest.Create(_ca, now.AddDays(-1), now.AddDays(1), RandomNumberGenerator.GetBytes(8));
        using var withKey = signed.CopyWithPrivateKey(serverKey);
        // Re-import so the private key is usable by Kestrel/SChannel on Windows.
        ServerCertificate = X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pfx), null);
    }

    public X509Certificate2 ServerCertificate { get; }

    public string WriteCaPem(string path)
    {
        File.WriteAllText(path, _ca.ExportCertificatePem());
        return path;
    }

    public void Dispose()
    {
        _ca.Dispose();
        ServerCertificate.Dispose();
    }
}
