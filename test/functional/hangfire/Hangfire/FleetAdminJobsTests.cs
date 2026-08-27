// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Hangfire;
using Framlux.Vord.BillingGrpc;

namespace Framlux.FleetManagement.FunctionalTest.Hangfire;

/// <summary>
/// Covers the fleet-admin recurring-job RPCs against a real Hangfire storage and processing
/// server, which is the only place the nine registrations and a live server row both exist.
/// </summary>
/// <remarks>
/// Runs serially. Hangfire's DI registration resolves the process-global JobStorage.Current, and
/// the test factory resets it per host, so two of these classes running concurrently would read
/// each other's storage. Worse, a concurrent RegisterAll recomputes NextExecution for the very
/// job whose schedule stability one of these tests asserts.
/// </remarks>
[NotInParallel]
public sealed class FleetAdminJobsTests
{
    [Test]
    public async Task ListRecurringJobs_AfterRegistration_ReturnsEveryKnownJob()
    {
        // Intent: the view is the operator's inventory of background work. Every id this build
        // knows about must come back, whether or not storage has it.
        await using FleetAdminJobsFixture fixture = await FleetAdminJobsFixture.CreateAsync();

        ListRecurringJobsResponse response = await fixture.Service.ListRecurringJobs(
            new ListRecurringJobsRequest(), fixture.CallContext);

        await Assert.That(response.Jobs.Select(j => j.Id).OrderBy(id => id))
            .IsEquivalentTo(RecurringJobIds.All.OrderBy(id => id));
    }

    [Test]
    public async Task ListRecurringJobs_AfterRegistration_ReportsJobsAsScheduled()
    {
        // Intent: a freshly registered job must not read as Missing or Overdue, or the view
        // would alarm on a healthy platform.
        await using FleetAdminJobsFixture fixture = await FleetAdminJobsFixture.CreateAsync();

        ListRecurringJobsResponse response = await fixture.Service.ListRecurringJobs(
            new ListRecurringJobsRequest(), fixture.CallContext);

        FleetRecurringJob job = response.Jobs.First(j => j.Id == RecurringJobIds.TenantPurge);

        await Assert.That(job.Status).IsEqualTo(FleetRecurringJobStatus.Scheduled);
    }

    [Test]
    public async Task ListRecurringJobs_ReturnsServerTime()
    {
        // Intent: the panel renders ages against the fleet's clock, not the browser's. An unset
        // server_time would silently shift every relative timestamp in the view.
        await using FleetAdminJobsFixture fixture = await FleetAdminJobsFixture.CreateAsync();

        ListRecurringJobsResponse response = await fixture.Service.ListRecurringJobs(
            new ListRecurringJobsRequest(), fixture.CallContext);

        await Assert.That(response.ServerTime).IsNotNull();
    }

    [Test]
    public async Task ListRecurringJobs_WithProcessingServer_ReturnsServerBlock()
    {
        // Intent: worker heartbeat is the headline signal. If the server block is empty when a
        // worker is demonstrably running, the panel's most important indicator is broken.
        //
        // The server row is announced from a dedicated dispatcher thread that nothing in the test
        // factory awaits, so this polls rather than asserting immediately. This is not a
        // wall-clock assertion: the deadline only bounds failure, it never defines the pass
        // condition.
        await using FleetAdminJobsFixture fixture = await FleetAdminJobsFixture.CreateAsync();

        ListRecurringJobsResponse response = new();
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(10);

        while (DateTimeOffset.UtcNow < deadline)
        {
            response = await fixture.Service.ListRecurringJobs(
                new ListRecurringJobsRequest(), fixture.CallContext);

            if (response.Servers.Count > 0)
            {
                break;
            }

            await Task.Delay(50);
        }

        await Assert.That(response.Servers.Count).IsGreaterThan(0);
    }

    [Test]
    public async Task ListRecurringJobs_UnknownEnrichJobId_ReturnsRunMarkedNotRetained()
    {
        // Intent: a manual run whose job row Hangfire has expired must still appear, flagged as
        // no-longer-retained, rather than vanishing from the operator's history.
        await using FleetAdminJobsFixture fixture = await FleetAdminJobsFixture.CreateAsync();

        ListRecurringJobsRequest request = new();
        request.EnrichJobIds.Add("nonexistent-job-id");

        ListRecurringJobsResponse response = await fixture.Service.ListRecurringJobs(
            request, fixture.CallContext);

        FleetManualRun run = response.ManualRuns.Single();

        await Assert.That(run.Retained).IsFalse();
    }
}
