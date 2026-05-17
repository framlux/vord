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
using LinqToDB;
using LinqToDB.Extensions.DependencyInjection;
using LinqToDB.Extensions.Logging;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.StackExchangeRedis;
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
    /// Registers Serilog structured logging with compact JSON output.
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
            .Bind(configuration.GetSection("Hangfire"));

        services.AddOptions<ObjectStorageOptions>()
            .Bind(configuration.GetSection("ObjectStorage"))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<ObjectStorageOptions>, ObjectStorageOptionsValidator>();

        services.AddOptions<ResendOptions>()
            .Bind(configuration.GetSection("Resend"));

        services.AddOptions<AppOptions>()
            .Bind(configuration.GetSection("App"));

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
        string connectionString = (new NpgsqlConnectionStringBuilder()
        {
            ApplicationName = applicationName,
            GssEncryptionMode = GssEncryptionMode.Disable,
            Database = dbOpts.Db,
            Username = dbOpts.User,
            Password = dbOpts.Password,
            Host = dbOpts.Hostname,
            MaxPoolSize = 50,
            MinPoolSize = 5
        }).ConnectionString;

        services.AddNpgsqlDataSource(connectionString);
        services.AddLinqToDBContext<DatabaseContext>((provider, options) => options.UsePostgreSQL(connectionString: connectionString)
                .UseDefaultLogging(provider));

        services.AddScoped<DatabaseRepository>();
        services.AddScoped<IDatabaseTransactionProvider>(sp => sp.GetRequiredService<DatabaseRepository>());
        services.AddScoped<IAuditLogRepository>(sp => sp.GetRequiredService<DatabaseRepository>());
        services.AddScoped<IUserRepository>(sp => sp.GetRequiredService<DatabaseRepository>());
        services.AddScoped<ITenantRepository>(sp => sp.GetRequiredService<DatabaseRepository>());
        services.AddScoped<ISubscriptionRepository>(sp => sp.GetRequiredService<DatabaseRepository>());
        services.AddScoped<IMachineRepository>(sp => sp.GetRequiredService<DatabaseRepository>());
        services.AddScoped<IInvitationRepository>(sp => sp.GetRequiredService<DatabaseRepository>());
        services.AddScoped<ISigningKeyRepository>(sp => sp.GetRequiredService<DatabaseRepository>());
        services.AddScoped<IRemoteCommandRepository>(sp => sp.GetRequiredService<DatabaseRepository>());
        services.AddScoped<IAlertRuleRepository>(sp => sp.GetRequiredService<DatabaseRepository>());
        services.AddScoped<IAlertEventRepository>(sp => sp.GetRequiredService<DatabaseRepository>());
        services.AddScoped<IIntegrationRepository>(sp => sp.GetRequiredService<DatabaseRepository>());
        services.AddScoped<IDataExportRepository>(sp => sp.GetRequiredService<DatabaseRepository>());
        services.AddScoped<IRegistrationTokenRepository>(sp => sp.GetRequiredService<DatabaseRepository>());
        services.AddScoped<IMachineStateRepository>(sp => sp.GetRequiredService<DatabaseRepository>());
        services.AddScoped<IServerConfigurationRepository>(sp => sp.GetRequiredService<DatabaseRepository>());
        services.AddScoped<ITierFeatureLimitRepository>(sp => sp.GetRequiredService<DatabaseRepository>());
        services.AddScoped<ITenantSubscriptionOverrideRepository>(sp => sp.GetRequiredService<DatabaseRepository>());
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
        services.AddSingleton<IDistributedLock, RedisDistributedLock>();

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

        // Health checks for PostgreSQL and Redis
        services.AddHealthChecks()
            .AddNpgSql(postgresConnectionString, name: "postgresql", failureStatus: HealthStatus.Unhealthy)
            .AddRedis(redisOpts.ConnectionString, name: "redis", failureStatus: HealthStatus.Unhealthy);

        return services;
    }

    /// <summary>
    /// Registers ASP.NET Core Data Protection with keys persisted in Redis so multiple
    /// replicas (and processes — api-server and services-worker) share the same key ring.
    /// Must be called after <see cref="AddCoreInfrastructure"/> so that
    /// <see cref="IConnectionMultiplexer"/> is registered.
    /// </summary>
    public static IServiceCollection AddCoreDataProtection(
        this IServiceCollection services,
        string applicationName)
    {
        services.AddDataProtection()
                .SetApplicationName(applicationName);

        services.AddSingleton<IConfigureOptions<KeyManagementOptions>>(sp =>
        {
            IConnectionMultiplexer redis = sp.GetRequiredService<IConnectionMultiplexer>();

            return new ConfigureOptions<KeyManagementOptions>(options =>
            {
                options.XmlRepository = new RedisXmlRepository(
                    () => redis.GetDatabase(), "DataProtection-Keys");
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
                .AddSingleton<IMachineSearchService, MachineSearchService>()
                .AddSingleton<ISqlDialect, PostgresSqlDialect>();

        services.AddSingleton<ServerConfigurationService>();
        services.AddScoped<ISubscriptionService, SubscriptionService>();
        services.AddScoped<IDowngradeGuardService, DowngradeGuardService>();
        services.AddScoped<IDowngradeCleanupService, DowngradeCleanupService>();
        services.AddSingleton<IOidcSecretProtector, OidcSecretProtector>();
        services.AddSingleton<IIntegrationPayloadFormatter, SlackPayloadFormatter>();
        services.AddSingleton<IIntegrationPayloadFormatter, TeamsPayloadFormatter>();
        services.AddSingleton<IIntegrationPayloadFormatter, DiscordPayloadFormatter>();
        services.AddSingleton<IIntegrationPayloadFormatter, PagerDutyPayloadFormatter>();
        services.AddSingleton<IIntegrationPayloadFormatter, CustomPayloadFormatter>();
        services.AddSingleton<IRoleCacheInvalidator, RoleCacheInvalidator>();
        services.AddHttpClient<IEmailService, ResendEmailService>();

        // Handler services for extracted endpoint business logic (scoped to share DatabaseContext)
        services.AddScoped<IInvitationHandler, InvitationHandler>();
        services.AddScoped<IMemberHandler, MemberHandler>();
        services.AddScoped<IOnboardingHandler, OnboardingHandler>();
        services.AddScoped<IMachineHandler, MachineHandler>();
        services.AddScoped<IDashboardHandler, DashboardHandler>();
        services.AddScoped<IAuthMeHandler, AuthMeHandler>();
        services.AddScoped<IAdminHandler, AdminHandler>();
        services.AddScoped<ITenantHandler, TenantHandler>();
        services.AddScoped<IRegistrationTokenHandler, RegistrationTokenHandler>();
        services.AddScoped<IUserHandler, UserHandler>();
        services.AddScoped<IMachineDetailHandler, MachineDetailHandler>();
        services.AddScoped<IDataExportHandler, DataExportHandler>();
        services.AddScoped<ISigningKeyService, SigningKeyService>();
        services.AddScoped<IMachineAuthorizedKeyService, MachineAuthorizedKeyService>();
        services.AddScoped<IRemoteCommandService, RemoteCommandService>();

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
        services.AddSingleton<IBillingStatus, BillingStatus>();

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
    /// Registers all background worker hosted services. Call this only in the services-worker process.
    /// </summary>
    public static IServiceCollection AddBackgroundWorkers(
        this IServiceCollection services,
        BillingOptions billingOpts,
        ObjectStorageOptions objectStorageOpts)
    {
        if (string.IsNullOrEmpty(objectStorageOpts.BucketName) == false)
        {
            services.AddHostedService<DataExportBackgroundService>();
            services.AddHostedService<DataExportCleanupService>();
        }

        services.AddHostedService<AlertEvaluationService>();
        services.AddHostedService<IntegrationDeliveryWorkerService>();
        services.AddHostedService<CommandExpiryBackgroundService>();
        services.AddHostedService<MachineStateStreamingService>();
        services.AddHostedService<HealthSweepService>();
        services.AddHostedService<PartitionManagementService>();

        if (billingOpts.Enabled)
        {
            // Stripe sync background service for reconciliation
            services.AddHostedService<StripeSyncService>();

            // Hourly usage heartbeat for metered billing
            services.AddHostedService<UsageHeartbeatService>();
        }

        return services;
    }

    /// <summary>
    /// Builds a PostgreSQL connection string from the given database options.
    /// </summary>
    public static string BuildConnectionString(DatabaseOptions dbOpts, string applicationName)
    {
        return (new NpgsqlConnectionStringBuilder()
        {
            ApplicationName = applicationName,
            GssEncryptionMode = GssEncryptionMode.Disable,
            Database = dbOpts.Db,
            Username = dbOpts.User,
            Password = dbOpts.Password,
            Host = dbOpts.Hostname,
            MaxPoolSize = 50,
            MinPoolSize = 5
        }).ConnectionString;
    }
}
