// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Server.Endpoints.Web.Models.Users;
using Framlux.FleetManagement.Server.Services.Handlers;
using Framlux.FleetManagement.Server.Services.Infrastructure;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Auth;

/// <summary>
/// Returns the currently authenticated user.
/// </summary>
public sealed class AuthMeEndpoint : EndpointWithoutRequest<ApiResponse<UserDto>>
{
    private readonly IAuthMeHandler _handler;
    private readonly ILogger<AuthMeEndpoint> _logger;

    /// <summary>
    /// Creates a new instance of the <see cref="AuthMeEndpoint"/> class.
    /// </summary>
    /// <param name="handler">The auth me handler instance.</param>
    /// <param name="logger">The logger instance.</param>
    public AuthMeEndpoint(IAuthMeHandler handler, ILogger<AuthMeEndpoint> logger)
    {
        _handler = handler;
        _logger = logger;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Get("/auth/me");
        Version(1);
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(CancellationToken ct)
    {
        if (User?.Identity?.IsAuthenticated != true)
        {
            await Send.UnauthorizedAsync(ct);

            return;
        }

        UserDto dto = UserDto.FromPrincipal(User, _logger);
        ServiceResult<AuthMeResult> result = await _handler.GetCurrentUserAsync(dto.UniqueId, ct);

        if (result.IsNotFound)
        {
            _logger.LogWarning("Authenticated user {UniqueId} not found in database", dto.UniqueId);
            await Send.NotFoundAsync(ct);

            return;
        }

        if (result.IsSuccess == false)
        {
            HttpContext.Response.StatusCode = result.StatusCode;

            return;
        }

        dto.Id = result.Data!.UserId;
        dto.IsGlobalAdmin = result.Data!.IsGlobalAdmin;
        dto.Tenants.AddRange(result.Data!.Tenants);
        dto.NeedsOnboarding = result.Data!.NeedsOnboarding;
        dto.ActiveTenantId = TenantClaimHelper.GetTenantIdFromClaims(User, HttpContext);

        await Send.OkAsync(ApiResponse<UserDto>.Ok(dto), cancellation: ct);
    }
}
