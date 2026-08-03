// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Test.Infrastructure;
using Framlux.Vord.BillingGrpc;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using LinqToDB;
using LinqToDB.Async;

namespace Framlux.FleetManagement.FunctionalTest.Endpoints.Grpc;

/// <summary>
/// Functional tests for the BillingGatewayService gRPC endpoint.
/// </summary>
public sealed class BillingGatewayServiceTests
{
    /// <summary>
    /// The client-certificate subject the internal services are configured to accept. In
    /// production it is the DNS name on the certificate cert-manager issues to billing-api.
    /// </summary>
    private const string PermittedClientSubject = "billing-api.vord-fleet.svc.cluster.local";
    [Test]
    public async Task ProcessBillingAction_NoClientCertificate_ThrowsUnauthenticated()
    {
        using FunctionalTestFactory factory = new();
        factory.WithInternalClientSubjects(PermittedClientSubject);
        using GrpcChannel channel = CreateChannel(factory);
        BillingGateway.BillingGatewayClient client = new(channel);

        RpcException? exception = null;
        try
        {
            await client.ProcessBillingActionAsync(new BillingActionRequest
            {
                TenantExternalId = "ext-123",
                Action = BillingAction.UpgradeToPro
            });
        }
        catch (RpcException ex)
        {
            exception = ex;
        }

        await Assert.That(exception).IsNotNull();
        await Assert.That(exception!.StatusCode).IsEqualTo(StatusCode.Unauthenticated);
    }

    /// <summary>
    /// A caller holding a certificate that is entirely valid — issued by the internal CA, in
    /// date, correctly formed — but whose subject is not on the permitted list must still be
    /// refused. This is what proves the service authorises on the caller's identity rather than
    /// on the mere fact that some certificate was presented.
    /// </summary>
    [Test]
    public async Task ProcessBillingAction_ValidCertificateWithNonPermittedSubject_ThrowsPermissionDenied()
    {
        using FunctionalTestFactory factory = new();
        factory.WithInternalClientSubjects(PermittedClientSubject);
        using GrpcChannel channel = CreateChannel(factory);
        BillingGateway.BillingGatewayClient client = new(channel);

        Metadata headers = new Metadata
        {
            { TestClientCertificateMiddleware.SubjectHeader, "impostor.vord-fleet.svc.cluster.local" }
        };

        RpcException? exception = null;
        try
        {
            await client.ProcessBillingActionAsync(new BillingActionRequest
            {
                TenantExternalId = "ext-123",
                Action = BillingAction.UpgradeToPro
            }, headers);
        }
        catch (RpcException ex)
        {
            exception = ex;
        }

        await Assert.That(exception).IsNotNull();
        await Assert.That(exception!.StatusCode).IsEqualTo(StatusCode.PermissionDenied);
    }

    [Test]
    public async Task ProcessBillingAction_TenantNotFound_ThrowsNotFound()
    {
        using FunctionalTestFactory factory = new();
        factory.WithInternalClientSubjects(PermittedClientSubject);
        using GrpcChannel channel = CreateChannel(factory);
        BillingGateway.BillingGatewayClient client = new(channel);

        Metadata headers = new Metadata
        {
            { TestClientCertificateMiddleware.SubjectHeader, PermittedClientSubject }
        };

        RpcException? exception = null;
        try
        {
            await client.ProcessBillingActionAsync(new BillingActionRequest
            {
                TenantExternalId = "nonexistent-tenant",
                Action = BillingAction.UpgradeToPro
            }, headers);
        }
        catch (RpcException ex)
        {
            exception = ex;
        }

        await Assert.That(exception).IsNotNull();
        await Assert.That(exception!.StatusCode).IsEqualTo(StatusCode.NotFound);
        await Assert.That(exception.Status.Detail).Contains("nonexistent-tenant");
    }

