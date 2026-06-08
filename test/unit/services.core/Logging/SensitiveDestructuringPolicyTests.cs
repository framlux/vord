// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Logging;
using Serilog.Core;
using Serilog.Events;

namespace Framlux.FleetManagement.Test.Logging;

/// <summary>
/// M16 tests: <see cref="SensitiveDestructuringPolicy"/> redacts properties tagged with
/// <see cref="SensitiveAttribute"/> and otherwise lets Serilog fall through to default
/// destructuring.
/// </summary>
public sealed class SensitiveDestructuringPolicyTests
{
    private sealed class TaggedShape
    {
        public string PublicField { get; set; } = string.Empty;
        [Sensitive] public string SecretField { get; set; } = string.Empty;
        public int Count { get; set; }
    }

    private sealed class CleanShape
    {
        public string OnlyPublic { get; set; } = string.Empty;
        public int Count { get; set; }
    }

    private sealed class NestedShape
    {
        [Sensitive] public string OuterSecret { get; set; } = string.Empty;
        public TaggedShape Inner { get; set; } = new();
    }

    private sealed class StubFactory : ILogEventPropertyValueFactory
    {
        public LogEventPropertyValue CreatePropertyValue(object? value, bool destructureObjects = false)
        {
            return value is null ? new ScalarValue(null) : new ScalarValue(value);
        }
    }

    [Test]
    public async Task TryDestructure_TaggedPropertyRedacted_OtherPropertiesPassThrough()
    {
        SensitiveDestructuringPolicy policy = new();
        TaggedShape input = new() { PublicField = "ok", SecretField = "DO-NOT-LEAK", Count = 7 };

        bool handled = policy.TryDestructure(input, new StubFactory(), out LogEventPropertyValue result);

        await Assert.That(handled).IsTrue();
        await Assert.That(result).IsTypeOf<StructureValue>();
        StructureValue sv = (StructureValue)result;
        LogEventProperty? secret = sv.Properties.FirstOrDefault(p => p.Name == nameof(TaggedShape.SecretField));
        LogEventProperty? open = sv.Properties.FirstOrDefault(p => p.Name == nameof(TaggedShape.PublicField));
        await Assert.That(secret).IsNotNull();
        await Assert.That(((ScalarValue)secret!.Value).Value).IsEqualTo("***");
        await Assert.That(open).IsNotNull();
        await Assert.That(((ScalarValue)open!.Value).Value).IsEqualTo("ok");
    }

    [Test]
    public async Task TryDestructure_TypeWithoutSensitiveTags_ReturnsFalse()
    {
        SensitiveDestructuringPolicy policy = new();
        CleanShape input = new() { OnlyPublic = "value", Count = 1 };

        bool handled = policy.TryDestructure(input, new StubFactory(), out _);

        await Assert.That(handled).IsFalse();
    }

    [Test]
    public async Task TryDestructure_NestedTaggedProperty_RedactedAtOuterLayer()
    {
        SensitiveDestructuringPolicy policy = new();
        NestedShape input = new()
        {
            OuterSecret = "top-level-secret",
            Inner = new TaggedShape { PublicField = "ok", SecretField = "inner-secret", Count = 3 },
        };

        bool handled = policy.TryDestructure(input, new StubFactory(), out LogEventPropertyValue result);

        await Assert.That(handled).IsTrue();
        StructureValue sv = (StructureValue)result;
        LogEventProperty? outer = sv.Properties.FirstOrDefault(p => p.Name == nameof(NestedShape.OuterSecret));
        await Assert.That(outer).IsNotNull();
        await Assert.That(((ScalarValue)outer!.Value).Value).IsEqualTo("***");
        // The inner shape would also be destructured by Serilog's chain on its own — covered by
        // the standalone TaggedShape test above.
    }

    [Test]
    public async Task TryDestructure_NullValue_Throws()
    {
        SensitiveDestructuringPolicy policy = new();

        ArgumentNullException? ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            policy.TryDestructure(null!, new StubFactory(), out _);

            return Task.CompletedTask;
        });

        await Assert.That(ex).IsNotNull();
    }

    [Test]
    public async Task TryDestructure_NullFactory_Throws()
    {
        SensitiveDestructuringPolicy policy = new();

        ArgumentNullException? ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            policy.TryDestructure(new TaggedShape(), null!, out _);

            return Task.CompletedTask;
        });

        await Assert.That(ex).IsNotNull();
    }
}
