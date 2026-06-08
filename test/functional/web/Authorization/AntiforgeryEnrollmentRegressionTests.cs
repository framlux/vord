// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Reflection;
using FastEndpoints;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Test.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Core;

namespace Framlux.FleetManagement.FunctionalTest.Authorization;

/// <summary>
/// Per-endpoint regression suite that pins the antiforgery enrollment behavior across the entire
/// FastEndpoints surface. Each endpoint becomes its own parameterized test case so that a failure
/// names the offending endpoint type directly in the test report.
/// <para>
/// Three independent assertions, each in its own test (one behavior per test, FIRST-compliant):
/// <list type="number">
///   <item><c>Endpoint_AntiforgeryEnabledFlag_MatchesEnrollmentRule</c> — for every registered
///         FE endpoint, the runtime <see cref="EndpointDefinition.AntiforgeryEnabled"/> flag
///         matches what <see cref="AntiforgeryEnrollment.ShouldEnforce(EndpointDefinition)"/>
///         would return. Catches a configurator that stops firing OR an endpoint whose metadata
///         doesn't round-trip through FE.</item>
///   <item><c>Endpoint_WithSkipAntiforgeryAttribute_IsOnReviewedAllowlist</c> — every endpoint
///         carrying <see cref="SkipAntiforgeryAttribute"/> has its full type name in
///         <see cref="AntiforgeryOptOutAllowlist.Entries"/>. Makes adding the attribute a
///         deliberate, reviewable change instead of a silent CSRF bypass.</item>
///   <item><c>AllowlistEntry_ResolvesToEndpointThatOptsOut</c> — every allowlist entry still
///         corresponds to an endpoint type that carries the attribute. Catches stale entries
///         left behind when an endpoint is deleted or the attribute is removed.</item>
/// </list>
/// </para>
/// </summary>
public sealed class AntiforgeryEnrollmentRegressionTests
{
    // ----------------------- Data sources -----------------------

    /// <summary>
    /// Statically discovers every FastEndpoints endpoint type in the server assembly at test-
    /// discovery time so each one becomes its own named test case. Returns wrapper funcs (a
    /// TUnit convention for non-trivial parameter types — the engine invokes them per case).
    /// </summary>
    public static IEnumerable<Func<Type>> AllEndpointTypes()
    {
        Assembly serverAssembly = typeof(Program).Assembly;
        Type baseEndpoint = typeof(BaseEndpoint);

        return serverAssembly
            .GetTypes()
            .Where(t => (t.IsAbstract == false) && t.IsClass && baseEndpoint.IsAssignableFrom(t))
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .Select<Type, Func<Type>>(t => () => t);
    }

    /// <summary>
    /// Materializes the antiforgery opt-out allowlist into test cases — one per entry.
    /// </summary>
    public static IEnumerable<Func<string>> AllowlistEntries()
    {
        return AntiforgeryOptOutAllowlist.Entries
            .OrderBy(e => e, StringComparer.Ordinal)
            .Select<string, Func<string>>(e => () => e);
    }

    // ----------------------- Runtime endpoint snapshot -----------------------

    private static readonly Lazy<IReadOnlyDictionary<Type, EndpointDefinition>> _definitions =
        new(BuildDefinitionsSnapshot, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Builds the runtime FastEndpoints host once and snapshots every endpoint's
    /// <see cref="EndpointDefinition"/> keyed by its endpoint type. The snapshot is shared
    /// across all tests in the class so the factory only spins up once for the whole suite.
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

                // FE may register the same endpoint type behind multiple routes (versioning,
                // multi-route). The first wins — all routes share the same EndpointDefinition.
                map.TryAdd(definition.EndpointType, definition);
            }

            return map;
        }
        finally
        {
            // Keep the factory alive for the duration of the test class so the snapshot's
            // EndpointDefinitions remain valid. Disposal happens at process exit.
            _factoryRoot = factory;
        }
    }

    private static FunctionalTestFactory? _factoryRoot;

    // ----------------------- Tests -----------------------

    [Test]
    [MethodDataSource(nameof(AllEndpointTypes))]
    public async Task Endpoint_AntiforgeryEnabledFlag_MatchesEnrollmentRule(Type endpointType)
    {
        if (_definitions.Value.TryGetValue(endpointType, out EndpointDefinition? definition) == false)
        {
            // Endpoint type exists in the assembly but FE did not register it (e.g., a base or
            // helper class that subclasses BaseEndpoint without being discoverable). Skip
            // silently rather than failing — there is no flag to assert.
            return;
        }

        bool expectedEnforcement = AntiforgeryEnrollment.ShouldEnforce(definition);

        await Assert.That(definition.AntiforgeryEnabled).IsEqualTo(expectedEnforcement);
    }

    [Test]
    [MethodDataSource(nameof(AllEndpointTypes))]
    public async Task Endpoint_WithSkipAntiforgeryAttribute_IsOnReviewedAllowlist(Type endpointType)
    {
        if (_definitions.Value.TryGetValue(endpointType, out EndpointDefinition? definition) == false)
        {
            return;
        }

        object[]? attributes = definition.EndpointAttributes;
        bool hasOptOut = (attributes is not null) && attributes.OfType<SkipAntiforgeryAttribute>().Any();

        if (hasOptOut == false)
        {
            return;
        }

        await Assert.That(AntiforgeryOptOutAllowlist.Entries).Contains(endpointType.FullName!);
    }

    [Test]
    [MethodDataSource(nameof(AllowlistEntries))]
    public async Task AllowlistEntry_ResolvesToEndpointTypeThatCarriesSkipAttribute(string fullTypeName)
    {
        Type? endpointType = typeof(Program).Assembly.GetType(fullTypeName);

        await Assert.That(endpointType).IsNotNull();

        Attribute? attribute = endpointType!.GetCustomAttribute(typeof(SkipAntiforgeryAttribute));

        await Assert.That(attribute).IsNotNull();
    }
}
