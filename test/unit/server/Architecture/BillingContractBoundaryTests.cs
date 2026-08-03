// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Reflection;
using System.Text;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Server.Endpoints.Grpc;
using Framlux.FleetManagement.Services.Core.Billing;

namespace Framlux.FleetManagement.UnitTest.Architecture;

/// <summary>
/// Enforces the boundary around the internal control-plane contract that ships as
/// <c>Framlux.Vord.BillingGrpc</c>.
/// </summary>
/// <remarks>
/// <para>
/// The contract lives in its own project (<c>src/billing-grpc</c>) precisely so that agent-facing
/// code cannot see it. Splitting the project is only half the guarantee: <c>services.core</c> and
/// <c>server</c> are each a single assembly, so the moment either takes a project reference the
/// billing types become visible to every type in that assembly. This test supplies the other half.
/// </para>
/// <para>
/// The rule is deliberately not "billing types stay in billing-named code". The proto declares
/// three services — <c>BillingGateway</c>, <c>BillingManagement</c> and <c>FleetAdmin</c> — so
/// fleet-administration types legitimately appear in code that has nothing to do with billing. The
/// rule is: <b>the internal control-plane contract is referenced only by the server-side endpoints
/// that implement it and by the services.core billing layer that calls it.</b>
/// </para>
/// </remarks>
public sealed class BillingContractBoundaryTests
{
    /// <summary>
    /// The namespace generated from <c>option csharp_namespace</c> in BillingService.proto.
    /// </summary>
    private const string ContractNamespace = "Framlux.Vord.BillingGrpc";

    /// <summary>
    /// Namespaces whose every type may use the contract, because the whole namespace exists to
    /// speak it.
    /// </summary>
    private static readonly HashSet<string> _allowedNamespaces = new(StringComparer.Ordinal)
    {
        // The services.core billing layer: the client side of BillingManagement plus the result
        // shapes it projects the contract into.
        "Framlux.FleetManagement.Services.Core.Billing",

        // The web endpoints that surface that layer's results to the UI. They handle contract
        // enums and messages directly rather than duplicating them.
        "Framlux.FleetManagement.Server.Endpoints.Web.Billing",
    };

    /// <summary>
    /// Individual types that may use the contract even though their namespace may not.
    /// </summary>
    private static readonly HashSet<string> _allowedTypes = new(StringComparer.Ordinal)
    {
        // The two server-side gRPC services that IMPLEMENT the contract. Their namespace is
        // deliberately NOT allowed wholesale: RegistrationService, ConfigurationService and
        // TelemetryService are the agent-facing services and share that namespace. Allowing the
        // namespace would hand the agent surface exactly the visibility this split removes.
        "Framlux.FleetManagement.Server.Endpoints.Grpc.BillingGatewayService",
        "Framlux.FleetManagement.Server.Endpoints.Grpc.FleetAdminService",

        // The composition root has to name the generated gRPC client to register it. Nothing else
        // in the Extensions namespace may touch the contract.
        "Framlux.FleetManagement.Services.Core.Extensions.ServiceCollectionExtensions",
    };

    /// <summary>
    /// No type outside the permitted set may reference the internal control-plane contract.
    /// </summary>
    [Test]
    public async Task InternalControlPlaneContract_IsReferencedOnlyByItsImplementorsAndTheBillingLayer()
    {
        Assembly[] assemblies =
        [
            typeof(BillingGatewayService).Assembly,
            typeof(BillingApiClient).Assembly,
            typeof(DatabaseContext).Assembly,
        ];

        List<string> violations = [];

        foreach (Assembly assembly in assemblies)
        {
            IReadOnlyDictionary<string, SortedSet<string>> referencing =
                AssemblyNamespaceReferenceScanner.FindReferencingTypes(assembly, ContractNamespace);

            foreach (KeyValuePair<string, SortedSet<string>> entry in referencing)
            {
                if (IsPermitted(entry.Key))
                {
                    continue;
                }

                violations.Add($"  {assembly.GetName().Name}: {entry.Key} -> {string.Join(", ", entry.Value)}");
            }
        }

        violations.Sort(StringComparer.Ordinal);

        await Assert.That(violations).IsEmpty().Because(BuildFailureMessage(violations));
    }

