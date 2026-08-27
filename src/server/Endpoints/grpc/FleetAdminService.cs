// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Text.Json;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Services.Core.Billing;
using Framlux.FleetManagement.Services.Core.Handlers;
using Framlux.FleetManagement.Services.Core.Hangfire;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Framlux.FleetManagement.Services.Core.Security;
using Framlux.FleetManagement.Services.Core.ServerConfiguration;
using Framlux.Vord.BillingGrpc;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Extensions.Options;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Hangfire.Storage;
using Hangfire.Storage.Monitoring;
using StackExchange.Redis;

namespace Framlux.FleetManagement.Server.Endpoints.Grpc;

/// <summary>
/// gRPC service that provides fleet administration data to the billing API admin panel.
/// </summary>
public sealed class FleetAdminService : FleetAdmin.FleetAdminBase
{
    private const int DefaultPageSize = 50;
    private const int MaxPageSize = 100;
    private const int MaxEnrichJobIds = 20;
    private const int MaxTriggeredByLength = 256;
    private const string FailedCountSection = "failed_count";
    private const string JobsSection = "jobs";
    private const string WorkersSection = "workers";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IInternalCallerAuthorizer _callerAuthorizer;
    private readonly IOidcSecretProtector _oidcSecretProtector;
    private readonly ILogger<FleetAdminService> _logger;
    private readonly IConnectionMultiplexer _redis;
    private readonly JobStorage _storage;
    private readonly TimeProvider _timeProvider;
    private readonly IBackgroundJobClientV2 _backgroundJobs;
    private readonly RecurringJobInspector _inspector;
    private readonly IOptions<HangfireOptions> _hangfireOptions;

    /// <summary>
    /// Creates a new instance of the <see cref="FleetAdminService"/> class.
    /// </summary>
    public FleetAdminService(
        IServiceScopeFactory scopeFactory,
        IInternalCallerAuthorizer callerAuthorizer,
        IOidcSecretProtector oidcSecretProtector,
        ILogger<FleetAdminService> logger,
        IConnectionMultiplexer redis,
        JobStorage storage,
        TimeProvider timeProvider,
        IBackgroundJobClientV2 backgroundJobs,
        IOptions<HangfireOptions> hangfireOptions)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(callerAuthorizer);
        ArgumentNullException.ThrowIfNull(oidcSecretProtector);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(redis);
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(backgroundJobs);
        ArgumentNullException.ThrowIfNull(hangfireOptions);
        _hangfireOptions = hangfireOptions;
        _scopeFactory = scopeFactory;
        _callerAuthorizer = callerAuthorizer;
        _oidcSecretProtector = oidcSecretProtector;
        _logger = logger;
        _redis = redis;
        _storage = storage;
        _timeProvider = timeProvider;
        _backgroundJobs = backgroundJobs;
        _inspector = new RecurringJobInspector(storage, timeProvider);
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

        bool parsed = System.Enum.TryParse(request.TargetTier, ignoreCase: true, out SubscriptionTier tier);

