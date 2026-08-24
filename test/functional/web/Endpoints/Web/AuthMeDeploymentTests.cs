// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Net;
using System.Text.Json;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using LinqToDB.Async;

namespace Framlux.FleetManagement.FunctionalTest.Endpoints.Web;

/// <summary>
/// The session payload is the only channel by which the web application learns which product it is
/// rendering, so the mode reported by <c>/auth/me</c> has to track the host it is served from. A
/// wrong answer here shows billing in a self-hosted deployment or hides it in the hosted one.
/// </summary>
public sealed class AuthMeDeploymentTests
{
    private static async Task<UserAccount> SeedUser(DatabaseContext db)
    {
        UserAccount user = new()
        {
            ExternalId = $"ext-deployment-{Guid.NewGuid():N}",
            Username = $"deployment-{Guid.NewGuid():N}@example.com",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 1,
            IsActive = true,
            IsSystem = false,
            IsGlobalAdmin = false,
        };
        user.Id = await db.InsertWithInt32IdentityAsync(user);

        return user;
    }

    private static async Task<bool> ReadSelfHostedFlag(HttpClient client)
    {
        HttpResponseMessage response = await client.GetAsync("/api/v1/auth/me");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        using JsonDocument json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

        return json.RootElement
            .GetProperty("data")
            .GetProperty("deployment")
            .GetProperty("selfHosted")
            .GetBoolean();
    }

    [Test]
    public async Task AuthMe_InSelfHosted_ReportsSelfHostedTrue()
    {
        using SelfHostedTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        UserAccount user = await SeedUser(db);

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(user.Id)
            .WithExternalId(user.ExternalId)
            .WithEmail(user.Username)
            .Build();

        await Assert.That(await ReadSelfHostedFlag(client)).IsTrue();
    }

    [Test]
    public async Task AuthMe_InSaas_ReportsSelfHostedFalse()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();
        UserAccount user = await SeedUser(db);

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(user.Id)
            .WithExternalId(user.ExternalId)
            .WithEmail(user.Username)
            .Build();

        await Assert.That(await ReadSelfHostedFlag(client)).IsFalse();
    }
}
