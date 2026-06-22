// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Notifications;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Framlux.FleetManagement.Test.Services.Notifications;

/// <summary>
/// Tests for <see cref="SendInvitationEmailJob"/>.
/// </summary>
public sealed class SendInvitationEmailJobTests
{
    // ========== Constructor guard tests ==========

    [Test]
    public async Task Constructor_NullEmailService_ThrowsArgumentNullException()
    {
        ArgumentNullException? ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            SendInvitationEmailJob _ = new(null!, NullLogger<SendInvitationEmailJob>.Instance);

            return Task.CompletedTask;
        });

        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.ParamName).IsEqualTo("emailService");
    }

    [Test]
    public async Task Constructor_NullLogger_ThrowsArgumentNullException()
    {
        IEmailService emailService = Substitute.For<IEmailService>();

        ArgumentNullException? ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            SendInvitationEmailJob _ = new(emailService, null!);

            return Task.CompletedTask;
        });

        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.ParamName).IsEqualTo("logger");
    }

    // ========== SendAsync — happy path ==========

    [Test]
    public async Task SendAsync_EmailServiceReturnsTrue_CompletesWithoutThrowing()
    {
        // Intent: when Resend accepts the email the job must complete cleanly so Hangfire
        // marks it succeeded and does not consume a retry attempt.
        IEmailService emailService = Substitute.For<IEmailService>();
        emailService.SendInvitationEmailAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);

        SendInvitationEmailJob job = new(emailService, NullLogger<SendInvitationEmailJob>.Instance);

        await job.SendAsync("user@example.com", "Acme Corp", "Alice", "https://app.test/accept?token=abc", CancellationToken.None);

        // No exception means the job completed successfully.
        await emailService.Received(1).SendInvitationEmailAsync(
            "user@example.com", "Acme Corp", "Alice", "https://app.test/accept?token=abc", CancellationToken.None);
    }

    // ========== SendAsync — failure path (Hangfire retry trigger) ==========

    [Test]
    public async Task SendAsync_EmailServiceReturnsFalse_ThrowsInvalidOperationException()
    {
        // Intent: IEmailService.SendInvitationEmailAsync returns false on failure (logs-and-returns
        // pattern). The job must convert that into a throw so Hangfire's AutomaticRetryAttribute
        // retries the job. Without the throw, Hangfire would mark the job succeeded and the
        // invitation email would be silently lost.
        IEmailService emailService = Substitute.For<IEmailService>();
        emailService.SendInvitationEmailAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(false);

        SendInvitationEmailJob job = new(emailService, NullLogger<SendInvitationEmailJob>.Instance);

        InvalidOperationException? ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            job.SendAsync("user@example.com", "Acme Corp", "Alice", "https://app.test/accept?token=abc", CancellationToken.None));

        await Assert.That(ex).IsNotNull();
    }

    [Test]
    public async Task SendAsync_EmailServiceReturnsFalse_ExceptionMessageContainsRecipient()
    {
        // Intent: the exception message should identify the failed recipient so the Hangfire
        // Failed tab shows actionable context without requiring a log search.
        IEmailService emailService = Substitute.For<IEmailService>();
        emailService.SendInvitationEmailAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(false);

        SendInvitationEmailJob job = new(emailService, NullLogger<SendInvitationEmailJob>.Instance);

        InvalidOperationException? ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            job.SendAsync("target@example.com", "Tenant X", "Bob", "https://app.test/accept?token=xyz", CancellationToken.None));

        await Assert.That(ex!.Message).Contains("target@example.com");
    }

    // ========== SendAsync — argument passthrough ==========

    [Test]
    public async Task SendAsync_PassesAllArgumentsToEmailService()
    {
        // Intent: every argument must be forwarded verbatim to IEmailService. A missing or
        // swapped argument would produce garbled invitation emails without a compile-time error.
        IEmailService emailService = Substitute.For<IEmailService>();
        emailService.SendInvitationEmailAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);

        SendInvitationEmailJob job = new(emailService, NullLogger<SendInvitationEmailJob>.Instance);

        await job.SendAsync("alice@example.com", "My Org", "Bob Smith", "https://vord.example.com/invitations/accept?token=tok123", CancellationToken.None);

        await emailService.Received(1).SendInvitationEmailAsync(
            "alice@example.com",
            "My Org",
            "Bob Smith",
            "https://vord.example.com/invitations/accept?token=tok123",
            CancellationToken.None);
    }
}
