// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Framlux.FleetManagement.Server;

/// <summary>
/// Provides helper methods for creating and managing X.509 certificates for secure communication.
/// </summary>
public static class CertificateHelper
{
    private const string ROOT_CERT_AUTHORITY = "Framlux MachineInfo Issuing Authority";
    private const int RSA_CERT_KEY_LENGTH = 4096;
    private const int CERT_VALID_NOT_BEFORE_DAYS_FROM_TODAY = -14;
    private const int CERT_VALID_NOT_AFTER_DAYS_FROM_TODAY = 365;
    private const string CERT_PASSWORD_ENV_VAR = "FRAMLUX_CERT_PASSWORD";

    /// <summary>
    /// Creates a server certificate if one doesn't exist or is expiring soon at the specified path.
    /// </summary>
    /// <param name="path">The file path where the certificate should be stored.</param>
    /// <returns>An <see cref="X509Certificate2"/> representing the root certificate.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the directory path cannot be determined.</exception>
    public static X509Certificate2 CreateServerCertificateIfNecessary(string path)
    {
        string? certPassword = Environment.GetEnvironmentVariable(CERT_PASSWORD_ENV_VAR);
        X509Certificate2? rootCert = GetRootCertificate(path, certPassword);
        if (rootCert == null)
        {
            rootCert = GenerateRootCertificate(ROOT_CERT_AUTHORITY);

            string filePath = Path.GetDirectoryName(path) ?? throw new ArgumentOutOfRangeException($"Could not find directory name for certificate '{path}'");
            Directory.CreateDirectory(filePath);
            File.WriteAllBytes(path, rootCert.Export(X509ContentType.Pkcs12, certPassword));
        }

        return rootCert;
    }

    /// <summary>
    /// Generates a new client certificate signed by the root certificate.
    /// </summary>
    /// <param name="rootCertPath">The file path to the root certificate.</param>
    /// <param name="clientName">The common name (CN) for the client certificate.</param>
    /// <returns>A byte array containing the exported client certificate in PKCS12 format.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the root certificate cannot be found.</exception>
    public static ReadOnlyMemory<byte> GenerateNewClientCertificate(string rootCertPath, string clientName)
    {
        string? certPassword = Environment.GetEnvironmentVariable(CERT_PASSWORD_ENV_VAR);
        using X509Certificate2? rootCert = GetRootCertificate(rootCertPath, certPassword);
        if (rootCert == null)
        {
            throw new InvalidOperationException("Could not find Root Certificate");
        }

        using X509Certificate2 clientCert = GenerateClientCertificate(rootCert, clientName);

        return clientCert.Export(X509ContentType.Pkcs12);
    }

    /// <summary>
    /// Retrieves the root certificate from the specified path if it exists and is still valid.
    /// </summary>
    /// <param name="path">The file path to the root certificate.</param>
    /// <param name="password">The password used to decrypt the PFX file, or null for no password.</param>
    /// <returns>The root certificate if valid, otherwise null.</returns>
    private static X509Certificate2? GetRootCertificate(string path, string? password)
    {
        X509Certificate2? rootCert;

        try
        {
            // Check if the cert exists
            byte[] certData = File.ReadAllBytes(path);
            rootCert = X509CertificateLoader.LoadPkcs12(certData, password: password);
        }
        catch (Exception ex) when (ex is IOException or CryptographicException)
        {
            rootCert = null;
        }

        // Check if the root cert exists and isn't going to expire within 30 days
        if ((rootCert == null) || (rootCert.NotAfter < DateTimeOffset.UtcNow.AddDays(30)))
        {
            rootCert = null;
        }

        return rootCert;
    }

    /// <summary>
    /// Generates a self-signed root certificate authority certificate.
    /// </summary>
    /// <param name="issuerName">The common name for the root certificate authority.</param>
    /// <returns>A self-signed <see cref="X509Certificate2"/> configured as a certificate authority.</returns>
    private static X509Certificate2 GenerateRootCertificate(string issuerName)
    {
        using (RSA parent = RSA.Create(RSA_CERT_KEY_LENGTH))
        {
            CertificateRequest parentReq = new(
                $"CN={issuerName}",
                parent,
                HashAlgorithmName.SHA512,
                RSASignaturePadding.Pkcs1);

            parentReq.CertificateExtensions.Add(
                new X509BasicConstraintsExtension(
                    certificateAuthority: true,   // This makes it a CA
                    hasPathLengthConstraint: true,
                    pathLengthConstraint: 1,      // Can issue client certs
                    critical: true));             // Mark as critical

            // **Key Usage must allow certificate signing**
            parentReq.CertificateExtensions.Add(
                new X509KeyUsageExtension(
                    X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign,
                    critical: true));

            parentReq.CertificateExtensions.Add(
                new X509SubjectKeyIdentifierExtension(parentReq.PublicKey, false));

            return parentReq.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddDays(CERT_VALID_NOT_BEFORE_DAYS_FROM_TODAY),
                DateTimeOffset.UtcNow.AddDays(CERT_VALID_NOT_AFTER_DAYS_FROM_TODAY));
        }
    }

    /// <summary>
    /// Generates a client certificate signed by the specified root certificate.
    /// </summary>
    /// <param name="rootCert">The root certificate authority that will sign the client certificate.</param>
    /// <param name="clientName">The common name (CN) for the client certificate.</param>
    /// <returns>A signed <see cref="X509Certificate2"/> for client authentication.</returns>
    private static X509Certificate2 GenerateClientCertificate(X509Certificate2 rootCert, string clientName)
    {
        using (RSA rsa = RSA.Create(RSA_CERT_KEY_LENGTH))
        {
            CertificateRequest req = new(
                $"CN={clientName}",
                rsa,
                HashAlgorithmName.SHA512,
                RSASignaturePadding.Pkcs1);

            req.CertificateExtensions.Add(
                        new X509BasicConstraintsExtension(false, false, 0, false));

            req.CertificateExtensions.Add(
                new X509KeyUsageExtension(
                    X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation,
                    false));

            req.CertificateExtensions.Add(
                new X509EnhancedKeyUsageExtension(
                    new OidCollection
                    {
                    new Oid("1.3.6.1.5.5.7.3.2") // Client Authentication
                    },
                    true));

            req.CertificateExtensions.Add(
                new X509SubjectKeyIdentifierExtension(req.PublicKey, false));

            ReadOnlyMemory<byte> serial = GenerateCertificateSerialNumber();
            using X509Certificate2 signedCert = req.Create(
                rootCert,
                DateTimeOffset.UtcNow.AddDays(-1),
                DateTimeOffset.UtcNow.AddDays(90),
                serial.Span);

            return signedCert.CopyWithPrivateKey(rsa);
        }
    }

    /// <summary>
    /// Generates a unique certificate serial number with version prefix and random component.
    /// </summary>
    /// <returns>An 8-byte serial number for the certificate.</returns>
    private static ReadOnlyMemory<byte> GenerateCertificateSerialNumber()
    {
        // Generate the serial number in the following format:
        //      First 4 bytes: version number (0x0001)
        //      Second 4 bytes: random
        Memory<byte> serial = new byte[8];
        Memory<byte> serialHigh = serial.Slice(0, 4);
        Memory<byte> serialLow = serial.Slice(4, 4);
        serialHigh.Span[0] = 0;
        serialHigh.Span[1] = 0;
        serialHigh.Span[2] = 0;
        serialHigh.Span[3] = 1;
        RandomNumberGenerator.Fill(serialLow.Span);

        return serial;
    }
}
