// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Server.Endpoints.Grpc;
using Framlux.FleetManagement.Server.Endpoints.Web;
using Framlux.FleetManagement.Server.Endpoints.Web.Machines.History;
using Framlux.FleetManagement.Server.Startup;
using Framlux.FleetManagement.Services.Core.Extensions;
using Framlux.FleetManagement.Services.Core.Hangfire;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Framlux.FleetManagement.Services.Core.Options;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.StackExchangeRedis;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Serilog;
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

builder.Host.AddCoreSerilog();

// Bind shared configuration options
builder.Services.AddCoreOptions(builder.Configuration);

// Server-specific configuration options
builder.Services.AddOptions<AuthCookieOptions>()
    .Bind(builder.Configuration.GetSection("Auth"));

builder.Services.AddOptions<InternalApiOptions>()
    .Bind(builder.Configuration.GetSection("InternalApi"));

builder.Services.AddOptions<AppCorsOptions>()
    .Bind(builder.Configuration.GetSection("Cors"));

builder.Services.AddOptions<AuthenticationProviderOptions>()
    .Bind(builder.Configuration.GetSection("Authentication"));

builder.Services.AddOptions<ForwardedHeadersConfig>()
    .Bind(builder.Configuration.GetSection("ForwardedHeaders"));

builder.Services.AddOptions<TelemetryOptions>()
    .Bind(builder.Configuration.GetSection("Telemetry"))
    .ValidateDataAnnotations()
    .ValidateOnStart();

// Read typed config for use during service registration (before DI container is built)
AppCorsOptions corsOpts = builder.Configuration.GetSection("Cors").Get<AppCorsOptions>() ?? new();
ForwardedHeadersConfig forwardedHeadersOpts = builder.Configuration.GetSection("ForwardedHeaders").Get<ForwardedHeadersConfig>() ?? new();
AuthCookieOptions authCookieOpts = builder.Configuration.GetSection("Auth").Get<AuthCookieOptions>() ?? new();
AuthenticationProviderOptions authProviderOpts = builder.Configuration.GetSection("Authentication").Get<AuthenticationProviderOptions>() ?? new();
DatabaseOptions dbOpts = builder.Configuration.GetSection("Database").Get<DatabaseOptions>()
    ?? throw new InvalidOperationException("Database configuration section is missing.");
RedisOptions redisOpts = builder.Configuration.GetSection("Redis").Get<RedisOptions>()
    ?? throw new InvalidOperationException("Redis configuration section is missing.");
BillingOptions billingOpts = builder.Configuration.GetSection("Billing").Get<BillingOptions>() ?? new();
ObjectStorageOptions objectStorageOpts = builder.Configuration.GetSection("ObjectStorage").Get<ObjectStorageOptions>() ?? new();

string[] corsOrigins = corsOpts.Origins;
// Fail fast on Production misconfiguration (empty list or wildcard).
CorsStartupValidator.Validate(corsOrigins, builder.Environment.EnvironmentName);
// Cookie removed from WithHeaders: it's a forbidden CORS header — browsers never include it in
// preflight allow lists. The cookie itself is automatically sent because AllowCredentials() is set.
builder.Services.AddCors(options => options.AddDefaultPolicy(policyBuilder => policyBuilder
            .WithOrigins(corsOrigins)
            .WithMethods("GET", "POST", "PUT", "DELETE", "OPTIONS")
            .WithHeaders("Content-Type", "Authorization", "x-api-key")
            .AllowCredentials()));
builder.Services.Configure<ForwardedHeadersOptions>(options =>
    ForwardedHeadersStartup.Configure(options, forwardedHeadersOpts, builder.Environment.EnvironmentName));

// SSRF-safe named HttpClients for OIDC discovery and token exchange.
// SsrfSafeSocketsHttpHandler rejects connections to private/reserved IPs at the socket level,
// preventing DNS rebinding attacks regardless of what DNS returned at validation time.
builder.Services.AddHttpClient("OidcDiscovery")
    .ConfigurePrimaryHttpMessageHandler(() => SsrfSafeSocketsHttpHandler.Create());
builder.Services.AddHttpClient("OidcTokenExchange")
    .ConfigurePrimaryHttpMessageHandler(() => SsrfSafeSocketsHttpHandler.Create());
