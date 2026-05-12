// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using Microsoft.Extensions.Logging.Abstractions;

namespace Framlux.FleetManagement.Test.Functional.DatabaseRepository;

/// <summary>
/// Functional tests for remote-command-related methods on <see cref="Database.Repositories.DatabaseRepository"/>.
/// </summary>
public class RemoteCommandRepositoryTests
{
    /// <summary>
    /// Seeds a user, tenant, machine, signing key, and authorized key in the database.
    /// Remote command tests require these prerequisite records for FK satisfaction.
    /// </summary>
    private static async Task<(int userId, int tenantId, long machineId, int signingKeyId)> SeedPrerequisitesAsync(TestDatabaseFactory dbFactory)
    {
        UserAccount user = TestDataBuilder.BuildUser();
        int userId = await dbFactory.Context.InsertWithInt32IdentityAsync(user);

        Tenant tenant = TestDataBuilder.BuildTenant(createdByUserId: userId);
        int tenantId = await dbFactory.Context.InsertWithInt32IdentityAsync(tenant);

        Machine machine = TestDataBuilder.BuildMachine(tenantId: tenantId);
        long machineId = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

        UserSigningKey signingKey = TestDataBuilder.BuildSigningKey(userId: userId, tenantId: tenantId);
        int signingKeyId = await dbFactory.Context.InsertWithInt32IdentityAsync(signingKey);

        return (userId, tenantId, machineId, signingKeyId);
    }

    // ========== CreateRemoteCommandAsync tests ==========

    [Test]
    public async Task CreateRemoteCommandAsync_ValidCommand_ReturnsCommandWithId()
    {
        using TestDatabaseFactory dbFactory = new();
        IRemoteCommandRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId, long machineId, int signingKeyId) = await SeedPrerequisitesAsync(dbFactory);

        RemoteCommand command = TestDataBuilder.BuildRemoteCommand(
            machineId: machineId,
            tenantId: tenantId,
            userId: userId,
            signingKeyId: signingKeyId);

        RemoteCommand result = await repo.CreateRemoteCommandAsync(command);

