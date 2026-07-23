// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Billing;
using Framlux.FleetManagement.Services.Core.Handlers;
using Framlux.FleetManagement.Test.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Framlux.FleetManagement.Test.Services.Handlers;

/// <summary>
/// Tests for <see cref="TenantDeletionHandler"/>.
/// </summary>
public class TenantDeletionHandlerTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 07, 23, 12, 0, 0, TimeSpan.Zero);

    // ========== ComputeScheduledPurge ==========

    [Test]
    public async Task ComputeScheduledPurge_AddsExactly30Days()
    {
        DateTimeOffset requestedAt = new(2026, 01, 01, 0, 0, 0, TimeSpan.Zero);

        DateTimeOffset scheduledPurgeAt = TenantDeletionHandler.ComputeScheduledPurge(requestedAt);

        await Assert.That(scheduledPurgeAt).IsEqualTo(requestedAt.AddDays(30));
    }

    // ========== IsUserOrphanedByTenantRemoval ==========

    [Test]
    public async Task IsUserOrphanedByTenantRemoval_NoActiveRoleElsewhere_ReturnsTrue()
    {
        bool orphaned = TenantDeletionHandler.IsUserOrphanedByTenantRemoval(false);

        await Assert.That(orphaned).IsTrue();
    }

    [Test]
    public async Task IsUserOrphanedByTenantRemoval_HasActiveRoleElsewhere_ReturnsFalse()
    {
        bool orphaned = TenantDeletionHandler.IsUserOrphanedByTenantRemoval(true);

        await Assert.That(orphaned).IsFalse();
    }

    // ========== RequestDeletionAsync ==========

    [Test]
    public async Task RequestDeletionAsync_HappyPath_InsertsDeactivatesAuditsCommitsThenCancelsBilling()
    {
        Tenant tenant = TestDataBuilder.BuildTenant(name: "Acme Corp", externalId: "ext-acme");
        tenant.Id = 7;
        ITenantRepository tenantRepo = Substitute.For<ITenantRepository>();
        tenantRepo.GetTenantByIdAsync(7, Arg.Any<CancellationToken>()).Returns(Task.FromResult<Tenant?>(tenant));

        List<string> callOrder = [];

        ITenantDeletionRepository deletionRepo = Substitute.For<ITenantDeletionRepository>();
        deletionRepo.GetActiveDeletionForTenantAsync(7, Arg.Any<CancellationToken>()).Returns(Task.FromResult<TenantDeletion?>(null));
        deletionRepo.InsertDeletionAsync(Arg.Any<TenantDeletion>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(callInfo.Arg<TenantDeletion>()))
            .AndDoes(_ => callOrder.Add("insert"));

        IDatabaseTransaction transaction = Substitute.For<IDatabaseTransaction>();
        transaction.CommitAsync(Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(_ => callOrder.Add("commit"));

        IDatabaseTransactionProvider transactionProvider = Substitute.For<IDatabaseTransactionProvider>();
        transactionProvider.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(transaction));

        IAuditLogRepository auditLog = Substitute.For<IAuditLogRepository>();
        auditLog.InsertAuditLogAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(_ => callOrder.Add("audit"));
        deletionRepo.SetTenantActiveAsync(7, false, 3, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(_ => callOrder.Add("deactivate"));

        IBillingApiClient billingApiClient = Substitute.For<IBillingApiClient>();
        billingApiClient.CancelSubscriptionImmediateAsync("ext-acme", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true))
            .AndDoes(_ => callOrder.Add("billing"));

        FakeTimeProvider timeProvider = new(FixedNow);
        TenantDeletionHandler handler = new(
            tenantRepo, deletionRepo, auditLog, transactionProvider, billingApiClient, timeProvider,
            Substitute.For<ILogger<TenantDeletionHandler>>());

        TenantDeletionResult result = await handler.RequestDeletionAsync(7, 3, "test reason", CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.ScheduledPurgeAt).IsEqualTo(FixedNow.AddDays(30));
        await deletionRepo.Received(1).InsertDeletionAsync(
            Arg.Is<TenantDeletion>(d =>
                (d.TenantId == 7) &&
                (d.TenantExternalId == "ext-acme") &&
                (d.TenantName == "Acme Corp") &&
                (d.RequestedByUserId == 3) &&
                (d.Status == TenantDeletionStatus.Deactivated) &&
                (d.ScheduledPurgeAt == FixedNow.AddDays(30)) &&
                (d.Reason == "test reason")),
            Arg.Any<CancellationToken>());
        await deletionRepo.Received(1).SetTenantActiveAsync(7, false, 3, FixedNow, Arg.Any<CancellationToken>());
        await auditLog.Received(1).InsertAuditLogAsync(
            Arg.Is<AuditLogEntry>(e => (e.Action == AuditAction.TenantDeletionRequested) && (e.TenantId == 7)),
            Arg.Any<CancellationToken>());
        await transaction.Received(1).CommitAsync(Arg.Any<CancellationToken>());
        await billingApiClient.Received(1).CancelSubscriptionImmediateAsync("ext-acme", Arg.Any<CancellationToken>());

        // Order matters: insert, deactivate, and audit happen before commit; billing cancel happens
        // strictly after commit — never before, and never inside the transaction.
        await Assert.That(callOrder.Count).IsEqualTo(5);
        int insertIdx = callOrder.IndexOf("insert");
        int deactivateIdx = callOrder.IndexOf("deactivate");
        int auditIdx = callOrder.IndexOf("audit");
        int commitIdx = callOrder.IndexOf("commit");
        int billingIdx = callOrder.IndexOf("billing");
        await Assert.That(insertIdx).IsLessThan(commitIdx);
        await Assert.That(deactivateIdx).IsLessThan(commitIdx);
        await Assert.That(auditIdx).IsLessThan(commitIdx);
        await Assert.That(commitIdx).IsLessThan(billingIdx);
    }

    [Test]
    public async Task RequestDeletionAsync_TenantNotFound_ReturnsFailure()
    {
        ITenantRepository tenantRepo = Substitute.For<ITenantRepository>();
        tenantRepo.GetTenantByIdAsync(99, Arg.Any<CancellationToken>()).Returns(Task.FromResult<Tenant?>(null));

        TenantDeletionHandler handler = BuildHandler(tenantRepo: tenantRepo);

        TenantDeletionResult result = await handler.RequestDeletionAsync(99, 1, null, CancellationToken.None);

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.ScheduledPurgeAt).IsNull();
    }

    [Test]
    public async Task RequestDeletionAsync_ActiveDeletionAlreadyExists_ReturnsFailureWithoutSideEffects()
    {
        Tenant tenant = TestDataBuilder.BuildTenant();
        tenant.Id = 5;
        ITenantRepository tenantRepo = Substitute.For<ITenantRepository>();
        tenantRepo.GetTenantByIdAsync(5, Arg.Any<CancellationToken>()).Returns(Task.FromResult<Tenant?>(tenant));

        ITenantDeletionRepository deletionRepo = Substitute.For<ITenantDeletionRepository>();
        deletionRepo.GetActiveDeletionForTenantAsync(5, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TenantDeletion?>(new TenantDeletion
            {
                Id = 1,
                TenantId = 5,
                TenantExternalId = tenant.ExternalId,
                TenantName = tenant.Name,
                RequestedByUserId = 1,
                RequestedAt = FixedNow,
                ScheduledPurgeAt = FixedNow.AddDays(30),
                Status = TenantDeletionStatus.Deactivated,
            }));

        IDatabaseTransactionProvider transactionProvider = Substitute.For<IDatabaseTransactionProvider>();
        IBillingApiClient billingApiClient = Substitute.For<IBillingApiClient>();

        TenantDeletionHandler handler = BuildHandler(
            tenantRepo: tenantRepo, deletionRepo: deletionRepo, transactionProvider: transactionProvider, billingApiClient: billingApiClient);

        TenantDeletionResult result = await handler.RequestDeletionAsync(5, 1, null, CancellationToken.None);

        await Assert.That(result.Success).IsFalse();
        await deletionRepo.DidNotReceive().InsertDeletionAsync(Arg.Any<TenantDeletion>(), Arg.Any<CancellationToken>());
        await deletionRepo.DidNotReceive().SetTenantActiveAsync(Arg.Any<int>(), Arg.Any<bool>(), Arg.Any<int?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>());
        await transactionProvider.DidNotReceive().BeginTransactionAsync(Arg.Any<CancellationToken>());
        await billingApiClient.DidNotReceive().CancelSubscriptionImmediateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RequestDeletionAsync_BillingCancelFailsAfterCommit_StillReturnsSuccessAndDeactivationPersists()
    {
        Tenant tenant = TestDataBuilder.BuildTenant(externalId: "ext-x");
        tenant.Id = 9;
        ITenantRepository tenantRepo = Substitute.For<ITenantRepository>();
        tenantRepo.GetTenantByIdAsync(9, Arg.Any<CancellationToken>()).Returns(Task.FromResult<Tenant?>(tenant));

        ITenantDeletionRepository deletionRepo = Substitute.For<ITenantDeletionRepository>();
        deletionRepo.GetActiveDeletionForTenantAsync(9, Arg.Any<CancellationToken>()).Returns(Task.FromResult<TenantDeletion?>(null));
        deletionRepo.InsertDeletionAsync(Arg.Any<TenantDeletion>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(callInfo.Arg<TenantDeletion>()));

        (IDatabaseTransactionProvider transactionProvider, IAuditLogRepository auditLog) = CreateMockTransactionAndAudit();

        IBillingApiClient billingApiClient = Substitute.For<IBillingApiClient>();
        billingApiClient.CancelSubscriptionImmediateAsync("ext-x", Arg.Any<CancellationToken>()).Returns(Task.FromResult(false));

        FakeTimeProvider timeProvider = new(FixedNow);
        TenantDeletionHandler handler = new(
            tenantRepo, deletionRepo, auditLog, transactionProvider, billingApiClient, timeProvider,
            Substitute.For<ILogger<TenantDeletionHandler>>());

        TenantDeletionResult result = await handler.RequestDeletionAsync(9, 2, null, CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        await deletionRepo.Received(1).SetTenantActiveAsync(9, false, 2, FixedNow, Arg.Any<CancellationToken>());
        await deletionRepo.Received(1).InsertDeletionAsync(Arg.Any<TenantDeletion>(), Arg.Any<CancellationToken>());
    }

    // ========== RestoreAsync ==========

    [Test]
    public async Task RestoreAsync_DeactivatedRow_RestoresAndReactivatesTenant()
    {
        TenantDeletion deletion = new()
        {
            Id = 11,
            TenantId = 4,
            TenantExternalId = "ext-r",
            TenantName = "Restore Corp",
            RequestedByUserId = 1,
            RequestedAt = FixedNow,
            ScheduledPurgeAt = FixedNow.AddDays(30),
            Status = TenantDeletionStatus.Deactivated,
        };

        ITenantDeletionRepository deletionRepo = Substitute.For<ITenantDeletionRepository>();
        deletionRepo.GetActiveDeletionForTenantAsync(4, Arg.Any<CancellationToken>()).Returns(Task.FromResult<TenantDeletion?>(deletion));

        (IDatabaseTransactionProvider transactionProvider, IAuditLogRepository auditLog) = CreateMockTransactionAndAudit();

        TenantDeletionHandler handler = BuildHandler(deletionRepo: deletionRepo, transactionProvider: transactionProvider, auditLog: auditLog);

        TenantDeletionResult result = await handler.RestoreAsync(4, 2, CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        await deletionRepo.Received(1).UpdateDeletionStatusAsync(11, TenantDeletionStatus.Restored, null, Arg.Any<CancellationToken>());
        await deletionRepo.Received(1).SetTenantActiveAsync(4, true, null, null, Arg.Any<CancellationToken>());
        await auditLog.Received(1).InsertAuditLogAsync(
            Arg.Is<AuditLogEntry>(e => (e.Action == AuditAction.TenantRestored) && (e.TenantId == 4)),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RestoreAsync_ActiveRowIsPurged_ReturnsFailureWithoutStateChange()
    {
        TenantDeletion deletion = new()
        {
            Id = 12,
            TenantId = 4,
            TenantExternalId = "ext-r",
            TenantName = "Restore Corp",
            RequestedByUserId = 1,
            RequestedAt = FixedNow,
            ScheduledPurgeAt = FixedNow.AddDays(30),
            Status = TenantDeletionStatus.Purged,
        };

        ITenantDeletionRepository deletionRepo = Substitute.For<ITenantDeletionRepository>();
        deletionRepo.GetActiveDeletionForTenantAsync(4, Arg.Any<CancellationToken>()).Returns(Task.FromResult<TenantDeletion?>(deletion));
        IDatabaseTransactionProvider transactionProvider = Substitute.For<IDatabaseTransactionProvider>();

        TenantDeletionHandler handler = BuildHandler(deletionRepo: deletionRepo, transactionProvider: transactionProvider);

        TenantDeletionResult result = await handler.RestoreAsync(4, 2, CancellationToken.None);

        await Assert.That(result.Success).IsFalse();
        await deletionRepo.DidNotReceive().UpdateDeletionStatusAsync(Arg.Any<int>(), Arg.Any<TenantDeletionStatus>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>());
        await deletionRepo.DidNotReceive().SetTenantActiveAsync(Arg.Any<int>(), Arg.Any<bool>(), Arg.Any<int?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>());
        await transactionProvider.DidNotReceive().BeginTransactionAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RestoreAsync_NoDeletionRow_ReturnsFailure()
    {
        ITenantDeletionRepository deletionRepo = Substitute.For<ITenantDeletionRepository>();
        deletionRepo.GetActiveDeletionForTenantAsync(6, Arg.Any<CancellationToken>()).Returns(Task.FromResult<TenantDeletion?>(null));

        TenantDeletionHandler handler = BuildHandler(deletionRepo: deletionRepo);

        TenantDeletionResult result = await handler.RestoreAsync(6, 2, CancellationToken.None);

        await Assert.That(result.Success).IsFalse();
    }

    // ========== Helper methods ==========

    private static TenantDeletionHandler BuildHandler(
        ITenantRepository? tenantRepo = null,
        ITenantDeletionRepository? deletionRepo = null,
        IAuditLogRepository? auditLog = null,
        IDatabaseTransactionProvider? transactionProvider = null,
        IBillingApiClient? billingApiClient = null,
        TimeProvider? timeProvider = null,
        ILogger<TenantDeletionHandler>? logger = null)
    {
        return new TenantDeletionHandler(
            tenantRepo ?? Substitute.For<ITenantRepository>(),
            deletionRepo ?? Substitute.For<ITenantDeletionRepository>(),
            auditLog ?? Substitute.For<IAuditLogRepository>(),
            transactionProvider ?? Substitute.For<IDatabaseTransactionProvider>(),
            billingApiClient ?? Substitute.For<IBillingApiClient>(),
            timeProvider ?? new FakeTimeProvider(FixedNow),
            logger ?? Substitute.For<ILogger<TenantDeletionHandler>>());
    }

    private static (IDatabaseTransactionProvider, IAuditLogRepository) CreateMockTransactionAndAudit()
    {
        IDatabaseTransaction tx = Substitute.For<IDatabaseTransaction>();
        tx.CommitAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        IDatabaseTransactionProvider txProvider = Substitute.For<IDatabaseTransactionProvider>();
        txProvider.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(tx));

        IAuditLogRepository auditLog = Substitute.For<IAuditLogRepository>();
        auditLog.InsertAuditLogAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        return (txProvider, auditLog);
    }
}
