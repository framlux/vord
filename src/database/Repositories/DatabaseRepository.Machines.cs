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

        // Data is normalized to lowercase at write time, so callers must
        // pass pre-lowered values for SerialNumber and SystemId.
        // A DB fault must propagate rather than be swallowed into a false negative:
        // returning "machine does not exist" on a transient error would let a duplicate
        // or over-limit registration proceed. Let the exception abort the registration.
        _logger.LogDebug("Searching for active Machine with Serial Number {SerialNumber}, System ID {SystemId}, or Asset Tag {AssetTag} in tenant {TenantId}", serialNumber, systemId, assetTag, tenantId);
        IQueryable<Machine> query = _db.Machines.Where(m =>
            (m.TenantId == tenantId) &&
            (m.IsDeleted == false) &&
            ((m.SerialNumber == serialNumber) ||
            (m.SystemId == systemId) ||
            (string.IsNullOrEmpty(assetTag) == false && (m.AssetTagNumber == assetTag))));

        bool exists = await query.AnyAsync(cancellationToken);
        _logger.LogInformation("Active Machine query for Serial Number {SerialNumber}, System ID {SystemId}, or Asset Tag {AssetTag}: {FoundResult}", serialNumber, systemId, assetTag, exists);

        return exists;
    }

    /// <inheritdoc/>
    public async Task<(Machine? machine, string? plaintextApiKey)> CreateMachineWithKeyAsync(Machine machine, long registrationTokenId, DateTimeOffset now, int? machineLimit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(machine);

        string plaintextApiKey = RandomNumberGenerator.GetHexString(64, true);
        string apiKeyHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(plaintextApiKey)));
        machine.ApiKeyHash = apiKeyHash;

        const int maxAttempts = 3;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                _logger.LogInformation("Creating Machine with Serial Number {SerialNumber}", machine.SerialNumber);
                // Use Serializable isolation to prevent concurrent registrations
                // from both passing the machine limit check before either inserts.
                using DataConnectionTransaction txn = await _db.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

                if (machineLimit.HasValue)
                {
                    int activeMachineCount = await _db.Machines
                        .Where(m => (m.TenantId == machine.TenantId) && (m.IsDeleted == false))
                        .CountAsync(cancellationToken);

                    if (activeMachineCount >= machineLimit.Value)
                    {
                        _logger.LogWarning("Tenant {TenantId} at machine limit ({Limit}) — rejecting machine creation", machine.TenantId, machineLimit.Value);

                        return (null, null);
                    }
                }

                // Insert the machine first so its id is available for the token-consume stamp.
                machine.Id = await _db.InsertWithInt64IdentityAsync(machine, token: cancellationToken);

                // Atomically consume the single-use registration token. The predicate re-asserts
                // every usability condition (available, not revoked, not expired) so that two
                // concurrent registrations on the same token cannot both produce a non-zero row
                // count under Serializable isolation. A zero affected-row count means the token was
                // consumed, revoked, or expired since the pre-check — reject and roll back by
                // returning before CommitAsync (disposing the transaction rolls back the insert).
                int tokenRows = await _db.RegistrationTokens
                    .Where(t => (t.Id == registrationTokenId) &&
                                (t.ConsumedAt == null) &&
                                (t.IsRevoked == false) &&
                                (t.ExpiresAt > now))
                    .Set(t => t.ConsumedAt, now)
                    .Set(t => t.ConsumedByMachineId, machine.Id)
                    .UpdateAsync(cancellationToken);

                if (tokenRows == 0)
                {
                    _logger.LogWarning("Registration token {TokenId} is already consumed, revoked, or expired — rejecting machine creation", registrationTokenId);

                    return (null, null);
                }

                await txn.CommitAsync(cancellationToken);
                _logger.LogInformation("Created Machine with Serial Number {SerialNumber}, ID {MachineId}", machine.SerialNumber, machine.Id);

                return (machine, plaintextApiKey);
            }
            catch (Exception ex) when (IsSerializationFailure(ex) && (attempt < maxAttempts))
            {
                // A committed conflicting transaction aborted this one; retry immediately against fresh state.
                _logger.LogWarning("Serializable conflict creating Machine with Serial Number {SerialNumber} (attempt {Attempt}/{Max}) — retrying", machine.SerialNumber, attempt, maxAttempts);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create Machine with Serial Number {SerialNumber}", machine.SerialNumber);
                throw;
            }
        }
    }

    /// <inheritdoc/>
    public async Task<(string? plaintextApiKey, string? oldKeyHash)> ReissueApiKeyAsync(long machineId, CancellationToken cancellationToken)
    {
        string plaintextApiKey = RandomNumberGenerator.GetHexString(64, true);
        string apiKeyHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(plaintextApiKey)));

        try
        {
            _logger.LogInformation("Re-issuing API key for Machine {MachineId}", machineId);

            // Capture the hash being replaced before the UPDATE overwrites it so the caller can
            // invalidate the old key's auth cache entry. Read against the same active-machine
            // predicate the UPDATE uses.
            // Known, accepted best-effort limitation (mirrors SoftDeleteMachineAsync): this SELECT and
            // the UPDATE below are two separate statements, so a concurrent soft-delete landing between
            // them makes the UPDATE affect zero rows — the updated == 0 guard then returns before any
            // invalidation, so a stale hash is never acted on. This is safe because cache invalidation
            // is best-effort and the auth-cache TTL bounds any residual exposure window.
            string? oldKeyHash = await _db.Machines
                .Where(m => (m.Id == machineId) && (m.IsDeleted == false))
                .Select(m => m.ApiKeyHash)
                .FirstOrDefaultAsync(cancellationToken);

            int updated = await _db.Machines
                .Where(m => (m.Id == machineId) && (m.IsDeleted == false))
                .Set(m => m.ApiKeyHash, apiKeyHash)
                .Set(m => m.KeyDeliveredAt, (DateTimeOffset?)null)
                .UpdateAsync(cancellationToken);

            if (updated == 0)
            {
                _logger.LogWarning("Re-issue failed: Machine {MachineId} not found or deleted", machineId);

                return (null, null);
            }

            _logger.LogInformation("API key re-issued for Machine {MachineId}", machineId);

            return (plaintextApiKey, oldKeyHash);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to re-issue API key for Machine {MachineId}", machineId);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<Machine?> GetMachineAsync(long machineId, int tenantId, CancellationToken cancellationToken)
    {
        // A DB fault must propagate rather than be swallowed into a null (treated as
        // "not found") result by callers. Let the exception surface.
        _logger.LogInformation("Checking for Machine with ID {MachineId} in tenant {TenantId}", machineId, tenantId);
        Machine? machine = await _db.Machines
                                  .Where(m => (m.Id == machineId) &&
                                              (m.TenantId == tenantId) &&
                                              (m.IsDeleted == false))
                                  .SingleOrDefaultAsync(cancellationToken);
        _logger.LogInformation("Found Machine with ID {MachineId}: {Found}", machineId, machine is not null);

        return machine;
    }

    /// <inheritdoc/>
    public async Task<Machine?> GetMachineByApiKeyAsync(string apiKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        string apiKeyHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey)));

        // A DB fault must propagate rather than be swallowed into a null result: returning
        // "no machine" on a transient error would reject telemetry from a valid machine.
        _logger.LogInformation("Searching for Machine with API Key hash");
        Machine? machine = await _db.Machines.Where(m => (m.ApiKeyHash == apiKeyHash) &&
                                                      (m.IsDeleted == false))
                                          .SingleOrDefaultAsync(cancellationToken);
        _logger.LogInformation("Found Machine with API Key hash: {Found}", machine is not null);

        return machine;
    }

    /// <inheritdoc/>
    public async Task<string?> SoftDeleteMachineAsync(long machineId, int tenantId, int userId, CancellationToken cancellationToken)
    {
        // Capture the hash before the soft-delete flips IsDeleted so the caller can invalidate the
        // deleted key's auth cache entry. The same active-machine predicate guards both the read
        // and the UPDATE, so a null here means nothing will be updated.
        // Known, accepted best-effort limitation: this SELECT and the UPDATE below are two separate
        // statements, so a concurrent reissue landing between them could leave the post-reissue hash
        // cached until its TTL (we only return the pre-reissue hash to invalidate). This is acceptable
        // because cache invalidation is best-effort and the auth-cache TTL bounds the exposure window.
        string? deletedKeyHash = await _db.Machines
            .Where(m => (m.Id == machineId) && (m.TenantId == tenantId) && (m.IsDeleted == false))
            .Select(m => m.ApiKeyHash)
            .FirstOrDefaultAsync(cancellationToken);

        int updated = await _db.Machines
            .Where(m => (m.Id == machineId) && (m.TenantId == tenantId) && (m.IsDeleted == false))
            .Set(m => m.IsDeleted, true)
            .Set(m => m.DeletedOn, DateTimeOffset.UtcNow)
            .Set(m => m.DeletedByUserId, userId)
            .UpdateAsync(cancellationToken);

        // Remove the machine's state summary so it no longer appears in the tenant's summary list.
        // A deleted machine can no longer authenticate, so the projection cannot recreate the row.
        if (updated > 0)
        {
            await _db.MachineStateSummaries
                .Where(s => s.MachineId == machineId)
                .DeleteAsync(cancellationToken);

            return deletedKeyHash;
        }

        return null;
    }

    /// <inheritdoc/>
    public async Task<int> GetActiveMachineCountAsync(int tenantId, CancellationToken cancellationToken)
    {
        int count = await _db.Machines
            .Where(m => (m.TenantId == tenantId) && (m.IsDeleted == false))
            .CountAsync(cancellationToken);

        return count;
    }

    /// <inheritdoc/>
    public async Task<Machine?> GetActiveMachineByIdAsync(long machineId, int tenantId, CancellationToken cancellationToken)
    {
        Machine? machine = await _db.Machines
            .FirstOrDefaultAsync(m => (m.Id == machineId) && (m.TenantId == tenantId) && (m.IsDeleted == false), cancellationToken);

        return machine;
    }

    /// <inheritdoc/>
    public async Task<bool> UpdateMachineFieldsAsync(long machineId, int tenantId, string name, string? description, string? location, CancellationToken cancellationToken)
    {
        int affected = await _db.Machines
            .Where(m => (m.Id == machineId) && (m.TenantId == tenantId) && (m.IsDeleted == false))
            .Set(m => m.Name, name)
            .Set(m => m.Description, description)
            .Set(m => m.Location, location)
            .UpdateAsync(cancellationToken);

        return affected > 0;
    }

    /// <inheritdoc/>
    public async Task<List<long>> GetActiveMachineIdsForTenantAsync(int tenantId, CancellationToken cancellationToken)
    {
        List<long> ids = await _db.Machines
            .Where(m => (m.TenantId == tenantId) && (m.IsDeleted == false))
            .Select(m => m.Id)
            .ToListAsync(cancellationToken);

        return ids;
    }

    /// <inheritdoc/>
    public async Task<int> GetMachineCountAtDateAsync(int tenantId, DateTimeOffset targetDate, CancellationToken cancellationToken)
    {
        int count = await _db.Machines
            .Where(m => (m.TenantId == tenantId) &&
                (m.RegisteredOn <= targetDate) &&
                ((m.IsDeleted == false) || (m.DeletedOn > targetDate)))
            .CountAsync(cancellationToken);

        return count;
    }

    /// <inheritdoc/>
    public async Task<Dictionary<long, string>> GetMachineNameMapForTenantAsync(int tenantId, CancellationToken cancellationToken)
    {
        Dictionary<long, string> nameMap = await _db.Machines
            .Where(m => (m.TenantId == tenantId) && (m.IsDeleted == false))
            .ToDictionaryAsync(m => m.Id, m => m.Name, cancellationToken);

        return nameMap;
    }

    /// <inheritdoc/>
    public async Task<List<Machine>> ListActiveMachinesForTenantAsync(int tenantId, CancellationToken cancellationToken)
    {
        List<Machine> machines = await _db.Machines
            .Where(m => (m.TenantId == tenantId) && (m.IsDeleted == false))
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
            .Where(m => (m.TenantId == tenantId) && (m.IsDeleted == false));

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

        List<TenantMachineCount> grouped = await _db.Machines
            .Where(m => tenantIds.Contains(m.TenantId) && (m.IsDeleted == false))
            .GroupBy(m => m.TenantId)
            .Select(g => new TenantMachineCount { TenantId = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        Dictionary<int, int> counts = grouped.ToDictionary(x => x.TenantId, x => x.Count);

        return counts;
    }

    /// <inheritdoc/>
    public async Task<List<long>> GetActiveMachineIdsForTenantAsync(int tenantId, IReadOnlyList<long> machineIds, CancellationToken cancellationToken)
    {
        if (machineIds.Count == 0)
        {
            return new List<long>();
        }

        List<long> activeMachineIds = await _db.Machines
            .Where(m => (m.TenantId == tenantId) &&
                        (m.IsDeleted == false) &&
                        machineIds.Contains(m.Id))
            .Select(m => m.Id)
            .ToListAsync(cancellationToken);

        return activeMachineIds;
    }

    /// <inheritdoc/>
    public async Task<Dictionary<long, string>> GetMachineNamesAsync(IReadOnlyList<long> machineIds, CancellationToken cancellationToken)
    {
        if (machineIds.Count == 0)
        {
            return new Dictionary<long, string>();
        }

        Dictionary<long, string> nameMap = await _db.Machines
            .Where(m => machineIds.Contains(m.Id))
            .ToDictionaryAsync(m => m.Id, m => m.Name, cancellationToken);

        return nameMap;
    }
}
