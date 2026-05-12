// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Notifications;

namespace Framlux.FleetManagement.Test.Infrastructure;

/// <summary>
/// No-op <see cref="IEmailService"/> for testing that records sent emails.
/// </summary>
public sealed class InMemoryEmailService : IEmailService
{
    /// <summary>
    /// Record of a sent email for test assertions.
    /// </summary>
    public sealed record SentEmail(string ToEmail, string TenantName, string InviterName, string AcceptUrl);

    /// <summary>
    /// All emails sent during the test.
    /// </summary>
    public List<SentEmail> SentEmails { get; } = [];

    /// <inheritdoc/>
    public Task<bool> SendInvitationEmailAsync(string toEmail, string tenantName, string inviterName, string acceptUrl, CancellationToken ct)
    {
        SentEmails.Add(new SentEmail(toEmail, tenantName, inviterName, acceptUrl));

        return Task.FromResult(true);
    }
}
