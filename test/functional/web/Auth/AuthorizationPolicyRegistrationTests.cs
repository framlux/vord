// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Reflection;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Test.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace Framlux.FleetManagement.FunctionalTest.Auth;

/// <summary>
/// Pins that every policy name constant is actually registered with the authorization
/// system, so adding a constant without a matching AddPolicy registration fails here
/// instead of surfacing as a runtime 500 on the first request that uses it.
/// </summary>
public sealed class AuthorizationPolicyRegistrationTests
{
    [Test]
    public async Task EveryPolicyConstant_IsRegistered()
    {
        using FunctionalTestFactory factory = new();
        IAuthorizationPolicyProvider provider =
            factory.Services.GetRequiredService<IAuthorizationPolicyProvider>();

        FieldInfo[] constants = typeof(AuthorizationPolicies)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && (f.FieldType == typeof(string)))
            .ToArray();

        await Assert.That(constants.Length).IsEqualTo(4);

        foreach (FieldInfo constant in constants)
        {
            string policyName = (constant.GetRawConstantValue() as string) ?? string.Empty;
            await Assert.That(policyName).IsNotEmpty();

            AuthorizationPolicy? policy = await provider.GetPolicyAsync(policyName);

            await Assert.That(policy).IsNotNull();
        }
    }
}
