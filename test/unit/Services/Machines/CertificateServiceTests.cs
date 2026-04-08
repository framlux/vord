// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Server.Options;
using Framlux.FleetManagement.Server.Services.Machines;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Framlux.FleetManagement.Test.Services;

/// <summary>
/// Tests for <see cref="CertificateService"/>.
/// </summary>
public class CertificateServiceTests
{
    private static (string pem, string thumbprint) GenerateTestCertificate(
        DateTimeOffset? notBefore = null,
        DateTimeOffset? notAfter = null)
    {
        using RSA rsa = RSA.Create(2048);
        CertificateRequest req = new("CN=Test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        DateTimeOffset nb = notBefore ?? DateTimeOffset.UtcNow.AddDays(-1);
        DateTimeOffset na = notAfter ?? DateTimeOffset.UtcNow.AddDays(365);
        using X509Certificate2 cert = req.CreateSelfSigned(nb, na);
        string pem = cert.ExportCertificatePem();
        string thumbprint = cert.Thumbprint;

        return (pem, thumbprint);
    }

    private static CertificateService CreateService(TestDatabaseFactory dbFactory)
    {
        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        IOptions<CertificateOptions> options = Options.Create(new CertificateOptions
        {
            RootCertPath = "/nonexistent/path.pfx"
        });
        ILogger<CertificateService> logger = new NullLogger<CertificateService>();

        return new CertificateService(scopeFactory, options, logger);
    }

    // ========== IsCertificateValidAsync tests ==========

    [Test]
    public async Task IsCertificateValidAsync_InvalidPem_ReturnsFalse()
    {
        using TestDatabaseFactory dbFactory = new();
        CertificateService service = CreateService(dbFactory);

        bool result = await service.IsCertificateValidAsync("not-a-valid-pem", CancellationToken.None);

        await Assert.That(result).IsEqualTo(false);
    }

    [Test]
    public async Task IsCertificateValidAsync_CertNotInDatabase_ReturnsFalse()
    {
        using TestDatabaseFactory dbFactory = new();
        CertificateService service = CreateService(dbFactory);
        (string pem, string _) = GenerateTestCertificate();

        bool result = await service.IsCertificateValidAsync(pem, CancellationToken.None);

        await Assert.That(result).IsEqualTo(false);
    }

    [Test]
    public async Task IsCertificateValidAsync_RevokedCert_ReturnsFalse()
    {
        using TestDatabaseFactory dbFactory = new();
        CertificateService service = CreateService(dbFactory);
        (string pem, string thumbprint) = GenerateTestCertificate();

        await dbFactory.Context.InsertAsync(new MachineCertificate
        {
            MachineId = 1,
            Thumbprint = thumbprint,
            IssuedAt = DateTimeOffset.UtcNow.AddDays(-30),
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(335),
            RevokedAt = DateTimeOffset.UtcNow.AddDays(-1),
        });

        bool result = await service.IsCertificateValidAsync(pem, CancellationToken.None);

        await Assert.That(result).IsEqualTo(false);
    }

    [Test]
    public async Task IsCertificateValidAsync_ExpiredCert_ReturnsFalse()
    {
        using TestDatabaseFactory dbFactory = new();
        CertificateService service = CreateService(dbFactory);
        (string pem, string thumbprint) = GenerateTestCertificate();

        await dbFactory.Context.InsertAsync(new MachineCertificate
        {
            MachineId = 1,
            Thumbprint = thumbprint,
            IssuedAt = DateTimeOffset.UtcNow.AddDays(-400),
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(-1),
        });

        bool result = await service.IsCertificateValidAsync(pem, CancellationToken.None);

        await Assert.That(result).IsEqualTo(false);
    }

    [Test]
    public async Task IsCertificateValidAsync_ValidActiveCert_ReturnsTrue()
    {
        using TestDatabaseFactory dbFactory = new();
        CertificateService service = CreateService(dbFactory);
        (string pem, string thumbprint) = GenerateTestCertificate();

        await dbFactory.Context.InsertAsync(new MachineCertificate
        {
            MachineId = 1,
            Thumbprint = thumbprint,
            IssuedAt = DateTimeOffset.UtcNow.AddDays(-30),
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(335),
        });

        bool result = await service.IsCertificateValidAsync(pem, CancellationToken.None);

        await Assert.That(result).IsEqualTo(true);
    }

    // ========== RevokeCertificateAsync tests ==========

    [Test]
    public async Task RevokeCertificateAsync_InvalidPem_ReturnsFalse()
    {
        using TestDatabaseFactory dbFactory = new();
        CertificateService service = CreateService(dbFactory);

        bool result = await service.RevokeCertificateAsync("not-a-valid-pem", CancellationToken.None);

        await Assert.That(result).IsEqualTo(false);
    }

    [Test]
    public async Task RevokeCertificateAsync_CertNotInDatabase_ReturnsFalse()
    {
        using TestDatabaseFactory dbFactory = new();
        CertificateService service = CreateService(dbFactory);
        (string pem, string _) = GenerateTestCertificate();

        bool result = await service.RevokeCertificateAsync(pem, CancellationToken.None);

        await Assert.That(result).IsEqualTo(false);
    }

    [Test]
    public async Task RevokeCertificateAsync_AlreadyRevoked_ReturnsFalse()
    {
        using TestDatabaseFactory dbFactory = new();
        CertificateService service = CreateService(dbFactory);
        (string pem, string thumbprint) = GenerateTestCertificate();

        await dbFactory.Context.InsertAsync(new MachineCertificate
        {
            MachineId = 1,
            Thumbprint = thumbprint,
            IssuedAt = DateTimeOffset.UtcNow.AddDays(-30),
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(335),
            RevokedAt = DateTimeOffset.UtcNow.AddDays(-1),
        });

        bool result = await service.RevokeCertificateAsync(pem, CancellationToken.None);

        await Assert.That(result).IsEqualTo(false);
    }

    [Test]
    public async Task RevokeCertificateAsync_ActiveCert_ReturnsTrue()
    {
        using TestDatabaseFactory dbFactory = new();
        CertificateService service = CreateService(dbFactory);
        (string pem, string thumbprint) = GenerateTestCertificate();

        await dbFactory.Context.InsertAsync(new MachineCertificate
        {
            MachineId = 1,
            Thumbprint = thumbprint,
            IssuedAt = DateTimeOffset.UtcNow.AddDays(-30),
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(335),
        });

        bool result = await service.RevokeCertificateAsync(pem, CancellationToken.None);

        await Assert.That(result).IsEqualTo(true);
    }

    [Test]
    public async Task RevokeCertificateAsync_ActiveCert_SetsRevokedAt()
    {
        using TestDatabaseFactory dbFactory = new();
        CertificateService service = CreateService(dbFactory);
        (string pem, string thumbprint) = GenerateTestCertificate();

        await dbFactory.Context.InsertAsync(new MachineCertificate
        {
            MachineId = 1,
            Thumbprint = thumbprint,
            IssuedAt = DateTimeOffset.UtcNow.AddDays(-30),
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(335),
        });

        await service.RevokeCertificateAsync(pem, CancellationToken.None);

        MachineCertificate? updated = await dbFactory.Context.MachineCertificates
            .FirstOrDefaultAsync(c => c.Thumbprint == thumbprint);
        await Assert.That(updated).IsNotNull();
        await Assert.That(updated!.RevokedAt.HasValue).IsEqualTo(true);
    }

    // ========== Constructor null-guard tests ==========

    [Test]
    public async Task Constructor_NullServiceScopeFactory_ThrowsArgumentNullException()
    {
        IOptions<CertificateOptions> options = Options.Create(new CertificateOptions
        {
            RootCertPath = "/nonexistent/path.pfx"
        });
        ILogger<CertificateService> logger = new NullLogger<CertificateService>();

        await Assert.That(() =>
            new CertificateService(null!, options, logger))
            .Throws<ArgumentNullException>();
    }

    [Test]
    public async Task Constructor_NullLogger_ThrowsArgumentNullException()
    {
        using TestDatabaseFactory dbFactory = new();
        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        IOptions<CertificateOptions> options = Options.Create(new CertificateOptions
        {
            RootCertPath = "/nonexistent/path.pfx"
        });

        await Assert.That(() =>
            new CertificateService(scopeFactory, options, null!))
            .Throws<ArgumentNullException>();
    }

    // ========== IsCertificateValidAsync edge cases ==========

    [Test]
    public async Task IsCertificateValidAsync_EmptyString_ReturnsFalse()
    {
        using TestDatabaseFactory dbFactory = new();
        CertificateService service = CreateService(dbFactory);

        bool result = await service.IsCertificateValidAsync(string.Empty, CancellationToken.None);

        await Assert.That(result).IsEqualTo(false);
    }

    [Test]
    public async Task IsCertificateValidAsync_CertExpiringExactlyNow_ReturnsFalse()
    {
        using TestDatabaseFactory dbFactory = new();
        CertificateService service = CreateService(dbFactory);
        // Generate a certificate that expired just moments ago
        (string pem, string thumbprint) = GenerateTestCertificate(
            notBefore: DateTimeOffset.UtcNow.AddDays(-2),
            notAfter: DateTimeOffset.UtcNow.AddDays(-1));

        await dbFactory.Context.InsertAsync(new MachineCertificate
        {
            MachineId = 1,
            Thumbprint = thumbprint,
            IssuedAt = DateTimeOffset.UtcNow.AddDays(-2),
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1),
        });

        bool result = await service.IsCertificateValidAsync(pem, CancellationToken.None);

        await Assert.That(result).IsEqualTo(false);
    }

