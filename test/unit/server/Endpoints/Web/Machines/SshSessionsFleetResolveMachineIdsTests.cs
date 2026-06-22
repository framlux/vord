// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Server.Endpoints.Web.Machines;

namespace Framlux.FleetManagement.Test.Endpoints.Web.Machines;

/// <summary>
/// Unit tests for <see cref="SshSessionsFleetEndpoint.ResolveMachineIds"/>, the search-to-id-set
/// resolution that lets the SSH fleet query push machine-name search down as a SQL predicate.
/// </summary>
public sealed class SshSessionsFleetResolveMachineIdsTests
{
    private static Dictionary<long, string> SampleMachines()
    {
        return new Dictionary<long, string>
        {
            { 1, "web-server-1" },
            { 2, "db-server-1" },
            { 3, "Web-Server-2" },
        };
    }

    [Test]
    public async Task ResolveMachineIds_NullSearch_ReturnsAllMachineIds()
    {
        List<long> result = SshSessionsFleetEndpoint.ResolveMachineIds(SampleMachines(), null);

        await Assert.That(result.Count).IsEqualTo(3);
    }

    [Test]
    public async Task ResolveMachineIds_WhitespaceSearch_ReturnsAllMachineIds()
    {
        List<long> result = SshSessionsFleetEndpoint.ResolveMachineIds(SampleMachines(), "   ");

        await Assert.That(result.Count).IsEqualTo(3);
    }

    [Test]
    public async Task ResolveMachineIds_NameSubstring_ReturnsMatchingIds()
    {
        List<long> result = SshSessionsFleetEndpoint.ResolveMachineIds(SampleMachines(), "web");

        await Assert.That(result.Count).IsEqualTo(2);
        await Assert.That(result.Contains(1)).IsTrue();
        await Assert.That(result.Contains(3)).IsTrue();
    }

    [Test]
    public async Task ResolveMachineIds_CaseInsensitiveMatch()
    {
        List<long> result = SshSessionsFleetEndpoint.ResolveMachineIds(SampleMachines(), "DB-SERVER");

        await Assert.That(result.Count).IsEqualTo(1);
        await Assert.That(result[0]).IsEqualTo(2L);
    }

    [Test]
    public async Task ResolveMachineIds_NoNameMatch_ReturnsEmpty()
    {
        // A term matching only a username (not a machine name) yields no machine IDs.
        List<long> result = SshSessionsFleetEndpoint.ResolveMachineIds(SampleMachines(), "root");

        await Assert.That(result.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ResolveMachineIds_EmptyMap_ReturnsEmpty()
    {
        List<long> result = SshSessionsFleetEndpoint.ResolveMachineIds(new Dictionary<long, string>(), null);

        await Assert.That(result.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ResolveMachineIds_NullMap_Throws()
    {
        await Assert.That(() => SshSessionsFleetEndpoint.ResolveMachineIds(null!, null))
            .Throws<ArgumentNullException>();
    }
}
