// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Security.Claims;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Server.Endpoints.Web.Machines.History;
using Framlux.FleetManagement.Services.Core.Billing;
using Microsoft.AspNetCore.Http;
using NSubstitute;

namespace Framlux.FleetManagement.Test.Endpoints.Web.Machines.History;

/// <summary>
/// Unit tests for <see cref="HistoryRequestValidator"/>.
/// </summary>
public sealed class HistoryRequestValidatorTests
{
    private readonly IMachineRepository _machineRepo = Substitute.For<IMachineRepository>();
    private readonly ISubscriptionService _subscriptionService = Substitute.For<ISubscriptionService>();
    private readonly HistoryRequestValidator _validator;

    /// <summary>
    /// Initializes the validator and its mocked dependencies.
    /// </summary>
    public HistoryRequestValidatorTests()
    {
        _validator = new HistoryRequestValidator(_machineRepo, _subscriptionService);
    }

    /// <summary>
    /// Creates an HttpContext with a claims principal that has the specified tenant ID
    /// in a role claim formatted as "{tenantId}:{roleId}".
    /// </summary>
    private static HttpContext CreateHttpContext(int? tenantId)
    {
        DefaultHttpContext httpContext = new();

        if (tenantId.HasValue)
        {
            List<Claim> claims =
            [
                new Claim(ClaimTypes.Role, $"{tenantId.Value}:1")
            ];
            ClaimsIdentity identity = new(claims, "TestAuth");
            httpContext.User = new ClaimsPrincipal(identity);
        }
        else
        {
            httpContext.User = new ClaimsPrincipal(new ClaimsIdentity());
        }

        return httpContext;
    }

    /// <summary>
    /// Creates a Machine instance with all required properties populated.
    /// </summary>
    private static Machine CreateMachine(long id, int tenantId)
    {
        return new Machine
        {
            Id = id,
            TenantId = tenantId,
            ApiKeyHash = new string('a', 64),
            Name = "test-machine",
            SerialNumber = "SN-001",
            SystemId = "SYS-001",
            MachineType = MachineTypes.BareMetalServer,
            OperatingSystem = OperatingSystems.Ubuntu,
            RegistrationTokenId = 1,
            RegisteredOn = DateTimeOffset.UtcNow,
            IsDeleted = false
        };
    }

    // ================================================================
    // Tenant claim validation (403)
    // ================================================================

    [Test]
    public async Task NoTenantClaim_Returns403AndNull()
    {
        HttpContext httpContext = CreateHttpContext(tenantId: null);

        HistoryRequestContext? result = await _validator.ValidateAsync(
            machineId: 1, range: "1h", httpContext, CancellationToken.None);

        await Assert.That(result).IsNull();
        await Assert.That(httpContext.Response.StatusCode).IsEqualTo(403);
    }

    // ================================================================
    // Machine not found (404)
    // ================================================================

    [Test]
    public async Task MachineNotFound_Returns404AndNull()
    {
        HttpContext httpContext = CreateHttpContext(tenantId: 1);
        _machineRepo.GetActiveMachineByIdAsync(1, 1, Arg.Any<CancellationToken>())
            .Returns((Machine?)null);

        HistoryRequestContext? result = await _validator.ValidateAsync(
            machineId: 1, range: "1h", httpContext, CancellationToken.None);

        await Assert.That(result).IsNull();
        await Assert.That(httpContext.Response.StatusCode).IsEqualTo(404);
    }

    // ================================================================
    // Invalid range (400)
    // ================================================================

    [Test]
    public async Task NullRange_Returns400AndNull()
    {
        HttpContext httpContext = CreateHttpContext(tenantId: 1);
        _machineRepo.GetActiveMachineByIdAsync(1, 1, Arg.Any<CancellationToken>())
            .Returns(CreateMachine(1, 1));
        _subscriptionService.GetRetentionDaysForTenantAsync(1, Arg.Any<CancellationToken>())
            .Returns(30);

        HistoryRequestContext? result = await _validator.ValidateAsync(
            machineId: 1, range: null, httpContext, CancellationToken.None);

        await Assert.That(result).IsNull();
        await Assert.That(httpContext.Response.StatusCode).IsEqualTo(400);
    }

    [Test]
    public async Task EmptyRange_Returns400AndNull()
    {
        HttpContext httpContext = CreateHttpContext(tenantId: 1);
        _machineRepo.GetActiveMachineByIdAsync(1, 1, Arg.Any<CancellationToken>())
            .Returns(CreateMachine(1, 1));
        _subscriptionService.GetRetentionDaysForTenantAsync(1, Arg.Any<CancellationToken>())
            .Returns(30);

        HistoryRequestContext? result = await _validator.ValidateAsync(
            machineId: 1, range: "", httpContext, CancellationToken.None);

        await Assert.That(result).IsNull();
        await Assert.That(httpContext.Response.StatusCode).IsEqualTo(400);
    }

