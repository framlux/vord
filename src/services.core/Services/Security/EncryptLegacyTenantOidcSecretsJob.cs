// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;

namespace Framlux.FleetManagement.Services.Core.Security;

/// <summary>
/// One-time startup task that re-encrypts any <c>TenantOidcConfigurations.ClientSecret</c>
/// values stored in plaintext (legacy, written before encryption was enforced on every
/// write path). Detection is prefix-based via <see cref="IOidcSecretProtector.IsProtected"/>,
/// not exception-based, so corrupt ciphertext or key-ring rotation cannot be silently
/// re-treated as plaintext. Idempotent: rows already carrying the marker are skipped.
/// </summary>
public sealed class EncryptLegacyTenantOidcSecretsJob
{
    private readonly ITenantRepository _tenantRepository;
    private readonly IOidcSecretProtector _secretProtector;
    private readonly ILogger<EncryptLegacyTenantOidcSecretsJob> _logger;

    /// <summary>
    /// Creates a new instance of the <see cref="EncryptLegacyTenantOidcSecretsJob"/> class.
    /// </summary>
    /// <param name="tenantRepository">The tenant repository.</param>
    /// <param name="secretProtector">The OIDC secret protector.</param>
    /// <param name="logger">The logger.</param>
    public EncryptLegacyTenantOidcSecretsJob(
        ITenantRepository tenantRepository,
        IOidcSecretProtector secretProtector,
        ILogger<EncryptLegacyTenantOidcSecretsJob> logger)
    {
        ArgumentNullException.ThrowIfNull(tenantRepository);
        ArgumentNullException.ThrowIfNull(secretProtector);
        ArgumentNullException.ThrowIfNull(logger);
        _tenantRepository = tenantRepository;
        _secretProtector = secretProtector;
        _logger = logger;
    }

    /// <summary>
    /// Scans all OIDC configurations and re-protects any value lacking the marker prefix.
    /// Per-row failures are logged and the loop continues — one bad row must not block the
    /// rest of the migration.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of rows migrated.</returns>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        IReadOnlyList<TenantOidcConfiguration> configs =
            await _tenantRepository.ListAllTenantOidcConfigsAsync(ct);

        int migrated = 0;
        foreach (TenantOidcConfiguration config in configs)
        {
            if (ct.IsCancellationRequested)
            {
                _logger.LogInformation(
                    "Legacy OIDC secret migration cancelled after {Migrated} rows", migrated);

                return migrated;
            }

            if (_secretProtector.IsProtected(config.ClientSecret))
            {
                continue;
            }

            try
            {
                string reProtected = _secretProtector.Protect(config.ClientSecret);
                int updated = await _tenantRepository.UpdateTenantOidcClientSecretAsync(
                    config.TenantId, reProtected, ct);
                if (updated > 0)
                {
                    migrated++;
                    _logger.LogInformation(
                        "Re-encrypted legacy plaintext OIDC client secret for tenant {TenantId}",
                        config.TenantId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to re-encrypt legacy OIDC client secret for tenant {TenantId}; continuing",
                    config.TenantId);
            }
        }

        _logger.LogInformation(
            "Legacy OIDC secret migration completed: {Migrated} rows re-encrypted", migrated);

        return migrated;
    }
}
