// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Kubernetes;
using System.Data.Common;

namespace Framlux.FleetManagement.Test.Services.Kubernetes;

/// <summary>
/// Tests for <see cref="KubernetesProjectionErrors.IsTransient"/>, which decides whether a failed
/// apply is worth retrying or is skipped for good.
/// </summary>
public class KubernetesProjectionErrorsTests
{
    private sealed class FakeDbException : DbException
    {
        private readonly bool _transient;
        private readonly string? _sqlState;

        public FakeDbException(bool transient, string? sqlState)
            : base("database error")
        {
            _transient = transient;
            _sqlState = sqlState;
        }

        public override bool IsTransient => _transient;

        public override string? SqlState => _sqlState;
    }

    [Test]
    public async Task IsTransient_DbExceptionFlaggedTransient_IsTransient()
    {
        await Assert.That(KubernetesProjectionErrors.IsTransient(new FakeDbException(transient: true, sqlState: null))).IsTrue();
    }

    [Test]
    public async Task IsTransient_TimeoutException_IsTransient()
    {
        await Assert.That(KubernetesProjectionErrors.IsTransient(new TimeoutException("pool exhausted"))).IsTrue();
    }

    [Test]
    [Arguments("40001")]
    [Arguments("40P01")]
    public async Task IsTransient_SerializationFailureOrDeadlock_IsTransient(string sqlState)
    {
        await Assert.That(KubernetesProjectionErrors.IsTransient(new FakeDbException(transient: false, sqlState: sqlState))).IsTrue();
    }

    [Test]
    [Arguments("23505")]
    [Arguments("22001")]
    [Arguments("42P01")]
    [Arguments(null)]
    public async Task IsTransient_OtherDatabaseErrors_AreNotTransient(string? sqlState)
    {
        await Assert.That(KubernetesProjectionErrors.IsTransient(new FakeDbException(transient: false, sqlState: sqlState))).IsFalse();
    }

    [Test]
    public async Task IsTransient_UnrelatedException_IsNotTransient()
    {
        await Assert.That(KubernetesProjectionErrors.IsTransient(new InvalidOperationException("bad state"))).IsFalse();
    }

    [Test]
    public async Task IsTransient_TransientCauseBehindAWrapper_IsTransient()
    {
        InvalidOperationException wrapped = new("command failed", new TimeoutException("connection timed out"));

        await Assert.That(KubernetesProjectionErrors.IsTransient(wrapped)).IsTrue();
    }

    [Test]
    public async Task IsTransient_Null_Throws()
    {
        await Assert.That(() => KubernetesProjectionErrors.IsTransient(null!)).Throws<ArgumentNullException>();
    }
}
