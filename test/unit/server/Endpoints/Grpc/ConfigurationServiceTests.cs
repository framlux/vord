// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Grpc.AgentCommon;
using Framlux.FleetManagement.Grpc.AgentConfiguration;
using Framlux.FleetManagement.Server.Endpoints.Grpc;
using Framlux.FleetManagement.Services.Core.Billing;
using Framlux.FleetManagement.Services.Core.Machines;
using Framlux.FleetManagement.Services.Core.Observability;
using Framlux.FleetManagement.Services.Core.ServerConfiguration;
using Framlux.FleetManagement.Test.Infrastructure;
using Grpc.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using StackExchange.Redis;
using System.Diagnostics.Metrics;
using System.Security.Claims;

namespace Framlux.FleetManagement.Test.Endpoints.Grpc;

/// <summary>
/// Unit tests for <see cref="ConfigurationService"/>.
/// </summary>
public sealed class ConfigurationServiceTests
{
    private readonly IMachinePingService _pingService = Substitute.For<IMachinePingService>();
    private readonly ILogger<ConfigurationService> _logger = Substitute.For<ILogger<ConfigurationService>>();

    [Test]
    [Arguments(Framlux.FleetManagement.Grpc.AgentRegistration.MachineType.VirtualMachineType, MachineTypes.VirtualMachine)]
    [Arguments(Framlux.FleetManagement.Grpc.AgentRegistration.MachineType.BareMetalServerType, MachineTypes.BareMetalServer)]
    [Arguments(Framlux.FleetManagement.Grpc.AgentRegistration.MachineType.DesktopType, MachineTypes.Desktop)]
    [Arguments(Framlux.FleetManagement.Grpc.AgentRegistration.MachineType.LaptopType, MachineTypes.Laptop)]
    public async Task ResolveReportedMachineType_KnownType_MapsToStoredEnum(
        Framlux.FleetManagement.Grpc.AgentRegistration.MachineType reported,
        MachineTypes expected)
    {
        await Assert.That(ConfigurationService.ResolveReportedMachineType(reported)).IsEqualTo(expected);
    }

    [Test]
    public async Task ResolveReportedMachineType_UnknownType_IsIgnored()
    {
        // UnknownType means the agent could not classify the host, not that the host is unknown.
        // Treating it as an assertion would let a detection failure erase a good stored value.
        await Assert.That(ConfigurationService.ResolveReportedMachineType(
            Framlux.FleetManagement.Grpc.AgentRegistration.MachineType.UnknownType)).IsNull();
    }

    [Test]
    public async Task ResolveReportedMachineType_OutOfRangeValue_IsIgnored()
    {
        // An agent must not be able to write an enum value the rest of the system cannot read.
        await Assert.That(ConfigurationService.ResolveReportedMachineType(
            (Framlux.FleetManagement.Grpc.AgentRegistration.MachineType)999)).IsNull();
    }

