// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Security.Cryptography;
using System.Text;
using Framlux.FleetManagement.Services.Core.Options;
using Grpc.Core;

namespace Framlux.FleetManagement.Server.Auth;

/// <summary>
/// Shared validator for the internal gRPC API key. Replaces the per-service
/// <c>string.Equals</c> compares with a constant-time SHA-256 hash compare via
/// <see cref="CryptographicOperations.FixedTimeEquals"/>. Supports key rotation by allowing the
/// caller to pass <c>x-internal-kid</c>; the matching secret is looked up in
/// <see cref="InternalApiOptions.Keys"/>, falling back to the legacy <see cref="InternalApiOptions.Key"/>.
/// </summary>
public static class InternalApiKeyValidator
{
    /// <summary>Header name carrying the API key.</summary>
    public const string KeyHeader = "x-internal-key";

    /// <summary>Header name carrying the optional key id (kid) for rotation.</summary>
    public const string KidHeader = "x-internal-kid";

    /// <summary>The implicit kid used when the legacy <see cref="InternalApiOptions.Key"/> is consulted.</summary>
    public const string DefaultKid = "default";

    /// <summary>
    /// Validates the request's API key against the configured secret(s). Throws
    /// <see cref="RpcException"/> with <see cref="StatusCode.Unavailable"/> when no secret is
    /// configured and <see cref="StatusCode.Unauthenticated"/> when the key is missing or wrong.
    /// </summary>
    /// <param name="context">The gRPC server call context.</param>
    /// <param name="options">The configured internal API options.</param>
    public static void Validate(ServerCallContext context, InternalApiOptions options)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(options);

        string providedKid = context.RequestHeaders.Get(KidHeader)?.Value ?? DefaultKid;
        string? configuredSecret = ResolveConfiguredSecret(options, providedKid);
        if (string.IsNullOrEmpty(configuredSecret))
        {
            throw new RpcException(new Status(StatusCode.Unavailable, "Internal API is not configured"));
        }

        string providedKey = context.RequestHeaders.Get(KeyHeader)?.Value ?? string.Empty;
        if (FixedTimeHashCompare(providedKey, configuredSecret) == false)
        {
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Unauthorized"));
        }
    }

    /// <summary>
    /// Returns the configured secret for the supplied kid, or the legacy
    /// <see cref="InternalApiOptions.Key"/> when <paramref name="kid"/> equals
    /// <see cref="DefaultKid"/> and no explicit entry is present.
    /// </summary>
    internal static string? ResolveConfiguredSecret(InternalApiOptions options, string kid)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(kid);

        if (options.Keys.TryGetValue(kid, out string? secretFromMap) && (string.IsNullOrEmpty(secretFromMap) == false))
        {
            return secretFromMap;
        }

        if (string.Equals(kid, DefaultKid, StringComparison.Ordinal) && (string.IsNullOrEmpty(options.Key) == false))
        {
            return options.Key;
        }

        return null;
    }

    /// <summary>
    /// SHA-256 hashes both sides and compares the 32-byte digests with
    /// <see cref="CryptographicOperations.FixedTimeEquals"/>. Hashing length-normalizes the
    /// inputs so the compare cost is constant regardless of where mismatches occur.
    /// </summary>
    internal static bool FixedTimeHashCompare(string a, string b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        byte[] hashA = SHA256.HashData(Encoding.UTF8.GetBytes(a));
        byte[] hashB = SHA256.HashData(Encoding.UTF8.GetBytes(b));

        return CryptographicOperations.FixedTimeEquals(hashA, hashB);
    }
}
