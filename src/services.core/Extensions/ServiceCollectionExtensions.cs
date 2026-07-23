// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Alerts;
using Framlux.FleetManagement.Services.Core.Alerts.Formatters;
using Framlux.FleetManagement.Services.Core.Billing;
using Framlux.FleetManagement.Services.Core.Commands;
using Framlux.FleetManagement.Services.Core.DataExport;
using Framlux.FleetManagement.Services.Core.Handlers;
using Framlux.FleetManagement.Services.Core.Hangfire;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Framlux.FleetManagement.Services.Core.Machines;
using Framlux.FleetManagement.Services.Core.Notifications;
using Framlux.FleetManagement.Services.Core.Options;
using Framlux.FleetManagement.Services.Core.Security;
using Framlux.FleetManagement.Services.Core.ServerConfiguration;
using Framlux.FleetManagement.Services.Core.Telemetry;
using Framlux.Vord.BillingGrpc;
using Hangfire;
using LinqToDB;
using LinqToDB.Extensions.DependencyInjection;
using LinqToDB.Extensions.Logging;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Npgsql;
using Polly;
using Serilog;
using Serilog.Formatting.Compact;
using StackExchange.Redis;

namespace Framlux.FleetManagement.Services.Core.Extensions;

/// <summary>
/// Extension methods for registering shared services and infrastructure across api-server and services-worker.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers Serilog structured logging with compact JSON output written to the console.
    /// </summary>
    public static IHostBuilder AddCoreSerilog(this IHostBuilder hostBuilder)
    {
        hostBuilder.UseSerilog((context, configuration) =>
            configuration
                .ReadFrom.Configuration(context.Configuration)
                .WriteTo.Console(new RenderedCompactJsonFormatter()));

        return hostBuilder;
    }

    /// <summary>
    /// Registers shared configuration option bindings used by both api-server and services-worker.
    /// </summary>
    public static IServiceCollection AddCoreOptions(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<BillingOptions>()
            .Bind(configuration.GetSection("Billing"))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<BillingOptions>, BillingOptionsValidator>();

        services.Configure<TierDefaultOptions>(configuration.GetSection("TierDefaults"));

        services.AddOptions<DatabaseOptions>()
            .Bind(configuration.GetSection("Database"))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<RedisOptions>()
            .Bind(configuration.GetSection("Redis"))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<HangfireOptions>()
            .Bind(configuration.GetSection("Hangfire"))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<ObjectStorageOptions>()
            .Bind(configuration.GetSection("ObjectStorage"))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<ObjectStorageOptions>, ObjectStorageOptionsValidator>();

        services.AddOptions<ResendOptions>()
            .Bind(configuration.GetSection("Resend"));

        services.AddOptions<AppOptions>()
            .Bind(configuration.GetSection("App"))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        return services;
    }

    /// <summary>
    /// Registers the LinqToDB database context, Npgsql data source, and all repository interfaces.
    /// </summary>
    public static IServiceCollection AddRepositories(
        this IServiceCollection services,
        DatabaseOptions dbOpts,
        string applicationName)
    {
        string connectionString = BuildConnectionString(dbOpts, applicationName);

        services.AddNpgsqlDataSource(connectionString);
        services.AddLinqToDBContext<DatabaseContext>((provider, options) => options.UsePostgreSQL(connectionString: connectionString)
                .UseDefaultLogging(provider));

        services.AddScoped<IAuditContextAccessor, NullAuditContextAccessor>();
        services.AddScoped<DatabaseRepository>();
        services.AddScoped<IDatabaseTransactionProvider>(sp => sp.GetRequiredService<DatabaseRepository>());
        services.AddScoped<IAuditLogRepository>(sp => sp.GetRequiredService<DatabaseRepository>());
        services.AddScoped<IUserRepository>(sp => sp.GetRequiredService<DatabaseRepository>());
        services.AddScoped<ITenantRepository>(sp => sp.GetRequiredService<DatabaseRepository>());
        // Subscription status is read on every state-changing request and every unary telemetry
        // call, so wrap the database-backed repository in a Redis short-TTL caching decorator that
        // invalidates on subscription mutations.
        services.AddScoped<RetentionReclassifyDispatcher>();
        services.AddScoped<ISubscriptionRepository>(sp => new CachingSubscriptionRepository(
            sp.GetRequiredService<DatabaseRepository>(),
            sp.GetRequiredService<IConnectionMultiplexer>(),
            sp.GetRequiredService<IOptions<RedisOptions>>(),
            sp.GetRequiredService<RetentionReclassifyDispatcher>()));
        // The reclassification job must read committed state, never the Redis-cached subscription
        // entry, which a concurrent read can re-seed with the pre-change tier between the decorator's
        // invalidate and the commit. Keying the database-backed repository keeps the job's dependency
        // explicit while leaving every other consumer on the cached decorator.
        services.AddKeyedScoped<ISubscriptionRepository>(
            RetentionReclassifyJob.UncachedRepositoryKey,
            (sp, key) => sp.GetRequiredService<DatabaseRepository>());
        services.AddScoped<IMachineRepository>(sp => sp.GetRequiredService<DatabaseRepository>());
        services.AddScoped<IInvitationRepository>(sp => sp.GetRequiredService<DatabaseRepository>());
        services.AddScoped<ISigningKeyRepository>(sp => sp.GetRequiredService<DatabaseRepository>());
        services.AddScoped<IRemoteCommandRepository>(sp => sp.GetRequiredService<DatabaseRepository>());
        services.AddScoped<IAlertRuleRepository>(sp => sp.GetRequiredService<DatabaseRepository>());
        services.AddScoped<IAlertEventRepository>(sp => sp.GetRequiredService<DatabaseRepository>());
        services.AddScoped<IAlertConditionStateRepository>(sp => sp.GetRequiredService<DatabaseRepository>());
        services.AddScoped<IIntegrationRepository>(sp => sp.GetRequiredService<DatabaseRepository>());
        services.AddScoped<IIntegrationDeliveryAttemptRepository>(sp => sp.GetRequiredService<DatabaseRepository>());
        services.AddScoped<IAlertEmailDeliveryAttemptRepository>(sp => sp.GetRequiredService<DatabaseRepository>());
        services.AddScoped<IDataProtectionKeyRepository>(sp => sp.GetRequiredService<DatabaseRepository>());
        services.AddScoped<IDataExportRepository>(sp => sp.GetRequiredService<DatabaseRepository>());
        services.AddScoped<IRegistrationTokenRepository>(sp => sp.GetRequiredService<DatabaseRepository>());
        services.AddScoped<IMachineStateRepository>(sp => sp.GetRequiredService<DatabaseRepository>());
        services.AddScoped<IServerConfigurationRepository>(sp => sp.GetRequiredService<DatabaseRepository>());
        services.AddScoped<ITierFeatureLimitRepository>(sp => sp.GetRequiredService<DatabaseRepository>());
        services.AddScoped<ITenantSubscriptionOverrideRepository>(sp => sp.GetRequiredService<DatabaseRepository>());
        services.AddScoped<IPartitionRepository>(sp => sp.GetRequiredService<DatabaseRepository>());
        services.AddScoped<ITenantDeletionRepository>(sp => sp.GetRequiredService<DatabaseRepository>());
        services.AddSingleton<IServerSettingsCache, ServerSettingsCache>();

        return services;
    }

    /// <summary>
    /// Registers Redis connection, distributed lock, machine ping, telemetry deduplication, and the Polly
    /// circuit breaker pipeline. Also registers health checks for PostgreSQL and Redis.
    /// </summary>
    public static IServiceCollection AddCoreInfrastructure(
        this IServiceCollection services,
        RedisOptions redisOpts,
        string postgresConnectionString)
    {
        ConfigurationOptions redisConfig = ConfigurationOptions.Parse(redisOpts.ConnectionString);
        redisConfig.ConnectTimeout = 5000;
        redisConfig.SyncTimeout = 3000;
        redisConfig.AsyncTimeout = 3000;
        redisConfig.ConnectRetry = 3;
        redisConfig.AbortOnConnectFail = false;
        services.AddSingleton<IConnectionMultiplexer>(sp =>
            ConnectionMultiplexer.Connect(redisConfig));
        services.AddSingleton<IMachinePingService, RedisMachinePingService>();
        services.AddSingleton<ITelemetryDeduplicationService, RedisTelemetryDeduplicationService>();
        services.AddSingleton<IAdvisoryLockProvider, PostgresAdvisoryLockProvider>();

        services.AddHangfireClient(postgresConnectionString);

        // Circuit breaker for telemetry database writes — prevents cascading failures
        // when PostgreSQL is slow or overloaded.
        services.AddSingleton(
            new ResiliencePipelineBuilder()
                .AddTimeout(TimeSpan.FromSeconds(10))
                .AddCircuitBreaker(new Polly.CircuitBreaker.CircuitBreakerStrategyOptions
                {
                    FailureRatio = 0.5,
                    SamplingDuration = TimeSpan.FromSeconds(30),
                    MinimumThroughput = 5,
                    BreakDuration = TimeSpan.FromSeconds(15),
                })
                .Build());

        // Retry pipeline for transient Redis ping-service failures — three retries with
        // exponential backoff and jitter starting at 200ms, never retrying cancellation.
        // Options are built by RedisRetryPipelineOptions so production and its unit tests
        // share the same retry semantics.
        services.AddResiliencePipeline("redis-ping", (pipelineBuilder, context) =>
        {
            Microsoft.Extensions.Logging.ILogger logger = context.ServiceProvider
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger<RedisMachinePingService>();

            pipelineBuilder.AddRetry(RedisRetryPipelineOptions.Create(TimeSpan.FromMilliseconds(200), logger));
        });

        // Health checks for PostgreSQL and Redis
        services.AddHealthChecks()
            .AddNpgSql(postgresConnectionString, name: "postgresql", failureStatus: HealthStatus.Unhealthy)
            .AddRedis(redisOpts.ConnectionString, name: "redis", failureStatus: HealthStatus.Unhealthy);

        return services;
    }

    /// <summary>
    /// Registers ASP.NET Core Data Protection with keys persisted in the shared Postgres
    /// database so multiple replicas (and processes — api-server and services-worker) share
    /// the same key ring. Persisting in Postgres rather than Redis means a Redis flush can no
    /// longer destroy the ring and, with it, every tenant OIDC secret encrypted under it.
    /// </summary>
    public static IServiceCollection AddCoreDataProtection(
        this IServiceCollection services,
        string applicationName)
    {
        services.AddDataProtection()
                .SetApplicationName(applicationName);

        services.AddSingleton<IConfigureOptions<KeyManagementOptions>>(sp =>
        {
            IServiceScopeFactory scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

            return new ConfigureOptions<KeyManagementOptions>(options =>
            {
                options.XmlRepository = new PostgresXmlRepository(scopeFactory);
            });
        });

        return services;
    }

    /// <summary>
    /// Registers core business services: machine, billing, alert, notification, and handler services.
    /// </summary>
    public static IServiceCollection AddCoreServices(
        this IServiceCollection services,
        BillingOptions billingOpts,
        ObjectStorageOptions objectStorageOpts)
    {
        services.AddSingleton<IMachineService, MachineService>()
                .AddSingleton<IMachineStateService, MachineStateService>()
                .AddSingleton<MachineSearchService>()
                .AddSingleton<ISqlDialect, PostgresSqlDialect>();

        services.AddSingleton<ServerConfigurationService>();
        services.AddScoped<ISubscriptionService, SubscriptionService>();
        services.AddScoped<DowngradeGuardService>();
        services.AddScoped<IDowngradeCleanupService, DowngradeCleanupService>();
        services.AddSingleton<IOidcSecretProtector, OidcSecretProtector>();
        services.AddSingleton<IIntegrationPayloadFormatter, SlackPayloadFormatter>();
        services.AddSingleton<IIntegrationPayloadFormatter, TeamsPayloadFormatter>();
        services.AddSingleton<IIntegrationPayloadFormatter, DiscordPayloadFormatter>();
        services.AddSingleton<IIntegrationPayloadFormatter, PagerDutyPayloadFormatter>();
        services.AddSingleton<IIntegrationPayloadFormatter, CustomPayloadFormatter>();
        services.AddSingleton<IRoleCacheInvalidator, RoleCacheInvalidator>();
        services.AddSingleton<IApiKeyCacheInvalidator, ApiKeyCacheInvalidator>();
        services.AddSingleton<IUserSecurityStampService, UserSecurityStampService>();
        services.AddHttpClient<IEmailService, ResendEmailService>();

        // System clock abstraction so time-dependent logic (e.g. registration token expiry)
        // can be unit-tested with a controllable TimeProvider.
        services.AddSingleton(TimeProvider.System);

        // Handler services for extracted endpoint business logic (scoped to share DatabaseContext)
        services.AddScoped<InvitationHandler>();
        services.AddScoped<MemberHandler>();
        services.AddScoped<OnboardingHandler>();
        services.AddScoped<IMachineBillingSync, MachineBillingSync>();
        services.AddScoped<MachineHandler>();
        services.AddScoped<DashboardHandler>();
        services.AddScoped<AuthMeHandler>();
        services.AddScoped<AdminHandler>();
        services.AddScoped<TenantHandler>();
        services.AddScoped<RegistrationTokenHandler>();
        services.AddScoped<UserHandler>();
        services.AddScoped<MachineDetailHandler>();
        services.AddScoped<IDataExportHandler, DataExportHandler>();
        services.AddScoped<ISigningKeyService, SigningKeyService>();
        services.AddScoped<MachineAuthorizedKeyService>();
        services.AddScoped<RemoteCommandService>();

        if (string.IsNullOrEmpty(objectStorageOpts.BucketName) == false)
        {
            services.AddSingleton<IObjectStorageService, ObjectStorageService>();
        }
        else
        {
            services.AddSingleton<IObjectStorageService, NoOpObjectStorageService>();
        }

        services.AddSingleton<IAlertDeliveryService, AlertDeliveryService>();
        services.AddSingleton<IEventAlertService, EventAlertService>();

        // Billing configuration: explicit opt-in via Billing:Enabled flag
        services.AddSingleton<BillingStatus>();

        if (billingOpts.Enabled)
        {
            // Billing gRPC client for managing Stripe subscriptions
            services.AddGrpcClient<BillingManagement.BillingManagementClient>(options =>
            {
                options.Address = new Uri(billingOpts.GrpcUrl);
            });
            services.AddSingleton<IBillingApiClient, BillingApiClient>();

            // Billing webhook handler processes inbound billing events
            services.AddScoped<IBillingWebhookHandler, BillingWebhookHandler>();
        }
        else
        {
            // No-op: billing calls silently succeed (machine add/delete quantity sync is harmless)
            services.AddSingleton<IBillingApiClient, NoOpBillingApiClient>();
        }

        return services;
    }

    /// <summary>
    /// Registers Hangfire job-type concrete classes for DI activation. Must be called by both
    /// the server (which enqueues) and the worker (which executes) so any caller resolving a
    /// job class — including expression-tree-built Enqueue calls — sees a registered scope.
    /// Feature gating mirrors the original <see cref="AddBackgroundWorkers"/> logic so that
    /// disabled features do not register their job classes.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="billingEnabled">Whether the Billing feature is enabled.</param>
    /// <param name="objectStorageEnabled">Whether object-storage (data export) is enabled.</param>
    public static IServiceCollection AddHangfireJobTypes(
        this IServiceCollection services,
        bool billingEnabled,
        bool objectStorageEnabled)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<RemoteCommandExpiryJob>();
        services.AddScoped<PartitionManagementJob>();
        services.AddScoped<RetentionReclassifyJob>();
        services.AddScoped<HealthSweepTenantJob>();
        services.AddScoped<HealthSweepCoordinatorJob>();
        services.AddScoped<AlertEvaluationJob>();
        services.AddScoped<SshAlertEvaluationJob>();
        services.AddScoped<AlertConditionStateCleanupJob>();
        services.AddScoped<IntegrationDeliveryJob>();
        services.AddScoped<SendInvitationEmailJob>();

        if (objectStorageEnabled)
        {
            services.AddScoped<DataExportProcessingJob>();
            services.AddScoped<DataExportCleanupJob>();
        }

        if (billingEnabled)
        {
            services.AddScoped<StripeSyncJob>();
            services.AddScoped<UsageHeartbeatJob>();
        }

        return services;
    }

    /// <summary>
    /// Registers all background worker hosted services. Call this only in the services-worker process.
    /// Hangfire job-type registrations are now delegated to <see cref="AddHangfireJobTypes"/> so
    /// the same set lands in both the server and worker processes.
    /// </summary>
    public static IServiceCollection AddBackgroundWorkers(
        this IServiceCollection services,
        BillingOptions billingOpts,
        ObjectStorageOptions objectStorageOpts,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        // The telemetry-state projection is sharded by machineId % ShardCount. Each shard is a
        // separate hosted service that takes its own advisory lock and tracks its own high-water
        // mark, so the projection scales across worker replicas instead of a single active consumer.
        // Every replica registers the full shard set; the per-shard lock guarantees exactly one
        // replica actively projects each shard, and a replica that loses a shard's lock falls
        // through to the existing wait-and-retry so shards rebalance on replica loss.
        services.AddOptions<StreamingOptions>().Bind(configuration.GetSection("Streaming"));
        int shardCount = configuration.GetSection("Streaming").Get<StreamingOptions>()?.ShardCount ?? 1;
        if (shardCount < 1)
        {
            shardCount = 1;
        }

        for (int shardIndex = 0; shardIndex < shardCount; shardIndex++)
        {
            int captured = shardIndex;
            services.AddSingleton<IHostedService>(sp => new MachineStateStreamingService(
                sp.GetRequiredService<IServiceScopeFactory>(),
                sp.GetRequiredService<ISqlDialect>(),
                sp.GetRequiredService<IAdvisoryLockProvider>(),
                sp.GetRequiredService<ILogger<MachineStateStreamingService>>(),
                shardIndex: captured,
                streamingOptions: sp.GetRequiredService<IOptions<StreamingOptions>>()));
        }

        services.AddHangfireJobTypes(
            billingEnabled: billingOpts.Enabled,
            objectStorageEnabled: string.IsNullOrEmpty(objectStorageOpts.BucketName) == false);

        return services;
    }

    /// <summary>
    /// Builds a PostgreSQL connection string from the given database options.
    /// </summary>
    public static string BuildConnectionString(DatabaseOptions dbOpts, string applicationName)
    {
        // KeepAlive/TcpKeepAlive ensure Postgres detects dead worker connections within ~1 minute
        // instead of waiting on Linux default tcp_keepalive_time (2 hours). This is critical for
        // PostgresAdvisoryLockProvider — a SIGKILLed/OOM-killed worker holds its advisory lock
        // until Postgres notices the dead TCP connection. See IAdvisoryLockProvider remarks.
        return (new NpgsqlConnectionStringBuilder()
        {
            ApplicationName = applicationName,
            GssEncryptionMode = GssEncryptionMode.Disable,
            Database = dbOpts.Db,
            Username = dbOpts.User,
            Password = dbOpts.Password,
            Host = dbOpts.Hostname,
            MaxPoolSize = dbOpts.MaxPoolSize,
            MinPoolSize = dbOpts.MinPoolSize,
            KeepAlive = 30,
            TcpKeepAlive = true
        }).ConnectionString;
    }
}
