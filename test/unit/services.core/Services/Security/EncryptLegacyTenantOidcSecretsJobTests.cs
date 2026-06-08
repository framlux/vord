// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Framlux.FleetManagement.Test.Services.Security;

/// <summary>
/// Tests for <see cref="EncryptLegacyTenantOidcSecretsJob"/>. Verifies legacy plaintext rows
/// are re-encrypted, already-protected rows are skipped, per-row errors do not block the
/// loop, and cancellation is honored mid-iteration.
/// </summary>
public sealed class EncryptLegacyTenantOidcSecretsJobTests
{
    private static OidcSecretProtector CreateProtector()
    {
        return new OidcSecretProtector(new EphemeralDataProtectionProvider());
    }

    private static TenantOidcConfiguration BuildConfig(int tenantId, string secret)
    {
        return new TenantOidcConfiguration
        {
            TenantId = tenantId,
            Authority = "https://idp.example",
            ClientId = $"client-{tenantId}",
            ClientSecret = secret,
            EmailDomain = "example.com",
            IsEnabled = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }

    [Test]
    public async Task RunAsync_PlaintextRow_IsReEncrypted()
    {
        OidcSecretProtector protector = CreateProtector();
        ITenantRepository repo = Substitute.For<ITenantRepository>();
        TenantOidcConfiguration plaintextConfig = BuildConfig(7, "raw-plaintext-secret");
        repo.ListAllTenantOidcConfigsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<TenantOidcConfiguration> { plaintextConfig });
        repo.UpdateTenantOidcClientSecretAsync(7, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(1);
        EncryptLegacyTenantOidcSecretsJob job = new(repo, protector, Substitute.For<ILogger<EncryptLegacyTenantOidcSecretsJob>>());

        int migrated = await job.RunAsync(CancellationToken.None);

        await Assert.That(migrated).IsEqualTo(1);
        await repo.Received(1).UpdateTenantOidcClientSecretAsync(
            7,
            Arg.Is<string>(s => protector.IsProtected(s)),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_AlreadyProtectedRow_IsSkipped()
    {
        OidcSecretProtector protector = CreateProtector();
        ITenantRepository repo = Substitute.For<ITenantRepository>();
        string alreadyProtected = protector.Protect("ok");
        TenantOidcConfiguration protectedConfig = BuildConfig(3, alreadyProtected);
        repo.ListAllTenantOidcConfigsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<TenantOidcConfiguration> { protectedConfig });
        EncryptLegacyTenantOidcSecretsJob job = new(repo, protector, Substitute.For<ILogger<EncryptLegacyTenantOidcSecretsJob>>());

        int migrated = await job.RunAsync(CancellationToken.None);

        await Assert.That(migrated).IsEqualTo(0);
        await repo.DidNotReceive().UpdateTenantOidcClientSecretAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_MixedRows_OnlyPlaintextMigrated()
    {
        OidcSecretProtector protector = CreateProtector();
        ITenantRepository repo = Substitute.For<ITenantRepository>();
        TenantOidcConfiguration plaintext = BuildConfig(1, "plain");
        TenantOidcConfiguration encrypted = BuildConfig(2, protector.Protect("encrypted"));
        TenantOidcConfiguration plaintext2 = BuildConfig(3, "plain-again");
        repo.ListAllTenantOidcConfigsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<TenantOidcConfiguration> { plaintext, encrypted, plaintext2 });
        repo.UpdateTenantOidcClientSecretAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(1);
        EncryptLegacyTenantOidcSecretsJob job = new(repo, protector, Substitute.For<ILogger<EncryptLegacyTenantOidcSecretsJob>>());

        int migrated = await job.RunAsync(CancellationToken.None);

        await Assert.That(migrated).IsEqualTo(2);
        await repo.Received(1).UpdateTenantOidcClientSecretAsync(1, Arg.Any<string>(), Arg.Any<CancellationToken>());
        await repo.DidNotReceive().UpdateTenantOidcClientSecretAsync(2, Arg.Any<string>(), Arg.Any<CancellationToken>());
        await repo.Received(1).UpdateTenantOidcClientSecretAsync(3, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_PerRowUpdateThrows_LoopContinues()
    {
        OidcSecretProtector protector = CreateProtector();
        ITenantRepository repo = Substitute.For<ITenantRepository>();
        TenantOidcConfiguration good = BuildConfig(1, "plain-good");
        TenantOidcConfiguration bad = BuildConfig(2, "plain-bad");
        TenantOidcConfiguration alsoGood = BuildConfig(3, "plain-good-2");
        repo.ListAllTenantOidcConfigsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<TenantOidcConfiguration> { good, bad, alsoGood });
        repo.UpdateTenantOidcClientSecretAsync(2, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("db blew up"));
        repo.UpdateTenantOidcClientSecretAsync(
                Arg.Is<int>(id => (id != 2)),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(1);
        EncryptLegacyTenantOidcSecretsJob job = new(repo, protector, Substitute.For<ILogger<EncryptLegacyTenantOidcSecretsJob>>());

        int migrated = await job.RunAsync(CancellationToken.None);

        await Assert.That(migrated).IsEqualTo(2);
    }

    [Test]
    public async Task RunAsync_CancellationRequested_StopsImmediately()
    {
        OidcSecretProtector protector = CreateProtector();
        ITenantRepository repo = Substitute.For<ITenantRepository>();
        repo.ListAllTenantOidcConfigsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<TenantOidcConfiguration>
            {
                BuildConfig(1, "p1"),
                BuildConfig(2, "p2"),
                BuildConfig(3, "p3"),
            });
        repo.UpdateTenantOidcClientSecretAsync(1, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(1);
        EncryptLegacyTenantOidcSecretsJob job = new(repo, protector, Substitute.For<ILogger<EncryptLegacyTenantOidcSecretsJob>>());

        using CancellationTokenSource cts = new();
        cts.Cancel();

        int migrated = await job.RunAsync(cts.Token);

        await Assert.That(migrated).IsEqualTo(0);
    }

    [Test]
    public async Task RunAsync_EmptyConfigurationList_ReturnsZero()
    {
        OidcSecretProtector protector = CreateProtector();
        ITenantRepository repo = Substitute.For<ITenantRepository>();
        repo.ListAllTenantOidcConfigsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<TenantOidcConfiguration>());
        EncryptLegacyTenantOidcSecretsJob job = new(repo, protector, Substitute.For<ILogger<EncryptLegacyTenantOidcSecretsJob>>());

        int migrated = await job.RunAsync(CancellationToken.None);

        await Assert.That(migrated).IsEqualTo(0);
    }

    [Test]
    public async Task Constructor_NullRepository_Throws()
    {
        ArgumentNullException? ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            EncryptLegacyTenantOidcSecretsJob _ = new(null!, CreateProtector(), Substitute.For<ILogger<EncryptLegacyTenantOidcSecretsJob>>());

            return Task.CompletedTask;
        });
        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.ParamName).IsEqualTo("tenantRepository");
    }

    [Test]
    public async Task Constructor_NullProtector_Throws()
    {
        ArgumentNullException? ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            EncryptLegacyTenantOidcSecretsJob _ = new(Substitute.For<ITenantRepository>(), null!, Substitute.For<ILogger<EncryptLegacyTenantOidcSecretsJob>>());

            return Task.CompletedTask;
        });
        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.ParamName).IsEqualTo("secretProtector");
    }

    [Test]
    public async Task Constructor_NullLogger_Throws()
    {
        ArgumentNullException? ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            EncryptLegacyTenantOidcSecretsJob _ = new(Substitute.For<ITenantRepository>(), CreateProtector(), null!);

            return Task.CompletedTask;
        });
        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.ParamName).IsEqualTo("logger");
    }
}
