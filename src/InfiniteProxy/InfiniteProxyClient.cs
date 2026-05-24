using System.Collections.Concurrent;
using InfiniteProxy.Checking;
using InfiniteProxy.Internal;
using InfiniteProxy.Sources;

namespace InfiniteProxy;

/// <summary>
/// Continuously discovers, validates, and maintains a pool of working free proxies.
/// <para>
/// Proxies are validated with real protocol handshakes (plus a data transfer test for SOCKS).
/// The background scanner periodically re-checks existing proxies and removes dead ones.
/// </para>
/// </summary>
public sealed class InfiniteProxyClient : IAsyncDisposable
{
    private readonly InfiniteProxyOptions _options;
    private readonly IReadOnlyList<IProxySource> _sources;
    private readonly ProxyChecker _checker;
    private readonly ProxyPool _pool = new();
    private readonly SourceFingerprintCache _fingerprints = new();
    private readonly ConcurrentDictionary<string, byte> _inFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _failedUntil = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _checkLimiter;

    private CancellationTokenSource? _cts;
    private Task? _backgroundTask;
    private int _started;
    private int _disposed;

    /// <summary>
    /// Creates a client with ProxyScrape as the default source.
    /// </summary>
    public InfiniteProxyClient(InfiniteProxyOptions? options = null)
        : this(options ?? new InfiniteProxyOptions(), CreateDefaultSources(options ?? new InfiniteProxyOptions()))
    {
    }

    private static IProxySource[] CreateDefaultSources(InfiniteProxyOptions options) =>
        [new ProxyScrapeSource(options)];

    /// <summary>
    /// Creates a client with custom sources (useful for testing or adding providers).
    /// </summary>
    public InfiniteProxyClient(InfiniteProxyOptions options, IEnumerable<IProxySource> sources)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(sources);

        _options = options;
        _sources = sources.ToArray();
        if (_sources.Count == 0)
        {
            throw new ArgumentException("At least one proxy source is required.", nameof(sources));
        }

