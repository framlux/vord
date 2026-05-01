// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
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

    [Test]
    public async Task CreateMachineWithKeyAsync_WithinLimit_ReturnsMachineAndKey()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineRepository cache = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        Machine machine = TestDataBuilder.BuildMachine();

        (Machine? created, string? plaintextApiKey) = await cache.CreateMachineWithKeyAsync(machine, machineLimit: 5);

        await Assert.That(created).IsNotNull();
        await Assert.That(plaintextApiKey).IsNotNull();
        await Assert.That(created!.Id).IsNotEqualTo(0L);
        await Assert.That(created.TenantId).IsEqualTo(1);
        await Assert.That(created.IsDeleted).IsFalse();
        await Assert.That(created.Name).IsEqualTo(machine.Name);
    }

    [Test]
    public async Task CreateMachineWithKeyAsync_AtLimit_ReturnsNulls()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineRepository cache = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        // Insert one existing active machine to reach limit of 1
        Machine existingMachine = TestDataBuilder.BuildMachine();
        await dbFactory.Context.InsertWithInt64IdentityAsync(existingMachine);

        Machine newMachine = TestDataBuilder.BuildMachine();

        (Machine? created, string? plaintextApiKey) = await cache.CreateMachineWithKeyAsync(newMachine, machineLimit: 1);

        await Assert.That(created).IsNull();
        await Assert.That(plaintextApiKey).IsNull();
    }

    [Test]
    public async Task CreateMachineWithKeyAsync_NoLimit_ReturnsMachineAndKey()
    {
        using TestDatabaseFactory dbFactory = new();
        IMachineRepository cache = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        Machine machine = TestDataBuilder.BuildMachine();

        (Machine? created, string? plaintextApiKey) = await cache.CreateMachineWithKeyAsync(machine, machineLimit: null);

        await Assert.That(created).IsNotNull();
        await Assert.That(plaintextApiKey).IsNotNull();
        await Assert.That(created!.Id).IsNotEqualTo(0L);
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
}
