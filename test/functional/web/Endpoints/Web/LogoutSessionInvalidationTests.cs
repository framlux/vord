// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Services.Core.Security;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.Extensions.DependencyInjection;

namespace Framlux.FleetManagement.FunctionalTest.Endpoints.Web;

/// <summary>
/// Functional regression tests proving that logging out rotates the user's security stamp,
/// which invalidates every cookie that carried the previous value.
/// </summary>
public sealed class LogoutSessionInvalidationTests
{
    [Test]
    public async Task Logout_AuthenticatedUser_BumpsSecurityStamp()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        UserAccount user = new()
        {
            ExternalId = "ext-logout-stamp",
            Username = "logout-stamp@example.com",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 1,
            IsActive = true,
            IsSystem = false,
            IsGlobalAdmin = false
        };
        user.Id = await db.InsertWithInt32IdentityAsync(user);

        IUserSecurityStampService stampService =
            factory.Services.GetRequiredService<IUserSecurityStampService>();

        // Reading the stamp mints an initial value, mirroring what happens at login.
        string before = await stampService.GetCurrentStampAsync(user.Id, CancellationToken.None);
        await Assert.That(before).IsNotEmpty();

        HttpClient client = new AuthenticatedClientBuilder(factory)
            .WithUserId(user.Id)
            .WithExternalId("ext-logout-stamp")
            .WithEmail("logout-stamp@example.com")
            .Build();

        await client.PostAsync("/api/v1/logout", null);

        string after = await stampService.GetCurrentStampAsync(user.Id, CancellationToken.None);

        await Assert.That(after).IsNotEmpty();
        await Assert.That(after).IsNotEqualTo(before);
    }
}