    [Test]
    public async Task UnrecognizedRange_Returns400AndNull()
    {
        HttpContext httpContext = CreateHttpContext(tenantId: 1);
        _machineRepo.GetActiveMachineByIdAsync(1, 1, Arg.Any<CancellationToken>())
            .Returns(CreateMachine(1, 1));
        _subscriptionService.GetRetentionDaysForTenantAsync(1, Arg.Any<CancellationToken>())
            .Returns(30);

        HistoryRequestContext? result = await _validator.ValidateAsync(
            machineId: 1, range: "99d", httpContext, CancellationToken.None);

        await Assert.That(result).IsNull();
        await Assert.That(httpContext.Response.StatusCode).IsEqualTo(400);
    }

    // ================================================================
    // Retention exceeded (403)
    // ================================================================

    [Test]
    public async Task RangeExceedsRetention_Returns403AndNull()
    {
        HttpContext httpContext = CreateHttpContext(tenantId: 1);
        _machineRepo.GetActiveMachineByIdAsync(1, 1, Arg.Any<CancellationToken>())
            .Returns(CreateMachine(1, 1));

        // Retention of 1 day, but requesting 7 days
        _subscriptionService.GetRetentionDaysForTenantAsync(1, Arg.Any<CancellationToken>())
            .Returns(1);

        HistoryRequestContext? result = await _validator.ValidateAsync(
            machineId: 1, range: "7d", httpContext, CancellationToken.None);

        await Assert.That(result).IsNull();
        await Assert.That(httpContext.Response.StatusCode).IsEqualTo(403);
    }

    [Test]
    public async Task Range30dExceedsRetention7d_Returns403AndNull()
    {
        HttpContext httpContext = CreateHttpContext(tenantId: 1);
        _machineRepo.GetActiveMachineByIdAsync(1, 1, Arg.Any<CancellationToken>())
            .Returns(CreateMachine(1, 1));
        _subscriptionService.GetRetentionDaysForTenantAsync(1, Arg.Any<CancellationToken>())
            .Returns(7);

        HistoryRequestContext? result = await _validator.ValidateAsync(
            machineId: 1, range: "30d", httpContext, CancellationToken.None);

        await Assert.That(result).IsNull();
        await Assert.That(httpContext.Response.StatusCode).IsEqualTo(403);
    }

    // ================================================================
    // Successful validation
    // ================================================================

    [Test]
    public async Task ValidRequest_ReturnsContext()
    {
        HttpContext httpContext = CreateHttpContext(tenantId: 5);
        _machineRepo.GetActiveMachineByIdAsync(42, 5, Arg.Any<CancellationToken>())
            .Returns(CreateMachine(42, 5));
        _subscriptionService.GetRetentionDaysForTenantAsync(5, Arg.Any<CancellationToken>())
            .Returns(30);

        HistoryRequestContext? result = await _validator.ValidateAsync(
            machineId: 42, range: "24h", httpContext, CancellationToken.None);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.MachineId).IsEqualTo(42);
        await Assert.That(result.TenantId).IsEqualTo(5);
        await Assert.That(result.RangeStart).IsLessThan(result.RangeEnd);
    }

    [Test]
    public async Task ValidRequest1hRange_ReturnsContextWithCorrectWindow()
    {
        HttpContext httpContext = CreateHttpContext(tenantId: 1);
        _machineRepo.GetActiveMachineByIdAsync(1, 1, Arg.Any<CancellationToken>())
            .Returns(CreateMachine(1, 1));
        _subscriptionService.GetRetentionDaysForTenantAsync(1, Arg.Any<CancellationToken>())
            .Returns(30);

        HistoryRequestContext? result = await _validator.ValidateAsync(
            machineId: 1, range: "1h", httpContext, CancellationToken.None);

        await Assert.That(result).IsNotNull();

        // The window should be approximately 1 hour (3600 seconds)
        TimeSpan window = result!.RangeEnd - result.RangeStart;
        await Assert.That(window.TotalSeconds).IsGreaterThanOrEqualTo(3599);
        await Assert.That(window.TotalSeconds).IsLessThanOrEqualTo(3601);
    }

    [Test]
    public async Task RangeExactlyAtRetentionLimit_ReturnsContext()
    {
        HttpContext httpContext = CreateHttpContext(tenantId: 1);
        _machineRepo.GetActiveMachineByIdAsync(1, 1, Arg.Any<CancellationToken>())
            .Returns(CreateMachine(1, 1));

        // 7 day retention, requesting 7 day range -- should pass since 7 <= 7
        _subscriptionService.GetRetentionDaysForTenantAsync(1, Arg.Any<CancellationToken>())
            .Returns(7);

        HistoryRequestContext? result = await _validator.ValidateAsync(
            machineId: 1, range: "7d", httpContext, CancellationToken.None);

        await Assert.That(result).IsNotNull();
    }
}
