// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using Framlux.FleetManagement.Test.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Framlux.FleetManagement.FunctionalTest.Authorization;

/// <summary>
/// Pins the tier guards on the alert-rule surface to the affordance table the web interface
/// renders from, so the two cannot drift apart.
/// </summary>
/// <remarks>
/// <para>
/// The interface must never offer an action the API will refuse. That rule is written twice, in
/// two languages, in two layers: <c>alert-entitlement.ts</c> decides which controls are drawn, and
/// these endpoints decide which requests are honoured. Nothing connects them, so either side can be
/// changed alone — and the failure is silent in both directions. Remove a tag and Free gets a
/// working screen the server no longer refuses; tighten a gate and Pro is shown a control that
/// answers 403.
/// </para>
/// <para>
/// This test states the server half of that table as data and asserts the registered endpoints
/// match it. The web half is asserted in <c>alert-entitlement.test.ts</c> against the same table,
/// written out in the same terms. Neither test can prove the other still agrees — that is what the
/// shared table in both files' comments is for — but each fails loudly the moment its own side
/// moves, which is what makes a drift reviewable instead of invisible.
/// </para>
/// <para>
/// Note what is deliberately NOT tagged. The Team half of the built-in-versus-custom rule is
/// enforced inside the handlers rather than by <see cref="EndpointTags.RequiresTeamSubscription"/>,
/// because those endpoints accept a Pro caller and refuse only the custom rules among their
/// targets. Tagging them Team would refuse Pro outright and take away the enable, disable and
/// assign that Pro is sold. Only create and delete — which exist solely to author custom rules —
/// carry the Team tag.
/// </para>
/// </remarks>
public sealed class AlertRuleTierGuardDriftTests
{
    /// <summary>
    /// One row of the affordance table: an endpoint and the tier gates it must carry. A named type
    /// rather than a tuple because TUnit resolves tuple parameters reflectively, which costs the
    /// suite its AOT compatibility (TUnit0301).
    /// </summary>
    /// <param name="Endpoint">The endpoint type whose registered definition is asserted.</param>
    /// <param name="RequiresPro">Whether it must carry the Pro gate.</param>
    /// <param name="RequiresTeam">Whether it must carry the Team gate.</param>
    public sealed record AlertGuardExpectation(Type Endpoint, bool RequiresPro, bool RequiresTeam);

    /// <summary>
    /// The affordance table, server side. Each entry names an endpoint and the tier gate it must
    /// carry, mirroring a row of the table the interface renders from:
    /// <list type="bullet">
    ///   <item>See rules and coverage — every tier, read-only for Free.</item>
    ///   <item>Enable / disable, assign machines — Pro (built-ins; custom refused in-handler).</item>
    ///   <item>Edit thresholds — Pro gate at the door, Team enforced in-handler per rule.</item>
    ///   <item>Create / delete custom — Pro and Team.</item>
    /// </list>
    /// </summary>
    public static IEnumerable<Func<AlertGuardExpectation>> ExpectedGuards()
    {
        AlertGuardExpectation[] table =
        [
            // Reading is deliberately ungated beyond the tenant scope: a Free tenant holds its
            // built-in rules seeded and disabled, and an invisible alert rule is indistinguishable
            // from no alert rule. This is the row that makes the upgrade case legible.
            new(typeof(AlertRuleListEndpoint), false, false),
            new(typeof(MachineAlertRulesListEndpoint), false, false),

            // The two things Pro may do to a built-in rule.
            new(typeof(AlertRuleEnabledUpdateEndpoint), true, false),
            new(typeof(AlertRuleMachinesUpdateEndpoint), true, false),
            new(typeof(MachineAlertRulesUpdateEndpoint), true, false),

            // Retuning a rule is authoring it, but the gate is Pro at the door because the endpoint
            // also serves the assignment path; the Team requirement is applied per rule inside.
            new(typeof(AlertRuleUpdateEndpoint), true, false),

            // Authoring proper: these exist only for custom rules, so they refuse below Team.
            new(typeof(AlertRuleCreateEndpoint), true, true),
            new(typeof(AlertRuleDeleteEndpoint), true, true),
        ];

        return table.Select<AlertGuardExpectation, Func<AlertGuardExpectation>>(entry => () => entry);
    }

