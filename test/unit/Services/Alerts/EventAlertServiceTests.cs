// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Text.Json;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Server.Services.Alerts;
using Framlux.FleetManagement.Server.Services.Billing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Framlux.FleetManagement.Test.Services.Alerts;

/// <summary>
/// Unit tests for <see cref="EventAlertService"/>.
/// </summary>
public sealed class EventAlertServiceTests
{
    private readonly IAlertRuleRepository _alertRuleRepo = Substitute.For<IAlertRuleRepository>();
    private readonly IAlertEventRepository _alertEventRepo = Substitute.For<IAlertEventRepository>();
    private readonly ISubscriptionService _subscriptionService = Substitute.For<ISubscriptionService>();
    private readonly IAlertDeliveryService _deliveryService = Substitute.For<IAlertDeliveryService>();
    private readonly ILogger<EventAlertService> _logger = Substitute.For<ILogger<EventAlertService>>();

    private EventAlertService CreateService()
    {
        ServiceCollection services = new();
        services.AddSingleton(_alertRuleRepo);
        services.AddSingleton(_alertEventRepo);
        services.AddSingleton(_subscriptionService);
        ServiceProvider provider = services.BuildServiceProvider();

        IServiceScopeFactory scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        return new EventAlertService(scopeFactory, _deliveryService, _logger);
    }

