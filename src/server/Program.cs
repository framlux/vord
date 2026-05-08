// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Server.Endpoints.Grpc;
using Framlux.FleetManagement.Server.Endpoints.Web.Machines.History;
using Framlux.FleetManagement.Server.Options;
using Framlux.FleetManagement.Server.Services.Alerts;
using Framlux.FleetManagement.Server.Services.Billing;
using Framlux.FleetManagement.Server.Services.Commands;
using Framlux.FleetManagement.Server.Services.DataExport;
using Framlux.FleetManagement.Server.Services.Handlers;
using Framlux.FleetManagement.Server.Services.Infrastructure;
using Framlux.FleetManagement.Server.Services.Machines;
using Framlux.FleetManagement.Server.Services.Notifications;
using Framlux.FleetManagement.Server.Services.Security;
using Framlux.FleetManagement.Server.Services.ServerConfiguration;
using Framlux.FleetManagement.Server.Services.Telemetry;
using Framlux.Vord.BillingGrpc;
using LinqToDB;
using LinqToDB.Extensions.DependencyInjection;
using LinqToDB.Extensions.Logging;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.StackExchangeRedis;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Npgsql;
using Serilog;
using Serilog.Formatting.Compact;
using StackExchange.Redis;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Host.UseDefaultServiceProvider(options =>
{
    options.ValidateScopes = true;
    options.ValidateOnBuild = true;
});
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 1 * 1024 * 1024; // 1 MB
});
string environment = builder.Environment.EnvironmentName;
builder.Configuration
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddJsonFile($"appsettings.{environment}.json", optional: true, reloadOnChange: true)
    .AddEnvironmentVariables()
    .AddUserSecrets<Program>();

builder.Host.UseSerilog((context, configuration) =>
    configuration
        .ReadFrom.Configuration(context.Configuration)
        .WriteTo.Console(new RenderedCompactJsonFormatter()));

// Bind typed configuration options — all config reads go through IOptions<T> after this point
builder.Services.AddOptions<BillingOptions>()
    .Bind(builder.Configuration.GetSection("Billing"))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<BillingOptions>, BillingOptionsValidator>();

builder.Services.Configure<TierDefaultOptions>(builder.Configuration.GetSection("TierDefaults"));

builder.Services.AddOptions<DatabaseOptions>()
    .Bind(builder.Configuration.GetSection("Database"))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<RedisOptions>()
    .Bind(builder.Configuration.GetSection("Redis"))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<ObjectStorageOptions>()
    .Bind(builder.Configuration.GetSection("ObjectStorage"))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<ObjectStorageOptions>, ObjectStorageOptionsValidator>();

builder.Services.AddOptions<ResendOptions>()
    .Bind(builder.Configuration.GetSection("Resend"));

builder.Services.AddOptions<AppOptions>()
    .Bind(builder.Configuration.GetSection("App"));

builder.Services.AddOptions<AuthCookieOptions>()
    .Bind(builder.Configuration.GetSection("Auth"));

builder.Services.AddOptions<InternalApiOptions>()
    .Bind(builder.Configuration.GetSection("InternalApi"));

builder.Services.AddOptions<AppCorsOptions>()
    .Bind(builder.Configuration.GetSection("Cors"));

builder.Services.AddOptions<AuthenticationProviderOptions>()
    .Bind(builder.Configuration.GetSection("Authentication"));

// Read typed config for use during service registration (before DI container is built)
AppCorsOptions corsOpts = builder.Configuration.GetSection("Cors").Get<AppCorsOptions>() ?? new();
AuthCookieOptions authCookieOpts = builder.Configuration.GetSection("Auth").Get<AuthCookieOptions>() ?? new();
AuthenticationProviderOptions authProviderOpts = builder.Configuration.GetSection("Authentication").Get<AuthenticationProviderOptions>() ?? new();
DatabaseOptions dbOpts = builder.Configuration.GetSection("Database").Get<DatabaseOptions>()
    ?? throw new InvalidOperationException("Database configuration section is missing.");
