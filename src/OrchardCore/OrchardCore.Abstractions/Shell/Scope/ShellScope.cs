using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OrchardCore.Environment.Cache;
using OrchardCore.Environment.Shell.Builders;
using OrchardCore.Environment.Shell.Models;
using OrchardCore.Modules;

namespace OrchardCore.Environment.Shell.Scope
{
    /// <summary>
    /// Custom 'IServiceScope' managing the shell state and the execution flow.
    /// </summary>
    public class ShellScope : IServiceScope
    {
        private static readonly AsyncLocal<ShellScope> _current = new AsyncLocal<ShellScope>();

        private static readonly Dictionary<string, SemaphoreSlim> _semaphores = new Dictionary<string, SemaphoreSlim>();

        private readonly IServiceScope _serviceScope;

        private readonly Dictionary<object, object> _items = new Dictionary<object, object>();
        private readonly List<Func<ShellScope, Task>> _beforeDispose = new List<Func<ShellScope, Task>>();
        private readonly HashSet<string> _deferredSignals = new HashSet<string>();
        private readonly List<Func<ShellScope, Task>> _deferredTasks = new List<Func<ShellScope, Task>>();

        private bool _serviceScopeOnly;
        private bool _disposeShellContext;
        private bool _disposed;

        public ShellScope(ShellContext shellContext)
        {
            // Prevent the context from being disposed until the end of the scope
            Interlocked.Increment(ref shellContext._refCount);

            ShellContext = shellContext;

            // The service provider is null if we try to create
            // a scope on a disabled shell or already disposed.
            if (shellContext.ServiceProvider == null)
            {
                throw new ArgumentNullException(nameof(shellContext.ServiceProvider),
                    $"Can't resolve a scope on tenant: {shellContext.Settings.Name}");
            }

            _serviceScope = shellContext.ServiceProvider.CreateScope();
            ServiceProvider = _serviceScope.ServiceProvider;
        }

        public ShellContext ShellContext { get; }
        public IServiceProvider ServiceProvider { get; }

        /// <summary>
        /// Retrieve the 'ShellContext' of the current shell scope.
        /// </summary>
        public static ShellContext Context => Current?.ShellContext;

        /// <summary>
        /// Retrieve the 'IServiceProvider' of the current shell scope.
        /// </summary>
        public static IServiceProvider Services => Current?.ServiceProvider;

        /// <summary>
        /// Retrieve the current shell scope from the async flow.
        /// </summary>
        public static ShellScope Current => _current.Value;

        /// <summary>
        /// Sets a shared item to the current shell scope.
        /// </summary>
        public static void Set(object key, object value)
        {
            if (Current != null)
            {
                Current._items[key] = value;
            }
        }

        /// <summary>
        /// Gets a shared item from the current shell scope.
        /// </summary>
        public static object Get(object key) => Current == null ? null : Current._items.TryGetValue(key, out var value) ? value : null;

        /// <summary>
        /// Gets a shared item of a given type from the current shell scope.
        /// </summary>
        public static T Get<T>(object key) => Current == null ? default : Current._items.TryGetValue(key, out var value) ? value is T item ? item : default : default;

        /// <summary>
        /// Gets (or creates) a shared item of a given type from the current shell scope.
        /// </summary>
        public static T GetOrCreate<T>(object key, Func<T> factory)
        {
            if (Current == null)
            {
                return factory();
            }

            if (!Current._items.TryGetValue(key, out var value) || !(value is T item))
            {
                Current._items[key] = item = factory();
            }

            return item;
        }

        /// <summary>
        /// Gets (or creates) a shared item of a given type from the current shell scope.
        /// </summary>
        public static T GetOrCreate<T>(object key) where T : class, new()
        {
            if (Current == null)
            {
                return new T();
            }

            if (!Current._items.TryGetValue(key, out var value) || !(value is T item))
            {
                Current._items[key] = item = new T();
            }

            return item;
        }

        /// <summary>
        /// Sets a shared feature to the current shell scope.
        /// </summary>
        public static void SetFeature<T>(T value) => Set(typeof(T), value);

        /// <summary>
        /// Gets a shared feature from the current shell scope.
        /// </summary>
        public static T GetFeature<T>() => Get<T>(typeof(T));

        /// <summary>
        /// Gets (or creates) a shared feature from the current shell scope.
        /// </summary>
        public static T GetOrCreateFeature<T>(Func<T> factory) => GetOrCreate(typeof(T), factory);

