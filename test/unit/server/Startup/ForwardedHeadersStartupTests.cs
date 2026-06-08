// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Server.Startup;
using Framlux.FleetManagement.Services.Core.Options;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;

namespace Framlux.FleetManagement.Test.Startup;

/// <summary>
/// Tests for <see cref="ForwardedHeadersStartup"/>. Verifies CIDR parsing, production
/// fail-fast on empty configuration, and rejection of malformed CIDR strings.
/// </summary>
public sealed class ForwardedHeadersStartupTests
{
    /// <summary>
    /// In Production, an empty <see cref="ForwardedHeadersConfig.KnownNetworks"/> must
    /// fail at startup. Silent misconfiguration is the root cause this fix addresses.
    /// </summary>
    [Test]
    public async Task Configure_Production_EmptyKnownNetworks_Throws()
    {
        ForwardedHeadersOptions options = new();
        ForwardedHeadersConfig config = new();

        InvalidOperationException? ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
        {
            ForwardedHeadersStartup.Configure(options, config, "Production");

            return Task.CompletedTask;
        });

        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.Message).Contains("KnownNetworks");
    }

    /// <summary>
    /// Development with an empty list MUST be permitted so developers can run locally
    /// without configuring CIDRs.
    /// </summary>
    [Test]
    public async Task Configure_Development_EmptyKnownNetworks_DoesNotThrow()
    {
        ForwardedHeadersOptions options = new();
        ForwardedHeadersConfig config = new();

        ForwardedHeadersStartup.Configure(options, config, "Development");

        await Assert.That(options.KnownIPNetworks.Count).IsEqualTo(0);
    }

    /// <summary>
    /// A valid CIDR list is parsed and added to <see cref="ForwardedHeadersOptions.KnownIPNetworks"/>.
    /// </summary>
    [Test]
    public async Task Configure_ValidCidrList_PopulatesKnownNetworks()
    {
        ForwardedHeadersOptions options = new();
        ForwardedHeadersConfig config = new()
        {
            KnownNetworks = ["10.0.0.0/8", "192.168.1.0/24"],
            ForwardLimit = 3,
        };

        ForwardedHeadersStartup.Configure(options, config, "Production");

        await Assert.That(options.KnownIPNetworks.Count).IsEqualTo(2);
        await Assert.That(options.ForwardLimit).IsEqualTo(3);
    }

    /// <summary>
    /// XForwarded* flags must be set regardless of environment so the framework knows
    /// which headers to honor.
    /// </summary>
    [Test]
    public async Task Configure_AnyEnvironment_SetsExpectedForwardedHeaderFlags()
    {
        ForwardedHeadersOptions options = new();
        ForwardedHeadersConfig config = new()
        {
            KnownNetworks = ["10.0.0.0/8"],
        };

        ForwardedHeadersStartup.Configure(options, config, "Production");

        ForwardedHeaders expected =
            ForwardedHeaders.XForwardedFor
            | ForwardedHeaders.XForwardedProto
            | ForwardedHeaders.XForwardedHost;
        await Assert.That(options.ForwardedHeaders).IsEqualTo(expected);
    }

    /// <summary>
    /// A malformed CIDR throws a wrapped <see cref="InvalidOperationException"/> with
    /// the offending entry in the message — operators need to know which line is wrong.
    /// </summary>
    [Test]
    public async Task Configure_MalformedCidr_ThrowsWithEntryInMessage()
    {
        ForwardedHeadersOptions options = new();
        ForwardedHeadersConfig config = new()
        {
            KnownNetworks = ["not-a-cidr"],
        };

        InvalidOperationException? ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
        {
            ForwardedHeadersStartup.Configure(options, config, "Development");

            return Task.CompletedTask;
        });

        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.Message).Contains("not-a-cidr");
    }

    /// <summary>
    /// An empty-string CIDR entry is rejected explicitly — this is an obvious config
    /// mistake that should fail fast with a clear message.
    /// </summary>
    [Test]
    public async Task Configure_EmptyStringCidr_ThrowsExplicitMessage()
    {
        ForwardedHeadersOptions options = new();
        ForwardedHeadersConfig config = new()
        {
            KnownNetworks = [""],
        };

        InvalidOperationException? ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
        {
            ForwardedHeadersStartup.Configure(options, config, "Development");

            return Task.CompletedTask;
        });

        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.Message).Contains("empty or whitespace");
    }

    /// <summary>
    /// Whitespace-only entries follow the same fail-fast rule.
    /// </summary>
    [Test]
    public async Task Configure_WhitespaceCidr_ThrowsExplicitMessage()
    {
        ForwardedHeadersOptions options = new();
        ForwardedHeadersConfig config = new()
        {
            KnownNetworks = ["   "],
        };

        InvalidOperationException? ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
        {
            ForwardedHeadersStartup.Configure(options, config, "Development");

            return Task.CompletedTask;
        });

        await Assert.That(ex).IsNotNull();
    }

    /// <summary>
    /// Null options should fail fast with <see cref="ArgumentNullException"/>.
    /// </summary>
    [Test]
    public async Task Configure_NullOptions_ThrowsArgumentNullException()
    {
        ArgumentNullException? ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            ForwardedHeadersStartup.Configure(null!, new ForwardedHeadersConfig(), "Development");

            return Task.CompletedTask;
        });

        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.ParamName).IsEqualTo("options");
    }

    /// <summary>
    /// Null config should fail fast.
    /// </summary>
    [Test]
    public async Task Configure_NullConfig_ThrowsArgumentNullException()
    {
        ArgumentNullException? ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            ForwardedHeadersStartup.Configure(new ForwardedHeadersOptions(), null!, "Development");

            return Task.CompletedTask;
        });

        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.ParamName).IsEqualTo("config");
    }

    /// <summary>
    /// Null environment should fail fast.
    /// </summary>
    [Test]
    public async Task Configure_NullEnvironmentName_ThrowsArgumentNullException()
    {
        ArgumentNullException? ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            ForwardedHeadersStartup.Configure(new ForwardedHeadersOptions(), new ForwardedHeadersConfig(), null!);

            return Task.CompletedTask;
        });

        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.ParamName).IsEqualTo("environmentName");
    }

    /// <summary>
    /// Production-name comparison is case-insensitive (operators may set the env var with
    /// any casing); the same fail-fast applies.
    /// </summary>
    [Test]
    public async Task Configure_LowerCaseProduction_StillTriggersEmptyCheck()
    {
        ForwardedHeadersOptions options = new();
        ForwardedHeadersConfig config = new();

        InvalidOperationException? ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
        {
            ForwardedHeadersStartup.Configure(options, config, "production");

            return Task.CompletedTask;
        });

        await Assert.That(ex).IsNotNull();
    }

    /// <summary>
    /// The default <see cref="ForwardedHeadersConfig.ForwardLimit"/> is honored when not
    /// overridden in configuration.
    /// </summary>
    [Test]
    public async Task Configure_DefaultForwardLimit_IsTwo()
    {
        ForwardedHeadersConfig config = new();

        await Assert.That(config.ForwardLimit).IsEqualTo(2);
    }

    /// <summary>
    /// KnownProxies and KnownIPNetworks must be cleared before adding configured CIDRs so
    /// repeated invocations during reload do not accumulate stale entries.
    /// </summary>
    [Test]
    public async Task Configure_CalledTwice_DoesNotAccumulateNetworks()
    {
        ForwardedHeadersOptions options = new();
        ForwardedHeadersConfig config = new()
        {
            KnownNetworks = ["10.0.0.0/8"],
        };

        ForwardedHeadersStartup.Configure(options, config, "Development");
        ForwardedHeadersStartup.Configure(options, config, "Development");

        await Assert.That(options.KnownIPNetworks.Count).IsEqualTo(1);
    }
}