builder.Services.AddHttpClient("IntegrationDelivery", client =>
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

    // Global admin policy. iga-claim presence is delegated to AuthClaims so the value contract
    // (case-insensitive "True") stays consistent across every check site.
    options.AddPolicy("Admin", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireAssertion(ctx => Framlux.FleetManagement.Services.Core.Auth.AuthClaims.IsUserGlobalAdmin(ctx.User));
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

// Shared infrastructure: database, repositories, Redis, Polly, health checks
string connectionString = ServiceCollectionExtensions.BuildConnectionString(dbOpts, "Framlux.FleetManagement.ApiServer");
builder.Services.AddRepositories(dbOpts, "Framlux.FleetManagement.ApiServer");
builder.Services.AddCoreInfrastructure(redisOpts, connectionString);
builder.Services.AddCoreServices(billingOpts, objectStorageOpts);

// Register Hangfire job-type concrete classes in the server too, not just the worker.
// The server enqueues these jobs (e.g. RequestDataExportEndpoint -> DataExportProcessingJob);
// without DI registration here, Hangfire's activator would fall back to Activator.CreateInstance
// — fragile against future scoped-dependency additions. Feature gating mirrors the worker.
builder.Services.AddHangfireJobTypes(
    billingEnabled: billingOpts.Enabled,
    objectStorageEnabled: string.IsNullOrEmpty(objectStorageOpts.BucketName) == false);

// Server-specific handler registrations (have Auth dependencies that stay in server)
builder.Services.AddScoped<Framlux.FleetManagement.Server.Services.Handlers.ITenantOidcHandler, Framlux.FleetManagement.Server.Services.Handlers.TenantOidcHandler>();

// Shared endpoint validators (scoped — depend on scoped repositories)
builder.Services.AddScoped<HistoryRequestValidator>();

// Server-specific: rate limiting (requires Redis from AddCoreInfrastructure)
builder.Services.AddRedisRateLimiting();

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

// ASP.NET Core antiforgery (CSRF). Cookie/header naming and security flags are centralized
// in AntiforgeryStartup.ConfigureOptions so production server and functional tests share the
// same defaults. The antiforgery cookie is signed and encrypted via IDataProtection, whose
// keys come from the Redis-backed ring registered just below, so tokens minted on one
// api-server replica validate on any other replica. Hangfire's dashboard antiforgery uses
// the same IAntiforgery service directly — it does NOT go through FE's middleware, so
// pipeline position of UseAntiforgeryFE has no effect on the dashboard's own form POSTs.
builder.Services.AddAntiforgery(AntiforgeryStartup.ConfigureOptions);

// Data protection for cookie compatibility — keys stored in Redis so the api-server's
// multiple replicas and the services-worker process share the same key ring.
builder.Services.AddCoreDataProtection("Framlux.FleetManagement.Web");

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
app.UseMiddleware<Framlux.FleetManagement.Server.Middleware.SecurityHeadersMiddleware>();
app.UseCors();
app.UseRateLimiter();
app.UseAuthentication()
    .UseAuthorization();

// Hangfire admin dashboard at /admin/hangfire — Admin-only authorization filter is wired
// inside UseHangfireAdminDashboard. Mounted before FastEndpoints so its routes are not
// captured by FastEndpoints' terminal middleware.
app.UseHangfireAdminDashboard();

// Antiforgery middleware. Skip predicate keys on the presence of the auth cookie so callers
// without a session (API key, anonymous) are not gated by a token they cannot mint. See
// AntiforgeryStartup.ShouldSkipAntiforgery for the precise rule.
app.UseAntiforgeryFE(skipRequestFilter: AntiforgeryStartup.ShouldSkipAntiforgery);

app.UseFastEndpoints(options =>
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

            // Global-by-default antiforgery enrollment. See AntiforgeryStartup and
            // AntiforgeryEnrollment for the rule; opt out per-endpoint with [SkipAntiforgery]
            // (every opt-out must also be reviewed and listed in AntiforgeryOptOutAllowlist).
            AntiforgeryStartup.EnableAntiforgeryIfApplicable(ep);
        };
        options.Errors.StatusCode = 400;
        options.Errors.ResponseBuilder = (failures, ctx, statusCode) =>
        {
            List<string> errorMessages = failures
                .Select(f => f.ErrorMessage)
                .ToList();
            string firstMessage = errorMessages.FirstOrDefault() ?? "One or more validation errors occurred.";

            return ApiResponse<object>.Error(firstMessage, errorMessages);
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
