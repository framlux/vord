// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Services.Core.Alerts;
using Framlux.FleetManagement.Services.Core.Notifications;
using Framlux.FleetManagement.Test.Infrastructure;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace Framlux.FleetManagement.FunctionalTest.Hangfire;

/// <summary>
/// End-to-end functional coverage of windowed failed-SSH-login alerting: stored telemetry rows are
/// counted by the real <see cref="FailedSshLoginWindowJob"/> resolved from the test host, a breach
/// raises an <see cref="AlertEvent"/> and enqueues delivery, the real delivery job sends the mail,
/// and a later quiet window resolves the incident and ends the trailing chain.
/// </summary>
/// <remarks>
/// Runs serially with every other class in this project that builds a test host, for the same
/// reason as the other Hangfire functional tests: Hangfire's DI registration resolves the
/// process-global JobStorage.Current, which each host's AddHangfire call reassigns.
/// </remarks>
[NotInParallel]
public sealed class FailedSshLoginAlertPipelineTests
{
    private const short SshSessionsTelemetryType = 9;

    [Test]
    public async Task WindowOverThreshold_RaisesAnEventAndDeliversTheEmail()
    {
        InMemoryEmailService emailService = new();

        using FunctionalTestFactory factory = new();
        factory.AdditionalTestServices = services =>
        {
            services.RemoveAll<IEmailService>();
            services.AddSingleton<IEmailService>(emailService);
        };

        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, long machineId, int ruleId) = await SeedTenantWithFailedLoginRuleAsync(db);

        await SeedFailedLoginsAsync(db, tenantId, machineId, count: 7);

        await RunWindowJobAsync(factory, tenantId, machineId);

        AlertEvent? raised = await db.AlertEvents
            .Where(e => (e.AlertRuleId == ruleId) && (e.MachineId == machineId))
            .FirstOrDefaultAsync();

        await Assert.That(raised).IsNotNull();
        await Assert.That(raised!.Status).IsEqualTo(AlertEventStatus.Triggered);
        await Assert.That(raised.Severity).IsEqualTo(AlertSeverity.Warning);
        await Assert.That(raised.Message).Contains("7 failed SSH logins");
        await Assert.That(raised.Details).Contains("\"distinctSourceIpCount\"");