        // TryParse alone is not enough: it accepts any integer string (e.g. "7") and any defined
        // name, and SubscriptionTier declares None = 0. Require a defined enum value and reject
        // None and Free explicitly — only Pro and Team are billable, and nothing should be sizing
        // a Free purchase. Without this, an unparseable or non-billable tier silently floors to 0
        // (no TierFeatureLimits row, no FallbackFloors entry), and the caller writes a quantity-0
        // line item onto what may be a paying subscription.
        if ((parsed == false) ||
            (System.Enum.IsDefined(tier) == false) ||
            (tier == SubscriptionTier.None) ||
            (tier == SubscriptionTier.Free))
        {
            return new GetBillableMachineCountResponse
            {
                Success = false,
                Message = $"'{request.TargetTier}' is not a billable tier"
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

    /// <summary>
    /// Lists every recurring job this build knows about with its health, plus worker heartbeats,
    /// the fleet-wide failed-job count, and the outcomes of any manual runs the caller asks about.
    /// </summary>
    public override Task<ListRecurringJobsResponse> ListRecurringJobs(
        ListRecurringJobsRequest request, ServerCallContext context)
    {
        AuthorizeInternalCaller(context);

        IMonitoringApi monitoring = _storage.GetMonitoringApi();

        ListRecurringJobsResponse response = new()
        {
            ServerTime = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()),
        };

        // Each block is isolated. This view is what an operator opens when the platform is already
        // misbehaving, so a storage fault in one section must not cost them the other two — losing
        // the worker heartbeat because a statistics query failed would hide the headline signal.
        // Sections that could not be read are named so the panel can say "unknown" rather than
        // rendering a confident zero.
        try
        {
            StatisticsDto statistics = monitoring.GetStatistics();
            response.FailedJobCount = statistics.Failed;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FleetAdmin: could not read Hangfire statistics");
            response.UnavailableSections.Add(FailedCountSection);
        }

        try
        {
            foreach (RecurringJobSnapshot snapshot in _inspector.Inspect())
            {
                response.Jobs.Add(MapRecurringJob(snapshot));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FleetAdmin: could not read recurring job state");
            response.Jobs.Clear();
            response.UnavailableSections.Add(JobsSection);
        }

        try
        {
            foreach (ServerDto server in monitoring.Servers())
            {
                response.Workers.Add(MapWorker(server));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FleetAdmin: could not read Hangfire worker heartbeats");
            response.Workers.Clear();
            response.UnavailableSections.Add(WorkersSection);
        }

        foreach (string jobId in request.EnrichJobIds.Take(MaxEnrichJobIds))
        {
            response.ManualRuns.Add(MapManualRun(monitoring, jobId));
        }

        return Task.FromResult(response);
    }

    /// <summary>
    /// Runs a recurring job once, out of band, by enqueueing an ordinary background job of the
    /// same type. The recurring-job schedule is deliberately not touched: Hangfire's own trigger
    /// path rewrites the last-execution and next-execution fields, which would erase the very
    /// state this service reports.
    /// </summary>
    public override async Task<RunRecurringJobNowResponse> RunRecurringJobNow(
        RunRecurringJobNowRequest request, ServerCallContext context)
    {
        AuthorizeInternalCaller(context);

        string caller = DescribeCaller(context);

        if (RecurringJobIds.All.Contains(request.JobId) == false)
        {
            // The id is not echoed back: it is caller-controlled and unbounded, and a large value
            // would land in an HTTP/2 trailer and break the response rather than report the error.
            _logger.LogWarning(
                "FleetAdmin: rejected on-demand run of unknown recurring job (caller {Caller}, id length {Length})",
                caller, request.JobId.Length);

            throw new RpcException(new Status(
                StatusCode.InvalidArgument, "Unknown recurring job id."));
        }

        RecurringJobRunTarget target = _inspector.ResolveForRun(request.JobId);

        if (target.Job is null)
        {
            string reason = (target.Status == RecurringJobHealth.LoadFailed)
                ? $"Recurring job '{request.JobId}' is registered but its payload cannot be loaded, so it cannot be run."
                : $"Recurring job '{request.JobId}' is not registered in storage, so there is nothing to run.";

            _logger.LogWarning(
                "FleetAdmin: rejected on-demand run of {JobId} (caller {Caller}, status {Status})",
                request.JobId, caller, target.Status);

            throw new RpcException(new Status(StatusCode.FailedPrecondition, reason));
        }

        if (await TryClaimManualRunAsync(request.JobId).ConfigureAwait(false) == false)
        {
            _logger.LogWarning(
                "FleetAdmin: rejected on-demand run of {JobId} still in cooldown (caller {Caller})",
                request.JobId, caller);

            throw new RpcException(new Status(
                StatusCode.FailedPrecondition,
                $"Recurring job '{request.JobId}' was already run on demand recently. Wait for the cooldown to elapse before running it again."));
        }

        // These are the same parameter names Hangfire's own scheduler stamps when it fires a
        // recurring job, so a manual run is a first-class run of that job in Hangfire's model.
        Dictionary<string, object> parameters = new()
        {
            ["RecurringJobId"] = request.JobId,
            ["TriggeredBy"] = Truncate(request.TriggeredBy, MaxTriggeredByLength),
        };

        string? enqueuedJobId;

        try
        {
            // EnqueuedState defaults to the "default" queue, but QueueAttribute is an elect-state
            // filter that runs first, so [Queue("critical")] and [Queue("long")] still win.
            enqueuedJobId = _backgroundJobs.Create(target.Job, new EnqueuedState(), parameters);
        }
        catch (BackgroundJobClientException ex)
        {
            // Hangfire wraps every storage failure in this type. Left unhandled it surfaces as
            // StatusCode.Unknown, which the admin API maps to a 500 carrying .NET internals.
            _logger.LogError(
                ex, "FleetAdmin: enqueueing {JobId} on demand failed (caller {Caller})", request.JobId, caller);

            throw new RpcException(new Status(
                StatusCode.Unavailable, $"Could not enqueue recurring job '{request.JobId}'."));
        }

        if (string.IsNullOrEmpty(enqueuedJobId))
        {
            // BackgroundJobClient.Create returns _factory.Create(context)?.Id, so this is null
            // whenever the storage declines to create the job. Hangfire targets netstandard2.0
            // without nullable annotations, so nothing warns. Reporting success here would tell
            // the caller a run happened and leave it with no id to record it against.
            _logger.LogError(
                "FleetAdmin: enqueueing {JobId} on demand returned no job id (caller {Caller})",
                request.JobId, caller);

            throw new RpcException(new Status(
                StatusCode.Unavailable, $"Could not enqueue recurring job '{request.JobId}'."));
        }

        _logger.LogInformation(
            "FleetAdmin: recurring job {JobId} run on demand by {TriggeredBy} (caller {Caller}) as job {EnqueuedJobId}",
            request.JobId, request.TriggeredBy, caller, enqueuedJobId);

        return new RunRecurringJobNowResponse
        {
            Success = true,
            Message = "OK",
            EnqueuedJobId = enqueuedJobId,
        };
    }

    /// <summary>
    /// Claims the right to run a job on demand, returning false while a previous run is still
    /// inside its cooldown. Set-if-absent in Redis, so concurrent callers cannot both claim it.
    /// </summary>
    /// <remarks>
    /// A failure to reach Redis lets the run proceed: the cooldown bounds an accident, and denying
    /// an operator their tool during a Redis outage would be the worse failure.
    /// </remarks>
    private async Task<bool> TryClaimManualRunAsync(string jobId)
    {
        int cooldownSeconds = _hangfireOptions.Value.ManualRunCooldownSeconds;

        if (cooldownSeconds <= 0)
        {
            return true;
        }

        try
        {
            IDatabase redis = _redis.GetDatabase();

            return await redis.StringSetAsync(
                $"fleet:manualrun:{jobId}",
                "1",
                TimeSpan.FromSeconds(cooldownSeconds),
                When.NotExists).ConfigureAwait(false);
        }
        catch (RedisException ex)
        {
            _logger.LogWarning(
                ex, "FleetAdmin: could not check the on-demand run cooldown for {JobId}; allowing the run", jobId);

            return true;
        }
    }

    /// <summary>
    /// Describes the authenticated peer for the audit trail. The client-certificate subject is the
    /// only identity the fleet itself verified; the operator identity that rides in the request is
    /// asserted by the caller and cannot stand alone.
    /// </summary>
    private static string DescribeCaller(ServerCallContext context)
    {
        try
        {
            string? subject = context.GetHttpContext()?.Connection.ClientCertificate?.Subject;

            if (string.IsNullOrEmpty(subject) == false)
            {
                return subject;
            }
        }
        catch (InvalidOperationException)
        {
            // GetHttpContext throws rather than returning null when the call is not hosted by
            // ASP.NET Core, which is the case for a directly-constructed context in the tests.
        }

        return context.Peer ?? "unknown";
    }

    private static string Truncate(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        return value[..maxLength];
    }

    private static FleetRecurringJob MapRecurringJob(RecurringJobSnapshot snapshot)
    {
        FleetRecurringJob job = new()
        {
            Id = snapshot.Id,
            Cron = snapshot.Cron,
            TimeZoneId = snapshot.TimeZoneId,
            LastJobId = snapshot.LastJobId,
            LastJobState = snapshot.LastJobState,
            Error = snapshot.Error,
            RetryAttempt = snapshot.RetryAttempt,
            Status = MapStatus(snapshot.Status),
        };

        if (snapshot.LastExecution.HasValue)
        {
            job.LastExecution = ToTimestamp(snapshot.LastExecution.Value);
        }

        if (snapshot.NextExecution.HasValue)
        {
            job.NextExecution = ToTimestamp(snapshot.NextExecution.Value);
        }

        return job;
    }

    private static FleetJobWorker MapWorker(ServerDto server)
    {
        FleetJobWorker mapped = new()
        {
            Name = server.Name ?? "",
            WorkersCount = server.WorkersCount,
        };

        if (server.Queues is not null)
        {
            mapped.Queues.AddRange(server.Queues);
        }

        // Hangfire substitutes DateTime.MinValue when a server reports no start time. A year-1
        // start time is meaningless to an operator, so leave the field unset rather than rendering
        // it. (It would not throw — ToTimestamp forces Kind to Utc and DateTime.MinValue is exactly
        // protobuf's minimum — but it would still be noise.)
        if (server.StartedAt > DateTime.MinValue)
        {
            mapped.StartedAt = ToTimestamp(server.StartedAt);
        }

        if (server.Heartbeat.HasValue)
        {
            mapped.Heartbeat = ToTimestamp(server.Heartbeat.Value);
        }

        return mapped;
    }

    private static FleetManualRun MapManualRun(IMonitoringApi monitoring, string jobId)
    {
        FleetManualRun run = new() { JobId = jobId };

        JobDetailsDto? details;

        try
        {
            details = monitoring.JobDetails(jobId);
        }
        catch (Exception)
        {
            // One bad id must not take down the whole jobs page. Deliberately broad: the Postgres
            // monitoring API parses the id with Convert.ToInt64 (FormatException on anything
            // non-numeric) and then builds its property dictionary with ToDictionary, which throws
            // on duplicate job-parameter rows the schema's non-unique index permits. The in-memory
            // storage used by tests returns null and has neither path, so none of this is
            // reachable from the functional suite — do not delete it for looking unreachable.
            run.Retained = false;

            return run;
        }

        if (details is null)
        {
            // Hangfire has expired the job row. The run still happened; the caller's own audit
            // record is what proves it, so report it rather than dropping it.
            run.Retained = false;

            return run;
        }

        run.Retained = true;

        if (details.CreatedAt.HasValue)
        {
            run.CreatedAt = ToTimestamp(details.CreatedAt.Value);
        }

        if (details.Properties is not null)
        {
            run.RecurringJobId = ReadJobParameter(details.Properties, "RecurringJobId");
            run.TriggeredBy = ReadJobParameter(details.Properties, "TriggeredBy");
        }

        StateHistoryDto? latest = details.History?.OrderByDescending(h => h.CreatedAt).FirstOrDefault();

        if (latest is not null)
        {
            run.State = latest.StateName ?? "";
            run.Reason = latest.Reason ?? "";
        }

        return run;
    }

    /// <summary>
    /// Reads one job parameter. Hangfire stores parameter values JSON-serialised, so a string
    /// round-trips with its quotes; returning the raw stored value would surface
    /// <c>"tenant-purge"</c> including them. Unparseable values fall back to the raw string rather
    /// than throwing, so one malformed parameter cannot break the whole response.
    /// </summary>
    private static string ReadJobParameter(IDictionary<string, string> properties, string name)
    {
        // Note: Properties is IDictionary, not IReadOnlyDictionary, so the GetValueOrDefault
        // extension does not apply here.
        if (properties.TryGetValue(name, out string? raw) == false)
        {
            return "";
        }

        if (string.IsNullOrEmpty(raw))
        {
            return "";
        }

        try
        {
            return JsonSerializer.Deserialize<string>(raw) ?? "";
        }
        catch (JsonException)
        {
            return raw;
        }
    }

    private static FleetRecurringJobStatus MapStatus(RecurringJobHealth health)
    {
        return health switch
        {
            RecurringJobHealth.Scheduled => FleetRecurringJobStatus.Scheduled,
            RecurringJobHealth.Overdue => FleetRecurringJobStatus.Overdue,
            RecurringJobHealth.Disabled => FleetRecurringJobStatus.Disabled,
            RecurringJobHealth.Missing => FleetRecurringJobStatus.Missing,
            RecurringJobHealth.LoadFailed => FleetRecurringJobStatus.LoadFailed,
            RecurringJobHealth.SchedulingError => FleetRecurringJobStatus.SchedulingError,
            RecurringJobHealth.Unknown => FleetRecurringJobStatus.Unspecified,
            _ => FleetRecurringJobStatus.Unspecified,
        };
    }

    private static Timestamp ToTimestamp(DateTime value)
    {
        return Timestamp.FromDateTime(DateTime.SpecifyKind(value, DateTimeKind.Utc));
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
