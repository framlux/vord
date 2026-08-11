// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Billing;
using Framlux.FleetManagement.Services.Core.Machines;
using Framlux.FleetManagement.Test.Infrastructure;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Framlux.FleetManagement.Test.Services.Machines;

/// <summary>
/// Tests for <see cref="MachineBillingSync"/>.
/// </summary>
public sealed class MachineBillingSyncTests
{
    private static MachineBillingSync CreateService(
        ISubscriptionService subscriptionService,
        ITenantRepository tenantRepo,
        IMachineRepository machineRepo,
        IBillingApiClient billingApiClient,
        ILogger<MachineBillingSync>? logger = null)
    {
        return new MachineBillingSync(
            subscriptionService,
            tenantRepo,
            machineRepo,
            billingApiClient,
            logger ?? Substitute.For<ILogger<MachineBillingSync>>());
    }

    [Test]
    public async Task ReportActiveMachineUsageAsync_NullSubscription_ReportsNothing()
    {
        ISubscriptionService subscriptionService = Substitute.For<ISubscriptionService>();
        subscriptionService.GetSubscriptionForTenantAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((TenantSubscription?)null);

        ITenantRepository tenantRepo = Substitute.For<ITenantRepository>();
        IMachineRepository machineRepo = Substitute.For<IMachineRepository>();
        IBillingApiClient billingApiClient = Substitute.For<IBillingApiClient>();

        MachineBillingSync service = CreateService(subscriptionService, tenantRepo, machineRepo, billingApiClient);

        await service.ReportActiveMachineUsageAsync(1, CancellationToken.None);

        await billingApiClient.DidNotReceive().UpdateQuantityAsync(
            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ReportActiveMachineUsageAsync_FreeTierSubscription_ReportsNothing()
    {
        TenantSubscription freeSub = TestDataBuilder.BuildSubscription(tenantId: 1, tier: SubscriptionTier.Free);

        ISubscriptionService subscriptionService = Substitute.For<ISubscriptionService>();
        subscriptionService.GetSubscriptionForTenantAsync(1, Arg.Any<CancellationToken>())
            .Returns(freeSub);

        ITenantRepository tenantRepo = Substitute.For<ITenantRepository>();
        IMachineRepository machineRepo = Substitute.For<IMachineRepository>();
        IBillingApiClient billingApiClient = Substitute.For<IBillingApiClient>();

        MachineBillingSync service = CreateService(subscriptionService, tenantRepo, machineRepo, billingApiClient);

        await service.ReportActiveMachineUsageAsync(1, CancellationToken.None);

        await billingApiClient.DidNotReceive().UpdateQuantityAsync(
            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ReportActiveMachineUsageAsync_NoneTierSubscription_ReportsNothing()
    {
        // Intent: the billing guard must allowlist Pro/Team rather than merely exclude Free. A
        // subscription row can carry Tier.None (e.g. one that predates a tier being set); None
        // has no billable floor and must not reach GetBillableMachineCountAsync, which refuses it.
        TenantSubscription noneSub = TestDataBuilder.BuildSubscription(tenantId: 1, tier: SubscriptionTier.None);

        ISubscriptionService subscriptionService = Substitute.For<ISubscriptionService>();
        subscriptionService.GetSubscriptionForTenantAsync(1, Arg.Any<CancellationToken>())
            .Returns(noneSub);

        // A resolvable tenant must be stubbed, or the "tenant is null" branch short-circuits
        // before the tier guard is ever reached, and this test would pass regardless of what
        // the guard does.
        Tenant tenant = TestDataBuilder.BuildTenant(externalId: "ext-tenant-none-tier");
        tenant.Id = 1;

        ITenantRepository tenantRepo = Substitute.For<ITenantRepository>();
        tenantRepo.GetTenantByIdAsync(1, Arg.Any<CancellationToken>()).Returns(tenant);
        IMachineRepository machineRepo = Substitute.For<IMachineRepository>();
        IBillingApiClient billingApiClient = Substitute.For<IBillingApiClient>();

        MachineBillingSync service = CreateService(subscriptionService, tenantRepo, machineRepo, billingApiClient);

        await service.ReportActiveMachineUsageAsync(1, CancellationToken.None);

        await billingApiClient.DidNotReceive().UpdateQuantityAsync(
            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await subscriptionService.DidNotReceive().GetBillableMachineCountAsync(
            Arg.Any<int>(), Arg.Any<SubscriptionTier>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ReportActiveMachineUsageAsync_ProTierWithTenant_CallsUpdateQuantityWithCorrectArgs()
    {
        int tenantId = 42;
        string externalId = "ext-tenant-42";
        int billableQuantity = 7;

        TenantSubscription proSub = TestDataBuilder.BuildSubscription(tenantId: tenantId, tier: SubscriptionTier.Pro);
        Tenant tenant = TestDataBuilder.BuildTenant(externalId: externalId);
        tenant.Id = tenantId;

        ISubscriptionService subscriptionService = Substitute.For<ISubscriptionService>();
        subscriptionService.GetSubscriptionForTenantAsync(tenantId, Arg.Any<CancellationToken>())
            .Returns(proSub);
        subscriptionService.GetBillableMachineCountAsync(tenantId, SubscriptionTier.Pro, Arg.Any<CancellationToken>())
            .Returns(billableQuantity);

        ITenantRepository tenantRepo = Substitute.For<ITenantRepository>();
        tenantRepo.GetTenantByIdAsync(tenantId, Arg.Any<CancellationToken>())
            .Returns(tenant);

        IMachineRepository machineRepo = Substitute.For<IMachineRepository>();
        IBillingApiClient billingApiClient = Substitute.For<IBillingApiClient>();

        MachineBillingSync service = CreateService(subscriptionService, tenantRepo, machineRepo, billingApiClient);

        await service.ReportActiveMachineUsageAsync(tenantId, CancellationToken.None);

        await billingApiClient.Received(1).UpdateQuantityAsync(
            externalId, billableQuantity, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ReportActiveMachineUsageAsync_TenantLookupReturnsNull_DoesNotCallBilling()
    {
        int tenantId = 5;

        TenantSubscription proSub = TestDataBuilder.BuildSubscription(tenantId: tenantId, tier: SubscriptionTier.Pro);

        ISubscriptionService subscriptionService = Substitute.For<ISubscriptionService>();
        subscriptionService.GetSubscriptionForTenantAsync(tenantId, Arg.Any<CancellationToken>())
            .Returns(proSub);

        ITenantRepository tenantRepo = Substitute.For<ITenantRepository>();
        tenantRepo.GetTenantByIdAsync(tenantId, Arg.Any<CancellationToken>())
            .Returns((Tenant?)null);

        IMachineRepository machineRepo = Substitute.For<IMachineRepository>();
        IBillingApiClient billingApiClient = Substitute.For<IBillingApiClient>();

        MachineBillingSync service = CreateService(subscriptionService, tenantRepo, machineRepo, billingApiClient);

        await service.ReportActiveMachineUsageAsync(tenantId, CancellationToken.None);

        await billingApiClient.DidNotReceive().UpdateQuantityAsync(
            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ReportActiveMachineUsageAsync_BillingClientThrows_SwallowsExceptionAndCompletes()
    {
        int tenantId = 10;
        string externalId = "ext-tenant-throw";

        TenantSubscription proSub = TestDataBuilder.BuildSubscription(tenantId: tenantId, tier: SubscriptionTier.Pro);
        Tenant tenant = TestDataBuilder.BuildTenant(externalId: externalId);
        tenant.Id = tenantId;

        ISubscriptionService subscriptionService = Substitute.For<ISubscriptionService>();
        subscriptionService.GetSubscriptionForTenantAsync(tenantId, Arg.Any<CancellationToken>())
            .Returns(proSub);
        subscriptionService.GetBillableMachineCountAsync(tenantId, SubscriptionTier.Pro, Arg.Any<CancellationToken>())
            .Returns(3);

        ITenantRepository tenantRepo = Substitute.For<ITenantRepository>();
        tenantRepo.GetTenantByIdAsync(tenantId, Arg.Any<CancellationToken>())
            .Returns(tenant);

        IMachineRepository machineRepo = Substitute.For<IMachineRepository>();

        IBillingApiClient billingApiClient = Substitute.For<IBillingApiClient>();
        billingApiClient.UpdateQuantityAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Billing service unavailable"));

        ILogger<MachineBillingSync> logger = Substitute.For<ILogger<MachineBillingSync>>();

        MachineBillingSync service = CreateService(subscriptionService, tenantRepo, machineRepo, billingApiClient, logger);

        await Assert.That(async () => await service.ReportActiveMachineUsageAsync(tenantId, CancellationToken.None))
            .ThrowsNothing();

        logger.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());
    }
}
