// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Reflection;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Server.Endpoints.Grpc;
using Framlux.FleetManagement.Services.Core.Billing;
using Framlux.FleetManagement.Services.Core.Handlers;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Framlux.FleetManagement.Services.Core.Options;
using Framlux.FleetManagement.Services.Core.Security;
using Framlux.Vord.BillingGrpc;
using Grpc.Core;
using Grpc.Core.Testing;
using Hangfire;
using Hangfire.Common;
using Framlux.FleetManagement.Services.Core.Hangfire;
using Hangfire.InMemory;
using NSubstitute.ExceptionExtensions;
using Hangfire.Storage;
using Hangfire.Storage.Monitoring;
using Hangfire.States;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using StackExchange.Redis;

namespace Framlux.FleetManagement.UnitTest.Endpoints.Grpc;

/// <summary>
/// Unit tests for FleetAdminService mapping helpers, pagination logic, and RPC methods.
/// </summary>
public sealed class FleetAdminServiceTests
{
    [Test]
    public async Task SanitizePagination_ValidValues_ReturnsUnchanged()
    {
        (int page, int pageSize) = FleetAdminService.SanitizePagination(3, 25);

        await Assert.That(page).IsEqualTo(3);
        await Assert.That(pageSize).IsEqualTo(25);
    }

    [Test]
    public async Task SanitizePagination_ZeroPage_DefaultsToOne()
    {
        (int page, int pageSize) = FleetAdminService.SanitizePagination(0, 25);

        await Assert.That(page).IsEqualTo(1);
        await Assert.That(pageSize).IsEqualTo(25);
    }

    [Test]
    public async Task SanitizePagination_NegativePage_DefaultsToOne()
    {
        (int page, int pageSize) = FleetAdminService.SanitizePagination(-5, 25);

        await Assert.That(page).IsEqualTo(1);
        await Assert.That(pageSize).IsEqualTo(25);
    }

    [Test]
    public async Task SanitizePagination_ZeroPageSize_DefaultsToFifty()
    {
        (int page, int pageSize) = FleetAdminService.SanitizePagination(1, 0);

        await Assert.That(page).IsEqualTo(1);
        await Assert.That(pageSize).IsEqualTo(50);
    }

    [Test]
    public async Task SanitizePagination_NegativePageSize_DefaultsToFifty()
    {
        (int page, int pageSize) = FleetAdminService.SanitizePagination(1, -10);

        await Assert.That(page).IsEqualTo(1);
        await Assert.That(pageSize).IsEqualTo(50);
    }

    [Test]
    public async Task SanitizePagination_OverMaxPageSize_CapsToOneHundred()
    {
        (int page, int pageSize) = FleetAdminService.SanitizePagination(1, 500);

        await Assert.That(page).IsEqualTo(1);
        await Assert.That(pageSize).IsEqualTo(100);
    }

    [Test]
    public async Task MapToFleetUser_MapsAllFieldsCorrectly()
    {
        DateTimeOffset createdAt = DateTimeOffset.UtcNow;
        UserAccount user = new UserAccount
        {
            Id = 42,
            ExternalId = "ext-abc",
            Username = "testuser",
            IsActive = true,
            IsGlobalAdmin = true,
            AuthProvider = AuthProviderType.GitHub,
            CreatedAt = createdAt,
            CreatedByUserId = 1,
            IsSystem = false
        };

        List<UserTenantRole> roles = new List<UserTenantRole>();
        Dictionary<int, string> tenantNames = new Dictionary<int, string>();

        FleetUser result = FleetAdminService.MapToFleetUser(user, roles, tenantNames);

        await Assert.That(result.Id).IsEqualTo(42);
        await Assert.That(result.ExternalId).IsEqualTo("ext-abc");
        await Assert.That(result.Username).IsEqualTo("testuser");
        await Assert.That(result.IsActive).IsTrue();
        await Assert.That(result.IsGlobalAdmin).IsTrue();
        await Assert.That(result.AuthProvider).IsEqualTo("GitHub");
        await Assert.That(result.TenantRoles.Count).IsEqualTo(0);
    }

    [Test]
    public async Task MapToFleetUser_WithTenantRoles_MapsRolesCorrectly()
    {
        UserAccount user = new UserAccount
        {
            Id = 1,
            ExternalId = "ext-1",
            Username = "admin",
            IsActive = true,
            IsGlobalAdmin = false,
            AuthProvider = AuthProviderType.Google,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 1,
            IsSystem = false
        };

        List<UserTenantRole> roles = new List<UserTenantRole>
        {
            new UserTenantRole
            {
                UserId = 1,
                AssignedTenantId = 10,
                Role = UserAccountRoles.TenantAdmin,
                AssignedByUserId = 1,
                AssignedAt = DateTimeOffset.UtcNow,
                IsActive = true
            },
            new UserTenantRole
            {
                UserId = 1,
                AssignedTenantId = 20,
                Role = UserAccountRoles.Viewer,
                AssignedByUserId = 1,
                AssignedAt = DateTimeOffset.UtcNow,
                IsActive = true
            }
        };

        Dictionary<int, string> tenantNames = new Dictionary<int, string>
        {
            { 10, "Tenant A" },
            { 20, "Tenant B" }
        };

        FleetUser result = FleetAdminService.MapToFleetUser(user, roles, tenantNames);

        await Assert.That(result.TenantRoles.Count).IsEqualTo(2);
        await Assert.That(result.TenantRoles[0].TenantId).IsEqualTo(10);
        await Assert.That(result.TenantRoles[0].TenantName).IsEqualTo("Tenant A");
        await Assert.That(result.TenantRoles[0].Role).IsEqualTo("TenantAdmin");
        await Assert.That(result.TenantRoles[1].TenantId).IsEqualTo(20);
        await Assert.That(result.TenantRoles[1].TenantName).IsEqualTo("Tenant B");
        await Assert.That(result.TenantRoles[1].Role).IsEqualTo("Viewer");
    }