RedisOptions redisOpts = builder.Configuration.GetSection("Redis").Get<RedisOptions>()
    ?? throw new InvalidOperationException("Redis configuration section is missing.");
BillingOptions billingOpts = builder.Configuration.GetSection("Billing").Get<BillingOptions>() ?? new();
ObjectStorageOptions objectStorageOpts = builder.Configuration.GetSection("ObjectStorage").Get<ObjectStorageOptions>() ?? new();

string[] corsOrigins = corsOpts.Origins;
builder.Services.AddCors(options => options.AddDefaultPolicy(policyBuilder => policyBuilder
            .WithOrigins(corsOrigins)
            .WithMethods("GET", "POST", "PUT", "DELETE", "OPTIONS")
            .WithHeaders("Content-Type", "Authorization", "x-api-key", "Cookie")
            .AllowCredentials()));
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders =
        ForwardedHeaders.XForwardedFor |
        ForwardedHeaders.XForwardedProto |
        ForwardedHeaders.XForwardedHost;

    // NOTE: Trusting all proxies — only safe when deployed behind a known reverse proxy
    // (e.g. Docker/K8s ingress). The server does NOT terminate TLS itself; security headers
    // (HSTS, X-Frame-Options, etc.) are configured at the reverse proxy level.
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

// SSRF-safe named HttpClients for OIDC discovery and token exchange.
// SsrfSafeSocketsHttpHandler rejects connections to private/reserved IPs at the socket level,
// preventing DNS rebinding attacks regardless of what DNS returned at validation time.
builder.Services.AddHttpClient("OidcDiscovery")
    .ConfigurePrimaryHttpMessageHandler(() => SsrfSafeSocketsHttpHandler.Create());
builder.Services.AddHttpClient("OidcTokenExchange")
    .ConfigurePrimaryHttpMessageHandler(() => SsrfSafeSocketsHttpHandler.Create());
builder.Services.AddHttpClient("WebhookDelivery", client =>
    {
        client.Timeout = TimeSpan.FromSeconds(10);
    })
    .ConfigurePrimaryHttpMessageHandler(() => SsrfSafeSocketsHttpHandler.Create());

