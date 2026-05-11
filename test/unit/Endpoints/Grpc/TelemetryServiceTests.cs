// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Grpc.AgentTelemetry;
using Framlux.FleetManagement.Server.Endpoints.Grpc;
using Framlux.FleetManagement.Server.Services.Alerts;
using Framlux.FleetManagement.Server.Services.Billing;
using Framlux.FleetManagement.Server.Services.Machines;
using Framlux.FleetManagement.Server.Services.Telemetry;
using Framlux.FleetManagement.Test.Infrastructure;
using Grpc.Core;
using LinqToDB.Async;
using LinqToDB;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Polly;
using System.Security.Claims;

namespace Framlux.FleetManagement.Test.Endpoints.Grpc;

/// <summary>
/// Unit tests for <see cref="TelemetryService"/>.
/// </summary>
public sealed class TelemetryServiceTests
{
    private readonly ITelemetryDeduplicationService _dedupService;
    private readonly ISubscriptionService _subscriptionService;
    private readonly IEventAlertService _eventAlertService = Substitute.For<IEventAlertService>();
    private readonly ILogger<TelemetryService> _logger = Substitute.For<ILogger<TelemetryService>>();

    /// <summary>
    /// Initializes a new instance of the <see cref="TelemetryServiceTests"/> class.
    /// </summary>
    public TelemetryServiceTests()
    {
        _dedupService = Substitute.For<ITelemetryDeduplicationService>();
        // Default: mark all event IDs as new (not duplicates).
        _dedupService.TryMarkSeenBatchAsync(Arg.Any<IEnumerable<string>>())
            .Returns(callInfo =>
            {
                IEnumerable<string> ids = callInfo.Arg<IEnumerable<string>>();

                return ids.ToDictionary(id => id, _ => true);
            });

        _subscriptionService = Substitute.For<ISubscriptionService>();
        // Default: subscription is active.
        _subscriptionService.GetSubscriptionForTenantAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new Database.Models.TenantSubscription
            {
                TenantId = 1,
                Tier = Database.Enums.SubscriptionTier.Free,
                Status = Database.Enums.SubscriptionStatus.Active,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
    }

    /// <summary>
    /// Creates a <see cref="ServerCallContext"/> backed by an <see cref="HttpContext"/>
    /// that has a <c>MachineId</c> claim set.
    /// </summary>
    private static ServerCallContext CreateAuthenticatedContext(long machineId, string? headerMachineId = null, int tenantId = 1)
    {
        DefaultHttpContext httpContext = new();
        ClaimsIdentity identity = new("ApiKey");
        identity.AddClaim(new Claim("MachineId", machineId.ToString()));
        identity.AddClaim(new Claim("TenantId", tenantId.ToString()));
        httpContext.User = new ClaimsPrincipal(identity);

        Metadata headers = new();
        if (headerMachineId is not null)
        {
            headers.Add("x-machine-id", headerMachineId);
        }

        TestServerCallContextWithHttp context = new(httpContext, headers);

        return context;
    }

    /// <summary>
    /// Creates a <see cref="ServerCallContext"/> with no MachineId claim (unauthenticated).
    /// </summary>
    private static ServerCallContext CreateUnauthenticatedContext()
    {
        DefaultHttpContext httpContext = new();
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity());

        return new TestServerCallContextWithHttp(httpContext, new Metadata());
    }

    private static readonly ResiliencePipeline NoOpPipeline = ResiliencePipeline.Empty;

    private TelemetryService CreateService(IServiceScopeFactory scopeFactory)
    {
        return new TelemetryService(scopeFactory, _dedupService, _subscriptionService, _eventAlertService, NoOpPipeline, _logger);
    }

    [Test]
    public async Task SubmitTelemetry_NoMachineIdClaim_ReturnsUnauthenticated()
    {
        using TestDatabaseFactory dbFactory = new();
        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        TelemetryService service = CreateService(scopeFactory);
        ServerCallContext context = CreateUnauthenticatedContext();

        TelemetryEnvelope envelope = new()
        {
            AgentTimestamp = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            BatchId ="batch-1",
        };
        envelope.Items.Add(new TelemetryItem
        {
            EventId = "event-1",
            Type = TelemetryTypes.CpuUtilizationType,
            CpuUtilization = new CpuUtilizationRecord { CpuUsagePercent =50 }
        });

        TelemetryAck ack = await service.SubmitTelemetry(envelope, context);

        await Assert.That(ack.Success).IsFalse();
        await Assert.That(ack.ErrorMessage).IsEqualTo("Could not determine machine identity");
    }

    [Test]
    public async Task SubmitTelemetry_NoMachineIdClaim_DoesNotInsertTelemetry()
    {
        using TestDatabaseFactory dbFactory = new();
        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        TelemetryService service = CreateService(scopeFactory);
        ServerCallContext context = CreateUnauthenticatedContext();

        TelemetryEnvelope envelope = new()
        {
            AgentTimestamp = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            BatchId ="batch-no-auth",
        };
        envelope.Items.Add(new TelemetryItem
        {
            EventId = "event-no-auth",
            Type = TelemetryTypes.CpuUtilizationType,
            CpuUtilization = new CpuUtilizationRecord { CpuUsagePercent = 50 }
        });

        await service.SubmitTelemetry(envelope, context);

        List<MachineTelemetry> telemetry = await dbFactory.Context.MachineTelemetry.ToListAsync();

        await Assert.That(telemetry.Count).IsEqualTo(0);
    }