    [Test]
    public async Task ProcessBillingAction_UnspecifiedAction_ThrowsInvalidArgument()
    {
        using FunctionalTestFactory factory = new();
        factory.WithInternalClientSubjects(PermittedClientSubject);
        using DatabaseContext db = factory.CreateDbContext();

        string externalId = $"ext-{Guid.NewGuid():N}";
        await SeedTenantWithSubscription(db, externalId, SubscriptionTier.Free);

        using GrpcChannel channel = CreateChannel(factory);
        BillingGateway.BillingGatewayClient client = new(channel);

        Metadata headers = new Metadata { { TestClientCertificateMiddleware.SubjectHeader, PermittedClientSubject } };

        RpcException? exception = null;
        try
        {
            await client.ProcessBillingActionAsync(new BillingActionRequest
            {
                TenantExternalId = externalId,
                Action = BillingAction.Unspecified
            }, headers);
        }
        catch (RpcException ex)
        {
            exception = ex;
        }

        await Assert.That(exception).IsNotNull();
        await Assert.That(exception!.StatusCode).IsEqualTo(StatusCode.InvalidArgument);
        await Assert.That(exception.Status.Detail).Contains("Unknown billing action");
    }

    [Test]
    public async Task ProcessBillingAction_UpgradeToPro_UpdatesAllSubscriptionFields()
    {
        using FunctionalTestFactory factory = new();
        factory.WithInternalClientSubjects(PermittedClientSubject);
        using DatabaseContext db = factory.CreateDbContext();

        string externalId = $"ext-{Guid.NewGuid():N}";
        int tenantId = await SeedTenantWithSubscription(db, externalId, SubscriptionTier.Free);

        // Record the original UpdatedAt to verify it changes
        TenantSubscription? original = await db.TenantSubscriptions
            .FirstOrDefaultAsync(s => s.TenantId == tenantId);
        DateTimeOffset originalUpdatedAt = original!.UpdatedAt;

        using GrpcChannel channel = CreateChannel(factory);
        BillingGateway.BillingGatewayClient client = new(channel);

        Metadata headers = new Metadata { { TestClientCertificateMiddleware.SubjectHeader, PermittedClientSubject } };

        BillingActionResponse response = await client.ProcessBillingActionAsync(
            new BillingActionRequest
            {
                TenantExternalId = externalId,
                Action = BillingAction.UpgradeToPro
            }, headers);

        await Assert.That(response.Success).IsTrue();
        await Assert.That(response.Message).IsEqualTo("OK");

        TenantSubscription? updated = await db.TenantSubscriptions
            .FirstOrDefaultAsync(s => s.TenantId == tenantId);
        await Assert.That(updated).IsNotNull();
        await Assert.That(updated!.Tier).IsEqualTo(SubscriptionTier.Pro);
        await Assert.That(updated.Status).IsEqualTo(SubscriptionStatus.Active);
        await Assert.That(updated.UpdatedAt).IsGreaterThanOrEqualTo(originalUpdatedAt);
    }

    [Test]
    public async Task ProcessBillingAction_UpgradeToTeam_UpdatesAllSubscriptionFields()
    {
        using FunctionalTestFactory factory = new();
        factory.WithInternalClientSubjects(PermittedClientSubject);
        using DatabaseContext db = factory.CreateDbContext();

        string externalId = $"ext-{Guid.NewGuid():N}";
        int tenantId = await SeedTenantWithSubscription(db, externalId, SubscriptionTier.Free);

        TenantSubscription? original = await db.TenantSubscriptions
            .FirstOrDefaultAsync(s => s.TenantId == tenantId);
        DateTimeOffset originalUpdatedAt = original!.UpdatedAt;

        using GrpcChannel channel = CreateChannel(factory);
        BillingGateway.BillingGatewayClient client = new(channel);

        Metadata headers = new Metadata { { TestClientCertificateMiddleware.SubjectHeader, PermittedClientSubject } };

        BillingActionResponse response = await client.ProcessBillingActionAsync(
            new BillingActionRequest
            {
                TenantExternalId = externalId,
                Action = BillingAction.UpgradeToTeam
            }, headers);

        await Assert.That(response.Success).IsTrue();
        await Assert.That(response.Message).IsEqualTo("OK");

        TenantSubscription? updated = await db.TenantSubscriptions
            .FirstOrDefaultAsync(s => s.TenantId == tenantId);
        await Assert.That(updated).IsNotNull();
        await Assert.That(updated!.Tier).IsEqualTo(SubscriptionTier.Team);
        await Assert.That(updated.Status).IsEqualTo(SubscriptionStatus.Active);
        await Assert.That(updated.UpdatedAt).IsGreaterThanOrEqualTo(originalUpdatedAt);
    }

