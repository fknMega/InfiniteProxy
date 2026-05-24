namespace InfiniteProxy;

/// <summary>
/// A validated proxy endpoint discovered by the scanner.
/// </summary>
public sealed class ProxyEndpoint
{
    public required string Host { get; init; }
    public required int Port { get; init; }
    public required ProxyType Type { get; init; }
    public required string Source { get; init; }
    public TimeSpan Latency { get; init; }
    public DateTimeOffset LastValidated { get; init; }

    /// <summary>
    /// Returns the proxy in <c>host:port</c> format.
    /// </summary>
    public string Address => $"{Host}:{Port}";

    public override string ToString() => $"{Type}://{Address} ({Latency.TotalMilliseconds:F0}ms, {Source})";
}
