// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using FastEndpoints;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;

namespace Framlux.FleetManagement.FunctionalTest.Endpoints.Web;

/// <summary>
/// Functional tests for the page-size contract shared by the machine collection endpoints.
/// A caller that asks for more than an endpoint will serve is refused rather than handed a
/// short page, because a silently reduced page reads exactly like the end of the collection.
/// </summary>
public sealed class MachinePageSizeContractTests
{
    private static async Task<int> SeedTenantWithSubscription(DatabaseContext db, SubscriptionTier tier = SubscriptionTier.Pro)
    {
        Tenant tenant = new()
        {
            ExternalId = Guid.NewGuid().ToString("N"),
            Name = $"PageSize Tenant {Guid.NewGuid():N}",
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

        return tenant.Id;
    }

    private static async Task SeedMachine(DatabaseContext db, int tenantId, string hostname)
    {
        Machine machine = new()
        {
            ApiKeyHash = Guid.NewGuid().ToString("N"),
            Name = hostname,
            SerialNumber = $"sn-{Guid.NewGuid():N}",
            SystemId = $"sid-{Guid.NewGuid():N}",
            MachineType = MachineTypes.BareMetalServer,
            OperatingSystem = OperatingSystems.Ubuntu,
            RegistrationTokenId = 0,
            RegisteredOn = DateTimeOffset.UtcNow,
            IsDeleted = false,
            TenantId = tenantId,
        };
        await db.InsertWithInt64IdentityAsync(machine);
    }

    private static HttpClient BuildAuthenticatedClient(FunctionalTestFactory factory, int tenantId)
    {
        return new AuthenticatedClientBuilder(factory)
            .WithUserId(1)
            .WithRole(tenantId, (int)UserAccountRoles.Viewer)
            .WithActiveTenant(tenantId)
            .Build();
    }

    private static HttpClient BuildAdminClient(FunctionalTestFactory factory, int tenantId)
    {
        // Authorisation runs before the handler, so a Viewer would be refused by the
        // registration-tokens endpoint before its page size was ever read.
        return new AuthenticatedClientBuilder(factory)
            .WithUserId(1)
            .WithRole(tenantId, (int)UserAccountRoles.TenantAdmin)
            .WithActiveTenant(tenantId)
            .Build();
    }

    // ========== Over the ceiling is refused, not quietly reduced ==========

    [Test]
    public async Task List_PageSizeAboveTheCeiling_Returns400()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        int tenantId = await SeedTenantWithSubscription(db);
        await SeedMachine(db, tenantId, "ceiling-list");

        HttpClient client = BuildAuthenticatedClient(factory, tenantId);

        HttpResponseMessage response = await client.GetAsync(
            $"/api/v1/machines?pageSize={PaginationLimits.MaxPageSize + 1}");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task Search_PageSizeAboveTheCeiling_Returns400()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        int tenantId = await SeedTenantWithSubscription(db);
        await SeedMachine(db, tenantId, "ceiling-search");

        HttpClient client = BuildAuthenticatedClient(factory, tenantId);

        HttpResponseMessage response = await client.GetAsync(
            $"/api/v1/machines/search?pageSize={PaginationLimits.MaxPageSize + 1}");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task List_PageSizeAboveTheCeiling_NamesTheCeilingInTheError()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        int tenantId = await SeedTenantWithSubscription(db);

        HttpClient client = BuildAuthenticatedClient(factory, tenantId);

        HttpResponseMessage response = await client.GetAsync("/api/v1/machines?pageSize=1000");

        // Assert the refusal before reading the body. A successful response echoes the served
        // page size, so a body-only assertion would pass against the very behaviour being removed.
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);

        string body = await response.Content.ReadAsStringAsync();