// Dual authentication: API key for agents, cookie + social OAuth for web users
builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme = "MultiAuth";
    options.DefaultChallengeScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    options.DefaultSignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    options.DefaultSignOutScheme = CookieAuthenticationDefaults.AuthenticationScheme;
})
.AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, null)
.AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, options =>
{
    options.ExpireTimeSpan = TimeSpan.FromDays(7);
    options.SlidingExpiration = true;
    options.LoginPath = "/auth/login";
    options.LogoutPath = "/api/v1/logout";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.Name = "vord_auth";

    string cookieDomain = authCookieOpts.CookieDomain;
    if (string.IsNullOrEmpty(cookieDomain) == false)
    {
        options.Cookie.Domain = cookieDomain;
    }

    options.EventsType = typeof(CookiePrincipalValidator);
})
.AddGitHub("github", options =>
{
    options.ClientId = authProviderOpts.GitHub.ClientId;
    options.ClientSecret = authProviderOpts.GitHub.ClientSecret;
    options.CallbackPath = "/api/auth/callback/github";
    options.Scope.Add("user:email");
    options.Events.OnCreatingTicket = async context =>
    {
        await SocialAuthEvents.OnCreatingTicketAsync(context);
    };
})
.AddGoogle("google", options =>
{
    options.ClientId = authProviderOpts.Google.ClientId;
    options.ClientSecret = authProviderOpts.Google.ClientSecret;
    options.CallbackPath = "/api/auth/callback/google";
    options.Events.OnCreatingTicket = async context =>
    {
        await SocialAuthEvents.OnCreatingTicketAsync(context);
    };
})
.AddMicrosoftAccount("microsoft", options =>
{
    options.ClientId = authProviderOpts.Microsoft.ClientId;
    options.ClientSecret = authProviderOpts.Microsoft.ClientSecret;
    options.CallbackPath = "/api/auth/callback/microsoft";
    options.Events.OnCreatingTicket = async context =>
    {
        await SocialAuthEvents.OnCreatingTicketAsync(context);
    };
})
.AddOpenIdConnect("tenant-oidc", options =>
{
    // Dynamic OIDC for Team-tier custom providers — configured at challenge time
    options.Authority = "https://placeholder.invalid";
    options.ClientId = "placeholder";
    options.ResponseType = OpenIdConnectResponseType.Code;
    options.UsePkce = true;
    options.Scope.Clear();
    options.Scope.Add("openid");
    options.Scope.Add("profile");
    options.Scope.Add("email");
    options.SaveTokens = false;
    options.GetClaimsFromUserInfoEndpoint = true;
    options.CallbackPath = "/api/auth/callback/tenant-oidc";
    options.CorrelationCookie.Path = "/";
    options.NonceCookie.Path = "/";
    options.CorrelationCookie.SameSite = SameSiteMode.None;
    options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.Always;
    options.NonceCookie.SameSite = SameSiteMode.None;
    options.NonceCookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Events = new SsoOidcEvents();
})
.AddPolicyScheme("MultiAuth", "Multi-Auth", options =>
{
    options.ForwardDefaultSelector = context =>
    {
        // If the request has machine API key header, use API key auth
        if (context.Request.Headers.ContainsKey("x-api-key"))
        {
            return ApiKeyAuthenticationHandler.SchemeName;
        }

        // Otherwise use cookie auth for web users
        return CookieAuthenticationDefaults.AuthenticationScheme;
    };
});

// Authorization policies
builder.Services.AddSingleton<IAuthorizationHandler, AllowedRolesHandler>();
builder.Services.AddAuthorization(options =>
{
    // Default policy requires authenticated user
    options.DefaultPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();

    // API key policy for gRPC agents
    options.AddPolicy(ApiKeyAuthenticationHandler.SchemeName, policy =>
    {
        policy.AuthenticationSchemes.Add(ApiKeyAuthenticationHandler.SchemeName);
        policy.RequireAuthenticatedUser();
    });

    // Global admin policy
    options.AddPolicy("Admin", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireClaim("iga", true.ToString());
        policy.AddAuthenticationSchemes(CookieAuthenticationDefaults.AuthenticationScheme);
    });

    // Tenant-level role policies
    options.AddPolicy("TenantAdmin", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.Requirements.Add(new AllowedRolesRequirement(UserAccountRoles.TenantAdmin));
        policy.AddAuthenticationSchemes(CookieAuthenticationDefaults.AuthenticationScheme);
    });
    options.AddPolicy("MachineAdmin", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.Requirements.Add(new AllowedRolesRequirement(UserAccountRoles.MachineAdmin, UserAccountRoles.TenantAdmin));
        policy.AddAuthenticationSchemes(CookieAuthenticationDefaults.AuthenticationScheme);
    });
    options.AddPolicy("ViewOnly", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.Requirements.Add(new AllowedRolesRequirement(UserAccountRoles.Viewer, UserAccountRoles.MachineAdmin, UserAccountRoles.TenantAdmin));
        policy.AddAuthenticationSchemes(CookieAuthenticationDefaults.AuthenticationScheme);
    });
});

string connectionString = (new NpgsqlConnectionStringBuilder()
{
    ApplicationName = "Framlux.FleetManagement.ApiServer",
    GssEncryptionMode = GssEncryptionMode.Disable,
    Database = dbOpts.Db,
    Username = dbOpts.User,
    Password = dbOpts.Password,
    Host = dbOpts.Hostname
}).ConnectionString;

builder.Services.AddNpgsqlDataSource(connectionString);
builder.Services.AddLinqToDBContext<DatabaseContext>((provider, options) => options.UsePostgreSQL(connectionString: connectionString)
        .UseDefaultLogging(provider));

