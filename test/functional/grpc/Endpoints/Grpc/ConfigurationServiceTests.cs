// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Security.Cryptography;
using System.Text;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Grpc.AgentConfiguration;
using Framlux.FleetManagement.Grpc.AgentRegistration;
using Framlux.FleetManagement.Test.Infrastructure;
using Grpc.Core;
using Grpc.Net.Client;
using LinqToDB.Async;
using LinqToDB;

namespace Framlux.FleetManagement.FunctionalTest.Endpoints.Grpc;

/// <summary>
/// End-to-end functional tests for the control-plane gRPC
/// <see cref="Framlux.FleetManagement.Server.Endpoints.Grpc.ConfigurationService"/>. The agent
/// hits these endpoints on every boot; a regression in the wire contract bricks every fleet
/// machine's startup, so this exercises the full pipeline including API-key auth and tenant
/// isolation.
/// </summary>
public sealed class ConfigurationServiceTests
{
    [Test]
    public async Task GetConfiguration_NoApiKey_ReturnsUnauthenticated()
    {
        using FunctionalTestFactory factory = new();
        using GrpcChannel channel = CreateChannel(factory);
        Configuration.ConfigurationClient client = new(channel);

        RpcException? ex = await Assert.ThrowsAsync<RpcException>(
            async () => await client.GetConfigurationAsync(new GetConfigurationRequest { MachineId = 1 }));

        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.StatusCode).IsEqualTo(StatusCode.Unauthenticated);
    }

    [Test]
    public async Task GetConfiguration_WrongApiKey_ReturnsUnauthenticated()
    {
        using FunctionalTestFactory factory = new();
        using GrpcChannel channel = CreateChannel(factory);
        Configuration.ConfigurationClient client = new(channel);

        Metadata headers = new() { { "x-api-key", "not-a-real-key" } };
        RpcException? ex = await Assert.ThrowsAsync<RpcException>(
            async () => await client.GetConfigurationAsync(
                new GetConfigurationRequest { MachineId = 1 },
                headers: headers));

        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.StatusCode).IsEqualTo(StatusCode.Unauthenticated);
    }

    [Test]
    public async Task GetConfiguration_ValidApiKey_ReturnsConfigPayload()
    {
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        (long machineId, string apiKey) = await RegisterMachineAsync(factory);

        using GrpcChannel channel = CreateChannel(factory);
        Configuration.ConfigurationClient client = new(channel);

        Metadata headers = new() { { "x-api-key", apiKey } };
        GetConfigurationResponse response = await client.GetConfigurationAsync(
            new GetConfigurationRequest { MachineId = machineId },
            headers: headers);

        // Payload shape: every documented field present and within sensible ranges.
        await Assert.That(response.TimeConfig).IsNotNull();
        await Assert.That(response.TimeConfig.HeartbeatTimeInSeconds).IsGreaterThan(0);
        await Assert.That(response.TimeConfig.ConfigurationRefreshTimeInSeconds).IsGreaterThan(0);
        await Assert.That(response.TimeConfig.CommandPollTimeInSeconds).IsGreaterThan(0);
        await Assert.That(response.TimeConfig.TelemetryCollectFastSeconds).IsGreaterThan(0);
        await Assert.That(response.TimeConfig.TelemetryCollectSlowSeconds).IsGreaterThan(0);
        await Assert.That(response.TimeConfig.TelemetrySendFastSeconds).IsGreaterThan(0);
        await Assert.That(response.TimeConfig.TelemetrySendSlowSeconds).IsGreaterThan(0);
        await Assert.That(response.TimeConfig.ServiceStatusSeconds).IsGreaterThan(0);
        await Assert.That(response.TenantId).IsGreaterThan(0);
    }

    [Test]
    public async Task GetConfiguration_ReportedMachineType_CorrectsMachineAndSummary()
    {
        // Machine type is otherwise written only at registration, and an upgraded agent never
        // re-registers, so this is the only route by which a host classified by an older agent can
        // ever be corrected. Both rows must move: the dashboard reads the summary.
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        (long machineId, string apiKey) = await RegisterMachineAsync(factory);

        await db.Machines
            .Where(m => m.Id == machineId)
            .Set(m => m.MachineType, MachineTypes.Unknown)
            .UpdateAsync();
        await db.MachineStateSummaries
            .Where(s => s.MachineId == machineId)
            .Set(s => s.MachineType, (byte)MachineTypes.Unknown)
            .UpdateAsync();

        using GrpcChannel channel = CreateChannel(factory);
        Configuration.ConfigurationClient client = new(channel);

        Metadata headers = new() { { "x-api-key", apiKey } };
        await client.GetConfigurationAsync(
            new GetConfigurationRequest
            {
                MachineId = machineId,
                MachineType = Framlux.FleetManagement.Grpc.AgentRegistration.MachineType.VirtualMachineType,
            },
            headers: headers);

        Machine? stored = await db.Machines.FirstOrDefaultAsync(m => m.Id == machineId);
        MachineStateSummary? summary = await db.MachineStateSummaries.FirstOrDefaultAsync(s => s.MachineId == machineId);

        await Assert.That(stored!.MachineType).IsEqualTo(MachineTypes.VirtualMachine);
        await Assert.That(summary!.MachineType).IsEqualTo((byte)MachineTypes.VirtualMachine);
    }

    [Test]
    public async Task GetConfiguration_UnknownReportedType_DoesNotOverwriteStoredType()
    {
        // Every agent released before the field existed sends the zero value, and a current agent
        // sends it when detection failed. Neither is an assertion that the host is unknown, so a
        // good stored classification must survive.
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        (long machineId, string apiKey) = await RegisterMachineAsync(factory);

        await db.Machines
            .Where(m => m.Id == machineId)
            .Set(m => m.MachineType, MachineTypes.VirtualMachine)
            .UpdateAsync();

        using GrpcChannel channel = CreateChannel(factory);
        Configuration.ConfigurationClient client = new(channel);

        Metadata headers = new() { { "x-api-key", apiKey } };
        await client.GetConfigurationAsync(
            new GetConfigurationRequest { MachineId = machineId },
            headers: headers);

        Machine? stored = await db.Machines.FirstOrDefaultAsync(m => m.Id == machineId);
        await Assert.That(stored!.MachineType).IsEqualTo(MachineTypes.VirtualMachine);
    }

    [Test]
    public async Task GetConfiguration_MachineIdMismatch_ReturnsPermissionDenied()
    {
        using FunctionalTestFactory factory = new();
        (long machineId, string apiKey) = await RegisterMachineAsync(factory);

        using GrpcChannel channel = CreateChannel(factory);
        Configuration.ConfigurationClient client = new(channel);

        Metadata headers = new() { { "x-api-key", apiKey } };
        // Request claims a DIFFERENT machine id than the API key resolves to.
        long impersonatedMachineId = machineId + 9999;
        RpcException? ex = await Assert.ThrowsAsync<RpcException>(
            async () => await client.GetConfigurationAsync(
                new GetConfigurationRequest { MachineId = impersonatedMachineId },
                headers: headers));

        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.StatusCode).IsEqualTo(StatusCode.PermissionDenied);
    }

    [Test]
    public async Task AgentPing_ValidApiKey_ReturnsSuccess()
    {
        using FunctionalTestFactory factory = new();
        (long machineId, string apiKey) = await RegisterMachineAsync(factory);

        using GrpcChannel channel = CreateChannel(factory);
        Configuration.ConfigurationClient client = new(channel);
        Metadata headers = new() { { "x-api-key", apiKey } };

        AgentPingResponse response = await client.AgentPingAsync(
            new AgentPingRequest { MachineId = machineId },
            headers: headers);

        await Assert.That(response.Success).IsTrue();
    }

    [Test]
    public async Task AgentPing_MismatchedMachineId_ReturnsPermissionDenied()
    {
        using FunctionalTestFactory factory = new();
        (long machineId, string apiKey) = await RegisterMachineAsync(factory);

        using GrpcChannel channel = CreateChannel(factory);
        Configuration.ConfigurationClient client = new(channel);
        Metadata headers = new() { { "x-api-key", apiKey } };

        RpcException? ex = await Assert.ThrowsAsync<RpcException>(
            async () => await client.AgentPingAsync(
                new AgentPingRequest { MachineId = machineId + 1 },
                headers: headers));

        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.StatusCode).IsEqualTo(StatusCode.PermissionDenied);
    }

    [Test]
    public async Task GetPendingCommands_ValidApiKey_NoPending_ReturnsEmptyList()
    {
        using FunctionalTestFactory factory = new();
        (long machineId, string apiKey) = await RegisterMachineAsync(factory);

        using GrpcChannel channel = CreateChannel(factory);
        Configuration.ConfigurationClient client = new(channel);
        Metadata headers = new() { { "x-api-key", apiKey } };

        GetPendingCommandsResponse response = await client.GetPendingCommandsAsync(
            new GetPendingCommandsRequest { MachineId = machineId },
            headers: headers);

        await Assert.That(response.Commands.Count).IsEqualTo(0);
    }

    [Test]
    public async Task GetPendingCommands_MismatchedMachineId_ReturnsPermissionDenied()
    {
        using FunctionalTestFactory factory = new();
        (long machineId, string apiKey) = await RegisterMachineAsync(factory);

        using GrpcChannel channel = CreateChannel(factory);
        Configuration.ConfigurationClient client = new(channel);
        Metadata headers = new() { { "x-api-key", apiKey } };

        RpcException? ex = await Assert.ThrowsAsync<RpcException>(
            async () => await client.GetPendingCommandsAsync(
                new GetPendingCommandsRequest { MachineId = machineId + 1 },
                headers: headers));

        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.StatusCode).IsEqualTo(StatusCode.PermissionDenied);
    }

    // ---- helpers ----

    private static async Task<(long machineId, string apiKey)> RegisterMachineAsync(FunctionalTestFactory factory)
    {
        using DatabaseContext db = factory.CreateDbContext();
        int tenantId = await SeedTenant(db);
        await SeedActiveSubscription(db, tenantId);

        const string tokenValue = "test-config-token";
        RegistrationToken token = new()
        {
            TenantId = tenantId,
            TokenHash = ComputeHash(tokenValue),
            Name = "Config test token",
            CreatedByUserId = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
            IsRevoked = false,
        };
        await db.InsertWithIdentityAsync(token);

        using GrpcChannel channel = CreateChannel(factory);
        Registration.RegistrationClient regClient = new(channel);

        RegisterSystemResponse response = await regClient.RegisterSystemAsync(new RegisterSystemRequest
        {
            Hostname = $"config-host-{Guid.NewGuid():N}".Substring(0, 20),
            SerialNumber = $"sn-cfg-{Guid.NewGuid():N}".Substring(0, 20),
            SystemId = $"sys-cfg-{Guid.NewGuid():N}".Substring(0, 20),
            RegistrationToken = tokenValue,
            MachineType = MachineType.BareMetalServerType,
            Os = OperatingSystemType.UbuntuOs,
        });

        return (response.MachineId, response.ApiKey);
    }

    private static GrpcChannel CreateChannel(FunctionalTestFactory factory)
    {
        HttpMessageHandler handler = new ResponseVersionHandler
        {
            InnerHandler = factory.Server.CreateHandler(),
        };

        return GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
        {
            HttpHandler = handler,
        });
    }

    private static async Task<int> SeedTenant(DatabaseContext db)
    {
        Tenant tenant = new()
        {
            Name = $"Cfg Tenant {Guid.NewGuid():N}",
            ExternalId = $"cfg-ext-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = 1,
            IsActive = true,
            LogoUrl = string.Empty,
        };

        return (int)(long)await db.InsertWithIdentityAsync(tenant);
    }

    private static async Task SeedActiveSubscription(DatabaseContext db, int tenantId)
    {
        TenantSubscription subscription = new()
        {
            TenantId = tenantId,
            Tier = SubscriptionTier.Free,
            Status = SubscriptionStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await db.InsertAsync(subscription);
    }

    private static string ComputeHash(string input)
    {
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
    }
}
