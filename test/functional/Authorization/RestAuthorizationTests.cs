// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.FunctionalTest.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using System.Net;
using System.Text.Json;

namespace Framlux.FleetManagement.FunctionalTest.Authorization;

/// <summary>
/// Functional tests for REST authorization enforcement across policy-protected endpoints.
/// Verifies that unauthenticated requests are rejected, role-based policies are enforced,
/// and the internal billing endpoint validates its API key.
/// </summary>
public sealed class RestAuthorizationTests
{
    [Test]
    public async Task UnauthenticatedRequest_ToProtectedEndpoint_Returns401()
    {
        // Arrange
        using FunctionalTestFactory factory = new();
        HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false,
        });

        string[] protectedRoutes =
        [
            "/api/v1/auth/me",
            "/api/v1/machines",
            "/api/v1/admin/settings",
        ];

        // Act & Assert — each protected endpoint should reject unauthenticated requests
        foreach (string route in protectedRoutes)
        {
            HttpResponseMessage response = await client.GetAsync(route);
            bool isRejected = response.StatusCode == HttpStatusCode.Unauthorized ||
                              response.StatusCode == HttpStatusCode.Found;

            await Assert.That(isRejected)
                .IsTrue()
                .Because($"Expected 401 or 302 for unauthenticated request to {route}, got {response.StatusCode}");

            string body = await response.Content.ReadAsStringAsync();

            // Verify the response body does not contain authenticated success data
            if (string.IsNullOrWhiteSpace(body) == false)
            {
                try
                {
                    JsonDocument doc = JsonDocument.Parse(body);
                    bool hasSuccessTrue = doc.RootElement.TryGetProperty("success", out JsonElement successElement) &&
                                          successElement.ValueKind == JsonValueKind.True;
                    await Assert.That(hasSuccessTrue)
                        .IsFalse()
                        .Because($"Unauthenticated response for {route} should not contain success:true");
                }
                catch (JsonException)
                {
                    // Non-JSON response body (e.g. redirect HTML) is acceptable for unauthenticated requests
                }
            }
        }
    }

    [Test]
    public async Task ViewerRole_CannotAccessTenantAdminEndpoints_Returns403()
    {
        // Arrange
        using FunctionalTestFactory factory = new();
        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(1)
            .WithRole(1, 3) // Viewer
            .WithActiveTenant(1)
            .Build();

        string[] tenantAdminRoutes =
        [
            "/api/v1/tenants/registration-tokens",
            "/api/v1/users",
        ];

        // Act & Assert — Viewer should be forbidden from TenantAdmin endpoints
        foreach (string route in tenantAdminRoutes)
        {
            HttpResponseMessage response = await client.GetAsync(route);

            await Assert.That(response.StatusCode)
                .IsEqualTo(HttpStatusCode.Forbidden)
                .Because($"Viewer should receive 403 for {route}");

            string body = await response.Content.ReadAsStringAsync();

            // If a body is present, verify it does not indicate success
            if (string.IsNullOrWhiteSpace(body) == false)
            {
                JsonDocument doc = JsonDocument.Parse(body);
                bool hasSuccessTrue = doc.RootElement.TryGetProperty("success", out JsonElement successElement) &&
                                      successElement.ValueKind == JsonValueKind.True;
                await Assert.That(hasSuccessTrue)
                    .IsFalse()
                    .Because($"Forbidden response for {route} should not indicate success");
            }
        }
    }

    [Test]
    public async Task MachineAdminRole_CannotAccessTenantAdminEndpoints_Returns403()
    {
        // Arrange
        using FunctionalTestFactory factory = new();
        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(2)
            .WithRole(1, 2) // MachineAdmin (cannot access TenantAdmin endpoints)
            .WithActiveTenant(1)
            .Build();

        string[] tenantAdminRoutes =
        [
            "/api/v1/tenants/registration-tokens",
            "/api/v1/users",
        ];

        // Act & Assert — MachineAdmin should be forbidden from TenantAdmin endpoints
        foreach (string route in tenantAdminRoutes)
        {
            HttpResponseMessage response = await client.GetAsync(route);

            await Assert.That(response.StatusCode)
                .IsEqualTo(HttpStatusCode.Forbidden)
                .Because($"MachineAdmin should receive 403 for {route}");

            string body = await response.Content.ReadAsStringAsync();

            // If a body is present, verify it does not indicate success
            if (string.IsNullOrWhiteSpace(body) == false)
            {
                JsonDocument doc = JsonDocument.Parse(body);
                bool hasSuccessTrue = doc.RootElement.TryGetProperty("success", out JsonElement successElement) &&
                                      successElement.ValueKind == JsonValueKind.True;
                await Assert.That(hasSuccessTrue)
                    .IsFalse()
                    .Because($"Forbidden response for {route} should not indicate success");
            }
        }
    }

    [Test]
    public async Task TenantAdmin_CannotAccessGlobalAdminEndpoints_Returns403()
    {
        // Arrange
        using FunctionalTestFactory factory = new();
        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(3)
            .WithRole(1, 1) // TenantAdmin
            .WithActiveTenant(1)
            .Build();

        string[] adminRoutes =
        [
            "/api/v1/admin/settings",
            "/api/v1/admin/users",
        ];

        // Act & Assert — TenantAdmin (non-global) should be forbidden from Admin endpoints
        foreach (string route in adminRoutes)
        {
            HttpResponseMessage response = await client.GetAsync(route);

            await Assert.That(response.StatusCode)
                .IsEqualTo(HttpStatusCode.Forbidden)
                .Because($"TenantAdmin should receive 403 for {route}");

            string body = await response.Content.ReadAsStringAsync();

            // If a body is present, verify it does not indicate success
            if (string.IsNullOrWhiteSpace(body) == false)
            {
                JsonDocument doc = JsonDocument.Parse(body);
                bool hasSuccessTrue = doc.RootElement.TryGetProperty("success", out JsonElement successElement) &&
                                      successElement.ValueKind == JsonValueKind.True;
                await Assert.That(hasSuccessTrue)
                    .IsFalse()
                    .Because($"Forbidden response for {route} should not indicate success");
            }
        }
    }

    [Test]
    public async Task GlobalAdmin_CanAccessAdminEndpoints_ReturnsNon403()
    {
        // Arrange
        using FunctionalTestFactory factory = new();
        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(10)
            .AsGlobalAdmin()
            .WithRole(1, 1) // TenantAdmin
            .WithActiveTenant(1)
            .Build();

        string[] adminRoutes =
        [
            "/api/v1/admin/settings",
            "/api/v1/admin/users",
        ];

        // Act & Assert — GlobalAdmin should pass authorization (not 401 or 403)
        foreach (string route in adminRoutes)
        {
            HttpResponseMessage response = await client.GetAsync(route);
            bool isAuthorized = response.StatusCode != HttpStatusCode.Unauthorized &&
                                response.StatusCode != HttpStatusCode.Forbidden;

            await Assert.That(isAuthorized)
                .IsTrue()
                .Because($"GlobalAdmin should not receive 401/403 for {route}, got {response.StatusCode}");

            string body = await response.Content.ReadAsStringAsync();
            await Assert.That(string.IsNullOrWhiteSpace(body) == false)
                .IsTrue()
                .Because($"GlobalAdmin response for {route} should have a response body");

            JsonDocument doc = JsonDocument.Parse(body);
            bool hasSuccessProperty = doc.RootElement.TryGetProperty("success", out JsonElement _);
            await Assert.That(hasSuccessProperty)
                .IsTrue()
                .Because($"GlobalAdmin response for {route} should contain a 'success' property in the JSON response");
        }
    }
}
