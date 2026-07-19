// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Security.Cryptography;
using System.Text.Json;
using FastEndpoints;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Microsoft.AspNetCore.DataProtection;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Integrations;

/// <summary>
/// Rotates the secret for a Custom provider integration endpoint.
/// Requires TenantAdmin role.
/// </summary>
public sealed class IntegrationRotateSecretEndpoint : EndpointWithoutRequest<ApiResponse<IntegrationEndpointDto>>
{
    private readonly IIntegrationRepository _integrationRepo;
    private readonly IAuditLogRepository _auditLog;
    private readonly IDataProtectionProvider _dataProtectionProvider;
    private readonly ITenantContext _tenantContext;

    /// <summary>
    /// Creates a new instance of the <see cref="IntegrationRotateSecretEndpoint"/> class.
    /// </summary>
    public IntegrationRotateSecretEndpoint(
        IIntegrationRepository integrationRepo,
        IAuditLogRepository auditLog,
        IDataProtectionProvider dataProtectionProvider,
        ITenantContext tenantContext)
    {
        _integrationRepo = integrationRepo;
        _auditLog = auditLog;
        _dataProtectionProvider = dataProtectionProvider;
        _tenantContext = tenantContext;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Post("/integrations/{id:int}/rotate-secret");
        Policies("TenantAdmin");
        Tags(EndpointTags.RequiresTenant);
        Version(1);
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(CancellationToken ct)
    {
        int tenantId = _tenantContext.RequireTenantId();

        int? userId = _tenantContext.UserId;
        if (userId is null)
        {
            await HttpContext.SendApiErrorAsync(401, "Unable to identify user", ct);

            return;
        }

        int integrationId = Route<int>("id");

        IntegrationEndpoint? integration = await _integrationRepo.GetIntegrationByIdAsync(integrationId, tenantId, ct);
        if (integration is null)
        {
            await HttpContext.SendApiErrorAsync(404, "Integration not found", ct);

            return;
        }

        if (integration.Provider != IntegrationProvider.Custom)
        {
            await HttpContext.SendApiErrorAsync(400, "Secret rotation is only available for Custom provider integrations", ct);

            return;
        }

        // Generate a new secret
        byte[] secretBytes = RandomNumberGenerator.GetBytes(32);
        string plaintextSecret = Convert.ToHexString(secretBytes).ToLowerInvariant();

        IDataProtector protector = _dataProtectionProvider.CreateProtector("IntegrationEndpointSecret");
        string encryptedSecret = protector.Protect(plaintextSecret);

        // Parse existing configuration and update the secret
        Dictionary<string, string> config = JsonSerializer.Deserialize<Dictionary<string, string>>(
            integration.Configuration, JsonDefaults.CamelCase) ?? new();
        config["secret"] = encryptedSecret;

        string updatedConfigJson = JsonSerializer.Serialize(config, JsonDefaults.CamelCase);
        await _integrationRepo.UpdateIntegrationConfigurationAsync(integrationId, tenantId, updatedConfigJson, ct);

        await _auditLog.InsertAuditLogAsync(AuditHelper.Create(
            tenantId, userId.Value, null,
            AuditAction.IntegrationSecretRotated, AuditResourceType.Integration,
            integrationId.ToString(), integration.Name, null), ct);

        IntegrationEndpointDto dto = new()
        {
            Id = integration.Id,
            Provider = integration.Provider.ToString(),
            Name = integration.Name,
            IsEnabled = integration.IsEnabled,
            CreatedAt = integration.CreatedAt.ToString("o"),
            Secret = plaintextSecret,
        };

        await Send.OkAsync(ApiResponse<IntegrationEndpointDto>.Ok(dto, "Secret rotated"), cancellation: ct);
    }
}
