// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Observability;
using System.Reflection;

namespace Framlux.FleetManagement.Test.Architecture;

/// <summary>
/// Makes the tenant-label allowlist a constraint instead of a convention.
/// </summary>
/// <remarks>
/// A tenant tag multiplies a metric's series count by the customer count, so the set of instruments
/// allowed to carry one is deliberately small and deliberately hard to extend: adding a fifth is a
/// reviewable decision about cardinality, not an edit somebody makes while adding a feature. The
/// rule is enforceable here because call sites pass values rather than tag names, so a metric class
/// is the only place a tenant dimension can be introduced.
/// </remarks>
public sealed class MetricTenantAllowlistTests
{
    private static readonly HashSet<string> PermittedClasses = new(StringComparer.Ordinal)
    {
        "EmailMetrics",
        "IntegrationMetrics",
        "RegistrationMetrics",
        "AuthMetrics",
    };

    private static IEnumerable<Type> MetricClasses()
    {
        return typeof(VordMeter).Assembly
            .GetTypes()
            .Where(type => type.Namespace == typeof(VordMeter).Namespace)
            .Where(type => type.IsClass == true)
            .Where(type => type.Name.EndsWith("Metrics", StringComparison.Ordinal) == true);
    }

    private static bool TakesTenant(MethodInfo method)
    {
        return method.GetParameters()
            .Any(parameter => parameter.Name?.Contains("tenant", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Test]
    public async Task OnlyAllowlistedClassesAcceptATenantParameter()
    {
        List<string> offenders = [];

        foreach (Type type in MetricClasses())
        {
            if (PermittedClasses.Contains(type.Name) == true)
            {
                continue;
            }

            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (TakesTenant(method) == true)
                {
                    offenders.Add($"{type.Name}.{method.Name}");
                }
            }
        }

        await Assert.That(offenders).IsEmpty();
    }

    [Test]
    public async Task EveryAllowlistedClassStillUsesItsTenantParameter()
    {
        // Stops the allowlist rotting into a blanket exemption: an entry that no longer carries a
        // tenant must be removed, mirroring how the billing contract boundary polices its own
        // permitted set. Classes that do not exist yet are skipped, so the rule tightens as they land.
        List<string> stale = [];

        foreach (string permitted in PermittedClasses)
        {
            Type? type = MetricClasses().FirstOrDefault(candidate => candidate.Name == permitted);
            if (type is null)
            {
                continue;
            }

            bool usesTenant = type.GetMethods(BindingFlags.Public | BindingFlags.Instance).Any(TakesTenant);
            if (usesTenant == false)
            {
                stale.Add(permitted);
            }
        }

        await Assert.That(stale).IsEmpty();
    }

    [Test]
    public async Task TheScanActuallyFindsTheMetricClassesItIsPolicing()
    {
        // A reflection filter that matches nothing passes both rules above while enforcing nothing.
        // This is the guard against the guard silently going blind.
        List<Type> discovered = MetricClasses().ToList();

        await Assert.That(discovered).IsNotEmpty();
        await Assert.That(discovered.Any(type => type == typeof(IngestMetrics))).IsTrue();
    }
}
