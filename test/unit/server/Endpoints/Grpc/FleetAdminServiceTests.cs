// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Server.Endpoints.Grpc;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Framlux.FleetManagement.Services.Core.Options;
using Framlux.FleetManagement.Services.Core.Security;
using Framlux.Vord.BillingGrpc;
using Grpc.Core;
using Grpc.Core.Testing;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

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

    private const string ValidInternalKey = "test-fleet-admin-key";
    private const string TenantExternalId = "ext-tenant-001";
    private const int TenantInternalId = 10;

    private static FleetAdminService CreateFleetAdminService(
        IServiceScopeFactory scopeFactory,
        string configuredKey = ValidInternalKey,
        IOidcSecretProtector? oidcSecretProtector = null)
    {
        InternalApiOptions options = new InternalApiOptions { Key = configuredKey };
        IOptions<InternalApiOptions> wrappedOptions = Options.Create(options);
        ILogger<FleetAdminService> logger = Substitute.For<ILogger<FleetAdminService>>();
        IOidcSecretProtector resolvedProtector = oidcSecretProtector
            ?? new OidcSecretProtector(new EphemeralDataProtectionProvider());

        return new FleetAdminService(scopeFactory, wrappedOptions, resolvedProtector, logger);
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

    private static ServerCallContext CreateContext(string? apiKey = ValidInternalKey)
    {
        Metadata headers = new Metadata();
        if (apiKey is not null)
        {
            headers.Add("x-internal-key", apiKey);
        }

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

    // ── ValidateInternalKey: empty configured key ──

    /// <summary>
    /// When the configured key is empty, all RPC methods must throw Unavailable before touching any repository.
    /// </summary>
    [Test]
    public async Task ListUsers_EmptyConfiguredKey_ThrowsUnavailable()
    {
        IServiceScopeFactory scopeFactory = Substitute.For<IServiceScopeFactory>();
        FleetAdminService service = CreateFleetAdminService(scopeFactory, configuredKey: string.Empty);
        ServerCallContext context = CreateContext(apiKey: ValidInternalKey);

        RpcException? exception = await Assert.ThrowsAsync<RpcException>(
            async () => await service.ListUsers(new ListUsersRequest(), context));

        await Assert.That(exception).IsNotNull();
        await Assert.That(exception!.StatusCode).IsEqualTo(StatusCode.Unavailable);
    }

    /// <summary>
    /// When an incorrect key is supplied, all RPC methods must throw Unauthenticated.
    /// </summary>
    [Test]
    public async Task ListUsers_WrongKey_ThrowsUnauthenticated()
    {
        IServiceScopeFactory scopeFactory = Substitute.For<IServiceScopeFactory>();
        FleetAdminService service = CreateFleetAdminService(scopeFactory);
        ServerCallContext context = CreateContext(apiKey: "wrong-key");

        RpcException? exception = await Assert.ThrowsAsync<RpcException>(
            async () => await service.ListUsers(new ListUsersRequest(), context));

        await Assert.That(exception).IsNotNull();
        await Assert.That(exception!.StatusCode).IsEqualTo(StatusCode.Unauthenticated);
    }

    // ── ListUsers ──

    /// <summary>
    /// ListUsers returns users and their tenant roles when the authenticated key is valid.
    /// </summary>
    [Test]
    public async Task ListUsers_ValidKey_ReturnsMappedUsers()
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
        tenantRepo.GetActiveRolesForTenantAsync(TenantInternalId, Arg.Any<CancellationToken>())
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
            UpdatedAt = DateTimeOffset.UtcNow
        };

        tenantRepo.GetTenantByExternalIdAsync(TenantExternalId, Arg.Any<CancellationToken>())
            .Returns(tenant);
        tenantRepo.GetActiveRolesForTenantAsync(TenantInternalId, Arg.Any<CancellationToken>())
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

        configRepo.GetAllSettingsAsync(Arg.Any<CancellationToken>())
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
                new UpdateServerSettingRequest { Key = 9999, Value = "x" }, context));

        await Assert.That(exception).IsNotNull();
        await Assert.That(exception!.StatusCode).IsEqualTo(StatusCode.InvalidArgument);
        await configRepo.DidNotReceive().UpdateSettingAsync(
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
    public async Task UpdateServerSetting_KeyNotFound_ReturnsFailure()
    {
        IServerConfigurationRepository configRepo = Substitute.For<IServerConfigurationRepository>();
        configRepo.UpdateSettingAsync(Arg.Any<ServerConfigurationSettingKeys>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(0);

        IServiceScopeFactory scopeFactory = CreateScopeFactoryWithServices(new Dictionary<Type, object>
        {
            { typeof(IServerConfigurationRepository), configRepo }
        });

        FleetAdminService service = CreateFleetAdminService(scopeFactory);
        ServerCallContext context = CreateContext();

        UpdateServerSettingResponse response = await service.UpdateServerSetting(
            new UpdateServerSettingRequest
            {
                Key = (int)ServerConfigurationSettingKeys.AgentHeartbeatSeconds,
                Value = "60"
            }, context);

        await Assert.That(response.Success).IsFalse();
        await Assert.That(response.Message).Contains("not found");
    }

    /// <summary>
    /// UpdateServerSetting when a row is updated returns a success response.
    /// </summary>
    [Test]
    public async Task UpdateServerSetting_ValidKey_ReturnsSuccess()
    {
        IServerConfigurationRepository configRepo = Substitute.For<IServerConfigurationRepository>();
        configRepo.UpdateSettingAsync(Arg.Any<ServerConfigurationSettingKeys>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(1);

        IServiceScopeFactory scopeFactory = CreateScopeFactoryWithServices(new Dictionary<Type, object>
        {
            { typeof(IServerConfigurationRepository), configRepo }
        });

        FleetAdminService service = CreateFleetAdminService(scopeFactory);
        ServerCallContext context = CreateContext();

        UpdateServerSettingResponse response = await service.UpdateServerSetting(
            new UpdateServerSettingRequest
            {
                Key = (int)ServerConfigurationSettingKeys.AgentHeartbeatSeconds,
                Value = "120"
            }, context);

        await Assert.That(response.Success).IsTrue();
        await Assert.That(response.Message).IsEqualTo("OK");
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
        subscriptionRepo.UpdateSubscriptionAdminAsync(TenantInternalId, Arg.Any<SubscriptionTier>(), Arg.Any<SubscriptionStatus>(), Arg.Any<CancellationToken>())
            .Returns(0);

        IServiceScopeFactory scopeFactory = CreateScopeFactoryWithServices(new Dictionary<Type, object>
        {
            { typeof(ITenantRepository), tenantRepo },
            { typeof(ISubscriptionRepository), subscriptionRepo }
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
        subscriptionRepo.UpdateSubscriptionAdminAsync(TenantInternalId, SubscriptionTier.Pro, SubscriptionStatus.Active, Arg.Any<CancellationToken>())
            .Returns(1);

        IServiceScopeFactory scopeFactory = CreateScopeFactoryWithServices(new Dictionary<Type, object>
        {
            { typeof(ITenantRepository), tenantRepo },
            { typeof(ISubscriptionRepository), subscriptionRepo }
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

        IServiceScopeFactory scopeFactory = CreateScopeFactoryWithServices(new Dictionary<Type, object>
        {
            { typeof(ITenantRepository), tenantRepo },
            { typeof(ITenantSubscriptionOverrideRepository), overrideRepo },
            { typeof(IDatabaseTransactionProvider), txProvider },
            { typeof(IAuditLogRepository), auditLog },
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

        IServiceScopeFactory scopeFactory = CreateScopeFactoryWithServices(new Dictionary<Type, object>
        {
            { typeof(ITenantRepository), tenantRepo },
            { typeof(ITenantSubscriptionOverrideRepository), overrideRepo },
            { typeof(IDatabaseTransactionProvider), txProvider },
            { typeof(IAuditLogRepository), auditLog },
        });

        FleetAdminService service = CreateFleetAdminService(scopeFactory);
        ServerCallContext context = CreateContext();

        RemoveTenantOverrideResponse response = await service.RemoveTenantOverride(
            new RemoveTenantOverrideRequest { TenantExternalId = TenantExternalId }, context);

        await Assert.That(response.Success).IsTrue();
        await Assert.That(response.Message).IsEqualTo("OK");
        await overrideRepo.Received(1).RemoveOverrideAsync(TenantInternalId, Arg.Any<CancellationToken>());
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