    [Test]
    public async Task MapToFleetUser_MissingTenantName_ReturnsEmptyString()
    {
        UserAccount user = new UserAccount
        {
            Id = 1,
            ExternalId = "ext-1",
            Username = "user1",
            IsActive = true,
            IsGlobalAdmin = false,
            AuthProvider = AuthProviderType.Microsoft,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 1,
            IsSystem = false
        };

        List<UserTenantRole> roles = new List<UserTenantRole>
        {
            new UserTenantRole
            {
                UserId = 1,
                AssignedTenantId = 99,
                Role = UserAccountRoles.MachineAdmin,
                AssignedByUserId = 1,
                AssignedAt = DateTimeOffset.UtcNow,
                IsActive = true
            }
        };

        Dictionary<int, string> tenantNames = new Dictionary<int, string>();

        FleetUser result = FleetAdminService.MapToFleetUser(user, roles, tenantNames);

        await Assert.That(result.TenantRoles[0].TenantName).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task MapToFleetTenant_WithSubscription_MapsAllFields()
    {
        Tenant tenant = new Tenant
        {
            Id = 5,
            ExternalId = "ext-t5",
            Name = "acme",
            IsActive = true,
            LogoUrl = "https://example.com/logo.png",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 1
        };

        TenantSubscription subscription = new TenantSubscription
        {
            TenantId = 5,
            Tier = SubscriptionTier.Pro,
            Status = SubscriptionStatus.Active,

            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        TierFeatureLimit tierLimits = new TierFeatureLimit
        {
            Tier = SubscriptionTier.Pro,
            MachineLimit = 50,
            RetentionDays = 60,
            AlertRuleLimit = 10,
            WebhookLimit = 5,
            MemberLimit = 5,
            MinimumBillableMachines = 1,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        FleetTenant result = FleetAdminService.MapToFleetTenant(tenant, 10, 3, subscription, tierLimits);

        await Assert.That(result.Id).IsEqualTo(5);
        await Assert.That(result.ExternalId).IsEqualTo("ext-t5");
        await Assert.That(result.Name).IsEqualTo("acme");
        await Assert.That(result.IsActive).IsTrue();
        await Assert.That(result.LogoUrl).IsEqualTo("https://example.com/logo.png");
        await Assert.That(result.MachineCount).IsEqualTo(10);
        await Assert.That(result.UserCount).IsEqualTo(3);
        await Assert.That(result.Subscription).IsNotNull();
        await Assert.That(result.Subscription.Tier).IsEqualTo(BillingTier.Pro);
        await Assert.That(result.Subscription.Status).IsEqualTo("Active");
        await Assert.That(result.Subscription.MachineLimit).IsEqualTo(50);
        await Assert.That(result.Subscription.RetentionDays).IsEqualTo(60);
    }

    [Test]
    public async Task MapToFleetTenant_NullSubscription_LeavesSubscriptionNull()
    {
        Tenant tenant = new Tenant
        {
            Id = 1,
            ExternalId = "ext-t1",
            Name = "solo",
            IsActive = true,
            LogoUrl = "",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 1
        };

        FleetTenant result = FleetAdminService.MapToFleetTenant(tenant, 0, 0, null);

        await Assert.That(result.Subscription).IsNull();
    }

    [Test]
    public async Task MapSubscription_NullMachineLimit_SetsZero()
    {
        TenantSubscription subscription = new TenantSubscription
        {
            TenantId = 1,
            Tier = SubscriptionTier.Team,
            Status = SubscriptionStatus.Active,

            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        FleetTenantSubscription result = FleetAdminService.MapSubscription(subscription);

        await Assert.That(result.MachineLimit).IsEqualTo(0);
    }

    [Test]
    public async Task MapSubscription_WithCurrentPeriodEnd_SetsTimestamp()
    {
        DateTimeOffset periodEnd = DateTimeOffset.UtcNow.AddDays(30);
        TenantSubscription subscription = new TenantSubscription
        {
            TenantId = 1,
            Tier = SubscriptionTier.Pro,
            Status = SubscriptionStatus.Active,
            CurrentPeriodEnd = periodEnd,

            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        FleetTenantSubscription result = FleetAdminService.MapSubscription(subscription);

        await Assert.That(result.CurrentPeriodEnd).IsNotNull();

    }

    [Test]
    public async Task MapSubscription_NullCurrentPeriodEnd_OmitsTimestamp()
    {
        TenantSubscription subscription = new TenantSubscription
        {
            TenantId = 1,
            Tier = SubscriptionTier.Free,
            Status = SubscriptionStatus.Active,
            CurrentPeriodEnd = null,

            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        FleetTenantSubscription result = FleetAdminService.MapSubscription(subscription);

        await Assert.That(result.CurrentPeriodEnd).IsNull();
    }

    [Test]
    public async Task MapToFleetMachine_MapsAllFields()
    {
        Machine machine = new Machine
        {
            Id = 100,
            Name = "server-01",
            TenantId = 5,
            IsDeleted = false,
            RegisteredOn = DateTimeOffset.UtcNow,
            ApiKeyHash = "hash",
            SerialNumber = "SN001",
            SystemId = "SYS001",
            MachineType = MachineTypes.BareMetalServer,
            OperatingSystem = OperatingSystems.Ubuntu,
            RegistrationTokenId = 1
        };

        FleetMachine result = FleetAdminService.MapToFleetMachine(machine, "My Tenant");

        await Assert.That(result.Id).IsEqualTo(100);
        await Assert.That(result.Name).IsEqualTo("server-01");
        await Assert.That(result.TenantId).IsEqualTo(5);
        await Assert.That(result.TenantName).IsEqualTo("My Tenant");
        await Assert.That(result.IsDeleted).IsFalse();
    }

    [Test]
    public async Task MapToFleetAuditEntry_MapsEnumsToStrings()
    {
        AuditLogEntry entry = new AuditLogEntry
        {
            Id = 1,
            TenantId = 5,
            UserId = 10,
            Action = AuditAction.MachineRegistered,
            ResourceType = AuditResourceType.Machine,
            Details = "Machine registered",
            Timestamp = DateTimeOffset.UtcNow
        };

        FleetAuditEntry result = FleetAdminService.MapToFleetAuditEntry(entry, "testuser", "acme");

        await Assert.That(result.Id).IsEqualTo(1);
        await Assert.That(result.TenantId).IsEqualTo(5);
        await Assert.That(result.UserId).IsEqualTo(10);
        await Assert.That(result.Action).IsEqualTo("MachineRegistered");
        await Assert.That(result.ResourceType).IsEqualTo("Machine");
        await Assert.That(result.Details).IsEqualTo("Machine registered");
        await Assert.That(result.TenantName).IsEqualTo("acme");
        await Assert.That(result.Username).IsEqualTo("testuser");
    }

    [Test]
    public async Task MapToFleetAuditEntry_NullTenantAndUser_HandlesGracefully()
    {
        AuditLogEntry entry = new AuditLogEntry
        {
            Id = 2,
            TenantId = null,
            UserId = null,
            Action = AuditAction.UserLogin,
            ResourceType = AuditResourceType.User,
            Details = null,
            Timestamp = DateTimeOffset.UtcNow
        };

        FleetAuditEntry result = FleetAdminService.MapToFleetAuditEntry(entry, null, null);

        await Assert.That(result.TenantId).IsEqualTo(0);
        await Assert.That(result.UserId).IsEqualTo(0);
        await Assert.That(result.Details).IsEqualTo(string.Empty);
        await Assert.That(result.TenantName).IsEqualTo(string.Empty);
        await Assert.That(result.Username).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task MapToFleetUser_AllAuthProviders_MapToCorrectStrings()
    {
        Dictionary<int, string> emptyNames = new Dictionary<int, string>();
        List<UserTenantRole> emptyRoles = new List<UserTenantRole>();

        AuthProviderType[] providers = new[]
        {
            AuthProviderType.Unknown,
            AuthProviderType.GitHub,
            AuthProviderType.Google,
            AuthProviderType.Microsoft,
            AuthProviderType.CustomOidc
        };

        foreach (AuthProviderType provider in providers)
        {
            UserAccount user = new UserAccount
            {
                Id = 1,
                ExternalId = "ext",
                Username = "u",
                IsActive = true,
                IsGlobalAdmin = false,
                AuthProvider = provider,
                CreatedAt = DateTimeOffset.UtcNow,
                CreatedByUserId = 1,
                IsSystem = false
            };

            FleetUser result = FleetAdminService.MapToFleetUser(user, emptyRoles, emptyNames);

            await Assert.That(result.AuthProvider).IsEqualTo(provider.ToString());
        }
    }

    // ────────────────────────────────────────────────────────────────
    // RPC method tests — uses NSubstitute mocks for all dependencies
    // ────────────────────────────────────────────────────────────────

    private const string TenantExternalId = "ext-tenant-001";
    private const int TenantInternalId = 10;

    /// <summary>
    /// Builds the service under test. Caller authorisation is a collaborator here — its own
    /// rules are proven in <c>CertificateSubjectAuthorizerTests</c> and end to end in the
    /// functional gRPC suite. What matters at this level is that every RPC consults it before
    /// touching a repository and propagates its rejection unchanged.
    /// </summary>
    private static FleetAdminService CreateFleetAdminService(
        IServiceScopeFactory scopeFactory,
        StatusCode? rejectWith = null,
        IOidcSecretProtector? oidcSecretProtector = null,
        IConnectionMultiplexer? redis = null,
        IBackgroundJobClientV2? backgroundJobs = null,
        JobStorage? storage = null,
        HangfireOptions? hangfireOptions = null,
        TimeProvider? timeProvider = null)
    {
        ILogger<FleetAdminService> logger = Substitute.For<ILogger<FleetAdminService>>();
        IOidcSecretProtector resolvedProtector = oidcSecretProtector
            ?? new OidcSecretProtector(new EphemeralDataProtectionProvider());
        IConnectionMultiplexer resolvedRedis = redis ?? Substitute.For<IConnectionMultiplexer>();

        return new FleetAdminService(
            scopeFactory,
            CreateAuthorizer(rejectWith),
            resolvedProtector,
            logger,
            resolvedRedis,
            storage ?? new InMemoryStorage(),
            timeProvider ?? TimeProvider.System,
            backgroundJobs ?? Substitute.For<IBackgroundJobClientV2>(),
            Options.Create(hangfireOptions ?? new HangfireOptions()));
    }

    // ── On-demand run: failure paths ──

    /// <summary>
    /// Hangfire's client returns the new job id or null, and ships without nullable annotations so
    /// nothing warns. Reporting success on null would tell the caller a run had started and leave
    /// it no id to record the run against.
    /// </summary>
    [Test]
    public async Task RunRecurringJobNow_ClientReturnsNoJobId_ThrowsUnavailable()
    {
        IBackgroundJobClientV2 backgroundJobs = Substitute.For<IBackgroundJobClientV2>();
        backgroundJobs.Create(Arg.Any<Job>(), Arg.Any<IState>(), Arg.Any<IDictionary<string, object>>())
            .Returns((string?)null);

        RpcException? exception = await RunAgainstSeededStorageAsync(backgroundJobs);

        await Assert.That(exception!.StatusCode).IsEqualTo(StatusCode.Unavailable);
    }

    /// <summary>
    /// Hangfire wraps every storage failure during creation in BackgroundJobClientException. Left
    /// unhandled it surfaces as Unknown, which the admin API turns into a 500 carrying .NET
    /// internals rather than a clean unavailable.
    /// </summary>
    [Test]
    public async Task RunRecurringJobNow_ClientThrows_ThrowsUnavailable()
    {
        IBackgroundJobClientV2 backgroundJobs = Substitute.For<IBackgroundJobClientV2>();
        backgroundJobs.Create(Arg.Any<Job>(), Arg.Any<IState>(), Arg.Any<IDictionary<string, object>>())
            .Throws(new BackgroundJobClientException("storage down", new InvalidOperationException()));

        RpcException? exception = await RunAgainstSeededStorageAsync(backgroundJobs);

        await Assert.That(exception!.StatusCode).IsEqualTo(StatusCode.Unavailable);
    }

    /// <summary>
    /// A registration whose payload will not deserialise cannot be run, and the operator must be
    /// told that rather than that the job is unregistered — the two send them looking in different
    /// places.
    /// </summary>
    [Test]
    public async Task RunRecurringJobNow_PayloadWillNotLoad_ThrowsFailedPreconditionSayingSo()
    {
        using InMemoryStorage storage = new();
        SeedRecurringJob(storage, RecurringJobIds.TenantPurge, unloadable: true);

        FleetAdminService service = CreateFleetAdminService(
            Substitute.For<IServiceScopeFactory>(), storage: storage, redis: CreateRedisAllowing());

        RunRecurringJobNowRequest request = new()
        {
            JobId = RecurringJobIds.TenantPurge,
            TriggeredBy = "ops@framlux.io",
        };

        RpcException? exception = await Assert.ThrowsAsync<RpcException>(
            async () => await service.RunRecurringJobNow(request, CreateContext()));

        await Assert.That(exception!.StatusCode).IsEqualTo(StatusCode.FailedPrecondition);
        await Assert.That(exception.Status.Detail).Contains("payload");
    }

    /// <summary>
    /// The operator identity is caller-supplied and unbounded. Left uncapped it lands whole in a
    /// Hangfire job-parameter row and in a log event, once per call.
    /// </summary>
    [Test]
    public async Task RunRecurringJobNow_OverlongTriggeredBy_IsTruncatedBeforeStamping()
    {
        using InMemoryStorage storage = new();
        SeedRecurringJob(storage, RecurringJobIds.TenantPurge, unloadable: false);

        IBackgroundJobClientV2 backgroundJobs = Substitute.For<IBackgroundJobClientV2>();
        backgroundJobs.Create(Arg.Any<Job>(), Arg.Any<IState>(), Arg.Any<IDictionary<string, object>>())
            .Returns("job-1");

        FleetAdminService service = CreateFleetAdminService(
            Substitute.For<IServiceScopeFactory>(),
            backgroundJobs: backgroundJobs,
            storage: storage,
            redis: CreateRedisAllowing());

        RunRecurringJobNowRequest request = new()
        {
            JobId = RecurringJobIds.TenantPurge,
            TriggeredBy = new string('x', 5000),
        };

        await service.RunRecurringJobNow(request, CreateContext());

        IDictionary<string, object> captured = (IDictionary<string, object>)backgroundJobs
            .ReceivedCalls()
            .Single(c => c.GetMethodInfo().Name == nameof(IBackgroundJobClientV2.Create))
            .GetArguments()[2]!;

        await Assert.That(((string)captured["TriggeredBy"]).Length).IsEqualTo(256);
    }

    /// <summary>
    /// The cooldown bounds an accident. If Redis is unreachable, denying the operator their tool
    /// during a Redis outage would be the worse failure, so the run proceeds.
    /// </summary>
    [Test]
    public async Task RunRecurringJobNow_RedisUnreachable_StillRuns()
    {
        using InMemoryStorage storage = new();
        SeedRecurringJob(storage, RecurringJobIds.TenantPurge, unloadable: false);

        IBackgroundJobClientV2 backgroundJobs = Substitute.For<IBackgroundJobClientV2>();
        backgroundJobs.Create(Arg.Any<Job>(), Arg.Any<IState>(), Arg.Any<IDictionary<string, object>>())
            .Returns("job-1");

        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        IDatabase database = Substitute.For<IDatabase>();
        database.StringSetAsync(
                Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<When>())
            .Throws(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "down"));
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);

        FleetAdminService service = CreateFleetAdminService(
            Substitute.For<IServiceScopeFactory>(),
            backgroundJobs: backgroundJobs,
            storage: storage,
            redis: redis);

        RunRecurringJobNowRequest request = new()
        {
            JobId = RecurringJobIds.TenantPurge,
            TriggeredBy = "ops@framlux.io",
        };

        RunRecurringJobNowResponse response = await service.RunRecurringJobNow(request, CreateContext());

        await Assert.That(response.Success).IsTrue();
    }

    /// <summary>
    /// A zero or negative cooldown disables the check outright rather than blocking every run.
    /// </summary>
    [Test]
    public async Task RunRecurringJobNow_CooldownDisabled_DoesNotConsultRedis()
    {
        using InMemoryStorage storage = new();
        SeedRecurringJob(storage, RecurringJobIds.TenantPurge, unloadable: false);

        IBackgroundJobClientV2 backgroundJobs = Substitute.For<IBackgroundJobClientV2>();
        backgroundJobs.Create(Arg.Any<Job>(), Arg.Any<IState>(), Arg.Any<IDictionary<string, object>>())
            .Returns("job-1");

        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();

        FleetAdminService service = CreateFleetAdminService(
            Substitute.For<IServiceScopeFactory>(),
            backgroundJobs: backgroundJobs,
            storage: storage,
            redis: redis,
            hangfireOptions: new HangfireOptions { ManualRunCooldownSeconds = 0 });

        RunRecurringJobNowRequest request = new()
        {
            JobId = RecurringJobIds.TenantPurge,
            TriggeredBy = "ops@framlux.io",
        };

        RunRecurringJobNowResponse response = await service.RunRecurringJobNow(request, CreateContext());

        await Assert.That(response.Success).IsTrue();
        redis.DidNotReceiveWithAnyArgs().GetDatabase();
    }

    // ── Recurring-job read: degraded storage ──

    /// <summary>
    /// The jobs view is what an operator opens when the platform is already misbehaving. A storage
    /// fault in one section must not cost them the others, and the section that failed has to
    /// name itself so the panel can say "unknown" rather than render a confident zero.
    /// </summary>
    [Test]
    public async Task ListRecurringJobs_StatisticsUnavailable_NamesTheSectionAndKeepsTheRest()
    {
        IMonitoringApi monitoring = CreateHealthyMonitoring();
        monitoring.GetStatistics().Throws(new InvalidOperationException("statistics unavailable"));

        ListRecurringJobsResponse response = await ListAgainstAsync(monitoring);

        await Assert.That(response.UnavailableSections).Contains("failed_count");
        await Assert.That(response.FailedJobCount).IsEqualTo(0);
        await Assert.That(response.Jobs.Count).IsEqualTo(RecurringJobIds.All.Count);
    }

    /// <summary>
    /// Worker heartbeat is the headline signal, so its failure must be reported as unknown rather
    /// than as an empty list, which reads identically to "no workers are running".
    /// </summary>
    [Test]
    public async Task ListRecurringJobs_WorkersUnavailable_NamesTheSection()
    {
        IMonitoringApi monitoring = CreateHealthyMonitoring();
        monitoring.Servers().Throws(new InvalidOperationException("servers unavailable"));

        ListRecurringJobsResponse response = await ListAgainstAsync(monitoring);

        await Assert.That(response.UnavailableSections).Contains("workers");
        await Assert.That(response.Workers.Count).IsEqualTo(0);
    }

    /// <summary>
    /// The enrichment list is caller-controlled, so the cap is the only thing bounding the number
    /// of storage lookups one request can provoke.
    /// </summary>
    [Test]
    public async Task ListRecurringJobs_MoreEnrichIdsThanTheCap_ResolvesOnlyTheCap()
    {
        IMonitoringApi monitoring = CreateHealthyMonitoring();

        ListRecurringJobsRequest request = new();

        for (int i = 0; i < 25; i++)
        {
            request.EnrichJobIds.Add($"job-{i}");
        }

        ListRecurringJobsResponse response = await ListAgainstAsync(monitoring, request);

        await Assert.That(response.ManualRuns.Count).IsEqualTo(20);
    }

    /// <summary>
    /// A single unreadable job id must degrade to "outcome not retained" rather than failing the
    /// whole page. On PostgreSQL this path is reachable through a non-numeric id and through
    /// duplicate job-parameter rows; the in-memory storage used elsewhere has neither.
    /// </summary>
    [Test]
    public async Task ListRecurringJobs_EnrichLookupThrows_ReportsTheRunAsNotRetained()
    {
        IMonitoringApi monitoring = CreateHealthyMonitoring();
        monitoring.JobDetails(Arg.Any<string>()).Throws(new FormatException("not a number"));

        ListRecurringJobsRequest request = new();
        request.EnrichJobIds.Add("not-a-numeric-id");

        ListRecurringJobsResponse response = await ListAgainstAsync(monitoring, request);

        await Assert.That(response.ManualRuns.Single().Retained).IsFalse();
    }

    // ── Recurring-job read: work in flight ──

    /// <summary>
    /// The in-flight list answers the first question of an incident: is the queue wedged or is
    /// something genuinely running long? That needs the identity of the work, the worker running
    /// it and when it started — a start time, not a duration, so ages track server_time.
    /// </summary>
    [Test]
    public async Task ListRecurringJobs_ProcessingJobs_ReportsWhatIsRunningAndWhere()
    {
        DateTime startedAt = new(2026, 8, 28, 9, 30, 0, DateTimeKind.Utc);

        IMonitoringApi monitoring = CreateHealthyMonitoring();
        monitoring.ProcessingCount().Returns(3);
        monitoring.ProcessingJobs(Arg.Any<int>(), Arg.Any<int>())
            .Returns(ProcessingList(
                ("101", CreateProcessing("worker-a", startedAt, typeof(QueuedProbeJob)))));

        ListRecurringJobsResponse response = await ListAgainstAsync(monitoring);

        FleetProcessingJob running = response.ProcessingJobs.Single();

        await Assert.That(running.JobId).IsEqualTo("101");
        await Assert.That(running.JobName).IsEqualTo("QueuedProbeJob.RunAsync");

        // The queue has to come from the attribute. Job.Queue is null on every job the fleet
        // creates, so reading it would label a saturated "critical" job as "default" — the exact
        // wrong answer for the one question this view exists to answer.
        await Assert.That(running.Queue).IsEqualTo("critical");
        await Assert.That(running.ServerId).IsEqualTo("worker-a");
        await Assert.That(running.StartedAt.ToDateTime()).IsEqualTo(startedAt);
        await Assert.That(running.InProcessingState).IsTrue();

        // The list is capped, so the total has to come from storage or a busy fleet under-reports.
        await Assert.That(response.ProcessingJobCount).IsEqualTo(3);
        await Assert.That(response.UnavailableSections).DoesNotContain("processing");
    }

    /// <summary>
    /// A job with no queue attribute genuinely runs on the default queue, and the contract spells
    /// empty as exactly that. This is the case the attribute lookup must not over-reach on.
    /// </summary>
    [Test]
    public async Task ListRecurringJobs_ProcessingJobWithNoQueueAttribute_ReportsTheDefaultQueue()
    {
        IMonitoringApi monitoring = CreateHealthyMonitoring();
        monitoring.ProcessingCount().Returns(1);
        monitoring.ProcessingJobs(Arg.Any<int>(), Arg.Any<int>())
            .Returns(ProcessingList(("101", CreateProcessing("worker-a"))));

        ListRecurringJobsResponse response = await ListAgainstAsync(monitoring);

        await Assert.That(response.ProcessingJobs.Single().Queue).IsEqualTo("");
    }

    /// <summary>
    /// Empty is the normal healthy state for in-flight work, which makes this the one section
    /// where a swallowed storage fault reads as "the fleet is idle". The section must name itself
    /// and the list must be emptied, so the panel renders unknown rather than a confident nothing.
    /// </summary>
    [Test]
    public async Task ListRecurringJobs_ProcessingUnavailable_NamesTheSectionAndReturnsNothing()
    {
        IMonitoringApi monitoring = CreateHealthyMonitoring();
        monitoring.ProcessingCount().Returns(7);
        monitoring.ProcessingJobs(Arg.Any<int>(), Arg.Any<int>())
            .Throws(new InvalidOperationException("processing list unavailable"));

        ListRecurringJobsResponse response = await ListAgainstAsync(monitoring);

        await Assert.That(response.UnavailableSections).Contains("processing");
        await Assert.That(response.ProcessingJobs.Count).IsEqualTo(0);

        // The count is governed by the same section, so a partially-filled response would let the
        // panel show "7 running" beside an empty list it has just been told is unknown.
        await Assert.That(response.ProcessingJobCount).IsEqualTo(0);
        await Assert.That(response.Workers.Count).IsEqualTo(1);
    }

    /// <summary>
    /// A job carrying Hangfire's RecurringJobId parameter belongs to a schedule, and saying so is
    /// what lets an operator connect a long-running row to the cron entry that produced it.
    /// </summary>
    [Test]
    public async Task ListRecurringJobs_ProcessingJobFromASchedule_AttributesItToTheRecurringJob()
    {
        IMonitoringApi monitoring = CreateHealthyMonitoring();
        monitoring.ProcessingJobs(Arg.Any<int>(), Arg.Any<int>())
            .Returns(ProcessingList(("101", CreateProcessing("worker-a"))));
        monitoring.JobDetails("101").Returns(CreateDetails(
            new Dictionary<string, string>
            {
                ["RecurringJobId"] = $"\"{RecurringJobIds.TenantPurge}\"",
            }));

        ListRecurringJobsResponse response = await ListAgainstAsync(monitoring);

        FleetProcessingJob running = response.ProcessingJobs.Single();

        await Assert.That(running.Attribution).IsEqualTo(FleetProcessingAttribution.Recurring);
        await Assert.That(running.RecurringJobId).IsEqualTo(RecurringJobIds.TenantPurge);
    }

    /// <summary>
    /// Fan-out children and on-demand work have no schedule behind them and never will. That is a
    /// fact about the job, not a gap in the read, and the two must not be conflated.
    /// </summary>
    [Test]
    public async Task ListRecurringJobs_ProcessingJobWithNoSchedule_ReportsItAsNotRecurring()
    {
        IMonitoringApi monitoring = CreateHealthyMonitoring();
        monitoring.ProcessingJobs(Arg.Any<int>(), Arg.Any<int>())
            .Returns(ProcessingList(("101", CreateProcessing("worker-a"))));
        monitoring.JobDetails("101").Returns(CreateDetails(new Dictionary<string, string>()));

        ListRecurringJobsResponse response = await ListAgainstAsync(monitoring);

        FleetProcessingJob running = response.ProcessingJobs.Single();

        await Assert.That(running.Attribution).IsEqualTo(FleetProcessingAttribution.NotRecurring);
        await Assert.That(running.RecurringJobId).IsEqualTo("");
    }

    /// <summary>
    /// When the attribution read itself fails the row must survive, unattributed. Dropping it
    /// would hide the runaway job this view exists to expose, and calling it NotRecurring would
    /// assert something storage never said.
    /// </summary>
    [Test]
    public async Task ListRecurringJobs_ProcessingJobDetailsThrow_KeepsTheRowAsUnattributed()
    {
        IMonitoringApi monitoring = CreateHealthyMonitoring();
        monitoring.ProcessingJobs(Arg.Any<int>(), Arg.Any<int>())
            .Returns(ProcessingList(("101", CreateProcessing("worker-a"))));
        monitoring.JobDetails("101").Throws(new FormatException("not a number"));

        ListRecurringJobsResponse response = await ListAgainstAsync(monitoring);

        FleetProcessingJob running = response.ProcessingJobs.Single();

        await Assert.That(running.Attribution).IsEqualTo(FleetProcessingAttribution.Unknown);
        await Assert.That(running.JobId).IsEqualTo("101");
        await Assert.That(response.UnavailableSections).DoesNotContain("processing");
    }

    /// <summary>
    /// Hangfire's own page is bounded so a wedged fleet cannot make this a full-table read, and a
    /// short page means there is nothing more to scan — asking for another would be a wasted
    /// round-trip against the database the operator is already worried about.
    /// </summary>
    [Test]
    public async Task ListRecurringJobs_ProcessingListShorterThanAPage_IsReadInOneQuery()
    {
        IMonitoringApi monitoring = CreateHealthyMonitoring();

        await ListAgainstAsync(monitoring);

        monitoring.Received(1).ProcessingJobs(0, 200);
        monitoring.DidNotReceive().ProcessingJobs(Arg.Is<int>(from => from > 0), Arg.Any<int>());
    }

    /// <summary>
    /// The cap must keep the jobs that have been running LONGEST. Hangfire's PostgreSQL storage
    /// orders its processing page by descending job id, so simply taking that page would retain
    /// the twenty-five newest rows and silently drop the runaway job — telling an operator that
    /// nothing is running long is the one wrong answer this whole section exists to prevent.
    /// </summary>
    [Test]
    public async Task ListRecurringJobs_MoreInFlightJobsThanTheCap_KeepsTheOnesRunningLongest()
    {
        DateTime baseline = new(2026, 8, 28, 9, 0, 0, DateTimeKind.Utc);

        // Newest first, as the PostgreSQL storage returns them: job "0" is the six-hour straggler.
        (string JobId, ProcessingJobDto Processing)[] newestFirst = Enumerable.Range(0, 30)
            .Reverse()
            .Select(i => (i.ToString(), CreateProcessing("worker-a", baseline.AddMinutes(i))))
            .ToArray();

        IMonitoringApi monitoring = CreateHealthyMonitoring();
        monitoring.ProcessingJobs(Arg.Any<int>(), Arg.Any<int>()).Returns(ProcessingList(newestFirst));

        ListRecurringJobsResponse response = await ListAgainstAsync(monitoring);

        await Assert.That(response.ProcessingJobs.Count).IsEqualTo(25);

        // Oldest first, so the straggler is the headline row rather than one the cap discarded.
        // Joined rather than compared as collections: IsEquivalentTo ignores order, so it would
        // hold for every permutation and assert nothing about the ranking this test is named for.
        await Assert.That(string.Join(",", response.ProcessingJobs.Select(j => j.JobId)))
            .IsEqualTo(string.Join(",", Enumerable.Range(0, 25)));
    }

    /// <summary>
    /// A row Hangfire reports without a start time cannot be ranked by age, so it must not
    /// displace one that can: the cap exists to keep the longest-running work, and an unknown
    /// start is the least informative answer to that question.
    /// </summary>
    [Test]
    public async Task ListRecurringJobs_InFlightJobWithNoStartTime_RanksBehindOnesThatHaveOne()
    {
        DateTime startedAt = new(2026, 8, 28, 9, 0, 0, DateTimeKind.Utc);

        IMonitoringApi monitoring = CreateHealthyMonitoring();
        monitoring.ProcessingJobs(Arg.Any<int>(), Arg.Any<int>()).Returns(ProcessingList(
            ("no-start", CreateProcessing("worker-a")),
            ("started", CreateProcessing("worker-a", startedAt))));

        ListRecurringJobsResponse response = await ListAgainstAsync(monitoring);

        await Assert.That(string.Join(",", response.ProcessingJobs.Select(j => j.JobId)))
            .IsEqualTo("started,no-start");
    }

    /// <summary>
    /// The total and the list are separate storage reads, so work dequeued between them leaves a
    /// total below the rows it describes. The list is what the operator can see, so the total is
    /// raised to meet it rather than letting a panel print "0 running" above five visible rows.
    /// </summary>
    [Test]
    public async Task ListRecurringJobs_CountReadBeforeTheWorkStarted_NeverUndercountsTheRowsReturned()
    {
        IMonitoringApi monitoring = CreateHealthyMonitoring();
        monitoring.ProcessingCount().Returns(0);
        monitoring.ProcessingJobs(Arg.Any<int>(), Arg.Any<int>()).Returns(ProcessingList(
            ("101", CreateProcessing("worker-a")),
            ("102", CreateProcessing("worker-a"))));

        ListRecurringJobsResponse response = await ListAgainstAsync(monitoring);

        await Assert.That(response.ProcessingJobCount).IsEqualTo(2);
    }

    /// <summary>
    /// Hangfire reports a job whose payload it cannot deserialise with no Job at all and the
    /// reason on LoadException. That job is still genuinely executing, so the row has to survive
    /// with an empty name and the error attached — a mid-deploy type rename is exactly when an
    /// operator needs to see what is still running.
    /// </summary>
    [Test]
    public async Task ListRecurringJobs_InFlightJobWithAnUnreadablePayload_KeepsTheRowAndReportsWhy()
    {
        IMonitoringApi monitoring = CreateHealthyMonitoring();
        monitoring.ProcessingCount().Returns(1);
        monitoring.ProcessingJobs(Arg.Any<int>(), Arg.Any<int>())
            .Returns(ProcessingList(("101", CreateUnloadableProcessing("worker-a"))));

        ListRecurringJobsResponse response = await ListAgainstAsync(monitoring);

        FleetProcessingJob running = response.ProcessingJobs.Single();

        await Assert.That(running.JobId).IsEqualTo("101");
        await Assert.That(running.JobName).IsEqualTo("");
        await Assert.That(running.Queue).IsEqualTo("");
        await Assert.That(running.ServerId).IsEqualTo("worker-a");
        await Assert.That(running.LoadError).IsNotEmpty();
        await Assert.That(response.UnavailableSections).DoesNotContain("processing");
    }

    /// <summary>
    /// Attribution is one storage read per row against the database that is probably the reason
    /// the operator is here, and the caller bounds the whole RPC with a fixed deadline. Once the
    /// budget is gone the remaining rows must ship unattributed rather than push the response past
    /// that deadline — an unattributed row still shows the runaway job; a dead RPC shows nothing.
    /// </summary>
    [Test]
    public async Task ListRecurringJobs_AttributionBudgetExhausted_ShipsTheRemainingRowsUnattributed()
    {
        FakeTimeProvider timeProvider = new(new DateTimeOffset(2026, 8, 28, 9, 0, 0, TimeSpan.Zero));

        IMonitoringApi monitoring = CreateHealthyMonitoring();
        monitoring.ProcessingJobs(Arg.Any<int>(), Arg.Any<int>()).Returns(ProcessingList(
            ("101", CreateProcessing("worker-a", new DateTime(2026, 8, 28, 8, 0, 0, DateTimeKind.Utc))),
            ("102", CreateProcessing("worker-a", new DateTime(2026, 8, 28, 8, 30, 0, DateTimeKind.Utc)))));

        // One lookup is made to cost more than the whole two-second budget, so the first row is
        // attributed and the second is not. Driving the clock from the call keeps the test
        // deterministic — nothing here waits on wall time.
        monitoring.JobDetails(Arg.Any<string>()).Returns(_ =>
        {
            timeProvider.Advance(TimeSpan.FromMilliseconds(2100));

            return CreateDetails(new Dictionary<string, string>
            {
                ["RecurringJobId"] = $"\"{RecurringJobIds.TenantPurge}\"",
            });
        });

        ListRecurringJobsResponse response = await ListAgainstAsync(monitoring, timeProvider: timeProvider);

        await Assert.That(response.ProcessingJobs.Count).IsEqualTo(2);
        await Assert.That(response.ProcessingJobs[0].Attribution)
            .IsEqualTo(FleetProcessingAttribution.Recurring);
        await Assert.That(response.ProcessingJobs[1].Attribution)
            .IsEqualTo(FleetProcessingAttribution.Unknown);

        // The second row's lookup must not have been issued at all: the point of the budget is to
        // stop querying a struggling database, not to discard what it returned.
        monitoring.Received(1).JobDetails(Arg.Any<string>());
    }

    /// <summary>
    /// A monitoring API with every section answering healthily, for tests to break one at a time.
    /// </summary>
    /// <remarks>
    /// Every member the RPC touches has to be stubbed here, including the ones a given test does
    /// not care about. An unstubbed member returns null, the section's own try/catch swallows the
    /// resulting NullReferenceException, and the test passes while quietly reporting that section
    /// as unavailable — green, and testing the wrong behaviour.
    /// </remarks>
    private static IMonitoringApi CreateHealthyMonitoring()
    {
        IMonitoringApi monitoring = Substitute.For<IMonitoringApi>();
        monitoring.GetStatistics().Returns(new StatisticsDto());
        monitoring.Servers().Returns(new List<ServerDto> { new() { Name = "worker-a" } });
        monitoring.ProcessingCount().Returns(0);
        monitoring.ProcessingJobs(Arg.Any<int>(), Arg.Any<int>()).Returns(ProcessingList());
        monitoring.JobDetails(Arg.Any<string>()).Returns((JobDetailsDto?)null);

        return monitoring;
    }

    /// <summary>Builds Hangfire's processing page, whose key — not the DTO — carries the job id.</summary>
    private static JobList<ProcessingJobDto> ProcessingList(
        params (string JobId, ProcessingJobDto Processing)[] entries)
    {
        return new JobList<ProcessingJobDto>(
            entries.Select(e => new KeyValuePair<string, ProcessingJobDto>(e.JobId, e.Processing)));
    }

    /// <remarks>
    /// Job.Queue is deliberately never set. Hangfire only populates it when the job is created
    /// with an explicit queue, which nothing in this codebase does — the routing comes from the
    /// QueueAttribute elect-state filter. Passing a queue here would fabricate a state production
    /// never produces, and would hide a mapping that reads the wrong source.
    /// </remarks>
    private static ProcessingJobDto CreateProcessing(
        string serverId, DateTime? startedAt = null, Type? jobType = null)
    {
        Type type = jobType ?? typeof(SeedProbeJob);
        MethodInfo method = type.GetMethod("RunAsync")!;
        object[] arguments = [CancellationToken.None];

        return new ProcessingJobDto
        {
            ServerId = serverId,
            StartedAt = startedAt,
            InProcessingState = true,
            Job = new Job(type, method, arguments),
        };
    }

    /// <summary>
    /// Builds the row Hangfire produces when it cannot deserialise a job's payload: no Job at all,
    /// with the reason on LoadException. Constructed through InvocationData rather than by setting
    /// the two fields directly, so the shape stays whatever Hangfire itself would emit.
    /// </summary>
    private static ProcessingJobDto CreateUnloadableProcessing(string serverId)
    {
        InvocationData invocation = new(
            "Framlux.Does.Not.Exist, NoSuchAssembly", "RunAsync", "[]", "[]");

        JobLoadException? loadException = null;

        try
        {
            invocation.DeserializeJob();
        }
        catch (JobLoadException ex)
        {
            loadException = ex;
        }

        return new ProcessingJobDto
        {
            ServerId = serverId,
            InProcessingState = true,
            Job = null,
            LoadException = loadException,
        };
    }

    private static JobDetailsDto CreateDetails(Dictionary<string, string> properties)
    {
        return new JobDetailsDto { Properties = properties, History = [] };
    }

    private static async Task<ListRecurringJobsResponse> ListAgainstAsync(
        IMonitoringApi monitoring,
        ListRecurringJobsRequest? request = null,
        TimeProvider? timeProvider = null)
    {
        using InMemoryStorage backing = new();
        JobStorage storage = Substitute.For<JobStorage>();
        storage.GetMonitoringApi().Returns(monitoring);
        storage.GetConnection().Returns(_ => backing.GetConnection());

        FleetAdminService service = CreateFleetAdminService(
            Substitute.For<IServiceScopeFactory>(), storage: storage, timeProvider: timeProvider);

        return await service.ListRecurringJobs(request ?? new ListRecurringJobsRequest(), CreateContext());
    }

    private static async Task<RpcException?> RunAgainstSeededStorageAsync(IBackgroundJobClientV2 backgroundJobs)
    {
        using InMemoryStorage storage = new();
        SeedRecurringJob(storage, RecurringJobIds.TenantPurge, unloadable: false);

        FleetAdminService service = CreateFleetAdminService(
            Substitute.For<IServiceScopeFactory>(),
            backgroundJobs: backgroundJobs,
            storage: storage,
            redis: CreateRedisAllowing());

        RunRecurringJobNowRequest request = new()
        {
            JobId = RecurringJobIds.TenantPurge,
            TriggeredBy = "ops@framlux.io",
        };

        return await Assert.ThrowsAsync<RpcException>(
            async () => await service.RunRecurringJobNow(request, CreateContext()));
    }

    private static IConnectionMultiplexer CreateRedisAllowing()
    {
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        IDatabase database = Substitute.For<IDatabase>();
        database.StringSetAsync(
                Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<When>())
            .Returns(true);
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);

        return redis;
    }

    private static void SeedRecurringJob(InMemoryStorage storage, string id, bool unloadable)
    {
        string payload = unloadable
            ? "{\"Type\":\"Framlux.Does.Not.Exist, NoSuchAssembly\",\"Method\":\"RunAsync\",\"ParameterTypes\":\"[]\",\"Arguments\":\"[]\"}"
            : InvocationData.SerializeJob(
                Job.FromExpression<SeedProbeJob>(j => j.RunAsync(CancellationToken.None))).SerializePayload();

        Dictionary<string, string> hash = new()
        {
            ["Cron"] = "* * * * *",
            ["Job"] = payload,
            ["NextExecution"] = JobHelper.SerializeDateTime(DateTime.UtcNow.AddMinutes(10)),
            ["TimeZoneId"] = TimeZoneInfo.Utc.Id,
        };

        using IStorageConnection connection = storage.GetConnection();
        using IWriteOnlyTransaction transaction = connection.CreateWriteTransaction();
        transaction.AddToSet("recurring-jobs", id, 0);
        transaction.SetRangeInHash($"recurring-job:{id}", hash);
        transaction.Commit();
    }

    /// <summary>Stand-in job type used only to produce a resolvable payload for these fixtures.</summary>
    public sealed class SeedProbeJob
    {
        /// <summary>Never invoked; the payload only has to deserialise.</summary>
        public Task RunAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// A probe shaped like every real Vord job: the queue comes from the attribute, and nothing
    /// ever sets Job.Queue. Constructing a Job with an explicit queue instead would test a state
    /// production never reaches, and would pass whether or not the attribute is honoured.
    /// </summary>
    public sealed class QueuedProbeJob
    {
        /// <summary>Never invoked; the payload only has to deserialise.</summary>
        [Queue("critical")]
        public Task RunAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    private static IInternalCallerAuthorizer CreateAuthorizer(StatusCode? rejectWith)
    {
        IInternalCallerAuthorizer authorizer = Substitute.For<IInternalCallerAuthorizer>();
        if (rejectWith is not null)
        {
            authorizer
                .When(a => a.Authorize(Arg.Any<ServerCallContext>()))
                .Do(_ => throw new RpcException(new Status(rejectWith.Value, "Rejected")));
        }

        return authorizer;
    }

    private static IServiceScopeFactory CreateScopeFactoryWithServices(Dictionary<Type, object> services)
    {
        IServiceProvider serviceProvider = Substitute.For<IServiceProvider>();
        foreach (KeyValuePair<Type, object> entry in services)
        {
            serviceProvider.GetService(entry.Key).Returns(entry.Value);
        }

        IServiceScope scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(serviceProvider);

        IServiceScopeFactory scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(scope);

        return scopeFactory;
    }

    private static ServerCallContext CreateContext()
    {
        Metadata headers = new Metadata();

        return TestServerCallContext.Create(
            method: "Test",
            host: "localhost",
            deadline: DateTime.UtcNow.AddMinutes(1),
            requestHeaders: headers,
            cancellationToken: CancellationToken.None,
            peer: "127.0.0.1",
            authContext: null,
            contextPropagationToken: null,
            writeHeadersFunc: _ => Task.CompletedTask,
            writeOptionsGetter: () => new WriteOptions(),
            writeOptionsSetter: _ => { });
    }

    private static Tenant MakeTenant(int id = TenantInternalId, string externalId = TenantExternalId)
    {
        return new Tenant
        {
            Id = id,
            ExternalId = externalId,
            Name = "Test Corp",
            IsActive = true,
            LogoUrl = string.Empty,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 1
        };
    }

    // ── Caller authorisation ──

    /// <summary>
    /// The recurring-job read must consult the authorizer before touching Hangfire storage.
    /// </summary>
    [Test]
    public async Task ListRecurringJobs_AuthorizerRejects_Throws()
    {
        IServiceScopeFactory scopeFactory = Substitute.For<IServiceScopeFactory>();
        FleetAdminService service = CreateFleetAdminService(scopeFactory, rejectWith: StatusCode.PermissionDenied);
        ServerCallContext context = CreateContext();

        RpcException? exception = await Assert.ThrowsAsync<RpcException>(
            async () => await service.ListRecurringJobs(new ListRecurringJobsRequest(), context));

        await Assert.That(exception!.StatusCode).IsEqualTo(StatusCode.PermissionDenied);
    }

    /// <summary>
    /// The on-demand run is the only mutating background-infrastructure RPC, so its authorisation
    /// check must run before the allow-list validation and before anything is enqueued.
    /// </summary>
    [Test]
    public async Task RunRecurringJobNow_AuthorizerRejects_ThrowsAndEnqueuesNothing()
    {
        IServiceScopeFactory scopeFactory = Substitute.For<IServiceScopeFactory>();
        IBackgroundJobClientV2 backgroundJobs = Substitute.For<IBackgroundJobClientV2>();
        FleetAdminService service = CreateFleetAdminService(
            scopeFactory, rejectWith: StatusCode.PermissionDenied, backgroundJobs: backgroundJobs);
        ServerCallContext context = CreateContext();

        RunRecurringJobNowRequest request = new()
        {
            JobId = RecurringJobIds.TenantPurge,
            TriggeredBy = "ops@framlux.io",
        };

        RpcException? exception = await Assert.ThrowsAsync<RpcException>(
            async () => await service.RunRecurringJobNow(request, context));

        await Assert.That(exception!.StatusCode).IsEqualTo(StatusCode.PermissionDenied);
        backgroundJobs.DidNotReceiveWithAnyArgs().Create(default!, default!, default!);
    }

    /// <summary>
    /// When no permitted client subject is configured, RPC methods must throw Unavailable
    /// before touching any repository.
    /// </summary>
    [Test]
    public async Task ListUsers_AuthorizerReportsUnconfigured_ThrowsUnavailable()
    {
        IServiceScopeFactory scopeFactory = Substitute.For<IServiceScopeFactory>();
        FleetAdminService service = CreateFleetAdminService(scopeFactory, rejectWith: StatusCode.Unavailable);
        ServerCallContext context = CreateContext();

        RpcException? exception = await Assert.ThrowsAsync<RpcException>(
            async () => await service.ListUsers(new ListUsersRequest(), context));

        await Assert.That(exception).IsNotNull();
        await Assert.That(exception!.StatusCode).IsEqualTo(StatusCode.Unavailable);
    }

    /// <summary>
    /// A caller that presented no client certificate is rejected as unauthenticated.
    /// </summary>
    [Test]
    public async Task ListUsers_NoClientCertificate_ThrowsUnauthenticated()
    {
        IServiceScopeFactory scopeFactory = Substitute.For<IServiceScopeFactory>();
        FleetAdminService service = CreateFleetAdminService(scopeFactory, rejectWith: StatusCode.Unauthenticated);
        ServerCallContext context = CreateContext();

        RpcException? exception = await Assert.ThrowsAsync<RpcException>(
            async () => await service.ListUsers(new ListUsersRequest(), context));

        await Assert.That(exception).IsNotNull();
        await Assert.That(exception!.StatusCode).IsEqualTo(StatusCode.Unauthenticated);
    }

    /// <summary>
    /// A caller holding a certificate the internal CA issued, but whose subject is not on the
    /// permitted list, is rejected without any repository being touched.
    /// </summary>
    [Test]
    public async Task ListUsers_NonPermittedCertificateSubject_ThrowsPermissionDenied()
    {
        IServiceScopeFactory scopeFactory = Substitute.For<IServiceScopeFactory>();
        FleetAdminService service = CreateFleetAdminService(scopeFactory, rejectWith: StatusCode.PermissionDenied);
        ServerCallContext context = CreateContext();

        RpcException? exception = await Assert.ThrowsAsync<RpcException>(
            async () => await service.ListUsers(new ListUsersRequest(), context));

        await Assert.That(exception).IsNotNull();
        await Assert.That(exception!.StatusCode).IsEqualTo(StatusCode.PermissionDenied);
        scopeFactory.DidNotReceive().CreateScope();
    }

    // ── ListUsers ──

    /// <summary>
    /// ListUsers returns users and their tenant roles when the caller is authorised.
    /// </summary>
    [Test]
    public async Task ListUsers_AuthorizedCaller_ReturnsMappedUsers()
    {
        IUserRepository userRepo = Substitute.For<IUserRepository>();
        ITenantRepository tenantRepo = Substitute.For<ITenantRepository>();

        UserAccount user = new UserAccount
        {
            Id = 1,
            ExternalId = "ext-u1",
            Username = "alice",
            IsActive = true,
            IsGlobalAdmin = false,
            AuthProvider = AuthProviderType.GitHub,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 1,
            IsSystem = false
        };

        userRepo.SearchUsersPagedAsync(Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((new List<UserAccount> { user }, 1));
        tenantRepo.GetActiveRolesForUsersAsync(Arg.Any<List<int>>(), Arg.Any<CancellationToken>())
            .Returns(new List<UserTenantRole>());
        tenantRepo.ListTenantsByIdsAsync(Arg.Any<List<int>>(), Arg.Any<CancellationToken>())
            .Returns(new List<Tenant>());

        IServiceScopeFactory scopeFactory = CreateScopeFactoryWithServices(new Dictionary<Type, object>
        {
            { typeof(IUserRepository), userRepo },
            { typeof(ITenantRepository), tenantRepo }
        });

        FleetAdminService service = CreateFleetAdminService(scopeFactory);
        ServerCallContext context = CreateContext();

        ListUsersResponse response = await service.ListUsers(new ListUsersRequest(), context);

        await Assert.That(response.TotalCount).IsEqualTo(1);
        await Assert.That(response.Users.Count).IsEqualTo(1);
        await Assert.That(response.Users[0].Username).IsEqualTo("alice");
    }

    // ── ListTenants ──

    /// <summary>
    /// ListTenants returns tenant list with counts when valid key is used.
    /// </summary>
    [Test]
    public async Task ListTenants_ValidKey_ReturnsMappedTenants()
    {
        ITenantRepository tenantRepo = Substitute.For<ITenantRepository>();
        IMachineRepository machineRepo = Substitute.For<IMachineRepository>();
        ISubscriptionRepository subscriptionRepo = Substitute.For<ISubscriptionRepository>();
        ITierFeatureLimitRepository tierLimitRepo = Substitute.For<ITierFeatureLimitRepository>();

        Tenant tenant = MakeTenant();

        tenantRepo.SearchTenantsPagedAsync(Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((new List<Tenant> { tenant }, 1));
        machineRepo.GetMachineCountsByTenantsAsync(Arg.Any<List<int>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, int> { { TenantInternalId, 3 } });
        tenantRepo.GetActiveRolesForTenantsAsync(Arg.Any<List<int>>(), Arg.Any<CancellationToken>())
            .Returns(new List<UserTenantRole>());
        subscriptionRepo.GetSubscriptionsForTenantsAsync(Arg.Any<List<int>>(), Arg.Any<CancellationToken>())
            .Returns(new List<TenantSubscription>());
        tierLimitRepo.GetAllLimitsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<TierFeatureLimit>());

        IServiceScopeFactory scopeFactory = CreateScopeFactoryWithServices(new Dictionary<Type, object>
        {
            { typeof(ITenantRepository), tenantRepo },
            { typeof(IMachineRepository), machineRepo },
            { typeof(ISubscriptionRepository), subscriptionRepo },
            { typeof(ITierFeatureLimitRepository), tierLimitRepo }
        });

        FleetAdminService service = CreateFleetAdminService(scopeFactory);
        ServerCallContext context = CreateContext();

        ListTenantsResponse response = await service.ListTenants(new ListTenantsRequest(), context);

        await Assert.That(response.TotalCount).IsEqualTo(1);
        await Assert.That(response.Tenants.Count).IsEqualTo(1);
        await Assert.That(response.Tenants[0].Name).IsEqualTo("Test Corp");
        await Assert.That(response.Tenants[0].MachineCount).IsEqualTo(3);
    }

    /// <summary>
    /// ListTenants maps subscription and tier limits when both are present.
    /// </summary>
    [Test]
    public async Task ListTenants_TenantWithSubscriptionAndTierLimits_MapsLimitsCorrectly()
    {
        ITenantRepository tenantRepo = Substitute.For<ITenantRepository>();
        IMachineRepository machineRepo = Substitute.For<IMachineRepository>();
        ISubscriptionRepository subscriptionRepo = Substitute.For<ISubscriptionRepository>();
        ITierFeatureLimitRepository tierLimitRepo = Substitute.For<ITierFeatureLimitRepository>();

        Tenant tenant = MakeTenant();
        TenantSubscription subscription = new TenantSubscription
        {
            TenantId = TenantInternalId,
            Tier = SubscriptionTier.Pro,
            Status = SubscriptionStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        TierFeatureLimit limits = new TierFeatureLimit
        {
            Tier = SubscriptionTier.Pro,
            MachineLimit = 50,
            RetentionDays = 60,
            AlertRuleLimit = 10,
            WebhookLimit = 5,
            MemberLimit = 5,
            MinimumBillableMachines = 1,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        tenantRepo.SearchTenantsPagedAsync(Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((new List<Tenant> { tenant }, 1));
        machineRepo.GetMachineCountsByTenantsAsync(Arg.Any<List<int>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, int>());
        tenantRepo.GetActiveRolesForTenantsAsync(Arg.Any<List<int>>(), Arg.Any<CancellationToken>())
            .Returns(new List<UserTenantRole>());
        subscriptionRepo.GetSubscriptionsForTenantsAsync(Arg.Any<List<int>>(), Arg.Any<CancellationToken>())
            .Returns(new List<TenantSubscription> { subscription });
        tierLimitRepo.GetAllLimitsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<TierFeatureLimit> { limits });

        IServiceScopeFactory scopeFactory = CreateScopeFactoryWithServices(new Dictionary<Type, object>
        {
            { typeof(ITenantRepository), tenantRepo },
            { typeof(IMachineRepository), machineRepo },
            { typeof(ISubscriptionRepository), subscriptionRepo },
            { typeof(ITierFeatureLimitRepository), tierLimitRepo }
        });

        FleetAdminService service = CreateFleetAdminService(scopeFactory);
        ServerCallContext context = CreateContext();

        ListTenantsResponse response = await service.ListTenants(new ListTenantsRequest(), context);

        await Assert.That(response.Tenants[0].Subscription).IsNotNull();
        await Assert.That(response.Tenants[0].Subscription.MachineLimit).IsEqualTo(50);
    }

    // ── GetTenantDetail ──

    /// <summary>
    /// GetTenantDetail returns users and machines for a valid tenant external ID.
    /// </summary>
    [Test]
    public async Task GetTenantDetail_ValidTenant_ReturnsTenantWithUsersAndMachines()
    {
        ITenantRepository tenantRepo = Substitute.For<ITenantRepository>();
        IUserRepository userRepo = Substitute.For<IUserRepository>();
        IMachineRepository machineRepo = Substitute.For<IMachineRepository>();
        ISubscriptionRepository subscriptionRepo = Substitute.For<ISubscriptionRepository>();
        ITierFeatureLimitRepository tierLimitRepo = Substitute.For<ITierFeatureLimitRepository>();

        Tenant tenant = MakeTenant();
        Machine machine = new Machine
        {
            Id = 1,
            Name = "worker-01",
            TenantId = TenantInternalId,
            IsDeleted = false,
            RegisteredOn = DateTimeOffset.UtcNow,
            ApiKeyHash = "h",
            SerialNumber = "S",
            SystemId = "SYS",
            MachineType = MachineTypes.BareMetalServer,
            OperatingSystem = OperatingSystems.Ubuntu,
            RegistrationTokenId = 1
        };

        tenantRepo.GetTenantByExternalIdAsync(TenantExternalId, Arg.Any<CancellationToken>())
            .Returns(tenant);
        tenantRepo.GetActiveRolesForTenantsAsync(Arg.Is<List<int>>(ids => (ids.Count == 1) && (ids[0] == TenantInternalId)), Arg.Any<CancellationToken>())
            .Returns(new List<UserTenantRole>());
        userRepo.GetUsersByIdsAsync(Arg.Any<List<int>>(), Arg.Any<CancellationToken>())
            .Returns(new List<UserAccount>());
        machineRepo.SearchMachinesPagedAsync(TenantInternalId, 0, 10000, Arg.Any<CancellationToken>())
            .Returns((new List<Machine> { machine }, 1));
        subscriptionRepo.GetSubscriptionForTenantAsync(TenantInternalId, Arg.Any<CancellationToken>())
            .Returns((TenantSubscription?)null);
        tierLimitRepo.GetLimitsForTierAsync(Arg.Any<SubscriptionTier>(), Arg.Any<CancellationToken>())
            .Returns((TierFeatureLimit?)null);

        IServiceScopeFactory scopeFactory = CreateScopeFactoryWithServices(new Dictionary<Type, object>
        {
            { typeof(ITenantRepository), tenantRepo },
            { typeof(IUserRepository), userRepo },
            { typeof(IMachineRepository), machineRepo },
            { typeof(ISubscriptionRepository), subscriptionRepo },
            { typeof(ITierFeatureLimitRepository), tierLimitRepo }
        });

        FleetAdminService service = CreateFleetAdminService(scopeFactory);
        ServerCallContext context = CreateContext();

        GetTenantDetailResponse response = await service.GetTenantDetail(
            new GetTenantDetailRequest { TenantExternalId = TenantExternalId }, context);

        await Assert.That(response.Tenant.Name).IsEqualTo("Test Corp");
        await Assert.That(response.Machines.Count).IsEqualTo(1);
        await Assert.That(response.Machines[0].Name).IsEqualTo("worker-01");
    }

    /// <summary>
    /// GetTenantDetail throws NotFound when the tenant external ID does not exist.
    /// </summary>
    [Test]
    public async Task GetTenantDetail_TenantNotFound_ThrowsNotFound()
    {
        ITenantRepository tenantRepo = Substitute.For<ITenantRepository>();
        IUserRepository userRepo = Substitute.For<IUserRepository>();
        IMachineRepository machineRepo = Substitute.For<IMachineRepository>();
        ISubscriptionRepository subscriptionRepo = Substitute.For<ISubscriptionRepository>();
        ITierFeatureLimitRepository tierLimitRepo = Substitute.For<ITierFeatureLimitRepository>();

        // ResolveTenantByExternalIdAsync returns null which causes NotFound to be thrown.
        tenantRepo.GetTenantByExternalIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((Tenant?)null);

        IServiceScopeFactory scopeFactory = CreateScopeFactoryWithServices(new Dictionary<Type, object>
        {
            { typeof(ITenantRepository), tenantRepo },
            { typeof(IUserRepository), userRepo },
            { typeof(IMachineRepository), machineRepo },
            { typeof(ISubscriptionRepository), subscriptionRepo },
            { typeof(ITierFeatureLimitRepository), tierLimitRepo }
        });

        FleetAdminService service = CreateFleetAdminService(scopeFactory);
        ServerCallContext context = CreateContext();

        RpcException? exception = await Assert.ThrowsAsync<RpcException>(
            async () => await service.GetTenantDetail(
                new GetTenantDetailRequest { TenantExternalId = "nonexistent" }, context));

        await Assert.That(exception).IsNotNull();
        await Assert.That(exception!.StatusCode).IsEqualTo(StatusCode.NotFound);
        await Assert.That(exception.Status.Detail).Contains("nonexistent");
    }

    /// <summary>
    /// GetTenantDetail loads tier limits when a subscription is present.
    /// </summary>
    [Test]
    public async Task GetTenantDetail_TenantWithSubscription_LoadsTierLimits()
    {
        ITenantRepository tenantRepo = Substitute.For<ITenantRepository>();
        IUserRepository userRepo = Substitute.For<IUserRepository>();
        IMachineRepository machineRepo = Substitute.For<IMachineRepository>();
        ISubscriptionRepository subscriptionRepo = Substitute.For<ISubscriptionRepository>();
        ITierFeatureLimitRepository tierLimitRepo = Substitute.For<ITierFeatureLimitRepository>();

        Tenant tenant = MakeTenant();
        TenantSubscription subscription = new TenantSubscription
        {
            TenantId = TenantInternalId,
            Tier = SubscriptionTier.Team,
            Status = SubscriptionStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        TierFeatureLimit limits = new TierFeatureLimit
        {
            Tier = SubscriptionTier.Team,
            MachineLimit = 200,
            RetentionDays = 90,
            AlertRuleLimit = 50,
            WebhookLimit = 20,
            MemberLimit = int.MaxValue,
            MinimumBillableMachines = 3,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        tenantRepo.GetTenantByExternalIdAsync(TenantExternalId, Arg.Any<CancellationToken>())
            .Returns(tenant);
        tenantRepo.GetActiveRolesForTenantsAsync(Arg.Is<List<int>>(ids => (ids.Count == 1) && (ids[0] == TenantInternalId)), Arg.Any<CancellationToken>())
            .Returns(new List<UserTenantRole>());
        userRepo.GetUsersByIdsAsync(Arg.Any<List<int>>(), Arg.Any<CancellationToken>())
            .Returns(new List<UserAccount>());
        machineRepo.SearchMachinesPagedAsync(TenantInternalId, 0, 10000, Arg.Any<CancellationToken>())
            .Returns((new List<Machine>(), 0));
        subscriptionRepo.GetSubscriptionForTenantAsync(TenantInternalId, Arg.Any<CancellationToken>())
            .Returns(subscription);
        tierLimitRepo.GetLimitsForTierAsync(SubscriptionTier.Team, Arg.Any<CancellationToken>())
            .Returns(limits);

        IServiceScopeFactory scopeFactory = CreateScopeFactoryWithServices(new Dictionary<Type, object>
        {
            { typeof(ITenantRepository), tenantRepo },
            { typeof(IUserRepository), userRepo },
            { typeof(IMachineRepository), machineRepo },
            { typeof(ISubscriptionRepository), subscriptionRepo },
            { typeof(ITierFeatureLimitRepository), tierLimitRepo }
        });

        FleetAdminService service = CreateFleetAdminService(scopeFactory);
        ServerCallContext context = CreateContext();

        GetTenantDetailResponse response = await service.GetTenantDetail(
            new GetTenantDetailRequest { TenantExternalId = TenantExternalId }, context);

        await Assert.That(response.Tenant.Subscription).IsNotNull();
        await Assert.That(response.Tenant.Subscription.MachineLimit).IsEqualTo(200);
    }

    // ── ListMachines ──

    /// <summary>
    /// ListMachines without a tenant filter returns all machines across tenants.
    /// </summary>
    [Test]
    public async Task ListMachines_NoTenantFilter_ReturnsAllMachines()
    {
        ITenantRepository tenantRepo = Substitute.For<ITenantRepository>();
        IMachineRepository machineRepo = Substitute.For<IMachineRepository>();

        Machine machine = new Machine
        {
            Id = 5,
            Name = "db-01",
            TenantId = 99,
            IsDeleted = false,
            RegisteredOn = DateTimeOffset.UtcNow,
            ApiKeyHash = "h",
            SerialNumber = "S",
            SystemId = "SYS",
            MachineType = MachineTypes.BareMetalServer,
            OperatingSystem = OperatingSystems.Ubuntu,
            RegistrationTokenId = 1
        };

        machineRepo.SearchMachinesPagedAsync((int?)null, 0, 50, Arg.Any<CancellationToken>())
            .Returns((new List<Machine> { machine }, 1));
        tenantRepo.ListTenantsByIdsAsync(Arg.Any<List<int>>(), Arg.Any<CancellationToken>())
            .Returns(new List<Tenant>());

        IServiceScopeFactory scopeFactory = CreateScopeFactoryWithServices(new Dictionary<Type, object>
        {
            { typeof(ITenantRepository), tenantRepo },
            { typeof(IMachineRepository), machineRepo }
        });

        FleetAdminService service = CreateFleetAdminService(scopeFactory);
        ServerCallContext context = CreateContext();

        ListMachinesResponse response = await service.ListMachines(new ListMachinesRequest(), context);

        await Assert.That(response.TotalCount).IsEqualTo(1);
        await Assert.That(response.Machines.Count).IsEqualTo(1);
        await Assert.That(response.Machines[0].Name).IsEqualTo("db-01");
    }

    /// <summary>
    /// ListMachines with a valid tenant external ID filters machines to that tenant.
    /// </summary>
    [Test]
    public async Task ListMachines_WithTenantFilter_FiltersByTenantId()
    {
        ITenantRepository tenantRepo = Substitute.For<ITenantRepository>();
        IMachineRepository machineRepo = Substitute.For<IMachineRepository>();

        Tenant tenant = MakeTenant();

        tenantRepo.GetTenantByExternalIdAsync(TenantExternalId, Arg.Any<CancellationToken>())
            .Returns(tenant);
        machineRepo.SearchMachinesPagedAsync(TenantInternalId, 0, 50, Arg.Any<CancellationToken>())
            .Returns((new List<Machine>(), 0));
        tenantRepo.ListTenantsByIdsAsync(Arg.Any<List<int>>(), Arg.Any<CancellationToken>())
            .Returns(new List<Tenant>());

        IServiceScopeFactory scopeFactory = CreateScopeFactoryWithServices(new Dictionary<Type, object>
        {
            { typeof(ITenantRepository), tenantRepo },
            { typeof(IMachineRepository), machineRepo }
        });

        FleetAdminService service = CreateFleetAdminService(scopeFactory);
        ServerCallContext context = CreateContext();

        ListMachinesResponse response = await service.ListMachines(
            new ListMachinesRequest { TenantExternalId = TenantExternalId }, context);

        await Assert.That(response.TotalCount).IsEqualTo(0);
        await machineRepo.Received(1).SearchMachinesPagedAsync(TenantInternalId, 0, 50, Arg.Any<CancellationToken>());
    }

    // ── ListAuditLogEntries ──

    /// <summary>
    /// ListAuditLogEntries returns entries with usernames and tenant names resolved.
    /// </summary>
    [Test]
    public async Task ListAuditLogEntries_ValidRequest_ReturnsMappedEntries()
    {
        ITenantRepository tenantRepo = Substitute.For<ITenantRepository>();
        IUserRepository userRepo = Substitute.For<IUserRepository>();
        IAuditLogRepository auditLogRepo = Substitute.For<IAuditLogRepository>();

        AuditLogEntry entry = new AuditLogEntry
        {
            Id = 1,
            TenantId = TenantInternalId,
            UserId = 5,
            Action = AuditAction.MachineRegistered,
            ResourceType = AuditResourceType.Machine,
            Details = "Machine registered",
            Timestamp = DateTimeOffset.UtcNow
        };

        auditLogRepo.QueryAuditLogEntriesAsync((int?)null, 0, 50, Arg.Any<CancellationToken>())
            .Returns((new List<AuditLogEntry> { entry }, 1));
        userRepo.GetUsersByIdsAsync(Arg.Any<List<int>>(), Arg.Any<CancellationToken>())
            .Returns(new List<UserAccount>());
        tenantRepo.ListTenantsByIdsAsync(Arg.Any<List<int>>(), Arg.Any<CancellationToken>())
            .Returns(new List<Tenant>());

        IServiceScopeFactory scopeFactory = CreateScopeFactoryWithServices(new Dictionary<Type, object>
        {
            { typeof(ITenantRepository), tenantRepo },
            { typeof(IUserRepository), userRepo },
            { typeof(IAuditLogRepository), auditLogRepo }
        });

        FleetAdminService service = CreateFleetAdminService(scopeFactory);
        ServerCallContext context = CreateContext();

        ListAuditLogEntriesResponse response = await service.ListAuditLogEntries(
            new ListAuditLogEntriesRequest(), context);

        await Assert.That(response.TotalCount).IsEqualTo(1);
        await Assert.That(response.Entries.Count).IsEqualTo(1);
        await Assert.That(response.Entries[0].Action).IsEqualTo("MachineRegistered");
    }

    /// <summary>
    /// ListAuditLogEntries with a tenant external ID filter resolves the tenant and filters entries.
    /// </summary>
    [Test]
    public async Task ListAuditLogEntries_WithTenantFilter_FiltersToTenant()
    {
        ITenantRepository tenantRepo = Substitute.For<ITenantRepository>();
        IUserRepository userRepo = Substitute.For<IUserRepository>();
        IAuditLogRepository auditLogRepo = Substitute.For<IAuditLogRepository>();

        Tenant tenant = MakeTenant();

        tenantRepo.GetTenantByExternalIdAsync(TenantExternalId, Arg.Any<CancellationToken>())
            .Returns(tenant);
        auditLogRepo.QueryAuditLogEntriesAsync(TenantInternalId, 0, 50, Arg.Any<CancellationToken>())
            .Returns((new List<AuditLogEntry>(), 0));
        userRepo.GetUsersByIdsAsync(Arg.Any<List<int>>(), Arg.Any<CancellationToken>())
            .Returns(new List<UserAccount>());
        tenantRepo.ListTenantsByIdsAsync(Arg.Any<List<int>>(), Arg.Any<CancellationToken>())
            .Returns(new List<Tenant>());

        IServiceScopeFactory scopeFactory = CreateScopeFactoryWithServices(new Dictionary<Type, object>
        {
            { typeof(ITenantRepository), tenantRepo },
            { typeof(IUserRepository), userRepo },
            { typeof(IAuditLogRepository), auditLogRepo }
        });

        FleetAdminService service = CreateFleetAdminService(scopeFactory);
        ServerCallContext context = CreateContext();

        ListAuditLogEntriesResponse response = await service.ListAuditLogEntries(
            new ListAuditLogEntriesRequest { TenantExternalId = TenantExternalId }, context);

        await Assert.That(response.TotalCount).IsEqualTo(0);
        await auditLogRepo.Received(1).QueryAuditLogEntriesAsync(TenantInternalId, 0, 50, Arg.Any<CancellationToken>());
    }

    // ── GetServerSettings ──

    /// <summary>
    /// GetServerSettings returns all settings with their bounds and descriptions.
    /// </summary>
    [Test]
    public async Task GetServerSettings_ValidRequest_ReturnsMappedSettings()
    {
        IServerConfigurationRepository configRepo = Substitute.For<IServerConfigurationRepository>();

        configRepo.ListAllSettingsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<ServerConfigurationSettings>
            {
                new ServerConfigurationSettings
                {
                    Key = ServerConfigurationSettingKeys.AgentHeartbeatSeconds,
                    Value = "300",
                    Version = 1
                }
            });

        IServiceScopeFactory scopeFactory = CreateScopeFactoryWithServices(new Dictionary<Type, object>
        {
            { typeof(IServerConfigurationRepository), configRepo }
        });

        FleetAdminService service = CreateFleetAdminService(scopeFactory);
        ServerCallContext context = CreateContext();

        GetServerSettingsResponse response = await service.GetServerSettings(
            new GetServerSettingsRequest(), context);

        await Assert.That(response.Settings.Count).IsEqualTo(1);
        await Assert.That(response.Settings[0].Value).IsEqualTo("300");
        await Assert.That(response.Settings[0].KeyName).IsEqualTo("AgentHeartbeatSeconds");
    }

    // ── UpdateServerSetting ──

    /// <summary>
    /// UpdateServerSetting with an invalid key throws InvalidArgument before touching the repository.
    /// </summary>
    [Test]
    public async Task UpdateServerSetting_InvalidKey_ThrowsInvalidArgument()
    {
        IServerConfigurationRepository configRepo = Substitute.For<IServerConfigurationRepository>();
        IServiceScopeFactory scopeFactory = CreateScopeFactoryWithServices(new Dictionary<Type, object>
        {
            { typeof(IServerConfigurationRepository), configRepo }
        });

        FleetAdminService service = CreateFleetAdminService(scopeFactory);
        ServerCallContext context = CreateContext();

        RpcException? exception = await Assert.ThrowsAsync<RpcException>(
            async () => await service.UpdateServerSetting(
                new UpdateServerSettingRequest { Key = (ServerSettingKey)9999, Value = "x" }, context));

        await Assert.That(exception).IsNotNull();
        await Assert.That(exception!.StatusCode).IsEqualTo(StatusCode.InvalidArgument);
        await configRepo.DidNotReceive().UpsertSettingAsync(
            Arg.Any<ServerConfigurationSettingKeys>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// UpdateServerSetting with key None (0) throws InvalidArgument since None is not a settable key.
    /// </summary>
    [Test]
    public async Task UpdateServerSetting_NoneKey_ThrowsInvalidArgument()
    {
        IServerConfigurationRepository configRepo = Substitute.For<IServerConfigurationRepository>();
        IServiceScopeFactory scopeFactory = CreateScopeFactoryWithServices(new Dictionary<Type, object>
        {
            { typeof(IServerConfigurationRepository), configRepo }
        });

        FleetAdminService service = CreateFleetAdminService(scopeFactory);
        ServerCallContext context = CreateContext();

        RpcException? exception = await Assert.ThrowsAsync<RpcException>(
            async () => await service.UpdateServerSetting(
                new UpdateServerSettingRequest { Key = 0, Value = "x" }, context));

        await Assert.That(exception).IsNotNull();
        await Assert.That(exception!.StatusCode).IsEqualTo(StatusCode.InvalidArgument);
    }

    /// <summary>
    /// UpdateServerSetting when no rows are updated returns a non-success response.
    /// </summary>
    [Test]
    public async Task UpdateServerSetting_ValidKey_UpsertsAndReturnsSuccess()
    {
        IServerConfigurationRepository configRepo = Substitute.For<IServerConfigurationRepository>();

        IServiceScopeFactory scopeFactory = CreateScopeFactoryWithServices(new Dictionary<Type, object>
        {
            { typeof(IServerConfigurationRepository), configRepo }
        });

        FleetAdminService service = CreateFleetAdminService(scopeFactory);
        ServerCallContext context = CreateContext();

        UpdateServerSettingResponse response = await service.UpdateServerSetting(
            new UpdateServerSettingRequest
            {
                Key = (ServerSettingKey)(int)ServerConfigurationSettingKeys.AgentHeartbeatSeconds,
                Value = "120"
            }, context);

        await Assert.That(response.Success).IsTrue();
        await Assert.That(response.Message).IsEqualTo("OK");

        // The write goes through the same upsert the REST admin path uses, so a valid key
        // persists even if its row is missing — there is no "setting not found" failure mode.
        await configRepo.Received(1).UpsertSettingAsync(
            ServerConfigurationSettingKeys.AgentHeartbeatSeconds, "120", Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// UpdateServerSetting rejects an out-of-range value with InvalidArgument and never writes it, so the
    /// gRPC path can no longer persist values the REST admin path would reject.
    /// </summary>
    [Test]
    public async Task UpdateServerSetting_OutOfRangeValue_ThrowsInvalidArgumentAndDoesNotWrite()
    {
        IServerConfigurationRepository configRepo = Substitute.For<IServerConfigurationRepository>();
        IServiceScopeFactory scopeFactory = CreateScopeFactoryWithServices(new Dictionary<Type, object>
        {
            { typeof(IServerConfigurationRepository), configRepo }
        });

        FleetAdminService service = CreateFleetAdminService(scopeFactory);
        ServerCallContext context = CreateContext();

        // AgentHeartbeatSeconds bounds are 10-600; 99999 is out of range.
        RpcException? exception = await Assert.ThrowsAsync<RpcException>(
            async () => await service.UpdateServerSetting(
                new UpdateServerSettingRequest
                {
                    Key = (ServerSettingKey)(int)ServerConfigurationSettingKeys.AgentHeartbeatSeconds,
                    Value = "99999"
                }, context));

        await Assert.That(exception!.StatusCode).IsEqualTo(StatusCode.InvalidArgument);
        await configRepo.DidNotReceive().UpsertSettingAsync(
            Arg.Any<ServerConfigurationSettingKeys>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ── UpdateTenantSubscription ──

    /// <summary>
    /// UpdateTenantSubscription with an invalid billing tier throws InvalidArgument.
    /// </summary>
    [Test]
    public async Task UpdateTenantSubscription_InvalidTier_ThrowsInvalidArgument()
    {
        IServiceScopeFactory scopeFactory = Substitute.For<IServiceScopeFactory>();
        FleetAdminService service = CreateFleetAdminService(scopeFactory);
        ServerCallContext context = CreateContext();

        RpcException? exception = await Assert.ThrowsAsync<RpcException>(
            async () => await service.UpdateTenantSubscription(
                new UpdateTenantSubscriptionRequest
                {
                    TenantExternalId = TenantExternalId,
                    Tier = BillingTier.Unspecified,
                    Status = "Active"
                }, context));

        await Assert.That(exception).IsNotNull();
        await Assert.That(exception!.StatusCode).IsEqualTo(StatusCode.InvalidArgument);
        await Assert.That(exception.Status.Detail).Contains("Invalid subscription tier");
    }

    /// <summary>
    /// UpdateTenantSubscription with an invalid status string throws InvalidArgument.
    /// </summary>
    [Test]
    public async Task UpdateTenantSubscription_InvalidStatus_ThrowsInvalidArgument()
    {
        IServiceScopeFactory scopeFactory = Substitute.For<IServiceScopeFactory>();
        FleetAdminService service = CreateFleetAdminService(scopeFactory);
        ServerCallContext context = CreateContext();

        RpcException? exception = await Assert.ThrowsAsync<RpcException>(
            async () => await service.UpdateTenantSubscription(
                new UpdateTenantSubscriptionRequest
                {
                    TenantExternalId = TenantExternalId,
                    Tier = BillingTier.Pro,
                    Status = "BOGUS_STATUS"
                }, context));

        await Assert.That(exception).IsNotNull();
        await Assert.That(exception!.StatusCode).IsEqualTo(StatusCode.InvalidArgument);
        await Assert.That(exception.Status.Detail).Contains("Invalid subscription status");
    }

    /// <summary>
    /// UpdateTenantSubscription when no rows are updated returns a non-success response.
    /// </summary>
    [Test]
    public async Task UpdateTenantSubscription_NoSubscriptionFound_ReturnsFailure()
    {
        ITenantRepository tenantRepo = Substitute.For<ITenantRepository>();
        ISubscriptionRepository subscriptionRepo = Substitute.For<ISubscriptionRepository>();

        Tenant tenant = MakeTenant();
        tenantRepo.GetTenantByExternalIdAsync(TenantExternalId, Arg.Any<CancellationToken>())
            .Returns(tenant);
        subscriptionRepo.UpdateSubscriptionStateAsync(TenantInternalId, Arg.Any<SubscriptionTier?>(), Arg.Any<SubscriptionStatus>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(0);

        IServiceScopeFactory scopeFactory = CreateScopeFactoryWithServices(new Dictionary<Type, object>
        {
            { typeof(ITenantRepository), tenantRepo },
            { typeof(ISubscriptionRepository), subscriptionRepo },
            {
                typeof(RetentionReclassifyDispatcher),
                new RetentionReclassifyDispatcher(
                    Substitute.For<IBackgroundJobClient>(),
                    NullLogger<RetentionReclassifyDispatcher>.Instance)
            },
        });

        FleetAdminService service = CreateFleetAdminService(scopeFactory);
        ServerCallContext context = CreateContext();

        UpdateTenantSubscriptionResponse response = await service.UpdateTenantSubscription(
            new UpdateTenantSubscriptionRequest
            {
                TenantExternalId = TenantExternalId,
                Tier = BillingTier.Pro,
                Status = "Active"
            }, context);

        await Assert.That(response.Success).IsFalse();
    }

    /// <summary>
    /// UpdateTenantSubscription with valid inputs and a found subscription returns success.
    /// </summary>
    [Test]
    public async Task UpdateTenantSubscription_ValidRequest_ReturnsSuccess()
    {
        ITenantRepository tenantRepo = Substitute.For<ITenantRepository>();
        ISubscriptionRepository subscriptionRepo = Substitute.For<ISubscriptionRepository>();

        Tenant tenant = MakeTenant();
        tenantRepo.GetTenantByExternalIdAsync(TenantExternalId, Arg.Any<CancellationToken>())
            .Returns(tenant);
        subscriptionRepo.UpdateSubscriptionStateAsync(TenantInternalId, SubscriptionTier.Pro, SubscriptionStatus.Active, false, Arg.Any<CancellationToken>())
            .Returns(1);

        IServiceScopeFactory scopeFactory = CreateScopeFactoryWithServices(new Dictionary<Type, object>
        {
            { typeof(ITenantRepository), tenantRepo },
            { typeof(ISubscriptionRepository), subscriptionRepo },
            {
                typeof(RetentionReclassifyDispatcher),
                new RetentionReclassifyDispatcher(
                    Substitute.For<IBackgroundJobClient>(),
                    NullLogger<RetentionReclassifyDispatcher>.Instance)
            },
        });

        FleetAdminService service = CreateFleetAdminService(scopeFactory);
        ServerCallContext context = CreateContext();

        UpdateTenantSubscriptionResponse response = await service.UpdateTenantSubscription(
            new UpdateTenantSubscriptionRequest
            {
                TenantExternalId = TenantExternalId,
                Tier = BillingTier.Pro,
                Status = "Active"
            }, context);

        await Assert.That(response.Success).IsTrue();
        await Assert.That(response.Message).IsEqualTo("OK");
    }

    // ── GetTenantOverride ──

    /// <summary>
    /// GetTenantOverride returns HasOverride=false when no override record exists.
    /// </summary>
    [Test]
    public async Task GetTenantOverride_NoOverrideExists_ReturnsHasOverrideFalse()
    {
        ITenantRepository tenantRepo = Substitute.For<ITenantRepository>();
        ITenantSubscriptionOverrideRepository overrideRepo = Substitute.For<ITenantSubscriptionOverrideRepository>();

        Tenant tenant = MakeTenant();
        tenantRepo.GetTenantByExternalIdAsync(TenantExternalId, Arg.Any<CancellationToken>())
            .Returns(tenant);
        overrideRepo.GetOverrideForTenantAsync(TenantInternalId, Arg.Any<CancellationToken>())
            .Returns((TenantSubscriptionOverride?)null);

        IServiceScopeFactory scopeFactory = CreateScopeFactoryWithServices(new Dictionary<Type, object>
        {
            { typeof(ITenantRepository), tenantRepo },
            { typeof(ITenantSubscriptionOverrideRepository), overrideRepo }
        });

        FleetAdminService service = CreateFleetAdminService(scopeFactory);
        ServerCallContext context = CreateContext();

        GetTenantOverrideResponse response = await service.GetTenantOverride(
            new GetTenantOverrideRequest { TenantExternalId = TenantExternalId }, context);

        await Assert.That(response.HasOverride).IsFalse();
        await Assert.That(response.MachineLimit).IsEqualTo(0);
    }

    /// <summary>
    /// GetTenantOverride returns HasOverride=true with values when an override record exists.
    /// </summary>
    [Test]
    public async Task GetTenantOverride_OverrideExists_ReturnsValues()
    {
        ITenantRepository tenantRepo = Substitute.For<ITenantRepository>();
        ITenantSubscriptionOverrideRepository overrideRepo = Substitute.For<ITenantSubscriptionOverrideRepository>();

        Tenant tenant = MakeTenant();
        TenantSubscriptionOverride overrideRecord = new TenantSubscriptionOverride
        {
            TenantId = TenantInternalId,
            MachineLimit = 25,
            RetentionDays = 30,
            AlertRuleLimit = 5,
            WebhookLimit = 2
        };

        tenantRepo.GetTenantByExternalIdAsync(TenantExternalId, Arg.Any<CancellationToken>())
            .Returns(tenant);
        overrideRepo.GetOverrideForTenantAsync(TenantInternalId, Arg.Any<CancellationToken>())
            .Returns(overrideRecord);

        IServiceScopeFactory scopeFactory = CreateScopeFactoryWithServices(new Dictionary<Type, object>
        {
            { typeof(ITenantRepository), tenantRepo },
            { typeof(ITenantSubscriptionOverrideRepository), overrideRepo }
        });

        FleetAdminService service = CreateFleetAdminService(scopeFactory);
        ServerCallContext context = CreateContext();

        GetTenantOverrideResponse response = await service.GetTenantOverride(
            new GetTenantOverrideRequest { TenantExternalId = TenantExternalId }, context);

        await Assert.That(response.HasOverride).IsTrue();
        await Assert.That(response.MachineLimit).IsEqualTo(25);
        await Assert.That(response.RetentionDays).IsEqualTo(30);
    }

    /// <summary>
    /// GetTenantOverride maps null field values to -1 to indicate "use tier default".
    /// </summary>
    [Test]
    public async Task GetTenantOverride_NullOverrideFields_MapsToNegativeOne()
    {
        ITenantRepository tenantRepo = Substitute.For<ITenantRepository>();
        ITenantSubscriptionOverrideRepository overrideRepo = Substitute.For<ITenantSubscriptionOverrideRepository>();

        Tenant tenant = MakeTenant();
        TenantSubscriptionOverride overrideRecord = new TenantSubscriptionOverride
        {
            TenantId = TenantInternalId,
            MachineLimit = null,
            RetentionDays = null,
            AlertRuleLimit = null,
            WebhookLimit = null
        };

        tenantRepo.GetTenantByExternalIdAsync(TenantExternalId, Arg.Any<CancellationToken>())
            .Returns(tenant);
        overrideRepo.GetOverrideForTenantAsync(TenantInternalId, Arg.Any<CancellationToken>())
            .Returns(overrideRecord);

        IServiceScopeFactory scopeFactory = CreateScopeFactoryWithServices(new Dictionary<Type, object>
        {
            { typeof(ITenantRepository), tenantRepo },
            { typeof(ITenantSubscriptionOverrideRepository), overrideRepo }
        });

        FleetAdminService service = CreateFleetAdminService(scopeFactory);
        ServerCallContext context = CreateContext();

        GetTenantOverrideResponse response = await service.GetTenantOverride(
            new GetTenantOverrideRequest { TenantExternalId = TenantExternalId }, context);

        await Assert.That(response.HasOverride).IsTrue();
        await Assert.That(response.MachineLimit).IsEqualTo(-1);
        await Assert.That(response.RetentionDays).IsEqualTo(-1);
    }

    // ── SetTenantOverride ──

    /// <summary>
    /// SetTenantOverride converts positive values directly and negative values to null for DB storage.
    /// </summary>
    [Test]
    public async Task SetTenantOverride_ValidValues_CallsUpsertWithCorrectNullMapping()
    {
        ITenantRepository tenantRepo = Substitute.For<ITenantRepository>();
        ITenantSubscriptionOverrideRepository overrideRepo = Substitute.For<ITenantSubscriptionOverrideRepository>();

        Tenant tenant = MakeTenant();
        tenantRepo.GetTenantByExternalIdAsync(TenantExternalId, Arg.Any<CancellationToken>())
            .Returns(tenant);
        overrideRepo.UpsertOverrideAsync(Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        IDatabaseTransaction tx = Substitute.For<IDatabaseTransaction>();
        tx.CommitAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        IDatabaseTransactionProvider txProvider = Substitute.For<IDatabaseTransactionProvider>();
        txProvider.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(tx));
        IAuditLogRepository auditLog = Substitute.For<IAuditLogRepository>();
        auditLog.InsertAuditLogAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        ISubscriptionRepository subscriptionRepo = Substitute.For<ISubscriptionRepository>();
        subscriptionRepo.InvalidateSubscriptionCacheAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        IBackgroundJobClient backgroundJobs = Substitute.For<IBackgroundJobClient>();
        RetentionReclassifyDispatcher reclassifyDispatcher = new(
            backgroundJobs, NullLogger<RetentionReclassifyDispatcher>.Instance);

        IServiceScopeFactory scopeFactory = CreateScopeFactoryWithServices(new Dictionary<Type, object>
        {
            { typeof(ITenantRepository), tenantRepo },
            { typeof(ITenantSubscriptionOverrideRepository), overrideRepo },
            { typeof(ISubscriptionRepository), subscriptionRepo },
            { typeof(IDatabaseTransactionProvider), txProvider },
            { typeof(IAuditLogRepository), auditLog },
            { typeof(RetentionReclassifyDispatcher), reclassifyDispatcher },
        });

        FleetAdminService service = CreateFleetAdminService(scopeFactory);
        ServerCallContext context = CreateContext();

        SetTenantOverrideResponse response = await service.SetTenantOverride(
            new SetTenantOverrideRequest
            {
                TenantExternalId = TenantExternalId,
                MachineLimit = 10,
                RetentionDays = -1,
                AlertRuleLimit = 5,
                WebhookLimit = -1
            }, context);

        await Assert.That(response.Success).IsTrue();
        await Assert.That(response.Message).IsEqualTo("OK");

        await overrideRepo.Received(1).UpsertOverrideAsync(
            TenantInternalId,
            10,
            (int?)null,
            5,
            (int?)null,
            Arg.Any<CancellationToken>());

        // The override changes effective retention, so the tenant's subscription cache must be
        // invalidated after commit or the change would lag by one cache TTL.
        await subscriptionRepo.Received(1).InvalidateSubscriptionCacheAsync(TenantInternalId, Arg.Any<CancellationToken>());

        // An override edit can change effective retention without touching the subscription row, so
        // this path enqueues the reclassify job itself rather than relying on the tier-change seam.
        backgroundJobs.Received(1).Create(
            Arg.Is<Job>(j => (j.Method.Name == nameof(RetentionReclassifyJob.RunAsync))
                && ((int)j.Args[0] == TenantInternalId)),
            Arg.Any<IState>());
    }

    // ── RemoveTenantOverride ──

    /// <summary>
    /// RemoveTenantOverride calls the remove method and returns success.
    /// </summary>
    [Test]
    public async Task RemoveTenantOverride_ValidTenant_CallsRemoveAndReturnsSuccess()
    {
        ITenantRepository tenantRepo = Substitute.For<ITenantRepository>();
        ITenantSubscriptionOverrideRepository overrideRepo = Substitute.For<ITenantSubscriptionOverrideRepository>();

        Tenant tenant = MakeTenant();
        tenantRepo.GetTenantByExternalIdAsync(TenantExternalId, Arg.Any<CancellationToken>())
            .Returns(tenant);
        overrideRepo.RemoveOverrideAsync(TenantInternalId, Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        IDatabaseTransaction tx = Substitute.For<IDatabaseTransaction>();
        tx.CommitAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        IDatabaseTransactionProvider txProvider = Substitute.For<IDatabaseTransactionProvider>();
        txProvider.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(tx));
        IAuditLogRepository auditLog = Substitute.For<IAuditLogRepository>();
        auditLog.InsertAuditLogAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        ISubscriptionRepository subscriptionRepo = Substitute.For<ISubscriptionRepository>();
        subscriptionRepo.InvalidateSubscriptionCacheAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        IBackgroundJobClient backgroundJobs = Substitute.For<IBackgroundJobClient>();
        RetentionReclassifyDispatcher reclassifyDispatcher = new(
            backgroundJobs, NullLogger<RetentionReclassifyDispatcher>.Instance);

        IServiceScopeFactory scopeFactory = CreateScopeFactoryWithServices(new Dictionary<Type, object>
        {
            { typeof(ITenantRepository), tenantRepo },
            { typeof(ITenantSubscriptionOverrideRepository), overrideRepo },
            { typeof(ISubscriptionRepository), subscriptionRepo },
            { typeof(IDatabaseTransactionProvider), txProvider },
            { typeof(IAuditLogRepository), auditLog },
            { typeof(RetentionReclassifyDispatcher), reclassifyDispatcher },
        });

        FleetAdminService service = CreateFleetAdminService(scopeFactory);
        ServerCallContext context = CreateContext();

        RemoveTenantOverrideResponse response = await service.RemoveTenantOverride(
            new RemoveTenantOverrideRequest { TenantExternalId = TenantExternalId }, context);

        await Assert.That(response.Success).IsTrue();
        await Assert.That(response.Message).IsEqualTo("OK");
        await overrideRepo.Received(1).RemoveOverrideAsync(TenantInternalId, Arg.Any<CancellationToken>());

        // Clearing the override reverts effective retention; the cache must be invalidated after commit.
        await subscriptionRepo.Received(1).InvalidateSubscriptionCacheAsync(TenantInternalId, Arg.Any<CancellationToken>());

        // Reverting to the tier default can move the tenant into a different class, so the surviving
        // rows are reclassified here too.
        backgroundJobs.Received(1).Create(
            Arg.Is<Job>(j => (j.Method.Name == nameof(RetentionReclassifyJob.RunAsync))
                && ((int)j.Args[0] == TenantInternalId)),
            Arg.Any<IState>());
    }

    // ── ConfigureTenantOidc ──

    /// <summary>
    /// ConfigureTenantOidc creates a new OIDC record when none exists for the tenant.
    /// </summary>
    [Test]
    public async Task ConfigureTenantOidc_NoExistingConfig_InsertsNewConfig()
    {
        ITenantRepository tenantRepo = Substitute.For<ITenantRepository>();

        Tenant tenant = MakeTenant();
        tenantRepo.GetTenantByExternalIdAsync(TenantExternalId, Arg.Any<CancellationToken>())
            .Returns(tenant);
        tenantRepo.GetTenantOidcConfigByTenantIdAsync(TenantInternalId, Arg.Any<CancellationToken>())
            .Returns((TenantOidcConfiguration?)null);
        tenantRepo.InsertTenantOidcConfigAsync(Arg.Any<TenantOidcConfiguration>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        IServiceScopeFactory scopeFactory = CreateScopeFactoryWithServices(new Dictionary<Type, object>
        {
            { typeof(ITenantRepository), tenantRepo }
        });

        FleetAdminService service = CreateFleetAdminService(scopeFactory);
        ServerCallContext context = CreateContext();

        ConfigureTenantOidcResponse response = await service.ConfigureTenantOidc(
            new ConfigureTenantOidcRequest
            {
                TenantExternalId = TenantExternalId,
                Authority = "https://idp.example.com",
                ClientId = "client-id",
                ClientSecret = "secret",
                MetadataAddress = string.Empty,
                EmailDomain = "example.com",
                IsEnabled = true
            }, context);

        await Assert.That(response.Success).IsTrue();
        await tenantRepo.Received(1).InsertTenantOidcConfigAsync(
            Arg.Is<TenantOidcConfiguration>(c =>
                c.TenantId == TenantInternalId &&
                c.Authority == "https://idp.example.com" &&
                c.EmailDomain == "example.com" &&
                c.ClientSecret != "secret" &&
                c.ClientSecret.StartsWith("prot1:", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
        await tenantRepo.DidNotReceive().UpdateTenantOidcConfigAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// ConfigureTenantOidc updates the existing record when one already exists for the tenant.
    /// </summary>
    [Test]
    public async Task ConfigureTenantOidc_ExistingConfig_UpdatesConfig()
    {
        ITenantRepository tenantRepo = Substitute.For<ITenantRepository>();

        Tenant tenant = MakeTenant();
        TenantOidcConfiguration existing = new TenantOidcConfiguration
        {
            TenantId = TenantInternalId,
            Authority = "https://old-idp.example.com",
            ClientId = "old-client",
            ClientSecret = "old-secret",
            MetadataAddress = null,
            EmailDomain = "old.example.com",
            IsEnabled = false,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        tenantRepo.GetTenantByExternalIdAsync(TenantExternalId, Arg.Any<CancellationToken>())
            .Returns(tenant);
        tenantRepo.GetTenantOidcConfigByTenantIdAsync(TenantInternalId, Arg.Any<CancellationToken>())
            .Returns(existing);
        tenantRepo.UpdateTenantOidcConfigAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        IServiceScopeFactory scopeFactory = CreateScopeFactoryWithServices(new Dictionary<Type, object>
        {
            { typeof(ITenantRepository), tenantRepo }
        });

        FleetAdminService service = CreateFleetAdminService(scopeFactory);
        ServerCallContext context = CreateContext();

        ConfigureTenantOidcResponse response = await service.ConfigureTenantOidc(
            new ConfigureTenantOidcRequest
            {
                TenantExternalId = TenantExternalId,
                Authority = "https://new-idp.example.com",
                ClientId = "new-client",
                ClientSecret = "new-secret",
                MetadataAddress = "https://new-idp.example.com/.well-known/openid-configuration",
                EmailDomain = "new.example.com",
                IsEnabled = true
            }, context);

        await Assert.That(response.Success).IsTrue();
        await tenantRepo.Received(1).UpdateTenantOidcConfigAsync(
            TenantInternalId,
            "https://new-idp.example.com",
            "new-client",
            Arg.Is<string>(s => s.StartsWith("prot1:", StringComparison.Ordinal) && (s != "new-secret")),
            "https://new-idp.example.com/.well-known/openid-configuration",
            "new.example.com",
            true,
            Arg.Any<CancellationToken>());
        await tenantRepo.DidNotReceive().InsertTenantOidcConfigAsync(
            Arg.Any<TenantOidcConfiguration>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// ConfigureTenantOidc converts an empty MetadataAddress to null before persisting.
    /// </summary>
    [Test]
    public async Task ConfigureTenantOidc_EmptyMetadataAddress_StoresNull()
    {
        ITenantRepository tenantRepo = Substitute.For<ITenantRepository>();

        Tenant tenant = MakeTenant();
        TenantOidcConfiguration existing = new TenantOidcConfiguration
        {
            TenantId = TenantInternalId,
            Authority = "https://idp.example.com",
            ClientId = "c",
            ClientSecret = "s",
            MetadataAddress = null,
            EmailDomain = "e.com",
            IsEnabled = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        tenantRepo.GetTenantByExternalIdAsync(TenantExternalId, Arg.Any<CancellationToken>())
            .Returns(tenant);
        tenantRepo.GetTenantOidcConfigByTenantIdAsync(TenantInternalId, Arg.Any<CancellationToken>())
            .Returns(existing);
        tenantRepo.UpdateTenantOidcConfigAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        IServiceScopeFactory scopeFactory = CreateScopeFactoryWithServices(new Dictionary<Type, object>
        {
            { typeof(ITenantRepository), tenantRepo }
        });

        FleetAdminService service = CreateFleetAdminService(scopeFactory);
        ServerCallContext context = CreateContext();

        await service.ConfigureTenantOidc(
            new ConfigureTenantOidcRequest
            {
                TenantExternalId = TenantExternalId,
                Authority = "https://idp.example.com",
                ClientId = "c",
                ClientSecret = "s",
                MetadataAddress = "   ",
                EmailDomain = "e.com",
                IsEnabled = true
            }, context);

        await tenantRepo.Received(1).UpdateTenantOidcConfigAsync(
            TenantInternalId,
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Is<string?>(v => v == null),
            Arg.Any<string>(),
            Arg.Any<bool>(),
            Arg.Any<CancellationToken>());
    }

    // ── RequestTenantDeletion / RestoreTenant / ListTenantDeletions ──

    /// <summary>
    /// Builds a real <see cref="TenantDeletionHandler"/> backed by substituted dependencies. The
    /// handler is sealed, so it cannot itself be substituted — mirroring the existing
    /// <c>RetentionReclassifyDispatcher</c> real-instance pattern used elsewhere in this file lets
    /// these tests verify the RPC layer's request/response mapping without re-testing the handler's
    /// own logic, which is covered by <c>TenantDeletionHandlerTests</c>.
    /// </summary>
    private static TenantDeletionHandler CreateTenantDeletionHandler(
        ITenantRepository tenantRepo,
        ITenantDeletionRepository deletionRepo,
        DateTimeOffset now)
    {
        IAuditLogRepository auditLog = Substitute.For<IAuditLogRepository>();
        IDatabaseTransaction transaction = Substitute.For<IDatabaseTransaction>();
        IDatabaseTransactionProvider transactionProvider = Substitute.For<IDatabaseTransactionProvider>();
        transactionProvider.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(transaction);
        IBillingApiClient billingApiClient = Substitute.For<IBillingApiClient>();
        billingApiClient.CancelSubscriptionImmediateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);
        FakeTimeProvider timeProvider = new(now);

        return new TenantDeletionHandler(
            tenantRepo, deletionRepo, auditLog, transactionProvider, billingApiClient,
            Substitute.For<IRoleCacheInvalidator>(), timeProvider,
            Substitute.For<ILogger<TenantDeletionHandler>>());
    }

    /// <summary>
    /// RequestTenantDeletion resolves the tenant despite it not being pre-deactivated, delegates to the
    /// handler, and maps a successful result's ScheduledPurgeAt onto the response.
    /// </summary>
    [Test]
    public async Task RequestTenantDeletion_ValidRequest_ReturnsSuccessWithScheduledPurgeAt()
    {
        DateTimeOffset now = new(2026, 07, 23, 12, 0, 0, TimeSpan.Zero);
        Tenant tenant = MakeTenant();

        ITenantRepository tenantRepo = Substitute.For<ITenantRepository>();
        tenantRepo.GetTenantByExternalIdIncludingInactiveAsync(TenantExternalId, Arg.Any<CancellationToken>())
            .Returns(tenant);
        tenantRepo.GetTenantByIdAsync(TenantInternalId, Arg.Any<CancellationToken>()).Returns(tenant);

        ITenantDeletionRepository deletionRepo = Substitute.For<ITenantDeletionRepository>();
        deletionRepo.GetActiveDeletionForTenantAsync(TenantInternalId, Arg.Any<CancellationToken>())
            .Returns((TenantDeletion?)null);
        deletionRepo.InsertDeletionAsync(Arg.Any<TenantDeletion>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => callInfo.Arg<TenantDeletion>());

        TenantDeletionHandler handler = CreateTenantDeletionHandler(tenantRepo, deletionRepo, now);

        IServiceScopeFactory scopeFactory = CreateScopeFactoryWithServices(new Dictionary<Type, object>
        {
            { typeof(ITenantRepository), tenantRepo },
            { typeof(TenantDeletionHandler), handler }
        });

        FleetAdminService service = CreateFleetAdminService(scopeFactory);
        ServerCallContext context = CreateContext();

        RequestTenantDeletionResponse response = await service.RequestTenantDeletion(
            new RequestTenantDeletionRequest
            {
                TenantExternalId = TenantExternalId,
                RequestedByUserId = 3,
                Reason = "test reason"
            }, context);

        await Assert.That(response.Success).IsTrue();
        await Assert.That(response.Message).IsEqualTo("OK");
        await Assert.That(response.ScheduledPurgeAt.ToDateTimeOffset()).IsEqualTo(now.AddDays(30));
    }

    /// <summary>
    /// RequestTenantDeletion for an unknown external ID throws NotFound before reaching the handler.
    /// </summary>
    [Test]
    public async Task RequestTenantDeletion_UnknownTenant_ThrowsNotFound()
    {
        ITenantRepository tenantRepo = Substitute.For<ITenantRepository>();
        tenantRepo.GetTenantByExternalIdIncludingInactiveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((Tenant?)null);

        // The handler is resolved from the scope before the tenant lookup runs, so it must be
        // registered even though this test never reaches it.
        TenantDeletionHandler handler = CreateTenantDeletionHandler(
            tenantRepo, Substitute.For<ITenantDeletionRepository>(), DateTimeOffset.UtcNow);

        IServiceScopeFactory scopeFactory = CreateScopeFactoryWithServices(new Dictionary<Type, object>
        {
            { typeof(ITenantRepository), tenantRepo },
            { typeof(TenantDeletionHandler), handler }
        });

        FleetAdminService service = CreateFleetAdminService(scopeFactory);
        ServerCallContext context = CreateContext();

        RpcException? exception = await Assert.ThrowsAsync<RpcException>(
            async () => await service.RequestTenantDeletion(
                new RequestTenantDeletionRequest { TenantExternalId = "unknown", RequestedByUserId = 1 },
                context));

        await Assert.That(exception).IsNotNull();
        await Assert.That(exception!.StatusCode).IsEqualTo(StatusCode.NotFound);
    }

    /// <summary>
    /// RestoreTenant resolves a deactivated tenant (via the including-inactive lookup) and maps the
    /// handler's successful result onto the response.
    /// </summary>
    [Test]
    public async Task RestoreTenant_ValidRequest_ReturnsSuccess()
    {
        Tenant tenant = MakeTenant();
        tenant.IsActive = false;

        ITenantRepository tenantRepo = Substitute.For<ITenantRepository>();
        tenantRepo.GetTenantByExternalIdIncludingInactiveAsync(TenantExternalId, Arg.Any<CancellationToken>())
            .Returns(tenant);

        TenantDeletion pendingDeletion = new TenantDeletion
        {
            Id = 5,
            TenantId = TenantInternalId,
            TenantExternalId = TenantExternalId,
            TenantName = tenant.Name,
            RequestedByUserId = 3,
            RequestedAt = DateTimeOffset.UtcNow,
            ScheduledPurgeAt = DateTimeOffset.UtcNow.AddDays(30),
            Status = TenantDeletionStatus.Deactivated
        };

        ITenantDeletionRepository deletionRepo = Substitute.For<ITenantDeletionRepository>();
        deletionRepo.GetActiveDeletionForTenantAsync(TenantInternalId, Arg.Any<CancellationToken>())
            .Returns(pendingDeletion);

        TenantDeletionHandler handler = CreateTenantDeletionHandler(tenantRepo, deletionRepo, DateTimeOffset.UtcNow);

        IServiceScopeFactory scopeFactory = CreateScopeFactoryWithServices(new Dictionary<Type, object>
        {
            { typeof(ITenantRepository), tenantRepo },
            { typeof(TenantDeletionHandler), handler }
        });

        FleetAdminService service = CreateFleetAdminService(scopeFactory);
        ServerCallContext context = CreateContext();

        RestoreTenantResponse response = await service.RestoreTenant(
            new RestoreTenantRequest { TenantExternalId = TenantExternalId, RequestedByUserId = 3 }, context);

        await Assert.That(response.Success).IsTrue();
        await Assert.That(response.Message).IsEqualTo("OK");
        await deletionRepo.Received(1).SetTenantActiveAsync(TenantInternalId, true, null, null, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// RestoreTenant for an unknown external ID throws NotFound before reaching the handler.
    /// </summary>
    [Test]
    public async Task RestoreTenant_UnknownTenant_ThrowsNotFound()
    {
        ITenantRepository tenantRepo = Substitute.For<ITenantRepository>();
        tenantRepo.GetTenantByExternalIdIncludingInactiveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((Tenant?)null);

        // The handler is resolved from the scope before the tenant lookup runs, so it must be
        // registered even though this test never reaches it.
        TenantDeletionHandler handler = CreateTenantDeletionHandler(
            tenantRepo, Substitute.For<ITenantDeletionRepository>(), DateTimeOffset.UtcNow);

        IServiceScopeFactory scopeFactory = CreateScopeFactoryWithServices(new Dictionary<Type, object>
        {
            { typeof(ITenantRepository), tenantRepo },
            { typeof(TenantDeletionHandler), handler }
        });

        FleetAdminService service = CreateFleetAdminService(scopeFactory);
        ServerCallContext context = CreateContext();

        RpcException? exception = await Assert.ThrowsAsync<RpcException>(
            async () => await service.RestoreTenant(
                new RestoreTenantRequest { TenantExternalId = "unknown", RequestedByUserId = 1 },
                context));

        await Assert.That(exception).IsNotNull();
        await Assert.That(exception!.StatusCode).IsEqualTo(StatusCode.NotFound);
    }

    /// <summary>
    /// ListTenantDeletions maps every field, including the optional PurgedAt (set) and Reason
    /// (defaulted to empty string when null), and passes through the total count.
    /// </summary>
    [Test]
    public async Task ListTenantDeletions_MapsAllFieldsIncludingOptionalPurgedAt()
    {
        DateTimeOffset requestedAt = new(2026, 06, 01, 0, 0, 0, TimeSpan.Zero);
        DateTimeOffset scheduledPurgeAt = requestedAt.AddDays(30);
        DateTimeOffset purgedAt = scheduledPurgeAt.AddMinutes(5);

        TenantDeletion purged = new TenantDeletion
        {
            Id = 1,
            TenantId = TenantInternalId,
            TenantExternalId = TenantExternalId,
            TenantName = "Test Corp",
            RequestedByUserId = 3,
            RequestedAt = requestedAt,
            ScheduledPurgeAt = scheduledPurgeAt,
            Status = TenantDeletionStatus.Purged,
            PurgedAt = purgedAt,
            Reason = null
        };

        ITenantDeletionRepository deletionRepo = Substitute.For<ITenantDeletionRepository>();
        deletionRepo.ListDeletionsAsync(true, 0, 50, Arg.Any<CancellationToken>())
            .Returns((new List<TenantDeletion> { purged }, 1));

        IServiceScopeFactory scopeFactory = CreateScopeFactoryWithServices(new Dictionary<Type, object>
        {
            { typeof(ITenantDeletionRepository), deletionRepo }
        });

        FleetAdminService service = CreateFleetAdminService(scopeFactory);
        ServerCallContext context = CreateContext();

        ListTenantDeletionsResponse response = await service.ListTenantDeletions(
            new ListTenantDeletionsRequest { IncludeCompleted = true, Page = 1, PageSize = 50 }, context);

        await Assert.That(response.TotalCount).IsEqualTo(1);
        await Assert.That(response.Deletions.Count).IsEqualTo(1);

        TenantDeletionRecord record = response.Deletions[0];
        await Assert.That(record.Id).IsEqualTo(1);
        await Assert.That(record.TenantId).IsEqualTo(TenantInternalId);
        await Assert.That(record.TenantExternalId).IsEqualTo(TenantExternalId);
        await Assert.That(record.TenantName).IsEqualTo("Test Corp");
        await Assert.That(record.RequestedByUserId).IsEqualTo(3);
        await Assert.That(record.Status).IsEqualTo((int)TenantDeletionStatus.Purged);
        await Assert.That(record.Reason).IsEqualTo(string.Empty);
        await Assert.That(record.PurgedAt.ToDateTimeOffset()).IsEqualTo(purgedAt);
    }

    /// <summary>
    /// ListTenantDeletions passes IncludeCompleted through to the repository and sanitizes pagination
    /// the same way the other list RPCs do.
    /// </summary>
    [Test]
    public async Task ListTenantDeletions_ExcludeCompleted_PassesFlagAndPaginationThrough()
    {
        ITenantDeletionRepository deletionRepo = Substitute.For<ITenantDeletionRepository>();
        deletionRepo.ListDeletionsAsync(false, 20, 10, Arg.Any<CancellationToken>())
            .Returns((new List<TenantDeletion>(), 0));

        IServiceScopeFactory scopeFactory = CreateScopeFactoryWithServices(new Dictionary<Type, object>
        {
            { typeof(ITenantDeletionRepository), deletionRepo }
        });

        FleetAdminService service = CreateFleetAdminService(scopeFactory);
        ServerCallContext context = CreateContext();

        ListTenantDeletionsResponse response = await service.ListTenantDeletions(
            new ListTenantDeletionsRequest { IncludeCompleted = false, Page = 3, PageSize = 10 }, context);

        await Assert.That(response.TotalCount).IsEqualTo(0);
        await deletionRepo.Received(1).ListDeletionsAsync(false, 20, 10, Arg.Any<CancellationToken>());
    }

    // ── GetBillableMachineCount ──

    /// <summary>
    /// GetBillableMachineCount returns the billable count for the target tier, applying that
    /// tier's floor rather than the tenant's current tier.
    /// </summary>
    [Test]
    [Arguments("pro", 0, 1)]
    [Arguments("team", 1, 3)]
    [Arguments("team", 8, 8)]
    public async Task GetBillableMachineCount_AppliesTargetTierFloor(
        string targetTier, int active, int expected)
    {
        ITenantRepository tenantRepo = Substitute.For<ITenantRepository>();
        tenantRepo.GetTenantByExternalIdAsync(TenantExternalId, Arg.Any<CancellationToken>())
            .Returns(MakeTenant());

        ISubscriptionService subscriptions = Substitute.For<ISubscriptionService>();
        subscriptions.GetBillableMachineCountAsync(TenantInternalId, Arg.Any<SubscriptionTier>(), Arg.Any<CancellationToken>())
            .Returns(Math.Max(active, targetTier == "team" ? 3 : 1));

        IServiceScopeFactory scopeFactory = CreateScopeFactoryWithServices(new Dictionary<Type, object>
        {
            { typeof(ITenantRepository), tenantRepo },
            { typeof(ISubscriptionService), subscriptions }
        });

        FleetAdminService service = CreateFleetAdminService(scopeFactory);
        ServerCallContext context = CreateContext();

        GetBillableMachineCountResponse response = await service.GetBillableMachineCount(
            new GetBillableMachineCountRequest { TenantExternalId = TenantExternalId, TargetTier = targetTier },
            context);

        await Assert.That(response.Success).IsTrue();
        await Assert.That(response.BillableCount).IsEqualTo(expected);
    }

    /// <summary>
    /// GetBillableMachineCount fails gracefully when the tenant cannot be found.
    /// </summary>
    [Test]
    public async Task GetBillableMachineCount_UnknownTenant_ReturnsFailure()
    {
        ITenantRepository tenantRepo = Substitute.For<ITenantRepository>();
        tenantRepo.GetTenantByExternalIdAsync("missing", Arg.Any<CancellationToken>())
            .Returns((Tenant?)null);

        IServiceScopeFactory scopeFactory = CreateScopeFactoryWithServices(new Dictionary<Type, object>
        {
            { typeof(ITenantRepository), tenantRepo },
            { typeof(ISubscriptionService), Substitute.For<ISubscriptionService>() }
        });

        FleetAdminService service = CreateFleetAdminService(scopeFactory);
        ServerCallContext context = CreateContext();

        GetBillableMachineCountResponse response = await service.GetBillableMachineCount(
            new GetBillableMachineCountRequest { TenantExternalId = "missing", TargetTier = "pro" },
            context);

        await Assert.That(response.Success).IsFalse();
    }

    /// <summary>
    /// GetBillableMachineCount rejects any target tier that is not a real, billable tier — an
    /// unparseable string, a numeric string that happens to match an enum value that is not
    /// billable, an out-of-range numeric string, "none", and "free" (Free has no Stripe
    /// subscription to size). Without this guard, <c>Enum.TryParse</c> alone accepts all of
    /// these and the floor computation silently falls through to a billable count of 0 — a paid
    /// tenant would be billed nothing while keeping paid features.
    /// </summary>
    [Test]
    [Arguments("none")]
    [Arguments("0")]
    [Arguments("7")]
    [Arguments("garbage")]
    [Arguments("free")]
    public async Task GetBillableMachineCount_NonBillableTargetTier_ReturnsFailureWithoutZeroCount(string targetTier)
    {
        ITenantRepository tenantRepo = Substitute.For<ITenantRepository>();
        tenantRepo.GetTenantByExternalIdAsync(TenantExternalId, Arg.Any<CancellationToken>())
            .Returns(MakeTenant());

        ISubscriptionService subscriptions = Substitute.For<ISubscriptionService>();

        IServiceScopeFactory scopeFactory = CreateScopeFactoryWithServices(new Dictionary<Type, object>
        {
            { typeof(ITenantRepository), tenantRepo },
            { typeof(ISubscriptionService), subscriptions }
        });

        FleetAdminService service = CreateFleetAdminService(scopeFactory);
        ServerCallContext context = CreateContext();

        GetBillableMachineCountResponse response = await service.GetBillableMachineCount(
            new GetBillableMachineCountRequest { TenantExternalId = TenantExternalId, TargetTier = targetTier },
            context);

        await Assert.That(response.Success).IsFalse();
        await Assert.That(response.BillableCount).IsEqualTo(0);
        await subscriptions.DidNotReceive().GetBillableMachineCountAsync(
            Arg.Any<int>(), Arg.Any<SubscriptionTier>(), Arg.Any<CancellationToken>());
    }

    // ── MapBillingTierToSubscriptionTier ──

    /// <summary>
    /// MapBillingTierToSubscriptionTier returns null for Unspecified billing tier.
    /// </summary>
    [Test]
    public async Task MapBillingTierToSubscriptionTier_Unspecified_ReturnsNull()
    {
        SubscriptionTier? result = FleetAdminService.MapBillingTierToSubscriptionTier(BillingTier.Unspecified);

        await Assert.That(result).IsNull();
    }

    /// <summary>
    /// MapSubscriptionTierToBillingTier returns Unspecified for an unknown tier value.
    /// </summary>
    [Test]
    public async Task MapSubscriptionTierToBillingTier_UnknownTier_ReturnsUnspecified()
    {
        BillingTier result = FleetAdminService.MapSubscriptionTierToBillingTier((SubscriptionTier)999);

        await Assert.That(result).IsEqualTo(BillingTier.Unspecified);
    }
}
