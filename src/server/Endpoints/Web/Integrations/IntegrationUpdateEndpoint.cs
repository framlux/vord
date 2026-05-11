// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Text.Json;
using FastEndpoints;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Server.Services.Infrastructure;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Integrations;

/// <summary>
/// Request model for updating an integration endpoint.
/// </summary>
public sealed class UpdateIntegrationRequest
{
    /// <summary>Optional new name for the integration.</summary>
    public string? Name { get; set; }

    /// <summary>Optional new enabled state.</summary>
    public bool? IsEnabled { get; set; }

    /// <summary>Optional updated configuration key-value pairs.</summary>
    public Dictionary<string, string>? Configuration { get; set; }
}

/// <summary>
/// Updates an existing integration endpoint for the current tenant.
/// Requires TenantAdmin role.
/// </summary>
public sealed class IntegrationUpdateEndpoint : Endpoint<UpdateIntegrationRequest, ApiResponse<IntegrationEndpointDto>>
{
    private readonly IIntegrationRepository _integrationRepo;
    private readonly IAuditLogRepository _auditLog;

    /// <summary>
    /// Creates a new instance of the <see cref="IntegrationUpdateEndpoint"/> class.
    /// </summary>
    public IntegrationUpdateEndpoint(
        IIntegrationRepository integrationRepo,
        IAuditLogRepository auditLog)
    {
        _integrationRepo = integrationRepo;
        _auditLog = auditLog;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Put("/integrations/{id:int}");
        Policies("TenantAdmin");
        Version(1);
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(UpdateIntegrationRequest req, CancellationToken ct)
    {
        int? tenantId = TenantClaimHelper.GetTenantIdFromClaims(User, HttpContext);
        if (tenantId is null)
        {
            HttpContext.Response.StatusCode = 401;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<IntegrationEndpointDto>.Error("Unauthorized"), ct);

            return;
        }

        int integrationId = Route<int>("id");

        IntegrationEndpoint? integration = await _integrationRepo.GetIntegrationByIdAsync(integrationId, tenantId.Value, ct);
        if (integration is null)
        {
            HttpContext.Response.StatusCode = 404;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<IntegrationEndpointDto>.Error("Integration not found"), ct);

            return;
        }

        // Validate all fields before persisting
        string? trimmedName = null;
        if (req.Name is not null)
        {
            trimmedName = req.Name.Trim();
            if ((trimmedName.Length < 1) || (trimmedName.Length > 100))
            {
                HttpContext.Response.StatusCode = 400;
                await HttpContext.Response.WriteAsJsonAsync(
                    ApiResponse<IntegrationEndpointDto>.Error("Name must be between 1 and 100 characters"), ct);

                return;
            }
        }

        string? configurationJson = null;
        if (req.Configuration is not null)
        {
            string? configError = IntegrationConfigValidator.ValidateProviderConfiguration(integration.Provider, req.Configuration);
            if (configError is not null)
            {
                HttpContext.Response.StatusCode = 400;
                await HttpContext.Response.WriteAsJsonAsync(
                    ApiResponse<IntegrationEndpointDto>.Error(configError), ct);

                return;
            }

            configurationJson = JsonSerializer.Serialize(req.Configuration, JsonDefaults.CamelCase);
        }

        // Apply all changes in a single query
        await _integrationRepo.UpdateIntegrationAsync(integrationId, trimmedName, req.IsEnabled, configurationJson, ct);

        if (trimmedName is not null)
        {
            integration.Name = trimmedName;
        }

        if (req.IsEnabled is not null)
        {
            integration.IsEnabled = req.IsEnabled.Value;
        }

        if (configurationJson is not null)
        {
            integration.Configuration = configurationJson;
        }

        integration.UpdatedAt = DateTimeOffset.UtcNow;

        int? userId = TenantClaimHelper.GetUserIdFromClaims(User);
        await _auditLog.InsertAuditLogAsync(AuditHelper.Create(
            tenantId.Value, userId, null,
            AuditAction.IntegrationUpdated, AuditResourceType.Integration,
            integrationId.ToString(), integration.Name, null), ct);

        IntegrationEndpointDto dto = new()
        {
            Id = integration.Id,
            Provider = integration.Provider.ToString(),
            Name = integration.Name,
            IsEnabled = integration.IsEnabled,
            CreatedAt = integration.CreatedAt.ToString("o"),
        };

        await Send.OkAsync(ApiResponse<IntegrationEndpointDto>.Ok(dto, "Integration updated"), cancellation: ct);
    }
}
