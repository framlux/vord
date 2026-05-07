// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Server.Services.Billing;
using Framlux.Vord.BillingGrpc;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Framlux.FleetManagement.Test.Services;

/// <summary>
/// Tests for <see cref="BillingApiClient"/>.
/// </summary>
public sealed class BillingApiClientTests
{
    private static AsyncUnaryCall<T> CreateAsyncCall<T>(T response) where T : class
    {
        return new AsyncUnaryCall<T>(
            Task.FromResult(response),
            Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess,
            () => new Metadata(),
            () => { });
    }

    private static AsyncUnaryCall<T> CreateFaultedCall<T>(Exception ex) where T : class
    {
        return new AsyncUnaryCall<T>(
            Task.FromException<T>(ex),
            Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess,
            () => new Metadata(),
            () => { });
    }

    private static (BillingApiClient Client, BillingManagement.BillingManagementClient GrpcClient, ILogger<BillingApiClient> Logger) CreateSut()
    {
        BillingManagement.BillingManagementClient grpcClient = Substitute.For<BillingManagement.BillingManagementClient>();
        ILogger<BillingApiClient> logger = Substitute.For<ILogger<BillingApiClient>>();
        BillingApiClient client = new(grpcClient, logger);

        return (client, grpcClient, logger);
    }

    // --- UpdateQuantityAsync ---

