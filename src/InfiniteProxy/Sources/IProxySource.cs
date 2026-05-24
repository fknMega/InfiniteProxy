namespace InfiniteProxy.Sources;

/// <summary>
/// Metadata returned by a proxy source to detect list updates.
/// </summary>
public sealed class ProxySourceInfo
{
    public required string SourceName { get; init; }
    public required ProxyType Type { get; init; }
    public int Count { get; init; }
    public DateTimeOffset? LastUpdated { get; init; }
    public string? Fingerprint { get; init; }
}

/// <summary>
/// A raw proxy candidate before validation.
/// </summary>
public sealed class ProxyCandidate
{
    public required string Host { get; init; }
    public required int Port { get; init; }
    public required ProxyType Type { get; init; }
    public required string Source { get; init; }
}

/// <summary>
/// Abstraction for a remote proxy list provider.
/// </summary>
public interface IProxySource
{
    string Name { get; }

    Task<IReadOnlyList<ProxyCandidate>> FetchAsync(
        ProxyType type,
        CancellationToken cancellationToken = default);

    Task<ProxySourceInfo?> GetInfoAsync(
        ProxyType type,
        CancellationToken cancellationToken = default);
}
