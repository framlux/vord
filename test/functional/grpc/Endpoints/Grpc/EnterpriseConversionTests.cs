// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Services.Core.Alerts;
using Framlux.FleetManagement.Test.Infrastructure;
using Framlux.Vord.BillingGrpc;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using LinqToDB;
using LinqToDB.Async;

namespace Framlux.FleetManagement.FunctionalTest.Endpoints.Grpc;

/// <summary>
/// Functional tests for converting a tenant to Enterprise, over the real gRPC pipeline with the real
/// provisioner and repositories: what the apply gives back to a tenant coming from Free or Pro, what
/// it leaves alone for a tenant coming from Team, and that nothing Stripe sends afterwards can undo it.
/// </summary>
public sealed class EnterpriseConversionTests
{
    private const string PermittedClientSubject = "billing-api.vord-fleet.svc.cluster.local";

    private static readonly DateTimeOffset TermEnd = new(2027, 10, 4, 23, 59, 59, TimeSpan.Zero);

    /// <summary>
    /// A Team tenant that pays through Stripe converts and then has its Stripe subscription cancelled.
    /// Whichever cancellation action billing-api delivers afterwards, the tenant must keep every machine,
    /// its OIDC sign-in and its alert rules, none of which an Enterprise tenant is allowed to lose to a
    /// Stripe event.
    /// </summary>
    [Test]
    [Arguments(BillingAction.DowngradeToFree)]
    [Arguments(BillingAction.CancelAccount)]
    [Arguments(BillingAction.DowngradeToPro)]
    public async Task TeamTenantWithResources_AppliedToEnterprise_KeepsThemWhenTheStripeCancellationArrives(BillingAction action)
    {
        using FunctionalTestFactory factory = new();
        factory.WithInternalClientSubjects(PermittedClientSubject);
        using DatabaseContext db = factory.CreateDbContext();
        string extId = $"ext-{Guid.NewGuid():N}";
        int tenantId = await SeedTenantAsync(db, extId, SubscriptionTier.Team);
        int machineCount = 6;
        for (int i = 0; i < machineCount; i++)
        {
            await SeedMachineAsync(db, tenantId);
        }

        await db.InsertAsync(TestDataBuilder.BuildTenantOidcConfiguration(tenantId: tenantId, isEnabled: true));
        await SeedBuiltInRulesAsync(db, tenantId, isEnabled: true);
        int customRuleId = await SeedCustomRuleAsync(db, tenantId, isEnabled: true);
        await db.InsertAsync(TestDataBuilder.BuildIntegrationEndpoint(tenantId: tenantId, isEnabled: true));
        using GrpcChannel channel = CreateChannel(factory);
        FleetAdmin.FleetAdminClient fleetAdmin = new(channel);
        BillingGateway.BillingGatewayClient gateway = new(channel);

        ApplyEnterpriseAgreementResponse applied = await fleetAdmin.ApplyEnterpriseAgreementAsync(ApplyRequest(extId, 1), Headers());
        BillingActionResponse cancelled = await gateway.ProcessBillingActionAsync(
            new BillingActionRequest { TenantExternalId = extId, Action = action }, Headers());

        TenantSubscription row = await db.TenantSubscriptions.FirstAsync(s => s.TenantId == tenantId);
        await Assert.That(applied.Outcome).IsEqualTo("applied");
        await Assert.That(cancelled.Success).IsTrue();
        await Assert.That(row.Tier).IsEqualTo(SubscriptionTier.Enterprise);
        await Assert.That(row.Status).IsEqualTo(SubscriptionStatus.Active);
        await Assert.That(await db.Machines.CountAsync(m => (m.TenantId == tenantId) && (m.IsDeleted == false))).IsEqualTo(machineCount);
        await Assert.That((await db.TenantOidcConfigurations.FirstAsync(c => c.TenantId == tenantId)).IsEnabled).IsTrue();
        await Assert.That(await db.AlertRules.CountAsync(r => (r.TenantId == tenantId) && (r.IsEnabled == false))).IsEqualTo(0);
        await Assert.That((await db.AlertRules.FirstAsync(r => r.Id == customRuleId)).IsEnabled).IsTrue();
        await Assert.That(await db.IntegrationEndpoints.CountAsync(i => (i.TenantId == tenantId) && (i.IsEnabled == false))).IsEqualTo(0);
        await Assert.That(await db.AuditLog.CountAsync(a => (a.TenantId == tenantId) && (a.Action == AuditAction.SubscriptionDowngraded))).IsEqualTo(0);
    }

