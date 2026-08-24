// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Notifications;

namespace Framlux.FleetManagement.Test.Services.Notifications;

/// <summary>
/// Tests for <see cref="EmailTemplates"/>. The invitation body used to live inside the Resend
/// transport; it is shared now, so both providers must render the same message and both must
/// encode caller-supplied names.
/// </summary>
public sealed class EmailTemplatesTests
{
    /// <summary>
    /// The rendered invitation must carry every piece of context the recipient needs to act.
    /// </summary>
    [Test]
    public async Task RenderInvitation_IncludesTenantInviterAndUrl()
    {
        string html = EmailTemplates.RenderInvitation("Acme Fleet", "Dana Reid", "https://app.example.com/accept?t=abc");

        await Assert.That(html).Contains("Acme Fleet");
        await Assert.That(html).Contains("Dana Reid");
        await Assert.That(html).Contains("https://app.example.com/accept?t=abc");
    }

    /// <summary>
    /// Tenant names are user-supplied, so an unencoded template would let a tenant name inject
    /// markup into an email sent to someone who is not yet a member.
    /// </summary>
    [Test]
    public async Task RenderInvitation_EncodesHtmlInTenantName()
    {
        string html = EmailTemplates.RenderInvitation("<script>alert(1)</script>", "Dana", "https://example.com");

        await Assert.That(html).DoesNotContain("<script>");
        await Assert.That(html).Contains("&lt;script&gt;");
    }

    /// <summary>
    /// Inviter display names come from the identity provider and are equally untrusted.
    /// </summary>
    [Test]
    public async Task RenderInvitation_EncodesHtmlInInviterName()
    {
        string html = EmailTemplates.RenderInvitation("Acme", "<b>Dana</b>", "https://example.com");

        await Assert.That(html).DoesNotContain("<b>Dana</b>");
    }

    /// <summary>
    /// The subject line has to identify the tenant, or a recipient with several invitations
    /// cannot tell them apart.
    /// </summary>
    [Test]
    public async Task InvitationSubject_NamesTheTenant()
    {
        await Assert.That(EmailTemplates.InvitationSubject("Acme Fleet")).Contains("Acme Fleet");
    }
}
