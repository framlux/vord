// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Services.Core.Billing;
using Framlux.FleetManagement.Services.Core.Handlers;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Framlux.FleetManagement.Services.Core.Security;
using Framlux.FleetManagement.Services.Core.ServerConfiguration;
using Framlux.Vord.BillingGrpc;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using StackExchange.Redis;

namespace Framlux.FleetManagement.Server.Endpoints.Grpc;

/// <summary>
/// gRPC service that provides fleet administration data to the billing API admin panel.
/// </summary>
public sealed class FleetAdminService : FleetAdmin.FleetAdminBase
{
    private const int DefaultPageSize = 50;
    private const int MaxPageSize = 100;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IInternalCallerAuthorizer _callerAuthorizer;
    private readonly IOidcSecretProtector _oidcSecretProtector;
    private readonly ILogger<FleetAdminService> _logger;
    private readonly IConnectionMultiplexer _redis;

    /// <summary>
    /// Creates a new instance of the <see cref="FleetAdminService"/> class.
    /// </summary>
    public FleetAdminService(
        IServiceScopeFactory scopeFactory,
        IInternalCallerAuthorizer callerAuthorizer,
        IOidcSecretProtector oidcSecretProtector,
        ILogger<FleetAdminService> logger,
        IConnectionMultiplexer redis)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(callerAuthorizer);
        ArgumentNullException.ThrowIfNull(oidcSecretProtector);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(redis);
        _scopeFactory = scopeFactory;
        _callerAuthorizer = callerAuthorizer;
        _oidcSecretProtector = oidcSecretProtector;
        _logger = logger;
        _redis = redis;
    }

    /// <summary>
    /// Lists user accounts with optional search and pagination.
    /// </summary>
    public override async Task<ListUsersResponse> ListUsers(
        ListUsersRequest request, ServerCallContext context)
    {
        AuthorizeInternalCaller(context);

        using IServiceScope scope = _scopeFactory.CreateScope();
        IUserRepository userRepo = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        ITenantRepository tenantRepo = scope.ServiceProvider.GetRequiredService<ITenantRepository>();

        (int page, int pageSize) = SanitizePagination(request.Page, request.PageSize);

        (List<UserAccount> users, int totalCount) = await userRepo.SearchUsersPagedAsync(
            request.Search, (page - 1) * pageSize, pageSize, context.CancellationToken);

        List<int> userIds = users.Select(u => u.Id).ToList();

        List<UserTenantRole> allRoles = await tenantRepo.GetActiveRolesForUsersAsync(userIds, context.CancellationToken);

        List<int> distinctTenantIds = allRoles.Select(r => r.AssignedTenantId).Distinct().ToList();
        List<Tenant> roleTenants = await tenantRepo.ListTenantsByIdsAsync(distinctTenantIds, context.CancellationToken);

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
        AuthorizeInternalCaller(context);

        using IServiceScope scope = _scopeFactory.CreateScope();
        ITenantRepository tenantRepo = scope.ServiceProvider.GetRequiredService<ITenantRepository>();
        IMachineRepository machineRepo = scope.ServiceProvider.GetRequiredService<IMachineRepository>();
        ISubscriptionRepository subscriptionRepo = scope.ServiceProvider.GetRequiredService<ISubscriptionRepository>();

        (int page, int pageSize) = SanitizePagination(request.Page, request.PageSize);

        (List<Tenant> tenants, int totalCount) = await tenantRepo.SearchTenantsPagedAsync(
            request.Search, (page - 1) * pageSize, pageSize, context.CancellationToken);

        List<int> tenantIds = tenants.Select(t => t.Id).ToList();

        // Batch-load machine counts per tenant
        Dictionary<int, int> machineCounts = await machineRepo.GetMachineCountsByTenantsAsync(tenantIds, context.CancellationToken);

        // Batch-load user counts per tenant
        List<UserTenantRole> activeRoles = await tenantRepo.GetActiveRolesForTenantsAsync(tenantIds, context.CancellationToken);
        Dictionary<int, int> userCounts = activeRoles
            .GroupBy(r => r.AssignedTenantId)
            .ToDictionary(g => g.Key, g => g.Count());

        // Batch-load subscriptions
        List<TenantSubscription> subscriptionList = await subscriptionRepo.GetSubscriptionsForTenantsAsync(tenantIds, context.CancellationToken);
        Dictionary<int, TenantSubscription> subscriptions = subscriptionList
            .ToDictionary(s => s.TenantId);

        // Batch-load tier limits so the admin panel shows correct values
        ITierFeatureLimitRepository tierLimitRepo = scope.ServiceProvider.GetRequiredService<ITierFeatureLimitRepository>();
        List<TierFeatureLimit> allLimits = await tierLimitRepo.GetAllLimitsAsync(context.CancellationToken);
        Dictionary<SubscriptionTier, TierFeatureLimit> tierLimitsMap = allLimits.ToDictionary(l => l.Tier);

        ListTenantsResponse response = new ListTenantsResponse
        {
            TotalCount = totalCount
        };

        foreach (Tenant tenant in tenants)
        {
            machineCounts.TryGetValue(tenant.Id, out int machineCount);
            userCounts.TryGetValue(tenant.Id, out int userCount);
            subscriptions.TryGetValue(tenant.Id, out TenantSubscription? subscription);

            TierFeatureLimit? tierLimits = null;
            if (subscription is not null)
            {
                tierLimitsMap.TryGetValue(subscription.Tier, out tierLimits);
            }

            response.Tenants.Add(MapToFleetTenant(tenant, machineCount, userCount, subscription, tierLimits));
        }

        return response;
    }

    /// <summary>
    /// Gets detailed information about a specific tenant including users, machines, and subscription.
    /// </summary>
    public override async Task<GetTenantDetailResponse> GetTenantDetail(
        GetTenantDetailRequest request, ServerCallContext context)
    {
        AuthorizeInternalCaller(context);

        using IServiceScope scope = _scopeFactory.CreateScope();
        ITenantRepository tenantRepo = scope.ServiceProvider.GetRequiredService<ITenantRepository>();
        IUserRepository userRepo = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        IMachineRepository machineRepo = scope.ServiceProvider.GetRequiredService<IMachineRepository>();
        ISubscriptionRepository subscriptionRepo = scope.ServiceProvider.GetRequiredService<ISubscriptionRepository>();

        Tenant tenant = await ResolveTenantByExternalIdAsync(
            tenantRepo, request.TenantExternalId, context.CancellationToken);

        // Load users via tenant roles
        List<UserTenantRole> roles = await tenantRepo.GetActiveRolesForTenantsAsync(new List<int> { tenant.Id }, context.CancellationToken);

        List<int> userIds = roles.Select(r => r.UserId).Distinct().ToList();
        List<UserAccount> users = await userRepo.GetUsersByIdsAsync(userIds, context.CancellationToken);

        Dictionary<int, string> tenantNameMap = new Dictionary<int, string>
        {
            { tenant.Id, tenant.Name }
        };

        // Load all machines (including deleted) for admin detail view
        (List<Machine> machines, int _) = await machineRepo.SearchMachinesPagedAsync(tenant.Id, 0, 10000, context.CancellationToken);

        // Load subscription
        TenantSubscription? subscription = await subscriptionRepo.GetSubscriptionForTenantAsync(tenant.Id, context.CancellationToken);

        // Load tier limits for the subscription
        ITierFeatureLimitRepository tierLimitRepo = scope.ServiceProvider.GetRequiredService<ITierFeatureLimitRepository>();
        TierFeatureLimit? tierLimits = null;
        if (subscription is not null)
        {
            tierLimits = await tierLimitRepo.GetLimitsForTierAsync(subscription.Tier, context.CancellationToken);
        }

        int machineCount = machines.Count(m => m.IsDeleted == false);
        int userCount = roles.Count;

        GetTenantDetailResponse response = new GetTenantDetailResponse
        {
            Tenant = MapToFleetTenant(tenant, machineCount, userCount, subscription, tierLimits)
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
        AuthorizeInternalCaller(context);

        using IServiceScope scope = _scopeFactory.CreateScope();
        ITenantRepository tenantRepo = scope.ServiceProvider.GetRequiredService<ITenantRepository>();
        IMachineRepository machineRepo = scope.ServiceProvider.GetRequiredService<IMachineRepository>();

        (int page, int pageSize) = SanitizePagination(request.Page, request.PageSize);

        // Resolve optional tenant filter
        int? tenantIdFilter = null;
        if (string.IsNullOrWhiteSpace(request.TenantExternalId) == false)
        {
            Tenant tenant = await ResolveTenantByExternalIdAsync(
                tenantRepo, request.TenantExternalId, context.CancellationToken);
            tenantIdFilter = tenant.Id;
        }

        (List<Machine> machines, int totalCount) = await machineRepo.SearchMachinesPagedAsync(
            tenantIdFilter, (page - 1) * pageSize, pageSize, context.CancellationToken);

        // Batch-load tenant names
        List<int> tenantIds = machines.Select(m => m.TenantId).Distinct().ToList();
        List<Tenant> tenantList = await tenantRepo.ListTenantsByIdsAsync(tenantIds, context.CancellationToken);
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
        AuthorizeInternalCaller(context);

        using IServiceScope scope = _scopeFactory.CreateScope();
        ITenantRepository tenantRepo = scope.ServiceProvider.GetRequiredService<ITenantRepository>();
        IUserRepository userRepo = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        IAuditLogRepository auditLogRepo = scope.ServiceProvider.GetRequiredService<IAuditLogRepository>();

        (int page, int pageSize) = SanitizePagination(request.Page, request.PageSize);

        // Resolve optional tenant filter
        int? tenantIdFilter = null;
        if (string.IsNullOrWhiteSpace(request.TenantExternalId) == false)
        {
            Tenant tenant = await ResolveTenantByExternalIdAsync(
                tenantRepo, request.TenantExternalId, context.CancellationToken);
            tenantIdFilter = tenant.Id;
        }

        (List<AuditLogEntry> entries, int totalCount) = await auditLogRepo.QueryAuditLogEntriesAsync(
            tenantIdFilter, (page - 1) * pageSize, pageSize, context.CancellationToken);

        // Batch-load usernames
        List<int> userIds = entries
            .Where(e => e.UserId.HasValue)
            .Select(e => e.UserId!.Value)
            .Distinct()
            .ToList();

        List<UserAccount> userList = await userRepo.GetUsersByIdsAsync(userIds, context.CancellationToken);
        Dictionary<int, string> usernames = userList.ToDictionary(u => u.Id, u => u.Username);

        // Batch-load tenant names
        List<int> tenantIds = entries
            .Where(e => e.TenantId.HasValue)
            .Select(e => e.TenantId!.Value)
            .Distinct()
            .ToList();

        List<Tenant> tenantList = await tenantRepo.ListTenantsByIdsAsync(tenantIds, context.CancellationToken);
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
        AuthorizeInternalCaller(context);

        using IServiceScope scope = _scopeFactory.CreateScope();
        IServerConfigurationRepository configRepo = scope.ServiceProvider.GetRequiredService<IServerConfigurationRepository>();

        List<ServerConfigurationSettings> settings = await configRepo.ListAllSettingsAsync(context.CancellationToken);

        GetServerSettingsResponse response = new GetServerSettingsResponse();

        foreach (ServerConfigurationSettings setting in settings)
        {
            ServerSettingValidation.Bounds.TryGetValue(setting.Key, out (int Min, int Max) bounds);

            response.Settings.Add(new ServerSetting
            {
                Key = (ServerSettingKey)(int)setting.Key,
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
        AuthorizeInternalCaller(context);

        ServerConfigurationSettingKeys key = (ServerConfigurationSettingKeys)(int)request.Key;

        // Validate the value with the same rules the REST admin path enforces so the gRPC path can no
        // longer persist values the REST path would reject.
        string? validationError = ServerSettingValidation.Validate(key, request.Value);
        if (validationError is not null)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, validationError));
        }

        using IServiceScope scope = _scopeFactory.CreateScope();
        IServerConfigurationRepository configRepo = scope.ServiceProvider.GetRequiredService<IServerConfigurationRepository>();

        // Upsert so a valid key always persists, matching the REST admin path. Validation above
        // already rejected unknown keys, so there is no missing-row case left to report.
        await configRepo.UpsertSettingAsync(key, request.Value, context.CancellationToken);

        // Evict the shared Redis read-through entry so every replica re-reads from the database,
        // matching the REST admin path.
        await ServerSettingsInvalidation.InvalidateAsync(_redis, key, _logger);

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
        AuthorizeInternalCaller(context);

        SubscriptionTier? tier = MapBillingTierToSubscriptionTier(request.Tier);
        if (tier is null)
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
        ITenantRepository tenantRepo = scope.ServiceProvider.GetRequiredService<ITenantRepository>();
        ISubscriptionRepository subscriptionRepo = scope.ServiceProvider.GetRequiredService<ISubscriptionRepository>();

        Tenant tenant = await ResolveTenantByExternalIdAsync(
            tenantRepo, request.TenantExternalId, context.CancellationToken);

        int updated = await subscriptionRepo.UpdateSubscriptionStateAsync(
            tenant.Id, tier.Value, status, cancellationToken: context.CancellationToken);

        if (updated == 0)
        {
            return new UpdateTenantSubscriptionResponse
            {
                Success = false,
                Message = $"No subscription found for tenant '{request.TenantExternalId}'"
            };
        }

        // The tier write above is not wrapped in a transaction, so it is already durable; dispatch the
        // reclassification the subscription seam marked rather than waiting for scope teardown.
        scope.ServiceProvider.GetRequiredService<RetentionReclassifyDispatcher>().DispatchPending();

        _logger.LogInformation(
            "FleetAdmin: tenant {TenantId} subscription updated to tier={Tier}, status={Status}",
            tenant.Id, tier.Value, status);

        return new UpdateTenantSubscriptionResponse
        {
            Success = true,
            Message = "OK"
        };
    }

    /// <summary>
    /// Gets the per-tenant subscription limit overrides for a tenant.
    /// </summary>
    public override async Task<GetTenantOverrideResponse> GetTenantOverride(
        GetTenantOverrideRequest request, ServerCallContext context)
    {
        AuthorizeInternalCaller(context);

        using IServiceScope scope = _scopeFactory.CreateScope();
        ITenantRepository tenantRepo = scope.ServiceProvider.GetRequiredService<ITenantRepository>();
        ITenantSubscriptionOverrideRepository overrideRepo = scope.ServiceProvider.GetRequiredService<ITenantSubscriptionOverrideRepository>();

        Tenant tenant = await ResolveTenantByExternalIdAsync(
            tenantRepo, request.TenantExternalId, context.CancellationToken);

        TenantSubscriptionOverride? overrideRecord = await overrideRepo.GetOverrideForTenantAsync(
            tenant.Id, context.CancellationToken);

        if (overrideRecord is null)
        {
            return new GetTenantOverrideResponse
            {
                HasOverride = false,
                MachineLimit = 0,
                RetentionDays = 0,
                AlertRuleLimit = 0,
                WebhookLimit = 0,
            };
        }

        return new GetTenantOverrideResponse
        {
            HasOverride = true,
            MachineLimit = overrideRecord.MachineLimit ?? -1,
            RetentionDays = overrideRecord.RetentionDays ?? -1,
            AlertRuleLimit = overrideRecord.AlertRuleLimit ?? -1,
            WebhookLimit = overrideRecord.WebhookLimit ?? -1,
        };
    }

    /// <summary>
    /// Creates or updates the per-tenant subscription limit overrides for a tenant.
    /// </summary>
    public override async Task<SetTenantOverrideResponse> SetTenantOverride(
        SetTenantOverrideRequest request, ServerCallContext context)
    {
        AuthorizeInternalCaller(context);

        using IServiceScope scope = _scopeFactory.CreateScope();
        ITenantRepository tenantRepo = scope.ServiceProvider.GetRequiredService<ITenantRepository>();
        ITenantSubscriptionOverrideRepository overrideRepo = scope.ServiceProvider.GetRequiredService<ITenantSubscriptionOverrideRepository>();
        ISubscriptionRepository subscriptionRepo = scope.ServiceProvider.GetRequiredService<ISubscriptionRepository>();
        IDatabaseTransactionProvider transactionProvider = scope.ServiceProvider.GetRequiredService<IDatabaseTransactionProvider>();
        IAuditLogRepository auditLog = scope.ServiceProvider.GetRequiredService<IAuditLogRepository>();

        Tenant tenant = await ResolveTenantByExternalIdAsync(
            tenantRepo, request.TenantExternalId, context.CancellationToken);

        // Convert -1 to null for database storage (-1 means "use tier default", 0 means "deny all")
        int? machineLimit = request.MachineLimit >= 0 ? request.MachineLimit : null;
        int? retentionDays = request.RetentionDays >= 0 ? request.RetentionDays : null;
        int? alertRuleLimit = request.AlertRuleLimit >= 0 ? request.AlertRuleLimit : null;
        int? webhookLimit = request.WebhookLimit >= 0 ? request.WebhookLimit : null;

        using IDatabaseTransaction transaction = await transactionProvider.BeginTransactionAsync(context.CancellationToken);

        await overrideRepo.UpsertOverrideAsync(
            tenant.Id, machineLimit, retentionDays, alertRuleLimit, webhookLimit, context.CancellationToken);

        await auditLog.InsertAuditLogAsync(AuditHelper.Create(
            tenantId: tenant.Id,
            userId: null,
            machineId: null,
            AuditAction.TenantSubscriptionOverrideChanged,
            AuditResourceType.Subscription,
            tenant.Id.ToString(),
            new { MachineLimit = machineLimit, RetentionDays = retentionDays, AlertRuleLimit = alertRuleLimit, WebhookLimit = webhookLimit },
            ipAddress: null), context.CancellationToken);

        await transaction.CommitAsync(context.CancellationToken);

        // The override changes effective retention but does not route through the subscription
        // repository's own mutators, so invalidate the cached entry here to make the change take
        // effect within one request rather than one cache TTL.
        await subscriptionRepo.InvalidateSubscriptionCacheAsync(tenant.Id, context.CancellationToken);

        // An override edit changes effective retention without touching the subscription row, so the
        // tier-change seam never sees it. Enqueue the reclassification here, after the commit, so the
        // tenant's surviving telemetry follows the retention the override now grants.
        EnqueueRetentionReclassify(scope, tenant.Id);

        _logger.LogInformation(
            "FleetAdmin: tenant {TenantId} override set (machineLimit={MachineLimit}, retentionDays={RetentionDays}, alertRuleLimit={AlertRuleLimit}, webhookLimit={WebhookLimit})",
            tenant.Id, machineLimit, retentionDays, alertRuleLimit, webhookLimit);

        return new SetTenantOverrideResponse
        {
            Success = true,
            Message = "OK"
        };
    }

    /// <summary>
    /// Removes the per-tenant subscription limit overrides for a tenant, reverting to tier defaults.
    /// </summary>
    public override async Task<RemoveTenantOverrideResponse> RemoveTenantOverride(
        RemoveTenantOverrideRequest request, ServerCallContext context)
    {
        AuthorizeInternalCaller(context);

        using IServiceScope scope = _scopeFactory.CreateScope();
        ITenantRepository tenantRepo = scope.ServiceProvider.GetRequiredService<ITenantRepository>();
        ITenantSubscriptionOverrideRepository overrideRepo = scope.ServiceProvider.GetRequiredService<ITenantSubscriptionOverrideRepository>();
        ISubscriptionRepository subscriptionRepo = scope.ServiceProvider.GetRequiredService<ISubscriptionRepository>();
        IDatabaseTransactionProvider transactionProvider = scope.ServiceProvider.GetRequiredService<IDatabaseTransactionProvider>();
        IAuditLogRepository auditLog = scope.ServiceProvider.GetRequiredService<IAuditLogRepository>();

        Tenant tenant = await ResolveTenantByExternalIdAsync(
            tenantRepo, request.TenantExternalId, context.CancellationToken);

        using IDatabaseTransaction transaction = await transactionProvider.BeginTransactionAsync(context.CancellationToken);

        await overrideRepo.RemoveOverrideAsync(tenant.Id, context.CancellationToken);

        await auditLog.InsertAuditLogAsync(AuditHelper.Create(
            tenantId: tenant.Id,
            userId: null,
            machineId: null,
            AuditAction.TenantSubscriptionOverrideChanged,
            AuditResourceType.Subscription,
            tenant.Id.ToString(),
            new { Cleared = true },
            ipAddress: null), context.CancellationToken);

        await transaction.CommitAsync(context.CancellationToken);

        // Clearing the override changes effective retention; invalidate the cached entry so the
        // reverted retention takes effect within one request rather than one cache TTL.
        await subscriptionRepo.InvalidateSubscriptionCacheAsync(tenant.Id, context.CancellationToken);

        // Reverting to the tier default can move the tenant into a different retention class, so the
        // surviving telemetry is reclassified here as well, after the commit.
        EnqueueRetentionReclassify(scope, tenant.Id);

        _logger.LogInformation(
            "FleetAdmin: tenant {TenantId} override removed", tenant.Id);

        return new RemoveTenantOverrideResponse
        {
            Success = true,
            Message = "OK"
        };
    }

    /// <summary>
    /// Queues the retention reclassification for a tenant after the write that changed its effective
    /// retention has committed. Routed through the same dispatcher the subscription seam marks into, so
    /// override edits and tier changes share one enqueue path and one failure policy.
    /// </summary>
    /// <param name="scope">The request scope the dispatcher is resolved from.</param>
    /// <param name="tenantId">The tenant whose telemetry is reclassified.</param>
    private static void EnqueueRetentionReclassify(IServiceScope scope, int tenantId)
    {
        RetentionReclassifyDispatcher dispatcher =
            scope.ServiceProvider.GetRequiredService<RetentionReclassifyDispatcher>();
        dispatcher.MarkPending(tenantId);
        dispatcher.DispatchPending();
    }

    /// <summary>
    /// Creates or updates the OIDC configuration for a tenant.
    /// </summary>
    public override async Task<ConfigureTenantOidcResponse> ConfigureTenantOidc(
        ConfigureTenantOidcRequest request, ServerCallContext context)
    {
        AuthorizeInternalCaller(context);

        using IServiceScope scope = _scopeFactory.CreateScope();
        ITenantRepository tenantRepo = scope.ServiceProvider.GetRequiredService<ITenantRepository>();

        Tenant tenant = await ResolveTenantByExternalIdAsync(
            tenantRepo, request.TenantExternalId, context.CancellationToken);

        TenantOidcConfiguration? existing = await tenantRepo.GetTenantOidcConfigByTenantIdAsync(tenant.Id, context.CancellationToken);

        string? metadataAddress = string.IsNullOrWhiteSpace(request.MetadataAddress) ? null : request.MetadataAddress;

        // Encrypt the client secret at rest. The protector emits a marker-prefixed value
        // so legacy plaintext rows are distinguishable from properly-encrypted ones.
        string protectedClientSecret = _oidcSecretProtector.Protect(request.ClientSecret);

        if (existing is not null)
        {
            await tenantRepo.UpdateTenantOidcConfigAsync(
                tenant.Id,
                request.Authority,
                request.ClientId,
                protectedClientSecret,
                metadataAddress,
                request.EmailDomain,
                request.IsEnabled,
                context.CancellationToken);

            _logger.LogInformation(
                "FleetAdmin: updated OIDC configuration for tenant {TenantId}", tenant.Id);
        }
        else
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            TenantOidcConfiguration config = new TenantOidcConfiguration
            {
                TenantId = tenant.Id,
                Authority = request.Authority,
                ClientId = request.ClientId,
                ClientSecret = protectedClientSecret,
                MetadataAddress = metadataAddress,
                EmailDomain = request.EmailDomain,
                IsEnabled = request.IsEnabled,
                CreatedAt = now,
                UpdatedAt = now,
            };

            await tenantRepo.InsertTenantOidcConfigAsync(config, context.CancellationToken);

            _logger.LogInformation(
                "FleetAdmin: created OIDC configuration for tenant {TenantId}", tenant.Id);
        }

        return new ConfigureTenantOidcResponse
        {
            Success = true,
            Message = "OK"
        };
    }

    /// <summary>Deactivates a tenant and schedules its purge (Phase 1 of deletion).</summary>
    public override async Task<RequestTenantDeletionResponse> RequestTenantDeletion(
        RequestTenantDeletionRequest request, ServerCallContext context)
    {
        AuthorizeInternalCaller(context);

        using IServiceScope scope = _scopeFactory.CreateScope();
        ITenantRepository tenantRepo = scope.ServiceProvider.GetRequiredService<ITenantRepository>();
        TenantDeletionHandler handler = scope.ServiceProvider.GetRequiredService<TenantDeletionHandler>();

        // Unlike ResolveTenantByExternalIdAsync, this lookup does not filter on IsActive: a repeat
        // request against an already-deactivated tenant must still resolve so it reaches the
        // handler's double-delete guard instead of surfacing as a false NotFound.
        Tenant tenant = await ResolveTenantByExternalIdIncludingInactiveAsync(
            tenantRepo, request.TenantExternalId, context.CancellationToken);

        TenantDeletionResult result = await handler.RequestDeletionAsync(
            tenant.Id, request.RequestedByUserId, request.Reason, context.CancellationToken);

        RequestTenantDeletionResponse response = new RequestTenantDeletionResponse
        {
            Success = result.Success,
            Message = result.Message,
        };
        if (result.ScheduledPurgeAt.HasValue)
        {
            response.ScheduledPurgeAt = Timestamp.FromDateTimeOffset(result.ScheduledPurgeAt.Value);
        }

        return response;
    }

    /// <summary>Restores a tenant during its deletion grace window.</summary>
    public override async Task<RestoreTenantResponse> RestoreTenant(
        RestoreTenantRequest request, ServerCallContext context)
    {
        AuthorizeInternalCaller(context);

        using IServiceScope scope = _scopeFactory.CreateScope();
        ITenantRepository tenantRepo = scope.ServiceProvider.GetRequiredService<ITenantRepository>();
        TenantDeletionHandler handler = scope.ServiceProvider.GetRequiredService<TenantDeletionHandler>();

        // A tenant being restored is, by definition, already deactivated, so this must resolve
        // regardless of IsActive — the plain ResolveTenantByExternalIdAsync would always 404 here.
        Tenant tenant = await ResolveTenantByExternalIdIncludingInactiveAsync(
            tenantRepo, request.TenantExternalId, context.CancellationToken);

        TenantDeletionResult result = await handler.RestoreAsync(
            tenant.Id, request.RequestedByUserId, context.CancellationToken);

        return new RestoreTenantResponse { Success = result.Success, Message = result.Message };
    }

    /// <summary>Lists tenant deletions for the admin panel.</summary>
    public override async Task<ListTenantDeletionsResponse> ListTenantDeletions(
        ListTenantDeletionsRequest request, ServerCallContext context)
    {
        AuthorizeInternalCaller(context);

        using IServiceScope scope = _scopeFactory.CreateScope();
        ITenantDeletionRepository deletionRepo = scope.ServiceProvider.GetRequiredService<ITenantDeletionRepository>();

        (int page, int pageSize) = SanitizePagination(request.Page, request.PageSize);
        (List<TenantDeletion> deletions, int totalCount) = await deletionRepo.ListDeletionsAsync(
            request.IncludeCompleted, (page - 1) * pageSize, pageSize, context.CancellationToken);

        ListTenantDeletionsResponse response = new ListTenantDeletionsResponse { TotalCount = totalCount };
        foreach (TenantDeletion d in deletions)
        {
            TenantDeletionRecord record = new TenantDeletionRecord
            {
                Id = d.Id,
                TenantId = d.TenantId,
                TenantExternalId = d.TenantExternalId,
                TenantName = d.TenantName,
                RequestedByUserId = d.RequestedByUserId,
                RequestedAt = Timestamp.FromDateTimeOffset(d.RequestedAt),
                ScheduledPurgeAt = Timestamp.FromDateTimeOffset(d.ScheduledPurgeAt),
                Status = (int)d.Status,
                Reason = d.Reason ?? string.Empty,
            };
            if (d.PurgedAt.HasValue)
            {
                record.PurgedAt = Timestamp.FromDateTimeOffset(d.PurgedAt.Value);
            }

            response.Deletions.Add(record);
        }

        return response;
    }

    /// <summary>
    /// Returns the quantity Stripe should bill for a tenant on <c>target_tier</c>. The tier is
    /// supplied by the caller because checkout sizes the first invoice for the tier being
    /// purchased, while the tenant is still on Free at that moment.
    /// </summary>
    public override async Task<GetBillableMachineCountResponse> GetBillableMachineCount(
        GetBillableMachineCountRequest request, ServerCallContext context)
    {
        AuthorizeInternalCaller(context);

        using IServiceScope scope = _scopeFactory.CreateScope();
        ITenantRepository tenantRepository = scope.ServiceProvider.GetRequiredService<ITenantRepository>();
        ISubscriptionService subscriptionService = scope.ServiceProvider.GetRequiredService<ISubscriptionService>();

        if (System.Enum.TryParse(request.TargetTier, ignoreCase: true, out SubscriptionTier tier) == false)
        {
            return new GetBillableMachineCountResponse
            {
                Success = false,
                Message = $"Unknown tier '{request.TargetTier}'"
            };
        }

        Tenant? tenant = await tenantRepository.GetTenantByExternalIdAsync(
            request.TenantExternalId, context.CancellationToken);

        if (tenant is null)
        {
            return new GetBillableMachineCountResponse
            {
                Success = false,
                Message = $"No tenant found for {request.TenantExternalId}"
            };
        }

        int billable = await subscriptionService.GetBillableMachineCountAsync(
            tenant.Id, tier, context.CancellationToken);

        return new GetBillableMachineCountResponse { BillableCount = billable, Success = true };
    }

    private void AuthorizeInternalCaller(ServerCallContext context)
    {
        _callerAuthorizer.Authorize(context);
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
        TenantSubscription? subscription,
        TierFeatureLimit? tierLimits = null)
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
            fleetTenant.Subscription = MapSubscription(subscription, tierLimits);
        }

        return fleetTenant;
    }

    internal static FleetTenantSubscription MapSubscription(TenantSubscription subscription, TierFeatureLimit? tierLimits = null)
    {
        FleetTenantSubscription proto = new FleetTenantSubscription
        {
            Tier = MapSubscriptionTierToBillingTier(subscription.Tier),
            Status = subscription.Status.ToString(),
            MachineLimit = tierLimits?.MachineLimit ?? 0,
            RetentionDays = tierLimits?.RetentionDays ?? 0,
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

    internal static BillingTier MapSubscriptionTierToBillingTier(SubscriptionTier tier)
    {
        return tier switch
        {
            SubscriptionTier.Free => BillingTier.Free,
            SubscriptionTier.Pro => BillingTier.Pro,
            SubscriptionTier.Team => BillingTier.Team,
            _ => BillingTier.Unspecified,
        };
    }

    internal static SubscriptionTier? MapBillingTierToSubscriptionTier(BillingTier tier)
    {
        return tier switch
        {
            BillingTier.Free => SubscriptionTier.Free,
            BillingTier.Pro => SubscriptionTier.Pro,
            BillingTier.Team => SubscriptionTier.Team,
            _ => null,
        };
    }

    private static async Task<Tenant> ResolveTenantByExternalIdAsync(
        ITenantRepository tenantRepo, string externalId, CancellationToken cancellationToken)
    {
        Tenant? tenant = await tenantRepo.GetTenantByExternalIdAsync(externalId, cancellationToken);

        if (tenant is null)
        {
            throw new RpcException(new Status(
                StatusCode.NotFound,
                $"Tenant not found for external ID: {externalId}"));
        }

        return tenant;
    }

    /// <summary>
    /// Resolves a tenant by external ID for the tenant-deletion lifecycle, where the target tenant
    /// may already be deactivated (a repeat deletion request or any restore). Unlike
    /// <see cref="ResolveTenantByExternalIdAsync"/>, this does not treat a deactivated tenant as
    /// not found.
    /// </summary>
    private static async Task<Tenant> ResolveTenantByExternalIdIncludingInactiveAsync(
        ITenantRepository tenantRepo, string externalId, CancellationToken cancellationToken)
    {
        Tenant? tenant = await tenantRepo.GetTenantByExternalIdIncludingInactiveAsync(externalId, cancellationToken);

        if (tenant is null)
        {
            throw new RpcException(new Status(
                StatusCode.NotFound,
                $"Tenant not found for external ID: {externalId}"));
        }

        return tenant;
    }
}
