// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;

namespace Framlux.FleetManagement.Database.Repositories;

/// <summary>
/// Repository for machine registration and API key operations.
/// </summary>
public interface IMachineRepository
{
    /// <summary>
    /// Looks up an active (non-deleted) machine by serial number and system ID within a tenant.
    /// Returns null if no matching machine is found.
    /// </summary>
    /// <param name="serialNumber">The machine serial number (lowercase).</param>
    /// <param name="systemId">The machine system ID (lowercase).</param>
    /// <param name="tenantId">The tenant ID.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task<Machine?> GetMachineBySerialAndSystemIdAsync(string serialNumber, string systemId, int tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically marks a machine's API key as delivered if it has not already been delivered.
    /// Returns the number of rows updated (1 if successful, 0 if already delivered).
    /// </summary>
    /// <param name="machineId">The machine ID.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task<int> MarkKeyDeliveredAsync(long machineId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks a machine's API key as delivered unconditionally.
    /// </summary>
    /// <param name="machineId">The machine ID.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task SetKeyDeliveredAsync(long machineId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if an active (non-deleted) machine exists based on the serial number, system ID, or asset tag number.
    /// </summary>
    Task<bool> DoesMachineExistAsync(string serialNumber, string systemId, string assetTag, int tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a new Machine with a hashed API key and returns the plaintext key.
    /// </summary>
    Task<(Machine? machine, string? plaintextApiKey)> CreateMachineWithKeyAsync(Machine machine, int? machineLimit = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates a new API key for an existing machine, replacing the old one.
    /// </summary>
    Task<string?> ReissueApiKeyAsync(long machineId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if an approved machine exists, and is active, with the given ID within a tenant.
    /// </summary>
    Task<Machine?> GetMachineAsync(long machineId, int tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a machine by its API key.
    /// </summary>
    Task<Machine?> GetMachineByApiKeyAsync(string apiKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Soft-deletes a machine within a tenant. Returns the number of rows updated.
    /// </summary>
    Task<int> SoftDeleteMachineAsync(long machineId, int tenantId, int userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the count of active (non-deleted) machines for a tenant.
    /// </summary>
    Task<int> GetActiveMachineCountAsync(int tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets an active (non-deleted) machine by its ID and tenant.
    /// </summary>
    Task<Machine?> GetActiveMachineByIdAsync(long machineId, int tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates a machine's user-editable fields (name, description, location).
    /// </summary>
    Task UpdateMachineFieldsAsync(long machineId, string name, string? description, string? location, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the IDs of all active (non-deleted) machines for a tenant.
    /// </summary>
    Task<List<long>> GetActiveMachineIdsForTenantAsync(int tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the tenant associated with a machine's tenant ID.
    /// </summary>
    Task<Tenant?> GetTenantForMachineAsync(int tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the count of active machines for a tenant at a specific date,
    /// including machines registered before that date that were not yet deleted.
    /// </summary>
    Task<int> GetMachineCountAtDateAsync(int tenantId, DateTimeOffset targetDate, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a dictionary mapping machine IDs to machine names for active machines in a tenant.
    /// </summary>
    Task<Dictionary<long, string>> GetMachineNameMapForTenantAsync(int tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists active machines for a tenant as a queryable for complex filtering and pagination.
    /// Returns a base query (non-deleted, matching tenant) that callers can extend.
    /// </summary>
    Task<List<Machine>> ListActiveMachinesForTenantAsync(int tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a paginated, filtered, sorted list of active machines for a tenant.
    /// </summary>
    /// <param name="tenantId">The tenant to query.</param>
    /// <param name="search">Optional name search filter (case-insensitive contains).</param>
    /// <param name="osFilter">Optional operating system filter.</param>
    /// <param name="typeFilter">Optional machine type filter.</param>
    /// <param name="sortBy">Sort field: "name", "type", or "registeredon".</param>
    /// <param name="sortDir">Sort direction: "asc" or "desc".</param>
    /// <param name="skip">Number of items to skip.</param>
    /// <param name="take">Number of items to take.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task<List<Machine>> QueryActiveMachinesAsync(
        int tenantId,
        string? search,
        OperatingSystems? osFilter,
        MachineTypes? typeFilter,
        string sortBy,
        string sortDir,
        int skip,
        int take,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the count of active machines for a tenant matching the specified filters.
    /// </summary>
    /// <param name="tenantId">The tenant to query.</param>
    /// <param name="search">Optional name search filter (case-insensitive contains).</param>
    /// <param name="osFilter">Optional operating system filter.</param>
    /// <param name="typeFilter">Optional machine type filter.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task<int> CountActiveMachinesAsync(
        int tenantId,
        string? search,
        OperatingSystems? osFilter,
        MachineTypes? typeFilter,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a paginated list of machines with optional tenant filter, ordered by ID.
    /// </summary>
    /// <param name="tenantId">Optional tenant ID filter.</param>
    /// <param name="skip">Number of records to skip.</param>
    /// <param name="take">Number of records to take.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task<(List<Machine> Machines, int TotalCount)> SearchMachinesPagedAsync(int? tenantId, int skip, int take, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the count of active (non-deleted) machines per tenant for the given tenant IDs.
    /// </summary>
    /// <param name="tenantIds">The tenant IDs to query.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task<Dictionary<int, int>> GetMachineCountsByTenantsAsync(List<int> tenantIds, CancellationToken cancellationToken = default);
}
