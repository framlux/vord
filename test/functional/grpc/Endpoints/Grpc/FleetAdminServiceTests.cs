// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Net;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Services.Core.Billing;
using Framlux.FleetManagement.Test.Infrastructure;
using Framlux.Vord.BillingGrpc;
using Grpc.Core;
using Grpc.Net.Client;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace Framlux.FleetManagement.FunctionalTest.Endpoints.Grpc;

/// <summary>
/// Functional tests for the FleetAdminService gRPC endpoint.
/// </summary>
public sealed class FleetAdminServiceTests
{
    // ========== Billing Disabled Tests ==========

    [Test]
    public async Task FleetAdmin_BillingDisabled_ServiceNotMapped()
    {
        using BillingDisabledTestFactory factory = new();
        HttpClient httpClient = factory.CreateClient();

        // Send a raw HTTP POST to the gRPC endpoint path to verify the service is not mapped
        HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, "/billing.FleetAdmin/ListUsers");
        request.Content = new ByteArrayContent(System.Array.Empty<byte>());
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/grpc");
        request.Version = new System.Version(2, 0);

        System.Net.Http.HttpResponseMessage response = await httpClient.SendAsync(request);

        // When billing is disabled the gRPC service is not mapped, so the request falls through
        // to the default auth middleware which returns a non-success status
        await Assert.That(response.IsSuccessStatusCode).IsFalse();
    }

    // ========== Authentication Tests ==========

    [Test]
    public async Task ListUsers_MissingInternalKey_ThrowsUnauthenticated()
    {
        using FunctionalTestFactory factory = new();
        factory.WithInternalApiKey("test-internal-key");
        using GrpcChannel channel = CreateChannel(factory);
        FleetAdmin.FleetAdminClient client = new(channel);

        RpcException? exception = null;
        try
        {
            await client.ListUsersAsync(new ListUsersRequest { Page = 1, PageSize = 10 });
        }
        catch (RpcException ex)
        {
            exception = ex;
        }

        await Assert.That(exception).IsNotNull();
        await Assert.That(exception!.StatusCode).IsEqualTo(StatusCode.Unauthenticated);
    }

    [Test]
    public async Task ListUsers_WrongInternalKey_ThrowsUnauthenticated()
    {
        using FunctionalTestFactory factory = new();
        factory.WithInternalApiKey("test-internal-key");
        using GrpcChannel channel = CreateChannel(factory);
        FleetAdmin.FleetAdminClient client = new(channel);

        Metadata headers = new Metadata { { "x-internal-key", "wrong-key" } };

        RpcException? exception = null;
        try
        {
            await client.ListUsersAsync(new ListUsersRequest { Page = 1, PageSize = 10 }, headers);
        }
        catch (RpcException ex)
        {
            exception = ex;
        }

        await Assert.That(exception).IsNotNull();
        await Assert.That(exception!.StatusCode).IsEqualTo(StatusCode.Unauthenticated);
    }

    // ========== ListUsers Tests ==========

    [Test]
    public async Task ListUsers_EmptyDatabase_ReturnsOnlySystemUser()
    {
        using FunctionalTestFactory factory = new();
        factory.WithInternalApiKey("test-key");
        using GrpcChannel channel = CreateChannel(factory);
        FleetAdmin.FleetAdminClient client = new(channel);

        Metadata headers = Headers("test-key");

        ListUsersResponse response = await client.ListUsersAsync(
            new ListUsersRequest { Page = 1, PageSize = 10 }, headers);

        // The migration-seeded system user (Id=1) is always present; the endpoint does not
        // exclude IsSystem rows. A truly empty user table is not a reachable state.
        await Assert.That(response.TotalCount).IsEqualTo(1);
        await Assert.That(response.Users.Count).IsEqualTo(1);
        await Assert.That(response.Users[0].Username).IsEqualTo("system");
    }

    [Test]
    public async Task ListUsers_WithUsers_ReturnsPaginatedResults()
    {
        using FunctionalTestFactory factory = new();
        factory.WithInternalApiKey("test-key");
        using DatabaseContext db = factory.CreateDbContext();

        await SeedUser(db, "user-a", "ext-a", AuthProviderType.GitHub);
        await SeedUser(db, "user-b", "ext-b", AuthProviderType.Google);
        await SeedUser(db, "user-c", "ext-c", AuthProviderType.Microsoft);

        using GrpcChannel channel = CreateChannel(factory);
        FleetAdmin.FleetAdminClient client = new(channel);

        ListUsersResponse response = await client.ListUsersAsync(
            new ListUsersRequest { Page = 1, PageSize = 2 }, Headers("test-key"));

        // Total = 3 seeded + 1 migration-seeded system user.
        await Assert.That(response.TotalCount).IsEqualTo(4);
        await Assert.That(response.Users.Count).IsEqualTo(2);
    }

    [Test]
    public async Task ListUsers_WithSearch_FiltersResults()
    {
        using FunctionalTestFactory factory = new();
        factory.WithInternalApiKey("test-key");
        using DatabaseContext db = factory.CreateDbContext();

        await SeedUser(db, "alice", "ext-alice", AuthProviderType.GitHub);
        await SeedUser(db, "bob", "ext-bob", AuthProviderType.GitHub);

        using GrpcChannel channel = CreateChannel(factory);
        FleetAdmin.FleetAdminClient client = new(channel);

        ListUsersResponse response = await client.ListUsersAsync(
            new ListUsersRequest { Search = "alice", Page = 1, PageSize = 50 }, Headers("test-key"));

        await Assert.That(response.TotalCount).IsEqualTo(1);
        await Assert.That(response.Users[0].Username).IsEqualTo("alice");
    }

    [Test]
    public async Task ListUsers_IncludesTenantRoles()
    {
        using FunctionalTestFactory factory = new();
        factory.WithInternalApiKey("test-key");
        using DatabaseContext db = factory.CreateDbContext();

        int userId = await SeedUser(db, "admin-user", "ext-admin", AuthProviderType.GitHub);
        int tenantId = await SeedTenant(db, "test-tenant", "ext-tenant-1");
        await SeedUserTenantRole(db, userId, tenantId, UserAccountRoles.TenantAdmin);

        using GrpcChannel channel = CreateChannel(factory);
        FleetAdmin.FleetAdminClient client = new(channel);

        ListUsersResponse response = await client.ListUsersAsync(
            new ListUsersRequest { Page = 1, PageSize = 50 }, Headers("test-key"));

        await Assert.That(response.Users.Count).IsGreaterThanOrEqualTo(1);

        FleetUser? adminUser = null;
        foreach (FleetUser u in response.Users)
        {
            if (u.Username == "admin-user")
            {
                adminUser = u;
            }
        }

        await Assert.That(adminUser).IsNotNull();
        await Assert.That(adminUser!.TenantRoles.Count).IsEqualTo(1);
        await Assert.That(adminUser.TenantRoles[0].Role).IsEqualTo("TenantAdmin");
        await Assert.That(adminUser.TenantRoles[0].TenantName).IsEqualTo("test-tenant");
    }

    // ========== ListTenants Tests ==========

    [Test]
    public async Task ListTenants_EmptyDatabase_ReturnsEmptyList()
    {
        using FunctionalTestFactory factory = new();
        factory.WithInternalApiKey("test-key");
        using GrpcChannel channel = CreateChannel(factory);
        FleetAdmin.FleetAdminClient client = new(channel);

        ListTenantsResponse response = await client.ListTenantsAsync(
            new ListTenantsRequest { Page = 1, PageSize = 10 }, Headers("test-key"));

        await Assert.That(response.TotalCount).IsEqualTo(0);
        await Assert.That(response.Tenants.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ListTenants_WithTenants_IncludesCountsAndSubscription()
    {
        using FunctionalTestFactory factory = new();
        factory.WithInternalApiKey("test-key");
        using DatabaseContext db = factory.CreateDbContext();

        string extId = $"ext-{Guid.NewGuid():N}";
        int tenantId = await SeedTenantWithSubscription(db, extId, SubscriptionTier.Pro);
        int userId = await SeedUser(db, "tenant-user", $"ext-u-{Guid.NewGuid():N}", AuthProviderType.GitHub);
        await SeedUserTenantRole(db, userId, tenantId, UserAccountRoles.Viewer);
        await SeedMachine(db, tenantId, "machine-1");

        using GrpcChannel channel = CreateChannel(factory);
        FleetAdmin.FleetAdminClient client = new(channel);

        ListTenantsResponse response = await client.ListTenantsAsync(
            new ListTenantsRequest { Page = 1, PageSize = 50 }, Headers("test-key"));

        await Assert.That(response.TotalCount).IsGreaterThanOrEqualTo(1);

        FleetTenant? found = null;
        foreach (FleetTenant t in response.Tenants)
        {
            if (t.ExternalId == extId)
            {
                found = t;
            }
        }

        await Assert.That(found).IsNotNull();
        await Assert.That(found!.MachineCount).IsEqualTo(1);
        await Assert.That(found.UserCount).IsEqualTo(1);
        await Assert.That(found.Subscription).IsNotNull();
        await Assert.That(found.Subscription.Tier).IsEqualTo(BillingTier.Pro);
    }

    [Test]
    public async Task ListTenants_WithSearch_FiltersResults()
    {
        using FunctionalTestFactory factory = new();
        factory.WithInternalApiKey("test-key");
        using DatabaseContext db = factory.CreateDbContext();

        await SeedTenant(db, "alpha-corp", $"ext-{Guid.NewGuid():N}");
        await SeedTenant(db, "beta-corp", $"ext-{Guid.NewGuid():N}");

        using GrpcChannel channel = CreateChannel(factory);
        FleetAdmin.FleetAdminClient client = new(channel);

        ListTenantsResponse response = await client.ListTenantsAsync(
            new ListTenantsRequest { Search = "alpha", Page = 1, PageSize = 50 }, Headers("test-key"));

        await Assert.That(response.TotalCount).IsEqualTo(1);
        await Assert.That(response.Tenants[0].Name).IsEqualTo("alpha-corp");
    }

    // ========== GetTenantDetail Tests ==========

    [Test]
    public async Task GetTenantDetail_NonexistentTenant_ThrowsNotFound()
    {
        using FunctionalTestFactory factory = new();
        factory.WithInternalApiKey("test-key");
        using GrpcChannel channel = CreateChannel(factory);
        FleetAdmin.FleetAdminClient client = new(channel);

        RpcException? exception = null;
        try
        {
            await client.GetTenantDetailAsync(
                new GetTenantDetailRequest { TenantExternalId = "does-not-exist" },
                Headers("test-key"));
        }
        catch (RpcException ex)
        {
            exception = ex;
        }

        await Assert.That(exception).IsNotNull();
        await Assert.That(exception!.StatusCode).IsEqualTo(StatusCode.NotFound);
    }

    [Test]
    public async Task GetTenantDetail_ReturnsTenantWithUsersAndMachines()
    {
        using FunctionalTestFactory factory = new();
        factory.WithInternalApiKey("test-key");
        using DatabaseContext db = factory.CreateDbContext();

        string extId = $"ext-{Guid.NewGuid():N}";
        int tenantId = await SeedTenantWithSubscription(db, extId, SubscriptionTier.Team);
        int userId = await SeedUser(db, "detail-user", $"ext-u-{Guid.NewGuid():N}", AuthProviderType.Google);
        await SeedUserTenantRole(db, userId, tenantId, UserAccountRoles.TenantAdmin);
        await SeedMachine(db, tenantId, "detail-machine");

        using GrpcChannel channel = CreateChannel(factory);
        FleetAdmin.FleetAdminClient client = new(channel);

        GetTenantDetailResponse response = await client.GetTenantDetailAsync(
            new GetTenantDetailRequest { TenantExternalId = extId },
            Headers("test-key"));

        await Assert.That(response.Tenant).IsNotNull();
        await Assert.That(response.Tenant.ExternalId).IsEqualTo(extId);
        await Assert.That(response.Users.Count).IsEqualTo(1);
        await Assert.That(response.Users[0].Username).IsEqualTo("detail-user");
        await Assert.That(response.Machines.Count).IsEqualTo(1);
        await Assert.That(response.Machines[0].Name).IsEqualTo("detail-machine");
        await Assert.That(response.Tenant.Subscription).IsNotNull();
        await Assert.That(response.Tenant.Subscription.Tier).IsEqualTo(BillingTier.Team);
    }

    // ========== ListMachines Tests ==========

    [Test]
    public async Task ListMachines_NoFilter_ReturnsAllMachines()
    {
        using FunctionalTestFactory factory = new();
        factory.WithInternalApiKey("test-key");
        using DatabaseContext db = factory.CreateDbContext();

        int tenantId = await SeedTenant(db, "machine-tenant", $"ext-mt-{Guid.NewGuid():N}");
        await SeedMachine(db, tenantId, "m1");
        await SeedMachine(db, tenantId, "m2");

        using GrpcChannel channel = CreateChannel(factory);
        FleetAdmin.FleetAdminClient client = new(channel);

        ListMachinesResponse response = await client.ListMachinesAsync(
            new ListMachinesRequest { Page = 1, PageSize = 50 }, Headers("test-key"));

        await Assert.That(response.TotalCount).IsGreaterThanOrEqualTo(2);
    }

    [Test]
    public async Task ListMachines_WithTenantFilter_ReturnsFilteredMachines()
    {
        using FunctionalTestFactory factory = new();
        factory.WithInternalApiKey("test-key");
        using DatabaseContext db = factory.CreateDbContext();

        string ext1 = $"ext-{Guid.NewGuid():N}";
        string ext2 = $"ext-{Guid.NewGuid():N}";
        int tenant1 = await SeedTenant(db, "t1", ext1);
        int tenant2 = await SeedTenant(db, "t2", ext2);
        await SeedMachine(db, tenant1, "m-t1");
        await SeedMachine(db, tenant2, "m-t2");

        using GrpcChannel channel = CreateChannel(factory);
        FleetAdmin.FleetAdminClient client = new(channel);

        ListMachinesResponse response = await client.ListMachinesAsync(
            new ListMachinesRequest { TenantExternalId = ext1, Page = 1, PageSize = 50 },
            Headers("test-key"));

        await Assert.That(response.TotalCount).IsEqualTo(1);
        await Assert.That(response.Machines[0].Name).IsEqualTo("m-t1");
    }

    [Test]
    public async Task ListMachines_InvalidTenantExternalId_ThrowsNotFound()
    {
        using FunctionalTestFactory factory = new();
        factory.WithInternalApiKey("test-key");
        using GrpcChannel channel = CreateChannel(factory);
        FleetAdmin.FleetAdminClient client = new(channel);

        RpcException? exception = null;
        try
        {
            await client.ListMachinesAsync(
                new ListMachinesRequest { TenantExternalId = "invalid", Page = 1, PageSize = 50 },
                Headers("test-key"));
        }
        catch (RpcException ex)
        {
            exception = ex;
        }

        await Assert.That(exception).IsNotNull();
        await Assert.That(exception!.StatusCode).IsEqualTo(StatusCode.NotFound);
    }

    // ========== ListAuditLogEntries Tests ==========

    [Test]
    public async Task ListAuditLogEntries_ReturnsEntriesOrderedByTimestampDesc()
    {
        using FunctionalTestFactory factory = new();
        factory.WithInternalApiKey("test-key");
        using DatabaseContext db = factory.CreateDbContext();

        int tenantId = await SeedTenant(db, "audit-tenant", $"ext-{Guid.NewGuid():N}");
        int userId = await SeedUser(db, "audit-user", $"ext-u-{Guid.NewGuid():N}", AuthProviderType.GitHub);

        await SeedAuditLogEntry(db, tenantId, userId, AuditAction.MachineRegistered,
            AuditResourceType.Machine, DateTimeOffset.UtcNow.AddMinutes(-10));
        await SeedAuditLogEntry(db, tenantId, userId, AuditAction.UserLogin,
            AuditResourceType.User, DateTimeOffset.UtcNow);

        using GrpcChannel channel = CreateChannel(factory);
        FleetAdmin.FleetAdminClient client = new(channel);

        ListAuditLogEntriesResponse response = await client.ListAuditLogEntriesAsync(
            new ListAuditLogEntriesRequest { Page = 1, PageSize = 50 }, Headers("test-key"));

        await Assert.That(response.TotalCount).IsGreaterThanOrEqualTo(2);

        // First entry should be the more recent one
        await Assert.That(response.Entries[0].Action).IsEqualTo("UserLogin");
        await Assert.That(response.Entries[0].Username).IsEqualTo("audit-user");
    }

    [Test]
    public async Task ListAuditLogEntries_WithTenantFilter_FiltersEntries()
    {
        using FunctionalTestFactory factory = new();
        factory.WithInternalApiKey("test-key");
        using DatabaseContext db = factory.CreateDbContext();

        string ext1 = $"ext-{Guid.NewGuid():N}";
        string ext2 = $"ext-{Guid.NewGuid():N}";
        int tenant1 = await SeedTenant(db, "t-audit-1", ext1);
        int tenant2 = await SeedTenant(db, "t-audit-2", ext2);

        await SeedAuditLogEntry(db, tenant1, null, AuditAction.TenantCreated,
            AuditResourceType.Tenant, DateTimeOffset.UtcNow);
        await SeedAuditLogEntry(db, tenant2, null, AuditAction.TenantCreated,
            AuditResourceType.Tenant, DateTimeOffset.UtcNow);

        using GrpcChannel channel = CreateChannel(factory);
        FleetAdmin.FleetAdminClient client = new(channel);

        ListAuditLogEntriesResponse response = await client.ListAuditLogEntriesAsync(
            new ListAuditLogEntriesRequest { TenantExternalId = ext1, Page = 1, PageSize = 50 },
            Headers("test-key"));

        await Assert.That(response.TotalCount).IsEqualTo(1);
    }

    // ========== GetServerSettings Tests ==========

    [Test]
    public async Task GetServerSettings_ReturnsAllSettings()
    {
        using FunctionalTestFactory factory = new();
        factory.WithInternalApiKey("test-key");
        using DatabaseContext db = factory.CreateDbContext();

        await SeedServerSetting(db, ServerConfigurationSettingKeys.AgentHeartbeatSeconds, "30");
        await SeedServerSetting(db, ServerConfigurationSettingKeys.OnlineThresholdSeconds, "60");

        using GrpcChannel channel = CreateChannel(factory);
        FleetAdmin.FleetAdminClient client = new(channel);

        GetServerSettingsResponse response = await client.GetServerSettingsAsync(
            new GetServerSettingsRequest(), Headers("test-key"));

        await Assert.That(response.Settings.Count).IsGreaterThanOrEqualTo(2);

        ServerSetting? heartbeat = null;
        foreach (ServerSetting s in response.Settings)
        {
            if (s.KeyName == "AgentHeartbeatSeconds")
            {
                heartbeat = s;
            }
        }

        await Assert.That(heartbeat).IsNotNull();
        await Assert.That(heartbeat!.Value).IsEqualTo("30");
        await Assert.That(heartbeat.Key).IsEqualTo((ServerSettingKey)(int)ServerConfigurationSettingKeys.AgentHeartbeatSeconds);
    }

    [Test]
    public async Task GetServerSettings_IncludesServiceStatusSetting()
    {
        using FunctionalTestFactory factory = new();
        factory.WithInternalApiKey("test-key");
        using DatabaseContext db = factory.CreateDbContext();

        await SeedServerSetting(db, ServerConfigurationSettingKeys.ServiceStatusSeconds, "3600");

        using GrpcChannel channel = CreateChannel(factory);
        FleetAdmin.FleetAdminClient client = new(channel);

        GetServerSettingsResponse response = await client.GetServerSettingsAsync(
            new GetServerSettingsRequest(), Headers("test-key"));

        ServerSetting? serviceStatus = null;
        foreach (ServerSetting s in response.Settings)
        {
            if (s.Key == (ServerSettingKey)(int)ServerConfigurationSettingKeys.ServiceStatusSeconds)
            {
                serviceStatus = s;
            }
        }

        await Assert.That(serviceStatus).IsNotNull();
        await Assert.That(serviceStatus!.Value).IsEqualTo("3600");
        await Assert.That(serviceStatus.KeyName).IsEqualTo("ServiceStatusSeconds");
    }

    // ========== UpdateServerSetting Tests ==========

    [Test]
    public async Task UpdateServerSetting_ValidKey_UpdatesValueAndIncrementsVersion()
    {
        using FunctionalTestFactory factory = new();
        factory.WithInternalApiKey("test-key");
        using DatabaseContext db = factory.CreateDbContext();

        await SeedServerSetting(db, ServerConfigurationSettingKeys.AgentHeartbeatSeconds, "30");

        using GrpcChannel channel = CreateChannel(factory);
        FleetAdmin.FleetAdminClient client = new(channel);

        UpdateServerSettingResponse response = await client.UpdateServerSettingAsync(
            new UpdateServerSettingRequest
            {
                Key = (ServerSettingKey)(int)ServerConfigurationSettingKeys.AgentHeartbeatSeconds,
                Value = "45"
            },
            Headers("test-key"));

        await Assert.That(response.Success).IsTrue();

        ServerConfigurationSettings? updated = await db.ServerConfigurationSettings
            .Where(s => s.Key == ServerConfigurationSettingKeys.AgentHeartbeatSeconds)
            .FirstOrDefaultAsync();

        await Assert.That(updated).IsNotNull();
        await Assert.That(updated!.Value).IsEqualTo("45");
        await Assert.That(updated.Version).IsEqualTo(2);
    }

    [Test]
    public async Task UpdateServerSetting_ServiceStatus_ValidValue_Succeeds()
    {
        using FunctionalTestFactory factory = new();
        factory.WithInternalApiKey("test-key");
        using DatabaseContext db = factory.CreateDbContext();

        await SeedServerSetting(db, ServerConfigurationSettingKeys.ServiceStatusSeconds, "3600");

        using GrpcChannel channel = CreateChannel(factory);
        FleetAdmin.FleetAdminClient client = new(channel);

        UpdateServerSettingResponse response = await client.UpdateServerSettingAsync(
            new UpdateServerSettingRequest
            {
                Key = (ServerSettingKey)(int)ServerConfigurationSettingKeys.ServiceStatusSeconds,
                Value = "1800"
            },
            Headers("test-key"));

        await Assert.That(response.Success).IsTrue();

        ServerConfigurationSettings? updated = await db.ServerConfigurationSettings
            .Where(s => s.Key == ServerConfigurationSettingKeys.ServiceStatusSeconds)
            .FirstOrDefaultAsync();

        await Assert.That(updated).IsNotNull();
        await Assert.That(updated!.Value).IsEqualTo("1800");
        await Assert.That(updated.Version).IsEqualTo(2);
    }

    [Test]
    public async Task UpdateServerSetting_ServiceStatus_NonexistentRow_CreatesSetting()
    {
        using FunctionalTestFactory factory = new();
        factory.WithInternalApiKey("test-key");
        using DatabaseContext db = factory.CreateDbContext();
        using GrpcChannel channel = CreateChannel(factory);
        FleetAdmin.FleetAdminClient client = new(channel);

        // No seed row for ServiceStatusSeconds — the write must create it, matching the REST
        // admin path's upsert semantics, so a valid key never fails on a missing row.
        UpdateServerSettingResponse response = await client.UpdateServerSettingAsync(
            new UpdateServerSettingRequest
            {
                Key = (ServerSettingKey)(int)ServerConfigurationSettingKeys.ServiceStatusSeconds,
                Value = "1800"
            },
            Headers("test-key"));

        await Assert.That(response.Success).IsTrue();

        ServerConfigurationSettings? created = await db.ServerConfigurationSettings
            .Where(s => s.Key == ServerConfigurationSettingKeys.ServiceStatusSeconds)
            .FirstOrDefaultAsync();

        await Assert.That(created).IsNotNull();
        await Assert.That(created!.Value).IsEqualTo("1800");
        await Assert.That(created.Version).IsEqualTo(1);
    }

    [Test]
    public async Task UpdateServerSetting_ValidKey_EvictsSharedRedisCacheEntry()
    {
        using FunctionalTestFactory factory = new();
        factory.WithInternalApiKey("test-key");
        using DatabaseContext db = factory.CreateDbContext();

        await SeedServerSetting(db, ServerConfigurationSettingKeys.OnlineThresholdSeconds, "60");

        // Pre-seed the Redis cache entry that other replicas would serve until evicted.
        // The key format matches what ServerConfigurationService and AdminHandler use.
        IConnectionMultiplexer redis = factory.Services.GetRequiredService<IConnectionMultiplexer>();
        string redisKey = $"config:{ServerConfigurationSettingKeys.OnlineThresholdSeconds}";

        // Use the 6-argument overload that the FakeRedisConnection stub intercepts.
        await redis.GetDatabase().StringSetAsync(redisKey, "60", null, false, When.Always, CommandFlags.None);

        RedisValue cachedBefore = await redis.GetDatabase().StringGetAsync(redisKey);
        await Assert.That(cachedBefore.IsNull).IsFalse();

        using GrpcChannel channel = CreateChannel(factory);
        FleetAdmin.FleetAdminClient client = new(channel);

        UpdateServerSettingResponse response = await client.UpdateServerSettingAsync(
            new UpdateServerSettingRequest
            {
                Key = (ServerSettingKey)(int)ServerConfigurationSettingKeys.OnlineThresholdSeconds,
                Value = "90"
            },
            Headers("test-key"));

        await Assert.That(response.Success).IsTrue();

        // The cache entry must have been deleted so other replicas re-read from the database.
        RedisValue cachedAfter = await redis.GetDatabase().StringGetAsync(redisKey);
        await Assert.That(cachedAfter.IsNull).IsTrue();
    }

    [Test]
    public async Task UpdateServerSetting_InvalidKey_ThrowsInvalidArgument()
    {
        using FunctionalTestFactory factory = new();
        factory.WithInternalApiKey("test-key");
        using GrpcChannel channel = CreateChannel(factory);
        FleetAdmin.FleetAdminClient client = new(channel);

        RpcException? exception = null;
        try
        {
            await client.UpdateServerSettingAsync(
                new UpdateServerSettingRequest { Key = (ServerSettingKey)999, Value = "bad" },
                Headers("test-key"));
        }
        catch (RpcException ex)
        {
            exception = ex;
        }

        await Assert.That(exception).IsNotNull();
        await Assert.That(exception!.StatusCode).IsEqualTo(StatusCode.InvalidArgument);
    }

    // ========== UpdateTenantSubscription Tests ==========

    [Test]
    public async Task UpdateTenantSubscription_ValidRequest_UpdatesAllFields()
    {
        using FunctionalTestFactory factory = new();
        factory.WithInternalApiKey("test-key");
        using DatabaseContext db = factory.CreateDbContext();

        string extId = $"ext-{Guid.NewGuid():N}";
        int tenantId = await SeedTenantWithSubscription(db, extId, SubscriptionTier.Free);

        using GrpcChannel channel = CreateChannel(factory);
        FleetAdmin.FleetAdminClient client = new(channel);

        UpdateTenantSubscriptionResponse response = await client.UpdateTenantSubscriptionAsync(
            new UpdateTenantSubscriptionRequest
            {
                TenantExternalId = extId,
                Tier = BillingTier.Pro,
                Status = "Active",
            },
            Headers("test-key"));

        await Assert.That(response.Success).IsTrue();

        TenantSubscription? updated = await db.TenantSubscriptions
            .Where(s => s.TenantId == tenantId)
            .FirstOrDefaultAsync();

        await Assert.That(updated).IsNotNull();
        await Assert.That(updated!.Tier).IsEqualTo(SubscriptionTier.Pro);
        await Assert.That(updated.Status).IsEqualTo(SubscriptionStatus.Active);
    }

    [Test]
    public async Task UpdateTenantSubscription_InvalidTier_ThrowsInvalidArgument()
    {
        using FunctionalTestFactory factory = new();
        factory.WithInternalApiKey("test-key");
        using GrpcChannel channel = CreateChannel(factory);
        FleetAdmin.FleetAdminClient client = new(channel);

        RpcException? exception = null;
        try
        {
            await client.UpdateTenantSubscriptionAsync(
                new UpdateTenantSubscriptionRequest
                {
                    TenantExternalId = "ext-x",
                    Tier = BillingTier.Unspecified,
                    Status = "Active",
                },
                Headers("test-key"));
        }
        catch (RpcException ex)
        {
            exception = ex;
        }

        await Assert.That(exception).IsNotNull();
        await Assert.That(exception!.StatusCode).IsEqualTo(StatusCode.InvalidArgument);
        await Assert.That(exception.Status.Detail).Contains("Unspecified");
    }

    [Test]
    public async Task UpdateTenantSubscription_NonexistentTenant_ThrowsNotFound()
    {
        using FunctionalTestFactory factory = new();
        factory.WithInternalApiKey("test-key");
        using GrpcChannel channel = CreateChannel(factory);
        FleetAdmin.FleetAdminClient client = new(channel);

        RpcException? exception = null;
        try
        {
            await client.UpdateTenantSubscriptionAsync(
                new UpdateTenantSubscriptionRequest
                {
                    TenantExternalId = "nonexistent",
                    Tier = BillingTier.Pro,
                    Status = "Active",
                },
                Headers("test-key"));
        }
        catch (RpcException ex)
        {
            exception = ex;
        }

        await Assert.That(exception).IsNotNull();
        await Assert.That(exception!.StatusCode).IsEqualTo(StatusCode.NotFound);
    }

    // ========== SetTenantOverride / RemoveTenantOverride Audit Tests ==========

    [Test]
    public async Task SetTenantOverride_ValidRequest_WritesExactlyOneAuditLogEntry()
    {
        using FunctionalTestFactory factory = new();
        factory.WithInternalApiKey("test-key");
        using DatabaseContext db = factory.CreateDbContext();

        string extId = $"ext-{Guid.NewGuid():N}";
        int tenantId = await SeedTenantWithSubscription(db, extId, SubscriptionTier.Pro);

        using GrpcChannel channel = CreateChannel(factory);
        FleetAdmin.FleetAdminClient client = new(channel);

        SetTenantOverrideResponse response = await client.SetTenantOverrideAsync(
            new SetTenantOverrideRequest
            {
                TenantExternalId = extId,
                MachineLimit = 50,
                RetentionDays = 30,
                AlertRuleLimit = 10,
                WebhookLimit = 5,
            },
            Headers("test-key"));

        await Assert.That(response.Success).IsTrue();

        List<AuditLogEntry> entries = await db.AuditLog
            .Where(e => e.Action == AuditAction.TenantSubscriptionOverrideChanged
                        && e.ResourceType == AuditResourceType.Subscription
                        && e.TenantId == tenantId)
            .ToListAsync();

        await Assert.That(entries.Count).IsEqualTo(1);
        await Assert.That(entries[0].UserId).IsNull();
    }

    [Test]
    public async Task RemoveTenantOverride_ValidRequest_WritesExactlyOneAuditLogEntry()
    {
        using FunctionalTestFactory factory = new();
        factory.WithInternalApiKey("test-key");
        using DatabaseContext db = factory.CreateDbContext();

        string extId = $"ext-{Guid.NewGuid():N}";
        int tenantId = await SeedTenantWithSubscription(db, extId, SubscriptionTier.Pro);

        // Seed an override row first so RemoveTenantOverride has something to remove.
        await db.InsertAsync(new TenantSubscriptionOverride
        {
            TenantId = tenantId,
            MachineLimit = 100,
            RetentionDays = null,
            AlertRuleLimit = null,
            WebhookLimit = null,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        using GrpcChannel channel = CreateChannel(factory);
        FleetAdmin.FleetAdminClient client = new(channel);

        RemoveTenantOverrideResponse response = await client.RemoveTenantOverrideAsync(
            new RemoveTenantOverrideRequest { TenantExternalId = extId },
            Headers("test-key"));

        await Assert.That(response.Success).IsTrue();

        List<AuditLogEntry> entries = await db.AuditLog
            .Where(e => e.Action == AuditAction.TenantSubscriptionOverrideChanged
                        && e.ResourceType == AuditResourceType.Subscription
                        && e.TenantId == tenantId)
            .ToListAsync();

        await Assert.That(entries.Count).IsEqualTo(1);
        await Assert.That(entries[0].UserId).IsNull();
    }

    // ========== ConfigureTenantOidc Tests ==========

    [Test]
    public async Task ConfigureTenantOidc_NewConfig_CreatesRecord()
    {
        using FunctionalTestFactory factory = new();
        factory.WithInternalApiKey("test-key");
        using DatabaseContext db = factory.CreateDbContext();

        string extId = $"ext-{Guid.NewGuid():N}";
        int tenantId = await SeedTenant(db, "oidc-tenant", extId);

        using GrpcChannel channel = CreateChannel(factory);
        FleetAdmin.FleetAdminClient client = new(channel);

        ConfigureTenantOidcResponse response = await client.ConfigureTenantOidcAsync(
            new ConfigureTenantOidcRequest
            {
                TenantExternalId = extId,
                Authority = "https://idp.example.com",
                ClientId = "client-123",
                ClientSecret = "secret-456",
                MetadataAddress = "https://idp.example.com/.well-known/openid-configuration",
                EmailDomain = "example.com",
                IsEnabled = true
            },
            Headers("test-key"));

        await Assert.That(response.Success).IsTrue();

        TenantOidcConfiguration? config = await db.TenantOidcConfigurations
            .Where(c => c.TenantId == tenantId)
            .FirstOrDefaultAsync();

        await Assert.That(config).IsNotNull();
        await Assert.That(config!.Authority).IsEqualTo("https://idp.example.com");
        await Assert.That(config.ClientId).IsEqualTo("client-123");
        await Assert.That(config.EmailDomain).IsEqualTo("example.com");
        await Assert.That(config.IsEnabled).IsTrue();
    }

    [Test]
    public async Task ConfigureTenantOidc_ExistingConfig_UpdatesRecord()
    {
        using FunctionalTestFactory factory = new();
        factory.WithInternalApiKey("test-key");
        using DatabaseContext db = factory.CreateDbContext();

        string extId = $"ext-{Guid.NewGuid():N}";
        int tenantId = await SeedTenant(db, "oidc-update-tenant", extId);

        // Seed existing OIDC config
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await db.InsertAsync(new TenantOidcConfiguration
        {
            TenantId = tenantId,
            Authority = "https://old-idp.example.com",
            ClientId = "old-client",
            ClientSecret = "old-secret",
            EmailDomain = "old.example.com",
            IsEnabled = false,
            CreatedAt = now,
            UpdatedAt = now
        });

        using GrpcChannel channel = CreateChannel(factory);
        FleetAdmin.FleetAdminClient client = new(channel);

        ConfigureTenantOidcResponse response = await client.ConfigureTenantOidcAsync(
            new ConfigureTenantOidcRequest
            {
                TenantExternalId = extId,
                Authority = "https://new-idp.example.com",
                ClientId = "new-client",
                ClientSecret = "new-secret",
                EmailDomain = "new.example.com",
                IsEnabled = true
            },
            Headers("test-key"));

        await Assert.That(response.Success).IsTrue();

        TenantOidcConfiguration? config = await db.TenantOidcConfigurations
            .Where(c => c.TenantId == tenantId)
            .FirstOrDefaultAsync();

        await Assert.That(config).IsNotNull();
        await Assert.That(config!.Authority).IsEqualTo("https://new-idp.example.com");
        await Assert.That(config.ClientId).IsEqualTo("new-client");
        await Assert.That(config.IsEnabled).IsTrue();
    }

    [Test]
    public async Task ConfigureTenantOidc_NonexistentTenant_ThrowsNotFound()
    {
        using FunctionalTestFactory factory = new();
        factory.WithInternalApiKey("test-key");
        using GrpcChannel channel = CreateChannel(factory);
        FleetAdmin.FleetAdminClient client = new(channel);

        RpcException? exception = null;
        try
        {
            await client.ConfigureTenantOidcAsync(
                new ConfigureTenantOidcRequest
                {
                    TenantExternalId = "nonexistent",
                    Authority = "https://idp.example.com",
                    ClientId = "c",
                    ClientSecret = "s",
                    EmailDomain = "e.com",
                    IsEnabled = true
                },
                Headers("test-key"));
        }
        catch (RpcException ex)
        {
            exception = ex;
        }

        await Assert.That(exception).IsNotNull();
        await Assert.That(exception!.StatusCode).IsEqualTo(StatusCode.NotFound);
    }

    // ========== RequestTenantDeletion Tests ==========

    [Test]
    public async Task RequestTenantDeletion_ValidTenant_DeactivatesAndSchedulesPurge()
    {
        using FunctionalTestFactory factory = new();
        factory.WithInternalApiKey("test-key");
        using DatabaseContext db = factory.CreateDbContext();

        string extId = $"ext-{Guid.NewGuid():N}";
        int tenantId = await SeedTenantWithSubscription(db, extId, SubscriptionTier.Pro);
        int userId = await SeedUser(db, "deleter", $"ext-u-{Guid.NewGuid():N}", AuthProviderType.GitHub);

        using GrpcChannel channel = CreateChannel(factory);
        FleetAdmin.FleetAdminClient client = new(channel);

        DateTimeOffset beforeCall = DateTimeOffset.UtcNow;

        RequestTenantDeletionResponse response = await client.RequestTenantDeletionAsync(
            new RequestTenantDeletionRequest
            {
                TenantExternalId = extId,
                RequestedByUserId = userId,
                Reason = "customer offboarding",
            },
            Headers("test-key"));

        await Assert.That(response.Success).IsTrue();

        DateTimeOffset scheduledPurgeAt = response.ScheduledPurgeAt.ToDateTimeOffset();
        DateTimeOffset expected = beforeCall.AddDays(30);
        await Assert.That((scheduledPurgeAt - expected).Duration()).IsLessThan(TimeSpan.FromMinutes(1));

        TenantDeletion? deletion = await db.TenantDeletions
            .Where(d => d.TenantId == tenantId)
            .FirstOrDefaultAsync();

        await Assert.That(deletion).IsNotNull();
        await Assert.That(deletion!.Status).IsEqualTo(TenantDeletionStatus.Deactivated);
        await Assert.That(deletion.Reason).IsEqualTo("customer offboarding");

        Tenant? tenant = await db.Tenants.Where(t => t.Id == tenantId).FirstOrDefaultAsync();
        await Assert.That(tenant).IsNotNull();
        await Assert.That(tenant!.IsActive).IsFalse();

        List<AuditLogEntry> entries = await db.AuditLog
            .Where(e => e.TenantId == tenantId && e.Action == AuditAction.TenantDeletionRequested)
            .ToListAsync();

        await Assert.That(entries.Count).IsEqualTo(1);
    }

    [Test]
    public async Task RequestTenantDeletion_SecondCallForSameTenant_ReturnsFailure()
    {
        using FunctionalTestFactory factory = new();
        factory.WithInternalApiKey("test-key");
        using DatabaseContext db = factory.CreateDbContext();

        string extId = $"ext-{Guid.NewGuid():N}";
        int tenantId = await SeedTenantWithSubscription(db, extId, SubscriptionTier.Pro);
        int userId = await SeedUser(db, "deleter2", $"ext-u-{Guid.NewGuid():N}", AuthProviderType.GitHub);

        using GrpcChannel channel = CreateChannel(factory);
        FleetAdmin.FleetAdminClient client = new(channel);

        RequestTenantDeletionResponse first = await client.RequestTenantDeletionAsync(
            new RequestTenantDeletionRequest { TenantExternalId = extId, RequestedByUserId = userId },
            Headers("test-key"));

        await Assert.That(first.Success).IsTrue();

        RequestTenantDeletionResponse second = await client.RequestTenantDeletionAsync(
            new RequestTenantDeletionRequest { TenantExternalId = extId, RequestedByUserId = userId },
            Headers("test-key"));

        await Assert.That(second.Success).IsFalse();

        int deletionCount = (int)await db.TenantDeletions
            .Where(d => d.TenantId == tenantId)
            .CountAsync();

        await Assert.That(deletionCount).IsEqualTo(1);
    }

    [Test]
    public async Task RequestTenantDeletion_UnknownTenant_ThrowsNotFound()
    {
        using FunctionalTestFactory factory = new();
        factory.WithInternalApiKey("test-key");
        using GrpcChannel channel = CreateChannel(factory);
        FleetAdmin.FleetAdminClient client = new(channel);

        RpcException? exception = null;
        try
        {
            await client.RequestTenantDeletionAsync(
                new RequestTenantDeletionRequest { TenantExternalId = "does-not-exist", RequestedByUserId = 1 },
                Headers("test-key"));
        }
        catch (RpcException ex)
        {
            exception = ex;
        }

        await Assert.That(exception).IsNotNull();
        await Assert.That(exception!.StatusCode).IsEqualTo(StatusCode.NotFound);
    }

    [Test]
    public async Task RequestTenantDeletion_MissingInternalKey_ThrowsUnauthenticated()
    {
        using FunctionalTestFactory factory = new();
        factory.WithInternalApiKey("test-key");
        using GrpcChannel channel = CreateChannel(factory);
        FleetAdmin.FleetAdminClient client = new(channel);

        RpcException? exception = null;
        try
        {
            await client.RequestTenantDeletionAsync(
                new RequestTenantDeletionRequest { TenantExternalId = "whatever", RequestedByUserId = 1 });
        }
        catch (RpcException ex)
        {
            exception = ex;
        }

        await Assert.That(exception).IsNotNull();
        await Assert.That(exception!.StatusCode).IsEqualTo(StatusCode.Unauthenticated);
    }

    [Test]
    public async Task RequestTenantDeletion_DeactivatesTenant_BlocksTelemetryIngest()
    {
        using FunctionalTestFactory factory = new();
        factory.WithInternalApiKey("test-key");
        using DatabaseContext db = factory.CreateDbContext();

        string extId = $"ext-{Guid.NewGuid():N}";
        int tenantId = await SeedTenantWithSubscription(db, extId, SubscriptionTier.Pro);
        int userId = await SeedUser(db, "ingest-guard", $"ext-u-{Guid.NewGuid():N}", AuthProviderType.GitHub);

        using GrpcChannel channel = CreateChannel(factory);
        FleetAdmin.FleetAdminClient client = new(channel);

        // Before deactivation, the tenant is ingest-eligible.
        using (IServiceScope beforeScope = factory.Services.CreateScope())
        {
            ISubscriptionService beforeSubscriptionService = beforeScope.ServiceProvider.GetRequiredService<ISubscriptionService>();
            await Assert.That(await beforeSubscriptionService.IsIngestEligibleAsync(tenantId, CancellationToken.None)).IsTrue();
        }

        RequestTenantDeletionResponse response = await client.RequestTenantDeletionAsync(
            new RequestTenantDeletionRequest { TenantExternalId = extId, RequestedByUserId = userId },
            Headers("test-key"));

        await Assert.That(response.Success).IsTrue();

        using IServiceScope afterScope = factory.Services.CreateScope();
        ISubscriptionService afterSubscriptionService = afterScope.ServiceProvider.GetRequiredService<ISubscriptionService>();
        await Assert.That(await afterSubscriptionService.IsIngestEligibleAsync(tenantId, CancellationToken.None)).IsFalse();
    }

    // ========== RestoreTenant Tests ==========

    [Test]
    public async Task RestoreTenant_DeactivatedTenant_RestoresAndReactivates()
    {
        using FunctionalTestFactory factory = new();
        factory.WithInternalApiKey("test-key");
        using DatabaseContext db = factory.CreateDbContext();

        string extId = $"ext-{Guid.NewGuid():N}";
        int tenantId = await SeedTenantWithSubscription(db, extId, SubscriptionTier.Pro);
        int userId = await SeedUser(db, "restorer", $"ext-u-{Guid.NewGuid():N}", AuthProviderType.GitHub);

        using GrpcChannel channel = CreateChannel(factory);
        FleetAdmin.FleetAdminClient client = new(channel);

        RequestTenantDeletionResponse requestResponse = await client.RequestTenantDeletionAsync(
            new RequestTenantDeletionRequest { TenantExternalId = extId, RequestedByUserId = userId },
            Headers("test-key"));

        await Assert.That(requestResponse.Success).IsTrue();

        RestoreTenantResponse restoreResponse = await client.RestoreTenantAsync(
            new RestoreTenantRequest { TenantExternalId = extId, RequestedByUserId = userId },
            Headers("test-key"));

        await Assert.That(restoreResponse.Success).IsTrue();

        Tenant? tenant = await db.Tenants.Where(t => t.Id == tenantId).FirstOrDefaultAsync();
        await Assert.That(tenant).IsNotNull();
        await Assert.That(tenant!.IsActive).IsTrue();

        TenantDeletion? deletion = await db.TenantDeletions
            .Where(d => d.TenantId == tenantId)
            .FirstOrDefaultAsync();

        await Assert.That(deletion).IsNotNull();
        await Assert.That(deletion!.Status).IsEqualTo(TenantDeletionStatus.Restored);
    }

    [Test]
    public async Task RestoreTenant_NoPendingDeletion_ReturnsFailure()
    {
        using FunctionalTestFactory factory = new();
        factory.WithInternalApiKey("test-key");
        using DatabaseContext db = factory.CreateDbContext();

        string extId = $"ext-{Guid.NewGuid():N}";
        await SeedTenantWithSubscription(db, extId, SubscriptionTier.Pro);

        using GrpcChannel channel = CreateChannel(factory);
        FleetAdmin.FleetAdminClient client = new(channel);

        RestoreTenantResponse restoreResponse = await client.RestoreTenantAsync(
            new RestoreTenantRequest { TenantExternalId = extId, RequestedByUserId = 1 },
            Headers("test-key"));

        await Assert.That(restoreResponse.Success).IsFalse();
    }

    [Test]
    public async Task RestoreTenant_UnknownTenant_ThrowsNotFound()
    {
        using FunctionalTestFactory factory = new();
        factory.WithInternalApiKey("test-key");
        using GrpcChannel channel = CreateChannel(factory);
        FleetAdmin.FleetAdminClient client = new(channel);

        RpcException? exception = null;
        try
        {
            await client.RestoreTenantAsync(
                new RestoreTenantRequest { TenantExternalId = "does-not-exist", RequestedByUserId = 1 },
                Headers("test-key"));
        }
        catch (RpcException ex)
        {
            exception = ex;
        }

        await Assert.That(exception).IsNotNull();
        await Assert.That(exception!.StatusCode).IsEqualTo(StatusCode.NotFound);
    }

    [Test]
    public async Task RestoreTenant_MissingInternalKey_ThrowsUnauthenticated()
    {
        using FunctionalTestFactory factory = new();
        factory.WithInternalApiKey("test-key");
        using GrpcChannel channel = CreateChannel(factory);
        FleetAdmin.FleetAdminClient client = new(channel);

        RpcException? exception = null;
        try
        {
            await client.RestoreTenantAsync(
                new RestoreTenantRequest { TenantExternalId = "whatever", RequestedByUserId = 1 });
        }
        catch (RpcException ex)
        {
            exception = ex;
        }

        await Assert.That(exception).IsNotNull();
        await Assert.That(exception!.StatusCode).IsEqualTo(StatusCode.Unauthenticated);
    }

    // ========== ListTenantDeletions Tests ==========

    [Test]
    public async Task ListTenantDeletions_ExcludeCompleted_ReturnsOnlyDeactivated()
    {
        using FunctionalTestFactory factory = new();
        factory.WithInternalApiKey("test-key");
        using DatabaseContext db = factory.CreateDbContext();

        string extActive = $"ext-{Guid.NewGuid():N}";
        string extRestored = $"ext-{Guid.NewGuid():N}";
        int tenantActive = await SeedTenantWithSubscription(db, extActive, SubscriptionTier.Pro);
        int tenantRestored = await SeedTenantWithSubscription(db, extRestored, SubscriptionTier.Pro);
        int userId = await SeedUser(db, "list-user", $"ext-u-{Guid.NewGuid():N}", AuthProviderType.GitHub);

        using GrpcChannel channel = CreateChannel(factory);
        FleetAdmin.FleetAdminClient client = new(channel);

        await client.RequestTenantDeletionAsync(
            new RequestTenantDeletionRequest { TenantExternalId = extActive, RequestedByUserId = userId, Reason = "still pending" },
            Headers("test-key"));

        await client.RequestTenantDeletionAsync(
            new RequestTenantDeletionRequest { TenantExternalId = extRestored, RequestedByUserId = userId },
            Headers("test-key"));
        await client.RestoreTenantAsync(
            new RestoreTenantRequest { TenantExternalId = extRestored, RequestedByUserId = userId },
            Headers("test-key"));

        ListTenantDeletionsResponse excludeCompleted = await client.ListTenantDeletionsAsync(
            new ListTenantDeletionsRequest { IncludeCompleted = false, Page = 1, PageSize = 50 },
            Headers("test-key"));

        await Assert.That(excludeCompleted.Deletions.Count).IsEqualTo(1);
        await Assert.That(excludeCompleted.Deletions[0].TenantExternalId).IsEqualTo(extActive);
        await Assert.That(excludeCompleted.Deletions[0].TenantId).IsEqualTo(tenantActive);
        await Assert.That(excludeCompleted.Deletions[0].Status).IsEqualTo((int)TenantDeletionStatus.Deactivated);
        await Assert.That(excludeCompleted.Deletions[0].Reason).IsEqualTo("still pending");
        await Assert.That(excludeCompleted.TotalCount).IsEqualTo(1);

        ListTenantDeletionsResponse includeCompleted = await client.ListTenantDeletionsAsync(
            new ListTenantDeletionsRequest { IncludeCompleted = true, Page = 1, PageSize = 50 },
            Headers("test-key"));

        await Assert.That(includeCompleted.TotalCount).IsEqualTo(2);

        TenantDeletionRecord? restoredRecord = null;
        foreach (TenantDeletionRecord record in includeCompleted.Deletions)
        {
            if (record.TenantId == tenantRestored)
            {
                restoredRecord = record;
            }
        }

        await Assert.That(restoredRecord).IsNotNull();
        await Assert.That(restoredRecord!.Status).IsEqualTo((int)TenantDeletionStatus.Restored);
        await Assert.That(restoredRecord.Reason).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task ListTenantDeletions_Pagination_ReturnsRequestedPageAndTotalCount()
    {
        using FunctionalTestFactory factory = new();
        factory.WithInternalApiKey("test-key");
        using DatabaseContext db = factory.CreateDbContext();

        int userId = await SeedUser(db, "page-user", $"ext-u-{Guid.NewGuid():N}", AuthProviderType.GitHub);

        using GrpcChannel channel = CreateChannel(factory);
        FleetAdmin.FleetAdminClient client = new(channel);

        for (int i = 0; i < 3; i++)
        {
            string extId = $"ext-{Guid.NewGuid():N}";
            await SeedTenantWithSubscription(db, extId, SubscriptionTier.Pro);
            await client.RequestTenantDeletionAsync(
                new RequestTenantDeletionRequest { TenantExternalId = extId, RequestedByUserId = userId },
                Headers("test-key"));
        }

        ListTenantDeletionsResponse page1 = await client.ListTenantDeletionsAsync(
            new ListTenantDeletionsRequest { IncludeCompleted = true, Page = 1, PageSize = 2 },
            Headers("test-key"));

        await Assert.That(page1.TotalCount).IsEqualTo(3);
        await Assert.That(page1.Deletions.Count).IsEqualTo(2);

        ListTenantDeletionsResponse page2 = await client.ListTenantDeletionsAsync(
            new ListTenantDeletionsRequest { IncludeCompleted = true, Page = 2, PageSize = 2 },
            Headers("test-key"));

        await Assert.That(page2.TotalCount).IsEqualTo(3);
        await Assert.That(page2.Deletions.Count).IsEqualTo(1);
    }

    [Test]
    public async Task ListTenantDeletions_MissingInternalKey_ThrowsUnauthenticated()
    {
        using FunctionalTestFactory factory = new();
        factory.WithInternalApiKey("test-key");
        using GrpcChannel channel = CreateChannel(factory);
        FleetAdmin.FleetAdminClient client = new(channel);

        RpcException? exception = null;
        try
        {
            await client.ListTenantDeletionsAsync(
                new ListTenantDeletionsRequest { IncludeCompleted = true, Page = 1, PageSize = 10 });
        }
        catch (RpcException ex)
        {
            exception = ex;
        }

        await Assert.That(exception).IsNotNull();
        await Assert.That(exception!.StatusCode).IsEqualTo(StatusCode.Unauthenticated);
    }

    // ========== Helpers ==========

    private static Metadata Headers(string key)
    {
        return new Metadata { { "x-internal-key", key } };
    }

    private static GrpcChannel CreateChannel(FunctionalTestFactory factory)
    {
        HttpMessageHandler handler = new ResponseVersionHandler
        {
            InnerHandler = factory.Server.CreateHandler()
        };

        return GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
        {
            HttpHandler = handler
        });
    }

    private static async Task<int> SeedUser(
        DatabaseContext db, string username, string externalId, AuthProviderType authProvider)
    {
        UserAccount user = new()
        {
            Username = username,
            ExternalId = externalId,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 1,
            IsActive = true,
            IsSystem = false,
            IsGlobalAdmin = false,
            AuthProvider = authProvider
        };

        return (int)(long)await db.InsertWithIdentityAsync(user);
    }

    private static async Task<int> SeedTenant(DatabaseContext db, string name, string externalId)
    {
        Tenant tenant = new()
        {
            Name = name,
            ExternalId = externalId,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 1,
            IsActive = true,
            LogoUrl = ""
        };

        return (int)(long)await db.InsertWithIdentityAsync(tenant);
    }

    private static async Task<int> SeedTenantWithSubscription(
        DatabaseContext db, string externalId, SubscriptionTier tier,
        SubscriptionStatus status = SubscriptionStatus.Active)
    {
        int tenantId = await SeedTenant(db, $"tenant-{Guid.NewGuid():N}", externalId);

        TenantSubscription subscription = new()
        {
            TenantId = tenantId,
            Tier = tier,
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await db.InsertAsync(subscription);

        return tenantId;
    }

    private static async Task SeedUserTenantRole(
        DatabaseContext db, int userId, int tenantId, UserAccountRoles role)
    {
        UserTenantRole utr = new()
        {
            UserId = userId,
            AssignedTenantId = tenantId,
            Role = role,
            AssignedByUserId = 1,
            AssignedAt = DateTimeOffset.UtcNow,
            IsActive = true
        };
        await db.InsertAsync(utr);
    }

    private static async Task SeedMachine(DatabaseContext db, int tenantId, string name)
    {
        // Seed a registration token first since Machine requires RegistrationTokenId
        RegistrationToken token = new()
        {
            TokenHash = $"hash-{Guid.NewGuid():N}",
            Name = "test-token",
            TenantId = tenantId,
            CreatedByUserId = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
            IsRevoked = false
        };
        long tokenId = (long)await db.InsertWithIdentityAsync(token);

        Machine machine = new()
        {
            Name = name,
            TenantId = tenantId,
            ApiKeyHash = $"hash-{Guid.NewGuid():N}",
            SerialNumber = $"SN-{Guid.NewGuid():N}",
            SystemId = $"SYS-{Guid.NewGuid():N}",
            MachineType = MachineTypes.BareMetalServer,
            OperatingSystem = OperatingSystems.Ubuntu,
            RegistrationTokenId = tokenId,
            RegisteredOn = DateTimeOffset.UtcNow,
            IsDeleted = false
        };
        await db.InsertAsync(machine);
    }

    private static async Task SeedAuditLogEntry(
        DatabaseContext db, int tenantId, int? userId,
        AuditAction action, AuditResourceType resourceType, DateTimeOffset timestamp)
    {
        AuditLogEntry entry = new()
        {
            TenantId = tenantId,
            UserId = userId,
            Action = action,
            ResourceType = resourceType,
            Details = $"Test entry: {action}",
            Timestamp = timestamp
        };
        await db.InsertAsync(entry);
    }

    private static async Task SeedServerSetting(
        DatabaseContext db, ServerConfigurationSettingKeys key, string value)
    {
        ServerConfigurationSettings setting = new()
        {
            Key = key,
            Value = value,
            Version = 1
        };
        await db.InsertAsync(setting);
    }
}
