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
/// only the Team gate able to refuse. The audit log endpoint carries no other gate and uses a Free row.
/// </para>
/// <para>
/// Command send is the exception to the tag: whether it needs Team depends on the command type, which
/// is in the request body, so its handler makes the check after validation. Its tests are below and
/// pin both the type rule and the order.
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

    private static async Task<int> SeedSigningKey(DatabaseContext db, int tenantId, int userId)
    {
        byte[] keyBytes = new byte[32];
        Random.Shared.NextBytes(keyBytes);

        UserSigningKey signingKey = new()
        {
            UserId = userId,
            TenantId = tenantId,
            Label = $"Team Gate Key {Guid.NewGuid():N}",
            PublicKey = Convert.ToBase64String(keyBytes),
            PublicKeyFingerprint = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(keyBytes)),
            CreatedAt = DateTimeOffset.UtcNow,
        };

        return await db.InsertWithInt32IdentityAsync(signingKey);
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
    /// A structurally valid command. The signing key it names does not exist, so a request that gets past
    /// the Team check is answered by the command service with a 400 about the key, while one the check
    /// refuses is answered with a 403 before the service is asked. Keeping the body valid means neither
    /// answer can come from request validation instead.
    /// </summary>
    private static object BuildCommand(string commandType = "reboot")
    {
        return new
        {
            CommandId = Guid.NewGuid().ToString("D"),
            MachineId = 1L,
            SigningKeyId = 1,
            CommandType = commandType,
            Nonce = Guid.NewGuid().ToString("N"),
            Signature = Convert.ToBase64String("not-a-real-signature"u8.ToArray()),
            CanonicalPayload = TestCanonicalPayload.Build(commandType),
            Timestamp = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
        };
    }

    private const string TeamMessage = "Remote commands require a Team subscription";
    private const string UnknownSigningKeyMessage = "Signing key not found or does not belong to tenant";

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
    [Arguments(SubscriptionTier.Free)]
    [Arguments(SubscriptionTier.Pro)]
    public async Task MachineAuthorizedKeyAdd_BelowTeam_IsAllowedThrough(SubscriptionTier tier)
    {
        // Intent: authorising a key is no longer a Team feature. It grants nothing on its own, because
        // sending a command still faces the per-type check, so a tenant below Team can authorise the key it
        // needs for the cluster reporter commands.
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenant(db, tier);
        long machineId = await SeedMachine(db, tenantId);
        int signingKeyId = await SeedSigningKey(db, tenantId, userId);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/api/v1/machines/{machineId}/authorized-keys",
            new { SigningKeyId = signingKeyId });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    [Arguments(SubscriptionTier.Free, "reboot")]
    [Arguments(SubscriptionTier.Free, "kill_process")]
    [Arguments(SubscriptionTier.Free, "kill_session")]
    [Arguments(SubscriptionTier.Free, "check_updates")]
    [Arguments(SubscriptionTier.Free, "install_updates")]
    [Arguments(SubscriptionTier.Pro, "reboot")]
    [Arguments(SubscriptionTier.Pro, "kill_process")]
    [Arguments(SubscriptionTier.Pro, "kill_session")]
    [Arguments(SubscriptionTier.Pro, "check_updates")]
    [Arguments(SubscriptionTier.Pro, "install_updates")]
    public async Task CommandSend_EveryOtherCommandBelowTeam_IsRefusedWithTheTeamMessageBeforeTheServiceIsAsked(SubscriptionTier tier, string commandType)
    {
        // Intent: only the three cluster reporter commands are open to every tier. The signing key here does
        // not exist, so a 403 proves the check ran ahead of the service, which would have answered 400.
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenant(db, tier);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/commands", BuildCommand(commandType));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(await ReadMessage(response)).IsEqualTo(TeamMessage);
    }

    [Test]
    [Arguments(SubscriptionTier.Free, "K8S_REPORTER_ENROL")]
    [Arguments(SubscriptionTier.Free, "k8s_reporter_Unenrol")]
    [Arguments(SubscriptionTier.Free, "K8s_Reporter_Uninstall")]
    [Arguments(SubscriptionTier.Free, "k8s_reporter_enrol ")]
    [Arguments(SubscriptionTier.Pro, "K8S_REPORTER_ENROL")]
    [Arguments(SubscriptionTier.Pro, "k8s_reporter_Unenrol")]
    [Arguments(SubscriptionTier.Pro, "K8s_Reporter_Uninstall")]
    [Arguments(SubscriptionTier.Pro, "k8s_reporter_enrol ")]
    public async Task CommandSend_NonLowercaseSpellingOfAReporterCommandBelowTeam_IsRefusedWithTheTeamMessage(SubscriptionTier tier, string commandType)
    {
        // Intent: the agent matches the type exactly, so a spelling that is not exactly one of the three
        // is not a reporter command and must not borrow their exemption from the Team check.
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenant(db, tier);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/commands", BuildCommand(commandType));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(await ReadMessage(response)).IsEqualTo(TeamMessage);
    }

    [Test]
    [Arguments(SubscriptionTier.Free, "k8s_reporter_enrol")]
    [Arguments(SubscriptionTier.Free, "k8s_reporter_unenrol")]
    [Arguments(SubscriptionTier.Free, "k8s_reporter_uninstall")]
    [Arguments(SubscriptionTier.Pro, "k8s_reporter_enrol")]
    [Arguments(SubscriptionTier.Pro, "k8s_reporter_unenrol")]
    [Arguments(SubscriptionTier.Pro, "k8s_reporter_uninstall")]
    public async Task CommandSend_ReporterCommandBelowTeam_PassesTheGateAndReachesTheService(SubscriptionTier tier, string commandType)
    {
        // Intent: cluster reporting works on every tier, so the three reporter commands are not stopped by
        // the Team check. The service answers 400 about the signing key, which does not exist here, and that
        // answer can only come from the service.
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenant(db, tier);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/commands", BuildCommand(commandType));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await ReadMessage(response)).IsEqualTo(UnknownSigningKeyMessage);
    }

    [Test]
    [Arguments(SubscriptionTier.Team, "reboot")]
    [Arguments(SubscriptionTier.Team, "k8s_reporter_enrol")]
    [Arguments(SubscriptionTier.Enterprise, "reboot")]
    [Arguments(SubscriptionTier.Enterprise, "kill_process")]
    [Arguments(SubscriptionTier.Enterprise, "k8s_reporter_enrol")]
    public async Task CommandSend_TierWithTeamFeatures_PassesTheGateForEveryCommand(SubscriptionTier tier, string commandType)
    {
        // Intent: without this the suite would still pass for a check that refused every tenant. Enterprise
        // has every Team feature without being the Team tier, so it also catches a check written as a
        // comparison with the Team tier instead of the Team-features predicate.
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenant(db, tier);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/commands", BuildCommand(commandType));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(await ReadMessage(response)).IsEqualTo(UnknownSigningKeyMessage);
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
    /// Pins where the command Team check sits relative to request validation: after it. The check lives in
    /// the handler because it needs the command type, so a request that fails validation is answered with
    /// the validation error whatever the tenant's tier, and only a valid request is then judged by tier.
    /// </summary>
    [Test]
    public async Task CommandSend_FreeTenantWithInvalidBody_GetsTheValidationErrorNotTheTeamRefusal()
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
            CommandType = "reboot",
            Nonce = "short",
            Signature = "!!!not-base64!!!",
            CanonicalPayload = "{}",
            Timestamp = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
        });

        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(body).Contains("Command ID must be a valid UUID");
        await Assert.That(body).DoesNotContain("Team subscription");
    }

    [Test]
    public async Task CommandSend_ProTenantWithMalformedParams_GetsTheParamsValidationErrorNotTheTeamRefusal()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedTenant(db, SubscriptionTier.Pro);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/commands", new
        {
            CommandId = Guid.NewGuid().ToString("D"),
            MachineId = 1L,
            SigningKeyId = 1,
            CommandType = "reboot",
            Params = """{"pid":42}""",
            Nonce = Guid.NewGuid().ToString("N"),
            Signature = Convert.ToBase64String("not-a-real-signature"u8.ToArray()),
            CanonicalPayload = TestCanonicalPayload.Build("reboot"),
            Timestamp = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
        });

        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(body).Contains("Params must be a JSON object of string values");
    }
}
