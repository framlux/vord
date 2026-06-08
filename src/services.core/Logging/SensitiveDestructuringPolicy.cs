// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Collections.Concurrent;
using System.Reflection;
using Serilog.Core;
using Serilog.Events;

namespace Framlux.FleetManagement.Services.Core.Logging;

/// <summary>
/// Serilog destructuring policy that replaces properties tagged with
/// <see cref="SensitiveAttribute"/> with the literal value <c>"***"</c>. Non-sensitive
/// properties pass through unchanged.
/// </summary>
/// <remarks>
/// Per-type property reflection is cached, so the per-event cost is minimal once a type has
/// been destructured at least once. Types with no sensitive properties opt out of custom
/// destructuring entirely (returns <c>false</c> from <see cref="TryDestructure"/>), letting
/// Serilog fall through to its default policy.
/// </remarks>
public sealed class SensitiveDestructuringPolicy : IDestructuringPolicy
{
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> SensitiveCache = new();
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> AllPropsCache = new();
    private const string RedactedMarker = "***";

    /// <inheritdoc/>
    public bool TryDestructure(object value, ILogEventPropertyValueFactory propertyValueFactory, out LogEventPropertyValue result)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(propertyValueFactory);

        Type type = value.GetType();
        PropertyInfo[] sensitive = SensitiveCache.GetOrAdd(type, GetSensitiveProperties);
        if (sensitive.Length == 0)
        {
            result = default!;

            return false;
        }

        PropertyInfo[] allProps = AllPropsCache.GetOrAdd(type, GetReadableProperties);
        List<LogEventProperty> structured = new(allProps.Length);
        foreach (PropertyInfo prop in allProps)
        {
            bool isSensitive = Array.IndexOf(sensitive, prop) >= 0;
            LogEventPropertyValue propValue;
            if (isSensitive)
            {
                propValue = new ScalarValue(RedactedMarker);
            }
            else
            {
                object? raw;
                try
                {
                    raw = prop.GetValue(value);
                }
                catch
                {
                    raw = null;
                }

                propValue = propertyValueFactory.CreatePropertyValue(raw, destructureObjects: true);
            }

            structured.Add(new LogEventProperty(prop.Name, propValue));
        }

        result = new StructureValue(structured, type.Name);

        return true;
    }

    private static PropertyInfo[] GetSensitiveProperties(Type type)
    {
        return type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetCustomAttribute<SensitiveAttribute>() is not null)
            .ToArray();
    }

    private static PropertyInfo[] GetReadableProperties(Type type)
    {
        return type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
            .ToArray();
    }
}
