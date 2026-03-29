// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Security.Claims;
using FastEndpoints;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Server.Services.Infrastructure;
using Framlux.FleetManagement.Server.Services.Machines;

namespace Framlux.FleetManagement.Server.Endpoints.Web.SigningKeys;

/// <summary>
/// Request to register a new signing key.
/// </summary>
public sealed class SigningKeyRegisterRequest
{
    /// <summary>
    /// User-chosen label for the key (e.g., "Work MacBook").
    /// </summary>
    public required string Label { get; set; }

    /// <summary>
    /// Base64-encoded 32-byte Ed25519 public key.
    /// </summary>
    public required string PublicKey { get; set; }
}

/// <summary>
/// Response DTO for a signing key.
/// </summary>
public sealed class SigningKeyDto
{
    /// <summary>
    /// The signing key ID.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// User-chosen label.
    /// </summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>
    /// Base64-encoded public key.
    /// </summary>
    public string PublicKey { get; set; } = string.Empty;

    /// <summary>
    /// SHA-256 fingerprint of the public key.
    /// </summary>
    public string Fingerprint { get; set; } = string.Empty;

    /// <summary>
    /// When the key was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// When the key was revoked, if applicable.
    /// </summary>
    public DateTimeOffset? RevokedAt { get; set; }
}

/// <summary>
/// Registers a new Ed25519 signing key for the authenticated user.
/// </summary>
public sealed class SigningKeyRegisterEndpoint : Endpoint<SigningKeyRegisterRequest, ApiResponse<SigningKeyDto>>
{
    private readonly ISigningKeyService _signingKeyService;

    /// <summary>
    /// Creates a new instance of the <see cref="SigningKeyRegisterEndpoint"/> class.
    /// </summary>
    public SigningKeyRegisterEndpoint(ISigningKeyService signingKeyService)
    {
        _signingKeyService = signingKeyService;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Post("/signing-keys");
        Policies("MachineAdmin");
        Version(1);
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(SigningKeyRegisterRequest req, CancellationToken ct)
    {
        int? tenantId = TenantClaimHelper.GetTenantIdFromClaims(User, HttpContext);
        if (tenantId is null)
        {
            await Send.NotFoundAsync(ct);

            return;
        }

        string? userIdStr = User.FindFirstValue(ClaimTypes.Actor);
        int userId = int.TryParse(userIdStr, out int uid) ? uid : 0;

        ServiceResult<UserSigningKey> result = await _signingKeyService.RegisterKeyAsync(
            userId, tenantId.Value, req.Label, req.PublicKey, ct);

        if (result.StatusCode == 409)
        {
            HttpContext.Response.StatusCode = 409;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<SigningKeyDto>.Error("Maximum active signing keys reached (5 per user per tenant)"), ct);

            return;
        }

        if (result.IsSuccess == false)
        {
            HttpContext.Response.StatusCode = result.StatusCode;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<SigningKeyDto>.Error("Invalid public key"), ct);

            return;
        }

        UserSigningKey key = result.Data!;

        await Send.OkAsync(ApiResponse<SigningKeyDto>.Ok(new SigningKeyDto
        {
            Id = key.Id,
            Label = key.Label,
            PublicKey = key.PublicKey,
            Fingerprint = key.PublicKeyFingerprint,
            CreatedAt = key.CreatedAt,
            RevokedAt = key.RevokedAt,
        }), cancellation: ct);
    }
}
