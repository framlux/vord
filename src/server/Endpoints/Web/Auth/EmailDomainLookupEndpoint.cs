// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.ComponentModel.DataAnnotations;
using System.Net.Mail;
using FastEndpoints;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Server.Auth;
using Microsoft.AspNetCore.DataProtection;

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
/// Response DTO for email domain SSO lookup. Deliberately opaque: it reveals only whether SSO is
/// available and, when it is, an opaque challenge slug the client can use to start the flow —
/// never the numeric tenant id, and never a different shape on hit vs miss.
/// </summary>
public sealed class EmailDomainLookupResponse
{
    /// <summary>Whether a usable SSO provider exists for the submitted domain.</summary>
    public bool SsoAvailable { get; set; }

    /// <summary>An opaque identifier used to initiate the SSO challenge, or null when unavailable.</summary>
    public string? Slug { get; set; }
}

/// <summary>
/// Looks up the SSO provider for a given email domain.
/// </summary>
public sealed class EmailDomainLookupEndpoint : Endpoint<EmailDomainLookupRequest, ApiResponse<EmailDomainLookupResponse>>
{
    private readonly ITenantRepository _tenantRepository;
    private readonly IDataProtectionProvider _dataProtectionProvider;

    /// <summary>
    /// Creates a new instance of the <see cref="EmailDomainLookupEndpoint"/> class.
    /// </summary>
    public EmailDomainLookupEndpoint(
        ITenantRepository tenantRepository,
        IDataProtectionProvider dataProtectionProvider)
    {
        _tenantRepository = tenantRepository;
        _dataProtectionProvider = dataProtectionProvider;
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
        EmailDomainLookupResponse unavailable = new() { SsoAvailable = false, Slug = null };

        string? domain = ExtractDomain(req.Email);
        if (domain is null)
        {
            await Send.OkAsync(ApiResponse<EmailDomainLookupResponse>.Ok(unavailable), cancellation: ct);

            return;
        }

        TenantOidcConfiguration? config = await _tenantRepository.GetTenantOidcConfigurationByEmailDomainAsync(domain, ct);
        if ((config is null) || (config.IsEnabled == false))
        {
            await Send.OkAsync(ApiResponse<EmailDomainLookupResponse>.Ok(unavailable), cancellation: ct);

            return;
        }

        IDataProtector protector = _dataProtectionProvider.CreateProtector(TenantSsoSlug.Purpose);
        EmailDomainLookupResponse available = new()
        {
            SsoAvailable = true,
            Slug = TenantSsoSlug.Build(protector, config.TenantId),
        };
        await Send.OkAsync(ApiResponse<EmailDomainLookupResponse>.Ok(available), cancellation: ct);
    }

    /// <summary>
    /// Extracts and validates the lowercase host portion of an email address, returning <c>null</c>
    /// when the input is missing or not a plausible domain.
    /// </summary>
    /// <param name="email">The raw email address from the request.</param>
    /// <returns>The normalized domain, or <c>null</c> when the email is invalid.</returns>
    internal static string? ExtractDomain(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return null;
        }

        MailAddress mailAddress;
        try
        {
            mailAddress = new MailAddress(email.Trim());
        }
        catch (FormatException)
        {
            return null;
        }

        string domain = mailAddress.Host.ToLowerInvariant();
        if ((domain.Length < 3) || domain.Contains(' ') || (domain.IndexOf('.') < 1))
        {
            return null;
        }

        return domain;
    }
}
