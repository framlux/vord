// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Server.Options;
using LinqToDB.Async;
using LinqToDB;
using Microsoft.Extensions.Options;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography;

namespace Framlux.FleetManagement.Server.Services.Machines;

/// <summary>
/// Service for managing machine certificates for secure communication.
/// </summary>
public sealed class CertificateService : ICertificateService
{
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ILogger<CertificateService> _logger;
    private readonly string _rootCertPath;

    /// <summary>
    /// Initializes a new instance of the <see cref="CertificateService"/> class.
    /// </summary>
    /// <param name="serviceScopeFactory">Factory for creating service scopes for database operations.</param>
    /// <param name="certificateOptions">Certificate configuration options.</param>
    /// <param name="logger">The logger instance for diagnostic logging.</param>
    /// <exception cref="ArgumentNullException">Thrown when a required parameter is null.</exception>
    public CertificateService(
        IServiceScopeFactory serviceScopeFactory,
        IOptions<CertificateOptions> certificateOptions,
        ILogger<CertificateService> logger)
    {
        _serviceScopeFactory = serviceScopeFactory ?? throw new ArgumentNullException(nameof(serviceScopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _rootCertPath = certificateOptions.Value.RootCertPath;
    }

    /// <summary>
    /// Creates a new certificate for the specified machine.
    /// </summary>
    /// <param name="machineId">The unique identifier of the machine.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>A tuple containing the public certificate PEM and private key PEM.</returns>
    public async Task<(string publicCert, string privateKey)> CreateCertificateForMachineAsync(long machineId, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Creating certificate for machine {MachineId}", machineId);

        string clientName = $"machine-{machineId}";
        ReadOnlyMemory<byte> pkcs12Bytes = CertificateHelper.GenerateNewClientCertificate(_rootCertPath, clientName);

        using X509Certificate2 cert = X509CertificateLoader.LoadPkcs12(pkcs12Bytes.Span, password: null);

        string publicCertPem = cert.ExportCertificatePem();
        using RSA rsaPrivateKey = cert.GetRSAPrivateKey()
            ?? throw new InvalidOperationException("Generated certificate does not contain an RSA private key");
        string privateKeyPem = rsaPrivateKey.ExportRSAPrivateKeyPem();

        MachineCertificate record = new()
        {
            MachineId = machineId,
            Thumbprint = cert.Thumbprint,
            IssuedAt = new DateTimeOffset(cert.NotBefore, TimeSpan.Zero),
            ExpiresAt = new DateTimeOffset(cert.NotAfter, TimeSpan.Zero),
        };

        using IServiceScope scope = _serviceScopeFactory.CreateScope();
        DatabaseContext db = scope.ServiceProvider.GetRequiredService<DatabaseContext>();
        await db.InsertAsync(record, token: cancellationToken);

        _logger.LogInformation("Certificate created for machine {MachineId} with thumbprint {Thumbprint}", machineId, cert.Thumbprint);

        return (publicCertPem, privateKeyPem);
    }

    /// <summary>
    /// Validates whether the specified certificate is valid and not revoked.
    /// </summary>
    /// <param name="publicCert">The PEM-encoded public certificate to validate.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>True if the certificate is valid and not revoked; otherwise, false.</returns>
    public async Task<bool> IsCertificateValidAsync(string publicCert, CancellationToken cancellationToken)
    {
        X509Certificate2 cert;
        try
        {
            cert = X509Certificate2.CreateFromPem(publicCert);
        }
        catch (CryptographicException ex)
        {
            _logger.LogWarning(ex, "Failed to parse certificate PEM for validation");

            return false;
        }

        using (cert)
        {
            string thumbprint = cert.Thumbprint;

            using IServiceScope scope = _serviceScopeFactory.CreateScope();
            DatabaseContext db = scope.ServiceProvider.GetRequiredService<DatabaseContext>();

            MachineCertificate? record = await db.MachineCertificates
                .FirstOrDefaultAsync(c => c.Thumbprint == thumbprint, cancellationToken);

            if (record is null)
            {
                _logger.LogWarning("Certificate with thumbprint {Thumbprint} not found in database", thumbprint);

                return false;
            }

            if (record.RevokedAt.HasValue)
            {
                _logger.LogWarning("Certificate with thumbprint {Thumbprint} has been revoked", thumbprint);

                return false;
            }

            if (record.ExpiresAt < DateTimeOffset.UtcNow)
            {
                _logger.LogWarning("Certificate with thumbprint {Thumbprint} has expired", thumbprint);

                return false;
            }

            return true;
        }
    }

    /// <summary>
    /// Revokes the specified certificate, preventing further use.
    /// </summary>
    /// <param name="publicCert">The PEM-encoded public certificate to revoke.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>True if the certificate was successfully revoked; otherwise, false.</returns>
    public async Task<bool> RevokeCertificateAsync(string publicCert, CancellationToken cancellationToken)
    {
        X509Certificate2 cert;
        try
        {
            cert = X509Certificate2.CreateFromPem(publicCert);
        }
        catch (CryptographicException ex)
        {
            _logger.LogWarning(ex, "Failed to parse certificate PEM for revocation");

            return false;
        }

        using (cert)
        {
            string thumbprint = cert.Thumbprint;

            using IServiceScope scope = _serviceScopeFactory.CreateScope();
            DatabaseContext db = scope.ServiceProvider.GetRequiredService<DatabaseContext>();

            int updated = await db.MachineCertificates
                .Where(c => c.Thumbprint == thumbprint && c.RevokedAt == null)
                .Set(c => c.RevokedAt, DateTimeOffset.UtcNow)
                .UpdateAsync(cancellationToken);

            if (updated > 0)
            {
                _logger.LogInformation("Revoked certificate with thumbprint {Thumbprint}", thumbprint);

                return true;
            }

            _logger.LogWarning("No active certificate found with thumbprint {Thumbprint} to revoke", thumbprint);

            return false;
        }
    }
}
