// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Security.Claims;
using FastEndpoints;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Server.Options;
using Framlux.FleetManagement.Server.Services.Handlers;
using Framlux.FleetManagement.Server.Services.Infrastructure;
using Microsoft.Extensions.Options;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Invitations;

/// <summary>
/// Resends an invitation by revoking the old one and creating a new one.
/// </summary>
public sealed class InvitationResendEndpoint : EndpointWithoutRequest<ApiResponse<InvitationResponse>>
{
    private readonly IInvitationHandler _handler;
    private readonly AppOptions _appOptions;
    private readonly ILogger<InvitationResendEndpoint> _logger;

    /// <summary>
    /// Creates a new instance of the <see cref="InvitationResendEndpoint"/> class.
    /// </summary>
    public InvitationResendEndpoint(
        IInvitationHandler handler,
        IOptions<AppOptions> appOptions,
        ILogger<InvitationResendEndpoint> logger)
    {
        _handler = handler;
        _appOptions = appOptions.Value;
        _logger = logger;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Post("/invitations/{id}/resend");
        Policies("TenantAdmin");
        Version(1);
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(CancellationToken ct)
    {
        int invitationId = Route<int>("id");
        int? tenantId = TenantClaimHelper.GetTenantIdFromClaims(User, HttpContext);

        int? userId = TenantClaimHelper.GetUserIdFromClaims(User);
        if (userId is null)
        {
            HttpContext.Response.StatusCode = 401;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<InvitationResponse>.Error("Unable to identify user"), ct);

            return;
        }

        string inviterEmail = User.FindFirstValue(ClaimTypes.Email) ?? "A team member";
        string baseUrl = string.IsNullOrEmpty(_appOptions.BaseUrl) == false
            ? _appOptions.BaseUrl
            : $"{HttpContext.Request.Scheme}://{HttpContext.Request.Host}";

        ServiceResult<InvitationResendResult> result = await _handler.ResendAsync(invitationId, tenantId, userId.Value, inviterEmail, baseUrl, ct);

        if (result.IsNotFound)
        {
            HttpContext.Response.StatusCode = 404;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<InvitationResponse>.Error("Invitation not found"), ct);

            return;
        }

        if (result.IsSuccess == false)
        {
            HttpContext.Response.StatusCode = result.StatusCode;
            await HttpContext.Response.WriteAsJsonAsync(ApiResponse<InvitationResponse>.Error(result.Data?.ErrorMessage ?? "Unknown error"), ct);

            return;
        }

        _logger.LogInformation("Invitation resent for email {Email} in tenant {TenantId}", result.Data!.Email, tenantId);

        InvitationResponse response = new()
        {
            Id = result.Data!.Id,
            Email = result.Data!.Email,
            Token = result.Data!.Token,
            AcceptUrl = result.Data!.AcceptUrl,
            ExpiresAt = result.Data!.ExpiresAt,
            Status = result.Data!.Status,
        };

        await Send.OkAsync(ApiResponse<InvitationResponse>.Ok(response), cancellation: ct);
    }
}
