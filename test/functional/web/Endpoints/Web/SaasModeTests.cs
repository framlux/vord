// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using LinqToDB.Async;

namespace Framlux.FleetManagement.FunctionalTest.Endpoints.Web;

/// <summary>
/// The hosted half of the two-mode matrix, mirroring <see cref="SelfHostedEndpointTests"/>. The
/// same requests that a self-hosted deployment must serve have to keep being refused here, so a
/// change that unlocks entitlements cannot leak into the paying product.
/// </summary>
public sealed class SaasModeTests
{
    private static async Task<(int TenantId, int UserId)> SeedTenant(
        DatabaseContext db,
        SubscriptionTier tier,
        bool isGlobalAdmin = false)
    {
        Tenant tenant = new()
        {
            ExternalId = Guid.NewGuid().ToString("N"),
            Name = $"Saas Mode Tenant {Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 1,
            IsActive = true,
            LogoUrl = ""
        };
        tenant.Id = await db.InsertWithInt32IdentityAsync(tenant);

        TenantSubscription subscription = new()
        {
            TenantId = tenant.Id,
            Tier = tier,
            Status = SubscriptionStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await db.InsertWithInt32IdentityAsync(subscription);

        UserAccount user = new()
        {
            ExternalId = $"ext-saas-mode-{Guid.NewGuid():N}",
            Username = $"saasmode-{Guid.NewGuid():N}@example.com",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 1,
            IsActive = true,
            IsSystem = false,
            IsGlobalAdmin = isGlobalAdmin,
        };
        user.Id = await db.InsertWithInt32IdentityAsync(user);

        UserTenantRole role = new()
        {
            UserId = user.Id,
            AssignedTenantId = tenant.Id,
            Role = UserAccountRoles.TenantAdmin,
            AssignedByUserId = user.Id,
            AssignedAt = DateTimeOffset.UtcNow,
            IsActive = true,
        };
        await db.InsertAsync(role);

        return (tenant.Id, user.Id);
    }

    private static async Task<long> SeedMachine(DatabaseContext db, int tenantId)
    {
        Machine machine = new()
        {
            TenantId = tenantId,
            Name = $"saas-mode-machine-{Guid.NewGuid():N}",
            ApiKeyHash = Guid.NewGuid().ToString("N"),
            SerialNumber = $"sn-{Guid.NewGuid():N}",
            SystemId = $"sid-{Guid.NewGuid():N}",
            MachineType = MachineTypes.VirtualMachine,
            OperatingSystem = OperatingSystems.Ubuntu,
            RegistrationTokenId = 0,
            RegisteredOn = DateTimeOffset.UtcNow,
            IsDeleted = false,
        };

        return await db.InsertWithInt64IdentityAsync(machine);
    }

    private static HttpClient BuildClient(
        FunctionalTestFactory factory,
        int tenantId,
        int userId,
        bool isGlobalAdmin = false)
    {
        AuthenticatedClientBuilder builder = new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithRole(tenantId, (int)UserAccountRoles.TenantAdmin)
            .WithActiveTenant(tenantId);

        if (isGlobalAdmin)
        {
            builder = builder.AsGlobalAdmin();
        }

        return builder.Build();
    }

    [Test]
    public async Task AlertRuleCreate_OnFreeRow_Returns403()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenant(db, SubscriptionTier.Free);
        long machineId = await SeedMachine(db, tenantId);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/alert-rules", new
        {
            Name = "Hosted CPU rule",
            Metric = "CpuUsage",
            Operator = "GreaterThan",
            Threshold = 90,
            DurationMinutes = 5,
            Severity = "Warning",
            MachineIds = new long[] { machineId },
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task IntegrationCreate_OnFreeRow_Returns403()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenant(db, SubscriptionTier.Free);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/integrations", new
        {
            provider = "Custom",
            name = "Hosted webhook",
            configuration = new Dictionary<string, string>
            {
                ["url"] = "https://relay.example.com/webhook"
            }
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task AuditLogList_OnFreeRow_Returns403()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenant(db, SubscriptionTier.Free);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.GetAsync("/api/v1/audit-log");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("Team subscription");
    }

    /// <summary>
    /// A Free row cannot invite at all in the hosted deployment, so role forcing is observed one
    /// tier up: every tier below Team has the invitee's requested role replaced with TenantAdmin.
    /// This is the behaviour a self-hosted deployment deliberately drops.
    /// </summary>
    [Test]
    public async Task Invitation_OnFreeRow_ForcesTenantAdminRole()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int freeTenantId, int freeUserId) = await SeedTenant(db, SubscriptionTier.Free);
        HttpClient freeClient = BuildClient(factory, freeTenantId, freeUserId);

        HttpResponseMessage freeResponse = await freeClient.PostAsJsonAsync("/api/v1/invitations", new
        {
            Email = $"free-invitee-{Guid.NewGuid():N}@example.com",
            Role = "Viewer",
        });

        await Assert.That(freeResponse.StatusCode).IsEqualTo(HttpStatusCode.PaymentRequired);

        (int proTenantId, int proUserId) = await SeedTenant(db, SubscriptionTier.Pro);
        HttpClient proClient = BuildClient(factory, proTenantId, proUserId);

        string inviteeEmail = $"pro-invitee-{Guid.NewGuid():N}@example.com";
        HttpResponseMessage proResponse = await proClient.PostAsJsonAsync("/api/v1/invitations", new
        {
            Email = inviteeEmail,
            Role = "Viewer",
        });

        await Assert.That(proResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);

        TenantInvitation? invitation = await db.TenantInvitations
            .FirstOrDefaultAsync(i => i.Email == inviteeEmail);

        await Assert.That(invitation).IsNotNull();
        await Assert.That(invitation!.Role).IsEqualTo(UserAccountRoles.TenantAdmin);
    }

    [Test]
    public async Task UpdateAdminSettings_InSaas_Returns404()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenant(db, SubscriptionTier.Pro, isGlobalAdmin: true);
        HttpClient client = BuildClient(factory, tenantId, userId, isGlobalAdmin: true);

        StringContent content = new(
            JsonSerializer.Serialize(new
            {
                settings = new[]
                {
                    new { key = (int)ServerConfigurationSettingKeys.AgentHeartbeatSeconds, value = "600" }
                }
            }),
            Encoding.UTF8,
            "application/json");

        HttpResponseMessage response = await client.PutAsync("/api/v1/admin/settings", content);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }
}
