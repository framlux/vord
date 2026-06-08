// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Services.Core.Options;
using Grpc.Core;
using Grpc.Core.Testing;

namespace Framlux.FleetManagement.Test.Auth;

/// <summary>
/// H1 tests: <see cref="InternalApiKeyValidator"/> performs constant-time hash compare and
/// supports kid-based key rotation. Locks the contract used by every internal gRPC service.
/// </summary>
public sealed class InternalApiKeyValidatorTests
{
    private static ServerCallContext CreateContext(string? key, string? kid = null)
    {
        Metadata headers = new();
        if (key is not null)
        {
            headers.Add(InternalApiKeyValidator.KeyHeader, key);
        }
        if (kid is not null)
        {
            headers.Add(InternalApiKeyValidator.KidHeader, kid);
        }

        return TestServerCallContext.Create(
            method: "Test",
            host: "localhost",
            deadline: DateTime.UtcNow.AddMinutes(1),
            requestHeaders: headers,
            cancellationToken: CancellationToken.None,
            peer: "127.0.0.1",
            authContext: null,
            contextPropagationToken: null,
            writeHeadersFunc: _ => Task.CompletedTask,
            writeOptionsGetter: () => new WriteOptions(),
            writeOptionsSetter: _ => { });
    }

    [Test]
    public async Task Validate_NoKeyConfigured_ThrowsUnavailable()
    {
        InternalApiOptions options = new();

        RpcException? ex = await Assert.ThrowsAsync<RpcException>(() =>
        {
            InternalApiKeyValidator.Validate(CreateContext("anything"), options);

            return Task.CompletedTask;
        });

        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.StatusCode).IsEqualTo(StatusCode.Unavailable);
    }

    [Test]
    public async Task Validate_MissingKeyHeader_ThrowsUnauthenticated()
    {
        InternalApiOptions options = new() { Key = "supersecret" };

        RpcException? ex = await Assert.ThrowsAsync<RpcException>(() =>
        {
            InternalApiKeyValidator.Validate(CreateContext(null), options);

            return Task.CompletedTask;
        });

        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.StatusCode).IsEqualTo(StatusCode.Unauthenticated);
    }

    [Test]
    public async Task Validate_WrongKey_ThrowsUnauthenticated()
    {
        InternalApiOptions options = new() { Key = "supersecret" };

        RpcException? ex = await Assert.ThrowsAsync<RpcException>(() =>
        {
            InternalApiKeyValidator.Validate(CreateContext("wrong"), options);

            return Task.CompletedTask;
        });

        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.StatusCode).IsEqualTo(StatusCode.Unauthenticated);
    }

    [Test]
    public async Task Validate_CorrectKeyNoKid_Succeeds()
    {
        InternalApiOptions options = new() { Key = "supersecret" };

        // No exception is the assertion — the matching key must validate cleanly.
        Exception? caught = null;
        try
        {
            InternalApiKeyValidator.Validate(CreateContext("supersecret"), options);
        }
        catch (Exception ex)
        {
            caught = ex;
        }
        await Assert.That(caught).IsNull();
    }

    [Test]
    public async Task Validate_KidLookup_Succeeds()
    {
        InternalApiOptions options = new()
        {
            Keys = { ["2026-05-20"] = "rotated-secret" },
        };

        Exception? caught = null;
        try
        {
            InternalApiKeyValidator.Validate(
                CreateContext("rotated-secret", kid: "2026-05-20"),
                options);
        }
        catch (Exception ex)
        {
            caught = ex;
        }
        await Assert.That(caught).IsNull();
    }

    [Test]
    public async Task Validate_KidLookup_TakesPrecedenceOverLegacyKey()
    {
        InternalApiOptions options = new()
        {
            Key = "legacy-secret",
            Keys = { ["2026-05-20"] = "rotated-secret" },
        };

        // Both keys are valid simultaneously — hitless rotation.
        Exception? caught = null;
        try
        {
            InternalApiKeyValidator.Validate(CreateContext("legacy-secret"), options);
            InternalApiKeyValidator.Validate(CreateContext("rotated-secret", kid: "2026-05-20"), options);
        }
        catch (Exception ex)
        {
            caught = ex;
        }
        await Assert.That(caught).IsNull();
    }

    [Test]
    public async Task Validate_UnknownKid_ThrowsUnavailable()
    {
        InternalApiOptions options = new() { Key = "legacy-secret" };

        RpcException? ex = await Assert.ThrowsAsync<RpcException>(() =>
        {
            InternalApiKeyValidator.Validate(CreateContext("anything", kid: "no-such-kid"), options);

            return Task.CompletedTask;
        });

        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.StatusCode).IsEqualTo(StatusCode.Unavailable);
    }

    [Test]
    public async Task FixedTimeHashCompare_SameInputs_ReturnsTrue()
    {
        bool result = InternalApiKeyValidator.FixedTimeHashCompare("hello", "hello");

        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task FixedTimeHashCompare_DifferentInputs_ReturnsFalse()
    {
        bool result = InternalApiKeyValidator.FixedTimeHashCompare("hello", "world");

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task FixedTimeHashCompare_DifferentLengths_ReturnsFalseWithoutThrow()
    {
        // The function SHA-256-hashes both sides before compare, so length mismatches do not
        // cause an exception or length-leak — both digests are 32 bytes.
        bool result = InternalApiKeyValidator.FixedTimeHashCompare("a", "ab");

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task FixedTimeHashCompare_EmptyInputs_ReturnsTrue()
    {
        bool result = InternalApiKeyValidator.FixedTimeHashCompare(string.Empty, string.Empty);

        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task FixedTimeHashCompare_NullInput_ThrowsArgumentNullException()
    {
        ArgumentNullException? ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            InternalApiKeyValidator.FixedTimeHashCompare(null!, "x");

            return Task.CompletedTask;
        });

        await Assert.That(ex).IsNotNull();
    }

    [Test]
    public async Task ResolveConfiguredSecret_DefaultKidReadsLegacyKey()
    {
        InternalApiOptions options = new() { Key = "legacy" };

        string? resolved = InternalApiKeyValidator.ResolveConfiguredSecret(options, "default");

        await Assert.That(resolved).IsEqualTo("legacy");
    }

    [Test]
    public async Task ResolveConfiguredSecret_KidNotInMapAndNotDefault_ReturnsNull()
    {
        InternalApiOptions options = new() { Key = "legacy" };

        string? resolved = InternalApiKeyValidator.ResolveConfiguredSecret(options, "rotated");

        await Assert.That(resolved).IsNull();
    }

    [Test]
    public async Task ResolveConfiguredSecret_KidMapWinsOverLegacyKey()
    {
        InternalApiOptions options = new()
        {
            Key = "legacy",
            Keys = { ["default"] = "from-map" },
        };

        string? resolved = InternalApiKeyValidator.ResolveConfiguredSecret(options, "default");

        await Assert.That(resolved).IsEqualTo("from-map");
    }
}
