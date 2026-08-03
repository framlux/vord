// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Server.Endpoints.Grpc;
using Framlux.FleetManagement.Services.Core.Options;
using Framlux.FleetManagement.Services.Core.Handlers;
using Framlux.Vord.BillingGrpc;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Core.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Framlux.FleetManagement.UnitTest.Endpoints.Grpc;

/// <summary>
/// Unit tests for <see cref="BillingGatewayService"/>.
/// </summary>
public sealed class BillingGatewayServiceTests
{
    private const string TenantExternalId = "ext-tenant-abc";
    private const int TenantId = 42;

    private readonly ITenantRepository _tenantRepository;
    private readonly IBillingWebhookHandler _webhookHandler;
    private readonly ILogger<BillingGatewayService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="BillingGatewayServiceTests"/> class.
    /// </summary>
    public BillingGatewayServiceTests()
    {
        _tenantRepository = Substitute.For<ITenantRepository>();
        _webhookHandler = Substitute.For<IBillingWebhookHandler>();
        _logger = Substitute.For<ILogger<BillingGatewayService>>();
    }

    /// <summary>
    /// Builds the service under test. Caller authorisation is a collaborator here — its own
    /// rules are proven in <c>CertificateSubjectAuthorizerTests</c> and end to end in the
    /// functional gRPC suite. What matters at this level is that the service consults it before
    /// doing any work and propagates its rejection unchanged.
    /// </summary>
    private BillingGatewayService CreateService(StatusCode? rejectWith = null)
    {
        IServiceScopeFactory scopeFactory = CreateScopeFactory();

        return new BillingGatewayService(scopeFactory, CreateAuthorizer(rejectWith), _logger);
    }

    private static IInternalCallerAuthorizer CreateAuthorizer(StatusCode? rejectWith)
    {
        IInternalCallerAuthorizer authorizer = Substitute.For<IInternalCallerAuthorizer>();
        if (rejectWith is not null)
        {
            authorizer
                .When(a => a.Authorize(Arg.Any<ServerCallContext>()))
                .Do(_ => throw new RpcException(new Status(rejectWith.Value, "Rejected")));
        }

        return authorizer;
    }

    private IServiceScopeFactory CreateScopeFactory()
    {
        IServiceProvider serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(ITenantRepository)).Returns(_tenantRepository);
        serviceProvider.GetService(typeof(IBillingWebhookHandler)).Returns(_webhookHandler);

        IServiceScope scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(serviceProvider);

        IServiceScopeFactory scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(scope);