        // Delivery is enqueued rather than run inline, and the window re-arms because the window
        // still holds failures.
        factory.BackgroundJobClientMock.Received(1).Create(
            Arg.Is<Job>(job => job.Type == typeof(IntegrationDeliveryJob)),
            Arg.Any<IState>());
        factory.BackgroundJobClientMock.Received(1).Create(
            Arg.Is<Job>(job => (job.Type == typeof(FailedSshLoginWindowJob))
                && ((long)job.Args[1] == machineId)),
            Arg.Is<IState>(state => state is ScheduledState));

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            IntegrationDeliveryJob deliveryJob = scope.ServiceProvider.GetRequiredService<IntegrationDeliveryJob>();
            await deliveryJob.DeliverAsync(raised.Id, ruleId, tenantId, CancellationToken.None);
        }

        await Assert.That(emailService.SentAlertEmails.Count).IsEqualTo(1);
        await Assert.That(emailService.SentAlertEmails[0].ToEmail).IsEqualTo("admin@example.com");
    }

    [Test]
    public async Task QuietWindow_ResolvesTheIncidentAndEndsTheChain()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        (int tenantId, long machineId, int ruleId) = await SeedTenantWithFailedLoginRuleAsync(db);

        // The attack happened, but every attempt landed well before this rule's five-minute window.
        await SeedFailedLoginsAsync(db, tenantId, machineId, count: 7, offset: TimeSpan.FromHours(2));

        AlertEvent open = TestDataBuilder.BuildAlertEvent(
            alertRuleId: ruleId,
            tenantId: tenantId,
            machineId: machineId,
            severity: AlertSeverity.Warning,
            message: "7 failed SSH logins in 5 minute(s)");
        long openEventId = await db.InsertWithInt64IdentityAsync(open);

        await RunWindowJobAsync(factory, tenantId, machineId);

        AlertEvent? resolved = await db.AlertEvents.Where(e => e.Id == openEventId).FirstOrDefaultAsync();

        await Assert.That(resolved).IsNotNull();
        await Assert.That(resolved!.Status).IsEqualTo(AlertEventStatus.Resolved);
        factory.BackgroundJobClientMock.DidNotReceive().Create(
            Arg.Is<Job>(job => job.Type == typeof(FailedSshLoginWindowJob)),
            Arg.Any<IState>());
    }

    /// <summary>
    /// The canonical brute-force shape: every attempt arrives in one envelope, so every row carries a
    /// single server receipt time, and the evaluation runs a full window later plus queue latency. A
    /// window measured back from the run instant alone starts after those rows and counts nothing, so
    /// the burst would resolve instead of firing.
    /// </summary>
    [Test]
    public async Task BurstDeliveredInOneEnvelope_StillFiresWhenTheJobRunsAWindowLater()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        (int tenantId, long machineId, int ruleId) = await SeedTenantWithFailedLoginRuleAsync(db);

        // The burst landed one window ago; the job it scheduled is only running now.
        DateTimeOffset windowOpenedAt = DateTimeOffset.UtcNow
            - AlertConstants.FailedSshLoginWindow
            - TimeSpan.FromSeconds(8);

        await SeedBurstAsync(db, tenantId, machineId, count: 7, at: windowOpenedAt);

        await RunWindowJobAsync(factory, tenantId, machineId, windowOpenedAt);

        AlertEvent? raised = await db.AlertEvents
            .Where(e => (e.AlertRuleId == ruleId) && (e.MachineId == machineId))
            .FirstOrDefaultAsync();

        await Assert.That(raised).IsNotNull();
        await Assert.That(raised!.Status).IsEqualTo(AlertEventStatus.Triggered);
        await Assert.That(raised.Message).Contains("7 failed SSH logins");
    }

    private static async Task RunWindowJobAsync(FunctionalTestFactory factory, int tenantId, long machineId, DateTimeOffset? windowOpenedAt = null)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        FailedSshLoginWindowJob job = scope.ServiceProvider.GetRequiredService<FailedSshLoginWindowJob>();

        await job.RunAsync(
            tenantId,
            machineId,
            windowOpenedAt ?? DateTimeOffset.UtcNow,
            Guid.NewGuid().ToString("N"),
            CancellationToken.None);
    }

    private static async Task SeedFailedLoginsAsync(DatabaseContext db, int tenantId, long machineId, int count, TimeSpan? offset = null)
    {
        DateTimeOffset newest = DateTimeOffset.UtcNow - (offset ?? TimeSpan.Zero);

        for (int attempt = 0; attempt < count; attempt++)
        {
            DateTimeOffset serverReceivedAt = newest.AddSeconds(-attempt);

            await db.InsertAsync(TestDataBuilder.BuildMachineTelemetry(
                machineId: machineId,
                tenantId: tenantId,
                telemetryType: SshSessionsTelemetryType,
                payload: $"{{\"user\":\"root\",\"source_ip\":\"203.0.113.7\",\"source_port\":{55000 + attempt},\"action\":\"failed\",\"auth_method\":\"password\",\"timestamp\":\"{serverReceivedAt:o}\"}}",
                receivedAt: serverReceivedAt,
                serverReceivedAt: serverReceivedAt));
        }
    }

    /// <summary>
    /// Seeds a burst of failed logins that all share one server receipt time, as every row in a
    /// single telemetry envelope does.
    /// </summary>
    private static async Task SeedBurstAsync(DatabaseContext db, int tenantId, long machineId, int count, DateTimeOffset at)
    {
        for (int attempt = 0; attempt < count; attempt++)
        {
            await db.InsertAsync(TestDataBuilder.BuildMachineTelemetry(
                machineId: machineId,
                tenantId: tenantId,
                telemetryType: SshSessionsTelemetryType,
                payload: $"{{\"user\":\"root\",\"source_ip\":\"203.0.113.7\",\"source_port\":{55000 + attempt},\"action\":\"failed\",\"auth_method\":\"password\",\"timestamp\":\"{at:o}\"}}",
                receivedAt: at,
                serverReceivedAt: at));
        }
    }

    private static async Task<(int TenantId, long MachineId, int RuleId)> SeedTenantWithFailedLoginRuleAsync(DatabaseContext db)
    {
        Tenant tenant = TestDataBuilder.BuildTenant();
        tenant.Id = await db.InsertWithInt32IdentityAsync(tenant);

        await db.InsertAsync(new TenantSubscription
        {
            TenantId = tenant.Id,
            Tier = SubscriptionTier.Pro,
            Status = SubscriptionStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        UserAccount admin = TestDataBuilder.BuildUser(username: "admin@example.com");
        admin.Id = await db.InsertWithInt32IdentityAsync(admin);

        await db.InsertAsync(TestDataBuilder.BuildUserTenantRole(
            userId: admin.Id,
            tenantId: tenant.Id,
            role: UserAccountRoles.TenantAdmin,
            assignedByUserId: admin.Id));

        RegistrationToken token = new()
        {
            TenantId = tenant.Id,
            TokenHash = Guid.NewGuid().ToString("N"),
            Name = "Failed SSH Login Test Token",
            CreatedByUserId = admin.Id,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
            IsRevoked = false,
        };
        long tokenId = await db.InsertWithInt64IdentityAsync(token);

        Machine machine = TestDataBuilder.BuildMachine(tenantId: tenant.Id, registrationTokenId: tokenId);
        long machineId = await db.InsertWithInt64IdentityAsync(machine);

        AlertRule rule = TestDataBuilder.BuildAlertRule(
            tenantId: tenant.Id,
            metric: AlertMetric.FailedSshLogin,
            op: AlertOperator.GreaterThan,
            threshold: 5,
            severity: AlertSeverity.Warning,
            isCustom: false,
            durationMinutes: AlertConstants.FailedSshLoginWindowMinutes,
            notifyEmail: true,
            createdByUserId: admin.Id);
        rule.Id = await db.InsertWithInt32IdentityAsync(rule);

        await db.InsertAsync(new AlertRuleMachine
        {
            AlertRuleId = rule.Id,
            MachineId = machineId,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        return (tenant.Id, machineId, rule.Id);
    }
}
