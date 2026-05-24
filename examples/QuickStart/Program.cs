using InfiniteProxy;

var client = new InfiniteProxyClient(new InfiniteProxyOptions
{
    ProxyTypes = [ProxyType.Http, ProxyType.Socks5],
    FetchInterval = TimeSpan.FromMinutes(5),
    CheckTimeout = TimeSpan.FromSeconds(8),
    MaxConcurrentChecks = 30
});

client.ProxyAdded += (_, e) => Console.WriteLine($"[+] {e.Proxy}");
client.ProxyRemoved += (_, e) => Console.WriteLine($"[-] {e.Proxy}");

Console.WriteLine("Starting InfiniteProxy background scanner...");
Console.WriteLine("Waiting for the first working proxy...\n");

var first = await client.StartAsync();
Console.WriteLine($"\nFirst proxy ready: {first}");
Console.WriteLine($"Pool size: {client.WorkingCount}");

var fastest = client.GetFastest();
if (fastest is not null)
{
    Console.WriteLine($"Fastest: {fastest}");
}

Console.WriteLine("\nScanner keeps running in the background. Press Ctrl+C to exit.");
await Task.Delay(Timeout.Infinite);
