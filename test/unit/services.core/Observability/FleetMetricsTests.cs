// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Deployment;
using Framlux.FleetManagement.Services.Core.Models.Machines;
using Framlux.FleetManagement.Services.Core.Observability;
using Framlux.FleetManagement.Services.Core.Options;
using Framlux.FleetManagement.Test.Infrastructure;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using System.Diagnostics.Metrics;

namespace Framlux.FleetManagement.Test.Observability;

/// <summary>
/// Pins the fleet gauge. Its explicit zeros are not cosmetic: the ingest-stalled rule is gated on
/// this series, so a status that reports nothing instead of zero turns that rule into one that can
/// never fire.
/// </summary>
public sealed class FleetMetricsTests
{
    private const string PausedInstrument = "vord.fleet.paused_machines";

    private static (FleetMetrics Metrics, IMeterFactory Factory) Build(
        IMachineStateRepository repository,
        IMachineRepository? machines = null,
        ISubscriptionRepository? subscriptions = null,
        ITierFeatureLimitRepository? tierRows = null,
        ITenantSubscriptionOverrideRepository? overrides = null,
        TierDefaultOptions? defaults = null,
        bool selfHosted = false)
    {
        IMeterFactory factory = TestMetricsFactory.CreateMeterFactory();

        // The substituted repositories are served from the additional-services dictionary before the
        // context is ever reached, so no database is needed here.
        TestServiceScopeFactory scopeFactory = new(
            null!,
            new Dictionary<Type, object>
            {
                [typeof(IMachineStateRepository)] = repository,
                [typeof(IMachineRepository)] = machines ?? MachinesWithRegisteredCounts(),
                [typeof(ISubscriptionRepository)] = subscriptions ?? SubscriptionsFor(),
                [typeof(ITierFeatureLimitRepository)] = tierRows ?? TierRows(),
                [typeof(ITenantSubscriptionOverrideRepository)] = overrides ?? Overrides(),
                [typeof(IOptions<TierDefaultOptions>)] = Options.Create(defaults ?? Defaults()),
                [typeof(DeploymentMode)] = new DeploymentMode(Options.Create(new DeploymentOptions { SelfHosted = selfHosted })),
            });

        return (new FleetMetrics(factory, scopeFactory, NullLogger<FleetMetrics>.Instance), factory);
    }

    private static IMachineStateRepository EmptyFleet()
    {
        IMachineStateRepository repository = Substitute.For<IMachineStateRepository>();
        repository.GetFleetMachineCountsByHealthAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<short, int>>(new Dictionary<short, int>()));

