// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Server.Services.Alerts;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Integrations;

/// <summary>
/// Response DTO for integration test results.
/// </summary>
public sealed class IntegrationTestResultDto
{
    /// <summary>Whether the test delivery was successful.</summary>
    public bool Success { get; set; }

    /// <summary>The HTTP status code returned by the target, if applicable.</summary>
    public int? StatusCode { get; set; }

    /// <summary>A human-readable message describing the test result.</summary>
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Sends a test message to an integration endpoint to verify connectivity.
/// Requires TenantAdmin role.
/// </summary>
public sealed class IntegrationTestEndpoint : EndpointWithoutRequest<ApiResponse<IntegrationTestResultDto>>
{
    private readonly IIntegrationRepository _integrationRepo;
    private readonly IEnumerable<IIntegrationPayloadFormatter> _formatters;
    private readonly IHttpClientFactory _httpClientFactory;

    /// <summary>
    /// Creates a new instance of the <see cref="IntegrationTestEndpoint"/> class.
    /// </summary>
    public IntegrationTestEndpoint(
        IIntegrationRepository integrationRepo,
        IEnumerable<IIntegrationPayloadFormatter> formatters,
        IHttpClientFactory httpClientFactory)
    {
        _integrationRepo = integrationRepo;
        _formatters = formatters;
        _httpClientFactory = httpClientFactory;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Post("/integrations/{id:int}/test");
        Policies("TenantAdmin");
        Version(1);
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(CancellationToken ct)
    {
        int? tenantId = TenantClaimHelper.GetTenantIdFromClaims(User, HttpContext);
        if (tenantId is null)
        {
            HttpContext.Response.StatusCode = 401;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<IntegrationTestResultDto>.Error("Unauthorized"), ct);

            return;
        }

        int integrationId = Route<int>("id");

        IntegrationEndpoint? integration = await _integrationRepo.GetIntegrationByIdAsync(integrationId, tenantId.Value, ct);
        if (integration is null)
        {
            HttpContext.Response.StatusCode = 404;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<IntegrationTestResultDto>.Error("Integration not found"), ct);

            return;
        }

        IIntegrationPayloadFormatter? formatter = _formatters.FirstOrDefault(f => f.Provider == integration.Provider);
        if (formatter is null)
        {
            HttpContext.Response.StatusCode = 500;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<IntegrationTestResultDto>.Error("No formatter available for this provider"), ct);

            return;
        }

        AlertEvent testEvent = new()
        {
            Id = 0,
            AlertRuleId = 0,
            TenantId = tenantId.Value,
            MachineId = 0,
            Severity = AlertSeverity.Info,
            Message = "This is a test notification from Vord to verify your integration is configured correctly.",
            Details = null,
            Status = AlertEventStatus.Resolved,
            TriggeredAt = DateTimeOffset.UtcNow,
        };

        AlertRule testRule = new()
        {
            Id = 0,
            TenantId = tenantId.Value,
            Name = "Test Alert",
            Metric = AlertMetric.CpuUsage,
            Operator = AlertOperator.GreaterThan,
            Threshold = 0,
            DurationMinutes = 0,
            Severity = AlertSeverity.Info,
            IsEnabled = false,
            NotifyEmail = false,
            NotifyWebhook = false,
            IsCustom = false,
            CreatedByUserId = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        try
        {
            HttpRequestMessage request = formatter.FormatRequest(testEvent, testRule, integration);
            HttpClient client = _httpClientFactory.CreateClient("IntegrationDelivery");
            HttpResponseMessage response = await client.SendAsync(request, ct);

            IntegrationTestResultDto result = new()
            {
                Success = response.IsSuccessStatusCode,
                StatusCode = (int)response.StatusCode,
                Message = response.IsSuccessStatusCode
                    ? "Test message delivered successfully"
                    : $"Target returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase})",
            };

            await Send.OkAsync(ApiResponse<IntegrationTestResultDto>.Ok(result), cancellation: ct);
        }
        catch (HttpRequestException ex)
        {
            IntegrationTestResultDto result = new()
            {
                Success = false,
                StatusCode = null,
                Message = $"Connection failed: {ex.Message}",
            };

            await Send.OkAsync(ApiResponse<IntegrationTestResultDto>.Ok(result), cancellation: ct);
        }
        catch (TaskCanceledException)
        {
            IntegrationTestResultDto result = new()
            {
                Success = false,
                StatusCode = null,
                Message = "Request timed out after 10 seconds",
            };

            await Send.OkAsync(ApiResponse<IntegrationTestResultDto>.Ok(result), cancellation: ct);
        }
    }
}