    private void SetupActiveProSubscription()
    {
        _subscriptionService.GetSubscriptionForTenantAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new TenantSubscription
            {
                TenantId = 1,
                Tier = SubscriptionTier.Pro,
                Status = SubscriptionStatus.Active,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
    }

    private void SetupFreeSubscription()
    {
        _subscriptionService.GetSubscriptionForTenantAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new TenantSubscription
            {
                TenantId = 1,
                Tier = SubscriptionTier.Free,
                Status = SubscriptionStatus.Active,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
    }

    [Test]
    public async Task EvaluateSshConnect_FreeTier_DoesNotEvaluateRules()
    {
        SetupFreeSubscription();
        EventAlertService service = CreateService();

        await service.EvaluateSshConnectAsync(1, 100, "root", "192.168.1.1", 22, "publickey", CancellationToken.None);

        await _alertRuleRepo.DidNotReceive()
            .GetEnabledRulesForMachineByMetricAsync(Arg.Any<int>(), Arg.Any<long>(), Arg.Any<AlertMetric>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task EvaluateSshConnect_NoMatchingRules_DoesNotCreateEvent()
    {
        SetupActiveProSubscription();
        _alertRuleRepo.GetEnabledRulesForMachineByMetricAsync(1, 100, AlertMetric.SshConnection, Arg.Any<CancellationToken>())
            .Returns(new List<AlertRule>());

        EventAlertService service = CreateService();

        await service.EvaluateSshConnectAsync(1, 100, "root", "192.168.1.1", 22, "publickey", CancellationToken.None);

        await _alertEventRepo.DidNotReceive()
            .CreateEventIfNotExistsAsync(Arg.Any<AlertEvent>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task EvaluateSshConnect_MatchingRule_CreatesEventAndEnqueuesDelivery()
    {
        SetupActiveProSubscription();

        AlertRule rule = new()
        {
            Id = 10,
            TenantId = 1,
            Name = "New SSH connection",
            Metric = AlertMetric.SshConnection,
            Operator = AlertOperator.EqualTo,
            Threshold = 1,
            Severity = AlertSeverity.Info,
            IsEnabled = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        _alertRuleRepo.GetEnabledRulesForMachineByMetricAsync(1, 100, AlertMetric.SshConnection, Arg.Any<CancellationToken>())
            .Returns(new List<AlertRule> { rule });

        AlertEvent? capturedEvent = null;
        _alertEventRepo.CreateEventIfNotExistsAsync(Arg.Any<AlertEvent>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                capturedEvent = callInfo.Arg<AlertEvent>();
                capturedEvent.Id = 42;

                return capturedEvent;
            });

        EventAlertService service = CreateService();

        await service.EvaluateSshConnectAsync(1, 100, "root", "192.168.1.50", 54321, "publickey", CancellationToken.None);

        await _alertEventRepo.Received(1)
            .CreateEventIfNotExistsAsync(Arg.Any<AlertEvent>(), Arg.Any<CancellationToken>());

        await Assert.That(capturedEvent).IsNotNull();
        await Assert.That(capturedEvent!.AlertRuleId).IsEqualTo(10);
        await Assert.That(capturedEvent.MachineId).IsEqualTo(100);
        await Assert.That(capturedEvent.TenantId).IsEqualTo(1);
        await Assert.That(capturedEvent.Severity).IsEqualTo(AlertSeverity.Info);
        await Assert.That(capturedEvent.Status).IsEqualTo(AlertEventStatus.Triggered);
        await Assert.That(capturedEvent.Message).IsEqualTo("New SSH connection: user root from 192.168.1.50 (publickey)");

        await _deliveryService.Received(1)
            .EnqueueAsync(42, 10, 1, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task EvaluateSshConnect_DuplicateEvent_DoesNotEnqueueDelivery()
    {
        SetupActiveProSubscription();

        AlertRule rule = new()
        {
            Id = 10,
            TenantId = 1,
            Name = "New SSH connection",
            Metric = AlertMetric.SshConnection,
            Operator = AlertOperator.EqualTo,
            Threshold = 1,
            Severity = AlertSeverity.Info,
            IsEnabled = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        _alertRuleRepo.GetEnabledRulesForMachineByMetricAsync(1, 100, AlertMetric.SshConnection, Arg.Any<CancellationToken>())
            .Returns(new List<AlertRule> { rule });

        _alertEventRepo.CreateEventIfNotExistsAsync(Arg.Any<AlertEvent>(), Arg.Any<CancellationToken>())
            .Returns((AlertEvent?)null);

        EventAlertService service = CreateService();

        await service.EvaluateSshConnectAsync(1, 100, "root", "192.168.1.50", 54321, "publickey", CancellationToken.None);

        await _deliveryService.DidNotReceive()
            .EnqueueAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ResolveSshDisconnect_ResolvesEventsByMetric()
    {
        EventAlertService service = CreateService();

        await service.ResolveSshDisconnectAsync(100, CancellationToken.None);

        await _alertEventRepo.Received(1)
            .ResolveEventsForMachineByMetricAsync(100, AlertMetric.SshConnection, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task EvaluateSshConnect_NullSubscription_DoesNotEvaluateRules()
    {
        _subscriptionService.GetSubscriptionForTenantAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((TenantSubscription?)null);

        EventAlertService service = CreateService();

        await service.EvaluateSshConnectAsync(1, 100, "root", "192.168.1.1", 22, "publickey", CancellationToken.None);

        await _alertRuleRepo.DidNotReceive()
            .GetEnabledRulesForMachineByMetricAsync(Arg.Any<int>(), Arg.Any<long>(), Arg.Any<AlertMetric>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task EvaluateSshConnect_InactiveSubscription_DoesNotEvaluate()
    {
        _subscriptionService.GetSubscriptionForTenantAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new TenantSubscription
            {
                TenantId = 1,
                Tier = SubscriptionTier.Pro,
                Status = SubscriptionStatus.PastDue,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });

        EventAlertService service = CreateService();

        await service.EvaluateSshConnectAsync(1, 100, "root", "192.168.1.1", 22, "publickey", CancellationToken.None);

        await _alertRuleRepo.DidNotReceive()
            .GetEnabledRulesForMachineByMetricAsync(Arg.Any<int>(), Arg.Any<long>(), Arg.Any<AlertMetric>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task EvaluateSshConnect_MultipleMatchingRules_CreatesAllEvents()
    {
        SetupActiveProSubscription();

        AlertRule ruleOne = new()
        {
            Id = 10,
            TenantId = 1,
            Name = "SSH alert one",
            Metric = AlertMetric.SshConnection,
            Operator = AlertOperator.EqualTo,
            Threshold = 1,
            Severity = AlertSeverity.Info,
            IsEnabled = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        AlertRule ruleTwo = new()
        {
            Id = 20,
            TenantId = 1,
            Name = "SSH alert two",
            Metric = AlertMetric.SshConnection,
            Operator = AlertOperator.EqualTo,
            Threshold = 1,
            Severity = AlertSeverity.Warning,
            IsEnabled = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        _alertRuleRepo.GetEnabledRulesForMachineByMetricAsync(1, 100, AlertMetric.SshConnection, Arg.Any<CancellationToken>())
            .Returns(new List<AlertRule> { ruleOne, ruleTwo });

        long nextId = 50;
        _alertEventRepo.CreateEventIfNotExistsAsync(Arg.Any<AlertEvent>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                AlertEvent evt = callInfo.Arg<AlertEvent>();
                evt.Id = nextId++;

                return evt;
            });

        EventAlertService service = CreateService();

        await service.EvaluateSshConnectAsync(1, 100, "admin", "10.0.0.1", 22, "publickey", CancellationToken.None);

        await _alertEventRepo.Received(2)
            .CreateEventIfNotExistsAsync(Arg.Any<AlertEvent>(), Arg.Any<CancellationToken>());

        await _deliveryService.Received(2)
            .EnqueueAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task EvaluateSshConnect_OneRuleDuplicate_OnlyNewRuleDelivered()
    {
        SetupActiveProSubscription();

        AlertRule ruleOne = new()
        {
            Id = 10,
            TenantId = 1,
            Name = "SSH alert one",
            Metric = AlertMetric.SshConnection,
            Operator = AlertOperator.EqualTo,
            Threshold = 1,
            Severity = AlertSeverity.Info,
            IsEnabled = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        AlertRule ruleTwo = new()
        {
            Id = 20,
            TenantId = 1,
            Name = "SSH alert two",
            Metric = AlertMetric.SshConnection,
            Operator = AlertOperator.EqualTo,
            Threshold = 1,
            Severity = AlertSeverity.Warning,
            IsEnabled = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        _alertRuleRepo.GetEnabledRulesForMachineByMetricAsync(1, 100, AlertMetric.SshConnection, Arg.Any<CancellationToken>())
            .Returns(new List<AlertRule> { ruleOne, ruleTwo });

        // Rule 1 returns null (already active, duplicate), rule 2 returns a new event
        _alertEventRepo.CreateEventIfNotExistsAsync(
            Arg.Is<AlertEvent>(e => e.AlertRuleId == 10), Arg.Any<CancellationToken>())
            .Returns((AlertEvent?)null);

        _alertEventRepo.CreateEventIfNotExistsAsync(
            Arg.Is<AlertEvent>(e => e.AlertRuleId == 20), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                AlertEvent evt = callInfo.Arg<AlertEvent>();
                evt.Id = 99;

                return evt;
            });

        EventAlertService service = CreateService();

        await service.EvaluateSshConnectAsync(1, 100, "admin", "10.0.0.1", 22, "publickey", CancellationToken.None);

        await _deliveryService.Received(1)
            .EnqueueAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());

        // Verify the single delivery was for rule 2's event
        await _deliveryService.Received(1)
            .EnqueueAsync(99, 20, 1, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task EvaluateSshConnect_MessageContainsAllSshDetails()
    {
        SetupActiveProSubscription();

        AlertRule rule = new()
        {
            Id = 10,
            TenantId = 1,
            Name = "SSH watcher",
            Metric = AlertMetric.SshConnection,
            Operator = AlertOperator.EqualTo,
            Threshold = 1,
            Severity = AlertSeverity.Critical,
            IsEnabled = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        _alertRuleRepo.GetEnabledRulesForMachineByMetricAsync(1, 200, AlertMetric.SshConnection, Arg.Any<CancellationToken>())
            .Returns(new List<AlertRule> { rule });

        AlertEvent? capturedEvent = null;
        _alertEventRepo.CreateEventIfNotExistsAsync(Arg.Any<AlertEvent>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                capturedEvent = callInfo.Arg<AlertEvent>();
                capturedEvent.Id = 1;

                return capturedEvent;
            });

        EventAlertService service = CreateService();

        await service.EvaluateSshConnectAsync(1, 200, "deploy", "172.16.0.5", 60022, "publickey", CancellationToken.None);

        await Assert.That(capturedEvent).IsNotNull();

        // Verify the message contains user, sourceIp, and authMethod
        await Assert.That(capturedEvent!.Message).Contains("deploy");
        await Assert.That(capturedEvent.Message).Contains("172.16.0.5");
        await Assert.That(capturedEvent.Message).Contains("publickey");

        // Verify the JSON details contain all four fields
        await Assert.That(capturedEvent.Details).IsNotNull();
        JsonDocument doc = JsonDocument.Parse(capturedEvent.Details!);
        JsonElement root = doc.RootElement;

        await Assert.That(root.GetProperty("user").GetString()).IsEqualTo("deploy");
        await Assert.That(root.GetProperty("sourceIp").GetString()).IsEqualTo("172.16.0.5");
        await Assert.That(root.GetProperty("sourcePort").GetInt32()).IsEqualTo(60022);
        await Assert.That(root.GetProperty("authMethod").GetString()).IsEqualTo("publickey");
    }

    [Test]
    public async Task EvaluateSshConnect_PasswordAuth_CapturedInMessage()
    {
        SetupActiveProSubscription();

        AlertRule rule = new()
        {
            Id = 10,
            TenantId = 1,
            Name = "SSH password alert",
            Metric = AlertMetric.SshConnection,
            Operator = AlertOperator.EqualTo,
            Threshold = 1,
            Severity = AlertSeverity.Warning,
            IsEnabled = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        _alertRuleRepo.GetEnabledRulesForMachineByMetricAsync(1, 100, AlertMetric.SshConnection, Arg.Any<CancellationToken>())
            .Returns(new List<AlertRule> { rule });

        AlertEvent? capturedEvent = null;
        _alertEventRepo.CreateEventIfNotExistsAsync(Arg.Any<AlertEvent>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                capturedEvent = callInfo.Arg<AlertEvent>();
                capturedEvent.Id = 1;

                return capturedEvent;
            });

        EventAlertService service = CreateService();

        await service.EvaluateSshConnectAsync(1, 100, "root", "192.168.1.1", 22, "password", CancellationToken.None);

        await Assert.That(capturedEvent).IsNotNull();
        await Assert.That(capturedEvent!.Message).Contains("(password)");
        await Assert.That(capturedEvent.Message).DoesNotContain("(publickey)");
    }

    [Test]
    public async Task ResolveSshDisconnect_NoActiveEvents_CompletesWithoutError()
    {
        // Configure the repository to complete without error when no events exist
        _alertEventRepo.ResolveEventsForMachineByMetricAsync(999, AlertMetric.SshConnection, Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        EventAlertService service = CreateService();

        // Should complete without throwing
        await service.ResolveSshDisconnectAsync(999, CancellationToken.None);

        await _alertEventRepo.Received(1)
            .ResolveEventsForMachineByMetricAsync(999, AlertMetric.SshConnection, Arg.Any<CancellationToken>());
    }
}
