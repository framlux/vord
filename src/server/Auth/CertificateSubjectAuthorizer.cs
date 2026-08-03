// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Security.Cryptography.X509Certificates;
using Framlux.FleetManagement.Server.Options;
using Grpc.Core;
using Microsoft.Extensions.Options;

namespace Framlux.FleetManagement.Server.Auth;

/// <summary>
/// Authorises internal gRPC callers by the subject of the client certificate they presented
/// during the TLS handshake.
/// </summary>
/// <remarks>
/// Kestrel has already proved that the certificate chains to the internal certificate
/// authority before the call reaches here. Chain validity alone is not an authorisation
/// decision though: every certificate the internal CA issues would satisfy it, which would be
/// no stronger than the shared secret this replaces. So the subject is matched against
/// <see cref="InternalGrpcOptions.AllowedClientSubjects"/> as well, which is what makes the
/// caller's identity — not merely its issuer — the thing being authorised.
/// </remarks>
public sealed class CertificateSubjectAuthorizer : IInternalCallerAuthorizer
{
    private readonly InternalGrpcOptions _options;
    private readonly ILogger<CertificateSubjectAuthorizer> _logger;

    /// <summary>
    /// Creates a new instance of the <see cref="CertificateSubjectAuthorizer"/> class.
    /// </summary>
    /// <param name="options">The internal gRPC endpoint configuration.</param>
    /// <param name="logger">The logger used to record rejected callers.</param>
    public CertificateSubjectAuthorizer(
        IOptions<InternalGrpcOptions> options,
        ILogger<CertificateSubjectAuthorizer> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc/>
    public void Authorize(ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        Authorize(context.GetHttpContext().Connection.ClientCertificate);
    }

    /// <summary>
    /// Applies the authorisation decision to an already-resolved client certificate. Throws
    /// <see cref="StatusCode.Unavailable"/> when no subjects are configured (the service cannot
    /// make a decision, so it makes none), <see cref="StatusCode.Unauthenticated"/> when no
    /// certificate was presented, and <see cref="StatusCode.PermissionDenied"/> when a valid
    /// certificate carries a subject that is not on the permitted list.
    /// </summary>
    /// <param name="clientCertificate">The certificate presented by the caller, if any.</param>
    internal void Authorize(X509Certificate2? clientCertificate)
    {
        if (_options.AllowedClientSubjects.Count == 0)
        {
            throw new RpcException(new Status(
                StatusCode.Unavailable,
                "Internal gRPC access is not configured"));
        }

        if (clientCertificate is null)
        {
            throw new RpcException(new Status(
                StatusCode.Unauthenticated,
                "A client certificate is required"));
        }

        if (IsSubjectAllowed(clientCertificate, _options.AllowedClientSubjects) == false)
        {
            _logger.LogWarning(
                "Rejected internal gRPC call: certificate subject {CertificateSubject} is not permitted",
                clientCertificate.Subject);

            throw new RpcException(new Status(
                StatusCode.PermissionDenied,
                "Client certificate subject is not permitted"));
        }
    }

    /// <summary>
    /// Returns whether the certificate carries one of the permitted identities. A permitted
    /// entry may name the full subject distinguished name, the common name on its own, or any
    /// DNS subject alternative name, so operators do not have to hand-write distinguished names
    /// to match what cert-manager issued.
    /// </summary>
    /// <param name="certificate">The certificate presented by the caller.</param>
    /// <param name="allowedSubjects">The configured permitted subjects.</param>
    internal static bool IsSubjectAllowed(
        X509Certificate2 certificate,
        IEnumerable<string> allowedSubjects)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        ArgumentNullException.ThrowIfNull(allowedSubjects);

        HashSet<string> identities = new(StringComparer.OrdinalIgnoreCase)
        {
            certificate.Subject
        };

        string commonName = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
        if (string.IsNullOrEmpty(commonName) == false)
        {
            identities.Add(commonName);
        }

        foreach (X509SubjectAlternativeNameExtension extension in
            certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>())
        {
            foreach (string dnsName in extension.EnumerateDnsNames())
            {
                identities.Add(dnsName);
            }
        }

        foreach (string allowedSubject in allowedSubjects)
        {
            if (string.IsNullOrWhiteSpace(allowedSubject))
            {
                continue;
            }

            if (identities.Contains(allowedSubject.Trim()))
            {
                return true;
            }
        }

        return false;
    }
}
