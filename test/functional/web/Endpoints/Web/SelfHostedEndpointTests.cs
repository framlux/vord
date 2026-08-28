// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Services.Core.Billing;
using Framlux.FleetManagement.Services.Core.Machines;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.Extensions.DependencyInjection;

namespace Framlux.FleetManagement.FunctionalTest.Endpoints.Web;

/// <summary>
/// The self-hosted half of the two-mode matrix. A self-hosted deployment has no subscription tiers,
/// so every entitlement-gated surface must succeed for a tenant whose stored subscription row is
/// still <see cref="SubscriptionTier.Free"/>, while the billing management endpoints — which need a
/// SaaS control plane behind them — are absent. The subscription read endpoint stays reachable in
/// both modes because the web shell fetches it on every page load.
/// </summary>
public sealed class SelfHostedEndpointTests
{
    private static async Task<(int TenantId, int UserId)> SeedTenantWithSubscription(
        DatabaseContext db,
        SubscriptionTier tier = SubscriptionTier.Pro,
        bool isGlobalAdmin = false)
    {
        Tenant tenant = new()
        {
            ExternalId = Guid.NewGuid().ToString("N"),
            Name = $"Self Hosted Tenant {Guid.NewGuid():N}",
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
            ExternalId = $"ext-self-hosted-{Guid.NewGuid():N}",
            Username = $"selfhosted-{Guid.NewGuid():N}@example.com",
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
            Name = $"self-hosted-machine-{Guid.NewGuid():N}",
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

    private static async Task<(int SigningKeyId, NSec.Cryptography.Key PrivateKey)> SeedSigningKey(
        DatabaseContext db,
        int tenantId,
        int userId)
    {
        NSec.Cryptography.SignatureAlgorithm algorithm = NSec.Cryptography.SignatureAlgorithm.Ed25519;
        NSec.Cryptography.Key privateKey = NSec.Cryptography.Key.Create(algorithm);
        byte[] publicKey = privateKey.Export(NSec.Cryptography.KeyBlobFormat.RawPublicKey);

        UserSigningKey signingKey = new()
        {
            UserId = userId,
            TenantId = tenantId,
            Label = $"Self Hosted Key {Guid.NewGuid():N}",
            PublicKey = Convert.ToBase64String(publicKey),
            PublicKeyFingerprint = Convert.ToHexStringLower(SHA256.HashData(publicKey)),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        signingKey.Id = await db.InsertWithInt32IdentityAsync(signingKey);

        return (signingKey.Id, privateKey);
    }

    private static HttpClient BuildClient(
        SelfHostedTestFactory factory,
        int tenantId,
        int userId,
        UserAccountRoles clientRole = UserAccountRoles.TenantAdmin,
        bool isGlobalAdmin = false)
    {
        AuthenticatedClientBuilder builder = new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithRole(tenantId, (int)clientRole)
            .WithActiveTenant(tenantId);

        if (isGlobalAdmin)
        {
            builder = builder.AsGlobalAdmin();
        }

        return builder.Build();
    }

    [Test]
    public async Task CancelSubscription_WhenSelfHosted_Returns404()
    {
        using SelfHostedTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenantWithSubscription(db);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsync("/api/v1/billing/cancel", null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task DowngradeSubscription_WhenSelfHosted_Returns404()
    {
        using SelfHostedTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenantWithSubscription(db);
        HttpClient client = BuildClient(factory, tenantId, userId);

        StringContent content = new(
            JsonSerializer.Serialize(new { targetTier = "free" }),
            Encoding.UTF8,
            "application/json");
        HttpResponseMessage response = await client.PostAsync("/api/v1/billing/downgrade", content);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task ResumeSubscription_WhenSelfHosted_Returns404()
    {
        using SelfHostedTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenantWithSubscription(db);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsync("/api/v1/billing/resume", null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task ReactivateSubscription_WhenSelfHosted_Returns404()
    {
        using SelfHostedTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenantWithSubscription(db);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsync("/api/v1/billing/reactivate", null);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// All four billing management endpoints in one place, so a future registration change that
    /// exposes any single one of them fails here rather than in only one of four separate tests.
    /// </summary>
    [Test]
    public async Task BillingManagementEndpoints_InSelfHosted_Return404()
    {
        using SelfHostedTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenantWithSubscription(db, SubscriptionTier.Free);
        HttpClient client = BuildClient(factory, tenantId, userId);

        StringContent downgrade = new(
            JsonSerializer.Serialize(new { targetTier = "free" }),
            Encoding.UTF8,
            "application/json");

        (string Path, HttpResponseMessage Response)[] responses =
        [
            ("/api/v1/billing/cancel", await client.PostAsync("/api/v1/billing/cancel", null)),
            ("/api/v1/billing/downgrade", await client.PostAsync("/api/v1/billing/downgrade", downgrade)),
            ("/api/v1/billing/resume", await client.PostAsync("/api/v1/billing/resume", null)),
            ("/api/v1/billing/reactivate", await client.PostAsync("/api/v1/billing/reactivate", null)),
        ];

        foreach ((string path, HttpResponseMessage response) in responses)
        {
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound)
                .Because($"{path} manages a Stripe subscription and has no meaning in a self-hosted deployment");
        }
    }

    /// <summary>
    /// The read endpoints whose entire payload comes from Stripe. Without a guard they answer 200
    /// with empty or zeroed data, which reads as "your account has no invoices" rather than "this
    /// product has no billing". Grouped so that exposing any one of them fails here.
    /// </summary>
    [Test]
    public async Task BillingReadEndpoints_InSelfHosted_Return404()
    {
        using SelfHostedTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenantWithSubscription(db, SubscriptionTier.Free);
        HttpClient client = BuildClient(factory, tenantId, userId, UserAccountRoles.Viewer);

        string[] paths =
        [
            "/api/v1/billing/catalog",
            "/api/v1/billing/invoices",
            "/api/v1/billing/upcoming-invoice",
            "/api/v1/billing/usage-history",
        ];

        foreach (string path in paths)
        {
            HttpResponseMessage response = await client.GetAsync(path);

            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound)
                .Because($"{path} serves Stripe data that does not exist in a self-hosted deployment");
        }
    }

    /// <summary>
    /// The subscription read endpoint is unguarded on purpose: the web layout fetches it on every
    /// page load, so a blanket 404 across the billing routes would break the application shell.
    /// It sits beside the 404 tests above so the exclusion reads as deliberate rather than missed.
    /// </summary>
    [Test]
    public async Task GetBillingSubscription_InSelfHosted_StillReturns200()
    {
        using SelfHostedTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenantWithSubscription(db, SubscriptionTier.Free);

        HttpClient client = BuildClient(factory, tenantId, userId, UserAccountRoles.Viewer);

        HttpResponseMessage response = await client.GetAsync("/api/v1/billing/subscription");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        bool success = root.GetProperty("success").GetBoolean();
        await Assert.That(success).IsTrue();

        // The stored row is still Free; the self-hosted entitlement view reports the synthetic
        // top tier so the tier checks spread across the endpoints all pass.
        string tier = root.GetProperty("data").GetProperty("tier").GetString()!;
        await Assert.That(tier).IsEqualTo("Team");
    }

    [Test]
    public async Task EffectiveLimits_InSelfHosted_AreUnlimited()
    {
        using SelfHostedTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenantWithSubscription(db, SubscriptionTier.Free);

        HttpClient client = BuildClient(factory, tenantId, userId, UserAccountRoles.Viewer);

        HttpResponseMessage response = await client.GetAsync("/api/v1/billing/subscription");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement data = doc.RootElement.GetProperty("data");

        await Assert.That(data.GetProperty("machineLimit").GetInt32()).IsEqualTo(int.MaxValue);
        await Assert.That(data.GetProperty("alertRuleLimit").GetInt32()).IsEqualTo(int.MaxValue);
        await Assert.That(data.GetProperty("webhookLimit").GetInt32()).IsEqualTo(int.MaxValue);

        // Retention is capped at the widest class the partitioning scheme supports rather than
        // being unlimited, because there is no unlimited retention class.
        await Assert.That(data.GetProperty("retentionDays").GetInt32())
            .IsEqualTo(RetentionClassPolicy.LongWindowDays);

        // The member limit does not travel on this payload, so it is asserted where it lives.
        using IServiceScope scope = factory.Services.CreateScope();
        ISubscriptionService subscriptions = scope.ServiceProvider.GetRequiredService<ISubscriptionService>();
        EffectiveLimits limits = await subscriptions.GetEffectiveLimitsForTenantAsync(tenantId, CancellationToken.None);

        await Assert.That(limits.MemberLimit).IsEqualTo(int.MaxValue);
    }

    /// <summary>
    /// Covers both the Team-tier gate and the alert-rule limit predicate: the Free row allows zero
    /// custom rules, so a delegated limit check would refuse this request.
    /// </summary>
    [Test]
    public async Task AlertRuleCreate_OnFreeRow_Succeeds()
    {
        using SelfHostedTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenantWithSubscription(db, SubscriptionTier.Free);
        long machineId = await SeedMachine(db, tenantId);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/alert-rules", new
        {
            Name = "Self hosted CPU rule",
            Metric = "CpuUsage",
            Operator = "GreaterThan",
            Threshold = 90,
            DurationMinutes = 5,
            Severity = "Warning",
            MachineIds = new long[] { machineId },
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("\"success\":true");
    }

    /// <summary>
    /// The Free row's webhook limit is zero, so a delegated webhook predicate would refuse this.
    /// </summary>
    [Test]
    public async Task IntegrationCreate_OnFreeRow_Succeeds()
    {
        using SelfHostedTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenantWithSubscription(db, SubscriptionTier.Free);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/integrations", new
        {
            provider = "Custom",
            name = "Self hosted webhook",
            configuration = new Dictionary<string, string>
            {
                ["url"] = "https://relay.example.com/webhook"
            }
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("\"success\":true");
    }

    [Test]
    public async Task MachineAuthorizedKeyAdd_OnFreeRow_Succeeds()
    {
        using SelfHostedTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenantWithSubscription(db, SubscriptionTier.Free);
        long machineId = await SeedMachine(db, tenantId);
        (int signingKeyId, NSec.Cryptography.Key privateKey) = await SeedSigningKey(db, tenantId, userId);
        privateKey.Dispose();

        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/api/v1/machines/{machineId}/authorized-keys",
            new { SigningKeyId = signingKeyId });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("\"success\":true");
    }

    [Test]
    public async Task CommandSend_OnFreeRow_Succeeds()
    {
        using SelfHostedTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenantWithSubscription(db, SubscriptionTier.Free);
        long machineId = await SeedMachine(db, tenantId);
        (int signingKeyId, NSec.Cryptography.Key privateKey) = await SeedSigningKey(db, tenantId, userId);

        using (privateKey)
        {
            await db.InsertWithInt32IdentityAsync(new MachineAuthorizedKey
            {
                MachineId = machineId,
                SigningKeyId = signingKeyId,
                TenantId = tenantId,
                AuthorizedAt = DateTimeOffset.UtcNow,
                AuthorizedByUserId = userId,
            });

            IMachinePingService pingService = factory.Services.GetRequiredService<IMachinePingService>();
            await pingService.SetAgentCapabilitiesAsync(machineId, 1UL);

            HttpClient client = BuildClient(factory, tenantId, userId, UserAccountRoles.MachineAdmin);

            string payload = "{}";
            byte[] signature = NSec.Cryptography.SignatureAlgorithm.Ed25519.Sign(
                privateKey, Encoding.UTF8.GetBytes(payload));

            HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/commands", new
            {
                CommandId = Guid.NewGuid().ToString("D"),
                MachineId = machineId,
                SigningKeyId = signingKeyId,
                CommandType = "reboot",
                Nonce = Guid.NewGuid().ToString("N"),
                Signature = Convert.ToBase64String(signature),
                CanonicalPayload = payload,
                Timestamp = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
            });

            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
            string body = await response.Content.ReadAsStringAsync();
            await Assert.That(body).Contains("\"success\":true");
        }
    }

    [Test]
    public async Task AuditLogList_OnFreeRow_Succeeds()
    {
        using SelfHostedTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenantWithSubscription(db, SubscriptionTier.Free);

        await db.InsertAsync(new AuditLogEntry
        {
            TenantId = tenantId,
            UserId = userId,
            Action = AuditAction.UserLogin,
            ResourceType = AuditResourceType.User,
            Timestamp = DateTimeOffset.UtcNow,
        });

        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.GetAsync("/api/v1/audit-log");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("\"success\":true");
        await Assert.That(body).Contains("\"totalCount\":1");
    }

    /// <summary>
    /// In the hosted deployment every non-Team tier has the invitee's role forced to TenantAdmin.
    /// A self-hosted deployment has no tiers, so the requested role must survive.
    /// </summary>
    [Test]
    public async Task Invitation_OnFreeRow_SucceedsAndHonoursRequestedRole()
    {
        using SelfHostedTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenantWithSubscription(db, SubscriptionTier.Free);
        HttpClient client = BuildClient(factory, tenantId, userId);

        string inviteeEmail = $"invitee-{Guid.NewGuid():N}@example.com";

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/invitations", new
        {
            Email = inviteeEmail,
            Role = "Viewer",
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        TenantInvitation? invitation = await db.TenantInvitations
            .FirstOrDefaultAsync(i => i.Email == inviteeEmail);

        await Assert.That(invitation).IsNotNull();
        await Assert.That(invitation!.Role).IsEqualTo(UserAccountRoles.Viewer);
    }

    [Test]
    public async Task UpdateAdminSettings_InSelfHosted_Succeeds()
    {
        using SelfHostedTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenantWithSubscription(db, SubscriptionTier.Free, isGlobalAdmin: true);

        await db.InsertAsync(new ServerConfigurationSettings
        {
            Key = ServerConfigurationSettingKeys.AgentHeartbeatSeconds,
            Value = "300",
            Version = 1,
        });

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

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("\"success\":true");
    }

    [Test]
    public async Task GetAdminSettings_InSelfHosted_Succeeds()
    {
        using SelfHostedTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenantWithSubscription(db, SubscriptionTier.Free, isGlobalAdmin: true);
        HttpClient client = BuildClient(factory, tenantId, userId, isGlobalAdmin: true);

        HttpResponseMessage response = await client.GetAsync("/api/v1/admin/settings");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("\"success\":true");
    }

    [Test]
    public async Task GetAdminUsers_InSelfHosted_Succeeds()
    {
        using SelfHostedTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenantWithSubscription(db, SubscriptionTier.Free, isGlobalAdmin: true);
        HttpClient client = BuildClient(factory, tenantId, userId, isGlobalAdmin: true);

        HttpResponseMessage response = await client.GetAsync("/api/v1/admin/users");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("\"success\":true");
    }

    [Test]
    public async Task GetTenants_AsGlobalAdminInSelfHosted_ReturnsEveryTenant()
    {
        // The fleet-local admin console is the whole point of this deployment, and its tenant tab
        // reads this route. Scoping it away here would empty that tab.
        using SelfHostedTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenantWithSubscription(db, SubscriptionTier.Free, isGlobalAdmin: true);
        (int otherTenantId, _) = await SeedTenantWithSubscription(db, SubscriptionTier.Free);
        HttpClient client = BuildClient(factory, tenantId, userId, isGlobalAdmin: true);

        HttpResponseMessage response = await client.GetAsync("/api/v1/tenants");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();

        await Assert.That(body).Contains($"\"id\":{tenantId}");
        await Assert.That(body).Contains($"\"id\":{otherTenantId}");
    }
}