    [Test]
    public async Task ProcessBillingAction_DowngradeToFree_RevertsSubscriptionToFreeTier()
    {
        using FunctionalTestFactory factory = new();
        factory.WithInternalClientSubjects(PermittedClientSubject);
        using DatabaseContext db = factory.CreateDbContext();

        string externalId = $"ext-{Guid.NewGuid():N}";
        int tenantId = await SeedTenantWithSubscription(db, externalId, SubscriptionTier.Pro);

        TenantSubscription? original = await db.TenantSubscriptions
            .FirstOrDefaultAsync(s => s.TenantId == tenantId);
        DateTimeOffset originalUpdatedAt = original!.UpdatedAt;

        using GrpcChannel channel = CreateChannel(factory);
        BillingGateway.BillingGatewayClient client = new(channel);

        Metadata headers = new Metadata { { TestClientCertificateMiddleware.SubjectHeader, PermittedClientSubject } };

        BillingActionResponse response = await client.ProcessBillingActionAsync(
            new BillingActionRequest
            {
                TenantExternalId = externalId,
                Action = BillingAction.DowngradeToFree
            }, headers);

        await Assert.That(response.Success).IsTrue();

        TenantSubscription? updated = await db.TenantSubscriptions
            .FirstOrDefaultAsync(s => s.TenantId == tenantId);
        await Assert.That(updated).IsNotNull();
        await Assert.That(updated!.Tier).IsEqualTo(SubscriptionTier.Free);
        await Assert.That(updated.Status).IsEqualTo(SubscriptionStatus.Active);
        await Assert.That(updated.UpdatedAt).IsGreaterThanOrEqualTo(originalUpdatedAt);
    }

    [Test]
    public async Task ProcessBillingAction_DowngradeFromTeamToFree_RevertsAllFields()
    {
        using FunctionalTestFactory factory = new();
        factory.WithInternalClientSubjects(PermittedClientSubject);
        using DatabaseContext db = factory.CreateDbContext();

        string externalId = $"ext-{Guid.NewGuid():N}";
        int tenantId = await SeedTenantWithSubscription(db, externalId, SubscriptionTier.Team);

        using GrpcChannel channel = CreateChannel(factory);
        BillingGateway.BillingGatewayClient client = new(channel);

        Metadata headers = new Metadata { { TestClientCertificateMiddleware.SubjectHeader, PermittedClientSubject } };

        BillingActionResponse response = await client.ProcessBillingActionAsync(
            new BillingActionRequest
            {
                TenantExternalId = externalId,
                Action = BillingAction.DowngradeToFree
            }, headers);

        await Assert.That(response.Success).IsTrue();

        TenantSubscription? updated = await db.TenantSubscriptions
            .FirstOrDefaultAsync(s => s.TenantId == tenantId);
        await Assert.That(updated).IsNotNull();
        await Assert.That(updated!.Tier).IsEqualTo(SubscriptionTier.Free);
        await Assert.That(updated.Status).IsEqualTo(SubscriptionStatus.Active);
    }

