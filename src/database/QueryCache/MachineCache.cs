// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Models;
using System.Data;
using LinqToDB;
using LinqToDB.Async;
using LinqToDB.Data;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Text;

namespace Framlux.FleetManagement.Database.Cache;

/// <inheritdoc/>
public partial class DatabaseCache : IDatabaseCache
{
    /// <inheritdoc />
    public async Task<bool> DoesMachineExistAsync(string serialNumber, string systemId, string assetTag, int tenantId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serialNumber);
        ArgumentException.ThrowIfNullOrWhiteSpace(systemId);

        bool exists;

        try
        {
            // Data is normalized to lowercase at write time, so callers must
            // pass pre-lowered values for SerialNumber and SystemId.
            _logger.LogDebug("Searching for active Machine with Serial Number {SerialNumber}, System ID {SystemId}, or Asset Tag {AssetTag} in tenant {TenantId}", serialNumber, systemId, assetTag, tenantId);
            IQueryable<Machine> query = _db.Machines.Where(m =>
                m.TenantId == tenantId &&
                m.IsDeleted == false &&
                (m.SerialNumber == serialNumber ||
                m.SystemId == systemId ||
                (string.IsNullOrEmpty(assetTag) == false && m.AssetTagNumber == assetTag)));

            exists = await query.AnyAsync(cancellationToken);
            _logger.LogInformation("Active Machine query for Serial Number {SerialNumber}, System ID {SystemId}, or Asset Tag {AssetTag}: {FoundResult}", serialNumber, systemId, assetTag, exists);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to search for Machine by Serial Number {SerialNumber}, System ID {SystemId}, or Asset Tag {AssetTag}", serialNumber, systemId, assetTag);
            exists = false;
        }

        return exists;
    }

    /// <inheritdoc/>
    public async Task<(Machine? machine, string? plaintextApiKey)> CreateMachineWithKeyAsync(Machine machine, int? machineLimit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(machine);

        string plaintextApiKey = RandomNumberGenerator.GetHexString(64, true);
        string apiKeyHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(plaintextApiKey)));
        machine.ApiKeyHash = apiKeyHash;

        try
        {
            _logger.LogInformation("Creating Machine with Serial Number {SerialNumber}", machine.SerialNumber);
            // Use Serializable isolation to prevent concurrent registrations
            // from both passing the machine limit check before either inserts.
            using DataConnectionTransaction txn = await _db.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

            if (machineLimit.HasValue)
            {
                int activeMachineCount = await _db.Machines
                    .Where(m => m.TenantId == machine.TenantId && m.IsDeleted == false)
                    .CountAsync(cancellationToken);

                if (activeMachineCount >= machineLimit.Value)
                {
                    _logger.LogWarning("Tenant {TenantId} at machine limit ({Limit}) — rejecting machine creation", machine.TenantId, machineLimit.Value);

                    return (null, null);
                }
            }

            machine.Id = await _db.InsertWithInt64IdentityAsync(machine, token: cancellationToken);
            await txn.CommitAsync(cancellationToken);
            _logger.LogInformation("Created Machine with Serial Number {SerialNumber}, ID {MachineId}", machine.SerialNumber, machine.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create Machine with Serial Number {SerialNumber}", machine.SerialNumber);
            throw;
        }

        return (machine, plaintextApiKey);
    }

    /// <inheritdoc/>
    public async Task<Machine?> GetMachineAsync(long machineId, int tenantId, CancellationToken cancellationToken)
    {
        Machine? machine;

        try
        {
            _logger.LogInformation("Checking for Machine with ID {MachineId} in tenant {TenantId}", machineId, tenantId);
            machine = await _db.Machines
                                      .Where(m => (m.Id == machineId) &&
                                                  (m.TenantId == tenantId) &&
                                                  (m.IsDeleted == false))
                                      .SingleOrDefaultAsync(cancellationToken);
            _logger.LogInformation("Found Machine with ID {MachineId}: {Found}", machineId, machine is not null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to query for Machine with ID {MachineId}", machineId);
            machine = null;
        }

        return machine;
    }

    /// <inheritdoc/>
    public async Task<Machine?> GetMachineByApiKeyAsync(string apiKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        string apiKeyHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey)));
        Machine? machine;

        try
        {
            _logger.LogInformation("Searching for Machine with API Key hash");
            machine = await _db.Machines.Where(m => (m.ApiKeyHash == apiKeyHash) &&
                                                          (m.IsDeleted == false))
                                              .SingleOrDefaultAsync(cancellationToken);
            _logger.LogInformation("Found Machine with API Key hash: {Found}", machine is not null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to query for Machine with API Key hash");
            machine = null;
        }

        return machine;
    }
}
