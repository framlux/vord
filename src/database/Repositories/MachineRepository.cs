// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using System.Data;
using LinqToDB;
using LinqToDB.Async;
using LinqToDB.Data;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Text;

namespace Framlux.FleetManagement.Database.Repositories;

/// <inheritdoc/>
public partial class DatabaseRepository : IMachineRepository
{
    /// <inheritdoc/>
    public async Task<Machine?> GetMachineBySerialAndSystemIdAsync(string serialNumber, string systemId, int tenantId, CancellationToken cancellationToken)
    {
        Machine? machine = await _db.Machines
            .FirstOrDefaultAsync(m => (m.SerialNumber == serialNumber) &&
                                      (m.SystemId == systemId) &&
                                      (m.TenantId == tenantId) &&
                                      (m.IsDeleted == false), cancellationToken);

        return machine;
    }

    /// <inheritdoc/>
    public async Task<int> MarkKeyDeliveredAsync(long machineId, CancellationToken cancellationToken)
    {
        int updated = await _db.Machines
            .Where(m => (m.Id == machineId) && (m.KeyDeliveredAt == null))
            .Set(m => m.KeyDeliveredAt, DateTimeOffset.UtcNow)
            .UpdateAsync(cancellationToken);

        return updated;
    }

    /// <inheritdoc/>
    public async Task SetKeyDeliveredAsync(long machineId, CancellationToken cancellationToken)
    {
        await _db.Machines
            .Where(m => m.Id == machineId)
            .Set(m => m.KeyDeliveredAt, DateTimeOffset.UtcNow)
            .UpdateAsync(cancellationToken);
    }

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
    public async Task<string?> ReissueApiKeyAsync(long machineId, CancellationToken cancellationToken)
    {
        string plaintextApiKey = RandomNumberGenerator.GetHexString(64, true);
        string apiKeyHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(plaintextApiKey)));

        try
        {
            _logger.LogInformation("Re-issuing API key for Machine {MachineId}", machineId);
            int updated = await _db.Machines
                .Where(m => (m.Id == machineId) && (m.IsDeleted == false))
                .Set(m => m.ApiKeyHash, apiKeyHash)
                .Set(m => m.KeyDeliveredAt, (DateTimeOffset?)null)
                .UpdateAsync(cancellationToken);

            if (updated == 0)
            {
                _logger.LogWarning("Re-issue failed: Machine {MachineId} not found or deleted", machineId);

                return null;
            }

            _logger.LogInformation("API key re-issued for Machine {MachineId}", machineId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to re-issue API key for Machine {MachineId}", machineId);
            throw;
        }

        return plaintextApiKey;
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

    /// <inheritdoc/>
    public async Task<int> SoftDeleteMachineAsync(long machineId, int tenantId, int userId, CancellationToken cancellationToken)
    {
        int updated = await _db.Machines
            .Where(m => m.Id == machineId && m.TenantId == tenantId && m.IsDeleted == false)
            .Set(m => m.IsDeleted, true)
            .Set(m => m.DeletedOn, DateTimeOffset.UtcNow)
            .Set(m => m.DeletedByUserId, userId)
            .UpdateAsync(cancellationToken);

        return updated;
    }

    /// <inheritdoc/>
    public async Task<int> GetActiveMachineCountAsync(int tenantId, CancellationToken cancellationToken)
    {
        int count = await _db.Machines
            .Where(m => m.TenantId == tenantId && m.IsDeleted == false)
            .CountAsync(cancellationToken);

        return count;
    }

    /// <inheritdoc/>
    public async Task<Machine?> GetActiveMachineByIdAsync(long machineId, int tenantId, CancellationToken cancellationToken)
    {
        Machine? machine = await _db.Machines
            .FirstOrDefaultAsync(m => m.Id == machineId && m.TenantId == tenantId && m.IsDeleted == false, cancellationToken);

        return machine;
    }

    /// <inheritdoc/>
    public async Task UpdateMachineFieldsAsync(long machineId, string name, string? description, string? location, CancellationToken cancellationToken)
    {
        await _db.Machines
            .Where(m => m.Id == machineId)
            .Set(m => m.Name, name)
            .Set(m => m.Description, description)
            .Set(m => m.Location, location)
            .UpdateAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<List<long>> GetActiveMachineIdsForTenantAsync(int tenantId, CancellationToken cancellationToken)
    {
        List<long> ids = await _db.Machines
            .Where(m => m.TenantId == tenantId && m.IsDeleted == false)
            .Select(m => m.Id)
            .ToListAsync(cancellationToken);

        return ids;
    }

    /// <inheritdoc/>
    public async Task<Tenant?> GetTenantForMachineAsync(int tenantId, CancellationToken cancellationToken)
    {
        Tenant? tenant = await _db.Tenants
            .Where(t => t.Id == tenantId)
            .FirstOrDefaultAsync(cancellationToken);

        return tenant;
    }

    /// <inheritdoc/>
    public async Task<int> GetMachineCountAtDateAsync(int tenantId, DateTimeOffset targetDate, CancellationToken cancellationToken)
    {
        int count = await _db.Machines
            .Where(m => m.TenantId == tenantId
                && m.RegisteredOn <= targetDate
                && (m.IsDeleted == false || m.DeletedOn > targetDate))
            .CountAsync(cancellationToken);

        return count;
    }

    /// <inheritdoc/>
    public async Task<Dictionary<long, string>> GetMachineNameMapForTenantAsync(int tenantId, CancellationToken cancellationToken)
    {
        Dictionary<long, string> nameMap = await _db.Machines
            .Where(m => m.TenantId == tenantId && m.IsDeleted == false)
            .ToDictionaryAsync(m => m.Id, m => m.Name, cancellationToken);

        return nameMap;
    }

    /// <inheritdoc/>
    public async Task<List<Machine>> ListActiveMachinesForTenantAsync(int tenantId, CancellationToken cancellationToken)
    {
        List<Machine> machines = await _db.Machines
            .Where(m => m.TenantId == tenantId && m.IsDeleted == false)
            .ToListAsync(cancellationToken);

        return machines;
    }

    /// <inheritdoc/>
    public async Task<List<Machine>> QueryActiveMachinesAsync(
        int tenantId,
        string? search,
        OperatingSystems? osFilter,
        MachineTypes? typeFilter,
        string sortBy,
        string sortDir,
        int skip,
        int take,
        CancellationToken cancellationToken)
    {
        IQueryable<Machine> query = BuildFilteredMachineQuery(tenantId, search, osFilter, typeFilter);

        IOrderedQueryable<Machine> orderedQuery = sortBy?.ToLowerInvariant() switch
        {
            "type" => sortDir == "desc"
                ? query.OrderByDescending(m => m.MachineType)
                : query.OrderBy(m => m.MachineType),
            "registeredon" => sortDir == "desc"
                ? query.OrderByDescending(m => m.RegisteredOn)
                : query.OrderBy(m => m.RegisteredOn),
            _ => sortDir == "desc"
                ? query.OrderByDescending(m => m.Name)
                : query.OrderBy(m => m.Name),
        };

        List<Machine> machines = await orderedQuery
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);

        return machines;
    }

    /// <inheritdoc/>
    public async Task<int> CountActiveMachinesAsync(
        int tenantId,
        string? search,
        OperatingSystems? osFilter,
        MachineTypes? typeFilter,
        CancellationToken cancellationToken)
    {
        IQueryable<Machine> query = BuildFilteredMachineQuery(tenantId, search, osFilter, typeFilter);

        int count = await query.CountAsync(cancellationToken);

        return count;
    }

    private IQueryable<Machine> BuildFilteredMachineQuery(
        int tenantId,
        string? search,
        OperatingSystems? osFilter,
        MachineTypes? typeFilter)
    {
        IQueryable<Machine> query = _db.Machines
            .Where(m => m.TenantId == tenantId && m.IsDeleted == false);

        if (string.IsNullOrWhiteSpace(search) == false)
        {
            string searchLower = search.ToLowerInvariant();
            query = query.Where(m => m.Name.ToLower().Contains(searchLower));
        }

        if (osFilter.HasValue)
        {
            query = query.Where(m => m.OperatingSystem == osFilter.Value);
        }

        if (typeFilter.HasValue)
        {
            query = query.Where(m => m.MachineType == typeFilter.Value);
        }

        return query;
    }

    /// <inheritdoc/>
    public async Task<(List<Machine> Machines, int TotalCount)> SearchMachinesPagedAsync(int? tenantId, int skip, int take, CancellationToken cancellationToken)
    {
        IQueryable<Machine> query = _db.Machines;

        if (tenantId.HasValue)
        {
            query = query.Where(m => m.TenantId == tenantId.Value);
        }

        int totalCount = await query.CountAsync(cancellationToken);

        List<Machine> machines = await query
            .OrderBy(m => m.Id)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);

        return (machines, totalCount);
    }

    /// <inheritdoc/>
    public async Task<Dictionary<int, int>> GetMachineCountsByTenantsAsync(List<int> tenantIds, CancellationToken cancellationToken)
    {
        if (tenantIds.Count == 0)
        {
            return new Dictionary<int, int>();
        }

        List<Machine> machines = await _db.Machines
            .Where(m => tenantIds.Contains(m.TenantId) && (m.IsDeleted == false))
            .ToListAsync(cancellationToken);

        Dictionary<int, int> counts = machines
            .GroupBy(m => m.TenantId)
            .ToDictionary(g => g.Key, g => g.Count());

        return counts;
    }
}
