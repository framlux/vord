// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Security.Cryptography;
using System.Text;
using FastEndpoints;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Contact;

/// <summary>
/// Contact form submission request. Field-level validation lives in
/// <see cref="ContactFormValidator"/> (length caps + CRLF rejection); the handler trusts the
/// validator and only emits SCALAR identifiers to logs to prevent log injection.
/// </summary>
public sealed class ContactFormRequest
{
    /// <summary>Contact name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Contact email.</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>Company name.</summary>
    public string Company { get; set; } = string.Empty;

    /// <summary>Fleet size estimate.</summary>
    public string FleetSize { get; set; } = string.Empty;

    /// <summary>Message body.</summary>
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Handles contact form submissions for Team tier inquiries.
/// </summary>
public sealed class ContactFormEndpoint : Endpoint<ContactFormRequest, ApiResponse<object>>
{
    private readonly ILogger<ContactFormEndpoint> _logger;

    /// <summary>
    /// Creates a new instance of the <see cref="ContactFormEndpoint"/> class.
    /// </summary>
    public ContactFormEndpoint(ILogger<ContactFormEndpoint> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Post("/contact");
        AllowAnonymous();
        Version(1);
        Options(x => x.RequireRateLimiting("login"));
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(ContactFormRequest req, CancellationToken ct)
    {
        // Log scalar identifiers only. The validator strips CRLF from Name/Company/FleetSize
        // before we get here, but the field bodies are still untrusted user input — keep them
        // out of structured-log message templates entirely. A short SHA-256 fingerprint of the
        // email provides correlation across multiple submissions from the same address without
        // storing the address in the log line.
        string emailFingerprint = ComputeEmailFingerprint(req.Email);
        _logger.LogInformation(
            "Contact form submitted (email-fp={EmailFingerprint}, nameLen={NameLength}, companyLen={CompanyLength}, fleetSizeLen={FleetSizeLength}, messageLen={MessageLength})",
            emailFingerprint,
            req.Name.Length,
            req.Company.Length,
            req.FleetSize.Length,
            req.Message.Length);

        await Send.OkAsync(ApiResponse<object>.Ok(new { }, "Thank you for your interest! We'll be in touch soon."), cancellation: ct);
    }

    /// <summary>
    /// Returns a short (16-hex-char = 64-bit) SHA-256 fingerprint of the lowercased trimmed
    /// email. Adequate for correlating multiple submissions from the same address without
    /// exposing the address itself in the log stream.
    /// </summary>
    internal static string ComputeEmailFingerprint(string email)
    {
        ArgumentNullException.ThrowIfNull(email);
        string normalized = email.Trim().ToLowerInvariant();
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));

        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }
}
