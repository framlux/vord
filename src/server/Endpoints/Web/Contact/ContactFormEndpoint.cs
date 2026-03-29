// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Contact;

/// <summary>
/// Contact form submission request.
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
        if (string.IsNullOrWhiteSpace(req.Name) || string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrWhiteSpace(req.Message))
        {
            HttpContext.Response.StatusCode = 400;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<object>.Error("Name, email, and message are required"), ct);

            return;
        }

        string maskedEmail = req.Email.IndexOf('@') > 0
            ? $"{req.Email[0]}***{req.Email[req.Email.IndexOf('@')..]}"
            : "***";
        _logger.LogInformation(
            "Contact form submission from {Name} ({Email}), Company: {Company}, Fleet Size: {FleetSize}",
            req.Name, maskedEmail, req.Company, req.FleetSize);

        await Send.OkAsync(ApiResponse<object>.Ok(new { }, "Thank you for your interest! We'll be in touch soon."), cancellation: ct);
    }
}
