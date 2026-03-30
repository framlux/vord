// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Server.Endpoints.Grpc;
using Framlux.Vord.BillingGrpc;

namespace Framlux.FleetManagement.UnitTest.Endpoints.Grpc;

/// <summary>
/// Unit tests for FleetAdminService mapping helpers and pagination logic.
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
        await Assert.That(result.IsActive).IsEqualTo(true);
        await Assert.That(result.IsGlobalAdmin).IsEqualTo(true);
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
            MachineLimit = 50,
            RetentionDays = 30,
            CancelAtPeriodEnd = false,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        FleetTenant result = FleetAdminService.MapToFleetTenant(tenant, 10, 3, subscription);

        await Assert.That(result.Id).IsEqualTo(5);
        await Assert.That(result.ExternalId).IsEqualTo("ext-t5");
        await Assert.That(result.Name).IsEqualTo("acme");
        await Assert.That(result.IsActive).IsEqualTo(true);
        await Assert.That(result.LogoUrl).IsEqualTo("https://example.com/logo.png");
        await Assert.That(result.MachineCount).IsEqualTo(10);
        await Assert.That(result.UserCount).IsEqualTo(3);
        await Assert.That(result.Subscription).IsNotNull();
        await Assert.That(result.Subscription.Tier).IsEqualTo("Pro");
        await Assert.That(result.Subscription.Status).IsEqualTo("Active");
        await Assert.That(result.Subscription.MachineLimit).IsEqualTo(50);
        await Assert.That(result.Subscription.RetentionDays).IsEqualTo(30);
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
            MachineLimit = null,
            RetentionDays = 365,
            CancelAtPeriodEnd = false,
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
            RetentionDays = 30,
            CurrentPeriodEnd = periodEnd,
            CancelAtPeriodEnd = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        FleetTenantSubscription result = FleetAdminService.MapSubscription(subscription);

        await Assert.That(result.CurrentPeriodEnd).IsNotNull();
        await Assert.That(result.CancelAtPeriodEnd).IsEqualTo(true);
    }

    [Test]
    public async Task MapSubscription_NullCurrentPeriodEnd_OmitsTimestamp()
    {
        TenantSubscription subscription = new TenantSubscription
        {
            TenantId = 1,
            Tier = SubscriptionTier.Free,
            Status = SubscriptionStatus.Active,
            RetentionDays = 1,
            CurrentPeriodEnd = null,
            CancelAtPeriodEnd = false,
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
        await Assert.That(result.IsDeleted).IsEqualTo(false);
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
}