    private static readonly Lazy<IReadOnlyDictionary<Type, EndpointDefinition>> _definitions =
        new(BuildDefinitionsSnapshot, LazyThreadSafetyMode.ExecutionAndPublication);

    private static FunctionalTestFactory? _factoryRoot;

    /// <summary>
    /// Boots the runtime FastEndpoints host once and snapshots every endpoint's
    /// <see cref="EndpointDefinition"/> keyed by endpoint type. Asserting against the registered
    /// definition rather than reading <c>Configure()</c> source is the point: it is the tag the
    /// pre-processor will actually consult at request time.
    /// </summary>
    private static IReadOnlyDictionary<Type, EndpointDefinition> BuildDefinitionsSnapshot()
    {
        FunctionalTestFactory factory = new();
        try
        {
            EndpointDataSource source = factory.Services.GetRequiredService<EndpointDataSource>();
            Dictionary<Type, EndpointDefinition> map = new();

            foreach (Endpoint endpoint in source.Endpoints)
            {
                EndpointDefinition? definition = endpoint.Metadata.GetMetadata<EndpointDefinition>();
                if (definition is null)
                {
                    continue;
                }

                map.TryAdd(definition.EndpointType, definition);
            }

            return map;
        }
        finally
        {
            _factoryRoot = factory;
        }
    }

    private static bool HasTag(EndpointDefinition definition, string tag)
    {
        return definition.EndpointTags?.Contains(tag) == true;
    }

    [Test]
    [MethodDataSource(nameof(ExpectedGuards))]
    public async Task AlertEndpoint_CarriesTheProGateItsAffordanceRowRequires(
        AlertGuardExpectation expected)
    {
        EndpointDefinition definition = _definitions.Value[expected.Endpoint];

        await Assert.That(HasTag(definition, EndpointTags.RequiresProSubscription))
            .IsEqualTo(expected.RequiresPro);
    }

    [Test]
    [MethodDataSource(nameof(ExpectedGuards))]
    public async Task AlertEndpoint_CarriesTheTeamGateItsAffordanceRowRequires(
        AlertGuardExpectation expected)
    {
        EndpointDefinition definition = _definitions.Value[expected.Endpoint];

        await Assert.That(HasTag(definition, EndpointTags.RequiresTeamSubscription))
            .IsEqualTo(expected.RequiresTeam);
    }

    /// <summary>
    /// Every endpoint in the table is actually registered. Without this, deleting or renaming an
    /// endpoint would make its row vanish rather than fail, and the table would quietly stop
    /// describing the surface it claims to pin.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(ExpectedGuards))]
    public async Task AlertEndpoint_IsRegistered(
        AlertGuardExpectation expected)
    {
        await Assert.That(_definitions.Value.ContainsKey(expected.Endpoint)).IsTrue();
    }

    /// <summary>
    /// Nothing in the alert namespace escapes the table. A new write endpoint added beside these
    /// without a row here fails rather than shipping ungated, which is the drift this suite exists
    /// to make impossible.
    /// </summary>
    [Test]
    public async Task EveryAlertRuleEndpoint_AppearsInTheAffordanceTable()
    {
        HashSet<Type> tabled = ExpectedGuards().Select(f => f().Endpoint).ToHashSet();

        List<string> untabled = _definitions.Value.Keys
            .Where(t => t.Namespace == typeof(AlertRuleListEndpoint).Namespace)
            .Where(t => t.Name.Contains("AlertRule", StringComparison.Ordinal))
            .Where(t => tabled.Contains(t) == false)
            .Select(t => t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        await Assert.That(untabled).IsEmpty();
    }
}
