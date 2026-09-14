// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Hangfire;
using Hangfire;
using Hangfire.Common;
using NSubstitute;

namespace Framlux.FleetManagement.Test.Hangfire;

/// <summary>
/// Proves the intended-job set and the registration it describes cannot drift apart.
/// </summary>
/// <remarks>
/// The health gauge reports only jobs this configuration intends to register. If the two disagree,
/// a job the configuration deliberately omits is reported Missing and pages an operator hourly,
/// forever, about an intended absence — which is precisely the trap the exclusion exists to avoid.
/// The set cannot drive the registration, because that uses a generic overload with a per-job cron
/// which a set of id strings cannot express, so the two stay separate and this test binds them.
/// </remarks>
public sealed class IntendedJobIdsTests
{
    [Test]
    [Arguments(true, true)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(false, false)]
    public async Task IntendedJobIds_MatchesWhatRegisterAllActuallyRegisters(bool isSaas, bool objectStorageEnabled)
    {
        // The generic AddOrUpdate<TJob> the registry calls is a static extension method, which
        // NSubstitute cannot intercept. It delegates to this interface member, which is what the
        // substitute actually sees.
        IRecurringJobManager manager = Substitute.For<IRecurringJobManager>();
        List<string> registered = [];
        manager.When(m => m.AddOrUpdate(
                Arg.Any<string>(), Arg.Any<Job>(), Arg.Any<string>(), Arg.Any<RecurringJobOptions>()))
            .Do(call => registered.Add(call.ArgAt<string>(0)));

        RecurringJobRegistry.RegisterAll(manager, isSaas, objectStorageEnabled);

        IReadOnlySet<string> intended = RecurringJobRegistry.IntendedJobIds(isSaas, objectStorageEnabled);

        await Assert.That(intended.OrderBy(id => id, StringComparer.Ordinal).ToList())
            .IsEquivalentTo(registered.OrderBy(id => id, StringComparer.Ordinal).ToList());
    }

    [Test]
    public async Task IntendedJobIds_HostedWithObjectStorage_IsTheFullNine()
    {
        // The only configuration that intends all nine. The health gauge's measurement count is
        // derived from this, so it is pinned rather than inferred.
        IReadOnlySet<string> intended = RecurringJobRegistry.IntendedJobIds(isSaas: true, objectStorageEnabled: true);

        await Assert.That(intended.Count).IsEqualTo(RecurringJobIds.All.Count);
    }

    [Test]
    public async Task IntendedJobIds_SelfHosted_OmitsTheBillingSyncJob()
    {
        IReadOnlySet<string> intended = RecurringJobRegistry.IntendedJobIds(isSaas: false, objectStorageEnabled: true);

        await Assert.That(intended.Contains(RecurringJobIds.StripeSync)).IsFalse();
    }

    [Test]
    public async Task IntendedJobIds_WithoutObjectStorage_OmitsBothDataExportJobs()
    {
        IReadOnlySet<string> intended = RecurringJobRegistry.IntendedJobIds(isSaas: true, objectStorageEnabled: false);

        await Assert.That(intended.Contains(RecurringJobIds.DataExportProcessing)).IsFalse();
        await Assert.That(intended.Contains(RecurringJobIds.DataExportCleanup)).IsFalse();
    }
}
