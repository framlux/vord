// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Server.Endpoints.Web.Tenants;
using Framlux.FleetManagement.Server.Services.Billing;
using Framlux.FleetManagement.Server.Services.Handlers;
using Framlux.FleetManagement.Server.Services.Infrastructure;
using Framlux.FleetManagement.Server.Services.Security;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB.Async;
using LinqToDB;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Framlux.FleetManagement.Test.Services.Handlers;

/// <summary>
/// Tests for <see cref="TenantOidcHandler"/>.
/// </summary>
public class TenantOidcHandlerTests
{
    private static TenantOidcHandler CreateHandler(
        TestDatabaseFactory dbFactory,
        ISubscriptionService? subscriptionService = null,
        IOidcSecretProtector? secretProtector = null)
    {
        subscriptionService ??= Substitute.For<ISubscriptionService>();
        secretProtector ??= Substitute.For<IOidcSecretProtector>();

        return new TenantOidcHandler(CreateRepo(dbFactory), subscriptionService, secretProtector);
    }

    private static ISubscriptionService CreateTeamSubscription(int tenantId)
    {
        ISubscriptionService svc = Substitute.For<ISubscriptionService>();
        TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: tenantId, tier: SubscriptionTier.Team);
        svc.GetSubscriptionForTenantAsync(tenantId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TenantSubscription?>(sub));