builder.Services.AddScoped<DatabaseRepository>();
builder.Services.AddScoped<IDatabaseTransactionProvider>(sp => sp.GetRequiredService<DatabaseRepository>());
builder.Services.AddScoped<IAuditLogRepository>(sp => sp.GetRequiredService<DatabaseRepository>());
builder.Services.AddScoped<IUserRepository>(sp => sp.GetRequiredService<DatabaseRepository>());
builder.Services.AddScoped<ITenantRepository>(sp => sp.GetRequiredService<DatabaseRepository>());
builder.Services.AddScoped<ISubscriptionRepository>(sp => sp.GetRequiredService<DatabaseRepository>());
builder.Services.AddScoped<IMachineRepository>(sp => sp.GetRequiredService<DatabaseRepository>());
builder.Services.AddScoped<IInvitationRepository>(sp => sp.GetRequiredService<DatabaseRepository>());
builder.Services.AddScoped<ISigningKeyRepository>(sp => sp.GetRequiredService<DatabaseRepository>());
builder.Services.AddScoped<IRemoteCommandRepository>(sp => sp.GetRequiredService<DatabaseRepository>());
builder.Services.AddScoped<IAlertRuleRepository>(sp => sp.GetRequiredService<DatabaseRepository>());
builder.Services.AddScoped<IAlertEventRepository>(sp => sp.GetRequiredService<DatabaseRepository>());
builder.Services.AddScoped<IWebhookRepository>(sp => sp.GetRequiredService<DatabaseRepository>());
builder.Services.AddScoped<IDataExportRepository>(sp => sp.GetRequiredService<DatabaseRepository>());
builder.Services.AddScoped<IRegistrationTokenRepository>(sp => sp.GetRequiredService<DatabaseRepository>());
builder.Services.AddScoped<IMachineStateRepository>(sp => sp.GetRequiredService<DatabaseRepository>());
builder.Services.AddScoped<IServerConfigurationRepository>(sp => sp.GetRequiredService<DatabaseRepository>());
builder.Services.AddScoped<ITierFeatureLimitRepository>(sp => sp.GetRequiredService<DatabaseRepository>());
builder.Services.AddScoped<ITenantSubscriptionOverrideRepository>(sp => sp.GetRequiredService<DatabaseRepository>());
builder.Services.AddSingleton<IServerSettingsCache, ServerSettingsCache>();

builder.Services.AddSingleton<IMachineService, MachineService>()
                .AddSingleton<IMachineStateService, MachineStateService>()
                .AddSingleton<IMachineSearchService, MachineSearchService>()
                .AddSingleton<ISqlDialect, PostgresSqlDialect>();

builder.Services.AddSingleton<ServerConfigurationService>();
builder.Services.AddScoped<ISubscriptionService, SubscriptionService>();
builder.Services.AddScoped<IDowngradeGuardService, DowngradeGuardService>();
builder.Services.AddScoped<IDowngradeCleanupService, DowngradeCleanupService>();
builder.Services.AddSingleton<IOidcSecretProtector, OidcSecretProtector>();
builder.Services.AddSingleton<IWebhookSecretProtector, WebhookSecretProtector>();
builder.Services.AddSingleton<IRoleCacheInvalidator, RoleCacheInvalidator>();
builder.Services.AddHttpClient<IEmailService, ResendEmailService>();

// Shared endpoint validators (scoped — depend on scoped repositories)
builder.Services.AddScoped<HistoryRequestValidator>();