        // A caller who guessed 1000 has to learn the real number from the refusal, or the
        // refusal just moves the guessing game one step later.
        await Assert.That(body).Contains(PaginationLimits.MaxPageSize.ToString());
    }

    // ========== The boundary itself is servable ==========

    [Test]
    public async Task List_PageSizeAtTheCeiling_Returns200()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        int tenantId = await SeedTenantWithSubscription(db);
        await SeedMachine(db, tenantId, "at-ceiling-list");

        HttpClient client = BuildAuthenticatedClient(factory, tenantId);

        HttpResponseMessage response = await client.GetAsync(
            $"/api/v1/machines?pageSize={PaginationLimits.MaxPageSize}");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement data = doc.RootElement.GetProperty("data");
        await Assert.That(data.GetProperty("pageSize").GetInt32()).IsEqualTo(PaginationLimits.MaxPageSize);
    }

    [Test]
    public async Task Search_PageSizeAtTheCeiling_Returns200()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        int tenantId = await SeedTenantWithSubscription(db);
        await SeedMachine(db, tenantId, "at-ceiling-search");

        HttpClient client = BuildAuthenticatedClient(factory, tenantId);

        HttpResponseMessage response = await client.GetAsync(
            $"/api/v1/machines/search?pageSize={PaginationLimits.MaxPageSize}");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement data = doc.RootElement.GetProperty("data");
        await Assert.That(data.GetProperty("pageSize").GetInt32()).IsEqualTo(PaginationLimits.MaxPageSize);
    }

    // ========== Nonsense page sizes are refused too ==========

    [Test]
    public async Task List_PageSizeZero_Returns400()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        int tenantId = await SeedTenantWithSubscription(db);

        HttpClient client = BuildAuthenticatedClient(factory, tenantId);

        HttpResponseMessage response = await client.GetAsync("/api/v1/machines?pageSize=0");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task Search_PageSizeNegative_Returns400()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        int tenantId = await SeedTenantWithSubscription(db);

        HttpClient client = BuildAuthenticatedClient(factory, tenantId);

        HttpResponseMessage response = await client.GetAsync("/api/v1/machines/search?pageSize=-5");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    // ========== Omitting the parameter still works ==========

    [Test]
    public async Task List_NoPageSize_UsesTheDefault()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        int tenantId = await SeedTenantWithSubscription(db);
        await SeedMachine(db, tenantId, "default-list");

        HttpClient client = BuildAuthenticatedClient(factory, tenantId);

        HttpResponseMessage response = await client.GetAsync("/api/v1/machines");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement data = doc.RootElement.GetProperty("data");
        await Assert.That(data.GetProperty("pageSize").GetInt32()).IsEqualTo(PaginationLimits.DefaultPageSize);
    }

    // ========== A low page number is a floor, not a truncation ==========

    [Test]
    public async Task List_PageBelowOne_IsFlooredRatherThanRefused()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        int tenantId = await SeedTenantWithSubscription(db);
        await SeedMachine(db, tenantId, "floor-list");

        HttpClient client = BuildAuthenticatedClient(factory, tenantId);

        // Unlike a reduced page size, clamping the page to 1 cannot hide rows from the caller:
        // page 1 is where they would have started anyway.
        HttpResponseMessage response = await client.GetAsync("/api/v1/machines?page=-1");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement data = doc.RootElement.GetProperty("data");
        await Assert.That(data.GetProperty("page").GetInt32()).IsEqualTo(1);
    }

    // ========== The rule is the same on every paginated collection ==========

    [Test]
    public async Task DashboardFleet_PageSizeAboveTheCeiling_Returns400()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        int tenantId = await SeedTenantWithSubscription(db);

        HttpClient client = BuildAuthenticatedClient(factory, tenantId);

        HttpResponseMessage response = await client.GetAsync(
            $"/api/v1/dashboard/fleet?pageSize={PaginationLimits.MaxPageSize + 1}");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task DashboardFleet_PageSizeAtTheCeiling_Returns200()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        int tenantId = await SeedTenantWithSubscription(db);

        HttpClient client = BuildAuthenticatedClient(factory, tenantId);

        HttpResponseMessage response = await client.GetAsync(
            $"/api/v1/dashboard/fleet?pageSize={PaginationLimits.MaxPageSize}");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task RegistrationTokens_PageSizeAboveTheCeiling_Returns400()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        int tenantId = await SeedTenantWithSubscription(db);

        HttpClient client = BuildAdminClient(factory, tenantId);

        HttpResponseMessage response = await client.GetAsync(
            $"/api/v1/machines/registration-tokens?pageSize={PaginationLimits.MaxPageSize + 1}");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task CommandHistory_PageSizeAboveTheCeiling_Returns400()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        int tenantId = await SeedTenantWithSubscription(db);
        await SeedMachine(db, tenantId, "command-host");

        HttpClient client = BuildAuthenticatedClient(factory, tenantId);

        HttpResponseMessage response = await client.GetAsync(
            $"/api/v1/machines/1/commands?pageSize={PaginationLimits.MaxPageSize + 1}");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    // ========== Every page-size reader, discovered rather than listed ==========

    // The sweep used to enumerate five paths by hand, which is how three endpoints still clamping
    // their own way went unnoticed while the contract was declared finished. It now finds its
    // subjects: every endpoint whose source reads the page size through the shared rule — the
    // architecture test in unit.server guarantees there is no other way to read one — mapped to its
    // registered GET route. Discovery is by source because one reader, the command history, returns
    // a plain list and reads the query imperatively, so neither of its types says it paginates.

    private static readonly string[] KnownPageSizeReaders =
    [
        "MachineListEndpoint",
        "MachineSearchEndpoint",
        "DashboardFleetEndpoint",
        "ListRegistrationTokensEndpoint",
        "CommandListEndpoint",
        "AlertEventListEndpoint",
        "AuditLogListEndpoint",
        "SshSessionsFleetEndpoint",
    ];

    private static string FindRepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "machine-info.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate machine-info.slnx walking up from " + AppContext.BaseDirectory);
    }

    private static HashSet<string> EndpointClassesUsingTheSharedRule()
    {
        string endpoints = Path.Combine(FindRepoRoot(), "src", "server", "Endpoints");
        Regex endpointClass = new(@"\bclass\s+(\w+Endpoint)\b", RegexOptions.Compiled);
        HashSet<string> names = new(StringComparer.Ordinal);

        foreach (string path in Directory.EnumerateFiles(endpoints, "*.cs", SearchOption.AllDirectories))
        {
            string source = File.ReadAllText(path);
            if (source.Contains("PageSizeQuery.TryResolve", StringComparison.Ordinal) == false)
            {
                continue;
            }

            foreach (Match match in endpointClass.Matches(source))
            {
                names.Add(match.Groups[1].Value);
            }
        }

        return names;
    }

    private static List<string> GetRoutesFor(FunctionalTestFactory factory, HashSet<string> endpointNames)
    {
        EndpointDataSource source = factory.Services.GetRequiredService<EndpointDataSource>();
        Regex routeParameter = new(@"\{[^}]+\}", RegexOptions.Compiled);
        List<string> routes = [];

        foreach (RouteEndpoint endpoint in source.Endpoints.OfType<RouteEndpoint>())
        {
            EndpointDefinition? definition = endpoint.Metadata.GetMetadata<EndpointDefinition>();
            if ((definition is null) || (endpointNames.Contains(definition.EndpointType.Name) == false))
            {
                continue;
            }

            IReadOnlyList<string>? methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods;
            if ((methods is null) || (methods.Contains("GET") == false))
            {
                continue;
            }

            // Route parameters all name the single machine the test seeds.
            string raw = endpoint.RoutePattern.RawText ?? string.Empty;
            routes.Add("/" + routeParameter.Replace(raw, "1").TrimStart('/'));
        }

        return routes.Distinct(StringComparer.Ordinal).OrderBy(r => r, StringComparer.Ordinal).ToList();
    }

    [Test]
    public async Task EveryKnownPageSizeReader_UsesTheSharedRule()
    {
        // The premise of the sweep below. Without it a discovery that found nothing would pass.
        HashSet<string> discovered = EndpointClassesUsingTheSharedRule();

        foreach (string known in KnownPageSizeReaders)
        {
            await Assert.That(discovered).Contains(known);
        }
    }

    [Test]
    public async Task EveryPageSizeReader_RefusesAnOverLimitPageOnTheWire()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        // Team, because the audit log is a Team feature and would otherwise refuse the request on
        // tier before its page size was ever read. An admin for the same reason on the
        // administrative collections.
        int tenantId = await SeedTenantWithSubscription(db, SubscriptionTier.Team);
        await SeedMachine(db, tenantId, "uniform-host");

        HttpClient client = BuildAdminClient(factory, tenantId);

        List<string> routes = GetRoutesFor(factory, EndpointClassesUsingTheSharedRule());
        await Assert.That(routes.Count).IsGreaterThanOrEqualTo(KnownPageSizeReaders.Length);

        foreach (string route in routes)
        {
            HttpResponseMessage response = await client.GetAsync($"{route}?pageSize={PaginationLimits.MaxPageSize + 1}");

            await Assert.That(response.StatusCode)
                .IsEqualTo(HttpStatusCode.BadRequest)
                .Because($"{route} must refuse an over-limit page size rather than serve a short one");
        }
    }
}