    [Test]
    public async Task SubmitTelemetry_MismatchedMachineIdHeader_ReturnsPermissionDenied()
    {
        using TestDatabaseFactory dbFactory = new();
        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        TelemetryService service = CreateService(scopeFactory);

        // Claim says 42, header says 99 — mismatch.
        ServerCallContext context = CreateAuthenticatedContext(42, headerMachineId: "99");

        TelemetryEnvelope envelope = new()
        {
            AgentTimestamp = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            BatchId ="batch-2",
        };
        envelope.Items.Add(new TelemetryItem
        {
            EventId = "event-2",
            Type = TelemetryTypes.CpuUtilizationType,
            CpuUtilization = new CpuUtilizationRecord { CpuUsagePercent =50 }
        });

        TelemetryAck ack = await service.SubmitTelemetry(envelope, context);

        await Assert.That(ack.Success).IsFalse();
        await Assert.That(ack.ErrorMessage).IsEqualTo("Machine ID mismatch between API key and header");
    }

    [Test]
    public async Task SubmitTelemetry_ValidRequest_ReturnsSuccessAck()
    {
        using TestDatabaseFactory dbFactory = new();
        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        TelemetryService service = CreateService(scopeFactory);
        ServerCallContext context = CreateAuthenticatedContext(100);

        TelemetryEnvelope envelope = new()
        {
            AgentTimestamp = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            BatchId ="batch-3",
        };
        envelope.Items.Add(new TelemetryItem
        {
            EventId = "event-3",
            Type = TelemetryTypes.CpuUtilizationType,
            CpuUtilization = new CpuUtilizationRecord { CpuUsagePercent =75 }
        });

        TelemetryAck ack = await service.SubmitTelemetry(envelope, context);

        await Assert.That(ack.Success).IsTrue();
        await Assert.That(ack.BatchId).IsEqualTo("batch-3");
        await Assert.That(ack.AcknowledgedEventIds.Count).IsEqualTo(1);
        await Assert.That(ack.AcknowledgedEventIds[0]).IsEqualTo("event-3");
    }

    [Test]
    public async Task SubmitTelemetry_ValidRequest_InsertsTelemetryIntoDatabase()
    {
        using TestDatabaseFactory dbFactory = new();
        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        TelemetryService service = CreateService(scopeFactory);
        ServerCallContext context = CreateAuthenticatedContext(100);

        TelemetryEnvelope envelope = new()
        {
            AgentTimestamp = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            BatchId ="batch-3",
        };
        envelope.Items.Add(new TelemetryItem
        {
            EventId = "event-3",
            Type = TelemetryTypes.CpuUtilizationType,
            CpuUtilization = new CpuUtilizationRecord { CpuUsagePercent =75 }
        });

        await service.SubmitTelemetry(envelope, context);

        List<MachineTelemetry> telemetry = await dbFactory.Context.MachineTelemetry.ToListAsync();

        await Assert.That(telemetry.Count).IsEqualTo(1);
        await Assert.That(telemetry[0].MachineId).IsEqualTo(100);
    }

    [Test]
    public async Task SubmitTelemetry_ValidRequest_StoresTelemetryWithCorrectTenantAndEventId()
    {
        using TestDatabaseFactory dbFactory = new();
        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        TelemetryService service = CreateService(scopeFactory);
        ServerCallContext context = CreateAuthenticatedContext(100, tenantId: 5);

        TelemetryEnvelope envelope = new()
        {
            AgentTimestamp = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            BatchId ="batch-state",
        };
        envelope.Items.Add(new TelemetryItem
        {
            EventId = "event-state-1",
            Type = TelemetryTypes.CpuUtilizationType,
            CpuUtilization = new CpuUtilizationRecord { CpuUsagePercent = 75 }
        });

        TelemetryAck ack = await service.SubmitTelemetry(envelope, context);

        List<MachineTelemetry> telemetry = await dbFactory.Context.MachineTelemetry.ToListAsync();
        await Assert.That(ack.Success).IsTrue();
        await Assert.That(telemetry.Count).IsEqualTo(1);
        await Assert.That(telemetry[0].TenantId).IsEqualTo(5);
        await Assert.That(telemetry[0].SourceEventId).IsEqualTo("event-state-1");
    }

    [Test]
    public async Task SubmitTelemetry_DuplicateEventId_SkipsInsert()
    {
        using TestDatabaseFactory dbFactory = new();
        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);

        // Configure the dedup service to report "dup-event" as already seen.
        ITelemetryDeduplicationService dupDedupService = Substitute.For<ITelemetryDeduplicationService>();
        dupDedupService.TryMarkSeenBatchAsync(Arg.Any<IEnumerable<string>>())
            .Returns(callInfo =>
            {
                IEnumerable<string> ids = callInfo.Arg<IEnumerable<string>>();

                return ids.ToDictionary(id => id, _ => false);
            });

        TelemetryService service = new(scopeFactory, dupDedupService, _subscriptionService, _eventAlertService, NoOpPipeline, _logger);
        ServerCallContext context = CreateAuthenticatedContext(200);

        TelemetryEnvelope envelope = new()
        {
            AgentTimestamp = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            BatchId ="batch-4",
        };
        envelope.Items.Add(new TelemetryItem
        {
            EventId = "dup-event",
            Type = TelemetryTypes.CpuUtilizationType,
            CpuUtilization = new CpuUtilizationRecord { CpuUsagePercent =50 }
        });

