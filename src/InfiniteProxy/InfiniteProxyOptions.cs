namespace InfiniteProxy;

/// <summary>
/// Configuration for <see cref="InfiniteProxyClient"/>.
/// </summary>
public sealed class InfiniteProxyOptions
{
    /// <summary>
    /// Proxy types to fetch and validate. Defaults to HTTP, SOCKS4, and SOCKS5.
    /// </summary>
    public IReadOnlyList<ProxyType> ProxyTypes { get; init; } =
    [
        ProxyType.Http,
        ProxyType.Socks4,
        ProxyType.Socks5
    ];

    /// <summary>
    /// How often to poll sources for updated proxy lists.
    /// </summary>
    public TimeSpan FetchInterval { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How often to re-validate proxies already in the pool.
    /// </summary>
    public TimeSpan RecheckInterval { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Maximum number of concurrent proxy validation tasks.
    /// </summary>
    public int MaxConcurrentChecks { get; init; } = 50;

    /// <summary>
    /// Timeout for each individual proxy check (overall budget).
    /// </summary>
    public TimeSpan CheckTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Timeout specifically for connecting to the proxy host (and DNS resolution).
    /// This primarily affects SOCKS validation. HTTP proxy checks use HttpClient's
    /// own connection handling (governed by the overall CheckTimeout).
    /// </summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(6);

    /// <summary>
    /// URL used to validate HTTP proxies. Must respond with HTTP 200.
    /// </summary>
    public Uri HttpValidationUrl { get; init; } = new("http://httpbin.org/ip");

    /// <summary>
    /// Host used to validate SOCKS proxies. We perform a full CONNECT handshake
    /// and then a small data transfer test to this host.
    /// </summary>
    public string SocksValidationHost { get; init; } = "httpbin.org";

    /// <summary>
    /// Port used for the SOCKS validation target (the data probe after CONNECT).
    /// </summary>
    public int SocksValidationPort { get; init; } = 80;

    /// <summary>
    /// ISO 3166-1 alpha-2 country filter for ProxyScrape, or <c>null</c> for all countries.
    /// </summary>
    public string? Country { get; init; }

    /// <summary>
    /// Maximum proxy timeout reported by ProxyScrape, in milliseconds.
    /// </summary>
    public int SourceTimeoutMs { get; init; } = 10_000;

    /// <summary>
    /// Maximum proxies to fetch per source request.
    /// </summary>
    public int SourceLimit { get; init; } = 2000;
}
