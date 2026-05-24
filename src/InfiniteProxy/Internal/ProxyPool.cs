using System.Collections.Concurrent;

namespace InfiniteProxy.Internal;

internal sealed class ProxyPool
{
    private readonly ConcurrentDictionary<string, ProxyEndpoint> _proxies = new(StringComparer.OrdinalIgnoreCase);

    public event EventHandler<ProxyAddedEventArgs>? ProxyAdded;
    public event EventHandler<ProxyRemovedEventArgs>? ProxyRemoved;

    public int Count => _proxies.Count;

    public bool TryAdd(ProxyEndpoint proxy)
    {
        var key = CreateKey(proxy);
        if (!_proxies.TryAdd(key, proxy))
        {
            _proxies[key] = proxy;
            return false;
        }

        ProxyAdded?.Invoke(this, new ProxyAddedEventArgs { Proxy = proxy });
        return true;
    }

    public bool TryRemove(ProxyEndpoint proxy)
    {
        if (!_proxies.TryRemove(CreateKey(proxy), out var removed))
        {
            return false;
        }

        ProxyRemoved?.Invoke(this, new ProxyRemovedEventArgs { Proxy = removed });
        return true;
    }

    public IReadOnlyList<ProxyEndpoint> GetAll(ProxyType? type = null)
    {
        var snapshot = _proxies.Values.ToArray();
        if (type is null)
        {
            return snapshot;
        }

        return snapshot.Where(p => p.Type == type).ToArray();
    }

    public ProxyEndpoint? GetRandom(ProxyType? type = null, Random? random = null)
    {
        random ??= Random.Shared;
        var candidates = GetAll(type);
        return candidates.Count == 0 ? null : candidates[random.Next(candidates.Count)];
    }

    public ProxyEndpoint? GetFastest(ProxyType? type = null)
    {
        var candidates = GetAll(type);
        return candidates.Count == 0 ? null : candidates.MinBy(p => p.Latency);
    }

    private static string CreateKey(ProxyEndpoint proxy) => $"{proxy.Type}:{proxy.Host}:{proxy.Port}";
}

internal readonly record struct ProxyKey(string Host, int Port, ProxyType Type)
{
    public static ProxyKey FromCandidate(Sources.ProxyCandidate candidate) =>
        new(candidate.Host, candidate.Port, candidate.Type);

    public override string ToString() => $"{Type}:{Host}:{Port}";
}

internal sealed class SourceFingerprintCache
{
    private readonly ConcurrentDictionary<string, string> _fingerprints = new(StringComparer.OrdinalIgnoreCase);

    public bool HasChanged(string key, string? fingerprint)
    {
        if (string.IsNullOrWhiteSpace(fingerprint))
        {
            return true;
        }

        if (_fingerprints.TryGetValue(key, out var existing))
        {
            if (existing == fingerprint)
            {
                return false;
            }
        }

        _fingerprints[key] = fingerprint;
        return true;
    }
}