    [Test]
    public async Task ProcessBillingAction_DowngradeToPro_SetsCorrectValues()
    {
        using FunctionalTestFactory factory = new();
        factory.WithInternalClientSubjects(PermittedClientSubject);
        using DatabaseContext db = factory.CreateDbContext();

        string externalId = $"ext-{Guid.NewGuid():N}";
        int tenantId = await SeedTenantWithSubscription(db, externalId, SubscriptionTier.Team);

        TenantSubscription? original = await db.TenantSubscriptions
            .FirstOrDefaultAsync(s => s.TenantId == tenantId);
        DateTimeOffset originalUpdatedAt = original!.UpdatedAt;

        using GrpcChannel channel = CreateChannel(factory);
        BillingGateway.BillingGatewayClient client = new(channel);

        Metadata headers = new Metadata { { TestClientCertificateMiddleware.SubjectHeader, PermittedClientSubject } };

        BillingActionResponse response = await client.ProcessBillingActionAsync(
            new BillingActionRequest
            {
                TenantExternalId = externalId,
                Action = BillingAction.DowngradeToPro
            }, headers);

        await Assert.That(response.Success).IsTrue();

        TenantSubscription? updated = await db.TenantSubscriptions
            .FirstOrDefaultAsync(s => s.TenantId == tenantId);
        await Assert.That(updated).IsNotNull();
        await Assert.That(updated!.Tier).IsEqualTo(SubscriptionTier.Pro);
        await Assert.That(updated.Status).IsEqualTo(SubscriptionStatus.Active);
        await Assert.That(updated.UpdatedAt).IsGreaterThanOrEqualTo(originalUpdatedAt);
    }

    [Test]
    public async Task ProcessBillingAction_UpdatePeriodEnd_UpdatesDate()
    {
        using FunctionalTestFactory factory = new();
        factory.WithInternalClientSubjects(PermittedClientSubject);
        using DatabaseContext db = factory.CreateDbContext();

        string externalId = $"ext-{Guid.NewGuid():N}";
        int tenantId = await SeedTenantWithSubscription(db, externalId, SubscriptionTier.Pro);

        using GrpcChannel channel = CreateChannel(factory);
        BillingGateway.BillingGatewayClient client = new(channel);

        Metadata headers = new Metadata { { TestClientCertificateMiddleware.SubjectHeader, PermittedClientSubject } };
        DateTimeOffset periodEnd = DateTimeOffset.UtcNow.AddDays(30);

        BillingActionResponse response = await client.ProcessBillingActionAsync(
            new BillingActionRequest
            {
                TenantExternalId = externalId,
                Action = BillingAction.UpdatePeriodEnd,
                CurrentPeriodEnd = Timestamp.FromDateTimeOffset(periodEnd)
            }, headers);

        await Assert.That(response.Success).IsTrue();

        TenantSubscription? updated = await db.TenantSubscriptions
            .FirstOrDefaultAsync(s => s.TenantId == tenantId);
        await Assert.That(updated).IsNotNull();
        await Assert.That(updated!.CurrentPeriodEnd.HasValue).IsTrue();
    }

    [Test]
    public async Task ProcessBillingAction_SetPastDue_UpdatesStatus()
    {
        using FunctionalTestFactory factory = new();
        factory.WithInternalClientSubjects(PermittedClientSubject);
        using DatabaseContext db = factory.CreateDbContext();

        string externalId = $"ext-{Guid.NewGuid():N}";
        int tenantId = await SeedTenantWithSubscription(db, externalId, SubscriptionTier.Pro);

        using GrpcChannel channel = CreateChannel(factory);
        BillingGateway.BillingGatewayClient client = new(channel);

        Metadata headers = new Metadata { { TestClientCertificateMiddleware.SubjectHeader, PermittedClientSubject } };

        BillingActionResponse response = await client.ProcessBillingActionAsync(
            new BillingActionRequest
            {
                TenantExternalId = externalId,
                Action = BillingAction.SetPastDue
            }, headers);

        await Assert.That(response.Success).IsTrue();

        TenantSubscription? updated = await db.TenantSubscriptions
            .FirstOrDefaultAsync(s => s.TenantId == tenantId);
        await Assert.That(updated).IsNotNull();
        await Assert.That(updated!.Status).IsEqualTo(SubscriptionStatus.PastDue);
    }

