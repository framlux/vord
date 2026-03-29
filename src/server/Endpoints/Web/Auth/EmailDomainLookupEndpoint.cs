// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.ComponentModel.DataAnnotations;
using System.Net.Mail;
using FastEndpoints;
using Framlux.FleetManagement.Database.Cache;
using Framlux.FleetManagement.Database.Models;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Auth;

/// <summary>
/// Request DTO for email domain SSO lookup.
/// </summary>
public sealed class EmailDomainLookupRequest
{
    /// <summary>
    /// The user's email address.
    /// </summary>
    [Required]
    public string Email { get; set; } = string.Empty;
}

/// <summary>
/// Response DTO for email domain SSO lookup.
/// </summary>
public sealed class EmailDomainLookupResponse
{
    /// <summary>
    /// The tenant ID that owns the SSO configuration for this email domain.
    /// </summary>
    public int TenantId { get; set; }
}

/// <summary>
/// Looks up the SSO provider for a given email domain.
/// </summary>
public sealed class EmailDomainLookupEndpoint : Endpoint<EmailDomainLookupRequest, ApiResponse<EmailDomainLookupResponse>>
{
    private readonly IDatabaseCache _dbCache;

    /// <summary>
    /// Creates a new instance of the <see cref="EmailDomainLookupEndpoint"/> class.
    /// </summary>
    public EmailDomainLookupEndpoint(IDatabaseCache dbCache)
    {
        _dbCache = dbCache;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Post("/auth/email-lookup");
        AllowAnonymous();
        Version(1);
        Options(x => x.RequireRateLimiting("login"));
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(EmailDomainLookupRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Email))
        {
            await Send.OkAsync(ApiResponse<EmailDomainLookupResponse>.Error("A valid email address is required"), cancellation: ct);

            return;
        }

        MailAddress? mailAddress;
        try
        {
            mailAddress = new MailAddress(req.Email.Trim());
        }
        catch (FormatException)
        {
            await Send.OkAsync(ApiResponse<EmailDomainLookupResponse>.Error("A valid email address is required"), cancellation: ct);

            return;
        }

        string domain = mailAddress.Host.ToLowerInvariant();
        if ((domain.Length < 3) || domain.Contains(' ') || (domain.IndexOf('.') < 1))
        {
            await Send.OkAsync(ApiResponse<EmailDomainLookupResponse>.Error("A valid email address is required"), cancellation: ct);

            return;
        }

        TenantOidcConfiguration? config = await _dbCache.GetTenantOidcConfigurationByEmailDomainAsync(domain, ct);
        if (config is null)
        {
            await Send.OkAsync(ApiResponse<EmailDomainLookupResponse>.Error("No SSO provider found for this email domain"), cancellation: ct);

            return;
        }

        await Send.OkAsync(ApiResponse<EmailDomainLookupResponse>.Ok(new EmailDomainLookupResponse { TenantId = config.TenantId }), cancellation: ct);
    }
}
