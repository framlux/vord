// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;

namespace Framlux.FleetManagement.Test.Enums;

/// <summary>
/// Verifies that audit-related enum members have stable, exact numeric values.
/// Changing or renumbering these values would silently corrupt existing audit log rows,
/// so these tests act as a regression guard against accidental renaming or reordering.
/// </summary>
public sealed class AuditEnumValuesTests
{
    // ========== AuditAction — new values added for Task C audit coverage ==========

    [Test]
    public async Task AuditAction_ServerConfigurationChanged_HasValue140()
    {
        short actual = (short)AuditAction.ServerConfigurationChanged;
        short expected = 140;

        await Assert.That(actual).IsEqualTo(expected);
    }

    [Test]
    public async Task AuditAction_TenantSubscriptionOverrideChanged_HasValue141()
    {
        short actual = (short)AuditAction.TenantSubscriptionOverrideChanged;
        short expected = 141;

        await Assert.That(actual).IsEqualTo(expected);
    }

    [Test]
    public async Task AuditAction_TenantOidcConfigured_HasValue142()
    {
        short actual = (short)AuditAction.TenantOidcConfigured;
        short expected = 142;

        await Assert.That(actual).IsEqualTo(expected);
    }

    [Test]
    public async Task AuditAction_TenantCreatedByAdmin_HasValue143()
    {
        short actual = (short)AuditAction.TenantCreatedByAdmin;
        short expected = 143;

        await Assert.That(actual).IsEqualTo(expected);
    }

    // ========== AuditResourceType — new values added for Task C audit coverage ==========

    [Test]
    public async Task AuditResourceType_ServerConfiguration_HasValue15()
    {
        short actual = (short)AuditResourceType.ServerConfiguration;
        short expected = 15;

        await Assert.That(actual).IsEqualTo(expected);
    }

    [Test]
    public async Task AuditResourceType_TenantOidcConfig_HasValue16()
    {
        short actual = (short)AuditResourceType.TenantOidcConfig;
        short expected = 16;

        await Assert.That(actual).IsEqualTo(expected);
    }
}
