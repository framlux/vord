// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Text.Json;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using LinqToDB.Async;

namespace Framlux.FleetManagement.FunctionalTest.Endpoints.Web;

/// <summary>
/// Pins the wire format of endpoint error responses: an <see cref="ApiResponse{T}"/>
/// envelope with camelCase property names, <c>success: false</c>, and the human-readable
/// message. Guards the error-writing path against serialization regressions, so it must
/// keep passing unchanged while endpoints migrate to the shared error helper.
/// </summary>
public sealed class ApiErrorEnvelopeRegressionTests
{
    [Test]
    public async Task ErrorResponses_KeepApiResponseEnvelopeShape()
    {
        using SelfHostedTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        Tenant tenant = new()
        {
            ExternalId = Guid.NewGuid().ToString("N"),
            Name = $"Envelope Regression Tenant {Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 1,
            IsActive = true,
            LogoUrl = ""
        };
        tenant.Id = await db.InsertWithInt32IdentityAsync(tenant);

        UserAccount user = new()
        {
            ExternalId = $"ext-envelope-regression-{Guid.NewGuid():N}",
            Username = $"enveloperegression-{Guid.NewGuid():N}@example.com",
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

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(user.Id)
            .WithRole(tenant.Id, (int)UserAccountRoles.TenantAdmin)
            .WithActiveTenant(tenant.Id)
            .Build();

        HttpResponseMessage response = await client.PostAsync("/api/v1/billing/cancel", null);
        string json = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(json);

        await Assert.That((int)response.StatusCode).IsEqualTo(404);
        await Assert.That(doc.RootElement.GetProperty("success").GetBoolean()).IsFalse();
        await Assert.That(doc.RootElement.GetProperty("message").GetString()).IsEqualTo("Billing is not enabled");
    }
}
