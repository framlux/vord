// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Framlux.FleetManagement.Test.Infrastructure;

/// <summary>
/// Test-host stand-in for the TLS handshake. <c>TestServer</c> never performs one, so a
/// functional test has no way to present a client certificate; this middleware mints one with
/// the subject named in the <see cref="SubjectHeader"/> request header and attaches it to the
/// connection exactly where Kestrel would have.
/// </summary>
/// <remarks>
/// It deliberately mints a certificate for whatever subject the test asks for, including
/// subjects that must be rejected. That is the point: the production authorisation decision is
/// then the only thing standing between the caller and the internal services, so a test that
/// asks for a non-permitted subject genuinely proves the subject check runs. Chain validation
/// happens at the transport layer in production and is covered separately by
/// <c>InternalGrpcEndpointTests</c>.
/// </remarks>
public sealed class TestClientCertificateMiddleware
{
    /// <summary>Request header naming the certificate subject the caller should appear to hold.</summary>
    public const string SubjectHeader = "x-test-client-certificate-subject";

    private readonly RequestDelegate _next;

    /// <summary>
    /// Creates a new instance of the <see cref="TestClientCertificateMiddleware"/> class.
    /// </summary>
    /// <param name="next">The next middleware in the pipeline.</param>
    public TestClientCertificateMiddleware(RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(next);
        _next = next;
    }

    /// <summary>
    /// Attaches a synthetic client certificate to the connection when the request asks for one.
    /// </summary>
    /// <param name="context">The current HTTP context.</param>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Request.Headers.TryGetValue(SubjectHeader, out Microsoft.Extensions.Primitives.StringValues subject) &&
            (string.IsNullOrWhiteSpace(subject.ToString()) == false))
        {
            context.Features.Set<ITlsConnectionFeature>(
                new StubTlsConnectionFeature(CreateCertificate(subject.ToString())));
        }
        else
        {
            // No certificate at all — the connection looks exactly like the plain-text agent port.
            context.Features.Set<ITlsConnectionFeature>(new StubTlsConnectionFeature(null));
        }

        await _next(context);
    }

    private static X509Certificate2 CreateCertificate(string commonName)
    {
        using RSA key = RSA.Create(2048);
        CertificateRequest request = new(
            $"CN={commonName}",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(1));
    }

    private sealed class StubTlsConnectionFeature : ITlsConnectionFeature
    {
        public StubTlsConnectionFeature(X509Certificate2? clientCertificate)
        {
            ClientCertificate = clientCertificate;
        }

        public X509Certificate2? ClientCertificate { get; set; }

        public Task<X509Certificate2?> GetClientCertificateAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(ClientCertificate);
        }
    }
}