    /// <summary>
    /// A tenant arriving from Free holds its rules disabled by the Free sweep, built-in and custom
    /// alike. Applying the agreement must turn both back on through the real provisioner, the same
    /// restoration a checkout gives a Free tenant that upgrades.
    /// </summary>
    [Test]
    public async Task FreeTenantWithDisabledRules_AppliedToEnterprise_HasBuiltInAndCustomRulesEnabled()
    {
        using FunctionalTestFactory factory = new();
        factory.WithInternalClientSubjects(PermittedClientSubject);
        using DatabaseContext db = factory.CreateDbContext();
        string extId = $"ext-{Guid.NewGuid():N}";
        int tenantId = await SeedTenantAsync(db, extId, SubscriptionTier.Free);
        await SeedBuiltInRulesAsync(db, tenantId, isEnabled: false);
        int customRuleId = await SeedCustomRuleAsync(db, tenantId, isEnabled: false);
        using GrpcChannel channel = CreateChannel(factory);
        FleetAdmin.FleetAdminClient fleetAdmin = new(channel);

        ApplyEnterpriseAgreementResponse applied = await fleetAdmin.ApplyEnterpriseAgreementAsync(ApplyRequest(extId, 1), Headers());

        List<AlertRule> builtIn = await db.AlertRules.Where(r => (r.TenantId == tenantId) && (r.IsCustom == false)).ToListAsync();
        await Assert.That(applied.Outcome).IsEqualTo("applied");
        await Assert.That(builtIn.Count).IsEqualTo(BuiltInAlertRuleDefinitions.All.Count);
        await Assert.That(builtIn.All(r => r.IsEnabled)).IsTrue();
        await Assert.That((await db.AlertRules.FirstAsync(r => r.Id == customRuleId)).IsEnabled).IsTrue();
    }

    [Test]
    public async Task ProTenantWithFrozenCustomRule_AppliedToEnterprise_ThawsItBecauseProLacksTeamFeatures()
    {
        using FunctionalTestFactory factory = new();
        factory.WithInternalClientSubjects(PermittedClientSubject);
        using DatabaseContext db = factory.CreateDbContext();
        string extId = $"ext-{Guid.NewGuid():N}";
        int tenantId = await SeedTenantAsync(db, extId, SubscriptionTier.Pro);
        await SeedBuiltInRulesAsync(db, tenantId, isEnabled: true);
        int customRuleId = await SeedCustomRuleAsync(db, tenantId, isEnabled: false);
        using GrpcChannel channel = CreateChannel(factory);
        FleetAdmin.FleetAdminClient fleetAdmin = new(channel);

        await fleetAdmin.ApplyEnterpriseAgreementAsync(ApplyRequest(extId, 1), Headers());

        await Assert.That((await db.AlertRules.FirstAsync(r => r.Id == customRuleId)).IsEnabled).IsTrue();
    }