        return svc;
    }

    private static ISubscriptionService CreateFreeSubscription(int tenantId)
    {
        ISubscriptionService svc = Substitute.For<ISubscriptionService>();
        TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: tenantId, tier: SubscriptionTier.Free);
        svc.GetSubscriptionForTenantAsync(tenantId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TenantSubscription?>(sub));

        return svc;
    }

    // ========== GetConfigAsync tests ==========

    [Test]
    public async Task GetConfigAsync_NullClaimTenantId_ReturnsNotFound()
    {
        using TestDatabaseFactory dbFactory = new();
        TenantOidcHandler handler = CreateHandler(dbFactory);

        ServiceResult<TenantOidcConfigDto> result = await handler.GetConfigAsync(1, null, CancellationToken.None);

        await Assert.That(result.IsNotFound).IsTrue();
    }

    [Test]
    public async Task GetConfigAsync_TenantMismatch_ReturnsNotFound()
    {
        using TestDatabaseFactory dbFactory = new();
        TenantOidcHandler handler = CreateHandler(dbFactory);

        ServiceResult<TenantOidcConfigDto> result = await handler.GetConfigAsync(1, 2, CancellationToken.None);

        await Assert.That(result.IsNotFound).IsTrue();
    }

    [Test]
    public async Task GetConfigAsync_NotTeamTier_Returns403()
    {
        using TestDatabaseFactory dbFactory = new();
        ISubscriptionService subService = CreateFreeSubscription(1);
        TenantOidcHandler handler = CreateHandler(dbFactory, subscriptionService: subService);

        ServiceResult<TenantOidcConfigDto> result = await handler.GetConfigAsync(1, 1, CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(403);
    }

    [Test]
    public async Task GetConfigAsync_NoConfig_ReturnsEmptyDto()
    {
        using TestDatabaseFactory dbFactory = new();
        ISubscriptionService subService = CreateTeamSubscription(1);
        TenantOidcHandler handler = CreateHandler(dbFactory, subscriptionService: subService);

        ServiceResult<TenantOidcConfigDto> result = await handler.GetConfigAsync(1, 1, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Data!.Authority).IsEqualTo(string.Empty);
        await Assert.That(result.Data!.ClientId).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task GetConfigAsync_WithConfig_ReturnsMaskedSecret()
    {
        using TestDatabaseFactory dbFactory = new();
        TenantOidcConfiguration config = TestDataBuilder.BuildTenantOidcConfiguration(tenantId: 1);
        await dbFactory.Context.InsertAsync(config);

        ISubscriptionService subService = CreateTeamSubscription(1);
        TenantOidcHandler handler = CreateHandler(dbFactory, subscriptionService: subService);

        ServiceResult<TenantOidcConfigDto> result = await handler.GetConfigAsync(1, 1, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Data!.Authority).IsEqualTo("https://login.example.com");
        await Assert.That(result.Data!.ClientId).IsEqualTo("test-client-id");
        await Assert.That(result.Data!.ClientSecret).IsEqualTo("********");
        await Assert.That(result.Data!.IsEnabled).IsTrue();
    }

    // ========== UpdateConfigAsync tests ==========

    [Test]
    public async Task UpdateConfigAsync_NullClaimTenantId_ReturnsNotFound()
    {
        using TestDatabaseFactory dbFactory = new();
        TenantOidcHandler handler = CreateHandler(dbFactory);

        TenantOidcConfigDto request = new() { Authority = "https://example.com" };
        ServiceResult<TenantOidcConfigDto> result = await handler.UpdateConfigAsync(1, null, request, CancellationToken.None);

        await Assert.That(result.IsNotFound).IsTrue();
    }

    [Test]
    public async Task UpdateConfigAsync_TenantMismatch_ReturnsNotFound()
    {
        using TestDatabaseFactory dbFactory = new();
        TenantOidcHandler handler = CreateHandler(dbFactory);

        TenantOidcConfigDto request = new() { Authority = "https://example.com" };
        ServiceResult<TenantOidcConfigDto> result = await handler.UpdateConfigAsync(1, 2, request, CancellationToken.None);

        await Assert.That(result.IsNotFound).IsTrue();
    }

    [Test]
    public async Task UpdateConfigAsync_NotTeamTier_Returns403()
    {
        using TestDatabaseFactory dbFactory = new();
        ISubscriptionService subService = CreateFreeSubscription(1);
        TenantOidcHandler handler = CreateHandler(dbFactory, subscriptionService: subService);

        TenantOidcConfigDto request = new() { Authority = "https://example.com" };
        ServiceResult<TenantOidcConfigDto> result = await handler.UpdateConfigAsync(1, 1, request, CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(403);
    }

    [Test]
    public async Task UpdateConfigAsync_EmptyAuthorityUrl_Returns400()
    {
        using TestDatabaseFactory dbFactory = new();
        ISubscriptionService subService = CreateTeamSubscription(1);
        TenantOidcHandler handler = CreateHandler(dbFactory, subscriptionService: subService);

        TenantOidcConfigDto request = new() { Authority = "" };
        ServiceResult<TenantOidcConfigDto> result = await handler.UpdateConfigAsync(1, 1, request, CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(400);
    }

    [Test]
    public async Task UpdateConfigAsync_WhitespaceAuthorityUrl_Returns400()
    {
        using TestDatabaseFactory dbFactory = new();
        ISubscriptionService subService = CreateTeamSubscription(1);
        TenantOidcHandler handler = CreateHandler(dbFactory, subscriptionService: subService);

        TenantOidcConfigDto request = new() { Authority = "   " };
        ServiceResult<TenantOidcConfigDto> result = await handler.UpdateConfigAsync(1, 1, request, CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(400);
    }

    // ========== UpdateConfigAsync success path tests ==========

    [Test]
    public async Task UpdateConfigAsync_NoExistingConfig_CreatesNewConfig()
    {
        using TestDatabaseFactory dbFactory = new();
        ISubscriptionService subService = CreateTeamSubscription(1);
        IOidcSecretProtector protector = Substitute.For<IOidcSecretProtector>();
        protector.Protect(Arg.Any<string>()).Returns(callInfo => $"protected-{callInfo.ArgAt<string>(0)}");
        TenantOidcHandler handler = CreateHandler(dbFactory, subscriptionService: subService, secretProtector: protector);

        TenantOidcConfigDto request = new()
        {
            Authority = "https://accounts.google.com",
            ClientId = "new-client",
            ClientSecret = "my-secret",
            EmailDomain = "test.com",
            IsEnabled = true,
        };
        ServiceResult<TenantOidcConfigDto> result = await handler.UpdateConfigAsync(1, 1, request, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();

        TenantOidcConfiguration? config = await dbFactory.Context.TenantOidcConfigurations
            .FirstOrDefaultAsync(c => c.TenantId == 1);
        await Assert.That(config).IsNotNull();
        await Assert.That(config!.Authority).IsEqualTo("https://accounts.google.com");
        await Assert.That(config.ClientId).IsEqualTo("new-client");
        await Assert.That(config.ClientSecret).IsEqualTo("protected-my-secret");
    }

    [Test]
    public async Task UpdateConfigAsync_ExistingConfig_UpdatesConfig()
    {
        using TestDatabaseFactory dbFactory = new();
        TenantOidcConfiguration existing = TestDataBuilder.BuildTenantOidcConfiguration(tenantId: 1);
        await dbFactory.Context.InsertAsync(existing);

        ISubscriptionService subService = CreateTeamSubscription(1);
        IOidcSecretProtector protector = Substitute.For<IOidcSecretProtector>();
        protector.Protect(Arg.Any<string>()).Returns(callInfo => $"protected-{callInfo.ArgAt<string>(0)}");
        TenantOidcHandler handler = CreateHandler(dbFactory, subscriptionService: subService, secretProtector: protector);

        TenantOidcConfigDto request = new()
        {
            Authority = "https://accounts.google.com",
            ClientId = "updated-client",
            ClientSecret = "updated-secret",
            EmailDomain = "test.com",
            IsEnabled = true,
        };
        ServiceResult<TenantOidcConfigDto> result = await handler.UpdateConfigAsync(1, 1, request, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();

        TenantOidcConfiguration? config = await dbFactory.Context.TenantOidcConfigurations
            .FirstOrDefaultAsync(c => c.TenantId == 1);
        await Assert.That(config).IsNotNull();
        await Assert.That(config!.Authority).IsEqualTo("https://accounts.google.com");
        await Assert.That(config.ClientId).IsEqualTo("updated-client");
    }

    [Test]
    public async Task UpdateConfigAsync_MaskedSecret_PreservesExistingSecret()
    {
        using TestDatabaseFactory dbFactory = new();
        TenantOidcConfiguration existing = TestDataBuilder.BuildTenantOidcConfiguration(
            tenantId: 1,
            clientSecret: "original-encrypted");
        await dbFactory.Context.InsertAsync(existing);

        ISubscriptionService subService = CreateTeamSubscription(1);
        IOidcSecretProtector protector = Substitute.For<IOidcSecretProtector>();
        protector.Protect(Arg.Any<string>()).Returns(callInfo => $"protected-{callInfo.ArgAt<string>(0)}");
        TenantOidcHandler handler = CreateHandler(dbFactory, subscriptionService: subService, secretProtector: protector);

        TenantOidcConfigDto request = new()
        {
            Authority = "https://accounts.google.com",
            ClientId = "test-client-id",
            ClientSecret = "********",
            EmailDomain = "test.com",
            IsEnabled = true,
        };
        ServiceResult<TenantOidcConfigDto> result = await handler.UpdateConfigAsync(1, 1, request, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();

        TenantOidcConfiguration? config = await dbFactory.Context.TenantOidcConfigurations
            .FirstOrDefaultAsync(c => c.TenantId == 1);
        await Assert.That(config).IsNotNull();
        await Assert.That(config!.ClientSecret).IsEqualTo("original-encrypted");
    }

    [Test]
    public async Task UpdateConfigAsync_NewConfig_ResponseMasksClientSecret()
    {
        using TestDatabaseFactory dbFactory = new();
        ISubscriptionService subService = CreateTeamSubscription(1);
        IOidcSecretProtector protector = Substitute.For<IOidcSecretProtector>();
        protector.Protect(Arg.Any<string>()).Returns(callInfo => $"protected-{callInfo.ArgAt<string>(0)}");
        TenantOidcHandler handler = CreateHandler(dbFactory, subscriptionService: subService, secretProtector: protector);

        TenantOidcConfigDto request = new()
        {
            Authority = "https://accounts.google.com",
            ClientId = "new-client",
            ClientSecret = "my-plaintext-secret",
            EmailDomain = "test.com",
            IsEnabled = true,
        };
        ServiceResult<TenantOidcConfigDto> result = await handler.UpdateConfigAsync(1, 1, request, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Data!.ClientSecret).IsEqualTo("********");
        await Assert.That(result.Data!.Authority).IsEqualTo("https://accounts.google.com");
        await Assert.That(result.Data!.ClientId).IsEqualTo("new-client");
    }

    [Test]
    public async Task UpdateConfigAsync_ExistingConfig_ResponseMasksClientSecret()
    {
        using TestDatabaseFactory dbFactory = new();
        TenantOidcConfiguration existing = TestDataBuilder.BuildTenantOidcConfiguration(tenantId: 1);
        await dbFactory.Context.InsertAsync(existing);

        ISubscriptionService subService = CreateTeamSubscription(1);
        IOidcSecretProtector protector = Substitute.For<IOidcSecretProtector>();
        protector.Protect(Arg.Any<string>()).Returns(callInfo => $"protected-{callInfo.ArgAt<string>(0)}");
        TenantOidcHandler handler = CreateHandler(dbFactory, subscriptionService: subService, secretProtector: protector);

        TenantOidcConfigDto request = new()
        {
            Authority = "https://accounts.google.com",
            ClientId = "updated-client",
            ClientSecret = "updated-plaintext-secret",
            EmailDomain = "test.com",
            IsEnabled = true,
        };
        ServiceResult<TenantOidcConfigDto> result = await handler.UpdateConfigAsync(1, 1, request, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Data!.ClientSecret).IsEqualTo("********");
    }

    // ========== Helper methods ==========

    private static DatabaseRepository CreateRepo(TestDatabaseFactory dbFactory)
    {
        return new DatabaseRepository(dbFactory.Context, new NullLogger<DatabaseRepository>());
    }
}
