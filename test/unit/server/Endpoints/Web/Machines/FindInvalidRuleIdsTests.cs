// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Server.Endpoints.Web.Machines;

namespace Framlux.FleetManagement.Test.Endpoints.Web.Machines;

/// <summary>
/// Unit tests for <see cref="MachineAlertRulesUpdateEndpoint.FindInvalidRuleIds"/>, the cross-tenant
/// isolation guard that rejects rule IDs not owned by the caller's tenant.
/// </summary>
public sealed class FindInvalidRuleIdsTests
{
    private static List<AlertRule> TenantRules(params int[] ids)
    {
        List<AlertRule> rules = [];
        foreach (int id in ids)
        {
            rules.Add(new AlertRule
            {
                Id = id,
                TenantId = 1,
                Name = $"rule-{id}",
                Metric = AlertMetric.CpuUsage,
                Operator = AlertOperator.GreaterThan,
                Threshold = 80m,
                Severity = AlertSeverity.Warning,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }

        return rules;
    }

    [Test]
    public async Task AllRequestedBelongToTenant_ReturnsEmpty()
    {
        List<int> invalid = MachineAlertRulesUpdateEndpoint.FindInvalidRuleIds([1, 2], TenantRules(1, 2, 3));

        await Assert.That(invalid.Count).IsEqualTo(0);
    }

    [Test]
    public async Task RequestedRuleNotOwnedByTenant_ReturnedAsInvalid()
    {
        // Rule 99 belongs to another tenant (not in this tenant's rule set).
        List<int> invalid = MachineAlertRulesUpdateEndpoint.FindInvalidRuleIds([1, 99], TenantRules(1, 2));

        await Assert.That(invalid.Count).IsEqualTo(1);
        await Assert.That(invalid[0]).IsEqualTo(99);
    }

    [Test]
    public async Task NoTenantRules_AllRequestedAreInvalid()
    {
        List<int> invalid = MachineAlertRulesUpdateEndpoint.FindInvalidRuleIds([5, 6], TenantRules());

        await Assert.That(invalid.Count).IsEqualTo(2);
    }

    [Test]
    public async Task EmptyRequest_ReturnsEmpty()
    {
        List<int> invalid = MachineAlertRulesUpdateEndpoint.FindInvalidRuleIds([], TenantRules(1, 2));

        await Assert.That(invalid.Count).IsEqualTo(0);
    }

    [Test]
    public async Task NullRequestedIds_Throws()
    {
        await Assert.That(() => MachineAlertRulesUpdateEndpoint.FindInvalidRuleIds(null!, TenantRules(1)))
            .Throws<ArgumentNullException>();
    }

    [Test]
    public async Task NullTenantRules_Throws()
    {
        await Assert.That(() => MachineAlertRulesUpdateEndpoint.FindInvalidRuleIds([1], null!))
            .Throws<ArgumentNullException>();
    }
}
