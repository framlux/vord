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
using Framlux.FleetManagement.Server.Services.Billing;
using Framlux.FleetManagement.Services.Core.Billing;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Microsoft.AspNetCore.DataProtection;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Integrations;

/// <summary>
/// Request model for creating a new integration endpoint.
/// </summary>
public sealed class CreateIntegrationRequest
{
    /// <summary>The integration provider type.</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>Optional user-facing name. Auto-generated from provider if omitted.</summary>
    public string? Name { get; set; }

    /// <summary>Provider-specific configuration key-value pairs.</summary>
    public Dictionary<string, string> Configuration { get; set; } = new();
}

/// <summary>
/// Creates a new integration endpoint for the current tenant.
/// Requires TenantAdmin role and Pro+ subscription.
/// </summary>
public sealed class IntegrationCreateEndpoint : Endpoint<CreateIntegrationRequest, ApiResponse<IntegrationEndpointDto>>
{
    private readonly IIntegrationRepository _integrationRepo;
    private readonly ISubscriptionService _subscriptionService;
    private readonly IAuditLogRepository _auditLog;
    private readonly IDataProtectionProvider _dataProtectionProvider;
    private readonly ITenantContext _tenantContext;
    private readonly IDatabaseTransactionProvider _transactionProvider;

    /// <summary>
    /// Creates a new instance of the <see cref="IntegrationCreateEndpoint"/> class.
    /// </summary>
    public IntegrationCreateEndpoint(
        IIntegrationRepository integrationRepo,
        ISubscriptionService subscriptionService,
        IAuditLogRepository auditLog,
        IDataProtectionProvider dataProtectionProvider,
        ITenantContext tenantContext,
        IDatabaseTransactionProvider transactionProvider)
    {
        _integrationRepo = integrationRepo;
        _subscriptionService = subscriptionService;
        _auditLog = auditLog;
        _dataProtectionProvider = dataProtectionProvider;
        _tenantContext = tenantContext;
        _transactionProvider = transactionProvider;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Post("/integrations");
        Policies("TenantAdmin");
        Tags(EndpointTags.RequiresTenant, EndpointTags.RequiresProSubscription);
        Options(b => b.WithMetadata(new RequiresProFeatureMessage(ProFeatureMessages.Integrations)));
        Version(1);
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(CreateIntegrationRequest req, CancellationToken ct)
    {
        int tenantId = _tenantContext.RequireTenantId();

        int? userId = _tenantContext.UserId;
        if (userId is null)
        {
            await HttpContext.SendApiErrorAsync(401, "Unable to identify user", ct);

            return;
        }

        bool canCreate = await _subscriptionService.CanCreateWebhookAsync(tenantId, ct);
        if (canCreate == false)
        {
            await HttpContext.SendApiErrorAsync(403, "Integration endpoint limit reached for your subscription tier", ct);

            return;
        }

        // CreateIntegrationValidator has already confirmed req.Provider parses to a valid
        // IntegrationProvider value; re-parse here to obtain that value.
        Enum.TryParse(req.Provider, true, out IntegrationProvider provider);

        if (provider == IntegrationProvider.None)
        {
            await HttpContext.SendApiErrorAsync(400, "Provider cannot be None", ct);

            return;
        }

        // Validate provider-specific configuration
        string? validationError = IntegrationConfigValidator.ValidateProviderConfiguration(provider, req.Configuration);
        if (validationError is not null)
        {
            await HttpContext.SendApiErrorAsync(400, validationError, ct);

            return;
        }

        // Auto-generate name from provider if not provided
        string name = string.IsNullOrWhiteSpace(req.Name)
            ? GenerateDefaultName(provider)
            : req.Name.Trim();

        // For Custom provider, generate and encrypt a secret
        string? plaintextSecret = null;
        Dictionary<string, string> configToStore = new(req.Configuration);

        if (provider == IntegrationProvider.Custom)
        {
            byte[] secretBytes = RandomNumberGenerator.GetBytes(32);
            plaintextSecret = Convert.ToHexString(secretBytes).ToLowerInvariant();

            IDataProtector protector = _dataProtectionProvider.CreateProtector("IntegrationEndpointSecret");
            string encryptedSecret = protector.Protect(plaintextSecret);
            configToStore["secret"] = encryptedSecret;
        }

        string configurationJson = JsonSerializer.Serialize(configToStore, JsonDefaults.CamelCase);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        IntegrationEndpoint integration = new()
        {
            TenantId = tenantId,
            Provider = provider,
            Name = name,
            Configuration = configurationJson,
            IsEnabled = true,
            CreatedByUserId = userId.Value,
            CreatedAt = now,
        };

        using IDatabaseTransaction transaction = await _transactionProvider.BeginTransactionAsync(ct);

        integration = await _integrationRepo.CreateIntegrationAsync(integration, ct);

        await _auditLog.InsertAuditLogAsync(AuditHelper.Create(
            tenantId, userId.Value, null,
            AuditAction.IntegrationCreated, AuditResourceType.Integration,
            integration.Id.ToString(), name, null), ct);

        await transaction.CommitAsync(ct);

        IntegrationEndpointDto dto = new()
        {
            Id = integration.Id,
            Provider = integration.Provider.ToString(),
            Name = integration.Name,
            IsEnabled = integration.IsEnabled,
            CreatedAt = integration.CreatedAt.ToString("o"),
            Secret = plaintextSecret,
        };

        HttpContext.Response.StatusCode = 201;
        await HttpContext.Response.WriteAsJsonAsync(
            ApiResponse<IntegrationEndpointDto>.Ok(dto, "Integration created"), ct);
    }

    private static string GenerateDefaultName(IntegrationProvider provider)
    {
        return provider switch
        {
            IntegrationProvider.Slack => "Slack Integration",
            IntegrationProvider.MicrosoftTeams => "Microsoft Teams Integration",
            IntegrationProvider.Discord => "Discord Integration",
            IntegrationProvider.PagerDuty => "PagerDuty Integration",
            IntegrationProvider.Custom => "Custom Webhook",
            _ => "Integration",
        };
    }
}
