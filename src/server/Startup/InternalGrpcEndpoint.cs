// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Security.Cryptography.X509Certificates;
using Framlux.FleetManagement.Server.Options;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;

namespace Framlux.FleetManagement.Server.Startup;

/// <summary>
/// Binds the dedicated mutual-TLS listener that serves the internal billing and fleet-admin
/// gRPC services.
/// </summary>
/// <remarks>
/// This is a separate listener on purpose. The agent endpoint (HTTP/2, plain text behind the
/// ingress) serves registration, configuration and telemetry for every customer machine, and
/// agents authenticate with an API key — they have no client certificate and never will.
/// Turning on <see cref="ClientCertificateMode.RequireCertificate"/> there would refuse the
/// whole fleet. Putting the internal services behind their own listener makes the requirement
/// a transport-level fact rather than a check a future code path could forget.
/// </remarks>
public static class InternalGrpcEndpoint
{
    /// <summary>
    /// Adds the mutual-TLS listener to Kestrel. The existing configuration-bound endpoints
    /// (the REST and agent gRPC ports) are untouched.
    /// </summary>
    /// <param name="kestrelOptions">The Kestrel server options being configured.</param>
    /// <param name="internalGrpcOptions">The internal endpoint configuration.</param>
    public static void Configure(
        KestrelServerOptions kestrelOptions,
        InternalGrpcOptions internalGrpcOptions)
    {
        ArgumentNullException.ThrowIfNull(kestrelOptions);
        ArgumentNullException.ThrowIfNull(internalGrpcOptions);

        X509Certificate2 serverCertificate = X509Certificate2.CreateFromPemFile(
            internalGrpcOptions.CertificatePath,
            internalGrpcOptions.CertificateKeyPath);

        X509Certificate2Collection trustedClientRoots = new();
        trustedClientRoots.ImportFromPemFile(internalGrpcOptions.ClientCaPath);

        kestrelOptions.ListenAnyIP(internalGrpcOptions.Port, listenOptions =>
        {
            listenOptions.Protocols = HttpProtocols.Http2;
            listenOptions.UseHttps(serverCertificate, httpsOptions =>
            {
                httpsOptions.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
                httpsOptions.ClientCertificateValidation = (certificate, _, _) =>
                    IsIssuedByTrustedCa(certificate, trustedClientRoots);
            });
        });
    }

    /// <summary>
    /// Builds the certificate's chain against the internal certificate authority only. The
    /// machine's trust store is deliberately excluded, so a certificate from a public CA — or
    /// from any other issuer that happens to be trusted by the container image — is rejected.
    /// </summary>
    /// <param name="certificate">The client certificate presented during the handshake.</param>
    /// <param name="trustedRoots">The internal certificate authority bundle.</param>
    internal static bool IsIssuedByTrustedCa(
        X509Certificate2 certificate,
        X509Certificate2Collection trustedRoots)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        ArgumentNullException.ThrowIfNull(trustedRoots);

        using X509Chain chain = new();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.AddRange(trustedRoots);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;

        return chain.Build(certificate);
    }
}
