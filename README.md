# InfiniteProxy

A .NET library that pulls free proxies from public sources, actively validates them, and keeps a live pool of ones that actually work.

It runs in the background, periodically refreshes the lists, and re-checks proxies that were working before. The goal is simple: give you working proxies when you ask for them instead of handing you dead ones from a scraped list.

## Quick Start

```csharp
using InfiniteProxy;

var client = new InfiniteProxyClient(new InfiniteProxyOptions
{
    ProxyTypes = [ProxyType.Http, ProxyType.Socks5],
    FetchInterval = TimeSpan.FromMinutes(5),
    CheckTimeout = TimeSpan.FromSeconds(10),
    ConnectTimeout = TimeSpan.FromSeconds(6)
});

var first = await client.StartAsync();
Console.WriteLine($"Ready: {first}");
```

After `StartAsync` returns you have at least one working proxy and the scanner continues running.

## Using a Proxy + Handling Failures

This is the pattern most people actually need.

```csharp
using System.Net;

// Grab one
var proxy = client.GetRandom(ProxyType.Http);

if (proxy is null)
{
    // Pool is currently empty. Either wait or fall back to direct.
    await Task.Delay(TimeSpan.FromSeconds(2));
    proxy = client.GetRandom(ProxyType.Http);
}

// Set it up for HttpClient
var handler = new HttpClientHandler
{
    Proxy = new WebProxy(proxy.Host, proxy.Port),
    UseProxy = true
};

using var http = new HttpClient(handler)
{
    Timeout = TimeSpan.FromSeconds(20)
};

try
{
    var response = await http.GetAsync("https://api.example.com/data");
    response.EnsureSuccessStatusCode();

    var body = await response.Content.ReadAsStringAsync();
    Console.WriteLine(body);
}
catch (HttpRequestException ex)
{
    // The proxy failed during actual use (very common with free proxies).
    // Ask the client for a different one and retry, or surface the failure.
    Console.WriteLine($"Proxy {proxy.Address} died: {ex.Message}");

    var replacement = client.GetRandom(ProxyType.Http);
    if (replacement is not null && replacement.Address != proxy.Address)
    {
        // retry logic with replacement, or just let the caller try again on next request
    }
}
```

A few notes on this pattern:
- Free proxies die constantly. Treat them as ephemeral.
- `GetRandom` and `GetFastest` are fast — call them when you need one.
- Listen to `ProxyRemoved` if you want to know when the client itself evicts a bad one after re-checking.

## Getting Different Kinds of Proxies

```csharp
var httpProxy   = client.GetRandom(ProxyType.Http);
var socks5Proxy = client.GetRandom(ProxyType.Socks5);
var fastest     = client.GetFastest();           // across all types
var allHttp     = client.GetProxies(ProxyType.Http);
```

`GetFastest()` uses the latency measured during the last successful validation.

## Configuration

```csharp
new InfiniteProxyOptions
{
    ProxyTypes = [ProxyType.Http, ProxyType.Socks4, ProxyType.Socks5],
    FetchInterval = TimeSpan.FromMinutes(5),     // how often we ask for fresh lists
    RecheckInterval = TimeSpan.FromMinutes(15),  // how often we re-test proxies we already have
    MaxConcurrentChecks = 50,
    CheckTimeout = TimeSpan.FromSeconds(10),     // hard cap for an entire validation
    ConnectTimeout = TimeSpan.FromSeconds(6),    // time allowed just to reach the proxy
    HttpValidationUrl = new Uri("http://httpbin.org/ip"),
    SocksValidationHost = "httpbin.org",
    SocksValidationPort = 80,
    Country = null,      // "us", "de", etc. or null for worldwide
    SourceLimit = 2000
}
```

### How Validation Works Now

- HTTP proxies: we make a real HTTP request through them.
- SOCKS4/SOCKS5: we do the proper CONNECT handshake **and then send actual data** over the tunnel. This filters out a lot of proxies that only pretend to work during the handshake.
- Proxies that were previously good get re-tested in the background. Bad ones are removed automatically.

We also use a separate connect timeout so a slow-to-respond proxy doesn't eat the whole check budget.

## Custom Sources

You can add your own providers by implementing `IProxySource`. Pass them when constructing the client:

```csharp
var client = new InfiniteProxyClient(options, new[]
{
    new ProxyScrapeSource(options),
    new MyOtherProxySource()
});
```

See `IProxySource.cs` for the two methods you need (`FetchAsync` and the optional `GetInfoAsync` for change detection).

## When the Pool Is Empty

Nothing explodes. `GetRandom` / `GetFastest` just return null, and `HasProxies` is false.

The background loop keeps running and will surface new proxies through the `ProxyAdded` event when they become available.

## Requirements

- .NET 8.0+
- Outbound network access (the whole point)

## License

MIT. See LICENSE.