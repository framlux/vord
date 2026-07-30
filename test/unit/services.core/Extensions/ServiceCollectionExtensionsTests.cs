// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Extensions;
using Framlux.FleetManagement.Services.Core.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Npgsql;
using System.Collections.Generic;
using System.Linq;

namespace Framlux.FleetManagement.Test.Services.Extensions;

/// <summary>Tests connection-string construction from DatabaseOptions pool settings.</summary>
public class ServiceCollectionExtensionsTests
{
    [Test]
    public async Task BuildConnectionString_UsesConfiguredPoolSizes()
    {
        DatabaseOptions opts = new()
        {
            Hostname = "db", User = "u", Password = "p", Db = "fleet",
            MaxPoolSize = 120, MinPoolSize = 10,
        };

        string conn = ServiceCollectionExtensions.BuildConnectionString(opts, "worker");
        NpgsqlConnectionStringBuilder parsed = new(conn);

        await Assert.That(parsed.MaxPoolSize).IsEqualTo(120);
        await Assert.That(parsed.MinPoolSize).IsEqualTo(10);
    }

    [Test]
    public async Task BuildConnectionString_UsesDefaultPoolSizes()
    {
        DatabaseOptions opts = new() { Hostname = "db", User = "u", Password = "p", Db = "fleet" };

        NpgsqlConnectionStringBuilder parsed = new(ServiceCollectionExtensions.BuildConnectionString(opts, "api"));

        await Assert.That(parsed.MaxPoolSize).IsEqualTo(50);
        await Assert.That(parsed.MinPoolSize).IsEqualTo(5);
    }

    /// <summary>
    /// A deployment with no Resend API key configured is a valid, deliberate opt-out of email
    /// sending, but it must still be logged at Information: without this line, a typo'd
    /// environment variable name (e.g. RESEND__APIKEY instead of Resend__ApiKey) looks identical
    /// to a deliberate opt-out and every invitation silently completes without ever being sent.
    /// </summary>
    [Test]
    public async Task AddCoreOptions_NoApiKeyConfigured_LogsEmailDisabledInformation()
    {
        ILogger<ResendOptions> logger = Substitute.For<ILogger<ResendOptions>>();
        using ServiceProvider provider = BuildResendProvider(logger);

        ResendOptions resolved = provider.GetRequiredService<IOptions<ResendOptions>>().Value;

        await Assert.That(resolved.ApiKey).IsEmpty();
        logger.Received().Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Is<object>(state => state.ToString()!.Contains("disabled")),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    /// <summary>
    /// A whitespace-only API key must be treated exactly like an absent one: the disabled log
    /// must still fire. This guards against predicate drift from <see cref="ResendOptionsValidator"/>,
    /// which also treats whitespace as "no key configured" — if the two ever used different
    /// emptiness checks, a whitespace key would pass validation as unconfigured while silently
    /// skipping the operator-facing log that explains why email is off.
    /// </summary>
    [Test]
    public async Task AddCoreOptions_WhitespaceApiKey_LogsEmailDisabledInformation()
    {
        ILogger<ResendOptions> logger = Substitute.For<ILogger<ResendOptions>>();
        using ServiceProvider provider = BuildResendProvider(logger, ("Resend:ApiKey", "   "));

        ResendOptions resolved = provider.GetRequiredService<IOptions<ResendOptions>>().Value;

        await Assert.That(resolved.ApiKey).IsEqualTo("   ");
        logger.Received().Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Is<object>(state => state.ToString()!.Contains("disabled")),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    /// <summary>
    /// A real, configured API key is the normal production case: email is enabled and no
    /// "disabled" log line should appear.
    /// </summary>
    [Test]
    public async Task AddCoreOptions_ApiKeyConfigured_DoesNotLogEmailDisabled()
    {
        ILogger<ResendOptions> logger = Substitute.For<ILogger<ResendOptions>>();
        using ServiceProvider provider = BuildResendProvider(
            logger,
            ("Resend:ApiKey", "re_test_key"),
            ("Resend:FromEmail", "Framlux Vord <invitations@outreach.framlux.io>"));

        ResendOptions resolved = provider.GetRequiredService<IOptions<ResendOptions>>().Value;

        await Assert.That(resolved.ApiKey).IsEqualTo("re_test_key");
        logger.DidNotReceive().Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    /// <summary>
    /// Builds a service provider with <see cref="ServiceCollectionExtensions.AddCoreOptions"/>
    /// registered against an in-memory configuration containing only the given Resend keys, and
    /// with the given substitute logger wired in as <see cref="ILogger{ResendOptions}"/> so the
    /// PostConfigure callback's dependency resolves. Only the Resend section is populated: no
    /// other AddCoreOptions-registered option type is ever resolved by these tests, so their
    /// unrelated ValidateOnStart requirements are never triggered.
    /// </summary>
    private static ServiceProvider BuildResendProvider(ILogger<ResendOptions> logger, params (string Key, string Value)[] configValues)
    {
        Dictionary<string, string?> settings = configValues.ToDictionary(kv => kv.Key, kv => (string?)kv.Value);
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        ServiceCollection services = new();
        services.AddCoreOptions(configuration);
        services.AddSingleton(logger);

        return services.BuildServiceProvider();
    }
}
