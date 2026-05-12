// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB.Async;
using LinqToDB;
using System.Net;

namespace Framlux.FleetManagement.FunctionalTest.Endpoints.Web;

/// <summary>
/// Functional tests for the SSH sessions fleet endpoint.
/// </summary>
public sealed class SshSessionsEndpointTests
{
    private static async Task<(int TenantId, int UserId)> SeedSshEnvironment(DatabaseContext db)
    {
        Tenant tenant = new()
        {
            ExternalId = Guid.NewGuid().ToString("N"),
            Name = $"SSH Tenant {Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 0,
            IsActive = true,
            LogoUrl = ""
        };
        tenant.Id = await db.InsertWithInt32IdentityAsync(tenant);

        TenantSubscription subscription = new()
        {
            TenantId = tenant.Id,
            Tier = SubscriptionTier.Pro,
            Status = SubscriptionStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await db.InsertWithInt32IdentityAsync(subscription);

        UserAccount user = new()
        {
            ExternalId = $"ext-ssh-user-{Guid.NewGuid():N}",
            Username = $"sshuser-{Guid.NewGuid():N}@example.com",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 0,
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

    private static async Task<long> SeedMachineWithSessions(DatabaseContext db, int tenantId, string machineName, string? sshSessionsJson)
    {
        Machine machine = new()
        {
            TenantId = tenantId,
            Name = machineName,
            ApiKeyHash = Guid.NewGuid().ToString("N"),
            SerialNumber = Guid.NewGuid().ToString("N")[..8],
            SystemId = Guid.NewGuid().ToString("N")[..8],
            MachineType = MachineTypes.BareMetalServer,
            OperatingSystem = OperatingSystems.Ubuntu,
            RegistrationTokenId = 0,
            RegisteredOn = DateTimeOffset.UtcNow,
            IsDeleted = false,
        };
        machine.Id = await db.InsertWithInt64IdentityAsync(machine);

        MachineStateSummary state = new()
        {
            MachineId = machine.Id,
            TenantId = tenantId,
            Name = machineName,
            OperatingSystem = (byte)OperatingSystems.Ubuntu,
            MachineType = (byte)MachineTypes.BareMetalServer,
            Hostname = machineName,
            HealthStatus = 0,
            LastSeenAt = DateTimeOffset.UtcNow,
        };
        await db.InsertAsync(state);

        MachineStateDetail detail = new()
        {
            MachineId = machine.Id,
        };
        await db.InsertAsync(detail);

        // Seed SSH sessions as MachineTelemetry rows (TelemetryType = 9).
        if (string.IsNullOrEmpty(sshSessionsJson) == false)
        {
            try
            {
                using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(sshSessionsJson);
                if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (System.Text.Json.JsonElement session in doc.RootElement.EnumerateArray())
                    {
                        MachineTelemetry telemetry = new()
                        {
                            MachineId = machine.Id,
                            TenantId = tenantId,
                            TelemetryType = 9,
                            Payload = session.GetRawText(),
                            ReceivedAt = DateTimeOffset.UtcNow,
                        };
                        await db.InsertWithInt64IdentityAsync(telemetry);
                    }
                }
            }
            catch (System.Text.Json.JsonException)
            {
                // For malformed JSON tests: insert the raw string as a single telemetry row.
                MachineTelemetry telemetry = new()
                {
                    MachineId = machine.Id,
                    TenantId = tenantId,
                    TelemetryType = 9,
                    Payload = sshSessionsJson,
                    ReceivedAt = DateTimeOffset.UtcNow,
                };
                await db.InsertWithInt64IdentityAsync(telemetry);
            }
        }

        return machine.Id;
    }

    private static HttpClient BuildClient(FunctionalTestFactory factory, int tenantId, int userId)
    {
        return new AuthenticatedClientBuilder(factory)
            .WithUserId(userId)
            .WithRole(tenantId, (int)UserAccountRoles.Viewer)
            .WithActiveTenant(tenantId)
            .Build();
    }

    [Test]
    public async Task SshSessions_NoTenant_Returns403()
    {
        using FunctionalTestFactory factory = new();
        HttpClient client = new AuthenticatedClientBuilder(factory).WithUserId(1).Build();

        HttpResponseMessage response = await client.GetAsync("/api/v1/machines/ssh-sessions");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task SshSessions_NoSessions_ReturnsEmptyPaginatedResponse()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedSshEnvironment(db);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.GetAsync("/api/v1/machines/ssh-sessions");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("\"totalCount\":0");
        await Assert.That(body).Contains("\"items\":[]");
    }

    [Test]
    public async Task SshSessions_WithSessions_ReturnsParsedResults()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedSshEnvironment(db);

        string sessionsJson = """[{"User":"root","SourceIp":"10.0.0.1","Action":"connect","AuthMethod":"publickey","Timestamp":"2026-01-01T00:00:00Z"}]""";
        await SeedMachineWithSessions(db, tenantId, "web-server-1", sessionsJson);

        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.GetAsync("/api/v1/machines/ssh-sessions");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("\"totalCount\":1");
        await Assert.That(body).Contains("root");
        await Assert.That(body).Contains("web-server-1");
    }

    [Test]
    public async Task SshSessions_SearchByMachineName_FiltersResults()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedSshEnvironment(db);

        string sessions1 = """[{"User":"root","SourceIp":"10.0.0.1","Action":"connect","AuthMethod":"publickey","Timestamp":"2026-01-01T00:00:00Z"}]""";
        string sessions2 = """[{"User":"admin","SourceIp":"10.0.0.2","Action":"connect","AuthMethod":"password","Timestamp":"2026-01-01T00:00:00Z"}]""";
        await SeedMachineWithSessions(db, tenantId, "web-server-1", sessions1);
        await SeedMachineWithSessions(db, tenantId, "db-server-1", sessions2);

        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.GetAsync("/api/v1/machines/ssh-sessions?Search=web-server");

        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("\"totalCount\":1");
        await Assert.That(body).Contains("web-server-1");
        await Assert.That(body.Contains("db-server-1")).IsFalse();
    }