        await Assert.That(result).IsNotNull();
        await Assert.That(result.Id).IsNotEqualTo(0);
        await Assert.That(result.CommandId).IsEqualTo(command.CommandId);
        await Assert.That(result.TenantId).IsEqualTo(tenantId);
        await Assert.That(result.MachineId).IsEqualTo(machineId);
        await Assert.That(result.Status).IsEqualTo(RemoteCommandStatus.Pending);
    }

    // ========== GetPendingCommandsForMachineAsync tests ==========

    [Test]
    public async Task GetPendingCommandsForMachineAsync_PendingCommandExists_ReturnsPendingCommands()
    {
        using TestDatabaseFactory dbFactory = new();
        IRemoteCommandRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId, long machineId, int signingKeyId) = await SeedPrerequisitesAsync(dbFactory);

        RemoteCommand pendingCommand = TestDataBuilder.BuildRemoteCommand(
            machineId: machineId, tenantId: tenantId, userId: userId, signingKeyId: signingKeyId,
            status: RemoteCommandStatus.Pending);
        pendingCommand.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10);
        await repo.CreateRemoteCommandAsync(pendingCommand);

        RemoteCommand executedCommand = TestDataBuilder.BuildRemoteCommand(
            machineId: machineId, tenantId: tenantId, userId: userId, signingKeyId: signingKeyId,
            status: RemoteCommandStatus.Executed);
        executedCommand.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10);
        await repo.CreateRemoteCommandAsync(executedCommand);

        List<RemoteCommand> result = await repo.GetPendingCommandsForMachineAsync(machineId, tenantId);

        await Assert.That(result.Count).IsEqualTo(1);
        await Assert.That(result[0].Status).IsEqualTo(RemoteCommandStatus.Pending);
    }

    [Test]
    public async Task GetPendingCommandsForMachineAsync_NoCommands_ReturnsEmptyList()
    {
        using TestDatabaseFactory dbFactory = new();
        IRemoteCommandRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        List<RemoteCommand> result = await repo.GetPendingCommandsForMachineAsync(99999, 99999);

        await Assert.That(result.Count).IsEqualTo(0);
    }

    [Test]
    public async Task GetPendingCommandsForMachineAsync_DifferentTenant_ReturnsEmpty()
    {
        using TestDatabaseFactory dbFactory = new();
        IRemoteCommandRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId, long machineId, int signingKeyId) = await SeedPrerequisitesAsync(dbFactory);

        RemoteCommand command = TestDataBuilder.BuildRemoteCommand(
            machineId: machineId, tenantId: tenantId, userId: userId, signingKeyId: signingKeyId,
            status: RemoteCommandStatus.Pending);
        command.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10);
        await repo.CreateRemoteCommandAsync(command);

        int otherTenantId = tenantId + 1000;
        List<RemoteCommand> result = await repo.GetPendingCommandsForMachineAsync(machineId, otherTenantId);

        await Assert.That(result.Count).IsEqualTo(0);
    }

    [Test]
    public async Task GetPendingCommandsForMachineAsync_ExpiredCommand_ReturnsEmpty()
    {
        using TestDatabaseFactory dbFactory = new();
        IRemoteCommandRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId, long machineId, int signingKeyId) = await SeedPrerequisitesAsync(dbFactory);

        RemoteCommand command = TestDataBuilder.BuildRemoteCommand(
            machineId: machineId, tenantId: tenantId, userId: userId, signingKeyId: signingKeyId,
            status: RemoteCommandStatus.Pending);
        command.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        await repo.CreateRemoteCommandAsync(command);

        List<RemoteCommand> result = await repo.GetPendingCommandsForMachineAsync(machineId, tenantId);

        await Assert.That(result.Count).IsEqualTo(0);
    }

    // ========== GetRemoteCommandByCommandIdAsync tests ==========

    [Test]
    public async Task GetRemoteCommandByCommandIdAsync_ExistingCommandId_ReturnsCommand()
    {
        using TestDatabaseFactory dbFactory = new();
        IRemoteCommandRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId, long machineId, int signingKeyId) = await SeedPrerequisitesAsync(dbFactory);

        string commandId = Guid.NewGuid().ToString("D");
        RemoteCommand command = TestDataBuilder.BuildRemoteCommand(
            machineId: machineId, tenantId: tenantId, userId: userId, signingKeyId: signingKeyId,
            commandId: commandId);
        await repo.CreateRemoteCommandAsync(command);

        RemoteCommand? result = await repo.GetRemoteCommandByCommandIdAsync(commandId);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.CommandId).IsEqualTo(commandId);
    }

    [Test]
    public async Task GetRemoteCommandByCommandIdAsync_NonExistentCommandId_ReturnsNull()
    {
        using TestDatabaseFactory dbFactory = new();
        IRemoteCommandRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        RemoteCommand? result = await repo.GetRemoteCommandByCommandIdAsync("nonexistent-command-id");

        await Assert.That(result).IsNull();
    }

    // ========== GetRemoteCommandByIdAsync tests ==========

    [Test]
    public async Task GetRemoteCommandByIdAsync_ExistingIdAndTenant_ReturnsCommandWithRelations()
    {
        using TestDatabaseFactory dbFactory = new();
        IRemoteCommandRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId, long machineId, int signingKeyId) = await SeedPrerequisitesAsync(dbFactory);

        RemoteCommand command = TestDataBuilder.BuildRemoteCommand(
            machineId: machineId, tenantId: tenantId, userId: userId, signingKeyId: signingKeyId);
        RemoteCommand created = await repo.CreateRemoteCommandAsync(command);

        RemoteCommand? result = await repo.GetRemoteCommandByIdAsync(created.Id, tenantId);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Id).IsEqualTo(created.Id);
        await Assert.That(result.User).IsNotNull();
        await Assert.That(result.Machine).IsNotNull();
    }

    [Test]
    public async Task GetRemoteCommandByIdAsync_NonExistentId_ReturnsNull()
    {
        using TestDatabaseFactory dbFactory = new();
        IRemoteCommandRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        RemoteCommand? result = await repo.GetRemoteCommandByIdAsync(99999, 1);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task GetRemoteCommandByIdAsync_WrongTenant_ReturnsNull()
    {
        using TestDatabaseFactory dbFactory = new();
        IRemoteCommandRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId, long machineId, int signingKeyId) = await SeedPrerequisitesAsync(dbFactory);

        RemoteCommand command = TestDataBuilder.BuildRemoteCommand(
            machineId: machineId, tenantId: tenantId, userId: userId, signingKeyId: signingKeyId);
        RemoteCommand created = await repo.CreateRemoteCommandAsync(command);

        int otherTenantId = tenantId + 1000;
        RemoteCommand? result = await repo.GetRemoteCommandByIdAsync(created.Id, otherTenantId);

        await Assert.That(result).IsNull();
    }

    // ========== GetCommandsForMachineAsync tests ==========

    [Test]
    public async Task GetCommandsForMachineAsync_MultipleCommands_ReturnsPaginatedResultsOrderedByCreatedAtDesc()
    {
        using TestDatabaseFactory dbFactory = new();
        IRemoteCommandRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId, long machineId, int signingKeyId) = await SeedPrerequisitesAsync(dbFactory);

        // Insert 3 commands with distinct CreatedAt timestamps
        for (int i = 0; i < 3; i++)
        {
            RemoteCommand cmd = TestDataBuilder.BuildRemoteCommand(
                machineId: machineId, tenantId: tenantId, userId: userId, signingKeyId: signingKeyId);
            cmd.CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-10 + i);
            await repo.CreateRemoteCommandAsync(cmd);
        }

        List<RemoteCommand> page1 = await repo.GetCommandsForMachineAsync(machineId, tenantId, page: 1, pageSize: 2);
        List<RemoteCommand> page2 = await repo.GetCommandsForMachineAsync(machineId, tenantId, page: 2, pageSize: 2);

        await Assert.That(page1.Count).IsEqualTo(2);
        await Assert.That(page2.Count).IsEqualTo(1);

        // Verify descending order by CreatedAt
        await Assert.That(page1[0].CreatedAt >= page1[1].CreatedAt).IsTrue();
    }

    [Test]
    public async Task GetCommandsForMachineAsync_NoCommands_ReturnsEmptyList()
    {
        using TestDatabaseFactory dbFactory = new();
        IRemoteCommandRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        List<RemoteCommand> result = await repo.GetCommandsForMachineAsync(99999, 99999, page: 1, pageSize: 10);

        await Assert.That(result.Count).IsEqualTo(0);
    }

    // ========== UpdateRemoteCommandStatusAsync tests ==========

    [Test]
    public async Task UpdateRemoteCommandStatusAsync_SetToExecuted_UpdatesStatusAndResultFields()
    {
        using TestDatabaseFactory dbFactory = new();
        IRemoteCommandRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId, long machineId, int signingKeyId) = await SeedPrerequisitesAsync(dbFactory);

        RemoteCommand command = TestDataBuilder.BuildRemoteCommand(
            machineId: machineId, tenantId: tenantId, userId: userId, signingKeyId: signingKeyId,
            status: RemoteCommandStatus.Pending);
        await repo.CreateRemoteCommandAsync(command);

        await repo.UpdateRemoteCommandStatusAsync(
            command.CommandId, machineId, RemoteCommandStatus.Executed,
            exitCode: 0, stdout: "success output", stderr: null, resultMessage: "Command completed");

        RemoteCommand? updated = await repo.GetRemoteCommandByCommandIdAsync(command.CommandId);

        await Assert.That(updated).IsNotNull();
        await Assert.That(updated!.Status).IsEqualTo(RemoteCommandStatus.Executed);
        await Assert.That(updated.ExitCode).IsEqualTo(0);
        await Assert.That(updated.Stdout).IsEqualTo("success output");
        await Assert.That(updated.Stderr).IsNull();
        await Assert.That(updated.ResultMessage).IsEqualTo("Command completed");
        await Assert.That(updated.CompletedAt).IsNotNull();
    }

    [Test]
    public async Task UpdateRemoteCommandStatusAsync_SetToDelivered_SetsDeliveredTimestamp()
    {
        using TestDatabaseFactory dbFactory = new();
        IRemoteCommandRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId, long machineId, int signingKeyId) = await SeedPrerequisitesAsync(dbFactory);

        RemoteCommand command = TestDataBuilder.BuildRemoteCommand(
            machineId: machineId, tenantId: tenantId, userId: userId, signingKeyId: signingKeyId,
            status: RemoteCommandStatus.Pending);
        await repo.CreateRemoteCommandAsync(command);

        await repo.UpdateRemoteCommandStatusAsync(
            command.CommandId, machineId, RemoteCommandStatus.Delivered,
            exitCode: null, stdout: null, stderr: null, resultMessage: null);

        RemoteCommand? updated = await repo.GetRemoteCommandByCommandIdAsync(command.CommandId);

        await Assert.That(updated).IsNotNull();
        await Assert.That(updated!.Status).IsEqualTo(RemoteCommandStatus.Delivered);
        await Assert.That(updated.DeliveredAt).IsNotNull();
    }

    [Test]
    public async Task UpdateRemoteCommandStatusAsync_SetToFailed_SetsCompletedAtAndStderr()
    {
        using TestDatabaseFactory dbFactory = new();
        IRemoteCommandRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId, long machineId, int signingKeyId) = await SeedPrerequisitesAsync(dbFactory);

        RemoteCommand command = TestDataBuilder.BuildRemoteCommand(
            machineId: machineId, tenantId: tenantId, userId: userId, signingKeyId: signingKeyId,
            status: RemoteCommandStatus.Pending);
        await repo.CreateRemoteCommandAsync(command);

        await repo.UpdateRemoteCommandStatusAsync(
            command.CommandId, machineId, RemoteCommandStatus.Failed,
            exitCode: 1, stdout: null, stderr: "error occurred", resultMessage: "Failed");

        RemoteCommand? updated = await repo.GetRemoteCommandByCommandIdAsync(command.CommandId);

        await Assert.That(updated).IsNotNull();
        await Assert.That(updated!.Status).IsEqualTo(RemoteCommandStatus.Failed);
        await Assert.That(updated.ExitCode).IsEqualTo(1);
        await Assert.That(updated.Stderr).IsEqualTo("error occurred");
        await Assert.That(updated.CompletedAt).IsNotNull();
    }

    // ========== IsNonceUsedAsync tests ==========

    [Test]
    public async Task IsNonceUsedAsync_ExistingNonce_ReturnsTrue()
    {
        using TestDatabaseFactory dbFactory = new();
        IRemoteCommandRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId, long machineId, int signingKeyId) = await SeedPrerequisitesAsync(dbFactory);

        RemoteCommand command = TestDataBuilder.BuildRemoteCommand(
            machineId: machineId, tenantId: tenantId, userId: userId, signingKeyId: signingKeyId);
        await repo.CreateRemoteCommandAsync(command);

        bool result = await repo.IsNonceUsedAsync(command.Nonce);

        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task IsNonceUsedAsync_NonExistentNonce_ReturnsFalse()
    {
        using TestDatabaseFactory dbFactory = new();
        IRemoteCommandRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        bool result = await repo.IsNonceUsedAsync("nonexistent-nonce-value");

        await Assert.That(result).IsFalse();
    }

    // ========== ExpirePendingCommandsAsync tests ==========

    [Test]
    public async Task ExpirePendingCommandsAsync_ExpiredPendingCommands_SetsStatusToExpired()
    {
        using TestDatabaseFactory dbFactory = new();
        IRemoteCommandRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId, long machineId, int signingKeyId) = await SeedPrerequisitesAsync(dbFactory);

        // Create an expired pending command
        RemoteCommand expiredCommand = TestDataBuilder.BuildRemoteCommand(
            machineId: machineId, tenantId: tenantId, userId: userId, signingKeyId: signingKeyId,
            status: RemoteCommandStatus.Pending);
        expiredCommand.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        await repo.CreateRemoteCommandAsync(expiredCommand);

        // Create a still-valid pending command
        RemoteCommand validCommand = TestDataBuilder.BuildRemoteCommand(
            machineId: machineId, tenantId: tenantId, userId: userId, signingKeyId: signingKeyId,
            status: RemoteCommandStatus.Pending);
        validCommand.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10);
        await repo.CreateRemoteCommandAsync(validCommand);

        await repo.ExpirePendingCommandsAsync();

        RemoteCommand? expiredResult = await repo.GetRemoteCommandByCommandIdAsync(expiredCommand.CommandId);
        RemoteCommand? validResult = await repo.GetRemoteCommandByCommandIdAsync(validCommand.CommandId);

        await Assert.That(expiredResult).IsNotNull();
        await Assert.That(expiredResult!.Status).IsEqualTo(RemoteCommandStatus.Expired);
        await Assert.That(expiredResult.CompletedAt).IsNotNull();

        await Assert.That(validResult).IsNotNull();
        await Assert.That(validResult!.Status).IsEqualTo(RemoteCommandStatus.Pending);
    }

    [Test]
    public async Task ExpirePendingCommandsAsync_NoExpiredCommands_LeavesAllUnchanged()
    {
        using TestDatabaseFactory dbFactory = new();
        IRemoteCommandRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId, long machineId, int signingKeyId) = await SeedPrerequisitesAsync(dbFactory);

        RemoteCommand command = TestDataBuilder.BuildRemoteCommand(
            machineId: machineId, tenantId: tenantId, userId: userId, signingKeyId: signingKeyId,
            status: RemoteCommandStatus.Pending);
        command.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10);
        await repo.CreateRemoteCommandAsync(command);

        await repo.ExpirePendingCommandsAsync();

        RemoteCommand? result = await repo.GetRemoteCommandByCommandIdAsync(command.CommandId);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Status).IsEqualTo(RemoteCommandStatus.Pending);
    }

    // ========== MarkCommandsDeliveredAsync tests ==========

    [Test]
    public async Task MarkCommandsDeliveredAsync_PendingCommands_SetsStatusToDeliveredWithTimestamp()
    {
        using TestDatabaseFactory dbFactory = new();
        IRemoteCommandRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId, long machineId, int signingKeyId) = await SeedPrerequisitesAsync(dbFactory);

        RemoteCommand cmd1 = TestDataBuilder.BuildRemoteCommand(
            machineId: machineId, tenantId: tenantId, userId: userId, signingKeyId: signingKeyId,
            status: RemoteCommandStatus.Pending);
        await repo.CreateRemoteCommandAsync(cmd1);

        RemoteCommand cmd2 = TestDataBuilder.BuildRemoteCommand(
            machineId: machineId, tenantId: tenantId, userId: userId, signingKeyId: signingKeyId,
            status: RemoteCommandStatus.Pending);
        await repo.CreateRemoteCommandAsync(cmd2);

        List<string> commandIds = new() { cmd1.CommandId, cmd2.CommandId };
        await repo.MarkCommandsDeliveredAsync(commandIds);

        RemoteCommand? result1 = await repo.GetRemoteCommandByCommandIdAsync(cmd1.CommandId);
        RemoteCommand? result2 = await repo.GetRemoteCommandByCommandIdAsync(cmd2.CommandId);

        await Assert.That(result1).IsNotNull();
        await Assert.That(result1!.Status).IsEqualTo(RemoteCommandStatus.Delivered);
        await Assert.That(result1.DeliveredAt).IsNotNull();

        await Assert.That(result2).IsNotNull();
        await Assert.That(result2!.Status).IsEqualTo(RemoteCommandStatus.Delivered);
        await Assert.That(result2.DeliveredAt).IsNotNull();
    }

    [Test]
    public async Task MarkCommandsDeliveredAsync_EmptyList_DoesNotThrow()
    {
        using TestDatabaseFactory dbFactory = new();
        IRemoteCommandRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        List<string> emptyIds = new();

        // Should not throw when given an empty list
        await repo.MarkCommandsDeliveredAsync(emptyIds);
    }

    [Test]
    public async Task MarkCommandsDeliveredAsync_NonPendingCommand_DoesNotUpdateStatus()
    {
        using TestDatabaseFactory dbFactory = new();
        IRemoteCommandRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId, long machineId, int signingKeyId) = await SeedPrerequisitesAsync(dbFactory);

        // Create a command that is already Executed, not Pending
        RemoteCommand command = TestDataBuilder.BuildRemoteCommand(
            machineId: machineId, tenantId: tenantId, userId: userId, signingKeyId: signingKeyId,
            status: RemoteCommandStatus.Executed);
        await repo.CreateRemoteCommandAsync(command);

        List<string> commandIds = new() { command.CommandId };
        await repo.MarkCommandsDeliveredAsync(commandIds);

        RemoteCommand? result = await repo.GetRemoteCommandByCommandIdAsync(command.CommandId);

        await Assert.That(result).IsNotNull();
        // Status should remain Executed since MarkCommandsDelivered only targets Pending commands
        await Assert.That(result!.Status).IsEqualTo(RemoteCommandStatus.Executed);
    }
}