        return repository;
    }

    private static IMachineRepository MachinesWithRegisteredCounts(params (int TenantId, int Count)[] counts)
    {
        IMachineRepository machines = Substitute.For<IMachineRepository>();
        machines.GetRegisteredMachineCountsForAllTenantsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(counts.ToDictionary(c => c.TenantId, c => c.Count)));

        return machines;
    }

    private static ISubscriptionRepository SubscriptionsFor(params TenantSubscription[] subscriptions)
    {
        ISubscriptionRepository repository = Substitute.For<ISubscriptionRepository>();
        repository.GetSubscriptionsForTenantsAsync(Arg.Any<List<int>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(subscriptions.ToList()));

        return repository;
    }

    private static ITierFeatureLimitRepository TierRows(params TierFeatureLimit[] rows)
    {
        ITierFeatureLimitRepository repository = Substitute.For<ITierFeatureLimitRepository>();
        repository.GetAllLimitsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(rows.ToList()));

        return repository;
    }

    private static ITenantSubscriptionOverrideRepository Overrides(params TenantSubscriptionOverride[] overrides)
    {
        ITenantSubscriptionOverrideRepository repository = Substitute.For<ITenantSubscriptionOverrideRepository>();
        repository.GetOverridesForTenantsAsync(Arg.Any<List<int>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(overrides.ToList()));

        return repository;
    }

    private static TierFeatureLimit TierRow(SubscriptionTier tier, int machineLimit)
    {
        return new TierFeatureLimit
        {
            Tier = tier,
            MachineLimit = machineLimit,
            RetentionDays = 30,
            AlertRuleLimit = 10,
            WebhookLimit = 5,
            MemberLimit = 5,
            MinimumBillableMachines = 0,
        };
    }

    private static TierDefaultOptions Defaults()
    {
        return new TierDefaultOptions
        {
            Free = new() { MachineLimit = 3 },
            Pro = new() { MachineLimit = 1000 },
            Team = new() { MachineLimit = 10000 },
            Enterprise = new() { MachineLimit = 10000 },
        };
    }

    [Test]
    public async Task Gauge_EmitsOneSeriesPerStatusEvenWithNoMachines()
    {
        // Production has no tenants today. A gauge that reported nothing here would leave the
        // ingest rule's gate with no series to evaluate against.
        IMachineStateRepository repository = Substitute.For<IMachineStateRepository>();
        repository.GetFleetMachineCountsByHealthAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<short, int>>(new Dictionary<short, int>()));

        (FleetMetrics metrics, IMeterFactory factory) = Build(repository);
        using MetricCollector<long> collector = new(factory, VordMeter.Name, "vord.fleet.machines");

        collector.RecordObservableInstruments();

        IReadOnlyList<CollectedMeasurement<long>> measurements = collector.GetMeasurementSnapshot();
        await Assert.That(measurements.Count).IsEqualTo(4);
        await Assert.That(measurements.All(measurement => measurement.Value == 0L)).IsTrue();
        await Assert.That(metrics).IsNotNull();
    }

    [Test]
    public async Task Gauge_ReportsTheCountForEachStatus()
    {
        IMachineStateRepository repository = Substitute.For<IMachineStateRepository>();
        repository.GetFleetMachineCountsByHealthAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<short, int>>(new Dictionary<short, int>
            {
                [(short)MachineHealthStatus.Healthy] = 3,
                [(short)MachineHealthStatus.Offline] = 1,
            }));

        (FleetMetrics metrics, IMeterFactory factory) = Build(repository);
        using MetricCollector<long> collector = new(factory, VordMeter.Name, "vord.fleet.machines");

        collector.RecordObservableInstruments();

        IReadOnlyList<CollectedMeasurement<long>> measurements = collector.GetMeasurementSnapshot();
        CollectedMeasurement<long> healthy = measurements.Single(m => (string?)m.Tags["status"] == "healthy");
        CollectedMeasurement<long> offline = measurements.Single(m => (string?)m.Tags["status"] == "offline");
        CollectedMeasurement<long> warning = measurements.Single(m => (string?)m.Tags["status"] == "warning");

        await Assert.That(healthy.Value).IsEqualTo(3L);
        await Assert.That(offline.Value).IsEqualTo(1L);
        await Assert.That(warning.Value).IsEqualTo(0L);
        await Assert.That(metrics).IsNotNull();
    }

    [Test]
    public async Task Gauge_WhenStorageThrows_YieldsNoMeasurement()
    {
        IMachineStateRepository repository = Substitute.For<IMachineStateRepository>();
        repository.GetFleetMachineCountsByHealthAsync(Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyDictionary<short, int>>>(_ => throw new InvalidOperationException("database unavailable"));

        (FleetMetrics metrics, IMeterFactory factory) = Build(repository);
        using MetricCollector<long> collector = new(factory, VordMeter.Name, "vord.fleet.machines");

        collector.RecordObservableInstruments();

        await Assert.That(collector.GetMeasurementSnapshot().Count).IsEqualTo(0);
        await Assert.That(metrics).IsNotNull();
    }

    [Test]
    public async Task PausedGauge_SumsEveryTenantsPausedHosts()
    {
        // Tenant 1 is over a 3-host override by two, tenant 2 is under the Pro row's 1000, and tenant
        // 3's override of 0 pauses all four of its hosts: 2 + 0 + 4.
        (FleetMetrics metrics, IMeterFactory factory) = Build(
            EmptyFleet(),
            machines: MachinesWithRegisteredCounts((1, 5), (2, 2), (3, 4)),
            subscriptions: SubscriptionsFor(
                TestDataBuilder.BuildSubscription(tenantId: 1, tier: SubscriptionTier.Pro),
                TestDataBuilder.BuildSubscription(tenantId: 2, tier: SubscriptionTier.Pro),
                TestDataBuilder.BuildSubscription(tenantId: 3, tier: SubscriptionTier.Pro)),
            tierRows: TierRows(TierRow(SubscriptionTier.Pro, machineLimit: 1000)),
            overrides: Overrides(
                new TenantSubscriptionOverride { TenantId = 1, MachineLimit = 3 },
                new TenantSubscriptionOverride { TenantId = 3, MachineLimit = 0 }));
        using MetricCollector<long> collector = new(factory, VordMeter.Name, PausedInstrument);

        collector.RecordObservableInstruments();

        IReadOnlyList<CollectedMeasurement<long>> measurements = collector.GetMeasurementSnapshot();
        await Assert.That(measurements.Count).IsEqualTo(1);
        await Assert.That(measurements[0].Value).IsEqualTo(6L);
        await Assert.That(measurements[0].Tags.Count).IsEqualTo(0);
        await Assert.That(metrics).IsNotNull();
    }

    [Test]
    public async Task PausedGauge_TenantWithoutSubscription_UsesFreeDefaults()
    {
        // A missing subscription row resolves to the Free default of 3, the same way pausing does.
        (FleetMetrics metrics, IMeterFactory factory) = Build(
            EmptyFleet(),
            machines: MachinesWithRegisteredCounts((7, 10)));
        using MetricCollector<long> collector = new(factory, VordMeter.Name, PausedInstrument);

        collector.RecordObservableInstruments();

        IReadOnlyList<CollectedMeasurement<long>> measurements = collector.GetMeasurementSnapshot();
        await Assert.That(measurements.Count).IsEqualTo(1);
        await Assert.That(measurements[0].Value).IsEqualTo(7L);
        await Assert.That(metrics).IsNotNull();
    }

    [Test]
    public async Task PausedGauge_TenantWithoutSubscription_IgnoresItsOverride()
    {
        // The override of a tenant with no subscription row is ignored by the shared fold; the gauge
        // must agree with what pausing actually refuses.
        (FleetMetrics metrics, IMeterFactory factory) = Build(
            EmptyFleet(),
            machines: MachinesWithRegisteredCounts((7, 10)),
            overrides: Overrides(new TenantSubscriptionOverride { TenantId = 7, MachineLimit = 100 }));
        using MetricCollector<long> collector = new(factory, VordMeter.Name, PausedInstrument);

        collector.RecordObservableInstruments();

        await Assert.That(collector.GetMeasurementSnapshot().Single().Value).IsEqualTo(7L);
        await Assert.That(metrics).IsNotNull();
    }

    [Test]
    public async Task PausedGauge_MissingTierRow_FallsBackToTheTiersConfigDefault()
    {
        (FleetMetrics metrics, IMeterFactory factory) = Build(
            EmptyFleet(),
            machines: MachinesWithRegisteredCounts((1, 1005)),
            subscriptions: SubscriptionsFor(TestDataBuilder.BuildSubscription(tenantId: 1, tier: SubscriptionTier.Pro)));
        using MetricCollector<long> collector = new(factory, VordMeter.Name, PausedInstrument);

        collector.RecordObservableInstruments();

        await Assert.That(collector.GetMeasurementSnapshot().Single().Value).IsEqualTo(5L);
        await Assert.That(metrics).IsNotNull();
    }

    [Test]
    public async Task PausedGauge_NoTenants_ReportsZero()
    {
        // The ingest rule subtracts this series, so it has to exist even when nothing is paused.
        (FleetMetrics metrics, IMeterFactory factory) = Build(EmptyFleet());
        using MetricCollector<long> collector = new(factory, VordMeter.Name, PausedInstrument);

        collector.RecordObservableInstruments();

        IReadOnlyList<CollectedMeasurement<long>> measurements = collector.GetMeasurementSnapshot();
        await Assert.That(measurements.Count).IsEqualTo(1);
        await Assert.That(measurements[0].Value).IsEqualTo(0L);
        await Assert.That(metrics).IsNotNull();
    }

    [Test]
    public async Task PausedGauge_SelfHosted_ReportsZeroWithoutQueryingLimits()
    {
        // Self-hosted never pauses and this path bypasses the self-hosted subscription decorator, so
        // it must not fold a Free-tier limit over a fleet that has none.
        IMachineRepository machines = MachinesWithRegisteredCounts((1, 50));
        ISubscriptionRepository subscriptions = SubscriptionsFor();
        ITierFeatureLimitRepository tierRows = TierRows();
        ITenantSubscriptionOverrideRepository overrides = Overrides();
        (FleetMetrics metrics, IMeterFactory factory) = Build(
            EmptyFleet(), machines, subscriptions, tierRows, overrides, selfHosted: true);
        using MetricCollector<long> collector = new(factory, VordMeter.Name, PausedInstrument);

        collector.RecordObservableInstruments();

        IReadOnlyList<CollectedMeasurement<long>> measurements = collector.GetMeasurementSnapshot();
        await Assert.That(measurements.Count).IsEqualTo(1);
        await Assert.That(measurements[0].Value).IsEqualTo(0L);
        await subscriptions.DidNotReceiveWithAnyArgs().GetSubscriptionsForTenantsAsync(default!, default);
        await tierRows.DidNotReceiveWithAnyArgs().GetAllLimitsAsync(default);
        await overrides.DidNotReceiveWithAnyArgs().GetOverridesForTenantsAsync(default!, default);
        await Assert.That(metrics).IsNotNull();
    }

    [Test]
    public async Task PausedGauge_WhenALoaderThrows_YieldsNoMeasurement()
    {
        // Reporting nothing rather than zero keeps a broken read from looking like "nothing paused",
        // and the health gauge in the same process must be unaffected.
        ISubscriptionRepository subscriptions = Substitute.For<ISubscriptionRepository>();
        subscriptions.GetSubscriptionsForTenantsAsync(Arg.Any<List<int>>(), Arg.Any<CancellationToken>())
            .Returns<Task<List<TenantSubscription>>>(_ => throw new InvalidOperationException("database unavailable"));

        (FleetMetrics metrics, IMeterFactory factory) = Build(
            EmptyFleet(),
            machines: MachinesWithRegisteredCounts((1, 5)),
            subscriptions: subscriptions);
        using MetricCollector<long> paused = new(factory, VordMeter.Name, PausedInstrument);
        using MetricCollector<long> health = new(factory, VordMeter.Name, "vord.fleet.machines");

        paused.RecordObservableInstruments();
        health.RecordObservableInstruments();

        await Assert.That(paused.GetMeasurementSnapshot().Count).IsEqualTo(0);
        await Assert.That(health.GetMeasurementSnapshot().Count).IsEqualTo(4);
        await Assert.That(metrics).IsNotNull();
    }
}
