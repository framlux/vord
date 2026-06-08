// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Hangfire;
using Hangfire;
using Hangfire.Common;
using NSubstitute;

namespace Framlux.FleetManagement.Test.Hangfire;

/// <summary>
/// Pins the exact set of recurring job ids and cron expressions registered by
/// <see cref="RecurringJobRegistry.RegisterAll"/>. The cron schedule of a recurring job is a
/// production contract: a silent change (e.g., from every minute to every hour) would either
/// stall the platform's background work or hammer downstream services. These tests fail loudly
/// when a job is removed, renamed, or rescheduled so the change is intentional rather than
/// accidental.
/// </summary>
public sealed class RecurringJobRegistryTests
{
    [Test]
    public async Task RegisterAll_AlwaysEnabledJobs_AreRegisteredWithExactCronExpressions()
    {
        // Intent: the five always-on jobs must be present regardless of optional feature flags.
        // Their cron expressions are the production contract — verifying them by exact match
        // catches both renames and schedule drift.
        IRecurringJobManager mgr = Substitute.For<IRecurringJobManager>();

        RecurringJobRegistry.RegisterAll(mgr, billingEnabled: false, objectStorageEnabled: false);

        await Assert.That(() =>
        {
            ReceivedAddOrUpdate(mgr, "remote-command-expiry", "* * * * *");
            ReceivedAddOrUpdate(mgr, "partition-management", "0 3 * * *");
            ReceivedAddOrUpdate(mgr, "health-sweep-coordinator", "* * * * *");
            ReceivedAddOrUpdate(mgr, "alert-evaluation", "* * * * *");
            ReceivedAddOrUpdate(mgr, "alert-condition-state-cleanup", "17 2 * * *");
        }).ThrowsNothing();
    }

    [Test]
    public async Task RegisterAll_BillingEnabled_AddsUsageHeartbeatAndStripeSync()
    {
        // Intent: when billing is enabled both billing-tier recurring jobs must be scheduled with
        // their exact cron expressions and the registry must NOT also remove them in the same call.
        IRecurringJobManager mgr = Substitute.For<IRecurringJobManager>();

        RecurringJobRegistry.RegisterAll(mgr, billingEnabled: true, objectStorageEnabled: false);

        await Assert.That(() =>
        {
            ReceivedAddOrUpdate(mgr, "usage-heartbeat", "7 * * * *");
            ReceivedAddOrUpdate(mgr, "stripe-sync", "*/5 * * * *");
            mgr.DidNotReceive().RemoveIfExists("usage-heartbeat");
            mgr.DidNotReceive().RemoveIfExists("stripe-sync");
        }).ThrowsNothing();
    }

    [Test]
    public async Task RegisterAll_BillingDisabled_RemovesBillingJobs()
    {
        // Intent: a previously registered billing job must be torn down when the feature flag is
        // off so it can no longer fire after the tenant disables billing.
        IRecurringJobManager mgr = Substitute.For<IRecurringJobManager>();

        RecurringJobRegistry.RegisterAll(mgr, billingEnabled: false, objectStorageEnabled: false);

        await Assert.That(() =>
        {
            mgr.Received(1).RemoveIfExists("usage-heartbeat");
            mgr.Received(1).RemoveIfExists("stripe-sync");
            DidNotReceiveAddOrUpdate(mgr, "usage-heartbeat");
            DidNotReceiveAddOrUpdate(mgr, "stripe-sync");
        }).ThrowsNothing();
    }

    [Test]
    public async Task RegisterAll_ObjectStorageEnabled_AddsDataExportJobs()
    {
        // Intent: the data export pipeline only runs when object storage is wired up; when
        // enabled both the processing tick and the cleanup sweep must be scheduled.
        IRecurringJobManager mgr = Substitute.For<IRecurringJobManager>();

        RecurringJobRegistry.RegisterAll(mgr, billingEnabled: false, objectStorageEnabled: true);

        await Assert.That(() =>
        {
            ReceivedAddOrUpdate(mgr, "data-export-processing", "* * * * *");
            ReceivedAddOrUpdate(mgr, "data-export-cleanup", "13 * * * *");
            mgr.DidNotReceive().RemoveIfExists("data-export-processing");
            mgr.DidNotReceive().RemoveIfExists("data-export-cleanup");
        }).ThrowsNothing();
    }

    [Test]
    public async Task RegisterAll_ObjectStorageDisabled_RemovesDataExportJobs()
    {
        // Intent: when object storage is not configured, any previously scheduled data export
        // jobs must be removed so they cannot fail repeatedly against missing storage.
        IRecurringJobManager mgr = Substitute.For<IRecurringJobManager>();

        RecurringJobRegistry.RegisterAll(mgr, billingEnabled: false, objectStorageEnabled: false);

        await Assert.That(() =>
        {
            mgr.Received(1).RemoveIfExists("data-export-processing");
            mgr.Received(1).RemoveIfExists("data-export-cleanup");
            DidNotReceiveAddOrUpdate(mgr, "data-export-processing");
            DidNotReceiveAddOrUpdate(mgr, "data-export-cleanup");
        }).ThrowsNothing();
    }

    [Test]
    public async Task RegisterAll_AllFeaturesEnabled_RegistersExactNineJobs()
    {
        // Intent: pin the total job count when all feature flags are on. A new job added without
        // updating this test should force the developer to also update the pin — surfacing a
        // forgotten test update during code review rather than after deployment.
        IRecurringJobManager mgr = Substitute.For<IRecurringJobManager>();

        RecurringJobRegistry.RegisterAll(mgr, billingEnabled: true, objectStorageEnabled: true);

        await Assert.That(() =>
        {
            mgr.Received(9).AddOrUpdate(
                Arg.Any<string>(),
                Arg.Any<Job>(),
                Arg.Any<string>(),
                Arg.Any<RecurringJobOptions>());
            mgr.DidNotReceive().RemoveIfExists(Arg.Any<string>());
        }).ThrowsNothing();
    }

    [Test]
    public async Task RegisterAll_NullManager_Throws()
    {
        // Intent: argument-null guard documented in the registry — passing a null manager must
        // fail fast at the boundary, not later when the first AddOrUpdate is dispatched.
        await Assert.That(() => RecurringJobRegistry.RegisterAll(null!, true, true))
            .Throws<ArgumentNullException>();
    }

    /// <summary>
    /// Verifies that the registry called <see cref="IRecurringJobManager.AddOrUpdate"/> exactly
    /// once with the supplied job id and cron expression. The typed extension methods in
    /// <c>RecurringJobManagerExtensions</c> all resolve to this single interface signature
    /// (id, Job, cron, RecurringJobOptions), which is what NSubstitute intercepts.
    /// </summary>
    private static void ReceivedAddOrUpdate(IRecurringJobManager mgr, string id, string expectedCron)
    {
        mgr.Received(1).AddOrUpdate(
            id,
            Arg.Any<Job>(),
            expectedCron,
            Arg.Any<RecurringJobOptions>());
    }

    /// <summary>
    /// Verifies the registry did not call <see cref="IRecurringJobManager.AddOrUpdate"/> for the
    /// supplied job id under any cron expression.
    /// </summary>
    private static void DidNotReceiveAddOrUpdate(IRecurringJobManager mgr, string id)
    {
        mgr.DidNotReceive().AddOrUpdate(
            id,
            Arg.Any<Job>(),
            Arg.Any<string>(),
            Arg.Any<RecurringJobOptions>());
    }
}
