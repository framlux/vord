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
    private readonly IDatabaseTransactionProvider _transactionProvider;

    /// <summary>
    /// Creates a new instance of the <see cref="IntegrationCreateEndpoint"/> class.
    /// </summary>
    public IntegrationCreateEndpoint(
        IIntegrationRepository integrationRepo,
        ISubscriptionService subscriptionService,
        IAuditLogRepository auditLog,
        IDataProtectionProvider dataProtectionProvider,
        IDatabaseTransactionProvider transactionProvider)
    {
        _integrationRepo = integrationRepo;
        _subscriptionService = subscriptionService;
        _auditLog = auditLog;
        _dataProtectionProvider = dataProtectionProvider;
        _transactionProvider = transactionProvider;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Post("/integrations");
        Policies("TenantAdmin");
        Version(1);
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(CreateIntegrationRequest req, CancellationToken ct)
    {
        int? tenantId = TenantClaimHelper.GetTenantIdFromClaims(User, HttpContext);
        if (tenantId is null)
        {
            HttpContext.Response.StatusCode = 401;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<IntegrationEndpointDto>.Error("Unauthorized"), ct);

            return;
        }

        int? userId = TenantClaimHelper.GetUserIdFromClaims(User);
        if (userId is null)
        {
            HttpContext.Response.StatusCode = 401;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<IntegrationEndpointDto>.Error("Unable to identify user"), ct);

            return;
        }

        TenantSubscription? subscription = await _subscriptionService.GetSubscriptionForTenantAsync(tenantId.Value, ct);
        if ((subscription is null) || (subscription.Tier == SubscriptionTier.Free) || (subscription.Status != SubscriptionStatus.Active))
        {
            HttpContext.Response.StatusCode = 403;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<IntegrationEndpointDto>.Error("Integrations require a Pro or Team subscription"), ct);

            return;
        }

        bool canCreate = await _subscriptionService.CanCreateWebhookAsync(tenantId.Value, ct);
        if (canCreate == false)
        {
            HttpContext.Response.StatusCode = 403;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<IntegrationEndpointDto>.Error("Integration endpoint limit reached for your subscription tier"), ct);

            return;
        }

        if (Enum.TryParse<IntegrationProvider>(req.Provider, true, out IntegrationProvider provider) == false)
        {
            HttpContext.Response.StatusCode = 400;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<IntegrationEndpointDto>.Error($"Invalid provider value: {req.Provider}"), ct);

            return;
        }

        if (provider == IntegrationProvider.None)
        {
            HttpContext.Response.StatusCode = 400;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<IntegrationEndpointDto>.Error("Provider cannot be None"), ct);

            return;
        }

        // Validate provider-specific configuration
        string? validationError = IntegrationConfigValidator.ValidateProviderConfiguration(provider, req.Configuration);
        if (validationError is not null)
        {
            HttpContext.Response.StatusCode = 400;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<IntegrationEndpointDto>.Error(validationError), ct);

            return;
        }

        // Auto-generate name from provider if not provided
        string name = string.IsNullOrWhiteSpace(req.Name)
            ? GenerateDefaultName(provider)
            : req.Name.Trim();

        if ((name.Length < 1) || (name.Length > 100))
        {
            HttpContext.Response.StatusCode = 400;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<IntegrationEndpointDto>.Error("Name must be between 1 and 100 characters"), ct);

            return;
        }

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
            TenantId = tenantId.Value,
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
            tenantId.Value, userId.Value, null,
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
