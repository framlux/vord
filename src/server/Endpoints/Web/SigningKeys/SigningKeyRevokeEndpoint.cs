// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Security.Claims;
using FastEndpoints;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Services.Core.Auth;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Framlux.FleetManagement.Services.Core.Machines;

namespace Framlux.FleetManagement.Server.Endpoints.Web.SigningKeys;

/// <summary>
/// Revokes a signing key by ID.
/// </summary>
public sealed class SigningKeyRevokeEndpoint : EndpointWithoutRequest<ApiResponse<bool>>
{
    private readonly ISigningKeyService _signingKeyService;
    private readonly ITenantContext _tenantContext;

    /// <summary>
    /// Creates a new instance of the <see cref="SigningKeyRevokeEndpoint"/> class.
    /// </summary>
    public SigningKeyRevokeEndpoint(ISigningKeyService signingKeyService, ITenantContext tenantContext)
    {
        _signingKeyService = signingKeyService;
        _tenantContext = tenantContext;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Delete("/signing-keys/{id}");
        Policies("MachineAdmin");
        Tags(EndpointTags.RequiresTenant);
        Version(1);
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(CancellationToken ct)
    {
        int keyId = Route<int>("id");

        int tenantId = _tenantContext.RequireTenantId();

        int? userId = _tenantContext.UserId;
        if (userId is null)
        {
            await HttpContext.SendApiErrorAsync(401, "Unable to identify user", ct);

            return;
        }

        // Check if the user is a TenantAdmin or GlobalAdmin for permission to revoke others' keys.
        bool isAdmin = AuthClaims.IsUserGlobalAdmin(User);
        bool isTenantAdmin = User.FindAll(ClaimTypes.Role)
            .Any(c => c.Value.EndsWith(":1")); // :1 = TenantAdmin role

        ServiceResult<bool> result = await _signingKeyService.RevokeKeyAsync(
            keyId, userId.Value, tenantId, isAdmin || isTenantAdmin, ct);

        if (result.IsNotFound)
        {
            await HttpContext.SendApiErrorAsync(404, "Signing key not found", ct);

            return;
        }

        if (result.IsSuccess == false)
        {
            await HttpContext.SendApiErrorAsync(result.StatusCode, "Cannot revoke this key", ct);

            return;
        }

        await Send.OkAsync(ApiResponse<bool>.Ok(true, "Key revoked"), cancellation: ct);
    }
}
