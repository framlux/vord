// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Net;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.FunctionalTest.Infrastructure;
using Framlux.Vord.BillingGrpc;
using Grpc.Core;
using Grpc.Net.Client;
using LinqToDB;
using LinqToDB.Async;

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
        await Assert.That(response.IsSuccessStatusCode).IsEqualTo(false);
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
    public async Task ListUsers_EmptyDatabase_ReturnsEmptyList()
    {
        using FunctionalTestFactory factory = new();
        factory.WithInternalApiKey("test-key");
        using GrpcChannel channel = CreateChannel(factory);
        FleetAdmin.FleetAdminClient client = new(channel);

        Metadata headers = Headers("test-key");

        ListUsersResponse response = await client.ListUsersAsync(
            new ListUsersRequest { Page = 1, PageSize = 10 }, headers);

        await Assert.That(response.TotalCount).IsEqualTo(0);
        await Assert.That(response.Users.Count).IsEqualTo(0);
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

        await Assert.That(response.TotalCount).IsEqualTo(3);
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
        await Assert.That(found.Subscription.Tier).IsEqualTo("Pro");
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
        await Assert.That(response.Tenant.Subscription.Tier).IsEqualTo("Team");
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
        await Assert.That(heartbeat.Key).IsEqualTo((int)ServerConfigurationSettingKeys.AgentHeartbeatSeconds);
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
                Key = (int)ServerConfigurationSettingKeys.AgentHeartbeatSeconds,
                Value = "45"
            },
            Headers("test-key"));

        await Assert.That(response.Success).IsEqualTo(true);

        ServerConfigurationSettings? updated = await db.ServerConfigurationSettings
            .Where(s => s.Key == ServerConfigurationSettingKeys.AgentHeartbeatSeconds)
            .FirstOrDefaultAsync();

        await Assert.That(updated).IsNotNull();
        await Assert.That(updated!.Value).IsEqualTo("45");
        await Assert.That(updated.Version).IsEqualTo(2);
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
                new UpdateServerSettingRequest { Key = 999, Value = "bad" },
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
                Tier = "Pro",
                Status = "Active",
                MachineLimit = 25,
                RetentionDays = 30
            },
            Headers("test-key"));

        await Assert.That(response.Success).IsEqualTo(true);

        TenantSubscription? updated = await db.TenantSubscriptions
            .Where(s => s.TenantId == tenantId)
            .FirstOrDefaultAsync();

        await Assert.That(updated).IsNotNull();
        await Assert.That(updated!.Tier).IsEqualTo(SubscriptionTier.Pro);
        await Assert.That(updated.Status).IsEqualTo(SubscriptionStatus.Active);
        await Assert.That(updated.MachineLimit).IsEqualTo(25);
        await Assert.That(updated.RetentionDays).IsEqualTo(30);
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
                    Tier = "InvalidTier",
                    Status = "Active",
                    MachineLimit = 10,
                    RetentionDays = 30
                },
                Headers("test-key"));
        }
        catch (RpcException ex)
        {
            exception = ex;
        }

        await Assert.That(exception).IsNotNull();
        await Assert.That(exception!.StatusCode).IsEqualTo(StatusCode.InvalidArgument);
        await Assert.That(exception.Status.Detail).Contains("InvalidTier");
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
                    Tier = "Pro",
                    Status = "Active",
                    MachineLimit = 10,
                    RetentionDays = 30
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

        await Assert.That(response.Success).IsEqualTo(true);

        TenantOidcConfiguration? config = await db.TenantOidcConfigurations
            .Where(c => c.TenantId == tenantId)
            .FirstOrDefaultAsync();

        await Assert.That(config).IsNotNull();
        await Assert.That(config!.Authority).IsEqualTo("https://idp.example.com");
        await Assert.That(config.ClientId).IsEqualTo("client-123");
        await Assert.That(config.EmailDomain).IsEqualTo("example.com");
        await Assert.That(config.IsEnabled).IsEqualTo(true);
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

        await Assert.That(response.Success).IsEqualTo(true);

        TenantOidcConfiguration? config = await db.TenantOidcConfigurations
            .Where(c => c.TenantId == tenantId)
            .FirstOrDefaultAsync();

        await Assert.That(config).IsNotNull();
        await Assert.That(config!.Authority).IsEqualTo("https://new-idp.example.com");
        await Assert.That(config.ClientId).IsEqualTo("new-client");
        await Assert.That(config.IsEnabled).IsEqualTo(true);
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
            CreatedByUserId = 0,
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
            CreatedByUserId = 0,
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
            MachineLimit = tier == SubscriptionTier.Free ? 3 : null,
            RetentionDays = tier switch
            {
                SubscriptionTier.Team => 365,
                SubscriptionTier.Pro => 30,
                _ => 1
            },
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
            AssignedByUserId = 0,
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
            CreatedByUserId = 0,
            CreatedAt = DateTimeOffset.UtcNow,
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
