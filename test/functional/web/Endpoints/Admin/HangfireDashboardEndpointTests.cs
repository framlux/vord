// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Net;
using Framlux.FleetManagement.Test.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Framlux.FleetManagement.FunctionalTest.Endpoints.Admin;

/// <summary>
/// Functional tests for the Hangfire admin dashboard mounted at /admin/hangfire.
/// Verifies the iga-claim-gated authorization filter rejects unauthenticated requests and
/// authenticated non-admin users, and allows global admins.
/// </summary>
public sealed class HangfireDashboardEndpointTests
{
    [Test]
    public async Task GetDashboard_Anonymous_IsRejected()
    {
        using FunctionalTestFactory factory = new();
        HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false,
        });

        HttpResponseMessage response = await client.GetAsync("/admin/hangfire");

        // Hangfire's dashboard middleware short-circuits unauthorized requests; depending on
        // configuration this surfaces as 401, 403, or a redirect. Any of these satisfies "rejected".
        bool rejected = response.StatusCode == HttpStatusCode.Unauthorized
            || response.StatusCode == HttpStatusCode.Forbidden
            || response.StatusCode == HttpStatusCode.Redirect
            || response.StatusCode == HttpStatusCode.Found;
        await Assert.That(rejected).IsTrue();
    }

    [Test]
    public async Task GetDashboard_AuthenticatedNonAdmin_IsRejected()
    {
        using FunctionalTestFactory factory = new();
        // No AsGlobalAdmin() — user has the test auth headers but the iga claim is "False".
        HttpClient client = new AuthenticatedClientBuilder(factory).WithUserId(1).Build();

        HttpResponseMessage response = await client.GetAsync("/admin/hangfire");

        bool rejected = response.StatusCode == HttpStatusCode.Unauthorized
            || response.StatusCode == HttpStatusCode.Forbidden;
        await Assert.That(rejected).IsTrue();
    }

    [Test]
    public async Task GetDashboard_GlobalAdmin_IsAllowed()
    {
        using FunctionalTestFactory factory = new();
        HttpClient client = new AuthenticatedClientBuilder(factory).WithUserId(1).AsGlobalAdmin().Build();

        HttpResponseMessage response = await client.GetAsync("/admin/hangfire");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        // Intent: a 200 alone is not enough — a misconfigured routing fallback could also return
        // 200 with an empty body. Confirm we're actually hitting the Hangfire dashboard by checking
        // for the dashboard's well-known title text. If a future change accidentally remounts the
        // route to a different handler, this assertion catches it.
        string body = await response.Content.ReadAsStringAsync();
        await Assert.That(body).Contains("Hangfire");
    }
}