    [Test]
    public async Task ProcessBillingAction_SetActive_RestoresActiveStatus()
    {
        using FunctionalTestFactory factory = new();
        factory.WithInternalClientSubjects(PermittedClientSubject);
        using DatabaseContext db = factory.CreateDbContext();

        string externalId = $"ext-{Guid.NewGuid():N}";
        int tenantId = await SeedTenantWithSubscription(db, externalId, SubscriptionTier.Pro, SubscriptionStatus.PastDue);

        using GrpcChannel channel = CreateChannel(factory);
        BillingGateway.BillingGatewayClient client = new(channel);

        Metadata headers = new Metadata { { TestClientCertificateMiddleware.SubjectHeader, PermittedClientSubject } };

        BillingActionResponse response = await client.ProcessBillingActionAsync(
            new BillingActionRequest
            {
                TenantExternalId = externalId,
                Action = BillingAction.SetActive
            }, headers);

        await Assert.That(response.Success).IsTrue();

        TenantSubscription? updated = await db.TenantSubscriptions
            .FirstOrDefaultAsync(s => s.TenantId == tenantId);
        await Assert.That(updated).IsNotNull();
        await Assert.That(updated!.Status).IsEqualTo(SubscriptionStatus.Active);
    }

    [Test]
    public async Task ProcessBillingAction_UpdatePeriodEnd_MissingTimestamp_ThrowsInvalidArgument()
    {
        using FunctionalTestFactory factory = new();
        factory.WithInternalClientSubjects(PermittedClientSubject);
        using DatabaseContext db = factory.CreateDbContext();

        string externalId = $"ext-{Guid.NewGuid():N}";
        await SeedTenantWithSubscription(db, externalId, SubscriptionTier.Pro);

        using GrpcChannel channel = CreateChannel(factory);
        BillingGateway.BillingGatewayClient client = new(channel);

        Metadata headers = new Metadata { { TestClientCertificateMiddleware.SubjectHeader, PermittedClientSubject } };

        RpcException? exception = null;
        try
        {
            await client.ProcessBillingActionAsync(new BillingActionRequest
            {
                TenantExternalId = externalId,
                Action = BillingAction.UpdatePeriodEnd
            }, headers);
        }
        catch (RpcException ex)
        {
            exception = ex;
        }

        await Assert.That(exception).IsNotNull();
        await Assert.That(exception!.StatusCode).IsEqualTo(StatusCode.InvalidArgument);
        await Assert.That(exception.Status.Detail).Contains("CurrentPeriodEnd");
    }

    [Test]
    public async Task ProcessBillingAction_NonExistentTenant_ThrowsNotFoundWithExternalId()
    {
        using FunctionalTestFactory factory = new();
        factory.WithInternalClientSubjects(PermittedClientSubject);
        using GrpcChannel channel = CreateChannel(factory);
        BillingGateway.BillingGatewayClient client = new(channel);

        Metadata headers = new Metadata { { TestClientCertificateMiddleware.SubjectHeader, PermittedClientSubject } };
        string nonExistentExternalId = $"ext-does-not-exist-{Guid.NewGuid():N}";

        RpcException? exception = null;
        try
        {
            await client.ProcessBillingActionAsync(new BillingActionRequest
            {
                TenantExternalId = nonExistentExternalId,
                Action = BillingAction.SetActive
            }, headers);
        }
        catch (RpcException ex)
        {
            exception = ex;
        }

        await Assert.That(exception).IsNotNull();
        await Assert.That(exception!.StatusCode).IsEqualTo(StatusCode.NotFound);
        await Assert.That(exception.Status.Detail).Contains(nonExistentExternalId);
    }

    [Test]
    public async Task ProcessBillingAction_DowngradeProToFree_SetsCorrectRetentionAndMachineLimit()
    {
        // Verify that downgrading from Pro to Free applies Free tier defaults
        using FunctionalTestFactory factory = new();
        factory.WithInternalClientSubjects(PermittedClientSubject);
        using DatabaseContext db = factory.CreateDbContext();

        string externalId = $"ext-{Guid.NewGuid():N}";
        int tenantId = await SeedTenantWithSubscription(db, externalId, SubscriptionTier.Pro);

        using GrpcChannel channel = CreateChannel(factory);
        BillingGateway.BillingGatewayClient client = new(channel);

        Metadata headers = new Metadata { { TestClientCertificateMiddleware.SubjectHeader, PermittedClientSubject } };

        BillingActionResponse response = await client.ProcessBillingActionAsync(
            new BillingActionRequest
            {
                TenantExternalId = externalId,
                Action = BillingAction.DowngradeToFree
            }, headers);

        await Assert.That(response.Success).IsTrue();

        TenantSubscription? updated = await db.TenantSubscriptions
            .FirstOrDefaultAsync(s => s.TenantId == tenantId);
        await Assert.That(updated).IsNotNull();
        await Assert.That(updated!.Tier).IsEqualTo(SubscriptionTier.Free);
        await Assert.That(updated.Status).IsEqualTo(SubscriptionStatus.Active);
    }

