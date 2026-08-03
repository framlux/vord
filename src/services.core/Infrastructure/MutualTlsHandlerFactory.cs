// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace Framlux.FleetManagement.Services.Core.Infrastructure;

/// <summary>
/// Builds the HTTP message handler used for outbound internal gRPC calls that prove this
/// process's identity with a client certificate instead of a shared secret.
/// </summary>
public static class MutualTlsHandlerFactory
{
    /// <summary>
    /// Creates a handler that presents <paramref name="clientCertificatePath"/> on every
    /// connection and, when <paramref name="serverCaPath"/> is supplied, verifies the server's
    /// certificate against that authority alone rather than the machine trust store.
    /// </summary>
    /// <param name="clientCertificatePath">Path to the PEM-encoded client certificate.</param>
    /// <param name="clientCertificateKeyPath">Path to the matching PEM-encoded private key.</param>
    /// <param name="serverCaPath">Optional path to the PEM-encoded internal CA bundle.</param>
    public static SocketsHttpHandler Create(
        string clientCertificatePath,
        string clientCertificateKeyPath,
        string serverCaPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientCertificatePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientCertificateKeyPath);
        ArgumentNullException.ThrowIfNull(serverCaPath);

        X509Certificate2 clientCertificate = X509Certificate2.CreateFromPemFile(
            clientCertificatePath,
            clientCertificateKeyPath);

        SslClientAuthenticationOptions sslOptions = new()
        {
            ClientCertificates = new X509CertificateCollection { clientCertificate }
        };

        if (string.IsNullOrWhiteSpace(serverCaPath) == false)
        {
            X509Certificate2Collection trustedRoots = new();
            trustedRoots.ImportFromPemFile(serverCaPath);

            sslOptions.CertificateChainPolicy = new X509ChainPolicy
            {
                TrustMode = X509ChainTrustMode.CustomRootTrust,
                RevocationMode = X509RevocationMode.NoCheck
            };
            sslOptions.CertificateChainPolicy.CustomTrustStore.AddRange(trustedRoots);
        }

        return new SocketsHttpHandler
        {
            SslOptions = sslOptions,
            // gRPC keeps HTTP/2 connections open indefinitely; bound their lifetime so a
            // rescheduled or renumbered peer is redialled rather than pinned forever. The
            // certificate itself is read once at startup, so a renewal needs a pod restart.
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
            EnableMultipleHttp2Connections = true
        };
    }
}
