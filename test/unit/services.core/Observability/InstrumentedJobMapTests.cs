// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Hangfire;
using Framlux.FleetManagement.Services.Core.Observability;

namespace Framlux.FleetManagement.Test.Observability;

/// <summary>
/// Pins how a job run is named. Both sources are needed and neither alone is sufficient: a recurring
/// job's id is unrelated to its type name, and a fire-and-forget job has no recurring id at all.
/// </summary>
public sealed class InstrumentedJobMapTests
{
    [Test]
    public async Task Resolve_RecurringId_NamesTheJob()
    {
        await Assert.That(InstrumentedJobMap.Resolve(RecurringJobIds.AlertEvaluation, typeName: null))
            .IsEqualTo(InstrumentedJob.AlertEvaluation);
    }

    [Test]
    public async Task Resolve_TypeNameOnly_NamesAFireAndForgetJob()
    {
        // These are the jobs an operator most wants timed, and they carry no recurring id, so
        // resolving from the id alone would put every one of them into the fallback bucket.
        await Assert.That(InstrumentedJobMap.Resolve(recurringJobId: null, "SendInvitationEmailJob"))
            .IsEqualTo(InstrumentedJob.SendInvitationEmail);
    }

    [Test]
    public async Task Resolve_RecurringIdWinsOverTheTypeName()
    {
        // The id is the authoritative source when both are present.
        await Assert.That(InstrumentedJobMap.Resolve(RecurringJobIds.TenantPurge, "SendInvitationEmailJob"))
            .IsEqualTo(InstrumentedJob.TenantPurge);
    }

    [Test]
    public async Task Resolve_UnknownRecurringId_FallsBackToTheTypeName()
    {
        await Assert.That(InstrumentedJobMap.Resolve("not-a-known-id", "IntegrationDeliveryJob"))
            .IsEqualTo(InstrumentedJob.IntegrationDelivery);
    }

    [Test]
    public async Task Resolve_NothingKnown_YieldsOther()
    {
        // A rising count on this member means the enum needs a new job, not that the tag rule
        // failed — which is why an unbounded value is never allowed to become the tag.
        await Assert.That(InstrumentedJobMap.Resolve("not-a-known-id", "NotAKnownJob"))
            .IsEqualTo(InstrumentedJob.Other);
        await Assert.That(InstrumentedJobMap.Resolve(recurringJobId: null, typeName: null))
            .IsEqualTo(InstrumentedJob.Other);
    }

    [Test]
    public async Task ResolveRecurring_EveryRegisteredJobIdIsNamed()
    {
        // The guard against adding a recurring job without a vocabulary member: an unnamed job
        // would land in Other, and the health gauge would report it under a tag nobody expects.
        List<string> unnamed = RecurringJobIds.All
            .Where(id => InstrumentedJobMap.ResolveRecurring(id) == InstrumentedJob.Other)
            .ToList();

        await Assert.That(unnamed).IsEmpty();
    }
}
