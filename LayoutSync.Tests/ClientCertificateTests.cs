using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using LayoutSync.Configuration;
using LayoutSync.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LayoutSync.Tests;

/// <summary>
/// Tests for issue #57: the <c>--cert</c> client certificate is loaded with
/// <see cref="X509CertificateLoader.LoadPkcs12FromFile(string, string?, X509KeyStorageFlags, Pkcs12LoaderLimits?)"/>
/// instead of the obsolete <c>X509Certificate2</c> file constructors (SYSLIB0057).
///
/// The contract: a .pfx with or without a password still loads, with the private key the
/// client-certificate handshake needs, and the real constructor accepts it; a wrong password still
/// fails; and a file that cannot authenticate a client (a bare DER or PEM certificate, or a .pfx
/// with no key) is refused with a message that says what <c>--cert</c> expects. Every certificate
/// is generated in-process, so no fixture holds a key.
/// </summary>
public sealed class ClientCertificateTests : IDisposable
{
    private const string Password = "s3cret";

    private readonly string _root;

    public ClientCertificateTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "layoutsync-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    // ── Harness ──────────────────────────────────────────────────────────────

    private static X509Certificate2 CreateSelfSigned()
    {
        using RSA rsa = RSA.Create(2048);
        CertificateRequest request = new(
            "CN=layoutsync-test-client",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    private string WriteFile(string name, byte[] content)
    {
        string path = Path.Combine(_root, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    /// <summary>
    /// The constructor exactly as the CLI reaches it with <c>--cert</c>. A certificate requires an
    /// https URL; building the DocumentStore opens no connection, so the URL is never dialed.
    /// </summary>
    private static RavenDbService CreateService(string certificatePath, string? password) => new(
        NullLogger<RavenDbService>.Instance,
        new RavenDbOptions
        {
            Url = "https://127.0.0.1:1",
            Database = "cert-test",
            CertificatePath = certificatePath,
            CertificatePassword = password,
        });

    // ── A .pfx loads, with its private key ───────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(Password)]
    public void Pfx_LoadsWithItsPrivateKey(string? password)
    {
        using X509Certificate2 generated = CreateSelfSigned();
        string path = WriteFile("client.pfx", generated.Export(X509ContentType.Pfx, password));

        using X509Certificate2 loaded = RavenDbService.LoadClientCertificate(path, password);

        Assert.True(loaded.HasPrivateKey);
        Assert.Equal(generated.Thumbprint, loaded.Thumbprint);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(Password)]
    public void Constructor_AcceptsPfx(string? password)
    {
        using X509Certificate2 generated = CreateSelfSigned();
        string path = WriteFile("client.pfx", generated.Export(X509ContentType.Pfx, password));

        using RavenDbService service = CreateService(path, password);
    }

    [Fact]
    public void Pfx_WithWrongPassword_Fails()
    {
        using X509Certificate2 generated = CreateSelfSigned();
        string path = WriteFile("client.pfx", generated.Export(X509ContentType.Pfx, Password));

        Assert.ThrowsAny<CryptographicException>(() => CreateService(path, "not-the-password"));
    }

    // ── A file that cannot authenticate a client is refused, saying why ──────

    [Theory]
    [InlineData("der")]
    [InlineData("pem")]
    [InlineData("not-a-certificate")]
    public void NonPkcs12File_IsRefused_SayingCertExpectsAPfxWithItsKey(string format)
    {
        using X509Certificate2 generated = CreateSelfSigned();
        byte[] content = format switch
        {
            "der" => generated.Export(X509ContentType.Cert),
            "pem" => System.Text.Encoding.ASCII.GetBytes(generated.ExportCertificatePem()),
            _ => System.Text.Encoding.UTF8.GetBytes("""{ "identifier": "not-a-certificate" }"""),
        };
        string path = WriteFile("client." + format, content);

        CryptographicException ex = Assert.Throws<CryptographicException>(() => CreateService(path, null));

        Assert.Contains("--cert expects a PKCS#12 (.pfx) file", ex.Message);
        Assert.Contains("private key", ex.Message);
        Assert.Contains(path, ex.Message);
    }

    [Fact]
    public void PfxWithoutAPrivateKey_IsRefused()
    {
        using X509Certificate2 generated = CreateSelfSigned();
        using X509Certificate2 publicOnly = X509CertificateLoader.LoadCertificate(generated.RawData);
        string path = WriteFile("public-only.pfx", publicOnly.Export(X509ContentType.Pfx));

        CryptographicException ex = Assert.Throws<CryptographicException>(() => CreateService(path, null));

        Assert.Contains("--cert expects a PKCS#12 (.pfx) file", ex.Message);
        Assert.Contains("has no private key", ex.Message);
    }

    [Fact]
    public void MissingFile_StillThrowsFileNotFound()
    {
        string path = Path.Combine(_root, "missing.pfx");

        FileNotFoundException ex = Assert.Throws<FileNotFoundException>(() => CreateService(path, null));

        Assert.Contains(path, ex.Message);
    }
}