    /// <summary>
    /// The permitted set must stay honest: every entry has to name a type that actually exists and
    /// actually uses the contract, so a rename or a removal cannot leave a silent hole behind.
    /// </summary>
    [Test]
    public async Task PermittedSet_ContainsNoStaleEntries()
    {
        Assembly[] assemblies =
        [
            typeof(BillingGatewayService).Assembly,
            typeof(BillingApiClient).Assembly,
            typeof(DatabaseContext).Assembly,
        ];

        HashSet<string> actualUsers = new(StringComparer.Ordinal);
        HashSet<string> actualNamespaces = new(StringComparer.Ordinal);

        foreach (Assembly assembly in assemblies)
        {
            foreach (string user in AssemblyNamespaceReferenceScanner.FindReferencingTypes(assembly, ContractNamespace).Keys)
            {
                actualUsers.Add(user);

                int lastDot = user.LastIndexOf('.');
                if (lastDot > 0)
                {
                    actualNamespaces.Add(user[..lastDot]);
                }
            }
        }

        foreach (string allowed in _allowedTypes)
        {
            await Assert.That(actualUsers.Contains(allowed)).IsTrue()
                .Because($"'{allowed}' is on the permitted list but no longer references {ContractNamespace}; remove it from the list.");
        }

        foreach (string allowed in _allowedNamespaces)
        {
            await Assert.That(actualNamespaces.Contains(allowed)).IsTrue()
                .Because($"namespace '{allowed}' is on the permitted list but no type in it references {ContractNamespace}; remove it from the list.");
        }
    }

    /// <summary>
    /// Determines whether a type is allowed to reference the contract.
    /// </summary>
    /// <param name="typeFullName">The full name of the referencing type.</param>
    /// <returns>True when the type or its namespace is on the permitted list.</returns>
    private static bool IsPermitted(string typeFullName)
    {
        if (_allowedTypes.Contains(typeFullName))
        {
            return true;
        }

        int lastDot = typeFullName.LastIndexOf('.');
        if (lastDot <= 0)
        {
            return false;
        }

        return _allowedNamespaces.Contains(typeFullName[..lastDot]);
    }

    /// <summary>
    /// Builds the explanation shown when the boundary is broken.
    /// </summary>
    /// <param name="violations">The offending types, already formatted and sorted.</param>
    /// <returns>A message that states the rule and how to satisfy it.</returns>
    private static string BuildFailureMessage(IReadOnlyList<string> violations)
    {
        StringBuilder message = new();

        message.AppendLine($"The internal control-plane contract ({ContractNamespace}, from src/billing-grpc) may be");
        message.AppendLine("referenced ONLY by the server-side gRPC services that implement it and by the services.core");
        message.AppendLine("billing layer that calls it. It lives in its own project so that agent-facing code cannot");
        message.AppendLine("reach it; because services.core and server are each a single assembly, that separation is");
        message.AppendLine("only real while this test holds.");
        message.AppendLine();
        message.AppendLine("Permitted types:");

        foreach (string allowed in _allowedTypes.OrderBy(t => t, StringComparer.Ordinal))
        {
            message.AppendLine($"  {allowed}");
        }

        message.AppendLine("Permitted namespaces:");

        foreach (string allowed in _allowedNamespaces.OrderBy(t => t, StringComparer.Ordinal))
        {
            message.AppendLine($"  {allowed}.*");
        }

        message.AppendLine();
        message.AppendLine("These types reach into the contract and are not permitted:");

        foreach (string violation in violations)
        {
            message.AppendLine(violation);
        }

        message.AppendLine();
        message.AppendLine("Fix it by moving the work behind IBillingApiClient (or another abstraction in");
        message.AppendLine("Framlux.FleetManagement.Services.Core.Billing) and exchanging domain types, not contract");
        message.AppendLine("types. Widen the permitted set only for a type that genuinely implements or calls the");
        message.AppendLine("internal control plane.");

        return message.ToString();
    }
}
