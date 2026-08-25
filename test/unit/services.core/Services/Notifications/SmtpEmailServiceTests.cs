// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Notifications;
using Framlux.FleetManagement.Services.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Framlux.FleetManagement.Test.Services;

/// <summary>
/// Tests for <see cref="SmtpEmailService"/>, the self-hosted transport. A deployment with no SMTP
/// host configured resolves <see cref="NoOpEmailService"/> instead, so every case here assumes a
/// host is set and the question is what happens when the relay does not answer.
/// </summary>
/// <remarks>
/// The success path needs a live relay and belongs in an integration test; these cover the failure
/// contract, which is the part callers branch on. The contract that matters is that a transport
/// failure returns Failed rather than throwing: every caller switches on the outcome, and an
/// exception escaping here would fail an invitation request or an alert delivery job outright.
/// </remarks>
public sealed class SmtpEmailServiceTests
{
    private static SmtpEmailService CreateService(ILogger<SmtpEmailService> logger)
    {
        EmailOptions options = new()
        {
            FromEmail = "alerts@example.com",
            Smtp = new SmtpEmailOptions
            {
                // Port 1 on loopback refuses immediately, so this is a deterministic connect
                // failure rather than a wait on a timeout.
                Host = "127.0.0.1",
                Port = 1,
                UseStartTls = false,
            },
        };

        return new SmtpEmailService(Options.Create(options), logger);
    }

    [Test]
    public async Task SendInvitationEmailAsync_WhenRelayRefusesConnection_ReturnsFailed()
    {
        SmtpEmailService service = CreateService(NullLogger<SmtpEmailService>.Instance);

        EmailDeliveryOutcome outcome = await service.SendInvitationEmailAsync(
            "invitee@example.com", "Acme", "Ada", "https://example.com/accept", CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(EmailDeliveryOutcome.Failed);
    }

    [Test]
    public async Task SendAlertEmailAsync_WhenRelayRefusesConnection_ReturnsFailed()
    {
        SmtpEmailService service = CreateService(NullLogger<SmtpEmailService>.Instance);

        EmailDeliveryOutcome outcome = await service.SendAlertEmailAsync(
            "oncall@example.com", "Disk nearly full", "<p>body</p>", CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(EmailDeliveryOutcome.Failed);
    }

    /// <summary>
    /// A failed send must be visible in the logs. Returning Failed silently would leave an operator
    /// with undelivered alerts and nothing explaining why.
    /// </summary>
    [Test]
    public async Task SendAlertEmailAsync_WhenSendFails_LogsError()
    {
        ILogger<SmtpEmailService> logger = Substitute.For<ILogger<SmtpEmailService>>();
        SmtpEmailService service = CreateService(logger);

        await service.SendAlertEmailAsync(
            "oncall@example.com", "Disk nearly full", "<p>body</p>", CancellationToken.None);

        logger.Received(1).Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    /// <summary>
    /// Cancellation is rethrown rather than reported as a delivery failure: a cancelled request is
    /// not a bad relay, and recording it as Failed would misattribute a shutdown to the operator's
    /// mail configuration.
    /// </summary>
    [Test]
    public async Task SendAlertEmailAsync_WhenCancelled_Throws()
    {
        SmtpEmailService service = CreateService(NullLogger<SmtpEmailService>.Instance);
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();

        await Assert.That(async () => await service.SendAlertEmailAsync(
                "oncall@example.com", "subject", "<p>body</p>", cts.Token))
            .Throws<OperationCanceledException>();
    }

    [Test]
    public async Task SendAlertEmailAsync_WhenFromAddressIsUnparseable_ReturnsFailed()
    {
        EmailOptions options = new()
        {
            FromEmail = "not an address",
            Smtp = new SmtpEmailOptions { Host = "127.0.0.1", Port = 1 },
        };
        SmtpEmailService service = new(Options.Create(options), NullLogger<SmtpEmailService>.Instance);

        EmailDeliveryOutcome outcome = await service.SendAlertEmailAsync(
            "oncall@example.com", "subject", "<p>body</p>", CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(EmailDeliveryOutcome.Failed);
    }

    [Test]
    public async Task Constructor_NullEmailOptions_Throws()
    {
        await Assert.That(() => new SmtpEmailService(null!, NullLogger<SmtpEmailService>.Instance))
            .Throws<ArgumentNullException>();
    }

    [Test]
    public async Task Constructor_NullLogger_Throws()
    {
        await Assert.That(() => new SmtpEmailService(Options.Create(new EmailOptions()), null!))
            .Throws<ArgumentNullException>();
    }
}