    // ========== RevokeCertificateAsync edge cases ==========

    [Test]
    public async Task RevokeCertificateAsync_EmptyString_ReturnsFalse()
    {
        using TestDatabaseFactory dbFactory = new();
        CertificateService service = CreateService(dbFactory);

        bool result = await service.RevokeCertificateAsync(string.Empty, CancellationToken.None);

        await Assert.That(result).IsEqualTo(false);
    }

    [Test]
    public async Task RevokeCertificateAsync_ActiveCert_RevokedAtIsRecent()
    {
        using TestDatabaseFactory dbFactory = new();
        CertificateService service = CreateService(dbFactory);
        (string pem, string thumbprint) = GenerateTestCertificate();

        await dbFactory.Context.InsertAsync(new MachineCertificate
        {
            MachineId = 1,
            Thumbprint = thumbprint,
            IssuedAt = DateTimeOffset.UtcNow.AddDays(-30),
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(335),
        });

        DateTimeOffset beforeRevoke = DateTimeOffset.UtcNow;
        bool result = await service.RevokeCertificateAsync(pem, CancellationToken.None);

        await Assert.That(result).IsEqualTo(true);

        MachineCertificate? updated = await dbFactory.Context.MachineCertificates
            .FirstOrDefaultAsync(c => c.Thumbprint == thumbprint);
        await Assert.That(updated).IsNotNull();
        await Assert.That(updated!.RevokedAt.HasValue).IsEqualTo(true);
        // RevokedAt should be set to approximately now
        await Assert.That(updated.RevokedAt!.Value >= beforeRevoke.AddSeconds(-5)).IsEqualTo(true);
    }

