// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Server.Options;
using Framlux.FleetManagement.Server.Services.Handlers;
using Framlux.FleetManagement.Server.Services.Infrastructure;
using Microsoft.Extensions.Options;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Invitations;

/// <summary>
/// Request model for creating an invitation.
/// </summary>
public sealed class CreateInvitationRequest
{
    /// <summary>
    /// The email address to invite.
    /// </summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>
    /// The role to assign to the invited user. Defaults to Viewer if not specified.
    /// </summary>
    public string? Role { get; set; }
}

/// <summary>
/// Response model for a created invitation.
/// </summary>
public sealed class InvitationResponse
{
    /// <summary>
    /// The invitation ID.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// The invited email address.
    /// </summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>
    /// The invitation token.
    /// </summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>
    /// The URL to accept the invitation.
    /// </summary>
    public string AcceptUrl { get; set; } = string.Empty;

    /// <summary>
    /// When the invitation expires.
    /// </summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>
    /// The invitation status.
    /// </summary>
    public string Status { get; set; } = string.Empty;
}

/// <summary>
/// Creates a new tenant invitation and sends an email.
/// </summary>
public sealed class InvitationCreateEndpoint : Endpoint<CreateInvitationRequest, ApiResponse<InvitationResponse>>
{
    private readonly IInvitationHandler _handler;
    private readonly AppOptions _appOptions;
    private readonly ILogger<InvitationCreateEndpoint> _logger;

    /// <summary>
    /// Creates a new instance of the <see cref="InvitationCreateEndpoint"/> class.
    /// </summary>
    public InvitationCreateEndpoint(
        IInvitationHandler handler,
        IOptions<AppOptions> appOptions,
        ILogger<InvitationCreateEndpoint> logger)
    {
        _handler = handler;
        _appOptions = appOptions.Value;
        _logger = logger;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Post("/invitations");
        Policies("TenantAdmin");
        Version(1);
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(CreateInvitationRequest req, CancellationToken ct)
    {
        int? tenantId = TenantClaimHelper.GetTenantIdFromClaims(User, HttpContext);

        int? userId = TenantClaimHelper.GetUserIdFromClaims(User);
        if (userId is null)
        {
            HttpContext.Response.StatusCode = 401;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<InvitationResponse>.Error("Unable to identify user"), ct);

            return;
        }

        string baseUrl = string.IsNullOrEmpty(_appOptions.BaseUrl) == false
            ? _appOptions.BaseUrl
            : $"{HttpContext.Request.Scheme}://{HttpContext.Request.Host}";

        ServiceResult<InvitationCreateResult> result = await _handler.CreateAsync(req.Email, req.Role, tenantId, userId.Value, baseUrl, ct);

        if (result.IsSuccess == false)
        {
            HttpContext.Response.StatusCode = result.StatusCode;
            await HttpContext.Response.WriteAsJsonAsync(ApiResponse<InvitationResponse>.Error(result.Data?.ErrorMessage ?? "Unknown error"), ct);

            return;
        }

        _logger.LogInformation("Invitation created for email {Email} in tenant {TenantId} by user {UserId}", req.Email, tenantId, userId.Value);

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
