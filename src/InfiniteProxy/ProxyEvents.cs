namespace InfiniteProxy;

/// <summary>
/// Event data when a working proxy is added to the pool.
/// </summary>
public sealed class ProxyAddedEventArgs : EventArgs
{
    public required ProxyEndpoint Proxy { get; init; }
}

/// <summary>
/// Event data when a proxy is removed from the pool after failing validation.
/// </summary>
public sealed class ProxyRemovedEventArgs : EventArgs
{
    public required ProxyEndpoint Proxy { get; init; }
}
