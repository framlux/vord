// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.Extensions.Logging.Abstractions;
using System.Security.Cryptography;
using System.Text;

namespace Framlux.FleetManagement.Test.Functional.DatabaseRepository;

/// <summary>
/// Functional tests for machine-related methods on <see cref="Database.Repositories.DatabaseRepository"/>.
/// </summary>
public class MachineCacheTests
{
    // ========== DoesMachineExistAsync tests ==========

    [Test]
    public async Task DoesMachineExistAsync_MatchBySerialNumber_ReturnsTrue()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineRepository cache = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        Machine machine = TestDataBuilder.BuildMachine();
        await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

        bool result = await cache.DoesMachineExistAsync(machine.SerialNumber, "no-match", "", 1);

        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task DoesMachineExistAsync_MatchBySystemId_ReturnsTrue()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineRepository cache = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        Machine machine = TestDataBuilder.BuildMachine();
        await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

        bool result = await cache.DoesMachineExistAsync("no-match", machine.SystemId, "", 1);

        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task DoesMachineExistAsync_NoMatch_ReturnsFalse()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineRepository cache = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        bool result = await cache.DoesMachineExistAsync("no-serial", "no-sysid", "", 1);

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task DoesMachineExistAsync_NormalizedSerialNumber_ReturnsTrue()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineRepository cache = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        Machine machine = TestDataBuilder.BuildMachine();
        await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

        // Callers are expected to normalize to lowercase before querying.
        bool result = await cache.DoesMachineExistAsync(machine.SerialNumber.ToLowerInvariant(), "no-match", "", 1);

        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task DoesMachineExistAsync_DeletedMachine_ReturnsFalse()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineRepository cache = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        Machine machine = TestDataBuilder.BuildMachine();
        machine.IsDeleted = true;
        await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

        bool result = await cache.DoesMachineExistAsync(machine.SerialNumber, machine.SystemId, "", 1);

