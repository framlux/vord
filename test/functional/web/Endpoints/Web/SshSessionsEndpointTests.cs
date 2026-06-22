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
            CreatedByUserId = 1,
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

        string sessionsJson = """[{"user":"root","source_ip":"10.0.0.1","action":"connect","auth_method":"publickey","timestamp":"2026-01-01T00:00:00Z"}]""";
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

        string sessions1 = """[{"user":"root","source_ip":"10.0.0.1","action":"connect","auth_method":"publickey","timestamp":"2026-01-01T00:00:00Z"}]""";
        string sessions2 = """[{"user":"admin","source_ip":"10.0.0.2","action":"connect","auth_method":"password","timestamp":"2026-01-01T00:00:00Z"}]""";
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
    public async Task SshSessions_SearchByMachineName_OnlyMatchesMachineName()
    {
        // Search is resolved to a machine-id set against machine names BEFORE the SQL query so it
        // can be pushed down as a predicate. A term that only matches a username inside the JSON
        // payload (and no machine name) therefore returns no rows — this documents the bounded
        // behavior that replaced the previous load-everything-then-filter-in-memory path.
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedSshEnvironment(db);

        string sessions = """[{"user":"root","source_ip":"10.0.0.1","action":"connect","auth_method":"publickey","timestamp":"2026-01-01T00:00:00Z"},{"user":"deploy","source_ip":"10.0.0.2","action":"connect","auth_method":"publickey","timestamp":"2026-01-01T01:00:00Z"}]""";
        await SeedMachineWithSessions(db, tenantId, "server-1", sessions);

        HttpClient client = BuildClient(factory, tenantId, userId);

        // "root" matches a username but no machine name → empty result.
        HttpResponseMessage userSearch = await client.GetAsync("/api/v1/machines/ssh-sessions?Search=root");
        string userBody = await userSearch.Content.ReadAsStringAsync();
        await Assert.That(userBody).Contains("\"totalCount\":0");

        // "server" matches the machine name → both that machine's sessions are returned.
        HttpResponseMessage nameSearch = await client.GetAsync("/api/v1/machines/ssh-sessions?Search=server");
        string nameBody = await nameSearch.Content.ReadAsStringAsync();
        await Assert.That(nameBody).Contains("\"totalCount\":2");
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
        string goodSessions = """[{"user":"admin","source_ip":"10.0.0.3","action":"connect","auth_method":"publickey","timestamp":"2026-01-01T00:00:00Z"}]""";
        await SeedMachineWithSessions(db, tenantId, "good-machine", goodSessions);

        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.GetAsync("/api/v1/machines/ssh-sessions");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        // The malformed row is skipped from the returned items but is still counted by the
        // bounded SQL TotalCount (the row exists). The good machine must appear, the bad must not.
        await Assert.That(body).Contains("good-machine");
        await Assert.That(body.Contains("bad-machine")).IsFalse();
    }

    [Test]
    public async Task SshSessions_Pagination_ReturnsRequestedPageWithFullTotalCount()
    {
        // Seed more rows than a page holds and assert the page is bounded by pageSize while
        // TotalCount reflects the whole (bounded) result set computed in SQL.
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedSshEnvironment(db);

        long machineId = await SeedMachineWithSessions(db, tenantId, "paged-machine", null);
        for (int i = 0; i < 7; i++)
        {
            MachineTelemetry telemetry = new()
            {
                MachineId = machineId,
                TenantId = tenantId,
                TelemetryType = 9,
                Payload = $$"""{"user":"u{{i}}","source_ip":"10.0.0.{{i}}","action":"connect","auth_method":"publickey","timestamp":"2026-01-01T00:0{{i}}:00Z"}""",
                ReceivedAt = DateTimeOffset.UtcNow.AddMinutes(-i),
            };
            await db.InsertWithInt64IdentityAsync(telemetry);
        }

        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage page1 = await client.GetAsync("/api/v1/machines/ssh-sessions?Page=1&PageSize=3");
        string body1 = await page1.Content.ReadAsStringAsync();
        await Assert.That(page1.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(body1).Contains("\"totalCount\":7");
        await Assert.That(body1).Contains("\"pageSize\":3");

        // Page 3 holds the remaining single row (7 = 3 + 3 + 1).
        HttpResponseMessage page3 = await client.GetAsync("/api/v1/machines/ssh-sessions?Page=3&PageSize=3");
        string body3 = await page3.Content.ReadAsStringAsync();
        await Assert.That(body3).Contains("\"totalCount\":7");
        await Assert.That(body3).Contains("\"page\":3");
    }

    [Test]
    public async Task SshSessions_RowsOlderThanRetentionWindow_AreExcluded()
    {
        // The query is bounded by the tenant's retention window. A row received long before the
        // window opened must not appear and must not inflate TotalCount.
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        (int tenantId, int userId) = await SeedSshEnvironment(db);

        long machineId = await SeedMachineWithSessions(db, tenantId, "retention-machine", null);

        // One recent row (within any retention window) and one very old row (10 years back).
        await db.InsertWithInt64IdentityAsync(new MachineTelemetry
        {
            MachineId = machineId,
            TenantId = tenantId,
            TelemetryType = 9,
            Payload = """{"user":"recent","source_ip":"10.0.0.1","action":"connect","auth_method":"publickey","timestamp":"2026-06-15T00:00:00Z"}""",
            ReceivedAt = DateTimeOffset.UtcNow,
        });
        await db.InsertWithInt64IdentityAsync(new MachineTelemetry
        {
            MachineId = machineId,
            TenantId = tenantId,
            TelemetryType = 9,
            Payload = """{"user":"ancient","source_ip":"10.0.0.2","action":"connect","auth_method":"publickey","timestamp":"2016-01-01T00:00:00Z"}""",
            ReceivedAt = DateTimeOffset.UtcNow.AddYears(-10),
        });

        HttpClient client = BuildClient(factory, tenantId, userId);

        HttpResponseMessage response = await client.GetAsync("/api/v1/machines/ssh-sessions");
        string body = await response.Content.ReadAsStringAsync();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(body).Contains("\"totalCount\":1");
        await Assert.That(body).Contains("recent");
        await Assert.That(body.Contains("ancient")).IsFalse();
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
