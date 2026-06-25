// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Services.Core.Alerts;
using Framlux.FleetManagement.Services.Core.Notifications;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Framlux.FleetManagement.FunctionalTest.Hangfire;

/// <summary>
/// End-to-end functional test for the alert email delivery pipeline. Seeds a tenant with an
/// active TenantAdmin recipient, an email-only <see cref="AlertRule"/>, and a triggered
/// <see cref="AlertEvent"/>, then drives delivery through the real Hangfire job
/// (<see cref="IntegrationDeliveryJob.DeliverAsync"/>) resolved from the test host's DI. The job
/// runs the real <see cref="AlertDeliveryService"/> path — recipient resolution via
/// <c>ITenantRepository.GetTenantAdminEmailsAsync</c>, rendering via
/// <see cref="AlertEmailContent.Build"/>, idempotency claims, and the final
/// <c>IEmailService.SendAlertEmailAsync</c> send — against an in-memory SQLite database. The
/// registered <see cref="InMemoryEmailService"/> records the send so the test can assert the
/// recipient, subject, and host link without touching the real Resend transport.
/// </summary>
public sealed class AlertEmailDeliveryTests
{
    [Test]
    public async Task DeliverAlertEvent_EmailRule_SendsOneEmailToTenantAdmin()
    {
        // The host resolves IEmailService from a fresh scope inside AlertDeliveryService, so the
        // recorder must be a singleton to survive across scopes and be observable from the test.
        InMemoryEmailService emailService = new();

        using FunctionalTestFactory factory = new();
        factory.AdditionalTestServices = services =>
        {
            services.RemoveAll<IEmailService>();
            services.AddSingleton<IEmailService>(emailService);
        };

        long eventId;
        int ruleId;
        int tenantId;
        long machineId;

        using (DatabaseContext db = factory.CreateDbContext())
        {
            Tenant tenant = TestDataBuilder.BuildTenant();
            tenant.Id = await db.InsertWithInt32IdentityAsync(tenant);
            tenantId = tenant.Id;

            UserAccount admin = TestDataBuilder.BuildUser(username: "admin@example.com");
            admin.Id = await db.InsertWithInt32IdentityAsync(admin);

            UserTenantRole role = TestDataBuilder.BuildUserTenantRole(
                userId: admin.Id,
                tenantId: tenantId,
                role: UserAccountRoles.TenantAdmin,
                assignedByUserId: admin.Id);
            await db.InsertAsync(role);

            RegistrationToken token = new()
            {
                TenantId = tenantId,
                TokenHash = Guid.NewGuid().ToString("N"),
                Name = "Alert Email Test Token",
                CreatedByUserId = 1,
                CreatedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
                IsRevoked = false,
            };
            long tokenId = await db.InsertWithInt64IdentityAsync(token);

            Machine machine = TestDataBuilder.BuildMachine(tenantId: tenantId, registrationTokenId: tokenId);
            machineId = await db.InsertWithInt64IdentityAsync(machine);

            AlertRule rule = TestDataBuilder.BuildAlertRule(
                tenantId: tenantId,
                severity: AlertSeverity.Critical,
                notifyEmail: true,
                notifyWebhook: false,
                createdByUserId: admin.Id);
            rule.Id = await db.InsertWithInt32IdentityAsync(rule);
            ruleId = rule.Id;

            AlertEvent alertEvent = TestDataBuilder.BuildAlertEvent(
                alertRuleId: ruleId,
                tenantId: tenantId,
                machineId: machineId,
                severity: AlertSeverity.Critical,
                message: "CPU usage exceeded threshold");
            eventId = await db.InsertWithInt64IdentityAsync(alertEvent);
        }

        // Drive delivery through the real Hangfire job. Resolving and invoking it directly
        // exercises the same job -> AlertDeliveryService -> DeliverEmailAsync path that the
        // processing server would run, deterministically and without polling.
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            IntegrationDeliveryJob job = scope.ServiceProvider.GetRequiredService<IntegrationDeliveryJob>();
            await job.DeliverAsync(eventId, ruleId, tenantId, CancellationToken.None);
        }

        await Assert.That(emailService.SentAlertEmails.Count).IsEqualTo(1);

        InMemoryEmailService.SentAlertEmail sent = emailService.SentAlertEmails[0];
        await Assert.That(sent.ToEmail).IsEqualTo("admin@example.com");
        await Assert.That(sent.Subject).Contains(AlertSeverity.Critical.ToString());
        await Assert.That(sent.HtmlBody).Contains($"/machines/{machineId}");
    }
}
