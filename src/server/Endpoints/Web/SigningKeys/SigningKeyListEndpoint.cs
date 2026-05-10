// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Server.Services.Machines;

namespace Framlux.FleetManagement.Server.Endpoints.Web.SigningKeys;

/// <summary>
/// Response for listing signing keys, including active count and limit.
/// </summary>
public sealed class SigningKeyListResponse
{
    /// <summary>
    /// The user's signing keys.
    /// </summary>
    public List<SigningKeyDto> Keys { get; set; } = [];

    /// <summary>
    /// Number of active (non-revoked) keys.
    /// </summary>
    public int ActiveCount { get; set; }

    /// <summary>
    /// Maximum number of active keys allowed.
    /// </summary>
    public int MaxKeys { get; set; } = ISigningKeyService.MaxActiveKeysPerUser;
}

/// <summary>
/// Lists all signing keys for the authenticated user within their active tenant.
/// </summary>
public sealed class SigningKeyListEndpoint : EndpointWithoutRequest<ApiResponse<SigningKeyListResponse>>
{
    private readonly ISigningKeyService _signingKeyService;

    /// <summary>
    /// Creates a new instance of the <see cref="SigningKeyListEndpoint"/> class.
    /// </summary>
    public SigningKeyListEndpoint(ISigningKeyService signingKeyService)
    {
        _signingKeyService = signingKeyService;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Get("/signing-keys");
        Policies("MachineAdmin");
        Version(1);
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(CancellationToken ct)
    {
        int? tenantId = TenantClaimHelper.GetTenantIdFromClaims(User, HttpContext);
        if (tenantId is null)
        {
            HttpContext.Response.StatusCode = 401;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<SigningKeyListResponse>.Error("Unable to identify tenant"), ct);

            return;
        }

        int? userId = TenantClaimHelper.GetUserIdFromClaims(User);
        if (userId is null)
        {
            HttpContext.Response.StatusCode = 401;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<SigningKeyListResponse>.Error("Unable to identify user"), ct);

            return;
        }

        List<UserSigningKey> keys = await _signingKeyService.ListKeysAsync(userId.Value, tenantId.Value, ct);
        int activeCount = keys.Count(k => k.RevokedAt is null);

        SigningKeyListResponse response = new()
        {
            Keys = keys.Select(k => new SigningKeyDto
            {
                Id = k.Id,
                Label = k.Label,
                PublicKey = k.PublicKey,
                Fingerprint = k.PublicKeyFingerprint,
                CreatedAt = k.CreatedAt,
                RevokedAt = k.RevokedAt,
            }).ToList(),
            ActiveCount = activeCount,
        };

        await Send.OkAsync(ApiResponse<SigningKeyListResponse>.Ok(response), cancellation: ct);
    }
}