        await Assert.That(result).IsFalse();
    }

    // ========== CreateMachineWithKeyAsync tests ==========

    // A fixed reference instant so the single-use token ExpiresAt > now check is deterministic.
    private static readonly DateTimeOffset ReferenceNow = new(2026, 06, 26, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Seeds an available (non-consumed, non-revoked, future-dated) single-use registration token
    /// and returns its id so the atomic consume inside CreateMachineWithKeyAsync can succeed.
    /// </summary>
    private static async Task<long> SeedAvailableTokenAsync(TestDatabaseFactory dbFactory)
    {
        return await dbFactory.Context.InsertWithInt64IdentityAsync(new RegistrationToken
        {
            TenantId = 1,
            TokenHash = Guid.NewGuid().ToString("N"),
            Name = "Token",
            CreatedByUserId = 1,
            CreatedAt = ReferenceNow,
            ExpiresAt = ReferenceNow.AddDays(7),
            IsRevoked = false,
        });
    }

    [Test]
    public async Task CreateMachineWithKeyAsync_WithinLimit_ReturnsMachineAndKey_AndConsumesToken()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineRepository cache = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        long tokenId = await SeedAvailableTokenAsync(dbFactory);
        Machine machine = TestDataBuilder.BuildMachine(registrationTokenId: tokenId);

        (Machine? created, string? plaintextApiKey) = await cache.CreateMachineWithKeyAsync(machine, tokenId, ReferenceNow, machineLimit: 5);

        await Assert.That(created).IsNotNull();
        await Assert.That(plaintextApiKey).IsNotNull();
        await Assert.That(created!.Id).IsNotEqualTo(0L);
        await Assert.That(created.TenantId).IsEqualTo(1);
        await Assert.That(created.IsDeleted).IsFalse();
        await Assert.That(created.Name).IsEqualTo(machine.Name);

        RegistrationToken token = await dbFactory.Context.RegistrationTokens.FirstAsync(t => t.Id == tokenId);
        await Assert.That(token.ConsumedAt).IsEqualTo(ReferenceNow);
        await Assert.That(token.ConsumedByMachineId).IsEqualTo(created.Id);
    }

    [Test]
    public async Task CreateMachineWithKeyAsync_AtLimit_ReturnsNulls_AndDoesNotConsumeToken()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineRepository cache = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        // Insert one existing active machine to reach limit of 1
        Machine existingMachine = TestDataBuilder.BuildMachine();
        await dbFactory.Context.InsertWithInt64IdentityAsync(existingMachine);

        long tokenId = await SeedAvailableTokenAsync(dbFactory);
        Machine newMachine = TestDataBuilder.BuildMachine(registrationTokenId: tokenId);

        (Machine? created, string? plaintextApiKey) = await cache.CreateMachineWithKeyAsync(newMachine, tokenId, ReferenceNow, machineLimit: 1);

        await Assert.That(created).IsNull();
        await Assert.That(plaintextApiKey).IsNull();

        // The limit guard returns before the consume, so the token must remain available.
        RegistrationToken token = await dbFactory.Context.RegistrationTokens.FirstAsync(t => t.Id == tokenId);
        await Assert.That(token.ConsumedAt).IsNull();
    }

    [Test]
    public async Task CreateMachineWithKeyAsync_NoLimit_ReturnsMachineAndKey()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineRepository cache = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        long tokenId = await SeedAvailableTokenAsync(dbFactory);
        Machine machine = TestDataBuilder.BuildMachine(registrationTokenId: tokenId);

        (Machine? created, string? plaintextApiKey) = await cache.CreateMachineWithKeyAsync(machine, tokenId, ReferenceNow, machineLimit: null);

        await Assert.That(created).IsNotNull();
        await Assert.That(plaintextApiKey).IsNotNull();
        await Assert.That(created!.Id).IsNotEqualTo(0L);
    }

    [Test]
    public async Task CreateMachineWithKeyAsync_ConsumedToken_ReturnsNulls_AndCreatesNoMachine()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineRepository cache = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        long tokenId = await SeedAvailableTokenAsync(dbFactory);

        // First registration consumes the single-use token.
        Machine first = TestDataBuilder.BuildMachine(registrationTokenId: tokenId);
        (Machine? firstCreated, string? _) = await cache.CreateMachineWithKeyAsync(first, tokenId, ReferenceNow, machineLimit: null);
        await Assert.That(firstCreated).IsNotNull();

        // Second registration with the same token must be rejected and roll back.
        Machine second = TestDataBuilder.BuildMachine(registrationTokenId: tokenId);
        (Machine? secondCreated, string? secondKey) = await cache.CreateMachineWithKeyAsync(second, tokenId, ReferenceNow, machineLimit: null);

        await Assert.That(secondCreated).IsNull();
        await Assert.That(secondKey).IsNull();

        int machineCount = await dbFactory.Context.Machines.CountAsync(m => m.RegistrationTokenId == tokenId);
        await Assert.That(machineCount).IsEqualTo(1);
    }

    // ========== GetMachineAsync tests ==========

    [Test]
    public async Task GetMachineAsync_ExistingActiveMachine_ReturnsMachine()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineRepository cache = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        Machine machine = TestDataBuilder.BuildMachine(tenantId: 1);
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

        Machine? result = await cache.GetMachineAsync(machine.Id, 1);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Id).IsEqualTo(machine.Id);
        await Assert.That(result.TenantId).IsEqualTo(1);
        await Assert.That(result.IsDeleted).IsFalse();
    }

    [Test]
    public async Task GetMachineAsync_DeletedMachine_ReturnsNull()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineRepository cache = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        Machine machine = TestDataBuilder.BuildMachine(tenantId: 1);
        machine.IsDeleted = true;
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

        Machine? result = await cache.GetMachineAsync(machine.Id, 1);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task GetMachineAsync_WrongTenant_ReturnsNull()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineRepository cache = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        Machine machine = TestDataBuilder.BuildMachine(tenantId: 1);
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

        Machine? result = await cache.GetMachineAsync(machine.Id, 999);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task GetMachineAsync_NonExistentId_ReturnsNull()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineRepository cache = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        Machine? result = await cache.GetMachineAsync(99999, 1);

        await Assert.That(result).IsNull();
    }

    // ========== GetMachineByApiKeyAsync tests ==========

    [Test]
    public async Task GetMachineByApiKeyAsync_ValidApiKey_ReturnsMachine()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineRepository cache = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        string plaintextKey = "test-api-key-for-lookup-12345";
        string apiKeyHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(plaintextKey)));

        Machine machine = TestDataBuilder.BuildMachine(tenantId: 1, apiKeyHash: apiKeyHash);
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

        Machine? result = await cache.GetMachineByApiKeyAsync(plaintextKey);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Id).IsEqualTo(machine.Id);
        await Assert.That(result.ApiKeyHash).IsEqualTo(apiKeyHash);
    }

    [Test]
    public async Task GetMachineByApiKeyAsync_InvalidApiKey_ReturnsNull()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineRepository cache = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        Machine? result = await cache.GetMachineByApiKeyAsync("nonexistent-key");

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task GetMachineByApiKeyAsync_DeletedMachine_ReturnsNull()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineRepository cache = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        string plaintextKey = "test-api-key-deleted-machine";
        string apiKeyHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(plaintextKey)));

        Machine machine = TestDataBuilder.BuildMachine(tenantId: 1, apiKeyHash: apiKeyHash);
        machine.IsDeleted = true;
        await dbFactory.Context.InsertWithInt64IdentityAsync(machine);

        Machine? result = await cache.GetMachineByApiKeyAsync(plaintextKey);

        await Assert.That(result).IsNull();
    }

    // ========== DoesMachineExistAsync DB-fault propagation ==========

    [Test]
    public async Task DoesMachineExistAsync_DatabaseFault_PropagatesInsteadOfReturningFalse()
    {
        // A transient DB fault must NOT be swallowed into a false "machine does not exist"
        // answer — doing so would let a duplicate or over-limit registration proceed.
        // Dispose the context to force the underlying query to fault, then assert the call
        // surfaces the exception rather than returning false.
        TestDatabaseFactory dbFactory = new();
        IMachineRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());
        dbFactory.Dispose();

        await Assert.That(async () => await repo.DoesMachineExistAsync("some-serial", "some-sysid", "", 1))
            .Throws<Exception>();
    }

    [Test]
    public async Task GetMachineByApiKeyAsync_DatabaseFault_PropagatesInsteadOfReturningNull()
    {
        // A transient DB fault must NOT be swallowed into a null result, which callers treat
        // as "no machine found" and would reject telemetry from a legitimately registered machine.
        TestDatabaseFactory dbFactory = new();
        IMachineRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());
        dbFactory.Dispose();

        await Assert.That(async () => await repo.GetMachineByApiKeyAsync("some-api-key"))
            .Throws<Exception>();
    }

    [Test]
    public async Task GetMachineAsync_DatabaseFault_PropagatesInsteadOfReturningNull()
    {
        TestDatabaseFactory dbFactory = new();
        IMachineRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());
        dbFactory.Dispose();

        await Assert.That(async () => await repo.GetMachineAsync(1, 1))
            .Throws<Exception>();
    }

    // ========== GetMachineCountsByTenantsAsync tests ==========

    [Test]
    public async Task GetMachineCountsByTenantsAsync_GroupsActiveMachinesByTenant()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        // Tenant 1: two active, one deleted (deleted must be excluded). Tenant 2: one active.
        await dbFactory.Context.InsertWithInt64IdentityAsync(TestDataBuilder.BuildMachine(tenantId: 1));
        await dbFactory.Context.InsertWithInt64IdentityAsync(TestDataBuilder.BuildMachine(tenantId: 1));
        Machine deleted = TestDataBuilder.BuildMachine(tenantId: 1);
        deleted.IsDeleted = true;
        await dbFactory.Context.InsertWithInt64IdentityAsync(deleted);
        await dbFactory.Context.InsertWithInt64IdentityAsync(TestDataBuilder.BuildMachine(tenantId: 2));

        Dictionary<int, int> counts = await repo.GetMachineCountsByTenantsAsync([1, 2], CancellationToken.None);

        await Assert.That(counts[1]).IsEqualTo(2);
        await Assert.That(counts[2]).IsEqualTo(1);
    }

    [Test]
    public async Task GetMachineCountsByTenantsAsync_EmptyTenantList_ReturnsEmpty()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        Dictionary<int, int> counts = await repo.GetMachineCountsByTenantsAsync([], CancellationToken.None);

        await Assert.That(counts.Count).IsEqualTo(0);
    }

    [Test]
    public async Task GetMachineCountsByTenantsAsync_TenantWithNoMachines_OmittedFromResult()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        await dbFactory.Context.InsertWithInt64IdentityAsync(TestDataBuilder.BuildMachine(tenantId: 1));

        Dictionary<int, int> counts = await repo.GetMachineCountsByTenantsAsync([1, 2], CancellationToken.None);

        await Assert.That(counts.ContainsKey(2)).IsFalse();
        await Assert.That(counts[1]).IsEqualTo(1);
    }
}
