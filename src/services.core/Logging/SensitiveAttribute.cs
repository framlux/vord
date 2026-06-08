// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Logging;

/// <summary>
/// Marks a property as containing data that must NEVER appear in log output. The Serilog
/// <c>SensitiveDestructuringPolicy</c> replaces values of tagged properties with
/// <c>"***"</c> when an object is destructured into a structured log event. Apply to
/// fields that carry PII (email addresses inside payload objects, source IPs from telemetry,
/// session tokens, etc.) so a debug-level log statement cannot inadvertently leak them.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class SensitiveAttribute : Attribute
{
}
