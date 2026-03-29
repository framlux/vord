// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Server;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography;

namespace Framlux.FleetManagement.Test;

/// <summary>
/// Tests for <see cref="CertificateHelper"/>.
/// </summary>
public sealed class CertificateHelperTests
{
    private string _tempDir = string.Empty;

    [Before(TUnit.Core.HookType.Test)]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cert_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [After(TUnit.Core.HookType.Test)]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }

    private string CertPath => Path.Combine(_tempDir, "root.pfx");

    // --- Root certificate creation ---

    [Test]
    public async Task CreateServerCertificateIfNecessary_CreatesFileWhenNoneExists()
    {
        using X509Certificate2 cert = CertificateHelper.CreateServerCertificateIfNecessary(CertPath);

        await Assert.That(File.Exists(CertPath)).IsTrue();
    }

    [Test]
    public async Task CreateServerCertificateIfNecessary_ReturnsExistingValidCert()
    {
        using X509Certificate2 first = CertificateHelper.CreateServerCertificateIfNecessary(CertPath);
        using X509Certificate2 second = CertificateHelper.CreateServerCertificateIfNecessary(CertPath);

        await Assert.That(second.Thumbprint).IsEqualTo(first.Thumbprint);
    }

    [Test]
    public async Task CreateServerCertificateIfNecessary_CreatesDirectoryIfMissing()
    {
        string nestedPath = Path.Combine(_tempDir, "sub", "dir", "root.pfx");

        using X509Certificate2 cert = CertificateHelper.CreateServerCertificateIfNecessary(nestedPath);

        await Assert.That(File.Exists(nestedPath)).IsTrue();
    }

    [Test]
    public async Task RootCertificate_IsCA()
    {
        using X509Certificate2 cert = CertificateHelper.CreateServerCertificateIfNecessary(CertPath);

        X509BasicConstraintsExtension? basicConstraints = cert.Extensions
            .OfType<X509BasicConstraintsExtension>()
            .FirstOrDefault();

        await Assert.That(basicConstraints).IsNotNull();
        await Assert.That(basicConstraints!.CertificateAuthority).IsTrue();
        await Assert.That(basicConstraints.HasPathLengthConstraint).IsTrue();
        await Assert.That(basicConstraints.PathLengthConstraint).IsEqualTo(1);
    }

    [Test]
    public async Task RootCertificate_HasKeyCertSignAndCrlSignKeyUsage()
    {
        using X509Certificate2 cert = CertificateHelper.CreateServerCertificateIfNecessary(CertPath);

        X509KeyUsageExtension? keyUsage = cert.Extensions
            .OfType<X509KeyUsageExtension>()
            .FirstOrDefault();

        await Assert.That(keyUsage).IsNotNull();
        bool hasKeyCertSign = (keyUsage!.KeyUsages & X509KeyUsageFlags.KeyCertSign) == X509KeyUsageFlags.KeyCertSign;
        bool hasCrlSign = (keyUsage.KeyUsages & X509KeyUsageFlags.CrlSign) == X509KeyUsageFlags.CrlSign;

        await Assert.That(hasKeyCertSign).IsTrue();
        await Assert.That(hasCrlSign).IsTrue();
    }

    [Test]
    public async Task RootCertificate_ValidityStartsFourteenDaysAgo()
    {
        using X509Certificate2 cert = CertificateHelper.CreateServerCertificateIfNecessary(CertPath);

        DateTimeOffset expectedNotBefore = DateTimeOffset.UtcNow.AddDays(-14);
        TimeSpan diff = cert.NotBefore.ToUniversalTime() - expectedNotBefore.UtcDateTime;

        await Assert.That(Math.Abs(diff.TotalMinutes)).IsLessThan(5);
    }

    [Test]
    public async Task RootCertificate_ValidityEndsInAbout365Days()
    {
        using X509Certificate2 cert = CertificateHelper.CreateServerCertificateIfNecessary(CertPath);

        DateTimeOffset expectedNotAfter = DateTimeOffset.UtcNow.AddDays(365);
        TimeSpan diff = cert.NotAfter.ToUniversalTime() - expectedNotAfter.UtcDateTime;

        await Assert.That(Math.Abs(diff.TotalMinutes)).IsLessThan(5);
    }

    // --- Client certificate generation ---

    [Test]
    public async Task GenerateNewClientCertificate_ProducesValidPkcs12()
    {
        CertificateHelper.CreateServerCertificateIfNecessary(CertPath);
        ReadOnlyMemory<byte> clientBytes = CertificateHelper.GenerateNewClientCertificate(CertPath, "test-client");
        using X509Certificate2 clientCert = X509CertificateLoader.LoadPkcs12(clientBytes.Span, password: null);

        await Assert.That(clientCert).IsNotNull();
    }

    [Test]
    public async Task ClientCertificate_IsNotCA()
    {
        CertificateHelper.CreateServerCertificateIfNecessary(CertPath);
        ReadOnlyMemory<byte> clientBytes = CertificateHelper.GenerateNewClientCertificate(CertPath, "test-client");
        using X509Certificate2 clientCert = X509CertificateLoader.LoadPkcs12(clientBytes.Span, password: null);

        X509BasicConstraintsExtension? basicConstraints = clientCert.Extensions
            .OfType<X509BasicConstraintsExtension>()
            .FirstOrDefault();

        await Assert.That(basicConstraints).IsNotNull();
        await Assert.That(basicConstraints!.CertificateAuthority).IsFalse();
    }

    [Test]
    public async Task ClientCertificate_HasCorrectKeyUsage()
    {
        CertificateHelper.CreateServerCertificateIfNecessary(CertPath);
        ReadOnlyMemory<byte> clientBytes = CertificateHelper.GenerateNewClientCertificate(CertPath, "test-client");
        using X509Certificate2 clientCert = X509CertificateLoader.LoadPkcs12(clientBytes.Span, password: null);

        X509KeyUsageExtension? keyUsage = clientCert.Extensions
            .OfType<X509KeyUsageExtension>()
            .FirstOrDefault();

        await Assert.That(keyUsage).IsNotNull();
        bool hasDigitalSignature = (keyUsage!.KeyUsages & X509KeyUsageFlags.DigitalSignature) == X509KeyUsageFlags.DigitalSignature;
        bool hasNonRepudiation = (keyUsage.KeyUsages & X509KeyUsageFlags.NonRepudiation) == X509KeyUsageFlags.NonRepudiation;

        await Assert.That(hasDigitalSignature).IsTrue();
        await Assert.That(hasNonRepudiation).IsTrue();
    }

    [Test]
    public async Task ClientCertificate_HasClientAuthenticationEku()
    {
        CertificateHelper.CreateServerCertificateIfNecessary(CertPath);
        ReadOnlyMemory<byte> clientBytes = CertificateHelper.GenerateNewClientCertificate(CertPath, "test-client");
        using X509Certificate2 clientCert = X509CertificateLoader.LoadPkcs12(clientBytes.Span, password: null);

        X509EnhancedKeyUsageExtension? eku = clientCert.Extensions
            .OfType<X509EnhancedKeyUsageExtension>()
            .FirstOrDefault();

        await Assert.That(eku).IsNotNull();
        bool hasClientAuth = eku!.EnhancedKeyUsages.Cast<Oid>().Any(o => o.Value == "1.3.6.1.5.5.7.3.2");

        await Assert.That(hasClientAuth).IsTrue();
    }

    [Test]
    public async Task ClientCertificate_CnMatchesProvidedClientName()
    {
        CertificateHelper.CreateServerCertificateIfNecessary(CertPath);
        ReadOnlyMemory<byte> clientBytes = CertificateHelper.GenerateNewClientCertificate(CertPath, "my-machine-01");
        using X509Certificate2 clientCert = X509CertificateLoader.LoadPkcs12(clientBytes.Span, password: null);

        await Assert.That(clientCert.Subject).Contains("CN=my-machine-01");
    }

    [Test]
    public async Task ClientCertificate_ValidityIsAbout90Days()
    {
        CertificateHelper.CreateServerCertificateIfNecessary(CertPath);
        ReadOnlyMemory<byte> clientBytes = CertificateHelper.GenerateNewClientCertificate(CertPath, "test-client");
        using X509Certificate2 clientCert = X509CertificateLoader.LoadPkcs12(clientBytes.Span, password: null);

        DateTimeOffset expectedNotAfter = DateTimeOffset.UtcNow.AddDays(90);
        TimeSpan diff = clientCert.NotAfter.ToUniversalTime() - expectedNotAfter.UtcDateTime;

        await Assert.That(Math.Abs(diff.TotalMinutes)).IsLessThan(5);
    }

    // --- Error cases ---

    [Test]
    public async Task GenerateNewClientCertificate_ThrowsWhenRootCertMissing()
    {
        string nonExistentPath = Path.Combine(_tempDir, "nonexistent.pfx");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
        {
            CertificateHelper.GenerateNewClientCertificate(nonExistentPath, "test-client");

            return Task.CompletedTask;
        });
    }

    [Test]
    public async Task CreateServerCertificateIfNecessary_ThrowsWhenPathHasNoDirectory()
    {
        // Path.GetDirectoryName("justfilename.pfx") returns "" (not null), causing
        // Directory.CreateDirectory("") to throw ArgumentException
        Exception thrown = await Assert.ThrowsAsync<Exception>(() =>
        {
            CertificateHelper.CreateServerCertificateIfNecessary("justfilename.pfx");

            return Task.CompletedTask;
        });

        await Assert.That(thrown is ArgumentException || thrown is ArgumentOutOfRangeException).IsTrue();
    }

    // --- Invalid/corrupt certificate handling ---

    [Test]
    public async Task CreateServerCertificateIfNecessary_RegeneratesWhenCertFileIsCorrupt()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CertPath)!);
        File.WriteAllBytes(CertPath, new byte[] { 0x00, 0xFF, 0xAB, 0xCD });

        using X509Certificate2 cert = CertificateHelper.CreateServerCertificateIfNecessary(CertPath);

        await Assert.That(cert).IsNotNull();
        await Assert.That(cert.HasPrivateKey).IsTrue();
    }

    [Test]
    public async Task CreateServerCertificateIfNecessary_ReturnsCertWithMoreThan30DaysRemaining()
    {
        using X509Certificate2 cert = CertificateHelper.CreateServerCertificateIfNecessary(CertPath);

        TimeSpan remaining = cert.NotAfter.ToUniversalTime() - DateTime.UtcNow;

        await Assert.That(remaining.TotalDays).IsGreaterThan(30);
    }
}