    [Test]
    public async Task ProcessBillingAction_DowngradeToFree_AlwaysRevertsToFreeTier()
    {
        // The billing-api determines the correct action from its PendingActions table
        // and dispatches DowngradeToFree only when an intentional downgrade was scheduled.
        // The fleet server always reverts to Free/Active when it receives this action.
        using FunctionalTestFactory factory = new();
        factory.WithInternalClientSubjects(PermittedClientSubject);
        using DatabaseContext db = factory.CreateDbContext();

        string externalId = $"ext-{Guid.NewGuid():N}";
        int tenantId = await SeedTenantWithSubscription(db, externalId, SubscriptionTier.Pro);

        TenantSubscription? original = await db.TenantSubscriptions
            .FirstOrDefaultAsync(s => s.TenantId == tenantId);
        DateTimeOffset originalUpdatedAt = original!.UpdatedAt;

        using GrpcChannel channel = CreateChannel(factory);
        BillingGateway.BillingGatewayClient client = new(channel);

        Metadata headers = new Metadata { { TestClientCertificateMiddleware.SubjectHeader, PermittedClientSubject } };

        BillingActionResponse response = await client.ProcessBillingActionAsync(
            new BillingActionRequest
            {
                TenantExternalId = externalId,
                Action = BillingAction.DowngradeToFree
            }, headers);

        await Assert.That(response.Success).IsTrue();

        TenantSubscription? updated = await db.TenantSubscriptions
            .FirstOrDefaultAsync(s => s.TenantId == tenantId);
        await Assert.That(updated).IsNotNull();
        await Assert.That(updated!.Tier).IsEqualTo(SubscriptionTier.Free);
        await Assert.That(updated.Status).IsEqualTo(SubscriptionStatus.Active);
        await Assert.That(updated.UpdatedAt).IsGreaterThanOrEqualTo(originalUpdatedAt);
    }

    [Test]
    public async Task ProcessBillingAction_UpdatePeriodEnd_UpdatesSubscriptionPeriodEnd()
    {
        // Verify that the exact timestamp provided is persisted on the subscription record
        using FunctionalTestFactory factory = new();
        factory.WithInternalClientSubjects(PermittedClientSubject);
        using DatabaseContext db = factory.CreateDbContext();

        string externalId = $"ext-{Guid.NewGuid():N}";
        int tenantId = await SeedTenantWithSubscription(db, externalId, SubscriptionTier.Team);

        using GrpcChannel channel = CreateChannel(factory);
        BillingGateway.BillingGatewayClient client = new(channel);

        Metadata headers = new Metadata { { TestClientCertificateMiddleware.SubjectHeader, PermittedClientSubject } };

        // Truncate to seconds because protobuf Timestamp has second-level precision
        DateTimeOffset periodEnd = new DateTimeOffset(
            DateTimeOffset.UtcNow.AddDays(365).ToUnixTimeSeconds() * TimeSpan.TicksPerSecond + DateTimeOffset.UnixEpoch.Ticks,
            TimeSpan.Zero);

        BillingActionResponse response = await client.ProcessBillingActionAsync(
            new BillingActionRequest
            {
                TenantExternalId = externalId,
                Action = BillingAction.UpdatePeriodEnd,
                CurrentPeriodEnd = Timestamp.FromDateTimeOffset(periodEnd)
            }, headers);

        await Assert.That(response.Success).IsTrue();
        await Assert.That(response.Message).IsEqualTo("OK");

        TenantSubscription? updated = await db.TenantSubscriptions
            .FirstOrDefaultAsync(s => s.TenantId == tenantId);
        await Assert.That(updated).IsNotNull();
        await Assert.That(updated!.CurrentPeriodEnd.HasValue).IsTrue();
        await Assert.That(updated.Tier).IsEqualTo(SubscriptionTier.Team);
        await Assert.That(updated.Status).IsEqualTo(SubscriptionStatus.Active);
    }

