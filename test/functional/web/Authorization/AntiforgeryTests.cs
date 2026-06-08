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

namespace Framlux.FleetManagement.FunctionalTest.Authorization;

/// <summary>
/// End-to-end tests for the global-by-default antiforgery wiring. The realistic CSRF surface
/// for our deployment is form-encoded POSTs (which bypass CORS preflight); FastEndpoints'
/// antiforgery middleware enforces specifically on the form/multipart content types. Because
/// no FastEndpoints endpoint in this codebase accepts form-encoded bodies (all DTOs bind JSON
/// only), the realistic enforcement target is the Hangfire dashboard's job-trigger forms,
/// exercised by <c>HangfireDashboardEndpointTests</c>. This file documents the orthogonal
/// guarantees we DO test through the public API:
/// <list type="bullet">
///   <item>JSON POSTs from cookie-authenticated callers continue to flow without an antiforgery
///         token, because the middleware does not enforce on <c>application/json</c>. That keeps
///         our JSON API surface unaffected by the new enrollment.</item>
/// </list>
/// The enrollment decision (verb + opt-out attribute) is unit-tested in
/// <c>AntiforgeryEnrollmentTests</c>; the middleware behavior itself is provided by
/// FastEndpoints (verified by inspection of FE 8.1.0's <c>AntiforgeryMiddleware</c>).
/// </summary>
public sealed class AntiforgeryTests
{
    [Test]
    public async Task JsonPost_CookieAuth_WithoutAntiforgeryToken_IsAllowed()
    {
        // Confirms the design intent: our JSON-only API surface is untouched by the new global
        // enrollment. FE's antiforgery middleware only enforces on form-encoded and multipart
        // content types, so a same-origin cookie-authenticated JSON POST goes through.
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        Tenant tenant = new()
        {
            ExternalId = Guid.NewGuid().ToString("N"),
            Name = $"Antiforgery JSON Tenant {Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 1,
            IsActive = true,
            LogoUrl = ""
        };
        tenant.Id = await db.InsertWithInt32IdentityAsync(tenant);

        await db.InsertAsync(new TenantSubscription
        {
            TenantId = tenant.Id,
            Tier = SubscriptionTier.Pro,
            Status = SubscriptionStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        UserAccount user = new()
        {
            ExternalId = $"ext-antiforgery-{Guid.NewGuid():N}",
            Username = $"antiforgery-{Guid.NewGuid():N}@example.com",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 1,
            IsActive = true,
            IsSystem = false,
            IsGlobalAdmin = false,
        };
        user.Id = await db.InsertWithInt32IdentityAsync(user);

        await db.InsertAsync(new UserTenantRole
        {
            UserId = user.Id,
            AssignedTenantId = tenant.Id,
            Role = UserAccountRoles.TenantAdmin,
            AssignedByUserId = user.Id,
            AssignedAt = DateTimeOffset.UtcNow,
            IsActive = true,
        });

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(user.Id)
            .WithRole(tenant.Id, (int)UserAccountRoles.TenantAdmin)
            .WithActiveTenant(tenant.Id)
            .Build();

        // /api/v1/contact accepts JSON; with cookie auth, antiforgery is enrolled but the
        // middleware skips JSON content types and lets the request through to FE.
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/contact", new
        {
            Name = "Legit User",
            Email = "legit@example.com",
            Company = "",
            FleetSize = "",
            Message = "Same-origin JSON POST should pass through antiforgery",
        });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        // Status alone is not enough — assert the response body shape so a regression that
        // turns the middleware into a body-rewriter (or a misrouted 200 with an empty body)
        // fails this test.
        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument document = JsonDocument.Parse(body);
        await Assert.That(document.RootElement.GetProperty("success").GetBoolean()).IsTrue();
        await Assert.That(document.RootElement.GetProperty("message").GetString())
            .IsEqualTo("Thank you for your interest! We'll be in touch soon.");
    }
}
