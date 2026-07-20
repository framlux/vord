// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Text.Json;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Server.Endpoints.Web.Machines.History;
using Framlux.FleetManagement.Services.Core.Billing;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Framlux.FleetManagement.Services.Core.Models.History;
using Framlux.FleetManagement.Services.Core.Models.Telemetry;
using Framlux.FleetManagement.Services.Core.Telemetry;
using Microsoft.AspNetCore.Http;
using NSubstitute;

namespace Framlux.FleetManagement.Test.Endpoints.Web.Machines.History;

/// <summary>
/// Unit tests for <see cref="ScalarHistoryHandler"/>, the shared validate-fetch-aggregate
/// flow extracted from the CPU and memory history endpoints.
/// </summary>
public sealed class ScalarHistoryHandlerTests
{
    private static readonly DateTimeOffset BaseTime = new(2026, 5, 6, 0, 0, 0, TimeSpan.Zero);

    private readonly IMachineRepository _machineRepo = Substitute.For<IMachineRepository>();
    private readonly IMachineStateRepository _stateRepo = Substitute.For<IMachineStateRepository>();
    private readonly ISubscriptionService _subscriptionService = Substitute.For<ISubscriptionService>();
    private readonly ITenantContext _tenantContext = Substitute.For<ITenantContext>();
    private readonly HistoryRequestValidator _validator;

    /// <summary>
    /// Initializes the validator and its mocked dependencies shared across the handler tests.
    /// </summary>
    public ScalarHistoryHandlerTests()
    {
        _validator = new HistoryRequestValidator(_machineRepo, _subscriptionService, _tenantContext);
    }

    /// <summary>
    /// Creates a MachineTelemetry row with the given payload serialized using snake_case options.
    /// </summary>
    private static MachineTelemetry CreateRow<TPayload>(TPayload payload, DateTimeOffset receivedAt, short telemetryType)
    {
        return new MachineTelemetry
        {
            Id = 0,
            MachineId = 1,
            TenantId = 1,
            TelemetryType = telemetryType,
            Payload = JsonSerializer.Serialize(payload, JsonDefaults.SnakeCase),
            ReceivedAt = receivedAt,
            ServerReceivedAt = receivedAt
        };
    }

    private static Machine CreateMachine(long id, int tenantId)
    {
        return new Machine
        {
            Id = id,
            TenantId = tenantId,
            ApiKeyHash = new string('a', 64),
            Name = "test-machine",
            SerialNumber = "SN-001",
            SystemId = "SYS-001",
            MachineType = MachineTypes.BareMetalServer,
            OperatingSystem = OperatingSystems.Ubuntu,
            RegistrationTokenId = 1,
            RegisteredOn = DateTimeOffset.UtcNow,
            IsDeleted = false
        };
    }

