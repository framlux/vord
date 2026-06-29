// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Security.Claims;
using FastEndpoints;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Services.Core.Models.Users;
using Framlux.FleetManagement.Services.Core.Handlers;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Microsoft.AspNetCore.Antiforgery;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Auth;

/// <summary>
/// Returns the currently authenticated user.
/// </summary>
public sealed class AuthMeEndpoint : EndpointWithoutRequest<ApiResponse<UserDto>>
{
    private readonly IAuthMeHandler _handler;
    private readonly IAntiforgery _antiforgery;
    private readonly ILogger<AuthMeEndpoint> _logger;
    private readonly ITenantContext _tenantContext;

    /// <summary>
    /// Creates a new instance of the <see cref="AuthMeEndpoint"/> class.
    /// </summary>
    /// <param name="handler">The auth me handler instance.</param>
    /// <param name="antiforgery">The antiforgery service used to issue the double-submit token.</param>
    /// <param name="logger">The logger instance.</param>
    /// <param name="tenantContext">Provides the resolved tenant and user identity for the current request.</param>
    public AuthMeEndpoint(IAuthMeHandler handler, IAntiforgery antiforgery, ILogger<AuthMeEndpoint> logger, ITenantContext tenantContext)
    {
        _handler = handler;
        _antiforgery = antiforgery;
        _logger = logger;
        _tenantContext = tenantContext;
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
            HttpContext.Response.StatusCode = 401;
            await HttpContext.Response.WriteAsJsonAsync(ApiResponse<UserDto>.Error("Unauthorized"), ct);

            return;
        }

        UserDto dto = UserDto.FromPrincipal(User, _logger);

        // The provider claim is minted alongside the principal. Resolve identity on the
        // (provider, subject) pair; absent, unparseable, or out-of-range values fall back to Unknown.
        AuthProviderType authProvider = (Enum.TryParse(User.FindFirstValue(SecurityStampClaims.AuthProviderClaim), out AuthProviderType parsedProvider) && Enum.IsDefined(parsedProvider))
            ? parsedProvider
            : AuthProviderType.Unknown;

        ServiceResult<AuthMeResult> result = await _handler.GetCurrentUserAsync(authProvider, dto.UniqueId, ct);

        if (result.IsNotFound)
        {
            _logger.LogWarning("Authenticated user {UniqueId} not found in database", dto.UniqueId);
            HttpContext.Response.StatusCode = 404;
            await HttpContext.Response.WriteAsJsonAsync(ApiResponse<UserDto>.Error("User not found"), ct);

            return;
        }

        if (result.IsSuccess == false)
        {
            HttpContext.Response.StatusCode = result.StatusCode;
            await HttpContext.Response.WriteAsJsonAsync(ApiResponse<UserDto>.Error("An error occurred"), ct);

            return;
        }

        dto.Id = result.Data!.UserId;
        dto.IsGlobalAdmin = result.Data!.IsGlobalAdmin;
        dto.Tenants.AddRange(result.Data!.Tenants);
        dto.NeedsOnboarding = result.Data!.NeedsOnboarding;
        dto.ActiveTenantId = _tenantContext.TenantId;

        // Issue the double-submit antiforgery pair on this authenticated GET: GetAndStoreTokens
        // writes the antiforgery cookie onto the response and returns the matching request token.
        // The client echoes the request token in the X-CSRF-TOKEN header on state-changing
        // requests, which the server validates against the cookie. This is the single issuance
        // path for the cookie-authenticated web flow.
        AntiforgeryTokenSet tokens = _antiforgery.GetAndStoreTokens(HttpContext);
        dto.CsrfToken = tokens.RequestToken;

        await Send.OkAsync(ApiResponse<UserDto>.Ok(dto), cancellation: ct);
    }
}