// Handler services for extracted endpoint business logic (scoped to share DatabaseContext)
builder.Services.AddScoped<IInvitationHandler, InvitationHandler>();
builder.Services.AddScoped<IMemberHandler, MemberHandler>();
builder.Services.AddScoped<IOnboardingHandler, OnboardingHandler>();
builder.Services.AddScoped<IMachineHandler, MachineHandler>();
builder.Services.AddScoped<IDashboardHandler, DashboardHandler>();
builder.Services.AddScoped<IAuthMeHandler, AuthMeHandler>();
builder.Services.AddScoped<IAdminHandler, AdminHandler>();
builder.Services.AddScoped<ITenantHandler, TenantHandler>();
builder.Services.AddScoped<IRegistrationTokenHandler, RegistrationTokenHandler>();
builder.Services.AddScoped<IUserHandler, UserHandler>();
builder.Services.AddScoped<IMachineDetailHandler, MachineDetailHandler>();
builder.Services.AddScoped<ITenantOidcHandler, TenantOidcHandler>();
builder.Services.AddScoped<IDataExportHandler, DataExportHandler>();
builder.Services.AddScoped<ISigningKeyService, SigningKeyService>();
builder.Services.AddScoped<IMachineAuthorizedKeyService, MachineAuthorizedKeyService>();
builder.Services.AddScoped<IRemoteCommandService, RemoteCommandService>();
if (string.IsNullOrEmpty(objectStorageOpts.BucketName) == false)
{
    builder.Services.AddSingleton<IObjectStorageService, ObjectStorageService>();
    builder.Services.AddHostedService<DataExportBackgroundService>();
    builder.Services.AddHostedService<DataExportCleanupService>();
}
else
{
    builder.Services.AddSingleton<IObjectStorageService, NoOpObjectStorageService>();
}

builder.Services.AddSingleton<IAlertDeliveryService, AlertDeliveryService>();
builder.Services.AddHostedService<AlertEvaluationService>();
builder.Services.AddHostedService<WebhookDeliveryWorkerService>();
builder.Services.AddHostedService<CommandExpiryBackgroundService>();
builder.Services.AddHostedService<MachineStateStreamingService>();
builder.Services.AddHostedService<HealthSweepService>();
builder.Services.AddHostedService<PartitionManagementService>();

// Billing configuration: explicit opt-in via Billing:Enabled flag
builder.Services.AddSingleton<IBillingStatus, BillingStatus>();

if (billingOpts.Enabled)
{
    // Billing gRPC client for managing Stripe subscriptions
    builder.Services.AddGrpcClient<BillingManagement.BillingManagementClient>(options =>
    {
        options.Address = new Uri(billingOpts.GrpcUrl);
    });
    builder.Services.AddSingleton<IBillingApiClient, BillingApiClient>();

    // Billing webhook handler processes inbound billing events
    builder.Services.AddScoped<IBillingWebhookHandler, BillingWebhookHandler>();

    // Stripe sync background service for reconciliation
    builder.Services.AddHostedService<StripeSyncService>();

    // Hourly usage heartbeat for metered billing
    builder.Services.AddHostedService<UsageHeartbeatService>();
}
else
{
    // No-op: billing calls silently succeed (machine add/delete quantity sync is harmless)
    builder.Services.AddSingleton<IBillingApiClient, NoOpBillingApiClient>();
}

string redisConnectionString = redisOpts.ConnectionString;
builder.Services.AddSingleton<IConnectionMultiplexer>(sp =>
    ConnectionMultiplexer.Connect(redisConnectionString));
builder.Services.AddSingleton<IMachinePingService, RedisMachinePingService>();
builder.Services.AddSingleton<ITelemetryDeduplicationService, RedisTelemetryDeduplicationService>();
builder.Services.AddSingleton<IDistributedLock, RedisDistributedLock>();
builder.Services.AddRedisRateLimiting();

// Health checks for PostgreSQL and Redis
builder.Services.AddHealthChecks()
    .AddNpgSql(connectionString, name: "postgresql", failureStatus: HealthStatus.Unhealthy)
    .AddRedis(redisConnectionString, name: "redis", failureStatus: HealthStatus.Unhealthy);

