// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Framlux.FleetManagement.Test.Infrastructure;

/// <summary>
/// Lightweight <see cref="IServiceScopeFactory"/> implementation that returns a
/// <see cref="DatabaseContext"/> from the test database factory.
/// </summary>
public sealed class TestServiceScopeFactory : IServiceScopeFactory
{
    private readonly DatabaseContext _context;
    private readonly Dictionary<Type, object> _additionalServices;

    /// <summary>
    /// Creates a new instance wrapping the provided test database context.
    /// </summary>
    public TestServiceScopeFactory(DatabaseContext context)
        : this(context, new Dictionary<Type, object>())
    {
    }

    /// <summary>
    /// Creates a new instance wrapping the provided test database context and additional services.
    /// </summary>
    public TestServiceScopeFactory(DatabaseContext context, Dictionary<Type, object> additionalServices)
    {
        _context = context;
        _additionalServices = additionalServices;
    }

    /// <inheritdoc/>
    public IServiceScope CreateScope()
    {
        return new TestServiceScope(_context, _additionalServices);
    }

    private sealed class TestServiceScope : IServiceScope
    {
        public IServiceProvider ServiceProvider { get; }

        public TestServiceScope(DatabaseContext context, Dictionary<Type, object> additionalServices)
        {
            ServiceProvider = new TestServiceProvider(context, additionalServices);
        }

        public void Dispose() { }
    }

    private sealed class TestServiceProvider : IServiceProvider
    {
        private readonly DatabaseContext _context;
        private readonly Dictionary<Type, object> _additionalServices;

        public TestServiceProvider(DatabaseContext context, Dictionary<Type, object> additionalServices)
        {
            _context = context;
            _additionalServices = additionalServices;
        }

        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(DatabaseContext))
            {
                return _context;
            }

            // Auto-resolve repository interfaces backed by the test DatabaseContext.
            if (serviceType == typeof(IMachineStateRepository))
            {
                if (_additionalServices.TryGetValue(serviceType, out object? cached))
                {
                    return cached;
                }

                DatabaseRepository repo = new(_context, NullLogger<DatabaseRepository>.Instance);

                return repo;
            }

            if (_additionalServices.TryGetValue(serviceType, out object? service))
            {
                return service;
            }

            return null;
        }
    }
}
