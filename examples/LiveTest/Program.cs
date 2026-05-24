using System.Net;
using InfiniteProxy;

using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));

var client = new InfiniteProxyClient(new InfiniteProxyOptions
{
    ProxyTypes = [ProxyType.Http],
    FetchInterval = TimeSpan.FromMinutes(5),
    CheckTimeout = TimeSpan.FromSeconds(10),
    ConnectTimeout = TimeSpan.FromSeconds(6),
    MaxConcurrentChecks = 40,
    HttpValidationUrl = new Uri("http://httpbin.org/ip")
});

client.ProxyAdded += (_, e) => Console.WriteLine($"found: {e.Proxy}");

Console.WriteLine("Starting scanner (HTTP only, 3 min timeout)...");
Console.WriteLine();

ProxyEndpoint proxy;
try
{
    proxy = await client.StartAsync(cts.Token);
}
catch (OperationCanceledException)
{
    Console.WriteLine("Timed out waiting for a working proxy.");
    return 1;
}

Console.WriteLine($"Using proxy: {proxy}");
Console.WriteLine($"Pool size: {client.WorkingCount}");
Console.WriteLine();

async Task<string> GetIpAsync(bool useProxy)
{
    var handler = new HttpClientHandler();
    if (useProxy)
    {
        handler.Proxy = new WebProxy(proxy.Host, proxy.Port);
        handler.UseProxy = true;
    }

    using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
    return await http.GetStringAsync("http://httpbin.org/ip");
}

try
{
    var directIp = await GetIpAsync(useProxy: false);
    Console.WriteLine($"Direct:  {directIp.Trim()}");

    var proxyIp = await GetIpAsync(useProxy: true);
    Console.WriteLine($"Proxy:   {proxyIp.Trim()}");

    if (directIp == proxyIp)
    {
        Console.WriteLine();
        Console.WriteLine("Warning: responses match. Proxy may be transparent or not routing traffic.");
        return 2;
    }

    Console.WriteLine();
    Console.WriteLine("OK: request through proxy returned a different IP.");
    return 0;
}
catch (Exception ex)
{
    Console.WriteLine($"Request failed: {ex.Message}");
    return 3;
}
finally
{
    await client.DisposeAsync();
}