        /// <summary>
        /// Gets (or creates) a shared feature from the current shell scope.
        /// </summary>
        public static T GetOrCreateFeature<T>() where T : class, new() => GetOrCreate<T>(typeof(T));

        /// <summary>
        /// Creates a child scope from the current one.
        /// </summary>
        public static Task<ShellScope> CreateChildScopeAsync()
        {
            var shellHost = ShellScope.Services.GetRequiredService<IShellHost>();
            return shellHost.GetScopeAsync(ShellScope.Context.Settings);
        }

        /// <summary>
        /// Creates a child scope from the current one.
        /// </summary>
        public static Task<ShellScope> CreateChildScopeAsync(ShellSettings settings)
        {
            var shellHost = ShellScope.Services.GetRequiredService<IShellHost>();
            return shellHost.GetScopeAsync(settings);
        }

        /// <summary>
        /// Creates a child scope from the current one.
        /// </summary>
        public static Task<ShellScope> CreateChildScopeAsync(string tenant)
        {
            var shellHost = ShellScope.Services.GetRequiredService<IShellHost>();
            return shellHost.GetScopeAsync(tenant);
        }

        /// <summary>
        /// Execute a delegate using a child scope created from the current one.
        /// </summary>
        public static async Task UsingChildScopeAsync(Func<ShellScope, Task> execute)
        {
            await (await CreateChildScopeAsync()).UsingAsync(execute);
        }

        /// <summary>
        /// Execute a delegate using a child scope created from the current one.
        /// </summary>
        public static async Task UsingChildScopeAsync(ShellSettings settings, Func<ShellScope, Task> execute)
        {
            await (await CreateChildScopeAsync(settings)).UsingAsync(execute);
        }

        /// <summary>
        /// Execute a delegate using a child scope created from the current one.
        /// </summary>
        public static async Task UsingChildScopeAsync(string tenant, Func<ShellScope, Task> execute)
        {
            await (await CreateChildScopeAsync(tenant)).UsingAsync(execute);
        }

        /// <summary>
        /// Start holding this shell scope along the async flow.
        /// </summary>
        internal void StartAsyncFlow() => _current.Value = this;

        /// <summary>
        /// Executes a delegate using this shell scope in an isolated async flow,
        /// but only as a service scope without managing the shell state and
        /// without invoking any tenant event.
        /// </summary>
        public Task UsingServiceScopeAsync(Func<ShellScope, Task> execute)
        {
            _serviceScopeOnly = true;
            return UsingAsync(execute);
        }

        /// <summary>
        /// Executes a delegate using this shell scope in an isolated async flow,
        /// while managing the shell state and invoking tenant events.
        /// </summary>
        public async Task UsingAsync(Func<ShellScope, Task> execute)
        {
            if (Current == this)
            {
                await execute(Current);
                return;
            }

            using (this)
            {
                StartAsyncFlow();

                await ActivateShellInternalAsync();

                await execute(this);

                await TerminateShellInternalAsync();

                await BeforeDisposeAsync();
            }
        }

        /// <summary>
        /// Terminates a shell through this shell scope if it remains the last one
        /// </summary>
        public async Task TerminateShellAsync()
        {
            using (this)
            {
                StartAsyncFlow();
                await TerminateShellInternalAsync();
                await BeforeDisposeAsync();
            }
        }

        /// <summary>
        /// Activate the shell, if not yet done, by calling the related tenant event handlers.
        /// </summary>
        internal async Task ActivateShellInternalAsync()
        {
            if (ShellContext.IsActivated)
            {
                return;
            }

            if (_serviceScopeOnly)
            {
                return;
            }

            SemaphoreSlim semaphore;

            lock (_semaphores)
            {
                if (!_semaphores.TryGetValue(ShellContext.Settings.Name, out semaphore))
                {
                    _semaphores[ShellContext.Settings.Name] = semaphore = new SemaphoreSlim(1);
                }
            }

            await semaphore.WaitAsync();

            try
            {
                // The tenant gets activated here.
                if (!ShellContext.IsActivated)
                {
                    using (var scope = ShellContext.CreateScope())
                    {
                        if (scope == null)
                        {
                            return;
                        }

                        scope.StartAsyncFlow();

                        var tenantEvents = scope.ServiceProvider.GetServices<IModularTenantEvents>();

                        foreach (var tenantEvent in tenantEvents)
                        {
                            await tenantEvent.ActivatingAsync();
                        }

                        foreach (var tenantEvent in tenantEvents.Reverse())
                        {
                            await tenantEvent.ActivatedAsync();
                        }

                        await scope.BeforeDisposeAsync();
                    }

                    ShellContext.IsActivated = true;
                }
            }
            finally
            {
                semaphore.Release();
            }
        }

