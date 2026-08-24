// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Server.Endpoints;
using Framlux.FleetManagement.Server.Endpoints.Web.Billing;
using Framlux.FleetManagement.Services.Core.Billing;
using Framlux.FleetManagement.Services.Core.Deployment;
using Framlux.FleetManagement.Services.Core.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using NSubstitute;
using System.Text.Json;

namespace Framlux.FleetManagement.Test.Endpoints.Web.Billing;

/// <summary>
/// Unit tests for <see cref="BillingEndpointGuards"/>.
/// </summary>
public sealed class BillingEndpointGuardsTests
{
    private const int TenantId = 42;

    [Test]
    public async Task LoadGatedSubscriptionAsync_SelfHosted_Writes404AndReturnsNull()
    {
        DeploymentMode deploymentMode = new(Options.Create(new DeploymentOptions { SelfHosted = true }));
        ISubscriptionService subscriptionService = Substitute.For<ISubscriptionService>();
        DefaultHttpContext httpContext = new();
        httpContext.Response.Body = new MemoryStream();

        TenantSubscription? result = await BillingEndpointGuards.LoadGatedSubscriptionAsync(
            httpContext, deploymentMode, subscriptionService, TenantId, CancellationToken.None);

        await Assert.That(result).IsNull();
        await Assert.That(httpContext.Response.StatusCode).IsEqualTo(404);

        httpContext.Response.Body.Position = 0;
        ApiResponse<object>? body = await JsonSerializer.DeserializeAsync<ApiResponse<object>>(
            httpContext.Response.Body,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await Assert.That(body?.Message).IsEqualTo("Billing is not enabled");

        await subscriptionService.DidNotReceive().GetSubscriptionForTenantAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task LoadGatedSubscriptionAsync_NoSubscription_Writes404AndReturnsNull()
    {
        DeploymentMode deploymentMode = new(Options.Create(new DeploymentOptions { SelfHosted = false }));
        ISubscriptionService subscriptionService = Substitute.For<ISubscriptionService>();
        subscriptionService.GetSubscriptionForTenantAsync(TenantId, Arg.Any<CancellationToken>())
            .Returns((TenantSubscription?)null);
        DefaultHttpContext httpContext = new();
        httpContext.Response.Body = new MemoryStream();

        TenantSubscription? result = await BillingEndpointGuards.LoadGatedSubscriptionAsync(
            httpContext, deploymentMode, subscriptionService, TenantId, CancellationToken.None);

        await Assert.That(result).IsNull();
        await Assert.That(httpContext.Response.StatusCode).IsEqualTo(404);

        httpContext.Response.Body.Position = 0;
        ApiResponse<object>? body = await JsonSerializer.DeserializeAsync<ApiResponse<object>>(
            httpContext.Response.Body,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await Assert.That(body?.Message).IsEqualTo("Subscription not found");
    }

    [Test]
    public async Task LoadGatedSubscriptionAsync_SubscriptionPresent_ReturnsSubscriptionAndWritesNothing()
    {
        DeploymentMode deploymentMode = new(Options.Create(new DeploymentOptions { SelfHosted = false }));
        TenantSubscription subscription = new()
        {
            Id = 1,
            TenantId = TenantId,
            Tier = SubscriptionTier.Pro,
            Status = SubscriptionStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        ISubscriptionService subscriptionService = Substitute.For<ISubscriptionService>();
        subscriptionService.GetSubscriptionForTenantAsync(TenantId, Arg.Any<CancellationToken>())
            .Returns(subscription);
        DefaultHttpContext httpContext = new();
        MemoryStream responseBody = new();
        httpContext.Response.Body = responseBody;

        TenantSubscription? result = await BillingEndpointGuards.LoadGatedSubscriptionAsync(
            httpContext, deploymentMode, subscriptionService, TenantId, CancellationToken.None);

        await Assert.That(result).IsEqualTo(subscription);
        await Assert.That(responseBody.Length).IsEqualTo(0L);
    }
}
