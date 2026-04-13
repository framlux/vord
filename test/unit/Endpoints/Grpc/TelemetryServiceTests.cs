// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Grpc.AgentTelemetry;
using Framlux.FleetManagement.Server.Endpoints.Grpc;
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
using System.Security.Claims;

namespace Framlux.FleetManagement.Test.Endpoints.Grpc;

/// <summary>
/// Unit tests for <see cref="TelemetryService"/>.
/// </summary>
public sealed class TelemetryServiceTests
{
    private readonly ITelemetryDeduplicationService _dedupService;
    private readonly ISubscriptionService _subscriptionService;
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
                RetentionDays = 7,
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

    private TelemetryService CreateService(IServiceScopeFactory scopeFactory)
    {
        return new TelemetryService(scopeFactory, _dedupService, _subscriptionService, _logger);
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

        await Assert.That(ack.Success).IsEqualTo(false);
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

        await Assert.That(ack.Success).IsEqualTo(false);
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

        await Assert.That(ack.Success).IsEqualTo(true);
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
        await Assert.That(ack.Success).IsEqualTo(true);
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

        TelemetryService service = new(scopeFactory, dupDedupService, _subscriptionService, _logger);
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

        await Assert.That(ack.Success).IsEqualTo(true);
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

        await Assert.That(ack.Success).IsEqualTo(true);
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

        await Assert.That(ack.Success).IsEqualTo(true);
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
                RetentionDays = 7,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });

        TelemetryService service = new(scopeFactory, _dedupService, inactiveSubService, _logger);
        ServerCallContext context = CreateAuthenticatedContext(100);

        TelemetryEnvelope envelope = new() { AgentTimestamp = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow), BatchId ="batch-inactive" };
        envelope.Items.Add(new TelemetryItem
        {
            EventId = "event-inactive",
            Type = TelemetryTypes.CpuUtilizationType,
            CpuUtilization = new CpuUtilizationRecord { CpuUsagePercent = 50 }
        });

        TelemetryAck ack = await service.SubmitTelemetry(envelope, context);

        await Assert.That(ack.Success).IsEqualTo(false);
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

        await Assert.That(ack.Success).IsEqualTo(false);
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

        await Assert.That(ack.Success).IsEqualTo(true);
        await Assert.That(ack.BatchId).IsEqualTo("batch-empty");
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
