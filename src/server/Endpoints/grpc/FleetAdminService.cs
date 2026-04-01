// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Server.Options;
using Framlux.FleetManagement.Server.Services.Handlers;
using Framlux.Vord.BillingGrpc;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.Extensions.Options;

namespace Framlux.FleetManagement.Server.Endpoints.Grpc;

/// <summary>
/// gRPC service that provides fleet administration data to the billing API admin panel.
/// </summary>
public sealed class FleetAdminService : FleetAdmin.FleetAdminBase
{
    private const int DefaultPageSize = 50;
    private const int MaxPageSize = 100;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly InternalApiOptions _internalApiOptions;
    private readonly ILogger<FleetAdminService> _logger;

    /// <summary>
    /// Creates a new instance of the <see cref="FleetAdminService"/> class.
    /// </summary>
    public FleetAdminService(
        IServiceScopeFactory scopeFactory,
        IOptions<InternalApiOptions> internalApiOptions,
        ILogger<FleetAdminService> logger)
    {
        _scopeFactory = scopeFactory;
        _internalApiOptions = internalApiOptions.Value;
        _logger = logger;
    }

    /// <summary>
    /// Lists user accounts with optional search and pagination.
    /// </summary>
    public override async Task<ListUsersResponse> ListUsers(
        ListUsersRequest request, ServerCallContext context)
    {
        ValidateInternalKey(context);

        using IServiceScope scope = _scopeFactory.CreateScope();
        DatabaseContext db = scope.ServiceProvider.GetRequiredService<DatabaseContext>();

        (int page, int pageSize) = SanitizePagination(request.Page, request.PageSize);

        IQueryable<UserAccount> query = db.UserAccounts;

        if (string.IsNullOrWhiteSpace(request.Search) == false)
        {
            string search = request.Search.Trim();
            query = query.Where(u => u.Username.Contains(search));
        }

        int totalCount = await query.CountAsync(context.CancellationToken);

        List<UserAccount> users = await query
            .OrderBy(u => u.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(context.CancellationToken);

        List<int> userIds = users.Select(u => u.Id).ToList();

        List<UserTenantRole> allRoles = await db.UserTenantRoles
            .Where(r => userIds.Contains(r.UserId) && (r.IsActive == true))
            .ToListAsync(context.CancellationToken);

        List<int> distinctTenantIds = allRoles.Select(r => r.AssignedTenantId).Distinct().ToList();
        List<Tenant> roleTenants = await db.Tenants
            .Where(t => distinctTenantIds.Contains(t.Id))
            .ToListAsync(context.CancellationToken);

        Dictionary<int, string> tenantNames = roleTenants.ToDictionary(t => t.Id, t => t.Name);

        ListUsersResponse response = new ListUsersResponse
        {
            TotalCount = totalCount
        };

        foreach (UserAccount user in users)
        {
            List<UserTenantRole> userRoles = allRoles.Where(r => r.UserId == user.Id).ToList();
            response.Users.Add(MapToFleetUser(user, userRoles, tenantNames));
        }

        return response;
    }

    /// <summary>
    /// Lists tenants with optional search, pagination, and aggregate counts.
    /// </summary>
    public override async Task<ListTenantsResponse> ListTenants(
        ListTenantsRequest request, ServerCallContext context)
    {
        ValidateInternalKey(context);

        using IServiceScope scope = _scopeFactory.CreateScope();
        DatabaseContext db = scope.ServiceProvider.GetRequiredService<DatabaseContext>();

        (int page, int pageSize) = SanitizePagination(request.Page, request.PageSize);

        IQueryable<Tenant> query = db.Tenants;

        if (string.IsNullOrWhiteSpace(request.Search) == false)
        {
            string search = request.Search.Trim();
            query = query.Where(t => t.Name.Contains(search));
        }

        int totalCount = await query.CountAsync(context.CancellationToken);

        List<Tenant> tenants = await query
            .OrderBy(t => t.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(context.CancellationToken);

        List<int> tenantIds = tenants.Select(t => t.Id).ToList();

        // Batch-load machine counts per tenant
        List<Machine> activeMachines = await db.Machines
            .Where(m => tenantIds.Contains(m.TenantId) && (m.IsDeleted == false))
            .ToListAsync(context.CancellationToken);
        Dictionary<int, int> machineCounts = activeMachines
            .GroupBy(m => m.TenantId)
            .ToDictionary(g => g.Key, g => g.Count());

        // Batch-load user counts per tenant
        List<UserTenantRole> activeRoles = await db.UserTenantRoles
            .Where(r => tenantIds.Contains(r.AssignedTenantId) && (r.IsActive == true))
            .ToListAsync(context.CancellationToken);
        Dictionary<int, int> userCounts = activeRoles
            .GroupBy(r => r.AssignedTenantId)
            .ToDictionary(g => g.Key, g => g.Count());

        // Batch-load subscriptions
        List<TenantSubscription> subscriptionList = await db.TenantSubscriptions
            .Where(s => tenantIds.Contains(s.TenantId))
            .ToListAsync(context.CancellationToken);
        Dictionary<int, TenantSubscription> subscriptions = subscriptionList
            .ToDictionary(s => s.TenantId);

        ListTenantsResponse response = new ListTenantsResponse
        {
            TotalCount = totalCount
        };

        foreach (Tenant tenant in tenants)
        {
            machineCounts.TryGetValue(tenant.Id, out int machineCount);
            userCounts.TryGetValue(tenant.Id, out int userCount);
            subscriptions.TryGetValue(tenant.Id, out TenantSubscription? subscription);

            response.Tenants.Add(MapToFleetTenant(tenant, machineCount, userCount, subscription));
        }

        return response;
    }

    /// <summary>
    /// Gets detailed information about a specific tenant including users, machines, and subscription.
    /// </summary>
    public override async Task<GetTenantDetailResponse> GetTenantDetail(
        GetTenantDetailRequest request, ServerCallContext context)
    {
        ValidateInternalKey(context);

        using IServiceScope scope = _scopeFactory.CreateScope();
        DatabaseContext db = scope.ServiceProvider.GetRequiredService<DatabaseContext>();

        Tenant tenant = await ResolveTenantByExternalIdAsync(
            db, request.TenantExternalId, context.CancellationToken);

        // Load users via tenant roles
        List<UserTenantRole> roles = await db.UserTenantRoles
            .Where(r => (r.AssignedTenantId == tenant.Id) && (r.IsActive == true))
            .ToListAsync(context.CancellationToken);

        List<int> userIds = roles.Select(r => r.UserId).Distinct().ToList();
        List<UserAccount> users = await db.UserAccounts
            .Where(u => userIds.Contains(u.Id))
            .ToListAsync(context.CancellationToken);

        Dictionary<int, string> tenantNameMap = new Dictionary<int, string>
        {
            { tenant.Id, tenant.Name }
        };

        // Load machines
        List<Machine> machines = await db.Machines
            .Where(m => m.TenantId == tenant.Id)
            .OrderBy(m => m.Id)
            .ToListAsync(context.CancellationToken);

        // Load subscription
        TenantSubscription? subscription = await db.TenantSubscriptions
            .Where(s => s.TenantId == tenant.Id)
            .FirstOrDefaultAsync(context.CancellationToken);

        int machineCount = machines.Count(m => m.IsDeleted == false);
        int userCount = roles.Count;

        GetTenantDetailResponse response = new GetTenantDetailResponse
        {
            Tenant = MapToFleetTenant(tenant, machineCount, userCount, subscription)
        };

        foreach (UserAccount user in users)
        {
            List<UserTenantRole> userRoles = roles.Where(r => r.UserId == user.Id).ToList();
            response.Users.Add(MapToFleetUser(user, userRoles, tenantNameMap));
        }

        foreach (Machine machine in machines)
        {
            response.Machines.Add(MapToFleetMachine(machine, tenant.Name));
        }

        return response;
    }

    /// <summary>
    /// Lists machines with optional tenant filter and pagination.
    /// </summary>
    public override async Task<ListMachinesResponse> ListMachines(
        ListMachinesRequest request, ServerCallContext context)
    {
        ValidateInternalKey(context);

        using IServiceScope scope = _scopeFactory.CreateScope();
        DatabaseContext db = scope.ServiceProvider.GetRequiredService<DatabaseContext>();

        (int page, int pageSize) = SanitizePagination(request.Page, request.PageSize);

        IQueryable<Machine> query = db.Machines;

        // Optionally filter by tenant
        if (string.IsNullOrWhiteSpace(request.TenantExternalId) == false)
        {
            Tenant tenant = await ResolveTenantByExternalIdAsync(
                db, request.TenantExternalId, context.CancellationToken);
            query = query.Where(m => m.TenantId == tenant.Id);
        }

        int totalCount = await query.CountAsync(context.CancellationToken);

        List<Machine> machines = await query
            .OrderBy(m => m.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(context.CancellationToken);

        // Batch-load tenant names
        List<int> tenantIds = machines.Select(m => m.TenantId).Distinct().ToList();
        List<Tenant> tenantList = await db.Tenants
            .Where(t => tenantIds.Contains(t.Id))
            .ToListAsync(context.CancellationToken);
        Dictionary<int, string> tenantNames = tenantList.ToDictionary(t => t.Id, t => t.Name);

        ListMachinesResponse response = new ListMachinesResponse
        {
            TotalCount = totalCount
        };

        foreach (Machine machine in machines)
        {
            tenantNames.TryGetValue(machine.TenantId, out string? tenantName);
            response.Machines.Add(MapToFleetMachine(machine, tenantName ?? string.Empty));
        }

        return response;
    }

    /// <summary>
    /// Lists audit log entries with optional tenant filter and pagination.
    /// </summary>
    public override async Task<ListAuditLogEntriesResponse> ListAuditLogEntries(
        ListAuditLogEntriesRequest request, ServerCallContext context)
    {
        ValidateInternalKey(context);

        using IServiceScope scope = _scopeFactory.CreateScope();
        DatabaseContext db = scope.ServiceProvider.GetRequiredService<DatabaseContext>();

        (int page, int pageSize) = SanitizePagination(request.Page, request.PageSize);

        IQueryable<AuditLogEntry> query = db.AuditLog;

        // Optionally filter by tenant
        if (string.IsNullOrWhiteSpace(request.TenantExternalId) == false)
        {
            Tenant tenant = await ResolveTenantByExternalIdAsync(
                db, request.TenantExternalId, context.CancellationToken);
            query = query.Where(e => e.TenantId == tenant.Id);
        }

        int totalCount = await query.CountAsync(context.CancellationToken);

        List<AuditLogEntry> entries = await query
            .OrderByDescending(e => e.Timestamp)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(context.CancellationToken);

        // Batch-load usernames
        List<int> userIds = entries
            .Where(e => e.UserId.HasValue)
            .Select(e => e.UserId!.Value)
            .Distinct()
            .ToList();

        List<UserAccount> userList = await db.UserAccounts
            .Where(u => userIds.Contains(u.Id))
            .ToListAsync(context.CancellationToken);
        Dictionary<int, string> usernames = userList.ToDictionary(u => u.Id, u => u.Username);

        // Batch-load tenant names
        List<int> tenantIds = entries
            .Where(e => e.TenantId.HasValue)
            .Select(e => e.TenantId!.Value)
            .Distinct()
            .ToList();

        List<Tenant> tenantList = await db.Tenants
            .Where(t => tenantIds.Contains(t.Id))
            .ToListAsync(context.CancellationToken);
        Dictionary<int, string> tenantNames = tenantList.ToDictionary(t => t.Id, t => t.Name);

        ListAuditLogEntriesResponse response = new ListAuditLogEntriesResponse
        {
            TotalCount = totalCount
        };

        foreach (AuditLogEntry entry in entries)
        {
            string? username = null;
            if (entry.UserId.HasValue)
            {
                usernames.TryGetValue(entry.UserId.Value, out username);
            }

            string? tenantName = null;
            if (entry.TenantId.HasValue)
            {
                tenantNames.TryGetValue(entry.TenantId.Value, out tenantName);
            }

            response.Entries.Add(MapToFleetAuditEntry(entry, username, tenantName));
        }

        return response;
    }

    /// <summary>
    /// Returns all server configuration settings.
    /// </summary>
    public override async Task<GetServerSettingsResponse> GetServerSettings(
        GetServerSettingsRequest request, ServerCallContext context)
    {
        ValidateInternalKey(context);

        using IServiceScope scope = _scopeFactory.CreateScope();
        DatabaseContext db = scope.ServiceProvider.GetRequiredService<DatabaseContext>();

        List<ServerConfigurationSettings> settings = await db.ServerConfigurationSettings
            .OrderBy(s => s.Key)
            .ToListAsync(context.CancellationToken);

        GetServerSettingsResponse response = new GetServerSettingsResponse();

        foreach (ServerConfigurationSettings setting in settings)
        {
            AdminHandler.SettingBounds.TryGetValue(setting.Key, out (int Min, int Max) bounds);

            response.Settings.Add(new ServerSetting
            {
                Key = (int)setting.Key,
                KeyName = setting.Key.ToString(),
                Value = setting.Value,
                Version = setting.Version,
                Description = AdminHandler.SettingDescriptions.GetValueOrDefault(setting.Key, string.Empty),
                Min = bounds.Min,
                Max = bounds.Max,
            });
        }

        return response;
    }

    /// <summary>
    /// Updates a single server configuration setting by key.
    /// </summary>
    public override async Task<UpdateServerSettingResponse> UpdateServerSetting(
        UpdateServerSettingRequest request, ServerCallContext context)
    {
        ValidateInternalKey(context);

        if (System.Enum.IsDefined(typeof(ServerConfigurationSettingKeys), request.Key) == false ||
            request.Key == (int)ServerConfigurationSettingKeys.None)
        {
            throw new RpcException(new Status(
                StatusCode.InvalidArgument,
                $"Invalid server setting key: {request.Key}"));
        }

        ServerConfigurationSettingKeys key = (ServerConfigurationSettingKeys)request.Key;

        using IServiceScope scope = _scopeFactory.CreateScope();
        DatabaseContext db = scope.ServiceProvider.GetRequiredService<DatabaseContext>();

        int updated = await db.ServerConfigurationSettings
            .Where(s => s.Key == key)
            .Set(s => s.Value, request.Value)
            .Set(s => s.Version, s => s.Version + 1)
            .UpdateAsync(context.CancellationToken);

        if (updated == 0)
        {
            return new UpdateServerSettingResponse
            {
                Success = false,
                Message = $"Setting with key '{key}' not found"
            };
        }

        _logger.LogInformation(
            "FleetAdmin: server setting {Key} updated to '{Value}'", key, request.Value);

        return new UpdateServerSettingResponse
        {
            Success = true,
            Message = "OK"
        };
    }

    /// <summary>
    /// Updates the subscription fields for a tenant.
    /// </summary>
    public override async Task<UpdateTenantSubscriptionResponse> UpdateTenantSubscription(
        UpdateTenantSubscriptionRequest request, ServerCallContext context)
    {
        ValidateInternalKey(context);

        if (System.Enum.TryParse<SubscriptionTier>(request.Tier, ignoreCase: true, out SubscriptionTier tier) == false)
        {
            throw new RpcException(new Status(
                StatusCode.InvalidArgument,
                $"Invalid subscription tier: '{request.Tier}'"));
        }

        if (System.Enum.TryParse<SubscriptionStatus>(request.Status, ignoreCase: true, out SubscriptionStatus status) == false)
        {
            throw new RpcException(new Status(
                StatusCode.InvalidArgument,
                $"Invalid subscription status: '{request.Status}'"));
        }

        using IServiceScope scope = _scopeFactory.CreateScope();
        DatabaseContext db = scope.ServiceProvider.GetRequiredService<DatabaseContext>();

        Tenant tenant = await ResolveTenantByExternalIdAsync(
            db, request.TenantExternalId, context.CancellationToken);

        int updated = await db.TenantSubscriptions
            .Where(s => s.TenantId == tenant.Id)
            .Set(s => s.Tier, tier)
            .Set(s => s.Status, status)
            .Set(s => s.MachineLimit, request.MachineLimit > 0 ? request.MachineLimit : (int?)null)
            .Set(s => s.RetentionDays, request.RetentionDays)
            .Set(s => s.UpdatedAt, DateTimeOffset.UtcNow)
            .UpdateAsync(context.CancellationToken);

        if (updated == 0)
        {
            return new UpdateTenantSubscriptionResponse
            {
                Success = false,
                Message = $"No subscription found for tenant '{request.TenantExternalId}'"
            };
        }

        _logger.LogInformation(
            "FleetAdmin: tenant {TenantId} subscription updated to tier={Tier}, status={Status}",
            tenant.Id, tier, status);

        return new UpdateTenantSubscriptionResponse
        {
            Success = true,
            Message = "OK"
        };
    }

    /// <summary>
    /// Creates or updates the OIDC configuration for a tenant.
    /// </summary>
    public override async Task<ConfigureTenantOidcResponse> ConfigureTenantOidc(
        ConfigureTenantOidcRequest request, ServerCallContext context)
    {
        ValidateInternalKey(context);

        using IServiceScope scope = _scopeFactory.CreateScope();
        DatabaseContext db = scope.ServiceProvider.GetRequiredService<DatabaseContext>();

        Tenant tenant = await ResolveTenantByExternalIdAsync(
            db, request.TenantExternalId, context.CancellationToken);

        TenantOidcConfiguration? existing = await db.TenantOidcConfigurations
            .Where(c => c.TenantId == tenant.Id)
            .FirstOrDefaultAsync(context.CancellationToken);

        DateTimeOffset now = DateTimeOffset.UtcNow;

        if (existing is not null)
        {
            await db.TenantOidcConfigurations
                .Where(c => c.Id == existing.Id)
                .Set(c => c.Authority, request.Authority)
                .Set(c => c.ClientId, request.ClientId)
                .Set(c => c.ClientSecret, request.ClientSecret)
                .Set(c => c.MetadataAddress, string.IsNullOrWhiteSpace(request.MetadataAddress) ? null : request.MetadataAddress)
                .Set(c => c.EmailDomain, request.EmailDomain)
                .Set(c => c.IsEnabled, request.IsEnabled)
                .Set(c => c.UpdatedAt, now)
                .UpdateAsync(context.CancellationToken);

            _logger.LogInformation(
                "FleetAdmin: updated OIDC configuration for tenant {TenantId}", tenant.Id);
        }
        else
        {
            TenantOidcConfiguration config = new TenantOidcConfiguration
            {
                TenantId = tenant.Id,
                Authority = request.Authority,
                ClientId = request.ClientId,
                ClientSecret = request.ClientSecret,
                MetadataAddress = string.IsNullOrWhiteSpace(request.MetadataAddress) ? null : request.MetadataAddress,
                EmailDomain = request.EmailDomain,
                IsEnabled = request.IsEnabled,
                CreatedAt = now,
                UpdatedAt = now
            };

            await db.InsertAsync(config, token: context.CancellationToken);

            _logger.LogInformation(
                "FleetAdmin: created OIDC configuration for tenant {TenantId}", tenant.Id);
        }

        return new ConfigureTenantOidcResponse
        {
            Success = true,
            Message = "OK"
        };
    }

    private void ValidateInternalKey(ServerCallContext context)
    {
        string configuredKey = _internalApiOptions.Key;
        if (string.IsNullOrEmpty(configuredKey))
        {
            throw new RpcException(new Status(StatusCode.Unavailable, "Internal API is not configured"));
        }

        Metadata.Entry? keyEntry = context.RequestHeaders.Get("x-internal-key");
        string providedKey = keyEntry?.Value ?? string.Empty;

        if (string.Equals(providedKey, configuredKey, StringComparison.Ordinal) == false)
        {
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Unauthorized"));
        }
    }

    internal static (int Page, int PageSize) SanitizePagination(int page, int pageSize)
    {
        if (page < 1)
        {
            page = 1;
        }

        if (pageSize < 1)
        {
            pageSize = DefaultPageSize;
        }
        else if (pageSize > MaxPageSize)
        {
            pageSize = MaxPageSize;
        }

        return (page, pageSize);
    }

    internal static FleetUser MapToFleetUser(
        UserAccount user,
        List<UserTenantRole> roles,
        Dictionary<int, string> tenantNames)
    {
        FleetUser fleetUser = new FleetUser
        {
            Id = user.Id,
            ExternalId = user.ExternalId,
            Username = user.Username,
            IsActive = user.IsActive,
            IsGlobalAdmin = user.IsGlobalAdmin,
            AuthProvider = user.AuthProvider.ToString(),
            CreatedAt = Timestamp.FromDateTimeOffset(user.CreatedAt)
        };

        foreach (UserTenantRole role in roles)
        {
            tenantNames.TryGetValue(role.AssignedTenantId, out string? tenantName);
            fleetUser.TenantRoles.Add(new FleetUserTenantRole
            {
                TenantId = role.AssignedTenantId,
                TenantName = tenantName ?? string.Empty,
                Role = role.Role.ToString()
            });
        }

        return fleetUser;
    }

    internal static FleetTenant MapToFleetTenant(
        Tenant tenant,
        int machineCount,
        int userCount,
        TenantSubscription? subscription)
    {
        FleetTenant fleetTenant = new FleetTenant
        {
            Id = tenant.Id,
            ExternalId = tenant.ExternalId,
            Name = tenant.Name,
            IsActive = tenant.IsActive,
            LogoUrl = tenant.LogoUrl,
            CreatedAt = Timestamp.FromDateTimeOffset(tenant.CreatedAt),
            MachineCount = machineCount,
            UserCount = userCount
        };

        if (subscription is not null)
        {
            fleetTenant.Subscription = MapSubscription(subscription);
        }

        return fleetTenant;
    }

    internal static FleetTenantSubscription MapSubscription(TenantSubscription subscription)
    {
        FleetTenantSubscription proto = new FleetTenantSubscription
        {
            Tier = subscription.Tier.ToString(),
            Status = subscription.Status.ToString(),
            MachineLimit = subscription.MachineLimit ?? 0,
            RetentionDays = subscription.RetentionDays,
            CancelAtPeriodEnd = subscription.CancelAtPeriodEnd
        };

        if (subscription.CurrentPeriodEnd.HasValue)
        {
            proto.CurrentPeriodEnd = Timestamp.FromDateTimeOffset(subscription.CurrentPeriodEnd.Value);
        }

        return proto;
    }

    internal static FleetMachine MapToFleetMachine(Machine machine, string tenantName)
    {
        return new FleetMachine
        {
            Id = machine.Id,
            Name = machine.Name,
            TenantId = machine.TenantId,
            TenantName = tenantName,
            IsDeleted = machine.IsDeleted,
            RegisteredOn = Timestamp.FromDateTimeOffset(machine.RegisteredOn)
        };
    }

    internal static FleetAuditEntry MapToFleetAuditEntry(
        AuditLogEntry entry,
        string? username,
        string? tenantName)
    {
        return new FleetAuditEntry
        {
            Id = entry.Id,
            TenantId = entry.TenantId ?? 0,
            UserId = entry.UserId ?? 0,
            Action = entry.Action.ToString(),
            ResourceType = entry.ResourceType.ToString(),
            Details = entry.Details ?? string.Empty,
            Timestamp = Timestamp.FromDateTimeOffset(entry.Timestamp),
            TenantName = tenantName ?? string.Empty,
            Username = username ?? string.Empty
        };
    }

    private static async Task<Tenant> ResolveTenantByExternalIdAsync(
        DatabaseContext db, string externalId, CancellationToken cancellationToken)
    {
        Tenant? tenant = await db.Tenants
            .Where(t => t.ExternalId == externalId)
            .FirstOrDefaultAsync(cancellationToken);

        if (tenant is null)
        {
            throw new RpcException(new Status(
                StatusCode.NotFound,
                $"Tenant not found for external ID: {externalId}"));
        }

        return tenant;
    }
}
