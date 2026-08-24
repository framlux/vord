// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Net;

namespace Framlux.FleetManagement.Services.Core.Notifications;

/// <summary>
/// Renders email bodies independently of the transport that sends them, so the Resend and SMTP
/// providers deliver identical messages. Alert bodies are built by AlertEmailContent and passed in
/// already rendered; only invitations are composed here.
/// </summary>
public static class EmailTemplates
{
    /// <summary>
    /// Builds the subject line for a tenant invitation.
    /// </summary>
    /// <param name="tenantName">The tenant the recipient is being invited to.</param>
    /// <returns>The subject line.</returns>
    public static string InvitationSubject(string tenantName)
    {
        return $"You've been invited to join {tenantName} on Framlux Vord";
    }

    /// <summary>
    /// Builds the HTML body for a tenant invitation. Tenant and inviter names are supplied by
    /// users, so both are HTML-encoded before substitution.
    /// </summary>
    /// <param name="tenantName">The tenant the recipient is being invited to.</param>
    /// <param name="inviterName">The display name of the member who sent the invitation.</param>
    /// <param name="acceptUrl">The absolute URL that accepts the invitation.</param>
    /// <returns>The rendered HTML body.</returns>
    public static string RenderInvitation(string tenantName, string inviterName, string acceptUrl)
    {
        string encodedTenant = WebUtility.HtmlEncode(tenantName);
        string encodedInviter = WebUtility.HtmlEncode(inviterName);
        string encodedUrl = WebUtility.HtmlEncode(acceptUrl);

        return $"""
            <div style="font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; max-width: 560px; margin: 0 auto; padding: 40px 20px;">
                <h2 style="color: #1a1a1a; margin-bottom: 8px;">You've been invited to join {encodedTenant}</h2>
                <p style="color: #666; font-size: 15px; line-height: 1.5;">
                    {encodedInviter} has invited you to join <strong>{encodedTenant}</strong> on Framlux Vord.
                </p>
                <div style="margin: 32px 0;">
                    <a href="{encodedUrl}" style="display: inline-block; background-color: #6366f1; color: #ffffff; text-decoration: none; padding: 12px 32px; border-radius: 8px; font-weight: 600; font-size: 15px;">
                        Accept Invitation
                    </a>
                </div>
                <p style="color: #999; font-size: 13px; line-height: 1.5;">
                    This invitation expires in 7 days. If you did not expect this email, you can safely ignore it.
                </p>
                <hr style="border: none; border-top: 1px solid #eee; margin: 32px 0;" />
                <p style="color: #bbb; font-size: 12px;">Framlux Vord &mdash; Fleet Monitoring</p>
            </div>
            """;
    }
}