    [Test]
    public async Task NoTenantClaim_ReturnsNullAndWrites403()
    {
        _tenantContext.TenantId.Returns((int?)null);
        HttpContext httpContext = new DefaultHttpContext();

        HistoryResponseDto? result = await ScalarHistoryHandler.HandleScalarHistoryAsync<CpuUsagePayload>(
            machineId: 1, range: "1h", TelemetryTypeIds.CpuUsage, payload => payload.CpuUsagePercent,
            _validator, _stateRepo, httpContext, CancellationToken.None);

        await Assert.That(result).IsNull();
        await Assert.That(httpContext.Response.StatusCode).IsEqualTo(403);
        await _stateRepo.DidNotReceive().GetTelemetryHistoryAsync(
            Arg.Any<long>(), Arg.Any<short>(), Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task MachineNotFound_ReturnsNullAndWrites404()
    {
        _tenantContext.TenantId.Returns(1);
        _machineRepo.GetActiveMachineByIdAsync(1, 1, Arg.Any<CancellationToken>())
            .Returns((Machine?)null);
        HttpContext httpContext = new DefaultHttpContext();

        HistoryResponseDto? result = await ScalarHistoryHandler.HandleScalarHistoryAsync<CpuUsagePayload>(
            machineId: 1, range: "1h", TelemetryTypeIds.CpuUsage, payload => payload.CpuUsagePercent,
            _validator, _stateRepo, httpContext, CancellationToken.None);

        await Assert.That(result).IsNull();
        await Assert.That(httpContext.Response.StatusCode).IsEqualTo(404);
    }

    [Test]
    public async Task InvalidRange_ReturnsNullAndWrites400()
    {
        _tenantContext.TenantId.Returns(1);
        _machineRepo.GetActiveMachineByIdAsync(1, 1, Arg.Any<CancellationToken>())
            .Returns(CreateMachine(1, 1));
        _subscriptionService.GetRetentionDaysForTenantAsync(1, Arg.Any<CancellationToken>())
            .Returns(30);
        HttpContext httpContext = new DefaultHttpContext();

        HistoryResponseDto? result = await ScalarHistoryHandler.HandleScalarHistoryAsync<CpuUsagePayload>(
            machineId: 1, range: "not-a-range", TelemetryTypeIds.CpuUsage, payload => payload.CpuUsagePercent,
            _validator, _stateRepo, httpContext, CancellationToken.None);

        await Assert.That(result).IsNull();
        await Assert.That(httpContext.Response.StatusCode).IsEqualTo(400);
    }

    [Test]
    public async Task ValidCpuRequest_FetchesUsingSuppliedTelemetryTypeAndSelector()
    {
        _tenantContext.TenantId.Returns(1);
        _machineRepo.GetActiveMachineByIdAsync(1, 1, Arg.Any<CancellationToken>())
            .Returns(CreateMachine(1, 1));
        _subscriptionService.GetRetentionDaysForTenantAsync(1, Arg.Any<CancellationToken>())
            .Returns(30);

        MachineTelemetry row = CreateRow(new CpuUsagePayload { CpuUsagePercent = 42 }, BaseTime, TelemetryTypeIds.CpuUsage);
        _stateRepo.GetTelemetryHistoryAsync(1, TelemetryTypeIds.CpuUsage, Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns([row]);
        HttpContext httpContext = new DefaultHttpContext();

        HistoryResponseDto? result = await ScalarHistoryHandler.HandleScalarHistoryAsync<CpuUsagePayload>(
            machineId: 1, range: "1h", TelemetryTypeIds.CpuUsage, payload => payload.CpuUsagePercent,
            _validator, _stateRepo, httpContext, CancellationToken.None);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.RawPointCount).IsEqualTo(1);
        await Assert.That(result.Stats.Min).IsEqualTo(42.0);
        await Assert.That(result.Stats.Max).IsEqualTo(42.0);
    }

    [Test]
    public async Task ValidMemoryRequest_UsesMemorySelectorAndTelemetryType()
    {
        _tenantContext.TenantId.Returns(1);
        _machineRepo.GetActiveMachineByIdAsync(1, 1, Arg.Any<CancellationToken>())
            .Returns(CreateMachine(1, 1));
        _subscriptionService.GetRetentionDaysForTenantAsync(1, Arg.Any<CancellationToken>())
            .Returns(30);

        MachineTelemetry row = CreateRow(
            new MemoryUsagePayload { MemoryTotal = 16_000_000_000, MemoryUsed = 4_000_000_000, MemoryUsagePercent = 25 },
            BaseTime, TelemetryTypeIds.MemoryUsage);
        _stateRepo.GetTelemetryHistoryAsync(1, TelemetryTypeIds.MemoryUsage, Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns([row]);
        HttpContext httpContext = new DefaultHttpContext();

        HistoryResponseDto? result = await ScalarHistoryHandler.HandleScalarHistoryAsync<MemoryUsagePayload>(
            machineId: 1, range: "1h", TelemetryTypeIds.MemoryUsage, payload => payload.MemoryUsagePercent,
            _validator, _stateRepo, httpContext, CancellationToken.None);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.RawPointCount).IsEqualTo(1);
        await Assert.That(result.Stats.Min).IsEqualTo(25.0);
    }

    [Test]
    public async Task NullPayloadRow_SkippedWithoutCrash()
    {
        _tenantContext.TenantId.Returns(1);
        _machineRepo.GetActiveMachineByIdAsync(1, 1, Arg.Any<CancellationToken>())
            .Returns(CreateMachine(1, 1));
        _subscriptionService.GetRetentionDaysForTenantAsync(1, Arg.Any<CancellationToken>())
            .Returns(30);

        MachineTelemetry nullRow = new()
        {
            Id = 0,
            MachineId = 1,
            TenantId = 1,
            TelemetryType = TelemetryTypeIds.CpuUsage,
            Payload = "null",
            ReceivedAt = BaseTime,
            ServerReceivedAt = BaseTime
        };
        _stateRepo.GetTelemetryHistoryAsync(1, TelemetryTypeIds.CpuUsage, Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns([nullRow]);
        HttpContext httpContext = new DefaultHttpContext();

        HistoryResponseDto? result = await ScalarHistoryHandler.HandleScalarHistoryAsync<CpuUsagePayload>(
            machineId: 1, range: "1h", TelemetryTypeIds.CpuUsage, payload => payload.CpuUsagePercent,
            _validator, _stateRepo, httpContext, CancellationToken.None);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.RawPointCount).IsEqualTo(0);
    }
}