        return scopeFactory;
    }

    private static ServerCallContext CreateContext()
    {
        Metadata headers = new Metadata();

        return TestServerCallContext.Create(
            method: "Test",
            host: "localhost",
            deadline: DateTime.UtcNow.AddMinutes(1),
            requestHeaders: headers,
            cancellationToken: CancellationToken.None,
            peer: "127.0.0.1",
            authContext: null,
            contextPropagationToken: null,
            writeHeadersFunc: _ => Task.CompletedTask,
            writeOptionsGetter: () => new WriteOptions(),
            writeOptionsSetter: _ => { });
    }

    private static BillingActionRequest CreateRequest(BillingAction action, string tenantExternalId = TenantExternalId)
    {
        return new BillingActionRequest
        {
            TenantExternalId = tenantExternalId,
            Action = action
        };
    }

    private void SetupTenantFound()
    {
        _tenantRepository.GetTenantByExternalIdAsync(TenantExternalId, Arg.Any<CancellationToken>())
            .Returns(new Tenant
            {
                Id = TenantId,
                ExternalId = TenantExternalId,
                Name = "Test Tenant",
                IsActive = true,
                LogoUrl = string.Empty,
                CreatedAt = DateTimeOffset.UtcNow,
                CreatedByUserId = 1
            });
    }

    private void SetupTenantNotFound()
    {
        _tenantRepository.GetTenantByExternalIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((Tenant?)null);
    }

    // ────────────────────────────────────────────────────────────────
    // API Key Validation
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// When no permitted client subject is configured the service cannot make an authorisation
    /// decision, so it makes none and reports Unavailable.
    /// </summary>
    [Test]
    public async Task ProcessBillingAction_AuthorizerReportsUnconfigured_ThrowsUnavailable()
    {
        BillingGatewayService service = CreateService(rejectWith: StatusCode.Unavailable);
        ServerCallContext context = CreateContext();
        BillingActionRequest request = CreateRequest(BillingAction.UpgradeToPro);

        RpcException? exception = await Assert.ThrowsAsync<RpcException>(
            async () => await service.ProcessBillingAction(request, context));

        await Assert.That(exception).IsNotNull();
        await Assert.That(exception!.StatusCode).IsEqualTo(StatusCode.Unavailable);
    }

    /// <summary>
    /// A caller that presented no client certificate is rejected as unauthenticated.
    /// </summary>
    [Test]
    public async Task ProcessBillingAction_NoClientCertificate_ThrowsUnauthenticated()
    {
        BillingGatewayService service = CreateService(rejectWith: StatusCode.Unauthenticated);
        ServerCallContext context = CreateContext();
        BillingActionRequest request = CreateRequest(BillingAction.UpgradeToPro);

        RpcException? exception = await Assert.ThrowsAsync<RpcException>(
            async () => await service.ProcessBillingAction(request, context));

        await Assert.That(exception).IsNotNull();
        await Assert.That(exception!.StatusCode).IsEqualTo(StatusCode.Unauthenticated);
    }

    /// <summary>
    /// A caller holding a certificate the internal CA issued, but whose subject is not on the
    /// permitted list, is rejected. This is the case that distinguishes authorising on identity
    /// from merely accepting any valid certificate.
    /// </summary>
    [Test]
    public async Task ProcessBillingAction_NonPermittedCertificateSubject_ThrowsPermissionDenied()
    {
        BillingGatewayService service = CreateService(rejectWith: StatusCode.PermissionDenied);
        ServerCallContext context = CreateContext();
        BillingActionRequest request = CreateRequest(BillingAction.UpgradeToPro);

        RpcException? exception = await Assert.ThrowsAsync<RpcException>(
            async () => await service.ProcessBillingAction(request, context));

        await Assert.That(exception).IsNotNull();
        await Assert.That(exception!.StatusCode).IsEqualTo(StatusCode.PermissionDenied);
    }

    /// <summary>
    /// When the caller is authorised and the tenant exists, the service should succeed.
    /// </summary>
    [Test]
    public async Task ProcessBillingAction_AuthorizedCaller_Succeeds()
    {
        BillingGatewayService service = CreateService();
        ServerCallContext context = CreateContext();
        SetupTenantFound();
        BillingActionRequest request = CreateRequest(BillingAction.UpgradeToPro);

        BillingActionResponse response = await service.ProcessBillingAction(request, context);

        await Assert.That(response.Success).IsTrue();
        await Assert.That(response.Message).IsEqualTo("OK");
    }

    // ────────────────────────────────────────────────────────────────
    // Tenant Lookup
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// When the tenant is not found by external ID, the service should return NotFound.
    /// </summary>
    [Test]
    public async Task ProcessBillingAction_TenantNotFound_ThrowsNotFound()
    {
        BillingGatewayService service = CreateService();
        ServerCallContext context = CreateContext();
        SetupTenantNotFound();
        BillingActionRequest request = CreateRequest(BillingAction.UpgradeToPro, "nonexistent-ext-id");

        RpcException? exception = await Assert.ThrowsAsync<RpcException>(
            async () => await service.ProcessBillingAction(request, context));

        await Assert.That(exception).IsNotNull();
        await Assert.That(exception!.StatusCode).IsEqualTo(StatusCode.NotFound);
        await Assert.That(exception.Status.Detail).Contains("nonexistent-ext-id");
    }

    // ────────────────────────────────────────────────────────────────
    // Billing Action Dispatch: UpgradeToPro
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// UpgradeToPro action should call HandleCheckoutCompletedAsync with Pro tier.
    /// </summary>
    [Test]
    public async Task ProcessBillingAction_UpgradeToPro_CallsHandleCheckoutCompletedWithPro()
    {
        BillingGatewayService service = CreateService();
        ServerCallContext context = CreateContext();
        SetupTenantFound();
        BillingActionRequest request = CreateRequest(BillingAction.UpgradeToPro);

        BillingActionResponse response = await service.ProcessBillingAction(request, context);

        await Assert.That(response.Success).IsTrue();
        await _webhookHandler.Received(1).HandleCheckoutCompletedAsync(
            TenantId, SubscriptionTier.Pro, Arg.Any<CancellationToken>());
    }

    // ────────────────────────────────────────────────────────────────
    // Billing Action Dispatch: UpgradeToTeam
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// UpgradeToTeam action should call HandleCheckoutCompletedAsync with Team tier.
    /// </summary>
    [Test]
    public async Task ProcessBillingAction_UpgradeToTeam_CallsHandleCheckoutCompletedWithTeam()
    {
        BillingGatewayService service = CreateService();
        ServerCallContext context = CreateContext();
        SetupTenantFound();
        BillingActionRequest request = CreateRequest(BillingAction.UpgradeToTeam);

        BillingActionResponse response = await service.ProcessBillingAction(request, context);

        await Assert.That(response.Success).IsTrue();
        await _webhookHandler.Received(1).HandleCheckoutCompletedAsync(
            TenantId, SubscriptionTier.Team, Arg.Any<CancellationToken>());
    }

    // ────────────────────────────────────────────────────────────────
    // Billing Action Dispatch: DowngradeToFree
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// DowngradeToFree action should call HandleSubscriptionDeletedAsync.
    /// </summary>
    [Test]
    public async Task ProcessBillingAction_DowngradeToFree_CallsHandleSubscriptionDeleted()
    {
        BillingGatewayService service = CreateService();
        ServerCallContext context = CreateContext();
        SetupTenantFound();
        BillingActionRequest request = CreateRequest(BillingAction.DowngradeToFree);

        BillingActionResponse response = await service.ProcessBillingAction(request, context);

        await Assert.That(response.Success).IsTrue();
        await _webhookHandler.Received(1).HandleSubscriptionDeletedAsync(
            TenantId, Arg.Any<CancellationToken>());
    }

    // ────────────────────────────────────────────────────────────────
    // Billing Action Dispatch: DowngradeToPro
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// DowngradeToPro action should call HandleDowngradeToProAsync.
    /// </summary>
    [Test]
    public async Task ProcessBillingAction_DowngradeToPro_CallsHandleDowngradeToPro()
    {
        BillingGatewayService service = CreateService();
        ServerCallContext context = CreateContext();
        SetupTenantFound();
        BillingActionRequest request = CreateRequest(BillingAction.DowngradeToPro);

        BillingActionResponse response = await service.ProcessBillingAction(request, context);

        await Assert.That(response.Success).IsTrue();
        await _webhookHandler.Received(1).HandleDowngradeToProAsync(
            TenantId, Arg.Any<CancellationToken>());
    }

    // ────────────────────────────────────────────────────────────────
    // Billing Action Dispatch: UpdatePeriodEnd
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// UpdatePeriodEnd action should call HandleSubscriptionUpdatedAsync with the period end date.
    /// </summary>
    [Test]
    public async Task ProcessBillingAction_UpdatePeriodEnd_CallsHandleSubscriptionUpdated()
    {
        BillingGatewayService service = CreateService();
        ServerCallContext context = CreateContext();
        SetupTenantFound();

        DateTimeOffset expectedPeriodEnd = new DateTimeOffset(2026, 6, 15, 0, 0, 0, TimeSpan.Zero);
        BillingActionRequest request = CreateRequest(BillingAction.UpdatePeriodEnd);
        request.CurrentPeriodEnd = Timestamp.FromDateTimeOffset(expectedPeriodEnd);

        BillingActionResponse response = await service.ProcessBillingAction(request, context);

        await Assert.That(response.Success).IsTrue();
        await _webhookHandler.Received(1).HandleSubscriptionUpdatedAsync(
            TenantId, expectedPeriodEnd, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// UpdatePeriodEnd action with null CurrentPeriodEnd should throw InvalidArgument.
    /// </summary>
    [Test]
    public async Task ProcessBillingAction_UpdatePeriodEnd_NullPeriodEnd_ThrowsInvalidArgument()
    {
        BillingGatewayService service = CreateService();
        ServerCallContext context = CreateContext();
        SetupTenantFound();
        BillingActionRequest request = CreateRequest(BillingAction.UpdatePeriodEnd);
        // CurrentPeriodEnd is not set, so it defaults to null

        RpcException? exception = await Assert.ThrowsAsync<RpcException>(
            async () => await service.ProcessBillingAction(request, context));

        await Assert.That(exception).IsNotNull();
        await Assert.That(exception!.StatusCode).IsEqualTo(StatusCode.InvalidArgument);
        await Assert.That(exception.Status.Detail).Contains("CurrentPeriodEnd");
    }

    // ────────────────────────────────────────────────────────────────
    // Billing Action Dispatch: SetPastDue
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// SetPastDue action should call HandlePaymentFailedAsync.
    /// </summary>
    [Test]
    public async Task ProcessBillingAction_SetPastDue_CallsHandlePaymentFailed()
    {
        BillingGatewayService service = CreateService();
        ServerCallContext context = CreateContext();
        SetupTenantFound();
        BillingActionRequest request = CreateRequest(BillingAction.SetPastDue);

        BillingActionResponse response = await service.ProcessBillingAction(request, context);

        await Assert.That(response.Success).IsTrue();
        await _webhookHandler.Received(1).HandlePaymentFailedAsync(
            TenantId, Arg.Any<CancellationToken>());
    }

    // ────────────────────────────────────────────────────────────────
    // Billing Action Dispatch: SetActive
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// SetActive action should call HandlePaymentSucceededAsync.
    /// </summary>
    [Test]
    public async Task ProcessBillingAction_SetActive_CallsHandlePaymentSucceeded()
    {
        BillingGatewayService service = CreateService();
        ServerCallContext context = CreateContext();
        SetupTenantFound();
        BillingActionRequest request = CreateRequest(BillingAction.SetActive);

        BillingActionResponse response = await service.ProcessBillingAction(request, context);

        await Assert.That(response.Success).IsTrue();
        await _webhookHandler.Received(1).HandlePaymentSucceededAsync(
            TenantId, Arg.Any<CancellationToken>());
    }

    // ────────────────────────────────────────────────────────────────
    // Billing Action Dispatch: CancelAccount
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// CancelAccount action should call HandleAccountCanceledAsync.
    /// </summary>
    [Test]
    public async Task ProcessBillingAction_CancelAccount_CallsHandleAccountCanceled()
    {
        BillingGatewayService service = CreateService();
        ServerCallContext context = CreateContext();
        SetupTenantFound();
        BillingActionRequest request = CreateRequest(BillingAction.CancelAccount);

        BillingActionResponse response = await service.ProcessBillingAction(request, context);

        await Assert.That(response.Success).IsTrue();
        await _webhookHandler.Received(1).HandleAccountCanceledAsync(
            TenantId, Arg.Any<CancellationToken>());
    }

    // ────────────────────────────────────────────────────────────────
    // Unknown Action
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// An unrecognized billing action value should throw InvalidArgument.
    /// </summary>
    [Test]
    public async Task ProcessBillingAction_UnknownAction_ThrowsInvalidArgument()
    {
        BillingGatewayService service = CreateService();
        ServerCallContext context = CreateContext();
        SetupTenantFound();
        BillingActionRequest request = CreateRequest((BillingAction)999);

        RpcException? exception = await Assert.ThrowsAsync<RpcException>(
            async () => await service.ProcessBillingAction(request, context));

        await Assert.That(exception).IsNotNull();
        await Assert.That(exception!.StatusCode).IsEqualTo(StatusCode.InvalidArgument);
        await Assert.That(exception.Status.Detail).Contains("Unknown billing action");
    }

    // ────────────────────────────────────────────────────────────────
    // Response Structure
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// All successful actions should return Success=true and Message="OK".
    /// </summary>
    [Test]
    public async Task ProcessBillingAction_SuccessfulAction_ReturnsExpectedResponse()
    {
        BillingGatewayService service = CreateService();
        ServerCallContext context = CreateContext();
        SetupTenantFound();
        BillingActionRequest request = CreateRequest(BillingAction.SetActive);

        BillingActionResponse response = await service.ProcessBillingAction(request, context);

        await Assert.That(response.Success).IsTrue();
        await Assert.That(response.Message).IsEqualTo("OK");
    }

    // ────────────────────────────────────────────────────────────────
    // Handler Not Called on Auth Failure
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// When authentication fails, the webhook handler should not be called.
    /// </summary>
    [Test]
    public async Task ProcessBillingAction_AuthFailure_DoesNotCallHandler()
    {
        BillingGatewayService service = CreateService(rejectWith: StatusCode.PermissionDenied);
        ServerCallContext context = CreateContext();
        SetupTenantFound();
        BillingActionRequest request = CreateRequest(BillingAction.UpgradeToPro);

        try
        {
            await service.ProcessBillingAction(request, context);
        }
        catch (RpcException)
        {
            // Expected
        }

        await _webhookHandler.DidNotReceive().HandleCheckoutCompletedAsync(
            Arg.Any<int>(), Arg.Any<SubscriptionTier>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// When the tenant is not found, the webhook handler should not be called.
    /// </summary>
    [Test]
    public async Task ProcessBillingAction_TenantNotFound_DoesNotCallHandler()
    {
        BillingGatewayService service = CreateService();
        ServerCallContext context = CreateContext();
        SetupTenantNotFound();
        BillingActionRequest request = CreateRequest(BillingAction.UpgradeToPro);

        try
        {
            await service.ProcessBillingAction(request, context);
        }
        catch (RpcException)
        {
            // Expected
        }

        await _webhookHandler.DidNotReceive().HandleCheckoutCompletedAsync(
            Arg.Any<int>(), Arg.Any<SubscriptionTier>(), Arg.Any<CancellationToken>());
    }
}
