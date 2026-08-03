// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Server.Options;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Framlux.FleetManagement.Test.Auth;

/// <summary>
/// Locks the contract every internal gRPC service depends on: the caller is authorised by the
/// subject of the client certificate it presented, and the decision fails closed. These replace
/// the shared-secret validator tests — the cases map across directly (unconfigured rejects,
/// absent credential rejects, wrong credential rejects, correct credential accepts), with one
/// addition that has no shared-secret equivalent: a certificate that is perfectly valid but
/// carries a subject nobody permitted must still be refused.
/// </summary>
public sealed class CertificateSubjectAuthorizerTests
{
    private const string PermittedSubject = "billing-api.vord-fleet.svc.cluster.local";
    private const string OtherSubject = "some-other-workload.vord-fleet.svc.cluster.local";

    private static X509Certificate2 CreateCertificate(string commonName, string? dnsName = null)
    {
        using RSA key = RSA.Create(2048);
        CertificateRequest request = new(
            $"CN={commonName}",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        if (dnsName is not null)
        {
            SubjectAlternativeNameBuilder sanBuilder = new();
            sanBuilder.AddDnsName(dnsName);
            request.CertificateExtensions.Add(sanBuilder.Build());
        }

        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(1));
    }

    private static CertificateSubjectAuthorizer CreateAuthorizer(params string[] allowedSubjects)
    {
        InternalGrpcOptions options = new()
        {
            Enabled = true,
            AllowedClientSubjects = allowedSubjects.ToList()
        };

        return new CertificateSubjectAuthorizer(
            Options.Create(options),
            Substitute.For<ILogger<CertificateSubjectAuthorizer>>());
    }

    [Test]
    public async Task Authorize_NoPermittedSubjectsConfigured_ThrowsUnavailable()
    {
        CertificateSubjectAuthorizer authorizer = CreateAuthorizer();
        using X509Certificate2 certificate = CreateCertificate(PermittedSubject);

        RpcException? exception = await Assert.ThrowsAsync<RpcException>(() =>
        {
            authorizer.Authorize(certificate);

            return Task.CompletedTask;
        });

        await Assert.That(exception).IsNotNull();
        await Assert.That(exception!.StatusCode).IsEqualTo(StatusCode.Unavailable);
    }

    [Test]
    public async Task Authorize_NoCertificatePresented_ThrowsUnauthenticated()
    {
        CertificateSubjectAuthorizer authorizer = CreateAuthorizer(PermittedSubject);

        RpcException? exception = await Assert.ThrowsAsync<RpcException>(() =>
        {
            authorizer.Authorize(clientCertificate: null);

            return Task.CompletedTask;
        });

        await Assert.That(exception).IsNotNull();
        await Assert.That(exception!.StatusCode).IsEqualTo(StatusCode.Unauthenticated);
    }

    /// <summary>
    /// The decisive test. The certificate here is structurally valid and — in production — would
    /// have chained to the internal CA before reaching this code. It is refused purely because
    /// its subject is not on the permitted list. Were the service to authorise on certificate
    /// validity alone, every certificate the internal CA ever issued would be accepted and the
    /// barrier would be no stronger than the shared secret it replaced.
    /// </summary>
    [Test]
    public async Task Authorize_ValidCertificateWithNonPermittedSubject_ThrowsPermissionDenied()
    {
        CertificateSubjectAuthorizer authorizer = CreateAuthorizer(PermittedSubject);
        using X509Certificate2 certificate = CreateCertificate(OtherSubject);

        RpcException? exception = await Assert.ThrowsAsync<RpcException>(() =>
        {
            authorizer.Authorize(certificate);

            return Task.CompletedTask;
        });

        await Assert.That(exception).IsNotNull();
        await Assert.That(exception!.StatusCode).IsEqualTo(StatusCode.PermissionDenied);
    }

    [Test]
    public async Task Authorize_PermittedCommonName_Succeeds()
    {
        CertificateSubjectAuthorizer authorizer = CreateAuthorizer(PermittedSubject);
        using X509Certificate2 certificate = CreateCertificate(PermittedSubject);

        Exception? caught = null;
        try
        {
            authorizer.Authorize(certificate);
        }
        catch (Exception ex)
        {
            caught = ex;
        }

        await Assert.That(caught).IsNull();
    }

    [Test]
    public async Task Authorize_SeveralPermittedSubjects_AcceptsEachOne()
    {
        CertificateSubjectAuthorizer authorizer = CreateAuthorizer(PermittedSubject, OtherSubject);
        using X509Certificate2 first = CreateCertificate(PermittedSubject);
        using X509Certificate2 second = CreateCertificate(OtherSubject);

        Exception? caught = null;
        try
        {
            authorizer.Authorize(first);
            authorizer.Authorize(second);
        }
        catch (Exception ex)
        {
            caught = ex;
        }

        await Assert.That(caught).IsNull();
    }

    [Test]
    public async Task IsSubjectAllowed_MatchesFullDistinguishedName()
    {
        using X509Certificate2 certificate = CreateCertificate(PermittedSubject);

        bool allowed = CertificateSubjectAuthorizer.IsSubjectAllowed(
            certificate,
            [certificate.Subject]);

        await Assert.That(allowed).IsTrue();
    }

    [Test]
    public async Task IsSubjectAllowed_MatchesDnsSubjectAlternativeName()
    {
        using X509Certificate2 certificate = CreateCertificate("unrelated-common-name", dnsName: PermittedSubject);

        bool allowed = CertificateSubjectAuthorizer.IsSubjectAllowed(certificate, [PermittedSubject]);

        await Assert.That(allowed).IsTrue();
    }

    [Test]
    public async Task IsSubjectAllowed_ComparisonIsCaseInsensitive()
    {
        using X509Certificate2 certificate = CreateCertificate(PermittedSubject);

        bool allowed = CertificateSubjectAuthorizer.IsSubjectAllowed(
            certificate,
            [PermittedSubject.ToUpperInvariant()]);

        await Assert.That(allowed).IsTrue();
    }

    [Test]
    public async Task IsSubjectAllowed_IgnoresBlankEntries()
    {
        using X509Certificate2 certificate = CreateCertificate(PermittedSubject);

        bool allowed = CertificateSubjectAuthorizer.IsSubjectAllowed(
            certificate,
            ["", "   ", OtherSubject]);

        await Assert.That(allowed).IsFalse();
    }

    [Test]
    public async Task IsSubjectAllowed_DoesNotMatchOnSubjectPrefix()
    {
        // A prefix must not be enough — otherwise "billing-api.vord-fleet.svc.cluster.local.evil"
        // would be accepted by a rule naming the real service.
        using X509Certificate2 certificate = CreateCertificate(PermittedSubject + ".evil.example");

        bool allowed = CertificateSubjectAuthorizer.IsSubjectAllowed(certificate, [PermittedSubject]);

        await Assert.That(allowed).IsFalse();
    }

    [Test]
    public async Task IsSubjectAllowed_NullCertificate_ThrowsArgumentNullException()
    {
        ArgumentNullException? exception = await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            CertificateSubjectAuthorizer.IsSubjectAllowed(null!, [PermittedSubject]);

            return Task.CompletedTask;
        });

        await Assert.That(exception).IsNotNull();
    }
}
