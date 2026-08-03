// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Framlux.FleetManagement.Server.Startup;

namespace Framlux.FleetManagement.Test.Startup;

/// <summary>
/// The transport half of the internal endpoint's defence: a client certificate is accepted only
/// when it chains to the internal certificate authority. Nothing in the machine trust store
/// counts, so a certificate from a public CA cannot be used to reach the internal services.
/// </summary>
public sealed class InternalGrpcEndpointTests
{
    private static X509Certificate2 CreateCertificateAuthority(string commonName)
    {
        using RSA key = RSA.Create(2048);
        CertificateRequest request = new(
            $"CN={commonName}",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(certificateAuthority: true, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true));

        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(30));
    }

    private static X509Certificate2 IssueClientCertificate(X509Certificate2 issuer, string commonName)
    {
        using RSA key = RSA.Create(2048);
        CertificateRequest request = new(
            $"CN={commonName}",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(certificateAuthority: false, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true));
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.2")], critical: false));

        byte[] serialNumber = RandomNumberGenerator.GetBytes(16);

        return request.Create(
            issuer,
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(1),
            serialNumber);
    }

    [Test]
    public async Task IsIssuedByTrustedCa_CertificateFromInternalCa_ReturnsTrue()
    {
        using X509Certificate2 certificateAuthority = CreateCertificateAuthority("vord-internal-ca");
        using X509Certificate2 client = IssueClientCertificate(certificateAuthority, "billing-api");

        bool trusted = InternalGrpcEndpoint.IsIssuedByTrustedCa(client, [certificateAuthority]);

        await Assert.That(trusted).IsTrue();
    }

    [Test]
    public async Task IsIssuedByTrustedCa_CertificateFromAnotherCa_ReturnsFalse()
    {
        using X509Certificate2 internalCa = CreateCertificateAuthority("vord-internal-ca");
        using X509Certificate2 foreignCa = CreateCertificateAuthority("someone-elses-ca");
        using X509Certificate2 client = IssueClientCertificate(foreignCa, "billing-api");

        bool trusted = InternalGrpcEndpoint.IsIssuedByTrustedCa(client, [internalCa]);

        await Assert.That(trusted).IsFalse();
    }

    [Test]
    public async Task IsIssuedByTrustedCa_SelfSignedCertificate_ReturnsFalse()
    {
        using X509Certificate2 internalCa = CreateCertificateAuthority("vord-internal-ca");
        using X509Certificate2 selfSigned = CreateCertificateAuthority("billing-api");

        bool trusted = InternalGrpcEndpoint.IsIssuedByTrustedCa(selfSigned, [internalCa]);

        await Assert.That(trusted).IsFalse();
    }

    [Test]
    public async Task IsIssuedByTrustedCa_NoTrustedRoots_ReturnsFalse()
    {
        using X509Certificate2 certificateAuthority = CreateCertificateAuthority("vord-internal-ca");
        using X509Certificate2 client = IssueClientCertificate(certificateAuthority, "billing-api");

        bool trusted = InternalGrpcEndpoint.IsIssuedByTrustedCa(client, []);

        await Assert.That(trusted).IsFalse();
    }
}