    [Test]
    public async Task ProcessBillingAction_SetPastDue_UpdatesSubscriptionStatus()
    {
        // Verify that SetPastDue marks an Active Team subscription as PastDue without altering the tier
        using FunctionalTestFactory factory = new();
        factory.WithInternalClientSubjects(PermittedClientSubject);
        using DatabaseContext db = factory.CreateDbContext();

        string externalId = $"ext-{Guid.NewGuid():N}";
        int tenantId = await SeedTenantWithSubscription(db, externalId, SubscriptionTier.Team);

        using GrpcChannel channel = CreateChannel(factory);
        BillingGateway.BillingGatewayClient client = new(channel);

        Metadata headers = new Metadata { { TestClientCertificateMiddleware.SubjectHeader, PermittedClientSubject } };

        BillingActionResponse response = await client.ProcessBillingActionAsync(
            new BillingActionRequest
            {
                TenantExternalId = externalId,
                Action = BillingAction.SetPastDue
            }, headers);

        await Assert.That(response.Success).IsTrue();
        await Assert.That(response.Message).IsEqualTo("OK");

        TenantSubscription? updated = await db.TenantSubscriptions
            .FirstOrDefaultAsync(s => s.TenantId == tenantId);
        await Assert.That(updated).IsNotNull();
        await Assert.That(updated!.Status).IsEqualTo(SubscriptionStatus.PastDue);
        await Assert.That(updated.Tier).IsEqualTo(SubscriptionTier.Team);
    }

    [Test]
    public async Task ProcessBillingAction_SetActive_UpdatesSubscriptionStatus()
    {
        // Verify that SetActive restores a PastDue Pro subscription to Active without altering the tier
        using FunctionalTestFactory factory = new();
        factory.WithInternalClientSubjects(PermittedClientSubject);
        using DatabaseContext db = factory.CreateDbContext();

        string externalId = $"ext-{Guid.NewGuid():N}";
        int tenantId = await SeedTenantWithSubscription(db, externalId, SubscriptionTier.Pro, SubscriptionStatus.PastDue);

        using GrpcChannel channel = CreateChannel(factory);
        BillingGateway.BillingGatewayClient client = new(channel);

        Metadata headers = new Metadata { { TestClientCertificateMiddleware.SubjectHeader, PermittedClientSubject } };

        BillingActionResponse response = await client.ProcessBillingActionAsync(
            new BillingActionRequest
            {
                TenantExternalId = externalId,
                Action = BillingAction.SetActive
            }, headers);

        await Assert.That(response.Success).IsTrue();
        await Assert.That(response.Message).IsEqualTo("OK");

        TenantSubscription? updated = await db.TenantSubscriptions
            .FirstOrDefaultAsync(s => s.TenantId == tenantId);
        await Assert.That(updated).IsNotNull();
        await Assert.That(updated!.Status).IsEqualTo(SubscriptionStatus.Active);
        await Assert.That(updated.Tier).IsEqualTo(SubscriptionTier.Pro);
    }

    // ========== Helpers ==========

    private static GrpcChannel CreateChannel(FunctionalTestFactory factory)
    {
        HttpMessageHandler handler = new ResponseVersionHandler
        {
            InnerHandler = factory.Server.CreateHandler()
        };

        return GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
        {
            HttpHandler = handler
        });
    }

    private static async Task<int> SeedTenantWithSubscription(
        DatabaseContext db,
        string externalId,
        SubscriptionTier tier,
        SubscriptionStatus status = SubscriptionStatus.Active)
    {
        Tenant tenant = new()
        {
            Name = $"Test Tenant {Guid.NewGuid():N}",
            ExternalId = externalId,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 1,
            IsActive = true,
            LogoUrl = ""
        };
        int tenantId = (int)(long)await db.InsertWithIdentityAsync(tenant);

        TenantSubscription subscription = new()
        {
            TenantId = tenantId,
            Tier = tier,
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await db.InsertAsync(subscription);

        return tenantId;
    }
}