        _checker = new ProxyChecker(options);
        _checkLimiter = new SemaphoreSlim(Math.Max(1, options.MaxConcurrentChecks));
    }

    /// <summary>
    /// Whether the background scanner is running.
    /// </summary>
    public bool IsRunning => _backgroundTask is { IsCompleted: false };

    /// <summary>
    /// Number of working proxies currently in the pool.
    /// </summary>
    public int WorkingCount => _pool.Count;

    /// <summary>
    /// Whether at least one working proxy is currently available.
    /// </summary>
    public bool HasProxies => _pool.Count > 0;

    /// <summary>
    /// Raised when a working proxy is added to the pool.
    /// </summary>
    public event EventHandler<ProxyAddedEventArgs>? ProxyAdded
    {
        add => _pool.ProxyAdded += value;
        remove => _pool.ProxyAdded -= value;
    }

    /// <summary>
    /// Raised when a proxy is removed after failing re-validation.
    /// </summary>
    public event EventHandler<ProxyRemovedEventArgs>? ProxyRemoved
    {
        add => _pool.ProxyRemoved += value;
        remove => _pool.ProxyRemoved -= value;
    }

    /// <summary>
    /// Starts the 24/7 background scanner and completes once at least one working proxy is available.
    /// </summary>
    /// <param name="cancellationToken">Cancel waiting for the first proxy. The scanner keeps running unless <see cref="StopAsync"/> is called.</param>
    /// <returns>The first validated proxy endpoint.</returns>
    public async Task<ProxyEndpoint> StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed == 1, this);

        if (Interlocked.CompareExchange(ref _started, 1, 0) == 0)
        {
            _cts = new CancellationTokenSource();
            _backgroundTask = Task.Run(() => RunBackgroundLoopAsync(_cts.Token), CancellationToken.None);
        }

        if (_pool.Count > 0)
        {
            return _pool.GetRandom()!;
        }

        var waitTcs = new TaskCompletionSource<ProxyEndpoint>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<ProxyAddedEventArgs>? handler = null;
        handler = (_, args) => waitTcs.TrySetResult(args.Proxy);
        _pool.ProxyAdded += handler;

        try
        {
            await using var registration = cancellationToken.Register(static state =>
            {
                var tcs = (TaskCompletionSource<ProxyEndpoint>)state!;
                tcs.TrySetCanceled();
            }, waitTcs).ConfigureAwait(false);

            return await waitTcs.Task.ConfigureAwait(false);
        }
        finally
        {
            _pool.ProxyAdded -= handler;
        }
    }

    /// <summary>
    /// Returns all working proxies, optionally filtered by type.
    /// </summary>
    public IReadOnlyList<ProxyEndpoint> GetProxies(ProxyType? type = null) => _pool.GetAll(type);

    /// <summary>
    /// Returns a random working proxy, optionally filtered by type.
    /// </summary>
    public ProxyEndpoint? GetRandom(ProxyType? type = null) => _pool.GetRandom(type);

    /// <summary>
    /// Returns the fastest working proxy by measured latency, optionally filtered by type.
    /// </summary>
    public ProxyEndpoint? GetFastest(ProxyType? type = null) => _pool.GetFastest(type);

    /// <summary>
    /// Stops the background scanner.
    /// </summary>
    public async Task StopAsync()
    {
        if (_cts is null)
        {
            return;
        }

        await _cts.CancelAsync().ConfigureAwait(false);

        if (_backgroundTask is not null)
        {
            try
            {
                await _backgroundTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private async Task RunBackgroundLoopAsync(CancellationToken cancellationToken)
    {
        var fetchInterval = _options.FetchInterval <= TimeSpan.Zero
            ? TimeSpan.FromMinutes(5)
            : _options.FetchInterval;

        var recheckInterval = _options.RecheckInterval <= TimeSpan.Zero
            ? TimeSpan.FromMinutes(15)
            : _options.RecheckInterval;

        var nextFetch = DateTimeOffset.MinValue;
        var nextRecheck = DateTimeOffset.UtcNow;

        while (!cancellationToken.IsCancellationRequested)
        {
            var now = DateTimeOffset.UtcNow;

            if (now >= nextFetch)
            {
                await FetchAndQueueCandidatesAsync(cancellationToken).ConfigureAwait(false);
                nextFetch = DateTimeOffset.UtcNow + fetchInterval;
            }

            if (now >= nextRecheck)
            {
                await RecheckExistingAsync(cancellationToken).ConfigureAwait(false);
                nextRecheck = DateTimeOffset.UtcNow + recheckInterval;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task FetchAndQueueCandidatesAsync(CancellationToken cancellationToken)
    {
        var checkTasks = new List<Task>();

        foreach (var type in _options.ProxyTypes.Distinct())
        {
            foreach (var source in _sources)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var fingerprintKey = $"{source.Name}:{type}";
                ProxySourceInfo? info = null;

                try
                {
                    info = await source.GetInfoAsync(type, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // Info endpoint is optional — still attempt fetch.
                }

                var shouldFetch = info is null || _fingerprints.HasChanged(fingerprintKey, info.Fingerprint);
                if (!shouldFetch)
                {
                    continue;
                }

                IReadOnlyList<ProxyCandidate> candidates;
                try
                {
                    candidates = await source.FetchAsync(type, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    continue;
                }

                foreach (var candidate in candidates)
                {
                    checkTasks.Add(QueueCheckAsync(candidate, cancellationToken));
                }
            }
        }

        if (checkTasks.Count > 0)
        {
            await Task.WhenAll(checkTasks).ConfigureAwait(false);
        }
    }

    private async Task RecheckExistingAsync(CancellationToken cancellationToken)
    {
        var existing = _pool.GetAll();
        if (existing.Count == 0)
        {
            return;
        }

        var tasks = existing.Select(proxy =>
            QueueCheckAsync(new ProxyCandidate
            {
                Host = proxy.Host,
                Port = proxy.Port,
                Type = proxy.Type,
                Source = proxy.Source
            }, cancellationToken, removeOnFailure: true, existing: proxy)).ToArray();

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task QueueCheckAsync(
        ProxyCandidate candidate,
        CancellationToken cancellationToken,
        bool removeOnFailure = false,
        ProxyEndpoint? existing = null)
    {
        var key = new ProxyKey(candidate.Host, candidate.Port, candidate.Type).ToString();

        if (_inFlight.ContainsKey(key))
        {
            return;
        }

        if (_failedUntil.TryGetValue(key, out var retryAfter) && retryAfter > DateTimeOffset.UtcNow)
        {
            return;
        }

        if (!removeOnFailure && _pool.GetAll(candidate.Type).Any(p => p.Host == candidate.Host && p.Port == candidate.Port))
        {
            return;
        }

        if (!_inFlight.TryAdd(key, 0))
        {
            return;
        }

        await _checkLimiter.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var validated = await _checker.CheckAsync(candidate, cancellationToken).ConfigureAwait(false);
            if (validated is not null)
            {
                _failedUntil.TryRemove(key, out _);
                _pool.TryAdd(validated);
                return;
            }

            _failedUntil[key] = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(10);

            if (removeOnFailure && existing is not null)
            {
                _pool.TryRemove(existing);
            }
        }
        finally
        {
            _checkLimiter.Release();
            _inFlight.TryRemove(key, out _);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        await StopAsync().ConfigureAwait(false);
        _checkLimiter.Dispose();
        _cts?.Dispose();

        foreach (var source in _sources)
        {
            if (source is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }
}
