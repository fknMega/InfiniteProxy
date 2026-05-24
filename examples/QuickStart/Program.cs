using InfiniteProxy;

var client = new InfiniteProxyClient(new InfiniteProxyOptions
{
    ProxyTypes = [ProxyType.Http, ProxyType.Socks5],
    FetchInterval = TimeSpan.FromMinutes(5),
    CheckTimeout = TimeSpan.FromSeconds(10),
    ConnectTimeout = TimeSpan.FromSeconds(6),
    MaxConcurrentChecks = 30
});

client.ProxyAdded += (_, e) => Console.WriteLine($"[+] {e.Proxy}");
client.ProxyRemoved += (_, e) => Console.WriteLine($"[-] {e.Proxy}");

Console.WriteLine("Starting InfiniteProxy background scanner...");
Console.WriteLine("Waiting until at least one working proxy is available...\n");

await client.WaitUntilReadyAsync();

var proxy = client.GetRandom(ProxyType.Http)!;
Console.WriteLine($"First proxy ready: {proxy}");
Console.WriteLine($"Pool size: {client.WorkingCount}");

var fastest = client.GetFastest();
if (fastest is not null)
{
    Console.WriteLine($"Fastest: {fastest}");
}

Console.WriteLine("\nScanner keeps running in the background. Press Ctrl+C to exit.");
await Task.Delay(Timeout.Infinite);
