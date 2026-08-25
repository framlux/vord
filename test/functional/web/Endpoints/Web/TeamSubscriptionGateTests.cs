// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using LinqToDB.Async;

namespace Framlux.FleetManagement.FunctionalTest.Endpoints.Web;

/// <summary>
/// The SaaS half of the Team gate. Every Team-only feature must keep refusing a non-Team tenant
/// after the gate moved from hand-written checks in each handler to the declarative tag.
/// </summary>
/// <remarks>
/// <para>
/// The failure this exists to catch is silent and fails <b>open</b>: a tag is inert metadata, so a
/// <c>TeamSubscriptionPreProcessor</c> that is never registered in the FastEndpoints configurator
/// leaves every converted endpoint available to every tier, with nothing failing to compile and no
/// error logged.
/// </para>
/// <para>
/// The fixtures are chosen to discriminate, which the obvious ones do not. Alert-rule create and
/// update are <i>also</i> Pro-tagged, so a Free-row tenant is refused by the Pro gate whether or
/// not the Team gate exists — a Free-row test on those two would pass with the Team pre-processor
/// unregistered and prove nothing. They use a Pro/Active row, which clears the Pro gate and leaves
/// only the Team gate able to refuse. The three endpoints carrying no other gate use a Free row.
/// </para>
/// </remarks>
public sealed class TeamSubscriptionGateTests
{
    private static async Task<(int TenantId, int UserId)> SeedTenant(
        DatabaseContext db,
        SubscriptionTier tier)
    {
        Tenant tenant = new()
        {
            ExternalId = Guid.NewGuid().ToString("N"),
            Name = $"Team Gate Tenant {Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 1,
            IsActive = true,
            LogoUrl = "",
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
            ExternalId = $"ext-team-gate-{Guid.NewGuid():N}",
            Username = $"team-gate-{Guid.NewGuid():N}@example.com",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 1,
            IsActive = true,
            IsSystem = false,
            IsGlobalAdmin = false,
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
            Name = $"team-gate-machine-{Guid.NewGuid():N}",
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

    private static HttpClient BuildClient(FunctionalTestFactory factory, int tenantId, int userId)
    {
        return new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithEmail($"user-{userId}@example.com")
            .WithRole(tenantId, (int)UserAccountRoles.TenantAdmin)
            .WithActiveTenant(tenantId)
            .Build();
    }

    /// <summary>
    /// A structurally valid alert rule, so request validation cannot be what refuses it.
    /// </summary>
    private static object BuildAlertRule(long machineId)
    {
        return new
        {
            Name = "High CPU",
            Metric = "CpuUsage",
            Operator = "GreaterThan",
            Threshold = 90,
            DurationMinutes = 5,
            Severity = "Warning",
            MachineIds = new[] { machineId },
        };
    }

    /// <summary>
    /// A structurally valid command. It never executes — the gate refuses it first — but keeping it
    /// valid means a 403 assertion cannot be satisfied by a malformed payload instead of the tier.
    /// </summary>
    private static object BuildCommand()
    {
        return new
        {
            CommandId = Guid.NewGuid().ToString("D"),
            MachineId = 1L,
            SigningKeyId = 1,
            CommandType = "reboot",
            Nonce = Guid.NewGuid().ToString("N"),
            Signature = Convert.ToBase64String("not-a-real-signature"u8.ToArray()),
            CanonicalPayload = "{}",
            Timestamp = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
        };
    }

    private static async Task<string> ReadMessage(HttpResponseMessage response)
    {
        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);

        return doc.RootElement.GetProperty("message").GetString() ?? string.Empty;
    }

    /// <summary>
    /// The discriminating case for alert-rule create: a Pro tenant clears the Pro gate, so a 403
    /// here can only have come from the Team gate.
    /// </summary>
    [Test]
    public async Task AlertRuleCreate_ProTenant_IsRefusedByTheTeamGate()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenant(db, SubscriptionTier.Pro);
        long machineId = await SeedMachine(db, tenantId);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/alert-rules", BuildAlertRule(machineId));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(await ReadMessage(response))
            .IsEqualTo("Custom alert rules require a Team subscription");
    }

    /// <summary>
    /// A Free tenant on the same endpoint must still see the <i>Pro</i> message, because the Pro
    /// gate runs first. This pins the pre-processor ordering: registering the Team gate ahead of
    /// the Pro one would silently change which message this tenant receives.
    /// </summary>
    [Test]
    public async Task AlertRuleCreate_FreeTenant_StillSeesTheProMessage()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenant(db, SubscriptionTier.Free);
        long machineId = await SeedMachine(db, tenantId);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/alert-rules", BuildAlertRule(machineId));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(await ReadMessage(response))
            .IsEqualTo("Alerting requires a Pro or Team subscription");
    }

    [Test]
    public async Task MachineAuthorizedKeyAdd_FreeTenant_IsRefused()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenant(db, SubscriptionTier.Free);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/machines/1/authorized-keys",
            new { SigningKeyId = 1 });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(await ReadMessage(response))
            .IsEqualTo("Remote commands require a Team subscription");
    }

    [Test]
    public async Task CommandSend_FreeTenant_IsRefused()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenant(db, SubscriptionTier.Free);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/commands", BuildCommand());

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(await ReadMessage(response))
            .IsEqualTo("Remote commands require a Team subscription");
    }

    [Test]
    public async Task AuditLogList_FreeTenant_IsRefused()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenant(db, SubscriptionTier.Free);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.GetAsync("/api/v1/audit-log");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(await ReadMessage(response))
            .IsEqualTo("Audit log requires a Team subscription");
    }

    /// <summary>
    /// A Team tenant passes the gate. Without this the suite would still be satisfied by a
    /// pre-processor that refused everyone.
    /// </summary>
    [Test]
    public async Task AuditLogList_TeamTenant_IsAllowedThrough()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenant(db, SubscriptionTier.Team);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.GetAsync("/api/v1/audit-log");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    /// <summary>
    /// Records where the gate sits relative to request validation, measured rather than assumed.
    /// </summary>
    /// <remarks>
    /// Global pre-processors run <b>before</b> request validation here, so a non-Team tenant sending
    /// an invalid body now gets 403 rather than the 400 it got while the check lived in the handler.
    /// That is a deliberate, observed change and it converges on the existing pattern: the Pro-tagged
    /// endpoints have always answered 403 to an invalid body from an ungated tenant. The tier is not
    /// a secret, so reporting it before validating the payload leaks nothing.
    /// </remarks>
    [Test]
    public async Task CommandSend_FreeTenantWithInvalidBody_IsRefusedByTheGateBeforeValidation()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenant(db, SubscriptionTier.Free);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/commands", new
        {
            CommandId = "not-a-uuid",
            MachineId = 0L,
            SigningKeyId = 0,
            CommandType = "",
            Nonce = "short",
            Signature = "!!!not-base64!!!",
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }
}
