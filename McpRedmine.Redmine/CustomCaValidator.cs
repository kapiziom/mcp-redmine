using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using McpRedmine.Config;

namespace McpRedmine.Redmine;

/// <summary>
/// Validates a server certificate against a per-target CA instead of ever disabling validation.
/// Name mismatches and any non-chain error are still rejected.
/// </summary>
public static class CustomCaValidator
{
    public static bool Validate(X509Certificate? certificate, SslPolicyErrors errors, X509Certificate2Collection trustedCas)
    {
        if (errors == SslPolicyErrors.None)
            return true;

        // Only an untrusted-chain failure can be rescued by the custom CA.
        if (certificate is null || (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != 0)
            return false;

        using var server = new X509Certificate2(certificate);
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.AddRange(trustedCas);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;

        return chain.Build(server);
    }

    /// <summary>Loads a PEM file (one or several certificates) or a single DER certificate.</summary>
    public static X509Certificate2Collection LoadCas(string path)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (IOException ex)
        {
            throw new UserFacingException($"Cannot read CA file '{path}': {ex.GetType().Name}.", ex);
        }

        var collection = new X509Certificate2Collection();
        try
        {
            if (System.Text.Encoding.ASCII.GetString(bytes).Contains("-----BEGIN CERTIFICATE-----", StringComparison.Ordinal))
                collection.ImportFromPem(System.Text.Encoding.ASCII.GetString(bytes));
            else
                collection.Add(X509CertificateLoader.LoadCertificate(bytes));
        }
        catch (CryptographicException ex)
        {
            throw new UserFacingException($"CA file '{path}' is not a valid certificate (expected PEM or DER).", ex);
        }

        if (collection.Count == 0)
            throw new UserFacingException($"CA file '{path}' contains no certificates (expected PEM or DER).");

        return collection;
    }
}