    [Test]
    public async Task GetConfiguration_ReportedType_CorrectsStoredMachineType()
    {
        IMachineRepository machineRepo = Substitute.For<IMachineRepository>();
        ConfigurationService service = CreateService(machineRepo: machineRepo);

        GetConfigurationRequest request = new()
        {
            MachineId = 42,
            MachineType = Framlux.FleetManagement.Grpc.AgentRegistration.MachineType.VirtualMachineType,
        };

        await service.GetConfiguration(request, CreateAuthenticatedContext(42));

        await machineRepo.Received(1).UpdateMachineTypeAsync(42, MachineTypes.VirtualMachine, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GetConfiguration_UnknownReportedType_LeavesStoredTypeAlone()
    {
        IMachineRepository machineRepo = Substitute.For<IMachineRepository>();
        ConfigurationService service = CreateService(machineRepo: machineRepo);

        // An agent that omits the field, including every agent released before it existed, sends
        // the zero value. That must never be read as an assertion about the machine.
        GetConfigurationRequest request = new() { MachineId = 42 };

        await service.GetConfiguration(request, CreateAuthenticatedContext(42));

        await machineRepo.DidNotReceive().UpdateMachineTypeAsync(Arg.Any<long>(), Arg.Any<MachineTypes>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GetConfiguration_MachineTypeUpdateFails_StillReturnsConfiguration()
    {
        // Correcting the type is opportunistic. A database fault must not cost the agent its
        // configuration fetch, which carries the timing settings the whole agent runs on.
        IMachineRepository machineRepo = Substitute.For<IMachineRepository>();
        machineRepo.UpdateMachineTypeAsync(Arg.Any<long>(), Arg.Any<MachineTypes>(), Arg.Any<CancellationToken>())
            .Returns<Task<bool>>(_ => throw new InvalidOperationException("database unavailable"));
        ConfigurationService service = CreateService(machineRepo: machineRepo);

        GetConfigurationRequest request = new()
        {
            MachineId = 42,
            MachineType = Framlux.FleetManagement.Grpc.AgentRegistration.MachineType.VirtualMachineType,
        };

        GetConfigurationResponse response = await service.GetConfiguration(request, CreateAuthenticatedContext(42));

        await Assert.That(response.TimeConfig).IsNotNull();
    }

    [Test]
    public async Task AgentPing_RecordsTheHeartbeatAtServerReceiptTime()
    {
        // The recorded time comes from the injected clock, never the agent's, so a skewed agent
        // cannot decide whether it looks live.
        IMachineStateRepository machineStateRepo = Substitute.For<IMachineStateRepository>();
        DateTimeOffset now = new(2026, 9, 13, 10, 0, 0, TimeSpan.Zero);
        ConfigurationService service = CreateService(
            machineStateRepo: machineStateRepo,
            timeProvider: new FakeTimeProvider(now));

        AgentPingResponse response = await service.AgentPing(new AgentPingRequest { MachineId = 42 }, CreateAuthenticatedContext(42));

        await Assert.That(response.Success).IsTrue();
        await machineStateRepo.Received(1).RecordHeartbeatAsync(42, now, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AgentPing_HeartbeatWriteFails_ReportsFailure()
    {
        // A ping that could not be recorded must not report success, or the agent would believe the
        // server knows it is alive when nothing was written.
        IMachineStateRepository machineStateRepo = Substitute.For<IMachineStateRepository>();
        machineStateRepo.RecordHeartbeatAsync(Arg.Any<long>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("database unavailable"));
        ConfigurationService service = CreateService(machineStateRepo: machineStateRepo);

        AgentPingResponse response = await service.AgentPing(new AgentPingRequest { MachineId = 42 }, CreateAuthenticatedContext(42));

        await Assert.That(response.Success).IsFalse();
    }

    [Test]
    public async Task AgentPing_ActiveHost_ReportsNoRejection()
    {
        ConfigurationService service = CreateService();

        AgentPingResponse response = await service.AgentPing(new AgentPingRequest { MachineId = 42 }, CreateAuthenticatedContext(42));

        await Assert.That(response.Success).IsTrue();
        await Assert.That(response.State).IsEqualTo(IngestRejectionReason.Unspecified);
    }

    [Test]
    public async Task AgentPing_PausedHost_ReportsPausedAndStillRecordsTheHeartbeat()
    {
        // The heartbeat is what tells a customer which paused hosts are still alive when choosing what
        // to remove, so pausing must never suppress it.
        IMachineStateRepository machineStateRepo = Substitute.For<IMachineStateRepository>();
        IQuotaPauseService pause = Substitute.For<IQuotaPauseService>();
        pause.IsPausedAsync(1, 42, Arg.Any<CancellationToken>()).Returns(true);
        ConfigurationService service = CreateService(machineStateRepo: machineStateRepo, quotaPause: pause);

        AgentPingResponse response = await service.AgentPing(new AgentPingRequest { MachineId = 42 }, CreateAuthenticatedContext(42));

        await Assert.That(response.Success).IsTrue();
        await Assert.That(response.State).IsEqualTo(IngestRejectionReason.HostPaused);
        await machineStateRepo.Received(1).RecordHeartbeatAsync(42, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AgentPing_InactiveSubscription_WinsOverPaused()
    {
        IQuotaPauseService pause = Substitute.For<IQuotaPauseService>();
        pause.IsPausedAsync(1, 42, Arg.Any<CancellationToken>()).Returns(true);
        ISubscriptionService subs = Substitute.For<ISubscriptionService>();
        subs.IsIngestEligibleAsync(1, Arg.Any<CancellationToken>()).Returns(false);
        ConfigurationService service = CreateService(quotaPause: pause, subscriptionService: subs);

        AgentPingResponse response = await service.AgentPing(new AgentPingRequest { MachineId = 42 }, CreateAuthenticatedContext(42));

        await Assert.That(response.Success).IsTrue();
        await Assert.That(response.State).IsEqualTo(IngestRejectionReason.SubscriptionInactive);
    }

    [Test]
    public async Task AgentPing_PauseCheckUnavailable_ReportsActive()
    {
        IQuotaPauseService pause = Substitute.For<IQuotaPauseService>();
        pause.IsPausedAsync(Arg.Any<int>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns<bool>(_ => throw new QuotaCheckUnavailableException(1, new InvalidOperationException("db")));
        ConfigurationService service = CreateService(quotaPause: pause);

        AgentPingResponse response = await service.AgentPing(new AgentPingRequest { MachineId = 42 }, CreateAuthenticatedContext(42));

        await Assert.That(response.Success).IsTrue();
        await Assert.That(response.State).IsEqualTo(IngestRejectionReason.Unspecified);
    }

    [Test]
    public async Task AgentPing_PauseCheckUnavailable_CountsTheFailureAgainstThePingSurface()
    {
        // Reporting the host as active hides that limits stopped being enforced, so the counter is the
        // only evidence, and its surface tag says which path is running unmetered.
        IMeterFactory meterFactory = TestMetricsFactory.CreateMeterFactory();
        using MetricCollector<long> failures = new(meterFactory, VordMeter.Name, "vord.quota.pause_check_failures");
        IQuotaPauseService pause = Substitute.For<IQuotaPauseService>();
        pause.IsPausedAsync(Arg.Any<int>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns<bool>(_ => throw new QuotaCheckUnavailableException(1, new InvalidOperationException("db")));
        ConfigurationService service = CreateService(quotaPause: pause, quotaPauseMetrics: new QuotaPauseMetrics(meterFactory));

        await service.AgentPing(new AgentPingRequest { MachineId = 42 }, CreateAuthenticatedContext(42));

        IReadOnlyList<CollectedMeasurement<long>> measurements = failures.GetMeasurementSnapshot();
        await Assert.That(measurements.Count).IsEqualTo(1);
        await Assert.That(measurements[0].Value).IsEqualTo(1L);
        await Assert.That(measurements[0].Tags["surface"]).IsEqualTo("ping");
    }

    [Test]
    public async Task AgentPing_PauseCheckCanceled_PropagatesTheCancellation()
    {
        // Only an unavailable decision is swallowed; a canceled call is the caller going away and must
        // not be turned into an "active" answer for an agent that will never read it.
        IQuotaPauseService pause = Substitute.For<IQuotaPauseService>();
        pause.IsPausedAsync(Arg.Any<int>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns<bool>(_ => throw new OperationCanceledException());
        ConfigurationService service = CreateService(quotaPause: pause);

        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await service.AgentPing(new AgentPingRequest { MachineId = 42 }, CreateAuthenticatedContext(42)));
    }

    [Test]
    public async Task AgentPing_SubscriptionLookupFails_StillSucceedsReportsNoStateAndKeepsTheHeartbeat()
    {
        // The heartbeat is written before the state is worked out, so a database or cache fault in the
        // subscription read must not turn a ping that did its job into a failed one. Reporting no
        // state leaves the agent sending, and ingest enforces the subscription and the pause itself.
        IMachineStateRepository machineStateRepo = Substitute.For<IMachineStateRepository>();
        IQuotaPauseService pause = Substitute.For<IQuotaPauseService>();
        pause.IsPausedAsync(Arg.Any<int>(), Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(true);
        ISubscriptionService subs = Substitute.For<ISubscriptionService>();
        subs.IsIngestEligibleAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<bool>(_ => throw new InvalidOperationException("redis unavailable"));
        ConfigurationService service = CreateService(machineStateRepo: machineStateRepo, quotaPause: pause, subscriptionService: subs);

        AgentPingResponse response = await service.AgentPing(new AgentPingRequest { MachineId = 42 }, CreateAuthenticatedContext(42));

        await Assert.That(response.Success).IsTrue();
        await Assert.That(response.State).IsEqualTo(IngestRejectionReason.Unspecified);
        await machineStateRepo.Received(1).RecordHeartbeatAsync(42, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AgentPing_SubscriptionLookupFails_CountsTheFailureAgainstThePingSurface()
    {
        // A fleet pinging through a broken subscription read looks healthy on the wire, so the counter
        // is the only evidence that the state is not being reported.
        IMeterFactory meterFactory = TestMetricsFactory.CreateMeterFactory();
        using MetricCollector<long> failures = new(meterFactory, VordMeter.Name, "vord.quota.pause_check_failures");
        ISubscriptionService subs = Substitute.For<ISubscriptionService>();
        subs.IsIngestEligibleAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<bool>(_ => throw new InvalidOperationException("database unavailable"));
        ConfigurationService service = CreateService(subscriptionService: subs, quotaPauseMetrics: new QuotaPauseMetrics(meterFactory));

        await service.AgentPing(new AgentPingRequest { MachineId = 42 }, CreateAuthenticatedContext(42));

        IReadOnlyList<CollectedMeasurement<long>> measurements = failures.GetMeasurementSnapshot();
        await Assert.That(measurements.Count).IsEqualTo(1);
        await Assert.That(measurements[0].Value).IsEqualTo(1L);
        await Assert.That(measurements[0].Tags["surface"]).IsEqualTo("ping");
    }

    [Test]
    public async Task AgentPing_SubscriptionLookupCanceled_PropagatesTheCancellation()
    {
        // Only a failed lookup is swallowed; a canceled call is the caller going away and must not be
        // turned into an answer for an agent that will never read it.
        ISubscriptionService subs = Substitute.For<ISubscriptionService>();
        subs.IsIngestEligibleAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<bool>(_ => throw new OperationCanceledException());
        ConfigurationService service = CreateService(subscriptionService: subs);

        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await service.AgentPing(new AgentPingRequest { MachineId = 42 }, CreateAuthenticatedContext(42)));
    }

    [Test]
    public async Task AgentPing_HeartbeatWriteFailsOnAPausedHost_StillReportsFailure()
    {
        // A pause never turns a failed heartbeat write into a success: the agent must retry the ping.
        IMachineStateRepository machineStateRepo = Substitute.For<IMachineStateRepository>();
        machineStateRepo.RecordHeartbeatAsync(Arg.Any<long>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("database unavailable"));
        IQuotaPauseService pause = Substitute.For<IQuotaPauseService>();
        pause.IsPausedAsync(Arg.Any<int>(), Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(true);
        ConfigurationService service = CreateService(machineStateRepo: machineStateRepo, quotaPause: pause);

        AgentPingResponse response = await service.AgentPing(new AgentPingRequest { MachineId = 42 }, CreateAuthenticatedContext(42));

        await Assert.That(response.Success).IsFalse();
    }

    private ConfigurationService CreateService(
        IServerSettingsReader? settingsCache = null,
        ISigningKeyRepository? signingKeyRepo = null,
        IRemoteCommandRepository? remoteCommandRepo = null,
        IMachineRepository? machineRepo = null,
        IMachineStateRepository? machineStateRepo = null,
        TimeProvider? timeProvider = null,
        IQuotaPauseService? quotaPause = null,
        ISubscriptionService? subscriptionService = null,
        QuotaPauseMetrics? quotaPauseMetrics = null,
        ILogger<ConfigurationService>? logger = null)
    {
        IServerSettingsReader resolvedSettingsCache = settingsCache ?? Substitute.For<IServerSettingsReader>();
        ISigningKeyRepository resolvedSigningKeyRepo = signingKeyRepo ?? Substitute.For<ISigningKeyRepository>();
        IRemoteCommandRepository resolvedRemoteCommandRepo = remoteCommandRepo ?? Substitute.For<IRemoteCommandRepository>();
        resolvedSigningKeyRepo.GetActiveSigningKeysForMachineAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(new List<Database.Models.UserSigningKey>());
        IMachineRepository resolvedMachineRepo = machineRepo ?? Substitute.For<IMachineRepository>();
        IMachineStateRepository resolvedMachineStateRepo = machineStateRepo ?? Substitute.For<IMachineStateRepository>();
        TimeProvider resolvedTimeProvider = timeProvider ?? new FakeTimeProvider(new DateTimeOffset(2026, 9, 13, 10, 0, 0, TimeSpan.Zero));
        ServerConfigurationService configService = new(resolvedSettingsCache, Substitute.For<IConnectionMultiplexer>());
        IQuotaPauseService resolvedQuotaPause = quotaPause ?? Substitute.For<IQuotaPauseService>();
        ISubscriptionService resolvedSubscriptionService = subscriptionService ?? Substitute.For<ISubscriptionService>();
        if (subscriptionService is null)
        {
            resolvedSubscriptionService.IsIngestEligibleAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(true);
        }

        return new ConfigurationService(
            resolvedSigningKeyRepo,
            resolvedRemoteCommandRepo,
            _pingService,
            resolvedMachineRepo,
            resolvedMachineStateRepo,
            resolvedSubscriptionService,
            resolvedQuotaPause,
            quotaPauseMetrics ?? TestMetricsFactory.CreateQuotaPauseMetrics(),
            configService,
            resolvedTimeProvider,
            logger ?? _logger);
    }

    private static ServerCallContext CreateAuthenticatedContext(long machineId, int tenantId = 1)
    {
        DefaultHttpContext httpContext = new();
        ClaimsIdentity identity = new("ApiKey");
        identity.AddClaim(new Claim("MachineId", machineId.ToString()));
        identity.AddClaim(new Claim("TenantId", tenantId.ToString()));
        httpContext.User = new ClaimsPrincipal(identity);

        return new TestServerCallContext(httpContext, new Metadata());
    }

    private static ServerCallContext CreateContextWithoutMachineClaim(int tenantId = 1)
    {
        DefaultHttpContext httpContext = new();
        ClaimsIdentity identity = new("ApiKey");
        identity.AddClaim(new Claim("TenantId", tenantId.ToString()));
        httpContext.User = new ClaimsPrincipal(identity);

        return new TestServerCallContext(httpContext, new Metadata());
    }

    [Test]
    public async Task GetConfiguration_NoDbSettings_ReturnsDefaults()
    {
        ConfigurationService service = CreateService();
        ServerCallContext context = CreateAuthenticatedContext(1);

        GetConfigurationResponse response = await service.GetConfiguration(
            new GetConfigurationRequest { MachineId = 1 }, context);

        // The shipped defaults, read from the constants the read paths fall back to rather than
        // copied here, so a retuned default cannot leave this assertion behind.
        await Assert.That(response.TimeConfig.HeartbeatTimeInSeconds).IsEqualTo(ServerSettingDefaults.AgentHeartbeatSeconds);
        await Assert.That(response.TimeConfig.ConfigurationRefreshTimeInSeconds).IsEqualTo(ServerSettingDefaults.AgentConfigRefreshSeconds);
    }

    [Test]
    public async Task GetConfiguration_WithDbSettings_ReturnsConfiguredValues()
    {
        IServerSettingsReader settingsCache = Substitute.For<IServerSettingsReader>();
        settingsCache.GetSettingFromDatabaseAsync(ServerConfigurationSettingKeys.AgentHeartbeatSeconds, Arg.Any<CancellationToken>())
            .Returns("60");
        settingsCache.GetSettingFromDatabaseAsync(ServerConfigurationSettingKeys.AgentConfigRefreshSeconds, Arg.Any<CancellationToken>())
            .Returns("3600");

        ConfigurationService service = CreateService(settingsCache: settingsCache);
        ServerCallContext context = CreateAuthenticatedContext(1);

        GetConfigurationResponse response = await service.GetConfiguration(
            new GetConfigurationRequest { MachineId = 1 }, context);

        await Assert.That(response.TimeConfig.HeartbeatTimeInSeconds).IsEqualTo(60);
        await Assert.That(response.TimeConfig.ConfigurationRefreshTimeInSeconds).IsEqualTo(3600);
    }

    [Test]
    public async Task GetConfiguration_NoDbSettings_ReturnsDefaultServiceStatusSeconds()
    {
        ConfigurationService service = CreateService();
        ServerCallContext context = CreateAuthenticatedContext(1);

        GetConfigurationResponse response = await service.GetConfiguration(
            new GetConfigurationRequest { MachineId = 1 }, context);

        // Default service status = 3600 seconds (1 hour).
        await Assert.That(response.TimeConfig.ServiceStatusSeconds).IsEqualTo(3600);
    }

    [Test]
    public async Task GetConfiguration_WithServiceStatusSetting_ReturnsConfiguredValue()
    {
        IServerSettingsReader settingsCache = Substitute.For<IServerSettingsReader>();
        settingsCache.GetSettingFromDatabaseAsync(ServerConfigurationSettingKeys.ServiceStatusSeconds, Arg.Any<CancellationToken>())
            .Returns("1800");

        ConfigurationService service = CreateService(settingsCache: settingsCache);
        ServerCallContext context = CreateAuthenticatedContext(1);

        GetConfigurationResponse response = await service.GetConfiguration(
            new GetConfigurationRequest { MachineId = 1 }, context);

        await Assert.That(response.TimeConfig.ServiceStatusSeconds).IsEqualTo(1800);
    }

    [Test]
    public async Task GetConfiguration_MismatchedMachineId_ThrowsPermissionDenied()
    {
        ConfigurationService service = CreateService();
        ServerCallContext context = CreateAuthenticatedContext(42);

        GetConfigurationRequest request = new() { MachineId = 99 };

        try
        {
            await service.GetConfiguration(request, context);
            Assert.Fail("Expected RpcException");
        }
        catch (RpcException ex)
        {
            await Assert.That(ex.StatusCode).IsEqualTo(StatusCode.PermissionDenied);
        }
    }

    [Test]
    public async Task AgentPing_MismatchedMachineId_ThrowsPermissionDenied()
    {
        ConfigurationService service = CreateService();
        ServerCallContext context = CreateAuthenticatedContext(42);

        AgentPingRequest request = new() { MachineId = 99 };

        try
        {
            await service.AgentPing(request, context);
            Assert.Fail("Expected RpcException");
        }
        catch (RpcException ex)
        {
            await Assert.That(ex.StatusCode).IsEqualTo(StatusCode.PermissionDenied);
        }
    }

    [Test]
    public async Task GetPendingCommands_ValidRequest_ReturnsEmptyResponse()
    {
        IRemoteCommandRepository remoteCommandRepo = Substitute.For<IRemoteCommandRepository>();
        remoteCommandRepo.GetPendingCommandsForMachineAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<Database.Models.RemoteCommand>());

        ConfigurationService service = CreateService(remoteCommandRepo: remoteCommandRepo);
        ServerCallContext context = CreateAuthenticatedContext(1);

        GetPendingCommandsResponse response = await service.GetPendingCommands(
            new GetPendingCommandsRequest { MachineId = 1 }, context);

        await Assert.That(response.Commands.Count).IsEqualTo(0);
    }

    [Test]
    public async Task GetPendingCommands_PausedHost_DeliversNothingAndMarksNothing()
    {
        IRemoteCommandRepository commands = Substitute.For<IRemoteCommandRepository>();
        IQuotaPauseService pause = Substitute.For<IQuotaPauseService>();
        pause.IsPausedAsync(1, 42, Arg.Any<CancellationToken>()).Returns(true);
        ConfigurationService service = CreateService(remoteCommandRepo: commands, quotaPause: pause);

        GetPendingCommandsResponse response = await service.GetPendingCommands(new GetPendingCommandsRequest { MachineId = 42 }, CreateAuthenticatedContext(42));

        await Assert.That(response.Commands.Count).IsEqualTo(0);
        await commands.DidNotReceiveWithAnyArgs().GetPendingCommandsForMachineAsync(default, default, default);
        await commands.DidNotReceiveWithAnyArgs().MarkCommandsDeliveredAsync(default!, default);
    }

    [Test]
    public async Task GetPendingCommands_PauseCheckUnavailable_DeliversAsActive()
    {
        // A broken quota check must never strand a paying fleet's commands.
        IRemoteCommandRepository commands = Substitute.For<IRemoteCommandRepository>();
        commands.GetPendingCommandsForMachineAsync(42, 1, Arg.Any<CancellationToken>())
            .Returns(new List<Database.Models.RemoteCommand> { BuildPendingCommand(machineId: 42) });
        IQuotaPauseService pause = Substitute.For<IQuotaPauseService>();
        pause.IsPausedAsync(Arg.Any<int>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns<bool>(_ => throw new QuotaCheckUnavailableException(1, new InvalidOperationException("db")));
        ConfigurationService service = CreateService(remoteCommandRepo: commands, quotaPause: pause);

        GetPendingCommandsResponse response = await service.GetPendingCommands(new GetPendingCommandsRequest { MachineId = 42 }, CreateAuthenticatedContext(42));

        await Assert.That(response.Commands.Count).IsEqualTo(1);
        await commands.Received(1).MarkCommandsDeliveredAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GetPendingCommands_PauseCheckUnavailable_CountsTheFailureAgainstTheCommandsSurface()
    {
        IMeterFactory meterFactory = TestMetricsFactory.CreateMeterFactory();
        using MetricCollector<long> failures = new(meterFactory, VordMeter.Name, "vord.quota.pause_check_failures");
        IRemoteCommandRepository commands = Substitute.For<IRemoteCommandRepository>();
        commands.GetPendingCommandsForMachineAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<Database.Models.RemoteCommand>());
        IQuotaPauseService pause = Substitute.For<IQuotaPauseService>();
        pause.IsPausedAsync(Arg.Any<int>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns<bool>(_ => throw new QuotaCheckUnavailableException(1, new InvalidOperationException("db")));
        ConfigurationService service = CreateService(remoteCommandRepo: commands, quotaPause: pause, quotaPauseMetrics: new QuotaPauseMetrics(meterFactory));

        await service.GetPendingCommands(new GetPendingCommandsRequest { MachineId = 42 }, CreateAuthenticatedContext(42));

        IReadOnlyList<CollectedMeasurement<long>> measurements = failures.GetMeasurementSnapshot();
        await Assert.That(measurements.Count).IsEqualTo(1);
        await Assert.That(measurements[0].Tags["surface"]).IsEqualTo("commands");
    }

    [Test]
    public async Task GetPendingCommands_HostResumes_DeliversTheCommandsHeldWhilePaused()
    {
        // Pausing only withholds delivery: nothing is marked delivered, so the command is still
        // pending and goes out on the first poll after the host is active again.
        IRemoteCommandRepository commands = Substitute.For<IRemoteCommandRepository>();
        commands.GetPendingCommandsForMachineAsync(42, 1, Arg.Any<CancellationToken>())
            .Returns(new List<Database.Models.RemoteCommand> { BuildPendingCommand(machineId: 42) });
        IQuotaPauseService pause = Substitute.For<IQuotaPauseService>();
        pause.IsPausedAsync(1, 42, Arg.Any<CancellationToken>()).Returns(true, false);
        ConfigurationService service = CreateService(remoteCommandRepo: commands, quotaPause: pause);
        ServerCallContext context = CreateAuthenticatedContext(42);

        GetPendingCommandsResponse whilePaused = await service.GetPendingCommands(new GetPendingCommandsRequest { MachineId = 42 }, context);
        GetPendingCommandsResponse afterResume = await service.GetPendingCommands(new GetPendingCommandsRequest { MachineId = 42 }, context);

        await Assert.That(whilePaused.Commands.Count).IsEqualTo(0);
        await Assert.That(afterResume.Commands.Count).IsEqualTo(1);
    }

    private static Database.Models.RemoteCommand BuildPendingCommand(long machineId, string commandId = "cmd-pending", string? commandParams = null)
    {
        return new Database.Models.RemoteCommand
        {
            Id = 1,
            CommandId = commandId,
            TenantId = 1,
            MachineId = machineId,
            UserId = 1,
            SigningKeyId = 1,
            CommandType = "reboot",
            Params = commandParams,
            Nonce = $"nonce-{commandId}",
            Signature = Convert.ToBase64String(new byte[64]),
            CanonicalPayload = "{}",
            Timestamp = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
            Status = Database.Enums.RemoteCommandStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }

    [Test]
    public async Task GetPendingCommands_ParamsWithANumericValue_DoNotStopTheOtherCommandsBeingDelivered()
    {
        // Intent: a stored row whose params are not strings used to throw out of the whole poll, which left
        // every other command for the machine undelivered. The bad row goes out with the number's own text,
        // so the agent's binding check rejects it, and the good row beside it is delivered as before.
        IRemoteCommandRepository commands = Substitute.For<IRemoteCommandRepository>();
        commands.GetPendingCommandsForMachineAsync(42, 1, Arg.Any<CancellationToken>())
            .Returns(new List<Database.Models.RemoteCommand>
            {
                BuildPendingCommand(42, "cmd-bad", """{"pid":42,"name":"x"}"""),
                BuildPendingCommand(42, "cmd-good", """{"delay_minutes":"5"}"""),
            });
        ConfigurationService service = CreateService(remoteCommandRepo: commands);

        GetPendingCommandsResponse response = await service.GetPendingCommands(
            new GetPendingCommandsRequest { MachineId = 42 }, CreateAuthenticatedContext(42));

        await Assert.That(response.Commands.Count).IsEqualTo(2);
        await Assert.That(response.Commands[0].Id).IsEqualTo("cmd-bad");
        await Assert.That(response.Commands[0].Params["pid"]).IsEqualTo("42");
        await Assert.That(response.Commands[0].Params["name"]).IsEqualTo("x");
        await Assert.That(response.Commands[1].Id).IsEqualTo("cmd-good");
        await Assert.That(response.Commands[1].Params["delay_minutes"]).IsEqualTo("5");
        await commands.Received(1).MarkCommandsDeliveredAsync(
            Arg.Is<IEnumerable<string>>(ids => ids.SequenceEqual(new[] { "cmd-bad", "cmd-good" })),
            Arg.Any<CancellationToken>());
    }

    [Test]
    [Arguments("""{"flag":true}""", "flag", "true")]
    [Arguments("""{"flag":false}""", "flag", "false")]
    [Arguments("""{"n":4.2}""", "n", "4.2")]
    [Arguments("""{"n":null}""", "n", "null")]
    [Arguments("""{"o":{"a":"b"}}""", "o", """{"a":"b"}""")]
    [Arguments("""{"l":["a","b"]}""", "l", """["a","b"]""")]
    public async Task GetPendingCommands_ParamValueThatIsNotAString_IsDeliveredAsItsRawJsonText(string storedParams, string key, string expected)
    {
        IRemoteCommandRepository commands = Substitute.For<IRemoteCommandRepository>();
        commands.GetPendingCommandsForMachineAsync(42, 1, Arg.Any<CancellationToken>())
            .Returns(new List<Database.Models.RemoteCommand> { BuildPendingCommand(42, "cmd-bad", storedParams) });
        ConfigurationService service = CreateService(remoteCommandRepo: commands);

        GetPendingCommandsResponse response = await service.GetPendingCommands(
            new GetPendingCommandsRequest { MachineId = 42 }, CreateAuthenticatedContext(42));

        await Assert.That(response.Commands.Count).IsEqualTo(1);
        await Assert.That(response.Commands[0].Params[key]).IsEqualTo(expected);
    }

    [Test]
    [Arguments("[]")]
    [Arguments("""["a"]""")]
    [Arguments("42")]
    [Arguments("\"text\"")]
    [Arguments("null")]
    [Arguments("true")]
    [Arguments("{")]
    [Arguments("not json")]
    public async Task GetPendingCommands_ParamsThatAreNotAnObject_AreDeliveredAsEmptyParamsAlongsideTheOtherCommands(string storedParams)
    {
        IRemoteCommandRepository commands = Substitute.For<IRemoteCommandRepository>();
        commands.GetPendingCommandsForMachineAsync(42, 1, Arg.Any<CancellationToken>())
            .Returns(new List<Database.Models.RemoteCommand>
            {
                BuildPendingCommand(42, "cmd-bad", storedParams),
                BuildPendingCommand(42, "cmd-good", """{"delay_minutes":"5"}"""),
            });
        ConfigurationService service = CreateService(remoteCommandRepo: commands);

        GetPendingCommandsResponse response = await service.GetPendingCommands(
            new GetPendingCommandsRequest { MachineId = 42 }, CreateAuthenticatedContext(42));

        await Assert.That(response.Commands.Count).IsEqualTo(2);
        await Assert.That(response.Commands[0].Id).IsEqualTo("cmd-bad");
        await Assert.That(response.Commands[0].Params.Count).IsEqualTo(0);
        await Assert.That(response.Commands[1].Params["delay_minutes"]).IsEqualTo("5");
        await commands.Received(1).MarkCommandsDeliveredAsync(
            Arg.Is<IEnumerable<string>>(ids => ids.SequenceEqual(new[] { "cmd-bad", "cmd-good" })),
            Arg.Any<CancellationToken>());
    }

    [Test]
    [Arguments("""{"pid":42}""")]
    [Arguments("[]")]
    [Arguments("not json")]
    public async Task GetPendingCommands_ParamsThatAreNotAnObjectOfStrings_LogOneWarningNamingTheCommand(string storedParams)
    {
        // Intent: a row like this was written by something other than the endpoint, so an operator needs to
        // find it. The warning names the command, not the machine's other commands.
        IRemoteCommandRepository commands = Substitute.For<IRemoteCommandRepository>();
        commands.GetPendingCommandsForMachineAsync(42, 1, Arg.Any<CancellationToken>())
            .Returns(new List<Database.Models.RemoteCommand>
            {
                BuildPendingCommand(42, "cmd-bad", storedParams),
                BuildPendingCommand(42, "cmd-good", """{"delay_minutes":"5"}"""),
            });
        FakeLogger<ConfigurationService> logger = new();
        ConfigurationService service = CreateService(remoteCommandRepo: commands, logger: logger);

        await service.GetPendingCommands(new GetPendingCommandsRequest { MachineId = 42 }, CreateAuthenticatedContext(42));

        IReadOnlyList<FakeLogRecord> warnings = logger.Collector.GetSnapshot().Where(r => r.Level == LogLevel.Warning).ToList();
        await Assert.That(warnings.Count).IsEqualTo(1);
        await Assert.That(warnings[0].Message).Contains("cmd-bad");
        await Assert.That(warnings[0].Message).DoesNotContain("cmd-good");
    }

    [Test]
    public async Task GetPendingCommands_ParamsThatAreNullEmptyOrStrings_LogNoWarning()
    {
        IRemoteCommandRepository commands = Substitute.For<IRemoteCommandRepository>();
        commands.GetPendingCommandsForMachineAsync(42, 1, Arg.Any<CancellationToken>())
            .Returns(new List<Database.Models.RemoteCommand>
            {
                BuildPendingCommand(42, "cmd-null", null),
                BuildPendingCommand(42, "cmd-empty", "{}"),
                BuildPendingCommand(42, "cmd-strings", """{"pid":"42"}"""),
            });
        FakeLogger<ConfigurationService> logger = new();
        ConfigurationService service = CreateService(remoteCommandRepo: commands, logger: logger);

        GetPendingCommandsResponse response = await service.GetPendingCommands(
            new GetPendingCommandsRequest { MachineId = 42 }, CreateAuthenticatedContext(42));

        await Assert.That(response.Commands.Count).IsEqualTo(3);
        await Assert.That(logger.Collector.GetSnapshot().Count(r => r.Level == LogLevel.Warning)).IsEqualTo(0);
    }

    [Test]
    public async Task GetPendingCommands_MismatchedMachineId_ThrowsPermissionDenied()
    {
        ConfigurationService service = CreateService();
        ServerCallContext context = CreateAuthenticatedContext(42);

        GetPendingCommandsRequest request = new() { MachineId = 99 };

        try
        {
            await service.GetPendingCommands(request, context);
            Assert.Fail("Expected RpcException");
        }
        catch (RpcException ex)
        {
            await Assert.That(ex.StatusCode).IsEqualTo(StatusCode.PermissionDenied);
        }
    }

    [Test]
    public async Task AcknowledgeCommand_ValidRequest_ReturnsSuccess()
    {
        IRemoteCommandRepository remoteCommandRepo = Substitute.For<IRemoteCommandRepository>();
        remoteCommandRepo.UpdateRemoteCommandStatusAsync(
            Arg.Any<string>(), Arg.Any<long>(), Arg.Any<Database.Enums.RemoteCommandStatus>(),
            Arg.Any<int?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        ConfigurationService service = CreateService(remoteCommandRepo: remoteCommandRepo);
        ServerCallContext context = CreateAuthenticatedContext(1);

        AcknowledgeCommandResponse response = await service.AcknowledgeCommand(
            new AcknowledgeCommandRequest
            {
                MachineId = 1,
                CommandId = "cmd-1",
                Result = new CommandResult { Success = true, ExitCode = 0, Message = "OK" }
            }, context);

        await Assert.That(response.Success).IsTrue();
    }

    [Test]
    public async Task AcknowledgeCommand_MismatchedMachineId_ThrowsPermissionDenied()
    {
        ConfigurationService service = CreateService();
        ServerCallContext context = CreateAuthenticatedContext(42);

        AcknowledgeCommandRequest request = new() { MachineId = 99, CommandId = "cmd-1" };

        try
        {
            await service.AcknowledgeCommand(request, context);
            Assert.Fail("Expected RpcException");
        }
        catch (RpcException ex)
        {
            await Assert.That(ex.StatusCode).IsEqualTo(StatusCode.PermissionDenied);
        }
    }

    // ========== T1: GetPendingCommands with actual data ==========

    [Test]
    public async Task GetPendingCommands_WithData_ConvertsToProtoAndMarksDelivered()
    {
        IRemoteCommandRepository remoteCommandRepo = Substitute.For<IRemoteCommandRepository>();

        Database.Models.RemoteCommand pendingCmd = new()
        {
            Id = 1,
            CommandId = "cmd-abc",
            TenantId = 1,
            MachineId = 1,
            UserId = 1,
            SigningKeyId = 1,
            CommandType = "reboot",
            Params = "{\"delay_minutes\":\"5\"}",
            Nonce = "nonce1",
            Signature = Convert.ToBase64String(new byte[64]),
            CanonicalPayload = "{}",
            Timestamp = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
            Status = Database.Enums.RemoteCommandStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        remoteCommandRepo.GetPendingCommandsForMachineAsync(1, 1, Arg.Any<CancellationToken>())
            .Returns(new List<Database.Models.RemoteCommand> { pendingCmd });
        remoteCommandRepo.MarkCommandsDeliveredAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        ConfigurationService service = CreateService(remoteCommandRepo: remoteCommandRepo);
        ServerCallContext context = CreateAuthenticatedContext(1, tenantId: 1);

        GetPendingCommandsResponse response = await service.GetPendingCommands(
            new GetPendingCommandsRequest { MachineId = 1 }, context);

        await Assert.That(response.Commands.Count).IsEqualTo(1);
        await Assert.That(response.Commands[0].Id).IsEqualTo("cmd-abc");
        await Assert.That(response.Commands[0].Type).IsEqualTo("reboot");
        await Assert.That(response.Commands[0].Params["delay_minutes"]).IsEqualTo("5");

        await remoteCommandRepo.Received(1).MarkCommandsDeliveredAsync(
            Arg.Is<IEnumerable<string>>(ids => ids.Contains("cmd-abc")),
            Arg.Any<CancellationToken>());
    }

    // ========== T2: AcknowledgeCommand failure and rejected paths ==========

    [Test]
    public async Task AcknowledgeCommand_Failure_MapsFailed()
    {
        IRemoteCommandRepository remoteCommandRepo = Substitute.For<IRemoteCommandRepository>();
        remoteCommandRepo.UpdateRemoteCommandStatusAsync(
            Arg.Any<string>(), Arg.Any<long>(), Arg.Any<Database.Enums.RemoteCommandStatus>(),
            Arg.Any<int?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        ConfigurationService service = CreateService(remoteCommandRepo: remoteCommandRepo);
        ServerCallContext context = CreateAuthenticatedContext(1);

        AcknowledgeCommandResponse response = await service.AcknowledgeCommand(
            new AcknowledgeCommandRequest
            {
                MachineId = 1,
                CommandId = "cmd-fail",
                Result = new CommandResult { Success = false, ExitCode = 1, Message = "error", ResultType = Framlux.FleetManagement.Grpc.AgentConfiguration.ResultType.Completed }
            }, context);

        await Assert.That(response.Success).IsTrue();
        await remoteCommandRepo.Received(1).UpdateRemoteCommandStatusAsync(
            "cmd-fail", 1L, Database.Enums.RemoteCommandStatus.Failed,
            Arg.Any<int?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AcknowledgeCommand_Rejected_MapsRejected()
    {
        IRemoteCommandRepository remoteCommandRepo = Substitute.For<IRemoteCommandRepository>();
        remoteCommandRepo.UpdateRemoteCommandStatusAsync(
            Arg.Any<string>(), Arg.Any<long>(), Arg.Any<Database.Enums.RemoteCommandStatus>(),
            Arg.Any<int?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        ConfigurationService service = CreateService(remoteCommandRepo: remoteCommandRepo);
        ServerCallContext context = CreateAuthenticatedContext(1);

        AcknowledgeCommandResponse response = await service.AcknowledgeCommand(
            new AcknowledgeCommandRequest
            {
                MachineId = 1,
                CommandId = "cmd-reject",
                Result = new CommandResult { Success = false, ExitCode = -1, Message = "invalid signature", ResultType = Framlux.FleetManagement.Grpc.AgentConfiguration.ResultType.Rejected }
            }, context);

        await Assert.That(response.Success).IsTrue();
        await remoteCommandRepo.Received(1).UpdateRemoteCommandStatusAsync(
            "cmd-reject", 1L, Database.Enums.RemoteCommandStatus.Rejected,
            Arg.Any<int?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AcknowledgeCommand_FailedResultType_MapsFailed()
    {
        IRemoteCommandRepository remoteCommandRepo = Substitute.For<IRemoteCommandRepository>();
        remoteCommandRepo.UpdateRemoteCommandStatusAsync(
            Arg.Any<string>(), Arg.Any<long>(), Arg.Any<Database.Enums.RemoteCommandStatus>(),
            Arg.Any<int?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        ConfigurationService service = CreateService(remoteCommandRepo: remoteCommandRepo);
        ServerCallContext context = CreateAuthenticatedContext(1);

        AcknowledgeCommandResponse response = await service.AcknowledgeCommand(
            new AcknowledgeCommandRequest
            {
                MachineId = 1,
                CommandId = "cmd-failed-type",
                Result = new CommandResult { Success = false, ExitCode = 1, Message = "reboot failed", ResultType = Framlux.FleetManagement.Grpc.AgentConfiguration.ResultType.Failed }
            }, context);

        await Assert.That(response.Success).IsTrue();
        await remoteCommandRepo.Received(1).UpdateRemoteCommandStatusAsync(
            "cmd-failed-type", 1L, Database.Enums.RemoteCommandStatus.Failed,
            Arg.Any<int?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AcknowledgeCommand_NullResult_MapsFailed()
    {
        IRemoteCommandRepository remoteCommandRepo = Substitute.For<IRemoteCommandRepository>();
        remoteCommandRepo.UpdateRemoteCommandStatusAsync(
            Arg.Any<string>(), Arg.Any<long>(), Arg.Any<Database.Enums.RemoteCommandStatus>(),
            Arg.Any<int?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        ConfigurationService service = CreateService(remoteCommandRepo: remoteCommandRepo);
        ServerCallContext context = CreateAuthenticatedContext(1);

        AcknowledgeCommandResponse response = await service.AcknowledgeCommand(
            new AcknowledgeCommandRequest
            {
                MachineId = 1,
                CommandId = "cmd-null",
            }, context);

        await Assert.That(response.Success).IsTrue();
        await remoteCommandRepo.Received(1).UpdateRemoteCommandStatusAsync(
            "cmd-null", 1L, Database.Enums.RemoteCommandStatus.Failed,
            Arg.Any<int?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GetConfiguration_WithActiveTenantId_LoadsSigningKeys()
    {
        ISigningKeyRepository signingKeyRepo = Substitute.For<ISigningKeyRepository>();

        // CreateService also stubs this, so we configure the return after creating the service.
        ConfigurationService service = CreateService(signingKeyRepo: signingKeyRepo);

        // Override to return a real signing key — the CreateService factory sets empty by default.
        signingKeyRepo.GetActiveSigningKeysForMachineAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(new List<Database.Models.UserSigningKey>
            {
                new Database.Models.UserSigningKey
                {
                    Id = 1,
                    UserId = 1,
                    TenantId = 1,
                    PublicKey = Convert.ToBase64String(new byte[32]),
                    PublicKeyFingerprint = "fp1",
                    Label = "My Key",
                    CreatedAt = DateTimeOffset.UtcNow
                }
            });

        // tenantId > 0 causes the signing keys branch to execute.
        ServerCallContext context = CreateAuthenticatedContext(machineId: 1, tenantId: 5);

        GetConfigurationResponse response = await service.GetConfiguration(
            new GetConfigurationRequest { MachineId = 1 }, context);

        await Assert.That(response.SigningKeys.Count).IsEqualTo(1);
        await Assert.That(response.SigningKeys[0].KeyId).IsEqualTo(1);
        await signingKeyRepo.Received(1).GetActiveSigningKeysForMachineAsync(1L, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GetConfiguration_WithZeroTenantId_SkipsSigningKeyLoad()
    {
        ISigningKeyRepository signingKeyRepo = Substitute.For<ISigningKeyRepository>();
        signingKeyRepo.GetActiveSigningKeysForMachineAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(new List<Database.Models.UserSigningKey>());

        ConfigurationService service = CreateService(signingKeyRepo: signingKeyRepo);

        // tenantId = 0 (no TenantId claim) means the signing keys branch is skipped.
        DefaultHttpContext httpContext = new();
        ClaimsIdentity identity = new("ApiKey");
        identity.AddClaim(new Claim("MachineId", "1"));
        httpContext.User = new ClaimsPrincipal(identity);
        ServerCallContext context = new TestServerCallContext(httpContext, new Metadata());

        GetConfigurationResponse response = await service.GetConfiguration(
            new GetConfigurationRequest { MachineId = 1 }, context);

        await Assert.That(response.SigningKeys.Count).IsEqualTo(0);
        await signingKeyRepo.DidNotReceive().GetActiveSigningKeysForMachineAsync(
            Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    // ========== Missing machine claim must be rejected, never falling back to the request ==========

    [Test]
    public async Task GetConfiguration_NoMachineClaim_ThrowsUnauthenticated()
    {
        ISigningKeyRepository signingKeyRepo = Substitute.For<ISigningKeyRepository>();
        ConfigurationService service = CreateService(signingKeyRepo: signingKeyRepo);
        ServerCallContext context = CreateContextWithoutMachineClaim();

        // A caller-supplied MachineId must never be trusted when the authenticated principal lacks
        // the claim; the request id (99) must not be consulted for any downstream work.
        GetConfigurationRequest request = new() { MachineId = 99 };

        try
        {
            await service.GetConfiguration(request, context);
            Assert.Fail("Expected RpcException");
        }
        catch (RpcException ex)
        {
            await Assert.That(ex.StatusCode).IsEqualTo(StatusCode.Unauthenticated);
        }

        await signingKeyRepo.DidNotReceive().GetActiveSigningKeysForMachineAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
        await _pingService.DidNotReceive().SetAgentCapabilitiesAsync(Arg.Any<long>(), Arg.Any<ulong>());
    }

    [Test]
    public async Task AgentPing_NoMachineClaim_ThrowsUnauthenticated()
    {
        IMachineStateRepository machineStateRepo = Substitute.For<IMachineStateRepository>();
        ConfigurationService service = CreateService(machineStateRepo: machineStateRepo);
        ServerCallContext context = CreateContextWithoutMachineClaim();

        AgentPingRequest request = new() { MachineId = 99 };

        try
        {
            await service.AgentPing(request, context);
            Assert.Fail("Expected RpcException");
        }
        catch (RpcException ex)
        {
            await Assert.That(ex.StatusCode).IsEqualTo(StatusCode.Unauthenticated);
        }

        await machineStateRepo.DidNotReceive().RecordHeartbeatAsync(
            Arg.Any<long>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GetPendingCommands_NoMachineClaim_ThrowsUnauthenticated()
    {
        IRemoteCommandRepository remoteCommandRepo = Substitute.For<IRemoteCommandRepository>();
        ConfigurationService service = CreateService(remoteCommandRepo: remoteCommandRepo);
        ServerCallContext context = CreateContextWithoutMachineClaim();

        GetPendingCommandsRequest request = new() { MachineId = 99 };

        try
        {
            await service.GetPendingCommands(request, context);
            Assert.Fail("Expected RpcException");
        }
        catch (RpcException ex)
        {
            await Assert.That(ex.StatusCode).IsEqualTo(StatusCode.Unauthenticated);
        }

        await remoteCommandRepo.DidNotReceive().GetPendingCommandsForMachineAsync(
            Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AcknowledgeCommand_NoMachineClaim_ThrowsUnauthenticated()
    {
        IRemoteCommandRepository remoteCommandRepo = Substitute.For<IRemoteCommandRepository>();
        ConfigurationService service = CreateService(remoteCommandRepo: remoteCommandRepo);
        ServerCallContext context = CreateContextWithoutMachineClaim();

        AcknowledgeCommandRequest request = new() { MachineId = 99, CommandId = "cmd-1" };

        try
        {
            await service.AcknowledgeCommand(request, context);
            Assert.Fail("Expected RpcException");
        }
        catch (RpcException ex)
        {
            await Assert.That(ex.StatusCode).IsEqualTo(StatusCode.Unauthenticated);
        }

        await remoteCommandRepo.DidNotReceive().UpdateRemoteCommandStatusAsync(
            Arg.Any<string>(), Arg.Any<long>(), Arg.Any<Database.Enums.RemoteCommandStatus>(),
            Arg.Any<int?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Custom <see cref="ServerCallContext"/> subclass for testing gRPC endpoints.
    /// </summary>
    private sealed class TestServerCallContext : ServerCallContext
    {
        private readonly Metadata _requestHeaders;

        /// <summary>
        /// Creates a new test context.
        /// </summary>
        public TestServerCallContext(HttpContext httpContext, Metadata requestHeaders)
        {
            _requestHeaders = requestHeaders;
            UserState["__HttpContext"] = httpContext;
        }

        /// <inheritdoc/>
        protected override string MethodCore => "Test";

        /// <inheritdoc/>
        protected override string HostCore => "localhost";

        /// <inheritdoc/>
        protected override string PeerCore => "127.0.0.1";

        /// <inheritdoc/>
        protected override DateTime DeadlineCore => DateTime.UtcNow.AddMinutes(1);

        /// <inheritdoc/>
        protected override Metadata RequestHeadersCore => _requestHeaders;

        /// <inheritdoc/>
        protected override CancellationToken CancellationTokenCore => CancellationToken.None;

        /// <inheritdoc/>
        protected override Metadata ResponseTrailersCore => new();

        /// <inheritdoc/>
        protected override Status StatusCore { get; set; }

        /// <inheritdoc/>
        protected override WriteOptions? WriteOptionsCore { get; set; }

        /// <inheritdoc/>
        protected override AuthContext AuthContextCore => new(string.Empty, new Dictionary<string, List<AuthProperty>>());

        /// <inheritdoc/>
        protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) => throw new NotImplementedException();

        /// <inheritdoc/>
        protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) => Task.CompletedTask;
    }
}
