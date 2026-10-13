// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Security.Cryptography;
using System.Text;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Grpc.AgentCommon;
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
    public async Task GetConfiguration_ReportedSingleBoardComputer_CorrectsAnUnknownMachineAndSummary()
    {
        // The owner's case: a Raspberry Pi that registered as Unknown with an older agent is
        // corrected by the first configuration fetch of the new one. Both rows must move.
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
                MachineType = Framlux.FleetManagement.Grpc.AgentRegistration.MachineType.SingleBoardComputerType,
            },
            headers: headers);

        Machine? stored = await db.Machines.FirstOrDefaultAsync(m => m.Id == machineId);
        MachineStateSummary? summary = await db.MachineStateSummaries.FirstOrDefaultAsync(s => s.MachineId == machineId);

        await Assert.That(stored!.MachineType).IsEqualTo(MachineTypes.SingleBoardComputer);
        await Assert.That(summary!.MachineType).IsEqualTo((byte)5);
    }

    [Test]
    public async Task GetConfiguration_UnknownReportedType_LeavesAStoredSingleBoardComputerAlone()
    {
        // A board whose agent later loses the signal, for instance by moving into a container that
        // hides the device tree, reports Unknown; that is no opinion and keeps the stored type.
        using FunctionalTestFactory factory = new();
        using DatabaseContext db = factory.CreateDbContext();

        (long machineId, string apiKey) = await RegisterMachineAsync(factory);

        await db.Machines
            .Where(m => m.Id == machineId)
            .Set(m => m.MachineType, MachineTypes.SingleBoardComputer)
            .UpdateAsync();
        await db.MachineStateSummaries
            .Where(s => s.MachineId == machineId)
            .Set(s => s.MachineType, (byte)MachineTypes.SingleBoardComputer)
            .UpdateAsync();

        using GrpcChannel channel = CreateChannel(factory);
        Configuration.ConfigurationClient client = new(channel);

        Metadata headers = new() { { "x-api-key", apiKey } };
        await client.GetConfigurationAsync(
            new GetConfigurationRequest
            {
                MachineId = machineId,
                MachineType = Framlux.FleetManagement.Grpc.AgentRegistration.MachineType.UnknownType,
            },
            headers: headers);

        Machine? stored = await db.Machines.FirstOrDefaultAsync(m => m.Id == machineId);
        MachineStateSummary? summary = await db.MachineStateSummaries.FirstOrDefaultAsync(s => s.MachineId == machineId);

        await Assert.That(stored!.MachineType).IsEqualTo(MachineTypes.SingleBoardComputer);
        await Assert.That(summary!.MachineType).IsEqualTo((byte)5);
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
    public async Task AgentPing_RecordsTheHeartbeatOnTheSummaryRow()
    {
        // The health sweep is SQL over the summary row and cannot read a Redis key, so the heartbeat
        // has to land on the same row the sweep reads or liveness stays blind to the control plane.
        using FunctionalTestFactory factory = new();

        // Drive a real registration so the summary row under test is the one registration produces.
        (long machineId, string apiKey) = await RegisterMachineAsync(factory);

        using GrpcChannel channel = CreateChannel(factory);
        Configuration.ConfigurationClient client = new(channel);
        Metadata headers = new() { { "x-api-key", apiKey } };

        await client.AgentPingAsync(new AgentPingRequest { MachineId = machineId }, headers: headers);

        using DatabaseContext db = factory.CreateDbContext();
        MachineStateSummary? summary = await db.MachineStateSummaries
            .FirstOrDefaultAsync(s => s.MachineId == machineId);

        await Assert.That(summary).IsNotNull();
        await Assert.That(summary!.LastHeartbeatAt).IsNotNull();
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
    public async Task GetPendingCommands_OneRowWithANumericParamAndOneGoodRow_DeliversBothAndMarksBothDelivered()
    {
        // A stored row whose params are not strings used to throw out of the whole poll, so one such row
        // stranded every other command for the machine. Through the real authentication and storage, the
        // poll now answers with both commands, the bad one carrying the number's own text for the agent
        // to reject on binding.
        using FunctionalTestFactory factory = new();
        (long machineId, string apiKey) = await RegisterMachineAsync(factory);
        string badCommandId = Guid.NewGuid().ToString("D");
        string goodCommandId = Guid.NewGuid().ToString("D");
        using (DatabaseContext db = factory.CreateDbContext())
        {
            int tenantId = await db.Machines.Where(m => m.Id == machineId).Select(m => m.TenantId).SingleAsync();
            int userId = await db.InsertWithInt32IdentityAsync(new UserAccount
            {
                ExternalId = $"ext-{Guid.NewGuid():N}",
                Username = $"user-{Guid.NewGuid():N}@example.com",
                CreatedAt = DateTimeOffset.UtcNow,
                CreatedByUserId = 1,
                IsActive = true,
                IsSystem = false,
                IsGlobalAdmin = false,
            });
            int signingKeyId = await db.InsertWithInt32IdentityAsync(new UserSigningKey
            {
                UserId = userId,
                TenantId = tenantId,
                Label = "Test Signing Key",
                PublicKey = Convert.ToBase64String(new byte[32]),
                PublicKeyFingerprint = Guid.NewGuid().ToString("N"),
                CreatedAt = DateTimeOffset.UtcNow,
            });

            foreach ((string commandId, string commandParams) in new[] { (badCommandId, """{"pid":42}"""), (goodCommandId, """{"pid":"42"}""") })
            {
                await db.InsertAsync(new RemoteCommand
                {
                    CommandId = commandId,
                    TenantId = tenantId,
                    MachineId = machineId,
                    UserId = userId,
                    SigningKeyId = signingKeyId,
                    CommandType = "kill_process",
                    Params = commandParams,
                    Nonce = Guid.NewGuid().ToString("N"),
                    Signature = Convert.ToBase64String(new byte[64]),
                    CanonicalPayload = "{}",
                    Timestamp = DateTimeOffset.UtcNow,
                    ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
                    Status = RemoteCommandStatus.Pending,
                    CreatedAt = DateTimeOffset.UtcNow,
                });
            }
        }

        using GrpcChannel channel = CreateChannel(factory);
        Configuration.ConfigurationClient client = new(channel);

        GetPendingCommandsResponse response = await client.GetPendingCommandsAsync(
            new GetPendingCommandsRequest { MachineId = machineId },
            headers: new Metadata { { "x-api-key", apiKey } });

        await Assert.That(response.Commands.Count).IsEqualTo(2);
        AgentCommand bad = response.Commands.Single(c => c.Id == badCommandId);
        AgentCommand good = response.Commands.Single(c => c.Id == goodCommandId);
        await Assert.That(bad.Params["pid"]).IsEqualTo("42");
        await Assert.That(good.Params["pid"]).IsEqualTo("42");
        using DatabaseContext verifyDb = factory.CreateDbContext();
        await Assert.That(await verifyDb.RemoteCommands.CountAsync(c => (c.MachineId == machineId) && (c.Status == RemoteCommandStatus.Pending))).IsEqualTo(0);
    }

    [Test]
    public async Task AgentPing_HostBeyondTheMachineLimit_ReportsPausedAndStillRecordsTheHeartbeat()
    {
        // With a limit of one machine the oldest host is told it is active and the newer one is told it
        // is paused, through the real authentication, subscription and cutoff wiring. The paused host's
        // heartbeat must still land, because it is how a customer tells which paused hosts are alive.
        using FunctionalTestFactory factory = new();
        (int tenantId, List<(long machineId, string apiKey)> machines) = await RegisterMachinesAsync(factory, 2);
        await LimitTenantToOneMachineAsync(factory, tenantId);

        using GrpcChannel channel = CreateChannel(factory);
        Configuration.ConfigurationClient client = new(channel);

        AgentPingResponse keptResponse = await client.AgentPingAsync(
            new AgentPingRequest { MachineId = machines[0].machineId },
            headers: new Metadata { { "x-api-key", machines[0].apiKey } });
        AgentPingResponse pausedResponse = await client.AgentPingAsync(
            new AgentPingRequest { MachineId = machines[1].machineId },
            headers: new Metadata { { "x-api-key", machines[1].apiKey } });

        await Assert.That(keptResponse.Success).IsTrue();
        await Assert.That(keptResponse.State).IsEqualTo(IngestRejectionReason.Unspecified);
        await Assert.That(pausedResponse.Success).IsTrue();
        await Assert.That(pausedResponse.State).IsEqualTo(IngestRejectionReason.HostPaused);

        using DatabaseContext db = factory.CreateDbContext();
        MachineStateSummary? summary = await db.MachineStateSummaries
            .FirstOrDefaultAsync(s => s.MachineId == machines[1].machineId);
        await Assert.That(summary).IsNotNull();
        await Assert.That(summary!.LastHeartbeatAt).IsNotNull();
    }

    [Test]
    public async Task AgentPing_CanceledSubscriptionOnAPausedHost_ReportsTheSubscriptionNotThePause()
    {
        // When both apply the agent must be told the subscription is the problem, because raising the
        // host limit would not make this tenant's telemetry flow again.
        using FunctionalTestFactory factory = new();
        (int tenantId, List<(long machineId, string apiKey)> machines) = await RegisterMachinesAsync(factory, 2);
        await LimitTenantToOneMachineAsync(factory, tenantId);
        using (DatabaseContext db = factory.CreateDbContext())
        {
            await db.TenantSubscriptions
                .Where(s => s.TenantId == tenantId)
                .Set(s => s.Status, SubscriptionStatus.Canceled)
                .UpdateAsync();
        }

        using GrpcChannel channel = CreateChannel(factory);
        Configuration.ConfigurationClient client = new(channel);

        AgentPingResponse response = await client.AgentPingAsync(
            new AgentPingRequest { MachineId = machines[1].machineId },
            headers: new Metadata { { "x-api-key", machines[1].apiKey } });

        await Assert.That(response.Success).IsTrue();
        await Assert.That(response.State).IsEqualTo(IngestRejectionReason.SubscriptionInactive);
    }

    [Test]
    public async Task GetPendingCommands_HostBeyondTheMachineLimit_DeliversNothingAndLeavesTheCommandPending()
    {
        // A paused host is not served commands, and nothing is marked delivered, so the command is
        // still pending for when the host resumes.
        using FunctionalTestFactory factory = new();
        (int tenantId, List<(long machineId, string apiKey)> machines) = await RegisterMachinesAsync(factory, 2);
        await LimitTenantToOneMachineAsync(factory, tenantId);
        string commandId = Guid.NewGuid().ToString("D");
        using (DatabaseContext db = factory.CreateDbContext())
        {
            int userId = await db.InsertWithInt32IdentityAsync(new UserAccount
            {
                ExternalId = $"ext-{Guid.NewGuid():N}",
                Username = $"user-{Guid.NewGuid():N}@example.com",
                CreatedAt = DateTimeOffset.UtcNow,
                CreatedByUserId = 1,
                IsActive = true,
                IsSystem = false,
                IsGlobalAdmin = false,
            });
            int signingKeyId = await db.InsertWithInt32IdentityAsync(new UserSigningKey
            {
                UserId = userId,
                TenantId = tenantId,
                Label = "Test Signing Key",
                PublicKey = Convert.ToBase64String(new byte[32]),
                PublicKeyFingerprint = Guid.NewGuid().ToString("N"),
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.InsertAsync(new RemoteCommand
            {
                CommandId = commandId,
                TenantId = tenantId,
                MachineId = machines[1].machineId,
                UserId = userId,
                SigningKeyId = signingKeyId,
                CommandType = "reboot",
                Params = null,
                Nonce = Guid.NewGuid().ToString("N"),
                Signature = Convert.ToBase64String(new byte[64]),
                CanonicalPayload = "{}",
                Timestamp = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
                Status = RemoteCommandStatus.Pending,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }

        using GrpcChannel channel = CreateChannel(factory);
        Configuration.ConfigurationClient client = new(channel);

        GetPendingCommandsResponse response = await client.GetPendingCommandsAsync(
            new GetPendingCommandsRequest { MachineId = machines[1].machineId },
            headers: new Metadata { { "x-api-key", machines[1].apiKey } });

        await Assert.That(response.Commands.Count).IsEqualTo(0);
        using DatabaseContext verifyDb = factory.CreateDbContext();
        RemoteCommand? stored = await verifyDb.RemoteCommands.FirstOrDefaultAsync(c => c.CommandId == commandId);
        await Assert.That(stored!.Status).IsEqualTo(RemoteCommandStatus.Pending);
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
        (int _, List<(long machineId, string apiKey)> machines) = await RegisterMachinesAsync(factory, 1);

        return machines[0];
    }

    private static async Task<(int tenantId, List<(long machineId, string apiKey)> machines)> RegisterMachinesAsync(
        FunctionalTestFactory factory, int count)
    {
        using DatabaseContext db = factory.CreateDbContext();
        int tenantId = await SeedTenant(db);
        await SeedActiveSubscription(db, tenantId);

        using GrpcChannel channel = CreateChannel(factory);
        Registration.RegistrationClient regClient = new(channel);

        List<(long machineId, string apiKey)> machines = [];
        for (int i = 0; i < count; i++)
        {
            // A registration token is single use, so each machine needs its own.
            string tokenValue = $"test-config-token-{i}";
            RegistrationToken token = new()
            {
                TenantId = tenantId,
                TokenHash = ComputeHash(tokenValue),
                Name = $"Config test token {i}",
                CreatedByUserId = 1,
                CreatedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
                IsRevoked = false,
            };
            await db.InsertWithIdentityAsync(token);

            RegisterSystemResponse response = await regClient.RegisterSystemAsync(new RegisterSystemRequest
            {
                Hostname = $"config-host-{Guid.NewGuid():N}".Substring(0, 20),
                SerialNumber = $"sn-cfg-{Guid.NewGuid():N}".Substring(0, 20),
                SystemId = $"sys-cfg-{Guid.NewGuid():N}".Substring(0, 20),
                RegistrationToken = tokenValue,
                MachineType = MachineType.BareMetalServerType,
                Os = OperatingSystemType.UbuntuOs,
            });
            machines.Add((response.MachineId, response.ApiKey));
        }

        return (tenantId, machines);
    }

    private static async Task LimitTenantToOneMachineAsync(FunctionalTestFactory factory, int tenantId)
    {
        using DatabaseContext db = factory.CreateDbContext();
        await db.InsertAsync(new TenantSubscriptionOverride
        {
            TenantId = tenantId,
            MachineLimit = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
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
