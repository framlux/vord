// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FluentMigrator.Runner;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Migrations;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Billing;
using Framlux.FleetManagement.Services.Core.Handlers;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Framlux.FleetManagement.Services.Core.Models;
using Framlux.FleetManagement.Services.Core.Security;
using Framlux.FleetManagement.Test.Integration;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Framlux.FleetManagement.Test.Integration.Services.Members;

/// <summary>
/// Live concurrency test against real Postgres (Testcontainers) proving the last-admin guard cannot be
/// defeated by a race: two simultaneous removals of the two last TenantAdmins must not both commit and
/// orphan the tenant. Serializable isolation plus the bounded 40001 retry lets exactly one succeed while
/// the other is rejected with 409.
/// </summary>
public sealed class LastAdminGuardRaceLiveTests
{
    private static PostgresFixture _fixture = default!;
    private static string _migratedConnectionString = default!;

    /// <summary>
    /// Starts the Postgres container once and runs migrations so the schema is ready for all tests.
    /// </summary>
    [Before(Class)]
    public static async Task BeforeClass()
    {
        _fixture = new PostgresFixture();
        await _fixture.InitializeAsync();

        _migratedConnectionString = _fixture.ConnectionString;
        await RunMigrationsAsync(_migratedConnectionString);
    }

    /// <summary>
    /// Stops the Postgres container after all tests in the class.
    /// </summary>
    [After(Class)]
    public static async Task AfterClass()
    {
        await _fixture.DisposeAsync();
    }

    private static async Task RunMigrationsAsync(string connectionString)
    {
        ServiceCollection services = new();
        services
            .AddFluentMigratorCore()
            .ConfigureRunner(rb => rb
                .AddPostgres()
                .WithGlobalConnectionString(connectionString)
                .ScanIn(typeof(InitialMigration).Assembly).For.Migrations())
            .AddLogging(lb => lb.AddDebug().SetMinimumLevel(LogLevel.Warning));

        await using ServiceProvider provider = services.BuildServiceProvider();
        using IServiceScope scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateUp();
    }

    private static DatabaseContext CreateContext()
    {
        DataOptions<DatabaseContext> options = new(
            new DataOptions().UsePostgreSQL(_migratedConnectionString));

        return new DatabaseContext(options);
    }

    // The handler drives its transaction and the guard through the same DatabaseRepository instance so
    // the disable and the admin check share one connection. Audit, subscription, cache, and stamp
    // dependencies are irrelevant to the race and are mocked to no-ops.
    private static MemberHandler CreateHandler(DatabaseContext db)
    {
        DatabaseRepository repo = new(db, NullLogger<DatabaseRepository>.Instance);

        return new MemberHandler(
            repo,
            Substitute.For<IAuditLogRepository>(),
            repo,
            Substitute.For<ISubscriptionService>(),
            Substitute.For<IRoleCacheInvalidator>(),
            Substitute.For<IUserSecurityStampService>());
    }

    private static async Task<int> SeedAdminAsync(DatabaseContext db, int tenantId)
    {
        int userId = await db.InsertWithInt32IdentityAsync(new UserAccount
        {
            ExternalId = Guid.NewGuid().ToString("N"),
            Username = $"admin-{Guid.NewGuid():N}@example.com",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 1,
            IsActive = true,
            IsSystem = false,
            IsGlobalAdmin = false,
            AuthProvider = AuthProviderType.Google,
        });

        await db.InsertAsync(new UserTenantRole
        {
            UserId = userId,
            AssignedTenantId = tenantId,
            Role = UserAccountRoles.TenantAdmin,
            AssignedByUserId = userId,
            AssignedAt = DateTimeOffset.UtcNow,
            IsActive = true,
        });

        return userId;
    }

    [Test]
    public async Task ConcurrentRemovalOfBothLastAdmins_ExactlyOneSucceeds_TheOther409s()
    {
        await using DatabaseContext seedDb = CreateContext();

        int tenantId = await seedDb.InsertWithInt32IdentityAsync(new Tenant
        {
            ExternalId = Guid.NewGuid().ToString("N"),
            Name = $"Race Tenant {Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 1,
            IsActive = true,
            LogoUrl = "",
        });

        int adminA = await SeedAdminAsync(seedDb, tenantId);
        int adminB = await SeedAdminAsync(seedDb, tenantId);

        // Each admin removes the other, concurrently, on independent connections.
        await using DatabaseContext db1 = CreateContext();
        await using DatabaseContext db2 = CreateContext();
        MemberHandler handler1 = CreateHandler(db1);
        MemberHandler handler2 = CreateHandler(db2);

        Task<ServiceResult<ApiResponse<object>>> t1 = handler1.RemoveAsync(adminB, tenantId, adminA, CancellationToken.None);
        Task<ServiceResult<ApiResponse<object>>> t2 = handler2.RemoveAsync(adminA, tenantId, adminB, CancellationToken.None);

        ServiceResult<ApiResponse<object>>[] results = await Task.WhenAll(t1, t2);

        int successes = results.Count(r => r.IsSuccess);
        int conflicts = results.Count(r => r.StatusCode == 409);

        await Assert.That(successes).IsEqualTo(1);
        await Assert.That(conflicts).IsEqualTo(1);

        // The tenant is never orphaned: at least one active TenantAdmin remains.
        int remainingAdmins = await seedDb.UserTenantRoles
            .CountAsync(r => (r.AssignedTenantId == tenantId)
                && (r.Role == UserAccountRoles.TenantAdmin)
                && (r.IsActive == true));

        await Assert.That(remainingAdmins).IsEqualTo(1);
    }
}