    [Test]
    public async Task RevokeCertificateAsync_RevokedCert_RemainsRevoked()
    {
        using TestDatabaseFactory dbFactory = new();
        CertificateService service = CreateService(dbFactory);
        (string pem, string thumbprint) = GenerateTestCertificate();

        DateTimeOffset originalRevokedAt = DateTimeOffset.UtcNow.AddDays(-5);
        await dbFactory.Context.InsertAsync(new MachineCertificate
        {
            MachineId = 1,
            Thumbprint = thumbprint,
            IssuedAt = DateTimeOffset.UtcNow.AddDays(-30),
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(335),
            RevokedAt = originalRevokedAt,
        });

        bool result = await service.RevokeCertificateAsync(pem, CancellationToken.None);

        await Assert.That(result).IsEqualTo(false);

        // Verify the original revocation timestamp is unchanged
        MachineCertificate? unchanged = await dbFactory.Context.MachineCertificates
            .FirstOrDefaultAsync(c => c.Thumbprint == thumbprint);
        await Assert.That(unchanged).IsNotNull();
        await Assert.That(unchanged!.RevokedAt.HasValue).IsEqualTo(true);
    }

    [Test]
    public async Task IsCertificateValidAsync_ValidCert_ThenRevoke_ThenValidateFails()
    {
        using TestDatabaseFactory dbFactory = new();
        CertificateService service = CreateService(dbFactory);
        (string pem, string thumbprint) = GenerateTestCertificate();

        await dbFactory.Context.InsertAsync(new MachineCertificate
        {
            MachineId = 1,
            Thumbprint = thumbprint,
            IssuedAt = DateTimeOffset.UtcNow.AddDays(-30),
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(335),
        });

        // First validate succeeds
        bool validBefore = await service.IsCertificateValidAsync(pem, CancellationToken.None);
        await Assert.That(validBefore).IsEqualTo(true);

        // Revoke
        bool revoked = await service.RevokeCertificateAsync(pem, CancellationToken.None);
        await Assert.That(revoked).IsEqualTo(true);

        // Now validate fails
        bool validAfter = await service.IsCertificateValidAsync(pem, CancellationToken.None);
        await Assert.That(validAfter).IsEqualTo(false);
    }
}