builder.Services.AddGrpc(options =>
{
    options.MaxReceiveMessageSize = 1 * 1024 * 1024; // 1 MB
    options.Interceptors.Add<GrpcRateLimitingInterceptor>();
});
builder.Services.AddSingleton<GrpcRateLimitingInterceptor>();
builder.Services.AddScoped<CookiePrincipalValidator>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddFastEndpoints();

// Data protection for cookie compatibility — keys stored in Redis for multi-replica support.
// Uses DI-aware configuration so the Redis connection is resolved lazily, allowing tests
// to replace IConnectionMultiplexer before any connection is established.
builder.Services.AddDataProtection()
                .SetApplicationName("Framlux.FleetManagement.Web");
builder.Services.AddSingleton<IConfigureOptions<KeyManagementOptions>>(sp =>
{
    IConnectionMultiplexer redis = sp.GetRequiredService<IConnectionMultiplexer>();

    return new ConfigureOptions<KeyManagementOptions>(options =>
    {
        options.XmlRepository = new RedisXmlRepository(
            () => redis.GetDatabase(), "DataProtection-Keys");
    });
});

WebApplication app = builder.Build();

// Global exception handler — return generic error in non-development environments
if (app.Environment.IsDevelopment() == false)
{
    app.UseExceptionHandler(error => error.Run(async context =>
    {
        context.Response.StatusCode = 500;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync("""{"error":"An unexpected error occurred."}""");
    }));
}

// Request ID correlation for distributed tracing.
app.UseMiddleware<RequestIdMiddleware>();

// Serilog structured request logging — suppress health check noise
app.UseSerilogRequestLogging(options =>
{
    options.GetLevel = (httpContext, elapsed, ex) =>
    {
        if (httpContext.Request.Path.StartsWithSegments("/healthz") ||
            httpContext.Request.Path.StartsWithSegments("/readyz"))
        {
            return Serilog.Events.LogEventLevel.Verbose;
        }

        return Serilog.Events.LogEventLevel.Information;
    };
});

app.UseForwardedHeaders();
app.UseCors();
app.UseRateLimiter();
app.UseAuthentication()
    .UseAuthorization()
    .UseFastEndpoints(options =>
    {
        // Guard against concurrent modification in parallel test hosts — FastEndpoints uses
        // a static JsonSerializerOptions that gets locked after first serialization use.
        if (options.Serializer.Options.IsReadOnly == false)
        {
            options.Serializer.Options.Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
        }

        options.Versioning.PrependToRoute = true;
        options.Versioning.DefaultVersion = 1;
        options.Versioning.Prefix = "api/v";
        options.Endpoints.Configurator = ep =>
        {
            ep.PreProcessor<SubscriptionStatusPreProcessor>(FastEndpoints.Order.Before);
        };
    });

// Health check endpoints
app.MapHealthChecks("/healthz", new HealthCheckOptions
{
    Predicate = _ => false, // Liveness: no dependency checks
    ResponseWriter = HealthCheckResponseWriter.WriteMinimal
});
app.MapHealthChecks("/readyz", new HealthCheckOptions
{
    ResponseWriter = HealthCheckResponseWriter.WriteMinimal
});

app.MapGrpcService<RegistrationService>()
    .RequireAuthorization(ApiKeyAuthenticationHandler.SchemeName);
app.MapGrpcService<ConfigurationService>()
    .RequireAuthorization(ApiKeyAuthenticationHandler.SchemeName);
app.MapGrpcService<TelemetryService>()
    .RequireAuthorization(ApiKeyAuthenticationHandler.SchemeName);
if (billingOpts.Enabled)
{
    app.MapGrpcService<BillingGatewayService>()
        .AllowAnonymous();
    app.MapGrpcService<FleetAdminService>()
        .AllowAnonymous();
}

// Graceful shutdown: allow in-flight requests to complete during Kubernetes rolling updates.
IHostApplicationLifetime lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
lifetime.ApplicationStopping.Register(() =>
{
    Log.Information("Application stopping — waiting for in-flight requests to complete");
});

await app.RunAsync();