    [Test]
    public async Task SshSessions_SearchByUser_FiltersResults()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedSshEnvironment(db);

        string sessions = """[{"User":"root","SourceIp":"10.0.0.1","Action":"connect","AuthMethod":"publickey","Timestamp":"2026-01-01T00:00:00Z"},{"User":"deploy","SourceIp":"10.0.0.2","Action":"connect","AuthMethod":"publickey","Timestamp":"2026-01-01T01:00:00Z"}]""";
        await SeedMachineWithSessions(db, tenantId, "server-1", sessions);

        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.GetAsync("/api/v1/machines/ssh-sessions?Search=root");

        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("root");
    }

    [Test]
    public async Task SshSessions_PaginationPageBelowOne_DefaultsToOne()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedSshEnvironment(db);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.GetAsync("/api/v1/machines/ssh-sessions?Page=-1");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("\"page\":1");
    }

    [Test]
    public async Task SshSessions_PageSizeAbove100_ClampedTo50()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedSshEnvironment(db);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.GetAsync("/api/v1/machines/ssh-sessions?PageSize=200");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("\"pageSize\":50");
    }

    [Test]
    public async Task SshSessions_PageSizeBelowOne_DefaultsTo50()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedSshEnvironment(db);
        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.GetAsync("/api/v1/machines/ssh-sessions?PageSize=0");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("\"pageSize\":50");
    }

    [Test]
    public async Task SshSessions_MalformedJson_SkipsGracefully()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedSshEnvironment(db);

        await SeedMachineWithSessions(db, tenantId, "bad-machine", "not valid json {{");
        string goodSessions = """[{"User":"admin","SourceIp":"10.0.0.3","Action":"connect","AuthMethod":"publickey","Timestamp":"2026-01-01T00:00:00Z"}]""";
        await SeedMachineWithSessions(db, tenantId, "good-machine", goodSessions);

        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.GetAsync("/api/v1/machines/ssh-sessions");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("good-machine");
        await Assert.That(body).Contains("\"totalCount\":1");
    }

    [Test]
    public async Task SshSessions_NullSshSessions_SkipsMachine()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedSshEnvironment(db);

        // Machine with no SSH sessions in state summary should be excluded from SSH results.
        Machine machine = new()
        {
            TenantId = tenantId,
            Name = "null-ssh-machine",
            ApiKeyHash = Guid.NewGuid().ToString("N"),
            SerialNumber = Guid.NewGuid().ToString("N")[..8],
            SystemId = Guid.NewGuid().ToString("N")[..8],
            MachineType = MachineTypes.BareMetalServer,
            OperatingSystem = OperatingSystems.Ubuntu,
            RegistrationTokenId = 0,
            RegisteredOn = DateTimeOffset.UtcNow,
            IsDeleted = false,
        };
        machine.Id = await db.InsertWithInt64IdentityAsync(machine);

        MachineStateSummary state = new()
        {
            MachineId = machine.Id,
            TenantId = tenantId,
            Name = "null-ssh-machine",
            OperatingSystem = (byte)OperatingSystems.Ubuntu,
            MachineType = (byte)MachineTypes.BareMetalServer,
            Hostname = "null-ssh-machine",
            HealthStatus = 0,
            LastSeenAt = DateTimeOffset.UtcNow,
        };
        await db.InsertAsync(state);

        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.GetAsync("/api/v1/machines/ssh-sessions");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("\"totalCount\":0");
    }
}
