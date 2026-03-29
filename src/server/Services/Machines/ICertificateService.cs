// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Server.Services.Machines;

/// <summary>
/// Service for managing machine certificates.
/// </summary>
public interface ICertificateService
{
    /// <summary>
    /// Creates a new certificate for the specified machine.
    /// </summary>
    /// <param name="machineId">The ID of the machine to generate a new certificate for</param>
    /// <param name="cancellationToken">Token used to cancel long-running tasks</param>
    /// <returns>Returns a certificate keypair</returns>
    Task<(string publicCert, string privateKey)> CreateCertificateForMachineAsync(long machineId, CancellationToken cancellationToken);

    /// <summary>
    /// Validates the provided certificate.
    /// </summary>
    /// <param name="publicCert">The public certificate to validate.</param>
    /// <param name="cancellationToken">Token used to cancel long-running tasks.</param>
    /// <returns>Returns true if the certificate is valid; otherwise false.</returns>
    Task<bool> IsCertificateValidAsync(string publicCert, CancellationToken cancellationToken);

    /// <summary>
    /// Revokes the specified certificate so it can no longer be used for authentication.
    /// </summary>
    /// <param name="publicCert">The public certificate to revoke.</param>
    /// <param name="cancellationToken">Token used to cancel long-running tasks.</param>
    /// <returns>Returns true if the certificate was successfully revoked; otherwise false.</returns>
    Task<bool> RevokeCertificateAsync(string publicCert, CancellationToken cancellationToken);
}
