// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Auth;

/// <summary>
/// Redirects to the frontend login page for provider selection.
/// </summary>
public sealed class AuthLoginEndpoint : EndpointWithoutRequest<ApiResponse<object>>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get("/login");
        AllowAnonymous();
        Version(1);
        Options(x => x.RequireRateLimiting("login"));
    }

    /// <inheritdoc />
    public override async Task HandleAsync(CancellationToken ct)
    {
        string returnUrl = Query<string?>("returnUrl", isRequired: false) ?? "/dashboard";

        // Prevent open redirect: only allow relative paths.
        if (Uri.IsWellFormedUriString(returnUrl, UriKind.Relative) == false ||
            returnUrl.StartsWith("//", StringComparison.Ordinal) ||
            returnUrl.StartsWith("\\", StringComparison.Ordinal) ||
            returnUrl.StartsWith("/", StringComparison.Ordinal) == false)
        {
            returnUrl = "/dashboard";
        }

        HttpContext.MarkResponseStart();

        HttpContext.Response.Redirect($"/auth/login?returnUrl={Uri.EscapeDataString(returnUrl)}");

        await Task.CompletedTask;
    }
}
