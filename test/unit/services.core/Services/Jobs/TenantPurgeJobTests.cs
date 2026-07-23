// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Billing;
using Framlux.FleetManagement.Services.Core.Jobs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Framlux.FleetManagement.Test.Services.Jobs;

/// <summary>
/// Tests for <see cref="TenantPurgeJob"/>.
/// </summary>
public sealed class TenantPurgeJobTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 07, 23, 12, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task RunAsync_NoDueDeletions_DoesNoTeardownNoBillingCallNoStatusUpdate()
    {
        ITenantDeletionRepository deletionRepo = Substitute.For<ITenantDeletionRepository>();
        deletionRepo.GetDueDeletionsAsync(FixedNow, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<TenantDeletion>()));

        IBillingApiClient billingApiClient = Substitute.For<IBillingApiClient>();

        TenantPurgeJob job = BuildJob(deletionRepo: deletionRepo, billingApiClient: billingApiClient);

        await job.RunAsync(CancellationToken.None);

        await deletionRepo.DidNotReceive().PurgeTenantOperationalDataAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await deletionRepo.DidNotReceive().DeleteUserTenantRolesForTenantAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await billingApiClient.DidNotReceive().DeleteCustomerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await deletionRepo.DidNotReceive().UpdateDeletionStatusAsync(
            Arg.Any<int>(), Arg.Any<TenantDeletionStatus>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_OneDueDeletionAllSucceed_RunsFullOrchestrationInOrderAndMarksPurged()
    {
        TenantDeletion deletion = BuildDeletion(id: 1, tenantId: 7, externalId: "ext-7", name: "Acme Corp");

        List<string> callOrder = [];

        ITenantDeletionRepository deletionRepo = Substitute.For<ITenantDeletionRepository>();
        deletionRepo.GetDueDeletionsAsync(FixedNow, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<TenantDeletion> { deletion }));
        deletionRepo.GetUserIdsWithAnyRoleInTenantAsync(7, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<int> { 100, 200 }))
            .AndDoes(_ => callOrder.Add("membership"));
        deletionRepo.PurgeTenantOperationalDataAsync(7, Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(_ => callOrder.Add("purge-operational"));
        deletionRepo.DeleteUserTenantRolesForTenantAsync(7, Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(_ => callOrder.Add("delete-roles"));

        // User 100 is orphaned (no active role elsewhere); user 200 remains active elsewhere.
        deletionRepo.UserHasAnyActiveRoleAsync(100, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(false))
            .AndDoes(_ => callOrder.Add("check-100"));
        deletionRepo.UserHasAnyActiveRoleAsync(200, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true))
            .AndDoes(_ => callOrder.Add("check-200"));
        deletionRepo.MaskUserAsync(100, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(1))
            .AndDoes(_ => callOrder.Add("mask-100"));

        deletionRepo.UpdateDeletionStatusAsync(1, TenantDeletionStatus.Purged, FixedNow, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(1))
            .AndDoes(_ => callOrder.Add("status-purged"));

        IDatabaseTransaction transaction = Substitute.For<IDatabaseTransaction>();
        transaction.CommitAsync(Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(_ => callOrder.Add("commit"));

        IDatabaseTransactionProvider transactionProvider = Substitute.For<IDatabaseTransactionProvider>();
        transactionProvider.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(transaction));

        IBillingApiClient billingApiClient = Substitute.For<IBillingApiClient>();
        billingApiClient.DeleteCustomerAsync("ext-7", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true))
            .AndDoes(_ => callOrder.Add("delete-customer"));

        IAuditLogRepository auditLog = Substitute.For<IAuditLogRepository>();
        auditLog.InsertAuditLogAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(_ => callOrder.Add("audit"));

        TenantPurgeJob job = BuildJob(
            deletionRepo: deletionRepo,
            auditLog: auditLog,
            transactionProvider: transactionProvider,
            billingApiClient: billingApiClient);

        await job.RunAsync(CancellationToken.None);

        // User 200 has an active role elsewhere and must NOT be masked.
        await deletionRepo.DidNotReceive().MaskUserAsync(200, Arg.Any<CancellationToken>());

        await auditLog.Received(1).InsertAuditLogAsync(
            Arg.Is<AuditLogEntry>(e =>
                (e.Action == AuditAction.TenantPurged) &&
                (e.TenantId == null) &&
                (e.ResourceType == AuditResourceType.Tenant) &&
                (e.ResourceId == "7")),
            Arg.Any<CancellationToken>());

        // Order: membership read -> purge operational -> delete roles -> orphan checks/mask -> commit ->
        // delete customer (billing, AFTER commit) -> status flipped to Purged -> completion audit.
        await Assert.That(callOrder.Count).IsEqualTo(10);
        int membershipIdx = callOrder.IndexOf("membership");
        int purgeOperationalIdx = callOrder.IndexOf("purge-operational");
        int deleteRolesIdx = callOrder.IndexOf("delete-roles");
        int check100Idx = callOrder.IndexOf("check-100");
        int mask100Idx = callOrder.IndexOf("mask-100");
        int check200Idx = callOrder.IndexOf("check-200");
        int commitIdx = callOrder.IndexOf("commit");
        int deleteCustomerIdx = callOrder.IndexOf("delete-customer");
        int statusPurgedIdx = callOrder.IndexOf("status-purged");
        int auditIdx = callOrder.IndexOf("audit");

        await Assert.That(membershipIdx).IsLessThan(purgeOperationalIdx);
        await Assert.That(purgeOperationalIdx).IsLessThan(deleteRolesIdx);
        await Assert.That(deleteRolesIdx).IsLessThan(check100Idx);
        await Assert.That(check100Idx).IsLessThan(mask100Idx);
        await Assert.That(mask100Idx).IsLessThan(check200Idx);
        await Assert.That(check200Idx).IsLessThan(commitIdx);
        await Assert.That(commitIdx).IsLessThan(deleteCustomerIdx);
        await Assert.That(deleteCustomerIdx).IsLessThan(statusPurgedIdx);
        await Assert.That(statusPurgedIdx).IsLessThan(auditIdx);
    }

    [Test]
    public async Task RunAsync_BillingDeleteFails_FleetTeardownRanButStatusStaysDeactivatedAndNoCompletionAudit()
    {
        TenantDeletion deletion = BuildDeletion(id: 2, tenantId: 8, externalId: "ext-8", name: "Beta LLC");

        ITenantDeletionRepository deletionRepo = Substitute.For<ITenantDeletionRepository>();
        deletionRepo.GetDueDeletionsAsync(FixedNow, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<TenantDeletion> { deletion }));
        deletionRepo.GetUserIdsWithAnyRoleInTenantAsync(8, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<int>()));

        (IDatabaseTransactionProvider transactionProvider, IDatabaseTransaction transaction) = CreateMockTransaction();

        IBillingApiClient billingApiClient = Substitute.For<IBillingApiClient>();
        billingApiClient.DeleteCustomerAsync("ext-8", Arg.Any<CancellationToken>()).Returns(Task.FromResult(false));

        IAuditLogRepository auditLog = Substitute.For<IAuditLogRepository>();

        TenantPurgeJob job = BuildJob(
            deletionRepo: deletionRepo,
            auditLog: auditLog,
            transactionProvider: transactionProvider,
            billingApiClient: billingApiClient);

        await job.RunAsync(CancellationToken.None);

        await deletionRepo.Received(1).PurgeTenantOperationalDataAsync(8, Arg.Any<CancellationToken>());
        await deletionRepo.Received(1).DeleteUserTenantRolesForTenantAsync(8, Arg.Any<CancellationToken>());
        await transaction.Received(1).CommitAsync(Arg.Any<CancellationToken>());
        await billingApiClient.Received(1).DeleteCustomerAsync("ext-8", Arg.Any<CancellationToken>());

        await deletionRepo.DidNotReceive().UpdateDeletionStatusAsync(
            Arg.Any<int>(), Arg.Any<TenantDeletionStatus>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>());
        await auditLog.DidNotReceive().InsertAuditLogAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_SecondRunOverStillDeactivatedTenant_ReRunsTeardownWithoutThrowing()
    {
        TenantDeletion deletion = BuildDeletion(id: 3, tenantId: 9, externalId: "ext-9", name: "Gamma Inc");

        ITenantDeletionRepository deletionRepo = Substitute.For<ITenantDeletionRepository>();
        deletionRepo.GetDueDeletionsAsync(FixedNow, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<TenantDeletion> { deletion }));
        deletionRepo.GetUserIdsWithAnyRoleInTenantAsync(9, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<int>()));

        (IDatabaseTransactionProvider transactionProvider, IDatabaseTransaction transaction) = CreateMockTransaction();

        IBillingApiClient billingApiClient = Substitute.For<IBillingApiClient>();
        billingApiClient.DeleteCustomerAsync("ext-9", Arg.Any<CancellationToken>()).Returns(Task.FromResult(false));

        IAuditLogRepository auditLog = Substitute.For<IAuditLogRepository>();

        TenantPurgeJob job = BuildJob(
            deletionRepo: deletionRepo,
            auditLog: auditLog,
            transactionProvider: transactionProvider,
            billingApiClient: billingApiClient);

        await job.RunAsync(CancellationToken.None);
        await job.RunAsync(CancellationToken.None);

        await deletionRepo.Received(2).PurgeTenantOperationalDataAsync(9, Arg.Any<CancellationToken>());
        await deletionRepo.Received(2).DeleteUserTenantRolesForTenantAsync(9, Arg.Any<CancellationToken>());
        await transaction.Received(2).CommitAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_MultipleDueDeletions_ProcessedIndependentlyOneBillingFailureDoesNotBlockAnother()
    {
        TenantDeletion failing = BuildDeletion(id: 4, tenantId: 10, externalId: "ext-10", name: "Delta Co");
        TenantDeletion succeeding = BuildDeletion(id: 5, tenantId: 11, externalId: "ext-11", name: "Epsilon Co");

        ITenantDeletionRepository deletionRepo = Substitute.For<ITenantDeletionRepository>();
        deletionRepo.GetDueDeletionsAsync(FixedNow, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<TenantDeletion> { failing, succeeding }));
        deletionRepo.GetUserIdsWithAnyRoleInTenantAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<int>()));
        deletionRepo.UpdateDeletionStatusAsync(5, TenantDeletionStatus.Purged, FixedNow, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(1));

        (IDatabaseTransactionProvider transactionProvider, IDatabaseTransaction transaction) = CreateMockTransaction();

        IBillingApiClient billingApiClient = Substitute.For<IBillingApiClient>();
        billingApiClient.DeleteCustomerAsync("ext-10", Arg.Any<CancellationToken>()).Returns(Task.FromResult(false));
        billingApiClient.DeleteCustomerAsync("ext-11", Arg.Any<CancellationToken>()).Returns(Task.FromResult(true));

        IAuditLogRepository auditLog = Substitute.For<IAuditLogRepository>();
        auditLog.InsertAuditLogAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        TenantPurgeJob job = BuildJob(
            deletionRepo: deletionRepo,
            auditLog: auditLog,
            transactionProvider: transactionProvider,
            billingApiClient: billingApiClient);

        await job.RunAsync(CancellationToken.None);

        await transaction.Received(2).CommitAsync(Arg.Any<CancellationToken>());
        await deletionRepo.DidNotReceive().UpdateDeletionStatusAsync(
            4, Arg.Any<TenantDeletionStatus>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>());
        await deletionRepo.Received(1).UpdateDeletionStatusAsync(5, TenantDeletionStatus.Purged, FixedNow, Arg.Any<CancellationToken>());
        await auditLog.Received(1).InsertAuditLogAsync(
            Arg.Is<AuditLogEntry>(e => (e.Action == AuditAction.TenantPurged) && (e.ResourceId == "11")),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_OneTenantThrowsDuringPurge_OtherTenantStillFullyPurgedAndNoExceptionEscapes()
    {
        TenantDeletion failing = BuildDeletion(id: 6, tenantId: 12, externalId: "ext-12", name: "Zeta LLC");
        TenantDeletion succeeding = BuildDeletion(id: 7, tenantId: 13, externalId: "ext-13", name: "Eta LLC");

        ITenantDeletionRepository deletionRepo = Substitute.For<ITenantDeletionRepository>();
        deletionRepo.GetDueDeletionsAsync(FixedNow, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<TenantDeletion> { failing, succeeding }));
        deletionRepo.GetUserIdsWithAnyRoleInTenantAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<int>()));

        // Tenant A's operational-data purge throws mid-transaction; tenant B's repo calls all succeed.
        deletionRepo.PurgeTenantOperationalDataAsync(12, Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("simulated purge failure for tenant 12"));
        deletionRepo.PurgeTenantOperationalDataAsync(13, Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        deletionRepo.UpdateDeletionStatusAsync(7, TenantDeletionStatus.Purged, FixedNow, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(1));

        (IDatabaseTransactionProvider transactionProvider, IDatabaseTransaction transaction) = CreateMockTransaction();

        IBillingApiClient billingApiClient = Substitute.For<IBillingApiClient>();
        billingApiClient.DeleteCustomerAsync("ext-13", Arg.Any<CancellationToken>()).Returns(Task.FromResult(true));

        IAuditLogRepository auditLog = Substitute.For<IAuditLogRepository>();
        auditLog.InsertAuditLogAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        TenantPurgeJob job = BuildJob(
            deletionRepo: deletionRepo,
            auditLog: auditLog,
            transactionProvider: transactionProvider,
            billingApiClient: billingApiClient);

        // Must not throw out of RunAsync: the per-tenant try/catch logs tenant A's failure and continues.
        await job.RunAsync(CancellationToken.None);

        // Tenant A never reached billing or status update — its row stays Deactivated for the next tick.
        await billingApiClient.DidNotReceive().DeleteCustomerAsync("ext-12", Arg.Any<CancellationToken>());
        await deletionRepo.DidNotReceive().UpdateDeletionStatusAsync(
            6, Arg.Any<TenantDeletionStatus>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>());

        // Tenant B was processed independently and reached full completion.
        await deletionRepo.Received(1).DeleteUserTenantRolesForTenantAsync(13, Arg.Any<CancellationToken>());
        await transaction.Received(1).CommitAsync(Arg.Any<CancellationToken>());
        await billingApiClient.Received(1).DeleteCustomerAsync("ext-13", Arg.Any<CancellationToken>());
        await deletionRepo.Received(1).UpdateDeletionStatusAsync(7, TenantDeletionStatus.Purged, FixedNow, Arg.Any<CancellationToken>());
        await auditLog.Received(1).InsertAuditLogAsync(
            Arg.Is<AuditLogEntry>(e => (e.Action == AuditAction.TenantPurged) && (e.ResourceId == "13")),
            Arg.Any<CancellationToken>());
    }

    // ========== Helper methods ==========

    private static TenantDeletion BuildDeletion(int id, int tenantId, string externalId, string name)
    {
        return new TenantDeletion
        {
            Id = id,
            TenantId = tenantId,
            TenantExternalId = externalId,
            TenantName = name,
            RequestedByUserId = 1,
            RequestedAt = FixedNow.AddDays(-30),
            ScheduledPurgeAt = FixedNow,
            Status = TenantDeletionStatus.Deactivated,
        };
    }

    private static (IDatabaseTransactionProvider Provider, IDatabaseTransaction Transaction) CreateMockTransaction()
    {
        IDatabaseTransaction transaction = Substitute.For<IDatabaseTransaction>();
        transaction.CommitAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        IDatabaseTransactionProvider transactionProvider = Substitute.For<IDatabaseTransactionProvider>();
        transactionProvider.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(transaction));

        return (transactionProvider, transaction);
    }

    private static TenantPurgeJob BuildJob(
        ITenantDeletionRepository? deletionRepo = null,
        IAuditLogRepository? auditLog = null,
        IDatabaseTransactionProvider? transactionProvider = null,
        IBillingApiClient? billingApiClient = null,
        TimeProvider? timeProvider = null,
        ILogger<TenantPurgeJob>? logger = null)
    {
        return new TenantPurgeJob(
            deletionRepo ?? Substitute.For<ITenantDeletionRepository>(),
            auditLog ?? Substitute.For<IAuditLogRepository>(),
            transactionProvider ?? Substitute.For<IDatabaseTransactionProvider>(),
            billingApiClient ?? Substitute.For<IBillingApiClient>(),
            timeProvider ?? new FakeTimeProvider(FixedNow),
            logger ?? Substitute.For<ILogger<TenantPurgeJob>>());
    }
}