    [Test]
    public async Task UpdateQuantityAsync_ReturnsTrueOnSuccess()
    {
        (BillingApiClient client, BillingManagement.BillingManagementClient grpc, ILogger<BillingApiClient> _) = CreateSut();
        grpc.UpdateSubscriptionQuantityAsync(
                Arg.Any<UpdateQuantityRequest>(), Arg.Any<Metadata>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(CreateAsyncCall(new UpdateQuantityResponse { Success = true }));

        bool result = await client.UpdateQuantityAsync("tenant-ext-1", 5, CancellationToken.None);

        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task UpdateQuantityAsync_ReturnsFalseAndLogsWarningOnFailureResponse()
    {
        (BillingApiClient client, BillingManagement.BillingManagementClient grpc, ILogger<BillingApiClient> logger) = CreateSut();
        grpc.UpdateSubscriptionQuantityAsync(
                Arg.Any<UpdateQuantityRequest>(), Arg.Any<Metadata>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(CreateAsyncCall(new UpdateQuantityResponse { Success = false, Message = "quota exceeded" }));

        bool result = await client.UpdateQuantityAsync("tenant-ext-1", 5, CancellationToken.None);

        await Assert.That(result).IsFalse();
        logger.Received().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Test]
    public async Task UpdateQuantityAsync_ReturnsFalseAndLogsErrorOnGrpcException()
    {
        (BillingApiClient client, BillingManagement.BillingManagementClient grpc, ILogger<BillingApiClient> logger) = CreateSut();
        grpc.UpdateSubscriptionQuantityAsync(
                Arg.Any<UpdateQuantityRequest>(), Arg.Any<Metadata>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(CreateFaultedCall<UpdateQuantityResponse>(new RpcException(new Status(StatusCode.Unavailable, "service down"))));

        bool result = await client.UpdateQuantityAsync("tenant-ext-1", 5, CancellationToken.None);

        await Assert.That(result).IsFalse();
        logger.Received().Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Test]
    public async Task UpdateQuantityAsync_ReturnsFalseOnGeneralException()
    {
        (BillingApiClient client, BillingManagement.BillingManagementClient grpc, ILogger<BillingApiClient> _) = CreateSut();
        grpc.UpdateSubscriptionQuantityAsync(
                Arg.Any<UpdateQuantityRequest>(), Arg.Any<Metadata>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(CreateFaultedCall<UpdateQuantityResponse>(new InvalidOperationException("unexpected")));

        bool result = await client.UpdateQuantityAsync("tenant-ext-1", 5, CancellationToken.None);

        await Assert.That(result).IsFalse();
    }

    // --- ReportMachineUsageAsync ---

    [Test]
    public async Task ReportMachineUsageAsync_ReturnsTrueOnSuccess()
    {
        (BillingApiClient client, BillingManagement.BillingManagementClient grpc, ILogger<BillingApiClient> _) = CreateSut();
        grpc.ReportMachineUsageAsync(Arg.Any<ReportMachineUsageRequest>(), Arg.Any<Metadata>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(CreateAsyncCall(new ReportMachineUsageResponse { Success = true, Message = "OK" }));

        bool result = await client.ReportMachineUsageAsync("tenant-ext-1", 5, CancellationToken.None);

        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task ReportMachineUsageAsync_ReturnsFalseOnFailureResponse()
    {
        (BillingApiClient client, BillingManagement.BillingManagementClient grpc, ILogger<BillingApiClient> logger) = CreateSut();
        grpc.ReportMachineUsageAsync(Arg.Any<ReportMachineUsageRequest>(), Arg.Any<Metadata>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(CreateAsyncCall(new ReportMachineUsageResponse { Success = false, Message = "No subscription" }));

        bool result = await client.ReportMachineUsageAsync("tenant-ext-1", 5, CancellationToken.None);

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task ReportMachineUsageAsync_ReturnsFalseOnException()
    {
        (BillingApiClient client, BillingManagement.BillingManagementClient grpc, ILogger<BillingApiClient> _) = CreateSut();
        grpc.ReportMachineUsageAsync(Arg.Any<ReportMachineUsageRequest>(), Arg.Any<Metadata>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(CreateFaultedCall<ReportMachineUsageResponse>(new RpcException(new Status(StatusCode.Internal, "error"))));

        bool result = await client.ReportMachineUsageAsync("tenant-ext-1", 5, CancellationToken.None);

        await Assert.That(result).IsFalse();
    }

    // --- CancelSubscriptionAsync ---

    [Test]
    public async Task CancelSubscriptionAsync_ReturnsTrueOnSuccess()
    {
        (BillingApiClient client, BillingManagement.BillingManagementClient grpc, ILogger<BillingApiClient> _) = CreateSut();
        grpc.CancelSubscriptionAsync(
                Arg.Any<CancelSubscriptionRequest>(), Arg.Any<Metadata>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(CreateAsyncCall(new CancelSubscriptionResponse { Success = true }));

        bool result = await client.CancelSubscriptionAsync("tenant-ext-1", PendingActionType.CancelAccount, CancellationToken.None);

        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task CancelSubscriptionAsync_ReturnsFalseAndLogsWarningOnFailure()
    {
        (BillingApiClient client, BillingManagement.BillingManagementClient grpc, ILogger<BillingApiClient> logger) = CreateSut();
        grpc.CancelSubscriptionAsync(
                Arg.Any<CancelSubscriptionRequest>(), Arg.Any<Metadata>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(CreateAsyncCall(new CancelSubscriptionResponse { Success = false, Message = "error" }));

        bool result = await client.CancelSubscriptionAsync("tenant-ext-1", PendingActionType.CancelAccount, CancellationToken.None);

        await Assert.That(result).IsFalse();
        logger.Received().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Test]
    public async Task CancelSubscriptionAsync_ReturnsFalseAndLogsErrorOnException()
    {
        (BillingApiClient client, BillingManagement.BillingManagementClient grpc, ILogger<BillingApiClient> logger) = CreateSut();
        grpc.CancelSubscriptionAsync(
                Arg.Any<CancelSubscriptionRequest>(), Arg.Any<Metadata>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(CreateFaultedCall<CancelSubscriptionResponse>(new RpcException(new Status(StatusCode.Unavailable, "down"))));

        bool result = await client.CancelSubscriptionAsync("tenant-ext-1", PendingActionType.CancelAccount, CancellationToken.None);

        await Assert.That(result).IsFalse();
        logger.Received().Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    // --- GetSubscriptionStatusAsync ---

    [Test]
    public async Task GetSubscriptionStatusAsync_ReturnsRecordFromResponse()
    {
        (BillingApiClient client, BillingManagement.BillingManagementClient grpc, ILogger<BillingApiClient> _) = CreateSut();
        grpc.GetSubscriptionStatusAsync(
                Arg.Any<GetSubscriptionStatusRequest>(), Arg.Any<Metadata>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(CreateAsyncCall(new GetSubscriptionStatusResponse
            {
                CancelAtPeriodEnd = true,
                StripeStatus = "active",
                PriceId = "price_pro_123",
                Quantity = 5,
                CurrentPeriodEnd = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(
                    new DateTimeOffset(2026, 4, 15, 0, 0, 0, TimeSpan.Zero))
            }));

        StripeSubscriptionStatus status = await client.GetSubscriptionStatusAsync("tenant-ext-1", CancellationToken.None);

        await Assert.That(status.CancelAtPeriodEnd).IsTrue();
        await Assert.That(status.StripeStatus).IsEqualTo("active");
        await Assert.That(status.PriceId).IsEqualTo("price_pro_123");
        await Assert.That(status.Quantity).IsEqualTo(5);
        await Assert.That(status.CurrentPeriodEnd).IsNotNull();
    }

    [Test]
    public async Task GetSubscriptionStatusAsync_GrpcFailure_ReturnsSafeFallback()
    {
        (BillingApiClient client, BillingManagement.BillingManagementClient grpc, ILogger<BillingApiClient> _) = CreateSut();
        grpc.GetSubscriptionStatusAsync(
                Arg.Any<GetSubscriptionStatusRequest>(), Arg.Any<Metadata>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(CreateFaultedCall<GetSubscriptionStatusResponse>(new RpcException(new Status(StatusCode.Unavailable, "down"))));

        StripeSubscriptionStatus status = await client.GetSubscriptionStatusAsync("tenant-ext-1", CancellationToken.None);

        await Assert.That(status.CancelAtPeriodEnd).IsFalse();
        await Assert.That(status.StripeStatus).IsEqualTo("none");
        await Assert.That(status.PriceId).IsEqualTo(string.Empty);
        await Assert.That(status.Quantity).IsEqualTo(0);
        await Assert.That(status.CurrentPeriodEnd).IsNull();
        await Assert.That(status.Tier).IsEqualTo(BillingTier.Unspecified);
    }

    // --- Parameter handling ---

    [Test]
    public async Task UpdateQuantityAsync_PassesCorrectParametersToGrpc()
    {
        (BillingApiClient client, BillingManagement.BillingManagementClient grpc, ILogger<BillingApiClient> _) = CreateSut();
        UpdateQuantityRequest? capturedRequest = null;
        grpc.UpdateSubscriptionQuantityAsync(
                Arg.Do<UpdateQuantityRequest>(r => capturedRequest = r), Arg.Any<Metadata>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(CreateAsyncCall(new UpdateQuantityResponse { Success = true }));

        await client.UpdateQuantityAsync("tenant-abc", 10, CancellationToken.None);

        await Assert.That(capturedRequest).IsNotNull();
        await Assert.That(capturedRequest!.TenantExternalId).IsEqualTo("tenant-abc");
        await Assert.That(capturedRequest.MachineCount).IsEqualTo(10);
    }

    [Test]
    public async Task CancelSubscriptionAsync_PassesCorrectTenantExternalIdToGrpc()
    {
        (BillingApiClient client, BillingManagement.BillingManagementClient grpc, ILogger<BillingApiClient> _) = CreateSut();
        CancelSubscriptionRequest? capturedRequest = null;
        grpc.CancelSubscriptionAsync(
                Arg.Do<CancelSubscriptionRequest>(r => capturedRequest = r), Arg.Any<Metadata>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(CreateAsyncCall(new CancelSubscriptionResponse { Success = true }));

        await client.CancelSubscriptionAsync("tenant-xyz", PendingActionType.CancelAccount, CancellationToken.None);

        await Assert.That(capturedRequest).IsNotNull();
        await Assert.That(capturedRequest!.TenantExternalId).IsEqualTo("tenant-xyz");
    }

    // --- gRPC deadline exceeded ---

    /// <summary>
    /// When the gRPC deadline is exceeded during UpdateQuantityAsync, the RpcException
    /// is caught and the method returns false.
    /// </summary>
    [Test]
    public async Task UpdateQuantityAsync_DeadlineExceeded_ReturnsFalse()
    {
        (BillingApiClient client, BillingManagement.BillingManagementClient grpc, ILogger<BillingApiClient> logger) = CreateSut();
        grpc.UpdateSubscriptionQuantityAsync(
                Arg.Any<UpdateQuantityRequest>(), Arg.Any<Metadata>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(CreateFaultedCall<UpdateQuantityResponse>(new RpcException(new Status(StatusCode.DeadlineExceeded, "deadline exceeded"))));

        bool result = await client.UpdateQuantityAsync("tenant-ext-1", 5, CancellationToken.None);

        await Assert.That(result).IsFalse();
        logger.Received().Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    /// <summary>
    /// When the gRPC deadline is exceeded during CancelSubscriptionAsync, the RpcException
    /// is caught and the method returns false.
    /// </summary>
    [Test]
    public async Task CancelSubscriptionAsync_DeadlineExceeded_ReturnsFalse()
    {
        (BillingApiClient client, BillingManagement.BillingManagementClient grpc, ILogger<BillingApiClient> logger) = CreateSut();
        grpc.CancelSubscriptionAsync(
                Arg.Any<CancelSubscriptionRequest>(), Arg.Any<Metadata>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(CreateFaultedCall<CancelSubscriptionResponse>(new RpcException(new Status(StatusCode.DeadlineExceeded, "deadline exceeded"))));

        bool result = await client.CancelSubscriptionAsync("tenant-ext-1", PendingActionType.CancelAccount, CancellationToken.None);

        await Assert.That(result).IsFalse();
        logger.Received().Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    /// <summary>
    /// When the gRPC deadline is exceeded during GetSubscriptionStatusAsync, the RpcException
    /// propagates because that method does not catch exceptions.
    /// </summary>
    [Test]
    public async Task GetSubscriptionStatusAsync_DeadlineExceeded_ReturnsSafeFallback()
    {
        (BillingApiClient client, BillingManagement.BillingManagementClient grpc, ILogger<BillingApiClient> _) = CreateSut();
        grpc.GetSubscriptionStatusAsync(
                Arg.Any<GetSubscriptionStatusRequest>(), Arg.Any<Metadata>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(CreateFaultedCall<GetSubscriptionStatusResponse>(new RpcException(new Status(StatusCode.DeadlineExceeded, "deadline exceeded"))));

        StripeSubscriptionStatus status = await client.GetSubscriptionStatusAsync("tenant-ext-1", CancellationToken.None);

        await Assert.That(status.CancelAtPeriodEnd).IsFalse();
        await Assert.That(status.StripeStatus).IsEqualTo("none");
        await Assert.That(status.Quantity).IsEqualTo(0);
    }

    /// <summary>
    /// When the gRPC deadline is exceeded during SwapSubscriptionPriceAsync, the RpcException
    /// is caught and the method returns false.
    /// </summary>
    [Test]
    public async Task SwapSubscriptionPriceAsync_DeadlineExceeded_ReturnsFalse()
    {
        (BillingApiClient client, BillingManagement.BillingManagementClient grpc, ILogger<BillingApiClient> logger) = CreateSut();
        grpc.SwapSubscriptionPriceAsync(
                Arg.Any<SwapSubscriptionPriceRequest>(), Arg.Any<Metadata>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(CreateFaultedCall<SwapSubscriptionPriceResponse>(new RpcException(new Status(StatusCode.DeadlineExceeded, "deadline exceeded"))));

        bool result = await client.SwapSubscriptionPriceAsync("tenant-ext-1", BillingTier.Pro, CancellationToken.None);

        await Assert.That(result).IsFalse();
        logger.Received().Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    /// <summary>
    /// When the gRPC deadline is exceeded during ResumeSubscriptionAsync, the RpcException
    /// is caught and the method returns false.
    /// </summary>
    [Test]
    public async Task ResumeSubscriptionAsync_DeadlineExceeded_ReturnsFalse()
    {
        (BillingApiClient client, BillingManagement.BillingManagementClient grpc, ILogger<BillingApiClient> logger) = CreateSut();
        grpc.ResumeSubscriptionAsync(
                Arg.Any<ResumeSubscriptionRequest>(), Arg.Any<Metadata>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(CreateFaultedCall<ResumeSubscriptionResponse>(new RpcException(new Status(StatusCode.DeadlineExceeded, "deadline exceeded"))));

        bool result = await client.ResumeSubscriptionAsync("tenant-ext-1", CancellationToken.None);

        await Assert.That(result).IsFalse();
        logger.Received().Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    // ========== GetUpcomingInvoice tests ==========

    [Test]
    public async Task GetUpcomingInvoiceAsync_NoInvoice_ReturnsNull()
    {
        (BillingApiClient client, BillingManagement.BillingManagementClient grpc, ILogger<BillingApiClient> _) = CreateSut();

        GetUpcomingInvoiceResponse response = new() { HasInvoice = false };
        grpc.GetUpcomingInvoiceAsync(
                Arg.Any<GetUpcomingInvoiceRequest>(), Arg.Any<Metadata>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(CreateAsyncCall(response));

        UpcomingInvoiceResult? result = await client.GetUpcomingInvoiceAsync("tenant-ext-1", CancellationToken.None);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task GetUpcomingInvoiceAsync_GrpcException_ReturnsNull()
    {
        (BillingApiClient client, BillingManagement.BillingManagementClient grpc, ILogger<BillingApiClient> logger) = CreateSut();

        grpc.GetUpcomingInvoiceAsync(
                Arg.Any<GetUpcomingInvoiceRequest>(), Arg.Any<Metadata>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(CreateFaultedCall<GetUpcomingInvoiceResponse>(new RpcException(new Status(StatusCode.Unavailable, "service unavailable"))));

        UpcomingInvoiceResult? result = await client.GetUpcomingInvoiceAsync("tenant-ext-1", CancellationToken.None);

        await Assert.That(result).IsNull();
        logger.Received().Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Test]
    public async Task GetUpcomingInvoiceAsync_HasInvoice_MapsAllFields()
    {
        (BillingApiClient client, BillingManagement.BillingManagementClient grpc, ILogger<BillingApiClient> _) = CreateSut();

        GetUpcomingInvoiceResponse response = new()
        {
            HasInvoice = true,
            AmountDueCents = 5000,
            Currency = "usd",
            UnitAmountCents = 1000,
        };
        grpc.GetUpcomingInvoiceAsync(
                Arg.Any<GetUpcomingInvoiceRequest>(), Arg.Any<Metadata>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(CreateAsyncCall(response));

        UpcomingInvoiceResult? result = await client.GetUpcomingInvoiceAsync("tenant-ext-1", CancellationToken.None);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.AmountDueCents).IsEqualTo(5000);
        await Assert.That(result.Currency).IsEqualTo("usd");
        await Assert.That(result.UnitAmountCents).IsEqualTo(1000);
    }

    // ========== ListInvoices tests ==========

    [Test]
    public async Task ListInvoicesAsync_GrpcException_ReturnsEmptyList()
    {
        (BillingApiClient client, BillingManagement.BillingManagementClient grpc, ILogger<BillingApiClient> logger) = CreateSut();

        grpc.ListInvoicesAsync(
                Arg.Any<ListInvoicesRequest>(), Arg.Any<Metadata>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(CreateFaultedCall<ListInvoicesResponse>(new RpcException(new Status(StatusCode.Unavailable, "service unavailable"))));

        List<InvoiceResult> result = await client.ListInvoicesAsync("tenant-ext-1", 12, CancellationToken.None);

        await Assert.That(result).IsNotNull();
        await Assert.That(result.Count).IsEqualTo(0);
        logger.Received().Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Test]
    public async Task ListInvoicesAsync_EmptyList_ReturnsEmptyList()
    {
        (BillingApiClient client, BillingManagement.BillingManagementClient grpc, ILogger<BillingApiClient> _) = CreateSut();

        ListInvoicesResponse response = new();
        grpc.ListInvoicesAsync(
                Arg.Any<ListInvoicesRequest>(), Arg.Any<Metadata>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(CreateAsyncCall(response));

        List<InvoiceResult> result = await client.ListInvoicesAsync("tenant-ext-1", 12, CancellationToken.None);

        await Assert.That(result).IsNotNull();
        await Assert.That(result.Count).IsEqualTo(0);
    }

    // ========== SwapSubscriptionPrice exception tests ==========

    [Test]
    public async Task SwapSubscriptionPriceAsync_GrpcException_ReturnsFalse()
    {
        (BillingApiClient client, BillingManagement.BillingManagementClient grpc, ILogger<BillingApiClient> logger) = CreateSut();

        grpc.SwapSubscriptionPriceAsync(
                Arg.Any<SwapSubscriptionPriceRequest>(), Arg.Any<Metadata>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(CreateFaultedCall<SwapSubscriptionPriceResponse>(new RpcException(new Status(StatusCode.Internal, "server error"))));

        bool result = await client.SwapSubscriptionPriceAsync("tenant-ext-1", BillingTier.Pro, CancellationToken.None);

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task SwapSubscriptionPriceAsync_SuccessResponseFalse_ReturnsFalse()
    {
        (BillingApiClient client, BillingManagement.BillingManagementClient grpc, ILogger<BillingApiClient> logger) = CreateSut();

        SwapSubscriptionPriceResponse response = new() { Success = false, Message = "Price not found" };
        grpc.SwapSubscriptionPriceAsync(
                Arg.Any<SwapSubscriptionPriceRequest>(), Arg.Any<Metadata>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(CreateAsyncCall(response));

        bool result = await client.SwapSubscriptionPriceAsync("tenant-ext-1", BillingTier.Unspecified, CancellationToken.None);

        await Assert.That(result).IsFalse();
    }

    // ========== ResumeSubscription exception tests ==========

    [Test]
    public async Task ResumeSubscriptionAsync_GrpcException_ReturnsFalse()
    {
        (BillingApiClient client, BillingManagement.BillingManagementClient grpc, ILogger<BillingApiClient> logger) = CreateSut();

        grpc.ResumeSubscriptionAsync(
                Arg.Any<ResumeSubscriptionRequest>(), Arg.Any<Metadata>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(CreateFaultedCall<ResumeSubscriptionResponse>(new RpcException(new Status(StatusCode.Internal, "server error"))));

        bool result = await client.ResumeSubscriptionAsync("tenant-ext-1", CancellationToken.None);

        await Assert.That(result).IsFalse();
    }
}
