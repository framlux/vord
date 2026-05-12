// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Microsoft.AspNetCore.Mvc.Testing;
using System.Net;

namespace Framlux.FleetManagement.Test.Infrastructure;

/// <summary>
/// Fluent builder for creating <see cref="HttpClient"/> instances with test authentication headers.
/// The built client includes headers that <see cref="TestAuthHandler"/> reads to build a
/// <see cref="System.Security.Claims.ClaimsPrincipal"/>.
/// </summary>
public sealed class AuthenticatedClientBuilder
{
    private readonly WebApplicationFactory<Program> _factory;
    private int _userId = 1;
    private string _externalId = "ext-test-user-1";
    private string _email = "test@example.com";
    private bool _isGlobalAdmin;
    private int? _activeTenantId;
    private readonly List<string> _roles = new();

    /// <summary>
    /// Creates a new builder for the given test factory.
    /// </summary>
    /// <param name="factory">The functional test factory.</param>
    public AuthenticatedClientBuilder(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    /// <summary>
    /// Sets the user ID claim.
    /// </summary>
    public AuthenticatedClientBuilder WithUserId(int userId)
    {
        _userId = userId;

        return this;
    }

    /// <summary>
    /// Sets the external ID claim.
    /// </summary>
    public AuthenticatedClientBuilder WithExternalId(string externalId)
    {
        _externalId = externalId;

        return this;
    }

    /// <summary>
    /// Sets the email claim.
    /// </summary>
    public AuthenticatedClientBuilder WithEmail(string email)
    {
        _email = email;

        return this;
    }

    /// <summary>
    /// Marks the user as a global administrator.
    /// </summary>
    public AuthenticatedClientBuilder AsGlobalAdmin()
    {
        _isGlobalAdmin = true;

        return this;
    }

    /// <summary>
    /// Adds a tenant-scoped role claim.
    /// </summary>
    /// <param name="tenantId">The tenant ID.</param>
    /// <param name="roleId">The role byte value (e.g. 1=Viewer, 2=MachineAdmin, 3=TenantAdmin).</param>
    public AuthenticatedClientBuilder WithRole(int tenantId, int roleId)
    {
        _roles.Add($"{tenantId}:{roleId}");

        return this;
    }

    /// <summary>
    /// Sets the active tenant cookie.
    /// </summary>
    public AuthenticatedClientBuilder WithActiveTenant(int tenantId)
    {
        _activeTenantId = tenantId;

        return this;
    }

    /// <summary>
    /// Builds an <see cref="HttpClient"/> with the configured test authentication headers.
    /// </summary>
    public HttpClient Build()
    {
        HttpClient client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false,
        });

        client.DefaultRequestHeaders.Add(TestAuthHandler.UserIdHeader, _userId.ToString());
        client.DefaultRequestHeaders.Add(TestAuthHandler.ExternalIdHeader, _externalId);
        client.DefaultRequestHeaders.Add(TestAuthHandler.EmailHeader, _email);
        client.DefaultRequestHeaders.Add(TestAuthHandler.IsGlobalAdminHeader, _isGlobalAdmin.ToString());

        if (_roles.Count > 0)
        {
            client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, string.Join(",", _roles));
        }

        if (_activeTenantId.HasValue)
        {
            client.DefaultRequestHeaders.Add("Cookie", $"vord_tenant={_activeTenantId.Value}");
        }

        return client;
    }
}