        TelemetryAck ack = await service.SubmitTelemetry(envelope, context);

        await Assert.That(ack.Success).IsTrue();
        await Assert.That(ack.AcknowledgedEventIds.Count).IsEqualTo(1);

        // No rows should be inserted — Redis layer caught the duplicate.
        List<MachineTelemetry> telemetry = await dbFactory.Context.MachineTelemetry.ToListAsync();

        await Assert.That(telemetry.Count).IsEqualTo(0);
    }

    [Test]
    public async Task SubmitTelemetry_MatchingHeaderMachineId_Succeeds()
    {
        using TestDatabaseFactory dbFactory = new();
        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        TelemetryService service = CreateService(scopeFactory);

        // Claim and header both say 300 — should succeed.
        ServerCallContext context = CreateAuthenticatedContext(300, headerMachineId: "300");

        TelemetryEnvelope envelope = new()
        {
            AgentTimestamp = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            BatchId ="batch-5",
        };
        envelope.Items.Add(new TelemetryItem
        {
            EventId = "event-5",
            Type = TelemetryTypes.MemoryUtilizationType,
            MemoryUtilization = new MemoryUtilizationRecord { MemoryUsagePercent =60 }
        });

        TelemetryAck ack = await service.SubmitTelemetry(envelope, context);

        await Assert.That(ack.Success).IsTrue();
        await Assert.That(ack.BatchId).IsEqualTo("batch-5");
    }

    [Test]
    public async Task SubmitTelemetry_MultipleItems_ProcessesAll()
    {
        using TestDatabaseFactory dbFactory = new();
        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        TelemetryService service = CreateService(scopeFactory);
        ServerCallContext context = CreateAuthenticatedContext(400);

        TelemetryEnvelope envelope = new()
        {
            AgentTimestamp = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            BatchId ="batch-6",
        };
        envelope.Items.Add(new TelemetryItem
        {
            EventId = "event-6a",
            Type = TelemetryTypes.CpuUtilizationType,
            CpuUtilization = new CpuUtilizationRecord { CpuUsagePercent =30 }
        });
        envelope.Items.Add(new TelemetryItem
        {
            EventId = "event-6b",
            Type = TelemetryTypes.MemoryUtilizationType,
            MemoryUtilization = new MemoryUtilizationRecord { MemoryUsagePercent =70 }
        });

        TelemetryAck ack = await service.SubmitTelemetry(envelope, context);

        await Assert.That(ack.Success).IsTrue();
        await Assert.That(ack.AcknowledgedEventIds.Count).IsEqualTo(2);

        List<MachineTelemetry> telemetry = await dbFactory.Context.MachineTelemetry.ToListAsync();

        await Assert.That(telemetry.Count).IsEqualTo(2);
    }

    [Test]
    public async Task SubmitTelemetry_InactiveSubscription_ReturnsError()
    {
        using TestDatabaseFactory dbFactory = new();
        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);

        ISubscriptionService inactiveSubService = Substitute.For<ISubscriptionService>();
        inactiveSubService.GetSubscriptionForTenantAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new TenantSubscription
            {
                TenantId = 1,
                Tier = Database.Enums.SubscriptionTier.Free,
                Status = Database.Enums.SubscriptionStatus.PastDue,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });

        TelemetryService service = new(scopeFactory, _dedupService, inactiveSubService, _eventAlertService, NoOpPipeline, _logger);
        ServerCallContext context = CreateAuthenticatedContext(100);

        TelemetryEnvelope envelope = new() { AgentTimestamp = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow), BatchId ="batch-inactive" };
        envelope.Items.Add(new TelemetryItem
        {
            EventId = "event-inactive",
            Type = TelemetryTypes.CpuUtilizationType,
            CpuUtilization = new CpuUtilizationRecord { CpuUsagePercent = 50 }
        });

        TelemetryAck ack = await service.SubmitTelemetry(envelope, context);

        await Assert.That(ack.Success).IsFalse();
        await Assert.That(ack.ErrorMessage).IsEqualTo("Tenant subscription is not active");

        // No telemetry rows should be inserted when subscription is inactive
        List<MachineTelemetry> telemetry = await dbFactory.Context.MachineTelemetry.ToListAsync();

        await Assert.That(telemetry.Count).IsEqualTo(0);
    }

    [Test]
    public async Task SubmitTelemetry_EnvelopeExceedsMaxItems_ReturnsError()
    {
        using TestDatabaseFactory dbFactory = new();
        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        TelemetryService service = CreateService(scopeFactory);
        ServerCallContext context = CreateAuthenticatedContext(100);

        TelemetryEnvelope envelope = new() { AgentTimestamp = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow), BatchId ="batch-overflow" };
        for (int i = 0; i < 501; i++)
        {
            envelope.Items.Add(new TelemetryItem
            {
                EventId = $"event-overflow-{i}",
                Type = TelemetryTypes.CpuUtilizationType,
                CpuUtilization = new CpuUtilizationRecord { CpuUsagePercent = 50 }
            });
        }

        TelemetryAck ack = await service.SubmitTelemetry(envelope, context);

        await Assert.That(ack.Success).IsFalse();
        await Assert.That(ack.ErrorMessage).Contains("exceeds maximum item count");
    }

    [Test]
    public async Task SubmitTelemetry_EmptyEnvelope_ReturnsSuccess()
    {
        using TestDatabaseFactory dbFactory = new();
        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        TelemetryService service = CreateService(scopeFactory);
        ServerCallContext context = CreateAuthenticatedContext(100);

        TelemetryEnvelope envelope = new() { AgentTimestamp = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow), BatchId ="batch-empty" };

        TelemetryAck ack = await service.SubmitTelemetry(envelope, context);

        await Assert.That(ack.Success).IsTrue();
        await Assert.That(ack.BatchId).IsEqualTo("batch-empty");
    }

    [Test]
    public async Task SubmitTelemetry_SshConnectEvent_InvokesEventAlertEvaluation()
    {
        using TestDatabaseFactory dbFactory = new();
        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        TelemetryService service = CreateService(scopeFactory);
        ServerCallContext context = CreateAuthenticatedContext(500, tenantId: 7);

        TelemetryEnvelope envelope = new()
        {
            AgentTimestamp = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            BatchId = "batch-ssh-connect",
        };
        envelope.Items.Add(new TelemetryItem
        {
            EventId = "event-ssh-connect-1",
            Type = TelemetryTypes.SshSessionType,
            SshSession = new SshSessionRecord
            {
                User = "admin",
                SourceIp = "192.168.1.100",
                SourcePort = 54321,
                Action = "connect",
                AuthMethod = "publickey",
                Timestamp = DateTimeOffset.UtcNow.ToString("o"),
            }
        });

        TelemetryAck ack = await service.SubmitTelemetry(envelope, context);

        await Assert.That(ack.Success).IsTrue();
        await _eventAlertService.Received(1).EvaluateSshConnectAsync(
            7, 500, "admin", "192.168.1.100", 54321, "publickey", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SubmitTelemetry_SshDisconnectEvent_InvokesResolution()
    {
        using TestDatabaseFactory dbFactory = new();
        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        TelemetryService service = CreateService(scopeFactory);
        ServerCallContext context = CreateAuthenticatedContext(600, tenantId: 8);

        TelemetryEnvelope envelope = new()
        {
            AgentTimestamp = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            BatchId = "batch-ssh-disconnect",
        };
        envelope.Items.Add(new TelemetryItem
        {
            EventId = "event-ssh-disconnect-1",
            Type = TelemetryTypes.SshSessionType,
            SshSession = new SshSessionRecord
            {
                User = "deploy",
                SourceIp = "10.0.0.50",
                SourcePort = 44000,
                Action = "disconnect",
                AuthMethod = "password",
                Timestamp = DateTimeOffset.UtcNow.ToString("o"),
            }
        });

        TelemetryAck ack = await service.SubmitTelemetry(envelope, context);

        await Assert.That(ack.Success).IsTrue();
        await _eventAlertService.Received(1).ResolveSshDisconnectAsync(
            600, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SubmitTelemetry_NonSshTelemetry_DoesNotInvokeEventAlertService()
    {
        using TestDatabaseFactory dbFactory = new();
        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        TelemetryService service = CreateService(scopeFactory);
        ServerCallContext context = CreateAuthenticatedContext(700, tenantId: 9);

        TelemetryEnvelope envelope = new()
        {
            AgentTimestamp = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            BatchId = "batch-cpu-only",
        };
        envelope.Items.Add(new TelemetryItem
        {
            EventId = "event-cpu-only-1",
            Type = TelemetryTypes.CpuUtilizationType,
            CpuUtilization = new CpuUtilizationRecord { CpuUsagePercent = 85 }
        });

        TelemetryAck ack = await service.SubmitTelemetry(envelope, context);

        await Assert.That(ack.Success).IsTrue();
        await _eventAlertService.DidNotReceive().EvaluateSshConnectAsync(
            Arg.Any<int>(), Arg.Any<long>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _eventAlertService.DidNotReceive().ResolveSshDisconnectAsync(
            Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SubmitTelemetry_SshAlertServiceThrows_TelemetryStillProcessed()
    {
        using TestDatabaseFactory dbFactory = new();
        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        TelemetryService service = CreateService(scopeFactory);
        ServerCallContext context = CreateAuthenticatedContext(800, tenantId: 10);

        _eventAlertService.EvaluateSshConnectAsync(
            Arg.Any<int>(), Arg.Any<long>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task>(callInfo => throw new InvalidOperationException("Alert service failure"));

        TelemetryEnvelope envelope = new()
        {
            AgentTimestamp = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            BatchId = "batch-ssh-throws",
        };
        envelope.Items.Add(new TelemetryItem
        {
            EventId = "event-ssh-throws-1",
            Type = TelemetryTypes.SshSessionType,
            SshSession = new SshSessionRecord
            {
                User = "root",
                SourceIp = "172.16.0.1",
                SourcePort = 22222,
                Action = "connect",
                AuthMethod = "keyboard-interactive",
                Timestamp = DateTimeOffset.UtcNow.ToString("o"),
            }
        });

        TelemetryAck ack = await service.SubmitTelemetry(envelope, context);

        await Assert.That(ack.Success).IsTrue();
        await Assert.That(ack.AcknowledgedEventIds.Count).IsEqualTo(1);
        await Assert.That(ack.AcknowledgedEventIds[0]).IsEqualTo("event-ssh-throws-1");
    }

    // ========================================================================
    // StreamTelemetry tests
    // ========================================================================

    [Test]
    public async Task StreamTelemetry_NoMachineIdClaim_SetsUnauthenticatedStatus()
    {
        using TestDatabaseFactory dbFactory = new();
        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        TelemetryService service = CreateService(scopeFactory);
        ServerCallContext context = CreateUnauthenticatedContext();

        FakeAsyncStreamReader<TelemetryEnvelope> requestStream = new([]);
        FakeServerStreamWriter<TelemetryAck> responseStream = new();

        await service.StreamTelemetry(requestStream, responseStream, context);

        await Assert.That(context.Status.StatusCode).IsEqualTo(StatusCode.Unauthenticated);
        await Assert.That(responseStream.Written.Count).IsEqualTo(0);
    }

    [Test]
    public async Task StreamTelemetry_MismatchedMachineIdHeader_SetsPermissionDeniedStatus()
    {
        using TestDatabaseFactory dbFactory = new();
        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        TelemetryService service = CreateService(scopeFactory);

        // Claim says 42, header says 99 — mismatch returns -1 which maps to PermissionDenied.
        ServerCallContext context = CreateAuthenticatedContext(42, headerMachineId: "99");

        FakeAsyncStreamReader<TelemetryEnvelope> requestStream = new([]);
        FakeServerStreamWriter<TelemetryAck> responseStream = new();

        await service.StreamTelemetry(requestStream, responseStream, context);

        await Assert.That(context.Status.StatusCode).IsEqualTo(StatusCode.PermissionDenied);
        await Assert.That(context.Status.Detail).IsEqualTo("Machine ID mismatch between API key and header");
        await Assert.That(responseStream.Written.Count).IsEqualTo(0);
    }

    [Test]
    public async Task StreamTelemetry_InactiveSubscription_SetsPermissionDeniedStatus()
    {
        using TestDatabaseFactory dbFactory = new();
        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);

        ISubscriptionService inactiveSubService = Substitute.For<ISubscriptionService>();
        inactiveSubService.GetSubscriptionForTenantAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new TenantSubscription
            {
                TenantId = 1,
                Tier = Database.Enums.SubscriptionTier.Free,
                Status = Database.Enums.SubscriptionStatus.PastDue,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });

        TelemetryService service = new(scopeFactory, _dedupService, inactiveSubService, _eventAlertService, NoOpPipeline, _logger);
        ServerCallContext context = CreateAuthenticatedContext(100);

        FakeAsyncStreamReader<TelemetryEnvelope> requestStream = new([]);
        FakeServerStreamWriter<TelemetryAck> responseStream = new();

        await service.StreamTelemetry(requestStream, responseStream, context);

        await Assert.That(context.Status.StatusCode).IsEqualTo(StatusCode.PermissionDenied);
        await Assert.That(context.Status.Detail).IsEqualTo("Tenant subscription is not active");
        await Assert.That(responseStream.Written.Count).IsEqualTo(0);
    }

    [Test]
    public async Task StreamTelemetry_EmptyStream_CompletesWithoutError()
    {
        using TestDatabaseFactory dbFactory = new();
        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        TelemetryService service = CreateService(scopeFactory);
        ServerCallContext context = CreateAuthenticatedContext(100);

        FakeAsyncStreamReader<TelemetryEnvelope> requestStream = new([]);
        FakeServerStreamWriter<TelemetryAck> responseStream = new();

        await service.StreamTelemetry(requestStream, responseStream, context);

        // Should complete normally with no acks written and default OK status.
        await Assert.That(responseStream.Written.Count).IsEqualTo(0);
        await Assert.That(context.Status.StatusCode).IsEqualTo(StatusCode.OK);
    }

    [Test]
    public async Task StreamTelemetry_SingleEnvelope_WritesOneAck()
    {
        using TestDatabaseFactory dbFactory = new();
        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        TelemetryService service = CreateService(scopeFactory);
        ServerCallContext context = CreateAuthenticatedContext(100);

        TelemetryEnvelope envelope = new()
        {
            AgentTimestamp = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            BatchId = "stream-batch-1",
        };
        envelope.Items.Add(new TelemetryItem
        {
            EventId = "stream-event-1",
            Type = TelemetryTypes.CpuUtilizationType,
            CpuUtilization = new CpuUtilizationRecord { CpuUsagePercent = 50 }
        });

        FakeAsyncStreamReader<TelemetryEnvelope> requestStream = new([envelope]);
        FakeServerStreamWriter<TelemetryAck> responseStream = new();

        await service.StreamTelemetry(requestStream, responseStream, context);

        await Assert.That(responseStream.Written.Count).IsEqualTo(1);
        await Assert.That(responseStream.Written[0].BatchId).IsEqualTo("stream-batch-1");
        await Assert.That(responseStream.Written[0].Success).IsTrue();
    }

    [Test]
    public async Task StreamTelemetry_TwoEnvelopes_WritesTwoAcks()
    {
        using TestDatabaseFactory dbFactory = new();
        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        TelemetryService service = CreateService(scopeFactory);
        ServerCallContext context = CreateAuthenticatedContext(100);

        TelemetryEnvelope envelope1 = new()
        {
            AgentTimestamp = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            BatchId = "stream-batch-a",
        };
        envelope1.Items.Add(new TelemetryItem
        {
            EventId = "stream-event-a",
            Type = TelemetryTypes.CpuUtilizationType,
            CpuUtilization = new CpuUtilizationRecord { CpuUsagePercent = 30 }
        });

        TelemetryEnvelope envelope2 = new()
        {
            AgentTimestamp = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            BatchId = "stream-batch-b",
        };
        envelope2.Items.Add(new TelemetryItem
        {
            EventId = "stream-event-b",
            Type = TelemetryTypes.MemoryUtilizationType,
            MemoryUtilization = new MemoryUtilizationRecord { MemoryUsagePercent = 70 }
        });

        FakeAsyncStreamReader<TelemetryEnvelope> requestStream = new([envelope1, envelope2]);
        FakeServerStreamWriter<TelemetryAck> responseStream = new();

        await service.StreamTelemetry(requestStream, responseStream, context);

        await Assert.That(responseStream.Written.Count).IsEqualTo(2);
        await Assert.That(responseStream.Written[0].BatchId).IsEqualTo("stream-batch-a");
        await Assert.That(responseStream.Written[0].Success).IsTrue();
        await Assert.That(responseStream.Written[1].BatchId).IsEqualTo("stream-batch-b");
        await Assert.That(responseStream.Written[1].Success).IsTrue();
    }

    [Test]
    public async Task StreamTelemetry_TwoEnvelopes_InsertsTelemetryIntoDatabase()
    {
        using TestDatabaseFactory dbFactory = new();
        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        TelemetryService service = CreateService(scopeFactory);
        ServerCallContext context = CreateAuthenticatedContext(100, tenantId: 3);

        TelemetryEnvelope envelope1 = new()
        {
            AgentTimestamp = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            BatchId = "stream-db-a",
        };
        envelope1.Items.Add(new TelemetryItem
        {
            EventId = "stream-db-event-a",
            Type = TelemetryTypes.CpuUtilizationType,
            CpuUtilization = new CpuUtilizationRecord { CpuUsagePercent = 25 }
        });

        TelemetryEnvelope envelope2 = new()
        {
            AgentTimestamp = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            BatchId = "stream-db-b",
        };
        envelope2.Items.Add(new TelemetryItem
        {
            EventId = "stream-db-event-b",
            Type = TelemetryTypes.MemoryUtilizationType,
            MemoryUtilization = new MemoryUtilizationRecord { MemoryUsagePercent = 60 }
        });

        FakeAsyncStreamReader<TelemetryEnvelope> requestStream = new([envelope1, envelope2]);
        FakeServerStreamWriter<TelemetryAck> responseStream = new();

        await service.StreamTelemetry(requestStream, responseStream, context);

        List<MachineTelemetry> telemetry = await dbFactory.Context.MachineTelemetry.ToListAsync();

        await Assert.That(telemetry.Count).IsEqualTo(2);
        await Assert.That(telemetry.All(t => t.MachineId == 100)).IsTrue();
        await Assert.That(telemetry.All(t => t.TenantId == 3)).IsTrue();
    }

    [Test]
    public async Task StreamTelemetry_ExceedsMaxEnvelopeLimit_StopsProcessingAfterLimit()
    {
        using TestDatabaseFactory dbFactory = new();
        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        TelemetryService service = CreateService(scopeFactory);
        ServerCallContext context = CreateAuthenticatedContext(100);

        // Create 1002 envelopes — the service should process the first 1000 then break.
        List<TelemetryEnvelope> envelopes = new();
        for (int i = 0; i < 1002; i++)
        {
            TelemetryEnvelope envelope = new()
            {
                AgentTimestamp = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                BatchId = $"stream-limit-{i}",
            };
            envelope.Items.Add(new TelemetryItem
            {
                EventId = $"stream-limit-event-{i}",
                Type = TelemetryTypes.CpuUtilizationType,
                CpuUtilization = new CpuUtilizationRecord { CpuUsagePercent = 10 }
            });
            envelopes.Add(envelope);
        }

        FakeAsyncStreamReader<TelemetryEnvelope> requestStream = new(envelopes);
        FakeServerStreamWriter<TelemetryAck> responseStream = new();

        await service.StreamTelemetry(requestStream, responseStream, context);

        // The loop processes envelope 1 through 1000 (envelopeCount 1..1000),
        // then on envelope 1001 (envelopeCount 1001) the > MaxEnvelopesPerStream check triggers break.
        // So exactly 1000 acks should be written.
        await Assert.That(responseStream.Written.Count).IsEqualTo(1000);
    }

    // Note: The stream timeout branch (MaxStreamDuration = 5 minutes) is infeasible to test
    // without actual wall-clock waits. The OperationCanceledException catch for streamTimeout
    // would require waiting 5 minutes or modifying the production code to accept configurable
    // timeouts. Skipping this scenario.

    // ========================================================================
    // ProcessEnvelopeAsync branch tests
    // ========================================================================

    [Test]
    public async Task SubmitTelemetry_MissingAgentTimestamp_ReturnsError()
    {
        using TestDatabaseFactory dbFactory = new();
        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        TelemetryService service = CreateService(scopeFactory);
        ServerCallContext context = CreateAuthenticatedContext(100);

        // AgentTimestamp is intentionally omitted to trigger the null-timestamp rejection path.
        TelemetryEnvelope envelope = new() { BatchId = "batch-no-ts" };
        envelope.Items.Add(new TelemetryItem
        {
            EventId = "event-no-ts",
            Type = TelemetryTypes.CpuUtilizationType,
            CpuUtilization = new CpuUtilizationRecord { CpuUsagePercent = 50 }
        });

        TelemetryAck ack = await service.SubmitTelemetry(envelope, context);

        await Assert.That(ack.Success).IsFalse();
        await Assert.That(ack.ErrorMessage).IsEqualTo("agent_timestamp is required");

        List<MachineTelemetry> telemetry = await dbFactory.Context.MachineTelemetry.ToListAsync();

        await Assert.That(telemetry.Count).IsEqualTo(0);
    }

    [Test]
    public async Task SubmitTelemetry_ClockSkewExceedsLimit_ReturnsError()
    {
        using TestDatabaseFactory dbFactory = new();
        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        TelemetryService service = CreateService(scopeFactory);
        ServerCallContext context = CreateAuthenticatedContext(100);

        // 10 minutes in the future exceeds the 5-minute skew window.
        DateTimeOffset skewedTime = DateTimeOffset.UtcNow.AddMinutes(10);

        TelemetryEnvelope envelope = new()
        {
            AgentTimestamp = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(skewedTime),
            BatchId = "batch-skew"
        };
        envelope.Items.Add(new TelemetryItem
        {
            EventId = "event-skew",
            Type = TelemetryTypes.CpuUtilizationType,
            CpuUtilization = new CpuUtilizationRecord { CpuUsagePercent = 50 }
        });

        TelemetryAck ack = await service.SubmitTelemetry(envelope, context);

        await Assert.That(ack.Success).IsFalse();
        await Assert.That(ack.ErrorMessage).Contains("clock skew");

        List<MachineTelemetry> telemetry = await dbFactory.Context.MachineTelemetry.ToListAsync();

        await Assert.That(telemetry.Count).IsEqualTo(0);
    }

    [Test]
    public async Task SubmitTelemetry_ClockSkewPastLimit_ReturnsError()
    {
        using TestDatabaseFactory dbFactory = new();
        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        TelemetryService service = CreateService(scopeFactory);
        ServerCallContext context = CreateAuthenticatedContext(100);

        // 10 minutes in the past also exceeds the 5-minute skew window.
        DateTimeOffset skewedTime = DateTimeOffset.UtcNow.AddMinutes(-10);

        TelemetryEnvelope envelope = new()
        {
            AgentTimestamp = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(skewedTime),
            BatchId = "batch-skew-past"
        };
        envelope.Items.Add(new TelemetryItem
        {
            EventId = "event-skew-past",
            Type = TelemetryTypes.CpuUtilizationType,
            CpuUtilization = new CpuUtilizationRecord { CpuUsagePercent = 50 }
        });

        TelemetryAck ack = await service.SubmitTelemetry(envelope, context);

        await Assert.That(ack.Success).IsFalse();
        await Assert.That(ack.ErrorMessage).Contains("clock skew");
    }

    [Test]
    public async Task SubmitTelemetry_BrokenCircuitException_ReturnsBackpressureError()
    {
        using TestDatabaseFactory dbFactory = new();

        // Replace the machineStateRepo with one that throws BrokenCircuitException.
        Database.Repositories.IMachineStateRepository throwingRepo = NSubstitute.Substitute.For<Database.Repositories.IMachineStateRepository>();
        throwingRepo.BulkInsertTelemetryAsync(
            NSubstitute.Arg.Any<List<MachineTelemetry>>(),
            NSubstitute.Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new Polly.CircuitBreaker.BrokenCircuitException("Circuit open"));

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context, new Dictionary<Type, object>
        {
            { typeof(Database.Repositories.IMachineStateRepository), throwingRepo }
        });

        // Use a no-op pipeline so the BrokenCircuitException propagates out unhandled by Polly.
        TelemetryService service = new(scopeFactory, _dedupService, _subscriptionService, _eventAlertService, NoOpPipeline, _logger);
        ServerCallContext context = CreateAuthenticatedContext(100);

        TelemetryEnvelope envelope = new()
        {
            AgentTimestamp = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            BatchId = "batch-circuit"
        };
        envelope.Items.Add(new TelemetryItem
        {
            EventId = "event-circuit",
            Type = TelemetryTypes.CpuUtilizationType,
            CpuUtilization = new CpuUtilizationRecord { CpuUsagePercent = 50 }
        });

        TelemetryAck ack = await service.SubmitTelemetry(envelope, context);

        await Assert.That(ack.Success).IsFalse();
        await Assert.That(ack.ErrorMessage).Contains("temporarily unavailable");
    }

    [Test]
    public async Task SubmitTelemetry_TimeoutRejectedException_ReturnsTimeoutError()
    {
        using TestDatabaseFactory dbFactory = new();

        Database.Repositories.IMachineStateRepository throwingRepo = NSubstitute.Substitute.For<Database.Repositories.IMachineStateRepository>();
        throwingRepo.BulkInsertTelemetryAsync(
            NSubstitute.Arg.Any<List<MachineTelemetry>>(),
            NSubstitute.Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new Polly.Timeout.TimeoutRejectedException("Timed out"));

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context, new Dictionary<Type, object>
        {
            { typeof(Database.Repositories.IMachineStateRepository), throwingRepo }
        });

        TelemetryService service = new(scopeFactory, _dedupService, _subscriptionService, _eventAlertService, NoOpPipeline, _logger);
        ServerCallContext context = CreateAuthenticatedContext(100);

        TelemetryEnvelope envelope = new()
        {
            AgentTimestamp = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            BatchId = "batch-timeout"
        };
        envelope.Items.Add(new TelemetryItem
        {
            EventId = "event-timeout",
            Type = TelemetryTypes.CpuUtilizationType,
            CpuUtilization = new CpuUtilizationRecord { CpuUsagePercent = 50 }
        });

        TelemetryAck ack = await service.SubmitTelemetry(envelope, context);

        await Assert.That(ack.Success).IsFalse();
        await Assert.That(ack.ErrorMessage).Contains("timed out");
    }

    [Test]
    public async Task SubmitTelemetry_UnhandledException_ReturnsInternalServerError()
    {
        using TestDatabaseFactory dbFactory = new();

        Database.Repositories.IMachineStateRepository throwingRepo = NSubstitute.Substitute.For<Database.Repositories.IMachineStateRepository>();
        throwingRepo.BulkInsertTelemetryAsync(
            NSubstitute.Arg.Any<List<MachineTelemetry>>(),
            NSubstitute.Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("Unexpected failure"));

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context, new Dictionary<Type, object>
        {
            { typeof(Database.Repositories.IMachineStateRepository), throwingRepo }
        });

        TelemetryService service = new(scopeFactory, _dedupService, _subscriptionService, _eventAlertService, NoOpPipeline, _logger);
        ServerCallContext context = CreateAuthenticatedContext(100);

        TelemetryEnvelope envelope = new()
        {
            AgentTimestamp = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            BatchId = "batch-unhandled"
        };
        envelope.Items.Add(new TelemetryItem
        {
            EventId = "event-unhandled",
            Type = TelemetryTypes.CpuUtilizationType,
            CpuUtilization = new CpuUtilizationRecord { CpuUsagePercent = 50 }
        });

        TelemetryAck ack = await service.SubmitTelemetry(envelope, context);

        await Assert.That(ack.Success).IsFalse();
        await Assert.That(ack.ErrorMessage).IsEqualTo("Internal server error");
    }

    // ========================================================================
    // IsSubscriptionActiveAsync: null TenantId claim
    // ========================================================================

    [Test]
    public async Task SubmitTelemetry_NoTenantIdClaim_ReturnsSubscriptionInactiveError()
    {
        using TestDatabaseFactory dbFactory = new();
        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);

        TelemetryService service = new(scopeFactory, _dedupService, _subscriptionService, _eventAlertService, NoOpPipeline, _logger);

        // Context with a valid MachineId claim but no TenantId claim — IsSubscriptionActiveAsync returns false.
        DefaultHttpContext httpContext = new();
        ClaimsIdentity identity = new("ApiKey");
        identity.AddClaim(new Claim("MachineId", "100"));
        httpContext.User = new ClaimsPrincipal(identity);
        ServerCallContext context = new TestServerCallContextWithHttp(httpContext, new Metadata());

        TelemetryEnvelope envelope = new()
        {
            AgentTimestamp = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            BatchId = "batch-no-tenant"
        };
        envelope.Items.Add(new TelemetryItem
        {
            EventId = "event-no-tenant",
            Type = TelemetryTypes.CpuUtilizationType,
            CpuUtilization = new CpuUtilizationRecord { CpuUsagePercent = 50 }
        });

        TelemetryAck ack = await service.SubmitTelemetry(envelope, context);

        await Assert.That(ack.Success).IsFalse();
        await Assert.That(ack.ErrorMessage).IsEqualTo("Tenant subscription is not active");

        List<MachineTelemetry> telemetry = await dbFactory.Context.MachineTelemetry.ToListAsync();

        await Assert.That(telemetry.Count).IsEqualTo(0);
    }

    // ========================================================================
    // Test helpers for streaming
    // ========================================================================

    /// <summary>
    /// Fake <see cref="IAsyncStreamReader{T}"/> backed by a list of items.
    /// </summary>
    private sealed class FakeAsyncStreamReader<T> : IAsyncStreamReader<T>
    {
        private readonly IReadOnlyList<T> _items;
        private int _index = -1;

        /// <summary>
        /// Creates a fake stream reader that yields the given items in order.
        /// </summary>
        public FakeAsyncStreamReader(IReadOnlyList<T> items)
        {
            _items = items;
        }

        /// <inheritdoc/>
        public T Current => _items[_index];

        /// <inheritdoc/>
        public Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _index++;

            return Task.FromResult(_index < _items.Count);
        }
    }

    /// <summary>
    /// Fake <see cref="IServerStreamWriter{T}"/> that captures all written messages.
    /// </summary>
    private sealed class FakeServerStreamWriter<T> : IServerStreamWriter<T>
    {
        private readonly List<T> _written = new();

        /// <summary>
        /// Gets the list of messages written to this stream.
        /// </summary>
        public IReadOnlyList<T> Written => _written;

        /// <inheritdoc/>
        public WriteOptions? WriteOptions { get; set; }

        /// <inheritdoc/>
        public Task WriteAsync(T message)
        {
            _written.Add(message);

            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        public Task WriteAsync(T message, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _written.Add(message);

            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Custom <see cref="ServerCallContext"/> subclass that provides an <see cref="HttpContext"/>
    /// for testing gRPC endpoints that use <c>context.GetHttpContext()</c>.
    /// </summary>
    private sealed class TestServerCallContextWithHttp : ServerCallContext
    {
        private readonly Metadata _requestHeaders;

        /// <summary>
        /// Creates a new test context with the given HTTP context and request headers.
        /// </summary>
        public TestServerCallContextWithHttp(HttpContext httpContext, Metadata requestHeaders)
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