    /// <summary>
    /// Moving between two tiers that both have Team features thaws nothing, so a rule the customer
    /// switched off themselves stays off through the conversion.
    /// </summary>
    [Test]
    public async Task TeamTenantWithRulesTheCustomerDisabled_AppliedToEnterprise_LeavesThemDisabled()
    {
        using FunctionalTestFactory factory = new();
        factory.WithInternalClientSubjects(PermittedClientSubject);
        using DatabaseContext db = factory.CreateDbContext();
        string extId = $"ext-{Guid.NewGuid():N}";
        int tenantId = await SeedTenantAsync(db, extId, SubscriptionTier.Team);
        await SeedBuiltInRulesAsync(db, tenantId, isEnabled: false);
        int customRuleId = await SeedCustomRuleAsync(db, tenantId, isEnabled: false);
        using GrpcChannel channel = CreateChannel(factory);
        FleetAdmin.FleetAdminClient fleetAdmin = new(channel);

        await fleetAdmin.ApplyEnterpriseAgreementAsync(ApplyRequest(extId, 1), Headers());

        await Assert.That(await db.AlertRules.CountAsync(r => (r.TenantId == tenantId) && (r.IsEnabled == true))).IsEqualTo(0);
        await Assert.That((await db.AlertRules.FirstAsync(r => r.Id == customRuleId)).IsEnabled).IsFalse();
    }

    // ========== Helpers ==========

    private static ApplyEnterpriseAgreementRequest ApplyRequest(string extId, int revision)
    {
        return new ApplyEnterpriseAgreementRequest
        {
            TenantExternalId = extId,
            AgreementId = 42,
            Revision = revision,
            MachineLimit = 500,
            RetentionDays = 180,
            MemberLimit = int.MaxValue,
            AlertRuleLimit = 40,
            WebhookLimit = 20,
            TermEnd = Timestamp.FromDateTimeOffset(TermEnd),
        };
    }

    private static Metadata Headers()
    {
        return new Metadata { { TestClientCertificateMiddleware.SubjectHeader, PermittedClientSubject } };
    }

    private static GrpcChannel CreateChannel(FunctionalTestFactory factory)
    {
        HttpMessageHandler handler = new ResponseVersionHandler
        {
            InnerHandler = factory.Server.CreateHandler()
        };

        return GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
        {
            HttpHandler = handler
        });
    }

    private static async Task<int> SeedTenantAsync(DatabaseContext db, string externalId, SubscriptionTier tier)
    {
        Tenant tenant = new()
        {
            Name = $"tenant-{Guid.NewGuid():N}",
            ExternalId = externalId,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 1,
            IsActive = true,
            LogoUrl = ""
        };
        int tenantId = await db.InsertWithInt32IdentityAsync(tenant);

        await db.InsertAsync(new TenantSubscription
        {
            TenantId = tenantId,
            Tier = tier,
            Status = SubscriptionStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        return tenantId;
    }

    private static async Task SeedMachineAsync(DatabaseContext db, int tenantId)
    {
        await db.InsertWithInt64IdentityAsync(TestDataBuilder.BuildMachine(tenantId: tenantId));
    }

    /// <summary>
    /// Seeds the full set of built-in rules, driven off the shipped definitions so the assertions stay
    /// honest if a metric is ever added.
    /// </summary>
    private static async Task SeedBuiltInRulesAsync(DatabaseContext db, int tenantId, bool isEnabled)
    {
        foreach (BuiltInAlertRuleDefinition definition in BuiltInAlertRuleDefinitions.All)
        {
            await db.InsertAsync(new AlertRule
            {
                TenantId = tenantId,
                Name = definition.Name,
                Metric = definition.Metric,
                Operator = definition.Operator,
                Threshold = definition.Threshold,
                DurationMinutes = definition.DurationMinutes,
                Severity = definition.Severity,
                IsEnabled = isEnabled,
                NotifyEmail = true,
                NotifyWebhook = false,
                IsCustom = false,
                CreatedByUserId = BuiltInAlertRuleDefinitions.SystemUserId,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
        }
    }

    private static async Task<int> SeedCustomRuleAsync(DatabaseContext db, int tenantId, bool isEnabled)
    {
        AlertRule rule = TestDataBuilder.BuildAlertRule(tenantId: tenantId, isCustom: true, isEnabled: isEnabled);
        rule.CreatedByUserId = BuiltInAlertRuleDefinitions.SystemUserId;

        return await db.InsertWithInt32IdentityAsync(rule);
    }
}
