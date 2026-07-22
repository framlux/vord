// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Security.Claims;
using FastEndpoints;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Services.Core.Handlers;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Framlux.FleetManagement.Services.Core.Options;
using Microsoft.Extensions.Options;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Invitations;

/// <summary>
/// Resends an invitation by revoking the old one and creating a new one.
/// </summary>
public sealed class InvitationResendEndpoint : EndpointWithoutRequest<ApiResponse<InvitationResponse>>
{
    private readonly InvitationHandler _handler;
    private readonly AppOptions _appOptions;
    private readonly ILogger<InvitationResendEndpoint> _logger;
    private readonly ITenantContext _tenantContext;

    /// <summary>
    /// Creates a new instance of the <see cref="InvitationResendEndpoint"/> class.
    /// </summary>
    public InvitationResendEndpoint(
        InvitationHandler handler,
        IOptions<AppOptions> appOptions,
        ILogger<InvitationResendEndpoint> logger,
        ITenantContext tenantContext)
    {
        _handler = handler;
        _appOptions = appOptions.Value;
        _logger = logger;
        _tenantContext = tenantContext;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Post("/invitations/{id}/resend");
        Policies(AuthorizationPolicies.TenantAdmin);
        Version(1);
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(CancellationToken ct)
    {
        int invitationId = Route<int>("id");
        int? tenantId = _tenantContext.TenantId;

        int? userId = _tenantContext.UserId;
        if (userId is null)
        {
            await HttpContext.SendApiErrorAsync(401, "Unable to identify user", ct);

            return;
        }

        string inviterEmail = User.FindFirstValue(ClaimTypes.Email) ?? "A team member";
        string baseUrl = _appOptions.BaseUrl;

        ServiceResult<InvitationDeliveryResult> result = await _handler.ResendAsync(invitationId, tenantId, userId.Value, inviterEmail, baseUrl, ct);

        if (result.IsNotFound)
        {
            await HttpContext.SendApiErrorAsync(404, "Invitation not found", ct);

            return;
        }

        if (result.IsSuccess == false)
        {
            await HttpContext.SendApiErrorAsync(result.StatusCode, result.Data?.ErrorMessage ?? "Unknown error", ct);

            return;
        }

        _logger.LogInformation("Invitation resent for email {Email} in tenant {TenantId}", result.Data!.Email, tenantId);

        InvitationResponse response = InvitationResponseMapper.ToResponse(result.Data!);

        await Send.OkAsync(ApiResponse<InvitationResponse>.Ok(response), cancellation: ct);
    }
}