        /// <summary>
        /// Registers a delegate to be invoked when 'BeforeDisposeAsync()' is called on this scope.
        /// </summary>
        private void BeforeDispose(Func<ShellScope, Task> callback) => _beforeDispose.Insert(0, callback);

        /// <summary>
        /// Adds a Signal (if not already present) to be sent just after 'BeforeDisposeAsync()'.
        /// </summary>
        private void DeferredSignal(string key) => _deferredSignals.Add(key);

        /// <summary>
        /// Adds a Task to be executed in a new scope after 'BeforeDisposeAsync()'.
        /// </summary>
        private void DeferredTask(Func<ShellScope, Task> task) => _deferredTasks.Add(task);

        /// <summary>
        /// Registers a delegate to be invoked before the current shell scope will be disposed.
        /// </summary>
        public static void RegisterBeforeDispose(Func<ShellScope, Task> callback) => Current?.BeforeDispose(callback);

        /// <summary>
        /// Adds a Signal (if not already present) to be sent just before the current shell scope will be disposed.
        /// </summary>
        public static void AddDeferredSignal(string key) => Current?.DeferredSignal(key);

        /// <summary>
        /// Adds a Task to be executed in a new scope once the current shell scope has been disposed.
        /// </summary>
        public static void AddDeferredTask(Func<ShellScope, Task> task) => Current?.DeferredTask(task);

        internal async Task BeforeDisposeAsync()
        {
            foreach (var callback in _beforeDispose)
            {
                await callback(this);
            }

            if (_serviceScopeOnly)
            {
                return;
            }

            if (_deferredSignals.Any())
            {
                var signal = ShellContext.ServiceProvider.GetRequiredService<ISignal>();

                foreach (var key in _deferredSignals)
                {
                    await signal.SignalTokenAsync(key);
                }
            }

            if (_deferredTasks.Any())
            {
                var shellHost = ShellContext.ServiceProvider.GetRequiredService<IShellHost>();

                foreach (var task in _deferredTasks)
                {
                    ShellScope scope;

                    // Create a new scope (maybe based on a new shell) for each task.
                    try
                    {
                        // May fail if a shell was released before being disabled.
                        scope = await shellHost.GetScopeAsync(ShellContext.Settings);
                    }
                    catch
                    {
                        // Fallback to a scope based on the current shell that is not yet disposed.
                        scope = new ShellScope(ShellContext);
                    }

                    using (scope)
                    {
                        scope.StartAsyncFlow();

                        var logger = scope.ServiceProvider.GetService<ILogger<ShellScope>>();

                        try
                        {
                            await task(scope);
                        }
                        catch (Exception e)
                        {
                            logger?.LogError(e,
                                "Error while processing deferred task '{TaskName}' on tenant '{TenantName}'.",
                                task.GetType().FullName, ShellContext.Settings.Name);
                        }

                        await scope.BeforeDisposeAsync();
                    }
                }
            }
        }

        /// <summary>
        /// Terminates the shell, if released and in its last scope, by calling the related event handlers,
        /// and specifies if the shell context should be disposed consequently to this scope being disposed.
        /// </summary>
        internal async Task TerminateShellInternalAsync()
        {
            if (_serviceScopeOnly)
            {
                return;
            }

            // A disabled shell still in use is released by its last scope.
            if (ShellContext.Settings.State == TenantState.Disabled)
            {
                if (Interlocked.CompareExchange(ref ShellContext._refCount, 1, 1) == 1)
                {
                    ShellContext.Release();
                }
            }

            // If the context is still being released, it will be disposed if the ref counter is equal to 0.
            // To prevent this while executing the terminating events, the ref counter is not decremented here.
            if (ShellContext._released && Interlocked.CompareExchange(ref ShellContext._refCount, 1, 1) == 1)
            {
                var tenantEvents = _serviceScope.ServiceProvider.GetServices<IModularTenantEvents>();

                foreach (var tenantEvent in tenantEvents)
                {
                    await tenantEvent.TerminatingAsync();
                }

                foreach (var tenantEvent in tenantEvents.Reverse())
                {
                    await tenantEvent.TerminatedAsync();
                }

                _disposeShellContext = true;
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _serviceScope.Dispose();

            if (_disposeShellContext)
            {
                ShellContext.Dispose();
            }

            // Decrement the counter at the very end of the scope
            Interlocked.Decrement(ref ShellContext._refCount);

            _disposed = true;
        }
    }
}
